using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public class InspectionRoiConfig : INotifyPropertyChanged
    {
        private readonly int _index;
        private bool _enabled = true;
        private string _modelKey;
        private double _threshold;
        private RoiShape _shape = RoiShape.Rectangle;
        private string _name;
        private string? _datasetPath;
        private bool _trainMemoryFit;
        private double? _calibratedThreshold;
        private double _thresholdDefault = 0.5;
        private double? _lastScore;
        private bool? _lastResultOk;
        private DateTime? _lastEvaluatedAt;
        private bool _datasetReady;
        private bool _isDatasetLoading;
        private string _datasetStatus = string.Empty;
        private int _datasetOkCount;
        private int _datasetKoCount;

        public InspectionRoiConfig(int index)
        {
            _index = index;
            DisplayName = $"ROI {index}";
            _name = $"Inspection {index}";
            _modelKey = $"inspection-{index}";
            DatasetPreview = new ObservableCollection<DatasetPreviewItem>();
        }

        public string DisplayName { get; }

        public string Name
        {
            get => _name;
            set
            {
                var newValue = string.IsNullOrWhiteSpace(value) ? $"Inspection {_index}" : value;
                if (string.Equals(_name, newValue, StringComparison.Ordinal)) return;
                _name = newValue;
                OnPropertyChanged();
            }
        }

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

        public string ModelKey
        {
            get => _modelKey;
            set
            {
                var newValue = string.IsNullOrWhiteSpace(value) ? $"inspection-{_index}" : value;
                if (string.Equals(_modelKey, newValue, StringComparison.Ordinal)) return;
                _modelKey = newValue;
                OnPropertyChanged();
            }
        }

        public string? DatasetPath
        {
            get => _datasetPath;
            set
            {
                if (string.Equals(_datasetPath, value, StringComparison.Ordinal)) return;
                _datasetPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDatasetPath));
            }
        }

        [JsonIgnore]
        public bool HasDatasetPath => !string.IsNullOrWhiteSpace(_datasetPath);

        public bool TrainMemoryFit
        {
            get => _trainMemoryFit;
            set
            {
                if (_trainMemoryFit == value) return;
                _trainMemoryFit = value;
                OnPropertyChanged();
            }
        }

        public double? CalibratedThreshold
        {
            get => _calibratedThreshold;
            set
            {
                if (_calibratedThreshold == value) return;
                _calibratedThreshold = value;
                OnPropertyChanged();
            }
        }

        public double ThresholdDefault
        {
            get => _thresholdDefault;
            set
            {
                if (Math.Abs(_thresholdDefault - value) < double.Epsilon) return;
                _thresholdDefault = value;
                OnPropertyChanged();
            }
        }

        public double? LastScore
        {
            get => _lastScore;
            set
            {
                if (_lastScore == value) return;
                _lastScore = value;
                OnPropertyChanged();
            }
        }

        public bool? LastResultOk
        {
            get => _lastResultOk;
            set
            {
                if (_lastResultOk == value) return;
                _lastResultOk = value;
                OnPropertyChanged();
            }
        }

        public DateTime? LastEvaluatedAt
        {
            get => _lastEvaluatedAt;
            set
            {
                if (_lastEvaluatedAt == value) return;
                _lastEvaluatedAt = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public ObservableCollection<DatasetPreviewItem> DatasetPreview { get; }

        [JsonIgnore]
        public bool DatasetReady
        {
            get => _datasetReady;
            set
            {
                if (_datasetReady == value) return;
                _datasetReady = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public bool IsDatasetLoading
        {
            get => _isDatasetLoading;
            set
            {
                if (_isDatasetLoading == value) return;
                _isDatasetLoading = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public string DatasetStatus
        {
            get => _datasetStatus;
            set
            {
                if (string.Equals(_datasetStatus, value, StringComparison.Ordinal)) return;
                _datasetStatus = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public int DatasetOkCount
        {
            get => _datasetOkCount;
            set
            {
                if (_datasetOkCount == value) return;
                _datasetOkCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DatasetCountsText));
            }
        }

        [JsonIgnore]
        public int DatasetKoCount
        {
            get => _datasetKoCount;
            set
            {
                if (_datasetKoCount == value) return;
                _datasetKoCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DatasetCountsText));
            }
        }

        [JsonIgnore]
        public string DatasetCountsText => $"OK: {DatasetOkCount} · KO: {DatasetKoCount}";

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
