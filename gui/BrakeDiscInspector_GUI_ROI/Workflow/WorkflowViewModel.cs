using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrakeDiscInspector_GUI_ROI.Models;
using Forms = System.Windows.Forms;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class WorkflowViewModel : INotifyPropertyChanged
    {
        private readonly BackendClient _client;
        private readonly DatasetManager _datasetManager;
        private readonly Func<Task<RoiExportResult?>> _exportRoiAsync;
        private readonly Func<string?> _getSourceImagePath;
        private readonly Action<string> _log;
        private readonly Func<RoiExportResult, byte[], double, Task> _showHeatmapAsync;
        private readonly Action _clearHeatmap;
        private readonly Action<bool?> _updateGlobalBadge;

        private ObservableCollection<InspectionRoiConfig>? _inspectionRois;
        private InspectionRoiConfig? _selectedInspectionRoi;
        private bool? _hasFitEndpoint;
        private bool? _hasCalibrateEndpoint;

        private bool _isBusy;
        private string _roleId = "Master1";
        private string _roiId = "Inspection";
        private double _mmPerPx = 0.20;
        private string _backendBaseUrl;
        private string _fitSummary = "";
        private string _calibrationSummary = "";
        private double? _inferenceScore;
        private double? _inferenceThreshold;
        private double _localThreshold;
        private string _inferenceSummary = string.Empty;
        private double _heatmapOpacity = 0.6;
        private string _healthSummary = "";

        private RoiExportResult? _lastExport;
        private byte[]? _lastHeatmapBytes;
        private InferResult? _lastInferResult;
        private readonly Dictionary<InspectionRoiConfig, RoiDatasetAnalysis> _roiDatasetCache = new();

        public WorkflowViewModel(
            BackendClient client,
            DatasetManager datasetManager,
            Func<Task<RoiExportResult?>> exportRoiAsync,
            Func<string?> getSourceImagePath,
            Action<string> log,
            Func<RoiExportResult, byte[], double, Task> showHeatmapAsync,
            Action clearHeatmap,
            Action<bool?> updateGlobalBadge)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _datasetManager = datasetManager ?? throw new ArgumentNullException(nameof(datasetManager));
            _exportRoiAsync = exportRoiAsync ?? throw new ArgumentNullException(nameof(exportRoiAsync));
            _getSourceImagePath = getSourceImagePath ?? throw new ArgumentNullException(nameof(getSourceImagePath));
            _log = log ?? (_ => { });
            _showHeatmapAsync = showHeatmapAsync ?? throw new ArgumentNullException(nameof(showHeatmapAsync));
            _clearHeatmap = clearHeatmap ?? throw new ArgumentNullException(nameof(clearHeatmap));
            _updateGlobalBadge = updateGlobalBadge ?? (_ => { });

            _backendBaseUrl = _client.BaseUrl;

            OkSamples = new ObservableCollection<DatasetSample>();
            NgSamples = new ObservableCollection<DatasetSample>();

            AddOkFromCurrentRoiCommand = CreateCommand(_ => AddSampleAsync(isNg: false));
            AddNgFromCurrentRoiCommand = CreateCommand(_ => AddSampleAsync(isNg: true));
            RemoveSelectedCommand = CreateCommand(_ => RemoveSelectedAsync(), _ => !IsBusy && (SelectedOkSample != null || SelectedNgSample != null));
            OpenDatasetFolderCommand = CreateCommand(_ => OpenDatasetFolderAsync(), _ => !IsBusy);
            TrainFitCommand = CreateCommand(_ => TrainAsync(), _ => !IsBusy && OkSamples.Count > 0);
            CalibrateCommand = CreateCommand(_ => CalibrateAsync(), _ => !IsBusy && OkSamples.Count > 0);
            InferFromCurrentRoiCommand = CreateCommand(_ => InferCurrentAsync(), _ => !IsBusy);
            RefreshDatasetCommand = CreateCommand(_ => RefreshDatasetAsync(), _ => !IsBusy);
            RefreshHealthCommand = CreateCommand(_ => RefreshHealthAsync(), _ => !IsBusy);

            BrowseDatasetCommand = CreateCommand(_ => BrowseDatasetAsync(), _ => !IsBusy && SelectedInspectionRoi != null);
            TrainSelectedRoiCommand = CreateCommand(async _ => await TrainSelectedRoiAsync().ConfigureAwait(false), _ => !IsBusy && SelectedInspectionRoi != null);
            CalibrateSelectedRoiCommand = CreateCommand(async _ => await CalibrateSelectedRoiAsync().ConfigureAwait(false), _ => !IsBusy && CanCalibrateSelectedRoi());
            EvaluateSelectedRoiCommand = CreateCommand(_ => EvaluateSelectedRoiAsync(), _ => !IsBusy && SelectedInspectionRoi != null && SelectedInspectionRoi.Enabled);
            EvaluateAllRoisCommand = CreateCommand(_ => EvaluateAllRoisAsync(), _ => !IsBusy && HasAnyEnabledInspectionRoi());

            AddRoiToDatasetOkCommand = new AsyncCommand(async param =>
            {
                if (param is InspectionRoiConfig roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, isOk: true)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToDatasetNgCommand = new AsyncCommand(async param =>
            {
                if (param is InspectionRoiConfig roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, isOk: false)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);
        }

        public ObservableCollection<DatasetSample> OkSamples { get; }
        public ObservableCollection<DatasetSample> NgSamples { get; }

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; private set; } = new();

        public InspectionRoiConfig? SelectedInspectionRoi
        {
            get => _selectedInspectionRoi;
            set
            {
                if (!ReferenceEquals(_selectedInspectionRoi, value))
                {
                    _selectedInspectionRoi = value;
                    OnPropertyChanged();
                    UpdateSelectedRoiState();
                }
            }
        }

        public void SetInspectionRoisCollection(ObservableCollection<InspectionRoiConfig>? rois)
        {
            if (ReferenceEquals(_inspectionRois, rois))
            {
                return;
            }

            if (_inspectionRois != null)
            {
                _inspectionRois.CollectionChanged -= InspectionRoisCollectionChanged;
                foreach (var roi in _inspectionRois)
                {
                    roi.PropertyChanged -= InspectionRoiPropertyChanged;
                }
            }

            _inspectionRois = rois;
            InspectionRois = rois ?? new ObservableCollection<InspectionRoiConfig>();
            OnPropertyChanged(nameof(InspectionRois));

            if (_inspectionRois != null)
            {
                _inspectionRois.CollectionChanged += InspectionRoisCollectionChanged;
                foreach (var roi in _inspectionRois)
                {
                    roi.PropertyChanged += InspectionRoiPropertyChanged;
                    if (string.IsNullOrWhiteSpace(roi.DatasetStatus))
                    {
                        roi.DatasetStatus = roi.HasDatasetPath ? "Validating dataset..." : "Select a dataset";
                    }
                    if (roi.HasDatasetPath)
                    {
                        roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                        roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                        _ = RefreshRoiDatasetStateAsync(roi);
                    }
                    else
                    {
                        roi.DatasetReady = false;
                        roi.DatasetOkCount = 0;
                        roi.DatasetKoCount = 0;
                        roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                        roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                    }
                }
                if (_inspectionRois.Count > 0)
                {
                    SelectedInspectionRoi = _inspectionRois.FirstOrDefault();
                }
            }
            else
            {
                SelectedInspectionRoi = null;
            }

            UpdateSelectedRoiState();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            UpdateGlobalBadge();
        }

        private void InspectionRoisCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (InspectionRoiConfig roi in e.OldItems)
                {
                    roi.PropertyChanged -= InspectionRoiPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (InspectionRoiConfig roi in e.NewItems)
                {
                    roi.PropertyChanged += InspectionRoiPropertyChanged;
                }
            }

            if (_inspectionRois != null && (SelectedInspectionRoi == null || !_inspectionRois.Contains(SelectedInspectionRoi)))
            {
                SelectedInspectionRoi = _inspectionRois.FirstOrDefault();
            }

            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            UpdateGlobalBadge();
        }

        private void InspectionRoiPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InspectionRoiConfig.Enabled))
            {
                EvaluateAllRoisCommand.RaiseCanExecuteChanged();
                EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.LastResultOk) || e.PropertyName == nameof(InspectionRoiConfig.Enabled))
            {
                UpdateGlobalBadge();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.DatasetPath) && sender is InspectionRoiConfig roiChanged)
            {
                _roiDatasetCache.Remove(roiChanged);
                if (!roiChanged.HasDatasetPath)
                {
                    roiChanged.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                    roiChanged.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                    roiChanged.DatasetReady = false;
                    roiChanged.DatasetOkCount = 0;
                    roiChanged.DatasetKoCount = 0;
                    roiChanged.DatasetStatus = "Select a dataset";
                    _ = RefreshDatasetPreviewsForRoiAsync(roiChanged);
                }
                else
                {
                    roiChanged.DatasetStatus = "Validating dataset...";
                    roiChanged.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                    roiChanged.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                    _ = RefreshDatasetPreviewsForRoiAsync(roiChanged);
                    _ = RefreshRoiDatasetStateAsync(roiChanged);
                }

                CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.DatasetOkCount)
                || e.PropertyName == nameof(InspectionRoiConfig.IsDatasetLoading))
            {
                CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            }

            if (ReferenceEquals(sender, SelectedInspectionRoi))
            {
                OnPropertyChanged(nameof(SelectedInspectionRoi));
            }
        }

        public DatasetSample? SelectedOkSample
        {
            get => _selectedOkSample;
            set
            {
                if (!Equals(value, _selectedOkSample))
                {
                    _selectedOkSample = value;
                    OnPropertyChanged();
                    RemoveSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }
        private DatasetSample? _selectedOkSample;

        public DatasetSample? SelectedNgSample
        {
            get => _selectedNgSample;
            set
            {
                if (!Equals(value, _selectedNgSample))
                {
                    _selectedNgSample = value;
                    OnPropertyChanged();
                    RemoveSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }
        private DatasetSample? _selectedNgSample;

        public AsyncCommand AddOkFromCurrentRoiCommand { get; }
        public AsyncCommand AddNgFromCurrentRoiCommand { get; }
        public AsyncCommand RemoveSelectedCommand { get; }
        public AsyncCommand OpenDatasetFolderCommand { get; }
        public AsyncCommand TrainFitCommand { get; }
        public AsyncCommand CalibrateCommand { get; }
        public AsyncCommand InferFromCurrentRoiCommand { get; }
        public AsyncCommand RefreshDatasetCommand { get; }
        public AsyncCommand RefreshHealthCommand { get; }
        public AsyncCommand BrowseDatasetCommand { get; }
        public AsyncCommand TrainSelectedRoiCommand { get; }
        public AsyncCommand CalibrateSelectedRoiCommand { get; }
        public AsyncCommand EvaluateSelectedRoiCommand { get; }
        public AsyncCommand EvaluateAllRoisCommand { get; }
        public AsyncCommand AddRoiToDatasetOkCommand { get; }
        public AsyncCommand AddRoiToDatasetNgCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    RaiseBusyChanged();
                }
            }
        }

        public string RoleId
        {
            get => _roleId;
            set
            {
                if (!string.Equals(_roleId, value, StringComparison.Ordinal))
                {
                    _roleId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RoiId
        {
            get => _roiId;
            set
            {
                if (!string.Equals(_roiId, value, StringComparison.Ordinal))
                {
                    _roiId = value;
                    OnPropertyChanged();
                }
            }
        }

        public double MmPerPx
        {
            get => _mmPerPx;
            set
            {
                if (Math.Abs(_mmPerPx - value) > 1e-6)
                {
                    _mmPerPx = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BackendBaseUrl
        {
            get => _backendBaseUrl;
            set
            {
                if (!string.Equals(_backendBaseUrl, value, StringComparison.Ordinal))
                {
                    _backendBaseUrl = value;
                    OnPropertyChanged();
                    _client.BaseUrl = value;
                }
            }
        }

        public string FitSummary
        {
            get => _fitSummary;
            private set
            {
                if (!string.Equals(_fitSummary, value, StringComparison.Ordinal))
                {
                    _fitSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CalibrationSummary
        {
            get => _calibrationSummary;
            private set
            {
                if (!string.Equals(_calibrationSummary, value, StringComparison.Ordinal))
                {
                    _calibrationSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public double? InferenceScore
        {
            get => _inferenceScore;
            private set
            {
                if (_inferenceScore != value)
                {
                    _inferenceScore = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                }
            }
        }

        public double? InferenceThreshold
        {
            get => _inferenceThreshold;
            private set
            {
                if (_inferenceThreshold != value)
                {
                    _inferenceThreshold = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                }
            }
        }

        public double LocalThreshold
        {
            get => _localThreshold;
            set
            {
                if (Math.Abs(_localThreshold - value) > 1e-6)
                {
                    _localThreshold = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                    if (_lastExport != null && _lastHeatmapBytes != null)
                    {
                        _ = _showHeatmapAsync(_lastExport, _lastHeatmapBytes, HeatmapOpacity);
                    }
                }
            }
        }

        public string InferenceSummary
        {
            get => _inferenceSummary;
            private set
            {
                if (!string.Equals(_inferenceSummary, value, StringComparison.Ordinal))
                {
                    _inferenceSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<Region> Regions { get; } = new();

        public double HeatmapOpacity
        {
            get => _heatmapOpacity;
            set
            {
                if (Math.Abs(_heatmapOpacity - value) > 1e-3)
                {
                    _heatmapOpacity = value;
                    OnPropertyChanged();
                    if (_lastExport != null && _lastHeatmapBytes != null)
                    {
                        _ = _showHeatmapAsync(_lastExport, _lastHeatmapBytes, HeatmapOpacity);
                    }
                }
            }
        }

        public string HealthSummary
        {
            get => _healthSummary;
            private set
            {
                if (!string.Equals(_healthSummary, value, StringComparison.Ordinal))
                {
                    _healthSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        private AsyncCommand CreateCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            return new AsyncCommand(async param =>
            {
                await RunExclusiveAsync(() => execute(param));
            }, canExecute ?? (_ => !IsBusy));
        }

        private bool CanCalibrateSelectedRoi()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return false;
            }

            if (!roi.HasDatasetPath || roi.IsDatasetLoading)
            {
                return false;
            }

            return roi.DatasetOkCount > 0;
        }

        private async Task RunExclusiveAsync(Func<Task> action)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RaiseBusyChanged()
        {
            AddOkFromCurrentRoiCommand.RaiseCanExecuteChanged();
            AddNgFromCurrentRoiCommand.RaiseCanExecuteChanged();
            RemoveSelectedCommand.RaiseCanExecuteChanged();
            OpenDatasetFolderCommand.RaiseCanExecuteChanged();
            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
            InferFromCurrentRoiCommand.RaiseCanExecuteChanged();
            RefreshDatasetCommand.RaiseCanExecuteChanged();
            RefreshHealthCommand.RaiseCanExecuteChanged();
            BrowseDatasetCommand.RaiseCanExecuteChanged();
            TrainSelectedRoiCommand.RaiseCanExecuteChanged();
            CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
        }

        private void UpdateSelectedRoiState()
        {
            BrowseDatasetCommand.RaiseCanExecuteChanged();
            TrainSelectedRoiCommand.RaiseCanExecuteChanged();
            CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
        }

        private async Task AddSampleAsync(bool isNg)
        {
            EnsureRoleRoi();
            _log($"[dataset] add {(isNg ? "NG" : "OK")} sample requested");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[dataset] export cancelled");
                return;
            }

            var source = _getSourceImagePath() ?? string.Empty;
            var sample = await _datasetManager.SaveSampleAsync(RoleId, RoiId, isNg, export.PngBytes, export.ShapeJson, MmPerPx, source, export.RoiImage.AngleDeg).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (isNg)
                {
                    NgSamples.Add(sample);
                }
                else
                {
                    OkSamples.Add(sample);
                }
            });

            _log($"[dataset] saved {(isNg ? "NG" : "OK")} sample -> {sample.ImagePath}");
            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
        }


        private async Task AddRoiToDatasetAsync(InspectionRoiConfig roi, bool isOk)
        {
            if (roi == null)
            {
                await ShowMessageAsync("No ROI selected.", "Dataset");
                return;
            }

            if (string.IsNullOrWhiteSpace(roi.DatasetPath))
            {
                await ShowMessageAsync($"Dataset path not set for '{roi.DisplayName}'.", "Dataset");
                return;
            }

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                await ShowMessageAsync("No image loaded.", "Dataset");
                return;
            }

            var cropped = ExtractRoiBitmapWithShapeMask(export);
            if (cropped == null)
            {
                await ShowMessageAsync($"Could not crop ROI '{roi.DisplayName}'.", "Dataset");
                return;
            }

            string labelDir = isOk ? "ok" : DetermineNegLabelDir(roi.DatasetPath!);
            string saveDir = Path.Combine(roi.DatasetPath!, labelDir);
            Directory.CreateDirectory(saveDir);
            string fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
            string fullPath = Path.Combine(saveDir, fileName);
            SavePng(fullPath, cropped);

            await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
            await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);

            await ShowTransientToastAsync($"{roi.DisplayName}: added to {(isOk ? "OK" : "NG")} dataset");
        }

        private async Task RemoveSelectedAsync()
        {
            var toRemove = new List<DatasetSample>();
            if (SelectedOkSample != null)
            {
                toRemove.Add(SelectedOkSample);
            }
            if (SelectedNgSample != null)
            {
                toRemove.Add(SelectedNgSample);
            }

            if (toRemove.Count == 0)
            {
                return;
            }

            foreach (var sample in toRemove)
            {
                _datasetManager.DeleteSample(sample);
                _log($"[dataset] removed sample {sample.ImagePath}");
            }

            await RefreshDatasetAsync().ConfigureAwait(false);
        }

        private async Task OpenDatasetFolderAsync()
        {
            EnsureRoleRoi();
            var dir = _datasetManager.GetRoleRoiDirectory(RoleId, RoiId);
            _datasetManager.EnsureRoleRoiDirectories(RoleId, RoiId);
            await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }).ConfigureAwait(false);
        }

        private async Task RefreshDatasetAsync()
        {
            EnsureRoleRoi();
            _log("[dataset] refreshing lists");
            var ok = await _datasetManager.LoadSamplesAsync(RoleId, RoiId, isNg: false).ConfigureAwait(false);
            var ng = await _datasetManager.LoadSamplesAsync(RoleId, RoiId, isNg: true).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                OkSamples.Clear();
                foreach (var sample in ok)
                    OkSamples.Add(sample);

                NgSamples.Clear();
                foreach (var sample in ng)
                    NgSamples.Add(sample);
            });

            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
        }

        private async Task TrainAsync()
        {
            EnsureRoleRoi();
            var images = OkSamples.Select(s => s.ImagePath).ToList();
            if (images.Count == 0)
            {
                FitSummary = "No OK samples";
                return;
            }

            _log($"[fit] sending {images.Count} samples to fit_ok");
            try
            {
                var result = await _client.FitOkAsync(RoleId, RoiId, MmPerPx, images).ConfigureAwait(false);
                FitSummary = $"Embeddings={result.n_embeddings} Coreset={result.coreset_size} TokenShape=[{string.Join(',', result.token_shape ?? Array.Empty<int>())}]";
                _log("[fit] completed " + FitSummary);
            }
            catch (HttpRequestException ex)
            {
                if (IsAlreadyTrainedError(ex))
                {
                    FitSummary = "Model already trained";
                    _log($"[fit] backend reported already trained: {ex.Message}");
                    return;
                }

                FitSummary = "Train failed";
                await ShowMessageAsync($"Training failed: {ex.Message}", caption: "Train error");
            }
        }

        private async Task CalibrateAsync()
        {
            EnsureRoleRoi();
            var ok = OkSamples.ToList();
            if (ok.Count == 0)
            {
                CalibrationSummary = "Need OK samples";
                return;
            }

            var okScores = new List<double>();
            var ngScores = new List<double>();

            _log($"[calibrate] evaluating {ok.Count} OK samples");
            foreach (var sample in ok)
            {
                var infer = await _client.InferAsync(RoleId, RoiId, MmPerPx, sample.ImagePath, sample.Metadata.shape_json).ConfigureAwait(false);
                okScores.Add(infer.score);
            }

            var ngList = NgSamples.ToList();
            if (ngList.Count > 0)
            {
                _log($"[calibrate] evaluating {ngList.Count} NG samples");
                foreach (var sample in ngList)
                {
                    var infer = await _client.InferAsync(RoleId, RoiId, MmPerPx, sample.ImagePath, sample.Metadata.shape_json).ConfigureAwait(false);
                    ngScores.Add(infer.score);
                }
            }

            var calib = await _client.CalibrateAsync(RoleId, RoiId, MmPerPx, okScores, ngScores.Count > 0 ? ngScores : null).ConfigureAwait(false);
            CalibrationSummary = $"Threshold={calib.threshold:0.###} OKµ={calib.ok_mean:0.###} NGµ={calib.ng_mean:0.###} Percentile={calib.score_percentile}";
            _log("[calibrate] " + CalibrationSummary);
        }

        private async Task InferCurrentAsync()
        {
            EnsureRoleRoi();
            _log("[infer] exporting current ROI");
            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[infer] export cancelled");
                return;
            }

            var inferFileName = $"roi_{DateTime.UtcNow:yyyyMMddHHmmssfff}.png";
            _log($"[infer] POST role={RoleId} roi={RoiId} bytes={export.PngBytes.Length} imgHash={export.ImageHash} cropHash={export.CropHash} rect=({export.CropRect.X},{export.CropRect.Y},{export.CropRect.Width},{export.CropRect.Height})");

            var result = await _client.InferAsync(RoleId, RoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson).ConfigureAwait(false);
            _lastExport = export;
            _lastInferResult = result;
            InferenceScore = result.score;
            InferenceThreshold = result.threshold;

            // ANTES:
            // LocalThreshold = result.threshold > 0 ? result.threshold : LocalThreshold;

            // AHORA (elige una de las dos):
            // Opción 1:
            if (result.threshold.HasValue && result.threshold.Value > 0)
            {
                LocalThreshold = result.threshold.Value;
            }
            // Opción 2 (C# 9+):
            // LocalThreshold = result.threshold is > 0 var t ? t : LocalThreshold;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Regions.Clear();
                if (result.regions != null)
                {
                    foreach (var region in result.regions)
                    {
                        Regions.Add(region);
                    }
                }
            });


            if (!string.IsNullOrWhiteSpace(result.heatmap_png_base64))
            {
                _lastHeatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                await _showHeatmapAsync(export, _lastHeatmapBytes, HeatmapOpacity).ConfigureAwait(false);
            }
            else
            {
                _lastHeatmapBytes = null;
                _clearHeatmap();
            }
        }

        private async Task RefreshHealthAsync()
        {
            var info = await _client.GetHealthAsync().ConfigureAwait(false);
            if (info == null)
            {
                HealthSummary = "Backend offline";
                return;
            }

            HealthSummary = $"{info.status ?? "ok"} — {info.device} — {info.model} ({info.version})";
        }

        private async Task BrowseDatasetAsync()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return;
            }

            var choice = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    "Choose Yes to browse a dataset folder (requires ok/ko).\nChoose No to load a CSV (filename,label).",
                    "Dataset source",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes));

            if (choice == MessageBoxResult.Cancel)
            {
                return;
            }

            if (choice == MessageBoxResult.Yes)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    using var dialog = new Forms.FolderBrowserDialog
                    {
                        Description = "Select dataset folder",
                        UseDescriptionForTitle = true,
                    };

                    if (!string.IsNullOrWhiteSpace(roi.DatasetPath) && Directory.Exists(roi.DatasetPath))
                    {
                        dialog.SelectedPath = roi.DatasetPath;
                    }

                    if (dialog.ShowDialog() == Forms.DialogResult.OK)
                    {
                        roi.DatasetPath = dialog.SelectedPath;
                    }
                });
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select dataset CSV",
                    Filter = "Dataset CSV (*.csv)|*.csv|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (!string.IsNullOrWhiteSpace(roi.DatasetPath) && File.Exists(roi.DatasetPath))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(roi.DatasetPath);
                    dialog.FileName = Path.GetFileName(roi.DatasetPath);
                }

                if (dialog.ShowDialog() == true)
                {
                    roi.DatasetPath = dialog.FileName;
                }
            });
        }



        private BitmapSource? ExtractRoiBitmapWithShapeMask(RoiExportResult export)
        {
            return LoadBitmapFromBytes(export.PngBytes);
        }

        private static BitmapSource? LoadBitmapFromBytes(byte[] data)

        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static string RoiFolderName(string displayName)
            => displayName.Replace(" ", "_");

        private static string DetermineNegLabelDir(string datasetRoot)
        {
            var ng = Path.Combine(datasetRoot, "ng");
            var ko = Path.Combine(datasetRoot, "ko");
            if (Directory.Exists(ng)) return "ng";
            if (Directory.Exists(ko)) return "ko";
            return "ng";
        }

        private static IEnumerable<string> EnumerateDatasetFiles(string rootDir, string labelDir, string roiDisplayName, int take = 24)
        {
            var roiFolder = RoiFolderName(roiDisplayName);
            var subDir = Path.Combine(rootDir, labelDir, roiFolder);
            string targetDir;
            if (Directory.Exists(subDir))
            {
                targetDir = subDir;
            }
            else
            {
                targetDir = Path.Combine(rootDir, labelDir);
            }

            if (!Directory.Exists(targetDir))
            {
                Debug.WriteLine($"[thumbs] no dir: {targetDir}");
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(targetDir, "*.png", SearchOption.TopDirectoryOnly)
                                         .OrderByDescending(File.GetCreationTimeUtc)
                                         .Take(take))
            {
                yield return file;
            }
        }

        private static BitmapSource? LoadBitmapSource(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame == null)
            {
                return null;
            }

            frame.Freeze();
            return frame;
        }

        public async Task RefreshDatasetPreviewsForRoiAsync(InspectionRoiConfig roi, int take = 24)
        {
            if (roi == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(roi.DatasetPath))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                    roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                });
                return;
            }

            var okItems = new ObservableCollection<DatasetPreviewItem>();
            foreach (var path in EnumerateDatasetFiles(roi.DatasetPath!, "ok", roi.DisplayName, take))
            {
                var bmp = LoadBitmapSource(path);
                if (bmp != null)
                {
                    okItems.Add(new DatasetPreviewItem { Path = path, Thumbnail = bmp });
                }
            }

            var negLabel = DetermineNegLabelDir(roi.DatasetPath!);
            var ngItems = new ObservableCollection<DatasetPreviewItem>();
            foreach (var path in EnumerateDatasetFiles(roi.DatasetPath!, negLabel, roi.DisplayName, take))
            {
                var bmp = LoadBitmapSource(path);
                if (bmp != null)
                {
                    ngItems.Add(new DatasetPreviewItem { Path = path, Thumbnail = bmp });
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                roi.OkPreview = okItems;
                roi.NgPreview = ngItems;
                Debug.WriteLine($"[thumbs] {roi.DisplayName} ok={okItems.Count} {negLabel}={ngItems.Count}");
            });
        }

        private static void SavePng(string path, BitmapSource bmp)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        private static async Task ShowTransientToastAsync(string message)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, "Dataset", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private async Task<RoiDatasetAnalysis> EnsureDatasetAnalysisAsync(InspectionRoiConfig roi, bool forceRefresh = false)
        {
            if (!forceRefresh && _roiDatasetCache.TryGetValue(roi, out var cached))
            {
                if (string.Equals(cached.DatasetPath, roi.DatasetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return cached;
                }
            }

            return await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
        }

        private async Task<RoiDatasetAnalysis> RefreshRoiDatasetStateAsync(InspectionRoiConfig roi)
        {
            var datasetPath = roi.DatasetPath;
            roi.IsDatasetLoading = true;
            roi.DatasetReady = false;
            roi.DatasetStatus = roi.HasDatasetPath ? "Validating dataset..." : "Select a dataset";

            var analysis = await Task.Run(() => AnalyzeDatasetPath(datasetPath)).ConfigureAwait(false);
            _roiDatasetCache[roi] = analysis;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                roi.IsDatasetLoading = false;
                roi.DatasetOkCount = analysis.OkCount;
                roi.DatasetKoCount = analysis.KoCount;
                roi.DatasetReady = analysis.IsValid;
                roi.DatasetStatus = analysis.StatusMessage;
            });

            await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);

            return analysis;
        }

        private static RoiDatasetAnalysis AnalyzeDatasetPath(string? datasetPath)
        {
            if (string.IsNullOrWhiteSpace(datasetPath))
            {
                return RoiDatasetAnalysis.Empty("Select a dataset");
            }

            if (Directory.Exists(datasetPath))
            {
                return AnalyzeFolderDataset(datasetPath);
            }

            if (File.Exists(datasetPath))
            {
                if (string.Equals(Path.GetExtension(datasetPath), ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    return AnalyzeCsvDataset(datasetPath);
                }

                return RoiDatasetAnalysis.Empty("Dataset file must be a CSV with filename,label columns");
            }

            return RoiDatasetAnalysis.Empty("Dataset path not found");
        }

        private static RoiDatasetAnalysis AnalyzeFolderDataset(string folderPath)
        {
            try
            {
                var okDir = Path.Combine(folderPath, "ok");
                if (!Directory.Exists(okDir))
                {
                    return RoiDatasetAnalysis.Empty("Folder must contain /ok and /ko subfolders");
                }

                var negLabel = DetermineNegLabelDir(folderPath);
                var negDir = Path.Combine(folderPath, negLabel);
                if (!Directory.Exists(negDir))
                {
                    return RoiDatasetAnalysis.Empty("Folder must contain /ok and /ko subfolders");
                }

                var okFiles = EnumerateImages(okDir);
                var koFiles = EnumerateImages(negDir);

                var entries = okFiles.Select(path => new DatasetEntry(path, true))
                    .Concat(koFiles.Select(path => new DatasetEntry(path, false)))
                    .ToList();

                var preview = new List<(string Path, bool IsOk)>();
                preview.AddRange(okFiles.Take(3).Select(p => (p, true)));
                preview.AddRange(koFiles.Take(3).Select(p => (p, false)));

                bool ready = okFiles.Count > 0 && koFiles.Count > 0;
                string status = ready ? (negLabel == "ng" ? "Dataset Ready ✅ (using /ng)" : "Dataset Ready ✅")
                    : (okFiles.Count == 0 ? "Dataset missing OK samples" : "Dataset missing KO samples");

                return new RoiDatasetAnalysis(folderPath, entries, okFiles.Count, koFiles.Count, ready, status, preview);
            }
            catch (Exception ex)
            {
                return RoiDatasetAnalysis.Empty($"Dataset error: {ex.Message}");
            }
        }

        private static RoiDatasetAnalysis AnalyzeCsvDataset(string csvPath)
        {
            try
            {
                var entries = new List<DatasetEntry>();
                var preview = new List<(string Path, bool IsOk)>();
                int okCount = 0, koCount = 0;
                int okPreview = 0, koPreview = 0;

                using var reader = new StreamReader(csvPath);
                string? header = reader.ReadLine();
                if (header == null)
                {
                    return RoiDatasetAnalysis.Empty("CSV is empty");
                }

                var headers = header.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None)
                    .Select(h => h.Trim().ToLowerInvariant())
                    .ToArray();

                int filenameIndex = Array.FindIndex(headers, h => h is "filename" or "file" or "path");
                int labelIndex = Array.FindIndex(headers, h => h is "label" or "class" or "target");

                if (filenameIndex < 0 || labelIndex < 0)
                {
                    return RoiDatasetAnalysis.Empty("CSV must contain 'filename' and 'label' columns");
                }

                string? line;
                var baseDir = Path.GetDirectoryName(csvPath) ?? string.Empty;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parts = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None);
                    if (parts.Length <= Math.Max(filenameIndex, labelIndex))
                    {
                        continue;
                    }

                    var rawPath = parts[filenameIndex].Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    var resolvedPath = Path.IsPathRooted(rawPath)
                        ? rawPath
                        : Path.GetFullPath(Path.Combine(baseDir, rawPath));

                    if (!File.Exists(resolvedPath))
                    {
                        continue;
                    }

                    var labelRaw = parts[labelIndex].Trim().Trim('"');
                    var parsedLabel = TryParseDatasetLabel(labelRaw);
                    if (parsedLabel == null)
                    {
                        continue;
                    }

                    bool isOk = parsedLabel.Value;
                    entries.Add(new DatasetEntry(resolvedPath, isOk));
                    if (isOk)
                    {
                        okCount++;
                        if (okPreview < 3)
                        {
                            preview.Add((resolvedPath, true));
                            okPreview++;
                        }
                    }
                    else
                    {
                        koCount++;
                        if (koPreview < 3)
                        {
                            preview.Add((resolvedPath, false));
                            koPreview++;
                        }
                    }
                }

                bool ready = okCount > 0 && koCount > 0;
                string status = ready ? "Dataset Ready ✅" : "CSV needs both OK and KO samples";
                return new RoiDatasetAnalysis(csvPath, entries, okCount, koCount, ready, status, preview);
            }
            catch (Exception ex)
            {
                return RoiDatasetAnalysis.Empty($"CSV error: {ex.Message}");
            }
        }

        private static bool? TryParseDatasetLabel(string labelRaw)
        {
            if (string.IsNullOrWhiteSpace(labelRaw))
            {
                return null;
            }

            var normalized = labelRaw.Trim().Trim('"').ToLowerInvariant();
            return normalized switch
            {
                "ok" or "good" or "pass" or "1" or "true" => true,
                "ko" or "ng" or "nok" or "fail" or "0" or "false" => false,
                _ => null
            };
        }

        private static List<string> EnumerateImages(string directory)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(directory))
            {
                return new List<string>();
            }

            string[] patterns = { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff" };
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
                {
                    set.Add(file);
                }
            }

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private async Task<bool> TrainSelectedRoiAsync(CancellationToken ct = default)
        {
            EnsureRoleRoi();
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return false;
            }

            if (!roi.HasDatasetPath)
            {
                await ShowMessageAsync("Select a dataset path before training.");
                return false;
            }

            var analysis = await EnsureDatasetAnalysisAsync(roi).ConfigureAwait(false);
            if (analysis.OkCount == 0)
            {
                var message = string.IsNullOrWhiteSpace(analysis.StatusMessage)
                    ? "Dataset has no OK samples for training."
                    : analysis.StatusMessage;
                await ShowMessageAsync(message, caption: "Dataset not ready");
                return false;
            }

            if (!analysis.IsValid && !string.IsNullOrWhiteSpace(analysis.StatusMessage))
            {
                _log($"[train] dataset warning: {analysis.StatusMessage}");
            }

            var okImages = analysis.Entries.Where(e => e.IsOk).Select(e => e.Path).Where(File.Exists).ToList();
            if (okImages.Count == 0)
            {
                await ShowMessageAsync($"No OK images found in dataset for ROI '{roi.Name}'.");
                return false;
            }

            if (!await EnsureFitEndpointAsync().ConfigureAwait(false))
            {
                await ShowMessageAsync("Backend /fit_ok endpoint is not available.");
                return false;
            }

            try
            {
                var result = await _client.FitOkAsync(RoleId, roi.ModelKey, MmPerPx, okImages, roi.TrainMemoryFit, ct).ConfigureAwait(false);
                FitSummary = $"Embeddings={result.n_embeddings} Coreset={result.coreset_size} TokenShape=[{string.Join(',', result.token_shape ?? Array.Empty<int>())}]";
            }
            catch (HttpRequestException ex)
            {
                if (IsAlreadyTrainedError(ex))
                {
                    FitSummary = "Model already trained";
                    _log($"[train] backend reported already trained: {ex.Message}");
                    return true;
                }

                FitSummary = "Train failed";
                await ShowMessageAsync($"Training failed: {ex.Message}", caption: "Train error");
                return false;
            }

            return true;
        }

        private static bool IsAlreadyTrainedError(HttpRequestException ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return false;
            }

            var message = ex.Message;
            return message.Contains("already trained", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("ya entrenad", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("memoria existente", StringComparison.OrdinalIgnoreCase);
        }

        private async Task CalibrateSelectedRoiAsync(CancellationToken ct = default)
        {
            EnsureRoleRoi();
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                await ShowMessageAsync("Select an Inspection ROI first.", "Calibrate");
                return;
            }

            if (!await EnsureTrainedBeforeCalibrateAsync(roi, ct).ConfigureAwait(false))
            {
                return;
            }

            var analysis = await EnsureDatasetAnalysisAsync(roi).ConfigureAwait(false);
            if (!analysis.IsValid && analysis.OkCount == 0)
            {
                await ShowMessageAsync(analysis.StatusMessage, caption: "Dataset not ready");
                return;
            }

            var okEntries = analysis.Entries.Where(e => e.IsOk).ToList();
            if (okEntries.Count == 0)
            {
                await ShowMessageAsync("Dataset has no OK samples for calibration.", "Calibrate");
                return;
            }

            var koEntries = analysis.Entries.Where(e => !e.IsOk).ToList();

            var okScores = new List<double>();
            foreach (var entry in okEntries)
            {
                var score = await InferScoreForCalibrationAsync(roi, entry.Path, ct).ConfigureAwait(false);
                if (!score.HasValue)
                {
                    return;
                }

                okScores.Add(score.Value);
            }

            var ngScores = new List<double>();
            foreach (var entry in koEntries)
            {
                var score = await InferScoreForCalibrationAsync(roi, entry.Path, ct).ConfigureAwait(false);
                if (!score.HasValue)
                {
                    return;
                }

                ngScores.Add(score.Value);
            }

            double? threshold = null;
            if (await EnsureCalibrateEndpointAsync().ConfigureAwait(false))
            {
                try
                {
                    var calib = await _client.CalibrateAsync(RoleId, roi.ModelKey, MmPerPx, okScores, ngScores.Count > 0 ? ngScores : null, ct: ct).ConfigureAwait(false);
                    threshold = calib.threshold;
                    CalibrationSummary = $"Threshold={calib.threshold:0.###} OKµ={calib.ok_mean:0.###} NGµ={calib.ng_mean:0.###} Percentile={calib.score_percentile:0.###}";
                }
                catch (HttpRequestException ex)
                {
                    _log("[calibrate] backend error: " + ex.Message);
                }
            }

            if (threshold == null)
            {
                threshold = ComputeYoudenThreshold(okScores, ngScores, roi.ThresholdDefault);
                CalibrationSummary = $"Threshold={threshold:0.###} (local)";
            }

            roi.CalibratedThreshold = threshold;
            OnPropertyChanged(nameof(SelectedInspectionRoi));
            UpdateGlobalBadge();
        }

        private async Task<bool> EnsureTrainedBeforeCalibrateAsync(InspectionRoiConfig? roi, CancellationToken ct)
        {
            if (roi == null)
            {
                await ShowMessageAsync("No ROI selected.", "Calibrate");
                return false;
            }

            if (string.IsNullOrWhiteSpace(roi.DatasetPath))
            {
                await ShowMessageAsync($"Dataset path is empty for ROI '{roi.Name}'. Select a dataset first.", "Calibrate");
                return false;
            }

            try
            {
                return await TrainSelectedRoiAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Training failed before calibration.\n{ex.Message}", "Calibrate");
                return false;
            }
        }

        private async Task<double?> InferScoreForCalibrationAsync(InspectionRoiConfig roi, string samplePath, CancellationToken ct)
        {
            bool retried = false;
            while (true)
            {
                try
                {
                    var infer = await _client.InferAsync(RoleId, roi.ModelKey, MmPerPx, samplePath, null, ct).ConfigureAwait(false);
                    return infer.score;
                }
                catch (HttpRequestException ex) when (!retried && ex.Message.Contains("Memoria no encontrada", StringComparison.OrdinalIgnoreCase))
                {
                    retried = true;
                    var retrained = await TrainSelectedRoiAsync(ct).ConfigureAwait(false);
                    if (!retrained)
                    {
                        await ShowMessageAsync("Training failed while retrying calibration inference.", "Calibrate");
                        return null;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
                {
                    await ShowMessageAsync($"Inference failed for '{Path.GetFileName(samplePath)}'.\n{ex.Message}", "Calibrate");
                    return null;
                }
            }
        }

        private async Task EvaluateSelectedRoiAsync()
        {
            await EvaluateRoiAsync(SelectedInspectionRoi, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task EvaluateAllRoisAsync()
        {
            if (_inspectionRois == null)
            {
                return;
            }

            foreach (var roi in _inspectionRois.Where(r => r.Enabled))
            {
                await EvaluateRoiAsync(roi, CancellationToken.None).ConfigureAwait(false);
            }

            UpdateGlobalBadge();
        }

        private async Task EvaluateRoiAsync(InspectionRoiConfig? roi, CancellationToken ct)
        {
            EnsureRoleRoi();
            if (roi == null || !roi.Enabled)
            {
                return;
            }

            if (!ValidateInferenceInputs(roi, out var validationError))
            {
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    await ShowMessageAsync(validationError, caption: "Cannot evaluate");
                }
                return;
            }

            _log($"[eval] export ROI for {roi.Name}");
            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[eval] export cancelled");
                return;
            }

            if (export.PngBytes == null || export.PngBytes.Length == 0)
            {
                _log("[eval] export produced empty payload");
                await ShowMessageAsync("ROI crop is empty. Save the ROI layout and try again.", caption: "Inference aborted");
                return;
            }

            var inferFileName = $"roi_{DateTime.UtcNow:yyyyMMddHHmmssfff}.png";
            try
            {
                var result = await _client.InferAsync(RoleId, roi.ModelKey, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson, ct).ConfigureAwait(false);
                _lastExport = export;
                _lastInferResult = result;
                InferenceScore = result.score;
                InferenceThreshold = result.threshold;

                if (result.threshold is double thresholdValue && thresholdValue > 0)
                {
                    LocalThreshold = thresholdValue;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Regions.Clear();
                    if (result.regions != null)
                    {
                        foreach (var region in result.regions)
                        {
                            Regions.Add(region);
                        }
                    }
                });

                if (!string.IsNullOrWhiteSpace(result.heatmap_png_base64))
                {
                    _lastHeatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                    await _showHeatmapAsync(export, _lastHeatmapBytes, HeatmapOpacity).ConfigureAwait(false);
                }
                else
                {
                    _lastHeatmapBytes = null;
                    _clearHeatmap();
                }

                roi.LastScore = result.score;
                var decisionThreshold = roi.CalibratedThreshold ?? roi.ThresholdDefault;
                roi.LastResultOk = result.score >= decisionThreshold;
                roi.LastEvaluatedAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                UpdateGlobalBadge();
            }
            catch (HttpRequestException ex)
            {
                var message = ExtractBackendError(ex);
                _lastExport = export;
                _lastInferResult = null;
                _lastHeatmapBytes = null;
                InferenceScore = null;
                InferenceThreshold = null;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Regions.Clear();
                });
                _clearHeatmap();
                roi.LastScore = null;
                roi.LastResultOk = null;
                roi.LastEvaluatedAt = DateTime.UtcNow;
                InferenceSummary = $"Inference failed: {message}";
                await ShowMessageAsync(message, caption: "Inference error");
                UpdateGlobalBadge();
            }
        }

        private bool ValidateInferenceInputs(InspectionRoiConfig roi, out string? message)
        {
            if (_inspectionRois == null || !_inspectionRois.Contains(roi))
            {
                message = "Save the ROI layout before evaluating.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(roi.ModelKey))
            {
                message = "ModelKey is required for inference.";
                return false;
            }

            var sourceImage = _getSourceImagePath();
            if (string.IsNullOrWhiteSpace(sourceImage) || !File.Exists(sourceImage))
            {
                message = "Load an inspection image before evaluating.";
                return false;
            }

            message = null;
            return true;
        }

        private static string ExtractBackendError(HttpRequestException ex)
        {
            var message = ex.Message;
            int colonIndex = message.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < message.Length - 1)
            {
                var tail = message[(colonIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    return tail;
                }
            }

            return message;
        }

        private void UpdateInferenceSummary()
        {
            if (InferenceScore == null)
            {
                InferenceSummary = string.Empty;
                return;
            }

            double thr = LocalThreshold > 0 ? LocalThreshold : (InferenceThreshold ?? 0);
            var sb = new StringBuilder();
            sb.AppendFormat("Score={0:0.###}", InferenceScore.Value);
            if (InferenceThreshold.HasValue)
            {
                sb.AppendFormat(" BackendThr={0:0.###}", InferenceThreshold.Value);
            }
            if (thr > 0)
            {
                sb.AppendFormat(" LocalThr={0:0.###}", thr);
                sb.Append(InferenceScore.Value >= thr ? " → NG" : " → OK");
            }
            InferenceSummary = sb.ToString();
        }

        private void EnsureRoleRoi()
        {
            if (string.IsNullOrWhiteSpace(RoleId))
                RoleId = "DefaultRole";
            if (string.IsNullOrWhiteSpace(RoiId))
                RoiId = "DefaultRoi";
            _datasetManager.EnsureRoleRoiDirectories(RoleId, RoiId);
        }

        private async Task<bool> EnsureFitEndpointAsync()
        {
            if (_hasFitEndpoint.HasValue)
            {
                return _hasFitEndpoint.Value;
            }

            _hasFitEndpoint = await _client.SupportsEndpointAsync("fit_ok").ConfigureAwait(false);
            return _hasFitEndpoint.Value;
        }

        private async Task<bool> EnsureCalibrateEndpointAsync()
        {
            if (_hasCalibrateEndpoint.HasValue)
            {
                return _hasCalibrateEndpoint.Value;
            }

            _hasCalibrateEndpoint = await _client.SupportsEndpointAsync("calibrate_ng").ConfigureAwait(false);
            return _hasCalibrateEndpoint.Value;
        }

        private void UpdateGlobalBadge()
        {
            try
            {
                var state = CalcGlobalDiskOk();
                if (Application.Current?.Dispatcher == null)
                {
                    _updateGlobalBadge(state);
                    return;
                }

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    _updateGlobalBadge(state);
                }
                else
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => _updateGlobalBadge(state)));
                }
            }
            catch
            {
                // ignore badge errors
            }
        }

        private bool? CalcGlobalDiskOk()
        {
            if (_inspectionRois == null)
            {
                return null;
            }

            var enabled = _inspectionRois.Where(r => r.Enabled).ToList();
            if (enabled.Count == 0)
            {
                return null;
            }

            if (enabled.Any(r => r.LastResultOk == false))
            {
                return false;
            }

            if (enabled.All(r => r.LastResultOk == true))
            {
                return true;
            }

            return null;
        }

        private static async Task ShowMessageAsync(string message, string caption = "BrakeDiscInspector")
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private static double ComputeYoudenThreshold(IReadOnlyCollection<double> okScores, IReadOnlyCollection<double> ngScores, double defaultValue)
        {
            if (okScores.Count == 0)
            {
                return defaultValue;
            }

            if (ngScores.Count == 0)
            {
                return okScores.Average();
            }

            var all = okScores.Select(s => (Score: s, IsOk: true))
                .Concat(ngScores.Select(s => (Score: s, IsOk: false)))
                .OrderBy(pair => pair.Score)
                .ToArray();

            int totalOk = okScores.Count;
            int totalNg = ngScores.Count;
            int okAbove = totalOk;
            int ngAbove = totalNg;

            double bestJ = double.NegativeInfinity;
            double bestThr = defaultValue;

            for (int i = 0; i < all.Length; i++)
            {
                var current = all[i];
                if (current.IsOk)
                {
                    okAbove--;
                }
                else
                {
                    ngAbove--;
                }

                double thr = current.Score;
                double tpr = totalOk > 0 ? (double)okAbove / totalOk : 0;
                double fpr = totalNg > 0 ? (double)ngAbove / totalNg : 0;
                double j = tpr - fpr;
                if (j > bestJ)
                {
                    bestJ = j;
                    bestThr = thr;
                }
            }

            return bestThr;
        }

        private sealed record RoiDatasetAnalysis(
            string DatasetPath,
            List<DatasetEntry> Entries,
            int OkCount,
            int KoCount,
            bool IsValid,
            string StatusMessage,
            List<(string Path, bool IsOk)> PreviewItems)
        {
            public static RoiDatasetAnalysis Empty(string status)
                => new(string.Empty, new List<DatasetEntry>(), 0, 0, false, status, new List<(string, bool)>());
        }

        private sealed record DatasetEntry(string Path, bool IsOk);

        private bool HasAnyEnabledInspectionRoi()
        {
            return _inspectionRois != null && _inspectionRois.Any(r => r.Enabled);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
