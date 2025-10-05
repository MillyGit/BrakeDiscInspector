
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    /// <summary>
    /// BackendClient revisado: mantiene la API pública original (métodos, firmas y DTOs)
    /// y añade robustez (tipos MIME correctos, InvariantCulture, errores con cuerpo).
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

        public async Task<FitOkResult> FitOkAsync(string roleId, string roiId, double mmPerPx, IEnumerable<string> okImagePaths)
        {
            if (string.IsNullOrWhiteSpace(roleId)) throw new ArgumentException("Role id required", nameof(roleId));
            if (string.IsNullOrWhiteSpace(roiId)) throw new ArgumentException("ROI id required", nameof(roiId));

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");

            bool hasImage = false;
            foreach (var path in okImagePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                var stream = File.OpenRead(path);
                var content = new StreamContent(stream);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMediaType(path));
                // Importante: mismo nombre de campo para cada imagen -> "images"
                form.Add(content, "images", Path.GetFileName(path));
                hasImage = true;
            }

            if (!hasImage)
            {
                throw new InvalidOperationException("No OK images were provided for training.");
            }

            using var response = await _httpClient.PostAsync("fit_ok", form).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/fit_ok {response.StatusCode}: {body}");

            using var streamResp = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var payload = await JsonSerializer.DeserializeAsync<FitOkResult>(streamResp, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty or invalid JSON from fit_ok endpoint.");
            }

            return payload;
        }

        public async Task<CalibResult> CalibrateAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            IEnumerable<double> okScores,
            IEnumerable<double>? ngScores = null,
            double areaMm2Thr = 1.0,
            int scorePercentile = 99)
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
            using var response = await _httpClient.PostAsync("calibrate_ng", content).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/calibrate_ng {response.StatusCode}: {raw}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<CalibResult>(stream, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty or invalid JSON from calibrate_ng endpoint.");
            }

            return payload;
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            string imagePath,
            string? shapeJson = null)
        {
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException("Inference image not found", imagePath);
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");

            var content = new StreamContent(File.OpenRead(imagePath));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMediaType(imagePath));
            form.Add(content, "image", Path.GetFileName(imagePath));

            if (!string.IsNullOrWhiteSpace(shapeJson))
            {
                // Importante: enviar como texto plano (sin application/json) para FastAPI Form(str)
                form.Add(new StringContent(shapeJson, Encoding.UTF8), "shape");
            }

            using var response = await _httpClient.PostAsync("infer", form).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/infer {response.StatusCode}: {raw}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<InferResult>(stream, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty or invalid JSON from infer endpoint.");
            }

            return payload;
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            Stream imageStream,
            string fileName,
            string? shapeJson = null)
        {
            if (imageStream == null) throw new ArgumentNullException(nameof(imageStream));
            if (imageStream.CanSeek) imageStream.Seek(0, SeekOrigin.Begin);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");

            var content = new StreamContent(imageStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMediaType(fileName));
            form.Add(content, "image", string.IsNullOrWhiteSpace(fileName) ? "roi" : fileName);

            if (!string.IsNullOrWhiteSpace(shapeJson))
            {
                form.Add(new StringContent(shapeJson, Encoding.UTF8), "shape");
            }

            using var response = await _httpClient.PostAsync("infer", form).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/infer {response.StatusCode}: {raw}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<InferResult>(stream, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty or invalid JSON from infer endpoint.");
            }

            return payload;
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            byte[] imageBytes,
            string fileName,
            string? shapeJson = null)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                throw new ArgumentException("Image bytes are required", nameof(imageBytes));
            }

            await using var ms = new MemoryStream(imageBytes, writable: false);
            return await InferAsync(roleId, roiId, mmPerPx, ms, fileName, shapeJson).ConfigureAwait(false);
        }

        public async Task<HealthInfo?> GetHealthAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync("health").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
                return await JsonSerializer.DeserializeAsync<HealthInfo>(stream, JsonOptions).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

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

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
    }

    // ============ DTOs (sin cambios de nombres/campos) ============

    public sealed class FitOkResult
    {
        public int n_embeddings { get; set; }
        public int coreset_size { get; set; }
        public int[]? token_shape { get; set; }
    }

    public sealed class CalibResult
    {
        [JsonPropertyName("threshold")]
        public double? Threshold { get; set; } // ¡nullable!
    
        [JsonPropertyName("score_percentile")]
        public int ScorePercentile { get; set; }
    
        [JsonPropertyName("ok_count")]
        public int OkCount { get; set; }
    
        [JsonPropertyName("ng_count")]
        public int NgCount { get; set; }
    }

    public sealed class InferResult
    {
        [JsonPropertyName("score")]
        public double Score { get; set; }
    
        // ¡nullable! para no romper si viene null u omitido
        [JsonPropertyName("threshold")]
        public double? Threshold { get; set; }
    
        [JsonPropertyName("token_shape")]
        public int[]? TokenShape { get; set; }  // ej. [56,56] (opcional)
    
        [JsonPropertyName("heatmap_png_base64")]
        public string? HeatmapPngBase64 { get; set; }
    
        [JsonPropertyName("regions")]
        public object? Regions { get; set; } // o tu tipo concreto
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
