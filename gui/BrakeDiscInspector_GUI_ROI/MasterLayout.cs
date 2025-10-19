using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
            if (!File.Exists(path)) return new MasterLayout();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MasterLayout>(json) ?? new MasterLayout();
        }

        public static void Save(PresetFile preset, MasterLayout layout)
        {
            var path = GetDefaultPath(preset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    public class InspectionRoiConfig : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string? _modelKey;
        private double _threshold;
        private RoiShape _shape = RoiShape.Rectangle;

        public InspectionRoiConfig(int index)
        {
            DisplayName = $"ROI {index}";
            _modelKey = "default";
        }

        public string DisplayName { get; }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnPropertyChanged();
            }
        }

        public string? ModelKey
        {
            get => _modelKey;
            set
            {
                if (_modelKey == value) return;
                _modelKey = value;
                OnPropertyChanged();
            }
        }

        public double Threshold
        {
            get => _threshold;
            set
            {
                if (Math.Abs(_threshold - value) < double.Epsilon) return;
                _threshold = value;
                OnPropertyChanged();
            }
        }

        public RoiShape Shape
        {
            get => _shape;
            set
            {
                if (_shape == value) return;
                _shape = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
