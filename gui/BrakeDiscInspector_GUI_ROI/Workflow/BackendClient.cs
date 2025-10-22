using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    /// <summary>
    /// BackendClient revisado: API igual, robustez en JSON, MIME y errores.
    /// </summary>
    public sealed class BackendClient
    {
        private readonly HttpClient _httpClient;

        public BackendClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
            BaseUrl = ResolveDefaultBaseUrl();
        }

        public string BaseUrl
        {
            get => _httpClient.BaseAddress?.ToString() ?? "";
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _httpClient.BaseAddress = null;
                    return;
                }

                var trimmed = value.TrimEnd('/');
                if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = "http://" + trimmed;
                }

                _httpClient.BaseAddress = new Uri(trimmed + "/");
            }
        }

        private static string ResolveDefaultBaseUrl()
        {
            string defaultUrl = "http://127.0.0.1:8000";

            string? envBaseUrl =
                Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASEURL") ??
                Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASE_URL") ??
                Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_URL");

            if (!string.IsNullOrWhiteSpace(envBaseUrl))
            {
                return envBaseUrl;
            }

            var host = Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_HOST") ??
                       Environment.GetEnvironmentVariable("HOST");
            var port = Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_PORT") ??
                       Environment.GetEnvironmentVariable("PORT");

            if (!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(port))
            {
                host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
                port = string.IsNullOrWhiteSpace(port) ? "8000" : port.Trim();
                return $"{host}:{port}";
            }

            return defaultUrl;
        }

        // =========================
        //  Endpoints
        // =========================

        public async Task<bool> SupportsEndpointAsync(string relativePath, CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Options, relativePath);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }

                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    return true;
                }

                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
                {
                    return true;
                }

                return response.StatusCode != HttpStatusCode.NotFound;
            }
            catch
            {
                return false;
            }
        }

        public async Task<FitOkResult> FitOkAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            IEnumerable<string> okImagePaths,
            bool memoryFit = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(roleId)) throw new ArgumentException("Role id required", nameof(roleId));
            if (string.IsNullOrWhiteSpace(roiId)) throw new ArgumentException("ROI id required", nameof(roiId));

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");
            form.Add(new StringContent(memoryFit ? "true" : "false"), "memory_fit");

            bool hasImage = false;
            foreach (var path in okImagePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                var stream = File.OpenRead(path);
                var content = new StreamContent(stream);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMediaType(path));
                // Importante: mismo nombre de campo para cada imagen -> "images"
                form.Add(content, "images", Path.GetFileName(path));
                hasImage = true;
            }

            if (!hasImage)
                throw new InvalidOperationException("No OK images were provided for training.");

            using var response = await _httpClient.PostAsync("fit_ok", form, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/fit_ok {response.StatusCode}: {body}");

            using var streamResp = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var payload = await JsonSerializer.DeserializeAsync<FitOkResult>(streamResp, JsonOptions, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Empty or invalid JSON from fit_ok endpoint.");

            return payload;
        }

        public async Task<CalibResult> CalibrateAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            IEnumerable<double> okScores,
            IEnumerable<double>? ngScores = null,
            double areaMm2Thr = 1.0,
            int scorePercentile = 99,
            CancellationToken ct = default)
        {
            var body = new
            {
                role_id = roleId,
                roi_id = roiId,
                mm_per_px = mmPerPx,
                ok_scores = okScores,
                ng_scores = ngScores,
                area_mm2_thr = areaMm2Thr,
                score_percentile = scorePercentile
            };

            using var content = JsonContent.Create(body, options: JsonOptions);
            using var response = await _httpClient.PostAsync("calibrate_ng", content, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/calibrate_ng {response.StatusCode}: {raw}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<CalibResult>(stream, JsonOptions, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Empty or invalid JSON from calibrate_ng endpoint.");

            // Fallback por si backend devuelve null u omite el campo (evita romper UI)
            payload.threshold ??= 0.5;

            return payload;
        }

        // --------- Inferencias (sobrecargas) ---------

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            string imagePath,
            string? shapeJson = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Inference image not found", imagePath);

            var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
            return await _InferMultipartAsync(roleId, roiId, mmPerPx, bytes, Path.GetFileName(imagePath), shapeJson, ct)
                   .ConfigureAwait(false);
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            Stream imageStream,
            string fileName,
            string? shapeJson = null,
            CancellationToken ct = default)
        {
            if (imageStream == null) throw new ArgumentNullException(nameof(imageStream));
            var bytes = await ReadAllBytesAsync(imageStream, ct).ConfigureAwait(false);
            return await _InferMultipartAsync(roleId, roiId, mmPerPx, bytes, fileName, shapeJson, ct)
                   .ConfigureAwait(false);
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            byte[] imageBytes,
            string fileName,
            string? shapeJson = null,
            CancellationToken ct = default)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("Image bytes are required", nameof(imageBytes));

            return await _InferMultipartAsync(roleId, roiId, mmPerPx, imageBytes, fileName, shapeJson, ct)
                   .ConfigureAwait(false);
        }

        // Core común para inferencias (evita duplicar lógica)
        private async Task<InferResult> _InferMultipartAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            byte[] imageBytes,
            string fileName,
            string? shapeJson,
            CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");

            if (!string.IsNullOrWhiteSpace(roiId))
            {
                form.Add(new StringContent(roiId), "model_key");
            }

            var content = new ByteArrayContent(imageBytes);
            var mediaType = GuessMediaType(fileName);
            if (mediaType == "application/octet-stream")
            {
                mediaType = "image/png";
            }
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "roi.png" : fileName;
            form.Add(content, "file", safeFileName);

            if (!string.IsNullOrWhiteSpace(shapeJson))
            {
                // Texto plano para FastAPI Form(str)
                form.Add(new StringContent(shapeJson, Encoding.UTF8), "shape");
            }

            using var response = await _httpClient.PostAsync("infer", form, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/infer {response.StatusCode}: {raw}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<InferResult>(stream, JsonOptions, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Empty or invalid JSON from infer endpoint.");

            // Fallback: si el backend devuelve threshold null/omitido
            payload.threshold ??= 0.5;

            return payload;
        }

        public async Task<HealthInfo?> GetHealthAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync("health", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
                return await JsonSerializer.DeserializeAsync<HealthInfo>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        // =========================
        // Utilidades
        // =========================

        private static string GuessMediaType(string pathOrName)
        {
            string ext = Path.GetExtension(pathOrName)?.ToLowerInvariant() ?? "";
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => "application/octet-stream"
            };
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
        {
            if (stream is MemoryStream memoryStream)
            {
                if (memoryStream.CanSeek)
                {
                    memoryStream.Seek(0, SeekOrigin.Begin);
                }
                return memoryStream.ToArray();
            }

            using var ms = new MemoryStream();
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }

        // Opciones JSON robustas
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
                           | JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
    }

    // ============ DTOs (mismos nombres/campos; threshold nullable) ============

    public sealed class FitOkResult
    {
        public int n_embeddings { get; set; }
        public int coreset_size { get; set; }
        public int[]? token_shape { get; set; }
    }

    public sealed class CalibResult
    {
        public double? threshold { get; set; }      // << ahora nullable
        public double ok_mean { get; set; }
        public double ng_mean { get; set; }
        public double score_percentile { get; set; }
        public double area_mm2_thr { get; set; }
    }

    public sealed class InferResult
    {
        public double score { get; set; }
        public double? threshold { get; set; }      // << ahora nullable
        public string? heatmap_png_base64 { get; set; }
        public Region[]? regions { get; set; }
    }

    public sealed class Region
    {
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        public double area_px { get; set; }
        public double area_mm2 { get; set; }
    }

    public sealed class HealthInfo
    {
        public string? status { get; set; }
        public string? device { get; set; }
        public string? model { get; set; }
        public string? version { get; set; }
    }
}
