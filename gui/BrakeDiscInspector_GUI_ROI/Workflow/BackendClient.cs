using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class BackendClient
    {
        private readonly HttpClient _httpClient;

        public BackendClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
            BaseUrl = "http://127.0.0.1:8000";
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

        public async Task<FitOkResult> FitOkAsync(string roleId, string roiId, double mmPerPx, IEnumerable<string> okImagePaths)
        {
            if (string.IsNullOrWhiteSpace(roleId)) throw new ArgumentException("Role id required", nameof(roleId));
            if (string.IsNullOrWhiteSpace(roiId)) throw new ArgumentException("ROI id required", nameof(roiId));

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(System.Globalization.CultureInfo.InvariantCulture)), "mm_per_px");

            int index = 0;
            foreach (var path in okImagePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                var content = new StreamContent(File.OpenRead(path));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(content, $"images[{index}]", Path.GetFileName(path));
                index++;
            }

            if (index == 0)
            {
                throw new InvalidOperationException("No OK images were provided for training.");
            }

            using var response = await _httpClient.PostAsync("fit_ok", form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<FitOkResult>(stream, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty response from fit_ok endpoint.");
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
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<CalibResult>(stream, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty response from calibrate_ng endpoint.");
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
            form.Add(new StringContent(mmPerPx.ToString(System.Globalization.CultureInfo.InvariantCulture)), "mm_per_px");

            var content = new StreamContent(File.OpenRead(imagePath));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(content, "image", Path.GetFileName(imagePath));

            if (!string.IsNullOrWhiteSpace(shapeJson))
            {
                form.Add(new StringContent(shapeJson, Encoding.UTF8, "application/json"), "shape");
            }

            using var response = await _httpClient.PostAsync("infer", form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<InferResult>(stream, JsonOptions).ConfigureAwait(false);
            if (payload == null)
            {
                throw new InvalidOperationException("Empty response from infer endpoint.");
            }

            return payload;
        }

        public async Task<HealthInfo?> GetHealthAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync("health").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<HealthInfo>(stream, JsonOptions).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public sealed class FitOkResult
    {
        public int n_embeddings { get; set; }
        public int coreset_size { get; set; }
        public int[]? token_shape { get; set; }
    }

    public sealed class CalibResult
    {
        public double threshold { get; set; }
        public double ok_mean { get; set; }
        public double ng_mean { get; set; }
        public double score_percentile { get; set; }
        public double area_mm2_thr { get; set; }
    }

    public sealed class InferResult
    {
        public double score { get; set; }
        public double threshold { get; set; }
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
