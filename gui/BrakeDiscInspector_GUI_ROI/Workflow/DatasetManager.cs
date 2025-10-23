using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class DatasetManager
    {
        private const string ImagesFolderName = "images";

        public DatasetManager(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            Directory.CreateDirectory(Path.Combine(RootDirectory, ImagesFolderName));
        }

        public string RootDirectory { get; }

        private string ImagesRoot => Path.Combine(RootDirectory, ImagesFolderName);

        public string GetRoleRoiDirectory(string roleId, string roiId)
        {
            return ImagesRoot;
        }

        public async Task<DatasetSample> SaveSampleAsync(
            string roleId,
            string roiId,
            bool isNg,
            byte[] pngBytes,
            string shapeJson,
            double mmPerPx,
            string sourceImagePath,
            double angleDeg)
        {
            var labelDir = Path.Combine(ImagesRoot, isNg ? "ng" : "ok");
            Directory.CreateDirectory(labelDir);

            var timestamp = DateTime.UtcNow;
            string fileName = $"SAMPLE_{Sanitize(roleId)}_{Sanitize(roiId)}_{timestamp:yyyyMMdd_HHmmssfff}.png";
            string imagePath = Path.Combine(labelDir, fileName);
            await File.WriteAllBytesAsync(imagePath, pngBytes).ConfigureAwait(false);

            var metadata = new SampleMetadata
            {
                role_id = roleId,
                roi_id = roiId,
                mm_per_px = mmPerPx,
                shape_json = shapeJson,
                source_path = sourceImagePath,
                angle = angleDeg,
                timestamp = timestamp
            };

            string metadataPath = Path.ChangeExtension(imagePath, ".json");
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            };
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, options);
            await File.WriteAllBytesAsync(metadataPath, jsonBytes).ConfigureAwait(false);

            return new DatasetSample(imagePath, metadataPath, isNg, metadata);
        }

        public Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(string roleId, string roiId, bool isNg)
        {
            return Task.Run(() =>
            {
                var result = new List<DatasetSample>();
                var labelDir = Path.Combine(ImagesRoot, isNg ? "ng" : "ok");
                if (!Directory.Exists(labelDir))
                {
                    return (IReadOnlyList<DatasetSample>)result;
                }

                foreach (var png in Directory.EnumerateFiles(labelDir, "*.png", SearchOption.TopDirectoryOnly).OrderBy(p => p))
                {
                    if (DatasetSample.TryRead(png, out var sample) && sample != null)
                    {
                        var metaRole = sample.Metadata.role_id ?? string.Empty;
                        var metaRoi = sample.Metadata.roi_id ?? string.Empty;
                        bool matches = string.Equals(metaRole, roleId, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(metaRoi, roiId, StringComparison.OrdinalIgnoreCase);
                        bool legacy = string.IsNullOrWhiteSpace(metaRole) && string.IsNullOrWhiteSpace(metaRoi);
                        if (matches || legacy)
                        {
                            result.Add(sample);
                        }
                    }
                }

                return (IReadOnlyList<DatasetSample>)result;
            });
        }

        public void EnsureRoleRoiDirectories(string roleId, string roiId)
        {
            Directory.CreateDirectory(Path.Combine(ImagesRoot, "ok"));
            Directory.CreateDirectory(Path.Combine(ImagesRoot, "ng"));
        }

        public void DeleteSample(DatasetSample sample)
        {
            if (File.Exists(sample.ImagePath))
            {
                File.Delete(sample.ImagePath);
            }

            if (File.Exists(sample.MetadataPath))
            {
                File.Delete(sample.MetadataPath);
            }
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
            {
                value = value.Replace(c, '_');
            }
            var trimmed = value.Trim();
            return string.IsNullOrEmpty(trimmed) ? "default" : trimmed;
        }
    }
}
