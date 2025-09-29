using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI
{
    public sealed class InferRegion
    {
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        public double area_px { get; set; }
        public double area_mm2 { get; set; }
    }

    public sealed class InferResponse
    {
        public double score { get; set; }
        public double threshold { get; set; }
        public string? heatmap_png_base64 { get; set; }
        public InferRegion[]? regions { get; set; }
        public int[]? token_shape { get; set; }
    }

    public sealed class InferFromImageResult
    {
        public InferFromImageResult(InferResponse response, byte[] pngBytes, string shapeJson, RoiModel roiImage)
        {
            Response = response;
            PngBytes = pngBytes;
            ShapeJson = shapeJson;
            RoiImage = roiImage;
        }

        public InferResponse Response { get; }
        public byte[] PngBytes { get; }
        public string ShapeJson { get; }
        public RoiModel RoiImage { get; }
    }

    public static class BackendAPI
    {
        public static string BaseUrl { get; private set; } = "http://127.0.0.1:8000";

        // === Endpoints
        public const string InferEndpoint = "/infer";
        public const string FitOkEndpoint = "/fit_ok";
        public const string CalibrateEndpoint = "/calibrate_ng";
        public const string TrainStatusEndpoint = "/train_status";

        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

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

        public static async Task<InferResponse> InferFromBytesAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            byte[] canonicalPng,
            string? shapeJson = null,
            Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(roleId))
                throw new ArgumentException("roleId is required", nameof(roleId));
            if (string.IsNullOrWhiteSpace(roiId))
                throw new ArgumentException("roiId is required", nameof(roiId));
            if (canonicalPng == null || canonicalPng.Length == 0)
                throw new ArgumentException("canonical ROI PNG is empty", nameof(canonicalPng));

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");

            var imgContent = new ByteArrayContent(canonicalPng);
            imgContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            form.Add(imgContent, "image", "roi.png");

            if (!string.IsNullOrWhiteSpace(shapeJson))
            {
                var shapeContent = new StringContent(shapeJson, Encoding.UTF8, "application/json");
                form.Add(shapeContent, "shape");
            }

            using var resp = await http.PostAsync(InferEndpoint, form).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                log?.Invoke($"[/infer] HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase} :: {body}");
                throw new HttpRequestException($"/infer failed with status {(int)resp.StatusCode}: {resp.ReasonPhrase}");
            }

            try
            {
                var result = JsonSerializer.Deserialize<InferResponse>(body, JsonOptions);
                if (result == null)
                {
                    throw new InvalidOperationException("Empty /infer response");
                }

                return result;
            }
            catch (JsonException jex)
            {
                log?.Invoke("[/infer] JSON parse error: " + jex.Message);
                throw;
            }
        }

        public static async Task<(bool ok, InferFromImageResult? result, string? error)> InferAsync(
            string imagePathWin,
            RoiModel roi,
            string roleId,
            string roiId,
            double mmPerPx,
            Action<string>? log = null)
        {
            MemoryStream? pngStream = null;
            MemoryStream? maskStream = null;
            try
            {
                if (!File.Exists(imagePathWin))
                {
                    return (false, null, "image not found");
                }

                if (!TryCropToPng(imagePathWin, roi, out pngStream, out maskStream, out var name, out var info, out var cropRect, log))
                {
                    return (false, null, "crop failed");
                }

                var pngBytes = pngStream.ToArray();
                var shapeJson = BuildShapeJson(roi, info, cropRect);
                var response = await InferFromBytesAsync(roleId, roiId, mmPerPx, pngBytes, shapeJson, log).ConfigureAwait(false);
                var roiImage = roi.Clone();
                return (true, new InferFromImageResult(response, pngBytes, shapeJson, roiImage), null);
            }
            catch (Exception ex)
            {
                log?.Invoke("[/infer] EX: " + ex.Message);
                return (false, null, ex.Message);
            }
            finally
            {
                try { pngStream?.Dispose(); } catch { /* ignore */ }
                try { maskStream?.Dispose(); } catch { /* ignore */ }
            }
        }

        // ========= util: recortar PNG desde ruta/rect
        public static bool TryCropToPng(
            string imagePathWin,
            RoiModel roi,
            out MemoryStream pngStream,
            out MemoryStream? maskStream,
            out string fileName,
            out RoiCropInfo cropInfo,
            out Rect cropRect,
            Action<string>? log = null)
        {
            pngStream = null;
            maskStream = null;
            fileName = $"crop_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";
            cropInfo = default;
            cropRect = default;

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

                cropInfo = info;
                if (!RoiCropUtils.TryGetRotatedCrop(src, info, roi.AngleDeg, out var cropMat, out var cropRectLocal))
                {
                    log?.Invoke("[TryCropToPng] failed to get rotated crop");
                    return false;
                }

                cropRect = cropRectLocal;
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

        private static string BuildShapeJson(RoiModel roi, RoiCropInfo info, Rect cropRect)
        {
            double w = cropRect.Width;
            double h = cropRect.Height;

            object shape = roi.Shape switch
            {
                RoiShape.Rectangle => new { kind = "rect", x = 0, y = 0, w, h },
                RoiShape.Circle => new
                {
                    kind = "circle",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = Math.Min(w, h) / 2.0
                },
                RoiShape.Annulus => new
                {
                    kind = "annulus",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = ResolveOuterRadiusPx(info, cropRect),
                    r_inner = ResolveInnerRadiusPx(info, cropRect)
                },
                _ => new { kind = "rect", x = 0, y = 0, w, h }
            };

            return JsonSerializer.Serialize(shape, JsonOptions);
        }

        private static double ResolveOuterRadiusPx(RoiCropInfo info, Rect cropRect)
        {
            double outer = info.Radius > 0 ? info.Radius : Math.Max(info.Width, info.Height) / 2.0;
            double scale = Math.Min(cropRect.Width / Math.Max(info.Width, 1.0), cropRect.Height / Math.Max(info.Height, 1.0));
            double result = outer * scale;
            if (result <= 0)
            {
                result = Math.Min(cropRect.Width, cropRect.Height) / 2.0;
            }

            return result;
        }

        private static double ResolveInnerRadiusPx(RoiCropInfo info, Rect cropRect)
        {
            if (info.Shape != RoiShape.Annulus)
                return 0;

            double inner = Math.Clamp(info.InnerRadius, 0, info.Radius);
            double scale = Math.Min(cropRect.Width / Math.Max(info.Width, 1.0), cropRect.Height / Math.Max(info.Height, 1.0));
            double result = inner * scale;
            return Math.Max(0, result);
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
