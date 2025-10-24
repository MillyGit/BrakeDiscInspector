using System;
using System.Text.Json;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class PresetSerializer
    {
        public static MastersPreset LoadMastersPreset(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Ruta de preset inválida", nameof(path));
            }

            var json = System.IO.File.ReadAllText(path);
            var obj = JsonSerializer.Deserialize<MastersPreset>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (obj == null)
            {
                throw new InvalidOperationException("Preset vacío o inválido");
            }

            if (obj.Master1 == null || obj.Master1Inspection == null || obj.Master2 == null || obj.Master2Inspection == null)
            {
                throw new InvalidOperationException("Preset incompleto: faltan Masters.");
            }

            return obj;
        }
    }

    public class MastersPreset
    {
        public RoiDto? Master1 { get; set; }
        public RoiDto? Master1Inspection { get; set; }
        public RoiDto? Master2 { get; set; }
        public RoiDto? Master2Inspection { get; set; }
        public bool scale_lock { get; set; }
        public bool use_local_matcher { get; set; }
        public double mm_per_px { get; set; }
    }

    public class RoiDto
    {
        public string? Shape { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double AngleDeg { get; set; }
        public double? InnerRadius { get; set; }
        public double InnerDiameter { get; set; }
    }
}
