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
        public DatasetManager(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string RootDirectory { get; }

        public string GetRoleRoiDirectory(string roleId, string roiId)
        {
            return Path.Combine(RootDirectory, Sanitize(roleId), Sanitize(roiId));
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
            var baseDir = GetRoleRoiDirectory(roleId, roiId);
            var labelDir = Path.Combine(baseDir, isNg ? "ng" : "ok");
            Directory.CreateDirectory(labelDir);

            var timestamp = DateTime.UtcNow;
            string fileName = $"SAMPLE_{timestamp:yyyyMMdd_HHmmssfff}.png";
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
                var labelDir = Path.Combine(GetRoleRoiDirectory(roleId, roiId), isNg ? "ng" : "ok");
                if (!Directory.Exists(labelDir))
                {
                    return (IReadOnlyList<DatasetSample>)result;
                }

                foreach (var png in Directory.EnumerateFiles(labelDir, "*.png", SearchOption.TopDirectoryOnly).OrderBy(p => p))
                {
                    if (DatasetSample.TryRead(png, out var sample) && sample != null)
                    {
                        result.Add(sample);
                    }
                }

                return (IReadOnlyList<DatasetSample>)result;
            });
        }

        public void EnsureRoleRoiDirectories(string roleId, string roiId)
        {
            var baseDir = GetRoleRoiDirectory(roleId, roiId);
            Directory.CreateDirectory(Path.Combine(baseDir, "ok"));
            Directory.CreateDirectory(Path.Combine(baseDir, "ng"));
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
            return value.Trim();
        }
    }
}
