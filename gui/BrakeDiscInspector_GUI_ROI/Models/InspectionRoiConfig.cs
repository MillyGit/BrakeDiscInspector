using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BrakeDiscInspector_GUI_ROI;
using BrakeDiscInspector_GUI_ROI.Workflow;

namespace BrakeDiscInspector_GUI_ROI.Models
{
    public class InspectionRoiConfig : INotifyPropertyChanged
    {
        private int _index;
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
        private ObservableCollection<DatasetPreviewItem> _okPreview = new();
        private ObservableCollection<DatasetPreviewItem> _ngPreview = new();

        public InspectionRoiConfig(int index)
        {
            Index = index;
            _name = $"Inspection {index}";
            _modelKey = $"inspection-{index}";
        }

        public int Index
        {
            get => _index;
            private set
            {
                if (_index == value) return;
                _index = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public string DisplayName => $"Inspection {Index}";

        public string Name
        {
            get => _name;
            set
            {
                var newValue = string.IsNullOrWhiteSpace(value) ? $"Inspection {Index}" : value;
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
                var newValue = string.IsNullOrWhiteSpace(value) ? $"inspection-{Index}" : value;
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

        [JsonIgnore]
        public ObservableCollection<DatasetPreviewItem> OkPreview
        {
            get => _okPreview;
            set
            {
                if (ReferenceEquals(_okPreview, value)) return;
                _okPreview = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public ObservableCollection<DatasetPreviewItem> NgPreview
        {
            get => _ngPreview;
            set
            {
                if (ReferenceEquals(_ngPreview, value)) return;
                _ngPreview = value;
                OnPropertyChanged();
            }
        }

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
                OnPropertyChanged(nameof(OkCount));
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
                OnPropertyChanged(nameof(NgCount));
            }
        }

        [JsonIgnore]
        public string DatasetCountsText => $"OK: {DatasetOkCount} · KO: {DatasetKoCount}";

        [JsonIgnore]
        public int OkCount
        {
            get => DatasetOkCount;
            set => DatasetOkCount = value;
        }

        [JsonIgnore]
        public int NgCount
        {
            get => DatasetKoCount;
            set => DatasetKoCount = value;
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
                OnPropertyChanged(nameof(LastEvaluatedAgo));
            }
        }

        public TimeSpan? LastEvaluatedAgo => _lastEvaluatedAt == null ? null : DateTime.UtcNow - _lastEvaluatedAt;

        public void MarkEvaluated(bool? ok, double? score)
        {
            LastResultOk = ok;
            LastScore = score;
            LastEvaluatedAt = DateTime.UtcNow;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
