using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows; // Point, Rect
using WRect = System.Windows.Rect;

namespace BrakeDiscInspector_GUI_ROI
{
    public class AnalyzeResponse
    {
        public string label { get; set; }
        public double score { get; set; }
        public double threshold { get; set; }
        public string heatmap_png_b64 { get; set; }
    }

    public static class BackendAPI
    {
        public static string BaseUrl { get; private set; } = "http://127.0.0.1:5000";

        // === Endpoints
        public const string AnalyzeEndpoint = "/analyze";
        public const string TrainStatusEndpoint = "/train_status";
        public const string MatchEndpoint = "/match_one";   // ajusta si tu backend usa otro path

        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

        static BackendAPI()
        {
            try
            {
                var json = File.ReadAllText("appsettings.json");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Backend", out var be) &&
                    be.TryGetProperty("BaseUrl", out var baseUrlEl))
                {
                    BaseUrl = baseUrlEl.GetString() ?? BaseUrl;
                }
            }
            catch { /* fallback */ }
            http.BaseAddress = new Uri(BaseUrl);
        }

        // ========= /analyze (bytes ya rotados)
        public static async Task<AnalyzeResponse> AnalyzeAsync(byte[] rotatedCropPng, byte[]? maskPng = null, object? annulus = null)
        {
            using var form = new MultipartFormDataContent();

            var imgContent = new ByteArrayContent(rotatedCropPng);
            imgContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            form.Add(imgContent, "file", "crop.png");

            if (maskPng != null)
            {
                var maskContent = new ByteArrayContent(maskPng);
                maskContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
                form.Add(maskContent, "mask", "mask.png");
            }
            else if (annulus != null)
            {
                var annulusJson = JsonSerializer.Serialize(annulus);
                form.Add(new StringContent(annulusJson), "annulus");
            }

            using var resp = await http.PostAsync(AnalyzeEndpoint, form).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<AnalyzeResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        // ========= /analyze (sobrecarga 4 args que usa tu MainWindow)
        public static async Task<(bool ok, string? label, double score, string? error, double threshold)> AnalyzeAsync(
            string imagePathWin, WRect inspRect, PresetFile preset, Action<string>? log = null)
        {
            try
            {
                if (!File.Exists(imagePathWin)) return (false, null, 0, "image not found", 0);

                if (!TryCropToPng(imagePathWin, inspRect, out var pngMs, out var name, log))
                    return (false, null, 0, "crop failed", 0);

                using (pngMs)
                {
                    var bytes = pngMs.ToArray();
                    var resp = await AnalyzeAsync(bytes, null, null);
                    return (true, resp.label, resp.score, null, resp.threshold);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("[Analyze] EX: " + ex.Message);
                return (false, null, 0, ex.Message, 0);
            }
        }

        // ========= match one: template desde fichero
        public static async Task<(bool ok, Point? center, double score, string? error)> MatchOneViaTemplateAsync(
            string imagePathWin,
            string templatePngPath,
            double thr,
            double rotRange,
            double scaleMin,
            double scaleMax,
            string feature,
            double tmThr,
            string tag,
            Action<string>? log = null)
        {
            try
            {
                string url = BaseUrl.TrimEnd('/') + MatchEndpoint;

                using var hc = new HttpClient();
                using var mp = new MultipartFormDataContent();

                mp.Add(new ByteArrayContent(File.ReadAllBytes(imagePathWin)), "image", Path.GetFileName(imagePathWin));
                mp.Add(new ByteArrayContent(File.ReadAllBytes(templatePngPath)), "template", Path.GetFileName(templatePngPath));

                mp.Add(new StringContent(tag), "tag");
                mp.Add(new StringContent(thr.ToString(System.Globalization.CultureInfo.InvariantCulture)), "thr");
                mp.Add(new StringContent(rotRange.ToString(System.Globalization.CultureInfo.InvariantCulture)), "rot_range");
                mp.Add(new StringContent(scaleMin.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_min");
                mp.Add(new StringContent(scaleMax.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_max");
                mp.Add(new StringContent(string.IsNullOrWhiteSpace(feature) ? "auto" : feature), "feature");
                mp.Add(new StringContent(tmThr.ToString(System.Globalization.CultureInfo.InvariantCulture)), "tm_thr");
                mp.Add(new StringContent("0"), "debug");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await hc.PostAsync(url, mp, cts.Token);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (false, null, 0, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                bool found = root.TryGetProperty("found", out var fEl) && fEl.GetBoolean();
                if (!found)
                {
                    string reason = root.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "not found") : "not found";
                    return (false, null, 0, reason);
                }

                double cx = root.GetProperty("center_x").GetDouble();
                double cy = root.GetProperty("center_y").GetDouble();
                double score = root.TryGetProperty("confidence", out var cEl) ? cEl.GetDouble()
                             : root.TryGetProperty("score", out var sEl) ? sEl.GetDouble() : 0;

                return (true, new Point(cx, cy), score, null);
            }
            catch (Exception ex)
            {
                log?.Invoke("[MatchOneViaTemplate] EX: " + ex.Message);
                return (false, null, 0, ex.Message);
            }
        }

        // ========= match one: recortando desde la imagen
        public static async Task<(bool ok, Point? center, double score, string? error)> MatchOneViaFilesAsync(
            string imagePathWin,
            WRect templateRect,
            double thr,
            double rotRange,
            double scaleMin,
            double scaleMax,
            string feature,
            double tmThr,
            bool debug,
            string tag,
            Action<string>? log = null)
        {
            MemoryStream? tplStream = null;
            try
            {
                string url = BaseUrl.TrimEnd('/') + MatchEndpoint;

                using var hc = new HttpClient();
                using var mp = new MultipartFormDataContent();

                mp.Add(new ByteArrayContent(File.ReadAllBytes(imagePathWin)), "image", Path.GetFileName(imagePathWin));

                if (!TryCropToPng(imagePathWin, templateRect, out tplStream, out var tplName, log))
                    return (false, null, 0, "crop template failed");

                tplStream.Position = 0;
                mp.Add(new StreamContent(tplStream), "template", string.IsNullOrWhiteSpace(tplName) ? "template.png" : tplName);

                mp.Add(new StringContent(tag), "tag");
                mp.Add(new StringContent(thr.ToString(System.Globalization.CultureInfo.InvariantCulture)), "thr");
                mp.Add(new StringContent(rotRange.ToString(System.Globalization.CultureInfo.InvariantCulture)), "rot_range");
                mp.Add(new StringContent(scaleMin.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_min");
                mp.Add(new StringContent(scaleMax.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_max");
                mp.Add(new StringContent(string.IsNullOrWhiteSpace(feature) ? "auto" : feature), "feature");
                mp.Add(new StringContent(tmThr.ToString(System.Globalization.CultureInfo.InvariantCulture)), "tm_thr");
                mp.Add(new StringContent(debug ? "1" : "0"), "debug");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await hc.PostAsync(url, mp, cts.Token);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (false, null, 0, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                bool found = root.TryGetProperty("found", out var fEl) && fEl.GetBoolean();
                if (!found)
                {
                    string reason = root.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "not found") : "not found";
                    return (false, null, 0, reason);
                }

                double cx = root.GetProperty("center_x").GetDouble();
                double cy = root.GetProperty("center_y").GetDouble();
                double score = root.TryGetProperty("confidence", out var cEl) ? cEl.GetDouble()
                             : root.TryGetProperty("score", out var sEl) ? sEl.GetDouble() : 0;

                return (true, new Point(cx, cy), score, null);
            }
            catch (Exception ex)
            {
                log?.Invoke("[MatchOneViaFiles] EX: " + ex.Message);
                return (false, null, 0, ex.Message);
            }
            finally
            {
                try { tplStream?.Dispose(); } catch { /* noop */ }
            }
        }

        // ========= util: recortar PNG desde ruta/rect
        public static bool TryCropToPng(string imagePathWin, WRect rect, out MemoryStream pngStream, out string fileName, Action<string>? log = null)
        {
            pngStream = new MemoryStream();
            fileName = $"crop_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";

            try
            {
                using var bmp = new System.Drawing.Bitmap(imagePathWin);
                var x = Math.Max(0, (int)rect.X);
                var y = Math.Max(0, (int)rect.Y);
                var w = Math.Max(1, (int)rect.Width);
                var h = Math.Max(1, (int)rect.Height);
                if (x + w > bmp.Width) w = Math.Max(1, bmp.Width - x);
                if (y + h > bmp.Height) h = Math.Max(1, bmp.Height - y);
                using var crop = bmp.Clone(new System.Drawing.Rectangle(x, y, w, h), bmp.PixelFormat);
                crop.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
                pngStream.Position = 0;
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("[TryCropToPng] " + ex.Message);
                pngStream.Dispose();
                pngStream = null;
                return false;
            }
        }

        // ========= /train_status
        public static async Task<string> TrainStatusAsync()
        {
            using var resp = await http.GetAsync(TrainStatusEndpoint).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
    }
}
