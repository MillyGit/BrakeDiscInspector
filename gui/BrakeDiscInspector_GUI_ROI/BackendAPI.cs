using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using OpenCvSharp;
using WPoint = System.Windows.Point;

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
        public static string BaseUrl { get; private set; } = "http://127.0.0.1:8000";

        // === Endpoints
        public const string AnalyzeEndpoint = "/analyze";
        public const string TrainStatusEndpoint = "/train_status";
        public const string MatchEndpoint = "/match_one";   // ajusta si tu backend usa otro path

        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

        static BackendAPI()
        {
            try
            {
                string? envBaseUrl =
                    Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASEURL") ??
                    Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASE_URL") ??
                    Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_URL");

                if (!string.IsNullOrWhiteSpace(envBaseUrl))
                {
                    BaseUrl = NormalizeBaseUrl(envBaseUrl);
                }
                else
                {
                    var host = Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_HOST") ??
                               Environment.GetEnvironmentVariable("HOST");
                    var port = Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_PORT") ??
                               Environment.GetEnvironmentVariable("PORT");

                    if (!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(port))
                    {
                        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
                        port = string.IsNullOrWhiteSpace(port) ? "8000" : port.Trim();
                        BaseUrl = NormalizeBaseUrl($"{host}:{port}");
                    }
                }

                var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Backend", out var be) &&
                        be.TryGetProperty("BaseUrl", out var baseUrlEl))
                    {
                        var fileUrl = baseUrlEl.GetString();
                        if (!string.IsNullOrWhiteSpace(fileUrl))
                        {
                            BaseUrl = NormalizeBaseUrl(fileUrl);
                        }
                    }
                }
            }
            catch
            {
                /* fallback */
            }

            http.BaseAddress = new Uri(BaseUrl.EndsWith("/") ? BaseUrl : BaseUrl + "/");
        }

        private static string NormalizeBaseUrl(string value)
        {
            var trimmed = value.Trim();
            if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed.TrimStart('/');
            }

            return trimmed.TrimEnd('/');
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
            string imagePathWin, RoiModel inspectionRoi, PresetFile preset, Action<string>? log = null)
        {
            try
            {
                if (!File.Exists(imagePathWin)) return (false, null, 0, "image not found", 0);

                if (!TryCropToPng(imagePathWin, inspectionRoi, out var pngMs, out var maskMs, out var name, log))
                    return (false, null, 0, "crop failed", 0);

                using (pngMs)
                using (maskMs)
                {
                    var bytes = pngMs.ToArray();
                    byte[]? maskBytes = maskMs?.ToArray();
                    var resp = await AnalyzeAsync(bytes, maskBytes, null);
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
        public static async Task<(bool ok, WPoint? center, double score, string? error)> MatchOneViaTemplateAsync(
            string imagePathWin,
            string templatePngPath,
            double thr,
            double rotRange,
            double scaleMin,
            double scaleMax,
            string feature,
            double tmThr,
            string tag,
            RoiModel? searchRoi = null,
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
                mp.Add(new StringContent(thr.ToString(CultureInfo.InvariantCulture)), "thr");
                mp.Add(new StringContent(rotRange.ToString(CultureInfo.InvariantCulture)), "rot_range");
                mp.Add(new StringContent(scaleMin.ToString(CultureInfo.InvariantCulture)), "scale_min");
                mp.Add(new StringContent(scaleMax.ToString(CultureInfo.InvariantCulture)), "scale_max");
                mp.Add(new StringContent(string.IsNullOrWhiteSpace(feature) ? "auto" : feature), "feature");
                mp.Add(new StringContent(tmThr.ToString(CultureInfo.InvariantCulture)), "tm_thr");
                mp.Add(new StringContent("0"), "debug");

                AddSearchParameters(mp, searchRoi);

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

                return (true, new WPoint(cx, cy), score, null);
            }
            catch (Exception ex)
            {
                log?.Invoke("[MatchOneViaTemplate] EX: " + ex.Message);
                return (false, null, 0, ex.Message);
            }
        }

        // ========= match one: recortando desde la imagen
        public static async Task<(bool ok, WPoint? center, double score, string? error)> MatchOneViaFilesAsync(
            string imagePathWin,
            RoiModel templateRoi,
            double thr,
            double rotRange,
            double scaleMin,
            double scaleMax,
            string feature,
            double tmThr,
            bool debug,
            string tag,
            RoiModel? searchRoi = null,
            Action<string>? log = null)
        {
            MemoryStream? tplStream = null;
            MemoryStream? maskStream = null;
            try
            {
                string url = BaseUrl.TrimEnd('/') + MatchEndpoint;

                using var hc = new HttpClient();
                using var mp = new MultipartFormDataContent();

                mp.Add(new ByteArrayContent(File.ReadAllBytes(imagePathWin)), "image", Path.GetFileName(imagePathWin));

                if (!TryCropToPng(imagePathWin, templateRoi, out tplStream, out maskStream, out var tplName, log))
                    return (false, null, 0, "crop template failed");

                tplStream.Position = 0;
                mp.Add(new StreamContent(tplStream), "template", string.IsNullOrWhiteSpace(tplName) ? "template.png" : tplName);
                if (maskStream != null)
                {
                    log?.Invoke($"[MatchOneViaFiles] mask bytes={maskStream.Length:n0} (alpha en template)");
                }

                mp.Add(new StringContent(tag), "tag");
                mp.Add(new StringContent(thr.ToString(CultureInfo.InvariantCulture)), "thr");
                mp.Add(new StringContent(rotRange.ToString(CultureInfo.InvariantCulture)), "rot_range");
                mp.Add(new StringContent(scaleMin.ToString(CultureInfo.InvariantCulture)), "scale_min");
                mp.Add(new StringContent(scaleMax.ToString(CultureInfo.InvariantCulture)), "scale_max");
                mp.Add(new StringContent(string.IsNullOrWhiteSpace(feature) ? "auto" : feature), "feature");
                mp.Add(new StringContent(tmThr.ToString(CultureInfo.InvariantCulture)), "tm_thr");
                mp.Add(new StringContent(debug ? "1" : "0"), "debug");

                AddSearchParameters(mp, searchRoi);

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

                return (true, new WPoint(cx, cy), score, null);
            }
            catch (Exception ex)
            {
                log?.Invoke("[MatchOneViaFiles] EX: " + ex.Message);
                return (false, null, 0, ex.Message);
            }
            finally
            {
                try { tplStream?.Dispose(); } catch { /* noop */ }
                try { maskStream?.Dispose(); } catch { /* noop */ }
            }
        }

        private static void AddSearchParameters(MultipartFormDataContent mp, RoiModel? searchRoi)
        {
            if (searchRoi == null)
                return;

            if (!TryGetSearchRect(searchRoi, out var x, out var y, out var w, out var h))
                return;

            mp.Add(new StringContent(x.ToString(CultureInfo.InvariantCulture)), "search_x");
            mp.Add(new StringContent(y.ToString(CultureInfo.InvariantCulture)), "search_y");
            mp.Add(new StringContent(w.ToString(CultureInfo.InvariantCulture)), "search_w");
            mp.Add(new StringContent(h.ToString(CultureInfo.InvariantCulture)), "search_h");
        }

        private static bool TryGetSearchRect(RoiModel roi, out int x, out int y, out int w, out int h)
        {
            x = y = 0;
            w = h = 0;

            if (roi == null)
                return false;

            double sx, sy, sw, sh;

            switch (roi.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    sx = roi.CX - roi.R;
                    sy = roi.CY - roi.R;
                    sw = roi.R * 2.0;
                    sh = roi.R * 2.0;
                    break;
                case RoiShape.Rectangle:
                default:
                    sx = roi.Left;
                    sy = roi.Top;
                    sw = roi.Width;
                    sh = roi.Height;
                    break;
            }

            if (double.IsNaN(sx) || double.IsNaN(sy) || double.IsNaN(sw) || double.IsNaN(sh))
                return false;

            x = Math.Max(0, (int)Math.Round(sx));
            y = Math.Max(0, (int)Math.Round(sy));
            w = Math.Max(1, (int)Math.Round(sw));
            h = Math.Max(1, (int)Math.Round(sh));

            return w > 0 && h > 0;
        }

        // ========= util: recortar PNG desde ruta/rect
        public static bool TryCropToPng(string imagePathWin, RoiModel roi, out MemoryStream pngStream, out MemoryStream? maskStream, out string fileName, Action<string>? log = null)
        {
            pngStream = null;
            maskStream = null;
            fileName = $"crop_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";

            try
            {
                if (roi == null)
                {
                    log?.Invoke("[TryCropToPng] ROI null");
                    return false;
                }

                using var src = Cv2.ImRead(imagePathWin, ImreadModes.Unchanged);
                if (src.Empty())
                {
                    log?.Invoke("[TryCropToPng] failed to load image");
                    return false;
                }

                if (!RoiCropUtils.TryBuildRoiCropInfo(roi, out var info))
                {
                    log?.Invoke("[TryCropToPng] unsupported ROI shape");
                    return false;
                }

                if (!RoiCropUtils.TryGetRotatedCrop(src, info, roi.AngleDeg, out var cropMat, out var cropRect))
                {
                    log?.Invoke("[TryCropToPng] failed to get rotated crop");
                    return false;
                }

                Mat? maskMat = null;
                Mat? encodeMat = null;
                try
                {
                    bool needsMask = roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus;
                    if (needsMask)
                    {
                        maskMat = RoiCropUtils.BuildRoiMask(info, cropRect);
                        encodeMat = RoiCropUtils.ConvertCropToBgra(cropMat, maskMat);
                    }
                    else
                    {
                        encodeMat = cropMat;
                    }

                    if (!Cv2.ImEncode(".png", encodeMat, out var pngBytes) || pngBytes is null || pngBytes.Length == 0)
                    {
                        log?.Invoke("[TryCropToPng] failed to encode PNG");
                        return false;
                    }

                    pngStream = new MemoryStream(pngBytes);

                    if (needsMask && maskMat != null)
                    {
                        if (Cv2.ImEncode(".png", maskMat, out var maskBytes) && maskBytes != null && maskBytes.Length > 0)
                        {
                            maskStream = new MemoryStream(maskBytes);
                        }
                        else
                        {
                            log?.Invoke("[TryCropToPng] failed to encode mask PNG");
                            maskStream = null;
                        }
                    }

                    log?.Invoke($"[TryCropToPng] ROI={roi.Shape} rect=({info.Left:0.##},{info.Top:0.##},{info.Width:0.##},{info.Height:0.##}) pivot=({info.PivotX:0.##},{info.PivotY:0.##}) crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) angle={roi.AngleDeg:0.##}");
                    return true;
                }
                finally
                {
                    if (!ReferenceEquals(encodeMat, cropMat))
                    {
                        encodeMat?.Dispose();
                    }
                    maskMat?.Dispose();
                    cropMat.Dispose();
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("[TryCropToPng] " + ex.Message);
                pngStream?.Dispose();
                pngStream = null;
                if (maskStream != null)
                {
                    maskStream.Dispose();
                    maskStream = null;
                }
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
