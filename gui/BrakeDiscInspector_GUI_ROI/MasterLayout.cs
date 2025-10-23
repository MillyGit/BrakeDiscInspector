using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using BrakeDiscInspector_GUI_ROI.Models;

namespace BrakeDiscInspector_GUI_ROI
{
    public class MasterLayout
    {
        public RoiModel? Master1Pattern { get; set; }
        public string? Master1PatternImagePath { get; set; }
        public RoiModel? Master1Search { get; set; }
        public RoiModel? Master2Pattern { get; set; }
        public string? Master2PatternImagePath { get; set; }
        public RoiModel? Master2Search { get; set; }
        public RoiModel? Inspection { get; set; }
        public RoiModel? InspectionBaseline { get; set; }

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; }
            = new ObservableCollection<InspectionRoiConfig>
            {
                new InspectionRoiConfig(1),
                new InspectionRoiConfig(2),
                new InspectionRoiConfig(3),
                new InspectionRoiConfig(4),
            };
    }

    public static class MasterLayoutManager
    {
        public static string GetDefaultPath(PresetFile preset)
            => Path.Combine(preset.Home, "configs", "master_layout.json");

        public static MasterLayout LoadOrNew(PresetFile preset)
        {
            var path = GetDefaultPath(preset);
            MasterLayout layout;
            if (!File.Exists(path))
            {
                layout = new MasterLayout();
            }
            else
            {
                var json = File.ReadAllText(path);
                layout = JsonSerializer.Deserialize<MasterLayout>(json) ?? new MasterLayout();
            }

            EnsureInspectionRoiDefaults(layout);
            return layout;
        }

        public static void Save(PresetFile preset, MasterLayout layout)
        {
            var path = GetDefaultPath(preset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        private static void EnsureInspectionRoiDefaults(MasterLayout layout)
        {
            for (int i = 0; i < layout.InspectionRois.Count; i++)
            {
                var roi = layout.InspectionRois[i];
                if (string.IsNullOrWhiteSpace(roi.Name))
                {
                    roi.Name = $"Inspection {i + 1}";
                }

                if (string.IsNullOrWhiteSpace(roi.ModelKey))
                {
                    roi.ModelKey = $"inspection-{i + 1}";
                }
            }
        }
    }
}
