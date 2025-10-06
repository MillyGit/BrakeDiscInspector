// Dialogs
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using BrakeDiscInspector_GUI_ROI.Workflow;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
// WPF media & shapes
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
// OpenCV alias
using Cv = OpenCvSharp;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WBrush = System.Windows.Media.Brush;
using WBrushes = System.Windows.Media.Brushes;
using WColor = System.Windows.Media.Color;
using WEllipse = System.Windows.Shapes.Ellipse;
using WLine = System.Windows.Shapes.Line;
// Aliases WPF
using WPoint = System.Windows.Point;
using WRect = System.Windows.Rect;
using WRectShape = System.Windows.Shapes.Rectangle;
using Path = System.IO.Path;
using WSize = System.Windows.Size;

namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow : System.Windows.Window
    {
        private enum MasterState { DrawM1_Pattern, DrawM1_Search, DrawM2_Pattern, DrawM2_Search, DrawInspection, Ready }
        private enum RoiCorner { TopLeft, TopRight, BottomRight, BottomLeft }
        private MasterState _state = MasterState.DrawM1_Pattern;

        private PresetFile _preset = new();
        private MasterLayout _layout = new();     // inicio en blanco

        private RoiModel? _tmpBuffer;
        private string _currentImagePath = "";
        private string _currentImagePathWin = "";
        private string _currentImagePathBackend = "";
        private BitmapImage? _imgSourceBI;
        private int _imgW, _imgH;

        private WorkflowViewModel? _workflowViewModel;
        private System.Windows.Controls.Image? _heatmapOverlayImage;
        private BitmapSource? _heatmapBitmap;
        private RoiModel? _heatmapRoiImage;
        private double _heatmapOverlayOpacity = 0.6;

        private Shape? _previewShape;
        private bool _isDrawing;
        private WPoint _p0;
        private RoiShape _currentShape = RoiShape.Rectangle;

        private DispatcherTimer? _trainTimer;

        // ==== Drag de ROI (mover) ====
        private Shape? _dragShape;
        private System.Windows.Point _dragStart;
        private double _dragOrigX, _dragOrigY;

        private readonly Dictionary<RoiModel, DragLogState> _dragLogStates = new();
        private const double DragLogMovementThreshold = 5.0; // px (canvas)
        private const double DragLogAngleThreshold = 1.0;    // grados

        private const double AnnulusLogThreshold = 0.5;
        private double _lastLoggedAnnulusOuterRadius = double.NaN;
        private double _lastLoggedAnnulusInnerProposed = double.NaN;
        private double _lastLoggedAnnulusInnerFinal = double.NaN;
        private bool _annulusResetLogged;
        private static readonly TimeSpan AnnulusLogMinInterval = TimeSpan.FromMilliseconds(120);
        private DateTime _lastAnnulusOuterLogTimestamp = DateTime.MinValue;
        private DateTime _lastAnnulusInnerLogTimestamp = DateTime.MinValue;

        // Cache de la última sincronización del overlay
        private double _canvasLeftPx = 0;
        private double _canvasTopPx = 0;
        private double _canvasWpx = 0;
        private double _canvasHpx = 0;
        private double _sx = 1.0;   // escala imagen->canvas en X
        private double _sy = 1.0;   // escala imagen->canvas en Y


        // === File Logger ===
        private readonly object _fileLogLock = new object();
        private readonly string _fileLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "brakedisc_localmatcher.log");
        private readonly Queue<int> _logLineLengths = new();
        private int _logCharCount;
        private const int MaxLogCharacters = 60000;
        private const int TrimmedLogCharacters = 50000;
        // Si tu overlay se llama distinto, ajusta esta propiedad (o referencia directa en los métodos).
        // Por ejemplo, si en XAML tienes <Canvas x:Name="Overlay"> usa ese nombre aquí.
        private Canvas OverlayCanvas => CanvasROI;

        private const string ANALYSIS_TAG = "analysis-mark";
        // === Helpers de overlay ===
        private const double LabelOffsetX = 10;   // desplazamiento a la derecha de la cruz
        private const double LabelOffsetY = -20;  // desplazamiento hacia arriba de la cruz

        private ROI CurrentRoi = new ROI
        {
            X = 200,
            Y = 150,
            CX = 200,
            CY = 150,
            Width = 100,
            Height = 80,
            AngleDeg = 0,
            Legend = string.Empty,
            Shape = RoiShape.Rectangle,
            R = 50,
            RInner = 0
        };
        private Mat? bgrFrame; // tu frame actual
        private bool UseAnnulus = false;

        private readonly Dictionary<string, Shape> _roiShapesById = new();

        private bool _syncScheduled;
        private int _syncRetryCount;
        private const int MaxSyncRetries = 3;
        // overlay diferido
        private bool _overlayNeedsRedraw;
        private bool _analysisViewActive;
        public MainWindow()
        {
            InitializeComponent();

            // (si no está ya) vincular overlay a la Image y recalcular en cambios de tamaño
            RoiOverlay.BindToImage(ImgMain);
            ImgMain.SizeChanged += (_, __) => RoiOverlay.InvalidateOverlay();
            SizeChanged += (_, __) => RoiOverlay.InvalidateOverlay();

            // === NUEVO: sincronizar CanvasROI con la misma transform imagen (scale+offset) ===
            RoiOverlay.OverlayTransformChanged += (_, __) =>
            {
                var tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(RoiOverlay.Scale, RoiOverlay.Scale));
                tg.Children.Add(new TranslateTransform(RoiOverlay.OffsetX, RoiOverlay.OffsetY));
                CanvasROI.RenderTransform = tg;
                CanvasROI.RenderTransformOrigin = new Point(0, 0);
            };

            // Fuerza una primera actualización al iniciar
            RoiOverlay.InvalidateOverlay();

            _preset = PresetManager.LoadOrDefault(_preset);

            InitUI();
            InitTrainPollingTimer();
            HookCanvasInput();
            InitWorkflow();

            ImgMain.SizeChanged += ImgMain_SizeChanged;
            this.SizeChanged += MainWindow_SizeChanged;
            this.Loaded += MainWindow_Loaded;
        }

        private void InitWorkflow()
        {
            try
            {
                var datasetRoot = Path.Combine(AppContext.BaseDirectory, "datasets");
                Directory.CreateDirectory(datasetRoot);

                var backendClient = new Workflow.BackendClient();
                if (!string.IsNullOrWhiteSpace(BackendAPI.BaseUrl))
                {
                    backendClient.BaseUrl = BackendAPI.BaseUrl;
                }

                var datasetManager = new DatasetManager(datasetRoot);
                _workflowViewModel = new WorkflowViewModel(
                    backendClient,
                    datasetManager,
                    ExportCurrentRoiCanonicalAsync,
                    () => _currentImagePathWin,
                    AppendLog,
                    ShowHeatmapOverlayAsync,
                    ClearHeatmapOverlay);

                if (WorkflowHost != null)
                {
                    WorkflowHost.DataContext = _workflowViewModel;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _workflowViewModel?.RefreshDatasetCommand.Execute(null);
                    _workflowViewModel?.RefreshHealthCommand.Execute(null);
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                AppendLog("[workflow] init error: " + ex.Message);
            }
        }

        private void InitUI()
        {
            ComboFeature.SelectedIndex = 0;
            ComboMasterRoiRole.ItemsSource = new[] { "ROI Master 1", "ROI Inspección Master 1" };
            ComboMasterRoiRole.SelectedIndex = 0;


            ComboMasterRoiShape.Items.Clear();
            ComboMasterRoiShape.Items.Add("Rectángulo");
            ComboMasterRoiShape.Items.Add("Círculo");
            ComboMasterRoiShape.SelectedIndex = 0;


            ComboM2Shape.SelectedIndex = 0;
            ComboInspShape.SelectedIndex = 0;


            ComboM2Role.ItemsSource = new[] { "ROI Master 2", "ROI Inspección Master 2" };
            ComboM2Role.SelectedIndex = 0;


            UpdateWizardState();
            ApplyPresetToUI(_preset);
        }

        private void UpdateWizardState()
        {
            bool m1Ready = _layout.Master1Pattern != null && _layout.Master1Search != null;
            bool m2Ready = _layout.Master2Pattern != null && _layout.Master2Search != null;
            bool mastersReady = m1Ready && m2Ready;

            TxtMasterHints.Text = _state switch
            {
                MasterState.DrawM1_Pattern => "1) Dibuja el ROI del patrón Master 1. 2) Cambia a 'ROI Inspección Master 1' para delimitar la zona de búsqueda. Usa rectángulo o círculo.",
                MasterState.DrawM1_Search => "Dibuja la zona de búsqueda para Master 1 y pulsa Guardar.",
                MasterState.DrawM2_Pattern => "Dibuja el ROI del patrón Master 2.",
                MasterState.DrawM2_Search => "Dibuja la zona de búsqueda para Master 2 y pulsa Guardar.",
                MasterState.DrawInspection => "Dibuja el ROI de Inspección (rect/círc/annulus) y pulsa Guardar.",
                MasterState.Ready => "Pulsa 'Analizar Master' para localizar centros y reubicar el ROI de Inspección.",
                _ => ""
            };

            // Habilitación de tabs por etapas
            TabMaster1.IsEnabled = true;
            TabMaster2.IsEnabled = m1Ready;           // puedes definir M2 cuando M1 está completo
            TabInspection.IsEnabled = mastersReady;     // puedes definir la inspección tras completar M1 y M2
            TabAnalyze.IsEnabled = mastersReady;     // ahora Análisis disponible con M1+M2 (aunque no haya inspección)

            // Selección de tab acorde a estado
            if (_state == MasterState.DrawM1_Pattern || _state == MasterState.DrawM1_Search)
                Tabs.SelectedItem = TabMaster1;
            else if (_state == MasterState.DrawM2_Pattern || _state == MasterState.DrawM2_Search)
                Tabs.SelectedItem = TabMaster2;
            else if (_state == MasterState.DrawInspection)
                Tabs.SelectedItem = TabInspection;
            else
                Tabs.SelectedItem = TabAnalyze;

            if (_analysisViewActive && _state != MasterState.Ready)
            {
                ResetAnalysisMarks();
            }

            // Botón "Analizar Master" disponible en cuanto M1+M2 estén definidos
            BtnAnalyzeMaster.IsEnabled = mastersReady;
        }

        private RoiModel? GetCurrentStatePersistedRoi()
        {
            return _state switch
            {
                MasterState.DrawM1_Pattern => _layout.Master1Pattern,
                MasterState.DrawM1_Search => _layout.Master1Search,
                MasterState.DrawM2_Pattern => _layout.Master2Pattern,
                MasterState.DrawM2_Search => _layout.Master2Search,
                MasterState.DrawInspection or MasterState.Ready => _layout.Inspection,
                _ => null
            };
        }

        private RoiRole? GetCurrentStateRole()
        {
            return _state switch
            {
                MasterState.DrawM1_Pattern => RoiRole.Master1Pattern,
                MasterState.DrawM1_Search => RoiRole.Master1Search,
                MasterState.DrawM2_Pattern => RoiRole.Master2Pattern,
                MasterState.DrawM2_Search => RoiRole.Master2Search,
                MasterState.DrawInspection or MasterState.Ready => RoiRole.Inspection,
                _ => null
            };
        }

        private bool ShouldEnableRoiEditing(RoiRole role)
        {
            if (role == RoiRole.Inspection)
            {
                return _state == MasterState.DrawInspection || _state == MasterState.Ready;
            }

            var currentRole = GetCurrentStateRole();
            return currentRole.HasValue && currentRole.Value == role;
        }

        private bool TryClearCurrentStatePersistedRoi(out RoiRole? clearedRole)
        {
            clearedRole = GetCurrentStateRole();

            switch (_state)
            {
                case MasterState.DrawM1_Pattern:
                    if (_layout.Master1Pattern != null)
                    {
                        _layout.Master1Pattern = null;
                        _layout.Master1PatternImagePath = null;
                        return true;
                    }
                    break;

                case MasterState.DrawM1_Search:
                    if (_layout.Master1Search != null)
                    {
                        _layout.Master1Search = null;
                        return true;
                    }
                    break;

                case MasterState.DrawM2_Pattern:
                    if (_layout.Master2Pattern != null)
                    {
                        _layout.Master2Pattern = null;
                        _layout.Master2PatternImagePath = null;
                        return true;
                    }
                    break;

                case MasterState.DrawM2_Search:
                    if (_layout.Master2Search != null)
                    {
                        _layout.Master2Search = null;
                        return true;
                    }
                    break;

                case MasterState.DrawInspection:
                    if (_layout.Inspection != null)
                    {
                        _layout.Inspection = null;
                        SetInspectionBaseline(null);
                        return true;
                    }
                    break;

                case MasterState.Ready:
                    if (_layout.Inspection != null)
                    {
                        _layout.Inspection = null;
                        SetInspectionBaseline(null);
                        _state = MasterState.DrawInspection;
                        return true;
                    }

                    _state = MasterState.DrawInspection;
                    break;
            }

            return false;
        }


        // ====== Imagen ======
        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog() != true) return;

            LoadImage(dlg.FileName);
        }

        private void LoadImage(string path)
        {
            _currentImagePathWin = path;
            _currentImagePathBackend = path;
            _currentImagePath = _currentImagePathWin;

            _imgSourceBI = new BitmapImage();
            _imgSourceBI.BeginInit();
            _imgSourceBI.CacheOption = BitmapCacheOption.OnLoad;
            _imgSourceBI.UriSource = new Uri(_currentImagePathWin);
            _imgSourceBI.EndInit();

            ImgMain.Source = _imgSourceBI;
            RoiOverlay.InvalidateOverlay();
            _imgW = _imgSourceBI.PixelWidth;
            _imgH = _imgSourceBI.PixelHeight;

            try
            {
                var newFrame = Cv2.ImRead(path, ImreadModes.Color);
                if (newFrame == null || newFrame.Empty())
                {
                    newFrame?.Dispose();
                    bgrFrame?.Dispose();
                    bgrFrame = null;
                    MessageBox.Show("No se pudo leer la imagen para análisis.");
                }
                else
                {
                    bgrFrame?.Dispose();
                    bgrFrame = newFrame;
                }
            }
            catch (Exception ex)
            {
                bgrFrame?.Dispose();
                bgrFrame = null;
                MessageBox.Show($"Error al leer la imagen: {ex.Message}");
            }

            // 🔧 clave: forzar reprogramación aunque el scheduler se hubiera quedado “true”
            ScheduleSyncOverlay(force: true);

            AppendLog($"Imagen cargada: {_imgW}x{_imgH}  (Canvas: {CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0})");
            RedrawOverlaySafe();
            ClearHeatmapOverlay();
        }

        private bool IsOverlayAligned()
        {
            var disp = GetImageDisplayRect();
            if (disp.Width <= 0 || disp.Height <= 0) return false;

            double dw = Math.Abs(CanvasROI.ActualWidth - disp.Width);
            double dh = Math.Abs(CanvasROI.ActualHeight - disp.Height);
            return dw <= 0.5 && dh <= 0.5; // tolerancia sub-px
        }

        private void RedrawOverlaySafe()
        {
            if (IsOverlayAligned())
            {
                RedrawOverlay();
                _overlayNeedsRedraw = false;
            }
            else
            {
                _overlayNeedsRedraw = true;
                ScheduleSyncOverlay(force: true);
                AppendLog("[guard] Redraw pospuesto (overlay aún no alineado)");
            }
        }





        private void ClearPersistedRoisFromCanvas()
        {
            if (CanvasROI == null)
                return;

            var persisted = CanvasROI.Children
                .OfType<Shape>()
                .Where(shape => !ReferenceEquals(shape, _previewShape) && shape.Tag is RoiModel)
                .ToList();

            foreach (var shape in persisted)
            {
                RemoveRoiAdorners(shape);
                CanvasROI.Children.Remove(shape);
            }

            _roiShapesById.Clear();
        }

        private void RedrawOverlay()
        {
            if (CanvasROI == null)
                return;

            var staleShapes = CanvasROI.Children
                .OfType<Shape>()
                .Where(shape => !ReferenceEquals(shape, _previewShape) && shape.Tag is RoiModel)
                .ToList();

            foreach (var shape in staleShapes)
            {
                RemoveRoiAdorners(shape);
                CanvasROI.Children.Remove(shape);
            }

            _roiShapesById.Clear();

            if (_imgW <= 0 || _imgH <= 0)
                return;

            void AddPersistentRoi(RoiModel? roi)
            {
                if (roi == null)
                    return;

                var shape = CreateLayoutShape(roi);
                if (shape == null)
                {
                    AppendLog($"[overlay] build failed for {roi.Role} ({roi.Label})");
                    return;
                }

                CanvasROI.Children.Add(shape);
                _roiShapesById[roi.Id] = shape;

                if (ShouldEnableRoiEditing(roi.Role))
                {
                    AttachRoiAdorner(shape);
                }
            }

            AddPersistentRoi(_layout.Master1Search);
            AddPersistentRoi(_layout.Master1Pattern);
            AddPersistentRoi(_layout.Master2Search);
            AddPersistentRoi(_layout.Master2Pattern);
            AddPersistentRoi(_layout.Inspection);

            if (_layout.Inspection != null)
            {
                SyncCurrentRoiFromInspection(_layout.Inspection);
            }
        }

        private Shape? CreateLayoutShape(RoiModel roi)
        {
            var canvasRoi = ImageToCanvas(roi);
            canvasRoi.Role = roi.Role;
            canvasRoi.Label = roi.Label;
            canvasRoi.Id = roi.Id;

            Shape shape = canvasRoi.Shape switch
            {
                RoiShape.Rectangle => new WRectShape(),
                _ => new WEllipse()
            };

            var style = GetRoiStyle(canvasRoi.Role);

            shape.Stroke = style.stroke;
            shape.Fill = style.fill;
            shape.StrokeThickness = style.thickness;
            if (style.dash != null)
                shape.StrokeDashArray = style.dash;
            shape.SnapsToDevicePixels = true;
            shape.IsHitTestVisible = ShouldEnableRoiEditing(canvasRoi.Role);

            if (canvasRoi.Shape == RoiShape.Rectangle)
            {
                Canvas.SetLeft(shape, canvasRoi.Left);
                Canvas.SetTop(shape, canvasRoi.Top);
                shape.Width = canvasRoi.Width;
                shape.Height = canvasRoi.Height;
            }
            else
            {
                double diameter = Math.Max(canvasRoi.Width, canvasRoi.R * 2.0);
                double height = canvasRoi.Shape == RoiShape.Annulus && canvasRoi.Height > 0 ? canvasRoi.Height : diameter;
                Canvas.SetLeft(shape, canvasRoi.CX - diameter / 2.0);
                Canvas.SetTop(shape, canvasRoi.CY - height / 2.0);
                shape.Width = diameter;
                shape.Height = height;
            }

            shape.Tag = canvasRoi;
            Panel.SetZIndex(shape, style.zIndex);
            ApplyRoiRotationToShape(shape, canvasRoi.AngleDeg);

            return shape;
        }

        private (WBrush stroke, WBrush fill, double thickness, DoubleCollection? dash, int zIndex) GetRoiStyle(RoiRole role)
        {
            var transparent = WBrushes.Transparent;
            switch (role)
            {
                case RoiRole.Master1Pattern:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(30, 0, 255, 255));
                        fill.Freeze();
                        return (WBrushes.Cyan, fill, 2.0, null, 5);
                    }
                case RoiRole.Master1Search:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(18, 255, 215, 0));
                        fill.Freeze();
                        var dash = new DoubleCollection { 4, 3 };
                        dash.Freeze();
                        return (WBrushes.Gold, fill, 1.5, dash, 4);
                    }
                case RoiRole.Master2Pattern:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(30, 255, 165, 0));
                        fill.Freeze();
                        return (WBrushes.Orange, fill, 2.0, null, 6);
                    }
                case RoiRole.Master2Search:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(18, 205, 92, 92));
                        fill.Freeze();
                        var dash = new DoubleCollection { 4, 3 };
                        dash.Freeze();
                        return (WBrushes.IndianRed, fill, 1.5, dash, 4);
                    }
                case RoiRole.Inspection:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(45, 50, 205, 50));
                        fill.Freeze();
                        return (WBrushes.Lime, fill, 2.5, null, 7);
                    }
                default:
                    return (WBrushes.White, transparent, 2.0, null, 5);
            }
        }

        private void AttachRoiAdorner(Shape shape)
        {
            if (!ShouldEnableRoiEditing((shape.Tag as RoiModel)?.Role ?? RoiRole.Inspection))
                return;

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
                return;

            var existing = layer.GetAdorners(shape);
            if (existing != null)
            {
                foreach (var adorner in existing.OfType<RoiAdorner>())
                    layer.Remove(adorner);
            }

            if (shape.Tag is not RoiModel roiInfo)
                return;

            if (RoiOverlay == null)
                return;

            var newAdorner = new RoiAdorner(shape, RoiOverlay, (changeKind, updatedModel) =>
            {
                var pixelModel = CanvasToImage(updatedModel);
                UpdateLayoutFromPixel(pixelModel);
                HandleAdornerChange(changeKind, updatedModel, pixelModel, "[adorner]");
            }, AppendLog);

            layer.Add(newAdorner);
        }

        private void RemoveRoiAdorners(Shape shape)
        {
            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
                return;

            var adorners = layer.GetAdorners(shape);
            if (adorners == null)
                return;

            foreach (var adorner in adorners.OfType<RoiAdorner>())
                layer.Remove(adorner);
        }


        private async Task ShowHeatmapOverlayAsync(Workflow.RoiExportResult export, byte[] heatmapBytes, double opacity)
        {
            if (export == null || heatmapBytes == null || heatmapBytes.Length == 0)
                return;

            try
            {
                BitmapSource heatmapSource;
                using (var ms = new MemoryStream(heatmapBytes))
                {
                    var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    heatmapSource = decoder.Frames[0];
                    heatmapSource.Freeze();
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    _heatmapBitmap = heatmapSource;
                    _heatmapRoiImage = export.RoiImage.Clone();
                    _heatmapOverlayOpacity = Math.Clamp(opacity, 0.0, 1.0);

                    if (HeatmapImage != null)
                    {
                        HeatmapImage.Source = _heatmapBitmap;
                    }

                    EnterAnalysisView();

                    if (CanvasROI != null)
                    {
                        if (_heatmapOverlayImage == null)
                        {
                            _heatmapOverlayImage = new System.Windows.Controls.Image
                            {
                                Stretch = Stretch.Fill,
                                IsHitTestVisible = false
                            };
                            Panel.SetZIndex(_heatmapOverlayImage, 20);
                            CanvasROI.Children.Add(_heatmapOverlayImage);
                        }

                        _heatmapOverlayImage.Source = _heatmapBitmap;
                        _heatmapOverlayImage.Opacity = _heatmapOverlayOpacity;
                        RefreshHeatmapOverlay();
                    }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                AppendLog("[heatmap] error: " + ex.Message);
            }
        }

        private void ClearHeatmapOverlay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearHeatmapOverlay);
                return;
            }

            _heatmapBitmap = null;
            _heatmapRoiImage = null;

            if (_heatmapOverlayImage != null && CanvasROI != null)
            {
                CanvasROI.Children.Remove(_heatmapOverlayImage);
                _heatmapOverlayImage = null;
            }

            if (HeatmapImage != null)
            {
                HeatmapImage.Source = null;
            }
        }

        private void RefreshHeatmapOverlay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshHeatmapOverlay);
                return;
            }

            if (_heatmapOverlayImage == null || _heatmapBitmap == null || _heatmapRoiImage == null || CanvasROI == null)
                return;

            if (!CanvasROI.Children.Contains(_heatmapOverlayImage))
            {
                CanvasROI.Children.Add(_heatmapOverlayImage);
            }

            var canvasRoi = ImageToCanvas(_heatmapRoiImage);

            double width;
            double height;
            double left;
            double top;

            if (canvasRoi.Shape == RoiShape.Rectangle)
            {
                left = canvasRoi.Left;
                top = canvasRoi.Top;
                width = Math.Max(1.0, canvasRoi.Width);
                height = Math.Max(1.0, canvasRoi.Height);
            }
            else
            {
                double diameter = Math.Max(canvasRoi.Width, canvasRoi.R * 2.0);
                height = canvasRoi.Shape == RoiShape.Annulus && canvasRoi.Height > 0 ? canvasRoi.Height : diameter;
                width = Math.Max(1.0, diameter);
                height = Math.Max(1.0, height);
                left = canvasRoi.CX - width / 2.0;
                top = canvasRoi.CY - height / 2.0;
            }

            Canvas.SetLeft(_heatmapOverlayImage, left);
            Canvas.SetTop(_heatmapOverlayImage, top);
            _heatmapOverlayImage.Width = width;
            _heatmapOverlayImage.Height = height;
            _heatmapOverlayImage.Opacity = _heatmapOverlayOpacity;
            _heatmapOverlayImage.RenderTransformOrigin = new WPoint(0.5, 0.5);

            if (_heatmapOverlayImage.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = canvasRoi.AngleDeg;
            }
            else
            {
                _heatmapOverlayImage.RenderTransform = new RotateTransform(canvasRoi.AngleDeg);
            }
        }


        private void ResetAnalysisMarks()
        {
            RemoveAnalysisMarks();
            ClearHeatmapOverlay();
            RedrawOverlaySafe();
            _analysisViewActive = false;
            AppendLog("[ANALYZE] Limpiadas marcas de análisis (cruces).");
        }

        private void RemoveAnalysisMarks()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RemoveAnalysisMarks);
                return;
            }

            if (CanvasROI == null)
                return;

            var toRemove = CanvasROI.Children
                .OfType<FrameworkElement>()
                .Where(el => el.Tag is string tag && tag == ANALYSIS_TAG)
                .ToList();

            foreach (var el in toRemove)
            {
                CanvasROI.Children.Remove(el);
            }
        }

        private void DrawCross(double x, double y, int size, Brush brush, double thickness)
        {
            if (CanvasROI == null)
                return;

            double half = size;

            var lineH = new WLine
            {
                X1 = x - half,
                Y1 = y,
                X2 = x + half,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                Tag = ANALYSIS_TAG
            };

            var lineV = new WLine
            {
                X1 = x,
                Y1 = y - half,
                X2 = x,
                Y2 = y + half,
                Stroke = brush,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                Tag = ANALYSIS_TAG
            };

            Panel.SetZIndex(lineH, 30);
            Panel.SetZIndex(lineV, 30);

            CanvasROI.Children.Add(lineH);
            CanvasROI.Children.Add(lineV);
        }

        private void DrawLabeledCross(double x, double y, string label, Brush crossColor, Brush labelBg, Brush labelFg, int crossSize, double thickness)
        {
            DrawCross(x, y, crossSize, crossColor, thickness);

            if (CanvasROI == null)
                return;

            var text = new TextBlock
            {
                Text = label,
                Foreground = labelFg,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2)
            };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);

            var border = new Border
            {
                Background = labelBg,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(0.8),
                Child = text,
                Opacity = 0.92,
                Tag = ANALYSIS_TAG
            };

            Canvas.SetLeft(border, x + LabelOffsetX);
            Canvas.SetTop(border, y + LabelOffsetY);
            Panel.SetZIndex(border, 31);

            CanvasROI.Children.Add(border);
        }

        private void DrawMasterMatch(RoiModel roi, WPoint matchImagePoint, string caption, Brush brush, bool withLabel)
        {
            var canvasPoint = ImagePxToCanvasPt(matchImagePoint.X, matchImagePoint.Y);
            var canvasRoi = ImageToCanvas(roi);
            double reference = Math.Max(canvasRoi.Width, canvasRoi.Height);
            if (reference <= 0)
            {
                reference = Math.Max(CanvasROI?.ActualWidth ?? 0, CanvasROI?.ActualHeight ?? 0) * 0.05;
            }

            int size = (int)Math.Round(Math.Clamp(reference * 0.2, 14, 60));
            double thickness = Math.Max(2.0, size / 8.0);

            if (withLabel)
            {
                DrawLabeledCross(canvasPoint.X, canvasPoint.Y, caption, brush, Brushes.Black, Brushes.White, size, thickness);
            }
            else
            {
                DrawCross(canvasPoint.X, canvasPoint.Y, size, brush, thickness);
            }
        }

        private void EnterAnalysisView()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(EnterAnalysisView);
                return;
            }

            _analysisViewActive = true;
            if (Tabs != null && TabAnalyze != null)
            {
                Tabs.SelectedItem = TabAnalyze;
            }
        }

        private string? ResolveRoiLabelText(RoiModel roi)
        {
            if (roi == null)
                return null;

            if (!string.IsNullOrWhiteSpace(roi.Label))
                return roi.Label;

            return roi.Role switch
            {
                RoiRole.Master1Pattern => "Master 1",
                RoiRole.Master1Search => "Master 1 search",
                RoiRole.Master2Pattern => "Master 2",
                RoiRole.Master2Search => "Master 2 search",
                RoiRole.Inspection => "Inspection",
                _ => null
            };
        }
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ScheduleSyncOverlay(force: true);
            RefreshHeatmapOverlay();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleSyncOverlay(force: true);
            RefreshHeatmapOverlay();
        }

        private void ImgMain_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleSyncOverlay(force: true);
            RefreshHeatmapOverlay();
        }


        // ====== Ratón & dibujo ======
        private RoiShape ReadShapeForCurrentStep()
        {
            string ToLower(object? x) => (x?.ToString() ?? "").ToLowerInvariant();

            if (_state == MasterState.DrawM1_Pattern || _state == MasterState.DrawM1_Search)
            {
                var t = ToLower(ComboMasterRoiShape.SelectedItem);
                if (t.Contains("círculo") || t.Contains("circulo")) return RoiShape.Circle;
                return RoiShape.Rectangle;
            }
            else if (_state == MasterState.DrawM2_Pattern || _state == MasterState.DrawM2_Search)
            {
                var t = ToLower(ComboM2Shape.SelectedItem);
                if (t.Contains("círculo") || t.Contains("circulo")) return RoiShape.Circle;
                return RoiShape.Rectangle;
            }
            else
            {
                var t = ToLower(ComboInspShape.SelectedItem);
                if (t.Contains("círculo") || t.Contains("circulo")) return RoiShape.Circle;
                if (t.Contains("annulus")) return RoiShape.Annulus;
                return RoiShape.Rectangle;
            }
        }

        private void BeginDraw(RoiShape shape, WPoint p0)
        {
            // Si había un preview anterior, elimínalo para evitar capas huérfanas
            ClearPreview();

            _previewShape = shape switch
            {
                RoiShape.Rectangle => new WRectShape
                {
                    Stroke = WBrushes.Cyan,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(40, 0, 255, 255))
                },
                RoiShape.Circle => new WEllipse
                {
                    Stroke = WBrushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(30, 0, 255, 0))
                },
                RoiShape.Annulus => new AnnulusShape
                {
                    Stroke = WBrushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(30, 0, 255, 0))
                },
                _ => null
            };

            if (_previewShape != null)
            {
                Canvas.SetLeft(_previewShape, p0.X);
                Canvas.SetTop(_previewShape, p0.Y);
                _previewShape.Width = 0;
                _previewShape.Height = 0;
                Panel.SetZIndex(_previewShape, 20);
                CanvasROI.Children.Add(_previewShape);
            }
        }

        private bool ShouldLogAnnulusValue(ref double lastValue, double newValue)
        {
            if (double.IsNaN(lastValue) || Math.Abs(lastValue - newValue) >= AnnulusLogThreshold)
            {
                lastValue = newValue;
                return true;
            }

            return false;
        }

        private bool ShouldLogAnnulusInner(double proposedInner, double finalInner)
        {
            bool shouldLog = false;

            if (double.IsNaN(_lastLoggedAnnulusInnerProposed) || Math.Abs(proposedInner - _lastLoggedAnnulusInnerProposed) >= AnnulusLogThreshold)
            {
                shouldLog = true;
            }

            if (double.IsNaN(_lastLoggedAnnulusInnerFinal) || Math.Abs(finalInner - _lastLoggedAnnulusInnerFinal) >= AnnulusLogThreshold)
            {
                shouldLog = true;
            }

            if (shouldLog)
            {
                _lastLoggedAnnulusInnerProposed = proposedInner;
                _lastLoggedAnnulusInnerFinal = finalInner;
            }

            return shouldLog;
        }

        private static bool TryConsumeLogSlot(ref DateTime lastTimestamp)
        {
            var now = DateTime.UtcNow;
            if (now - lastTimestamp < AnnulusLogMinInterval)
            {
                return false;
            }

            lastTimestamp = now;
            return true;
        }

        private void UpdateDraw(RoiShape shape, System.Windows.Point p0, System.Windows.Point p1)
        {
            if (_previewShape == null) return;

            if (shape == RoiShape.Rectangle)
            {
                var x = Math.Min(p0.X, p1.X);
                var y = Math.Min(p0.Y, p1.Y);
                var w = Math.Abs(p1.X - p0.X);
                var h = Math.Abs(p1.Y - p0.Y);

                Canvas.SetLeft(_previewShape, x);
                Canvas.SetTop(_previewShape, y);
                _previewShape.Width = w;
                _previewShape.Height = h;
                return;
            }

            // === Círculo / Annulus ===
            // Mantén el mismo sistema de coordenadas que el modelo/adorners:
            // usa radio = max(|dx|, |dy|) (norma L∞), no la distancia euclídea.
            var dx = p1.X - p0.X;
            var dy = p1.Y - p0.Y;

            double radius = Math.Max(Math.Abs(dx), Math.Abs(dy));

            // Evita que el preview se "vaya" fuera del canvas mientras dibujas
            radius = ClampRadiusToCanvasBounds(p0, radius);

            var diameter = radius * 2.0;
            var left = p0.X - radius;
            var top = p0.Y - radius;

            Canvas.SetLeft(_previewShape, left);
            Canvas.SetTop(_previewShape, top);
            _previewShape.Width = diameter;
            _previewShape.Height = diameter;

            if (shape == RoiShape.Annulus && _previewShape is AnnulusShape annulus)
            {
                // Outer radius = radius (canvas)
                var outer = radius;
                double previousOuter = _lastLoggedAnnulusOuterRadius;
                if (ShouldLogAnnulusValue(ref _lastLoggedAnnulusOuterRadius, outer))
                {
                    if (TryConsumeLogSlot(ref _lastAnnulusOuterLogTimestamp))
                    {
                        AppendLog($"[annulus] outer radius preview={outer:0.##} px");
                    }
                    else
                    {
                        _lastLoggedAnnulusOuterRadius = previousOuter;
                    }
                }

                // Conserva proporción si el usuario ya la ha cambiado; si no, usa el default & clamp.
                double proposedInner = annulus.InnerRadius;
                double resolvedInner = AnnulusDefaults.ResolveInnerRadius(proposedInner, outer);
                double finalInner = AnnulusDefaults.ClampInnerRadius(resolvedInner, outer);

                double previousInnerProposed = _lastLoggedAnnulusInnerProposed;
                double previousInnerFinal = _lastLoggedAnnulusInnerFinal;
                if (ShouldLogAnnulusInner(proposedInner, finalInner))
                {
                    if (TryConsumeLogSlot(ref _lastAnnulusInnerLogTimestamp))
                    {
                        AppendLog($"[annulus] outer={outer:0.##} px, proposed inner={proposedInner:0.##} px -> final inner={finalInner:0.##} px");
                    }
                    else
                    {
                        _lastLoggedAnnulusInnerProposed = previousInnerProposed;
                        _lastLoggedAnnulusInnerFinal = previousInnerFinal;
                    }
                }

                annulus.InnerRadius = finalInner;
            }
        }

        private double ClampRadiusToCanvasBounds(System.Windows.Point center, double desiredRadius)
        {
            if (CanvasROI == null) return desiredRadius;

            double cw = CanvasROI.ActualWidth;
            double ch = CanvasROI.ActualHeight;
            if (cw <= 0 || ch <= 0) return desiredRadius;

            double maxLeft = center.X;
            double maxRight = cw - center.X;
            double maxUp = center.Y;
            double maxDown = ch - center.Y;

            double maxRadius = Math.Max(0.0, Math.Min(Math.Min(maxLeft, maxRight), Math.Min(maxUp, maxDown)));
            return Math.Min(desiredRadius, maxRadius);
        }

        private void HookCanvasInput()
        {
            // Escuchamos SIEMPRE, aunque otro control marque Handled=true
            CanvasROI.AddHandler(UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(Canvas_MouseLeftButtonDownEx), true);
            CanvasROI.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(Canvas_MouseLeftButtonUpEx), true);
            CanvasROI.AddHandler(UIElement.MouseMoveEvent,
                new MouseEventHandler(Canvas_MouseMoveEx), true);
        }

        private void Canvas_MouseLeftButtonDownEx(object sender, MouseButtonEventArgs e)
        {
            var over = System.Windows.Input.Mouse.DirectlyOver;
            var handledBefore = e.Handled;
            AppendLog($"[canvas+] Down HB={handledBefore} src={e.OriginalSource?.GetType().Name}, over={over?.GetType().Name}");

            // ⛑️ No permitir interacción si el overlay no está alineado aún
            if (!IsOverlayAligned())
            {
                AppendLog("[guard] overlay no alineado todavía → reprogramo sync y cancelo este click");
                ScheduleSyncOverlay(force: true);
                e.Handled = true;
                return;
            }

            // 1) Thumb → lo gestiona el adorner
            if (over is System.Windows.Controls.Primitives.Thumb)
            {
                AppendLog("[canvas+] Down ignorado (Thumb debajo) -> Adorner manejará");
                return;
            }

            // 2) Arrastre de ROI existente
            if (e.OriginalSource is Shape sShape && sShape.Tag is RoiModel)
            {
                _dragShape = sShape;
                _dragStart = e.GetPosition(CanvasROI);
                _dragOrigX = Canvas.GetLeft(sShape);
                _dragOrigY = Canvas.GetTop(sShape);
                if (double.IsNaN(_dragOrigX)) _dragOrigX = 0;
                if (double.IsNaN(_dragOrigY)) _dragOrigY = 0;

                CanvasROI.CaptureMouse();
                AppendLog($"[drag] start HB={handledBefore} on {sShape.GetType().Name} at {_dragStart.X:0},{_dragStart.Y:0} orig=({_dragOrigX:0},{_dragOrigY:0})");
                e.Handled = true;
                return;
            }

            // 3) Dibujo nuevo ROI en canvas vacío
            if (e.OriginalSource is Canvas)
            {
                _isDrawing = true;
                _p0 = e.GetPosition(CanvasROI);
                _currentShape = ReadShapeForCurrentStep();
                BeginDraw(_currentShape, _p0);
                CanvasROI.CaptureMouse();
                AppendLog($"[mouse] Down @ {_p0.X:0},{_p0.Y:0} shape={_currentShape}");
                e.Handled = true;
                return;
            }

            AppendLog($"[canvas+] Down ignorado (src={e.OriginalSource?.GetType().Name})");
        }


        private void Canvas_MouseMoveEx(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // ARRASTRE activo
            if (_dragShape != null)
            {
                var p = e.GetPosition(CanvasROI);
                var dx = p.X - _dragStart.X;
                var dy = p.Y - _dragStart.Y;

                var nx = _dragOrigX + dx;
                var ny = _dragOrigY + dy;

                Canvas.SetLeft(_dragShape, nx);
                Canvas.SetTop(_dragShape, ny);

                // Sincroniza modelo y recoloca los thumbs del adorner
                SyncModelFromShape(_dragShape);
                InvalidateAdornerFor(_dragShape);

                AppendLog($"[drag] move dx={dx:0.##} dy={dy:0.##} -> pos=({nx:0.##},{ny:0.##})");
                return;
            }

            // DIBUJO activo
            if (_isDrawing)
            {
                var p1 = e.GetPosition(CanvasROI);
                UpdateDraw(_currentShape, _p0, p1);
            }
        }

        private void Canvas_MouseLeftButtonUpEx(object sender, MouseButtonEventArgs e)
        {
            var over = System.Windows.Input.Mouse.DirectlyOver;
            var handledBefore = e.Handled;
            AppendLog($"[canvas+] Up   HB={handledBefore} src={e.OriginalSource?.GetType().Name}, over={over?.GetType().Name}");

            // FIN ARRASTRE
            if (_dragShape != null)
            {
                AppendLog("[drag] end");
                CanvasROI.ReleaseMouseCapture();
                _dragShape = null;
                MasterLayoutManager.Save(_preset, _layout);
                e.Handled = true;
                return;
            }

            // FIN DIBUJO
            if (_isDrawing)
            {
                _isDrawing = false;
                var p1 = e.GetPosition(CanvasROI);
                EndDraw(_currentShape, _p0, p1);
                CanvasROI.ReleaseMouseCapture();
                AppendLog($"[mouse] Up   @ {p1.X:0},{p1.Y:0}");
                e.Handled = true;
                return;
            }
        }

        private void EndDraw(RoiShape shape, System.Windows.Point p0, System.Windows.Point p1)
        {
            if (_previewShape == null) return;

            RoiModel canvasDraft;
            if (shape == RoiShape.Rectangle)
            {
                var x = Canvas.GetLeft(_previewShape);
                var y = Canvas.GetTop(_previewShape);
                var w = _previewShape.Width;
                var h = _previewShape.Height;
                canvasDraft = new RoiModel { Shape = RoiShape.Rectangle, Width = w, Height = h };
                canvasDraft.Left = x;
                canvasDraft.Top = y;
            }
            else
            {
                var x = Canvas.GetLeft(_previewShape);
                var y = Canvas.GetTop(_previewShape);
                var w = _previewShape.Width;
                var r = w / 2.0;
                var cx = x + r; var cy = y + r;
                double innerRadius = 0;
                if (shape == RoiShape.Annulus)
                {
                    if (_previewShape is AnnulusShape annulus)
                        innerRadius = AnnulusDefaults.ResolveInnerRadius(annulus.InnerRadius, r);
                    else
                        innerRadius = AnnulusDefaults.ResolveInnerRadius(innerRadius, r);
                }

                canvasDraft = new RoiModel
                {
                    Shape = shape,
                    CX = cx,
                    CY = cy,
                    R = r,
                    RInner = innerRadius,
                    Width = w,
                    Height = _previewShape.Height
                };
                canvasDraft.Left = x;
                canvasDraft.Top = y;
            }

            var pixelDraft = CanvasToImage(canvasDraft);
            var activeRole = GetCurrentStateRole();
            _tmpBuffer = pixelDraft;
            if (activeRole.HasValue)
            {
                canvasDraft.Role = activeRole.Value;
                if (pixelDraft != null)
                    pixelDraft.Role = activeRole.Value;
                if (_tmpBuffer != null)
                    _tmpBuffer.Role = activeRole.Value;
            }
            AppendLog($"[draw] ROI draft = {DescribeRoi(_tmpBuffer)}");

            _previewShape.Tag = canvasDraft;
            ApplyRoiRotationToShape(_previewShape, canvasDraft.AngleDeg);
            if (_state == MasterState.DrawInspection)
            {
                if (_tmpBuffer != null)
                {
                    SyncCurrentRoiFromInspection(_tmpBuffer);
                }
            }
            else if (pixelDraft != null)
            {
                UpdateOverlayFromPixelModel(pixelDraft);
            }
            _previewShape.IsHitTestVisible = true; // el adorner coge los clics
            _previewShape.StrokeDashArray = new DoubleCollection { 4, 4 };

            var al = AdornerLayer.GetAdornerLayer(_previewShape);
            if (al != null)
            {
                var prev = al.GetAdorners(_previewShape);
                if (prev != null)
                {
                    foreach (var ad in prev.OfType<RoiAdorner>())
                        al.Remove(ad);
                }

                if (RoiOverlay == null)
                    return;

                var adorner = new RoiAdorner(_previewShape, RoiOverlay, (changeKind, modelUpdated) =>
                {
                    var pixelModel = CanvasToImage(modelUpdated);
                    _tmpBuffer = pixelModel.Clone();
                    if (_state == MasterState.DrawInspection && _tmpBuffer != null)
                    {
                        modelUpdated.Role = RoiRole.Inspection;
                        _tmpBuffer.Role = RoiRole.Inspection;
                        SyncCurrentRoiFromInspection(_tmpBuffer);
                    }
                    if (_tmpBuffer != null)
                    {
                        HandleAdornerChange(changeKind, modelUpdated, pixelModel, "[preview]");
                    }
                }, AppendLog); // ⬅️ pasa logger

                al.Add(adorner);
                AppendLog("[adorner] preview OK layer attach");
            }
            else
            {
                AppendLog("[adorner] preview layer NOT FOUND (falta AdornerDecorator)");
            }
        }

        private string DescribeRoi(RoiModel? r)
        {
            if (r == null) return "<null>";
            return r.Shape switch
            {
                RoiShape.Rectangle => $"Rect x={r.X:0},y={r.Y:0},w={r.Width:0},h={r.Height:0},ang={r.AngleDeg:0.0}",
                RoiShape.Circle => $"Circ cx={r.CX:0},cy={r.CY:0},r={r.R:0},ang={r.AngleDeg:0.0}",
                RoiShape.Annulus => $"Ann cx={r.CX:0},cy={r.CY:0},r={r.R:0},ri={r.RInner:0},ang={r.AngleDeg:0.0}",
                _ => "?"
            };
        }

        private void HandleAdornerChange(RoiAdornerChangeKind changeKind, RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            switch (changeKind)
            {
                case RoiAdornerChangeKind.DragStarted:
                    HandleDragStarted(canvasModel, pixelModel, contextLabel);
                    break;
                case RoiAdornerChangeKind.Delta:
                    HandleDragDelta(canvasModel, pixelModel, contextLabel);
                    break;
                case RoiAdornerChangeKind.DragCompleted:
                    HandleDragCompleted(canvasModel, pixelModel, contextLabel);
                    break;
            }

            UpdateOverlayFromPixelModel(pixelModel);
        }

        private void HandleDragStarted(RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            var state = GetOrCreateDragState(canvasModel);
            state.Buffer.Clear();
            state.LastSnapshot = CaptureSnapshot(canvasModel);
            state.HasSnapshot = true;

            AppendLog($"{contextLabel} drag start => {DescribeRoi(pixelModel)}");
        }

        private void HandleDragDelta(RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            var state = GetOrCreateDragState(canvasModel);
            var snapshot = CaptureSnapshot(canvasModel);

            if (!state.HasSnapshot)
            {
                state.LastSnapshot = snapshot;
                state.HasSnapshot = true;
            }

            if (!ShouldLogDelta(state, snapshot))
                return;

            state.Buffer.AppendLine($"{contextLabel} drag delta => {DescribeRoi(pixelModel)}");
            state.LastSnapshot = snapshot;
        }

        private void HandleDragCompleted(RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            if (_dragLogStates.TryGetValue(canvasModel, out var state))
            {
                FlushDragBuffer(state);
                _dragLogStates.Remove(canvasModel);
            }

            AppendLog($"{contextLabel} drag end => {DescribeRoi(pixelModel)}");
        }

        private DragLogState GetOrCreateDragState(RoiModel model)
        {
            if (!_dragLogStates.TryGetValue(model, out var state))
            {
                state = new DragLogState();
                _dragLogStates[model] = state;
            }

            return state;
        }

        private static RoiSnapshot CaptureSnapshot(RoiModel model)
        {
            double x;
            double y;
            double width;
            double height;

            switch (model.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    {
                        double radius = model.R;
                        if (radius > 0)
                        {
                            width = radius * 2.0;
                            height = width;
                        }
                        else
                        {
                            width = model.Width;
                            height = model.Height;
                            if (width <= 0 && height > 0) width = height;
                            if (height <= 0 && width > 0) height = width;
                        }

                        x = model.CX - width / 2.0;
                        y = model.CY - height / 2.0;
                        break;
                    }
                case RoiShape.Rectangle:
                default:
                    x = model.X;
                    y = model.Y;
                    width = model.Width;
                    height = model.Height;
                    break;
            }

            return new RoiSnapshot(x, y, width, height, model.AngleDeg);
        }

        private bool ShouldLogDelta(DragLogState state, RoiSnapshot current)
        {
            if (!state.HasSnapshot)
                return true;

            var last = state.LastSnapshot;

            if (Math.Abs(current.X - last.X) >= DragLogMovementThreshold)
                return true;
            if (Math.Abs(current.Y - last.Y) >= DragLogMovementThreshold)
                return true;
            if (Math.Abs(current.Width - last.Width) >= DragLogMovementThreshold)
                return true;
            if (Math.Abs(current.Height - last.Height) >= DragLogMovementThreshold)
                return true;

            var angleDelta = Math.Abs(NormalizeAngleDifference(current.Angle - last.Angle));
            return angleDelta >= DragLogAngleThreshold;
        }

        private void FlushDragBuffer(DragLogState state)
        {
            if (state.Buffer.Length == 0)
                return;

            var snapshot = state.Buffer.ToString();
            state.Buffer.Clear();

            using var reader = new StringReader(snapshot);
            string? line;
            var lines = new List<string>();
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            if (lines.Count > 0)
                AppendLogBulk(lines);
        }

        private static double NormalizeAngleDifference(double angleDeg)
        {
            angleDeg %= 360.0;
            if (angleDeg <= -180.0)
                angleDeg += 360.0;
            else if (angleDeg > 180.0)
                angleDeg -= 360.0;
            return angleDeg;
        }

        private sealed class DragLogState
        {
            public RoiSnapshot LastSnapshot;
            public bool HasSnapshot;
            public StringBuilder Buffer { get; } = new();
        }

        private readonly struct RoiSnapshot
        {
            public RoiSnapshot(double x, double y, double width, double height, double angle)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
                Angle = angle;
            }

            public double X { get; }
            public double Y { get; }
            public double Width { get; }
            public double Height { get; }
            public double Angle { get; }
        }

        // ====== Guardar pasos del wizard ======
        // ====== Guardar pasos del wizard ======
        private void BtnSaveMaster_Click(object sender, RoutedEventArgs e)
        {

            var layoutPath = MasterLayoutManager.GetDefaultPath(_preset);

            if (_tmpBuffer is null)
            {
                var previousState = _state;
                var cleared = TryClearCurrentStatePersistedRoi(out var clearedRole);

                if (cleared)
                    AppendLog($"[wizard] cleared ROI state={previousState} role={clearedRole}");
                else
                    AppendLog($"[wizard] no ROI to clear state={previousState} role={clearedRole}");

                ClearPreview();
                RedrawOverlaySafe();
                UpdateWizardState();

                if (!cleared)
                {
                    Snack("No hay ROI que eliminar. Dibuja un ROI válido antes de guardar.");
                    return;
                }

                Exception? clearException = null;
                try
                {
                    MasterLayoutManager.Save(_preset, _layout);
                    AppendLog($"[wizard] layout saved => {layoutPath}");
                }
                catch (Exception ex)
                {
                    clearException = ex;
                    AppendLog($"[wizard] layout save FAILED => {layoutPath} :: {ex}");
                }

                if (clearException != null)
                {
                    Snack("Error guardando layout: " + clearException.Message);
                    return;
                }

                var removalSummary = clearedRole?.ToString() ?? "ROI";
                Snack($"ROI eliminado ({removalSummary}). Dibuja un ROI válido antes de guardar.");
                return;
            }

            var bufferSource = "fresh";
            RoiModel? savedRoi = null;
            RoiRole? savedRole = null;

            switch (_state)
            {
                case MasterState.DrawM1_Pattern:
                    savedRole = RoiRole.Master1Pattern;
                    AppendLog($"[wizard] save state={_state} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    if (!IsAllowedMasterShape(_tmpBuffer.Shape)) { Snack("Master: usa rectángulo o círculo"); return; }
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master1Pattern = _tmpBuffer.Clone();
                    savedRoi = _layout.Master1Pattern;
                    // Preview (si hay imagen cargada)
                    {
                        var displayRect = GetImageDisplayRect();
                        var (pw, ph) = GetImagePixelSize();
                        double scale = displayRect.Width / System.Math.Max(1.0, pw);
                        AppendLog($"[save] scale={scale:0.####} dispRect=({displayRect.Left:0.##},{displayRect.Top:0.##})");
                        AppendLog($"[save] ROI image : {savedRoi}");
                    }
                    SaveRoiCropPreview(_layout.Master1Pattern, "M1_pattern");
                    _layout.Master1PatternImagePath = SaveMasterPatternCanonical(_layout.Master1Pattern, "master1_pattern");

                    _tmpBuffer = null;
                    _state = MasterState.DrawM1_Search;

                    // Auto-cambiar el combo de rol a "Inspección Master 1"
                    try { ComboMasterRoiRole.SelectedIndex = 1; } catch { }
                    break;

                case MasterState.DrawM1_Search:
                    savedRole = RoiRole.Master1Search;
                    AppendLog($"[wizard] save state={_state} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master1Search = _tmpBuffer.Clone();
                    savedRoi = _layout.Master1Search;
                    SaveRoiCropPreview(_layout.Master1Search, "M1_search");

                    _tmpBuffer = null;
                    _state = MasterState.DrawM2_Pattern;
                    break;

                case MasterState.DrawM2_Pattern:
                    savedRole = RoiRole.Master2Pattern;
                    AppendLog($"[wizard] save state={_state} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    if (!IsAllowedMasterShape(_tmpBuffer.Shape)) { Snack("Master: usa rectángulo o círculo"); return; }
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master2Pattern = _tmpBuffer.Clone();
                    savedRoi = _layout.Master2Pattern;
                    SaveRoiCropPreview(_layout.Master2Pattern, "M2_pattern");
                    _layout.Master2PatternImagePath = SaveMasterPatternCanonical(_layout.Master2Pattern, "master2_pattern");

                    _tmpBuffer = null;
                    _state = MasterState.DrawM2_Search;

                    // Auto-cambiar el combo de rol a "Inspección Master 2"
                    try { ComboM2Role.SelectedIndex = 1; } catch { }
                    break;

                case MasterState.DrawM2_Search:
                    savedRole = RoiRole.Master2Search;
                    AppendLog($"[wizard] save state={_state} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master2Search = _tmpBuffer.Clone();
                    savedRoi = _layout.Master2Search;
                    SaveRoiCropPreview(_layout.Master2Search, "M2_search");

                    _tmpBuffer = null;

                    // En este punto M1+M2 podrían estar completos → permite inspección pero NO la exige
                    _state = MasterState.DrawInspection; // Puedes seguir con inspección si quieres
                    break;

                case MasterState.DrawInspection:
                    savedRole = RoiRole.Inspection;
                    AppendLog($"[wizard] save state={_state} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Inspection = _tmpBuffer.Clone();
                    SetInspectionBaseline(_layout.Inspection);
                    savedRoi = _layout.Inspection;
                    SyncCurrentRoiFromInspection(_layout.Inspection);

                    // (Opcional) también puedes guardar un preview de la inspección inicial:
                    SaveRoiCropPreview(_layout.Inspection, "INS_init");

                    _tmpBuffer = null;
                    _state = MasterState.Ready;
                    break;
            }

            // Limpia preview/adorner y persiste
            ClearPreview();

            Exception? saveException = null;
            try
            {
                MasterLayoutManager.Save(_preset, _layout);
                AppendLog($"[wizard] layout saved => {layoutPath}");
            }
            catch (Exception ex)
            {
                saveException = ex;
                AppendLog($"[wizard] layout save FAILED => {layoutPath} :: {ex}");
            }

            ClearPersistedRoisFromCanvas();
            RedrawOverlaySafe();

            // IMPORTANTE: recalcula habilitaciones (esto ya deja el botón "Analizar Master" activo si M1+M2 están listos)
            UpdateWizardState();

            if (saveException != null)
            {
                Snack("Error guardando layout: " + saveException.Message);
                return;
            }

            var savedSummary = savedRoi != null
                ? $"{savedRole}: {DescribeRoi(savedRoi)}"
                : "<sin ROI>";
            Snack($"Guardado. {savedSummary}");
        }


        private void ClearPreview()
        {
            if (_previewShape != null)
            {
                var al = AdornerLayer.GetAdornerLayer(_previewShape);
                if (al != null)
                {
                    var prev = al.GetAdorners(_previewShape);
                    if (prev != null)
                    {
                        foreach (var ad in prev.OfType<RoiAdorner>())
                            al.Remove(ad);
                    }
                }
                CanvasROI.Children.Remove(_previewShape);
                _previewShape = null;
            }
        }

        private static bool IsAllowedMasterShape(RoiShape s) => s == RoiShape.Rectangle || s == RoiShape.Circle;

        // ====== Validadores ======
        private void BtnValidateM1_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateMasterGroup(_layout.Master1Pattern, _layout.Master1Search)) return;
            Snack("Master 1 válido.");
        }

        private void BtnValidateM2_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateMasterGroup(_layout.Master2Pattern, _layout.Master2Search)) return;
            Snack("Master 2 válido.");
        }

        private void BtnValidateInsp_Click(object sender, RoutedEventArgs e)
        {
            if (_layout.Inspection == null) { Snack("Falta ROI Inspección"); return; }
            if (!ValidateRoiInImage(_layout.Inspection)) return;
            Snack("Inspección válida.");
        }

        private bool ValidateMasterGroup(RoiModel? pattern, RoiModel? search)
        {
            if (pattern == null || search == null) { Snack("Faltan patrón o zona de búsqueda"); return false; }
            if (!ValidateRoiInImage(pattern)) return false;
            if (!ValidateRoiInImage(search)) return false;

            var patRect = RoiToRect(pattern);
            var seaRect = RoiToRect(search);

            // Centro del patrón
            var pc = new WPoint(patRect.X + patRect.Width / 2, patRect.Y + patRect.Height / 2);

            // Permitir validación si el centro cae en BÚSQUEDA o en INSPECCIÓN
            bool inSearch = seaRect.Contains(pc);
            bool inInspection = false;
            if (_layout.Inspection != null)
            {
                var insRect = RoiToRect(_layout.Inspection);
                inInspection = insRect.Contains(pc);
            }

            if (!inSearch && !inInspection)
            {
                Snack("Aviso: el centro del patrón no está dentro de la zona de búsqueda ni de la zona de inspección.");
            }

            // Guardar imágenes de depuración para verificar coordenadas
            try { SaveDebugRoiImages(pattern, search, _layout.Inspection!); }
            catch { /* no bloquear validación por errores de I/O */ }

            return true;
        }

        private bool ValidateRoiInImage(RoiModel roi)
        {
            if (_imgW <= 0 || _imgH <= 0) { Snack("Carga primero una imagen."); return false; }
            var r = RoiToRect(roi);
            if (r.Width < 2 || r.Height < 2) { Snack("ROI demasiado pequeño."); return false; }
            if (r.X < 0 || r.Y < 0 || r.Right > _imgW || r.Bottom > _imgH)
            {
                Snack("ROI fuera de límites. Se recomienda reajustar.");
                return false;
            }
            return true;
        }

        private WRect RoiToRect(RoiModel r)
        {
            if (r.Shape == RoiShape.Rectangle) return new WRect(r.Left, r.Top, r.Width, r.Height);
            var ro = r.R; return new WRect(r.CX - ro, r.CY - ro, 2 * ro, 2 * ro);
        }

        // ====== Analizar Master / ROI ======
        // --------- BOTÓN ANALIZAR MASTERS ---------
        // ===== En MainWindow.xaml.cs =====
        private async Task AnalyzeMastersAsync()
        {
            AppendLog("[ANALYZE] Begin AnalyzeMastersAsync");
            AppendLog("[FLOW] Entrando en AnalyzeMastersAsync");

            // Limpia cruces, mantiene ROIs
            ResetAnalysisMarks();

            WPoint? c1 = null, c2 = null;
            double s1 = 0, s2 = 0;

            // 1) Intento local primero (opcional)
            if (ChkUseLocalMatcher.IsChecked == true)
            {
                    AppendLog("[ANALYZE] Using local matcher first...");
                    try
                    {
                        AppendLog("[FLOW] Usando matcher local");
                        using var img = Cv.Cv2.ImRead(_currentImagePathWin);
                        Mat? m1Override = null;
                        Mat? m2Override = null;
                        try
                        {
                            if (_layout.Master1Pattern != null)
                                m1Override = TryLoadMasterPatternOverride(_layout.Master1PatternImagePath, "M1");
                            if (_layout.Master2Pattern != null)
                                m2Override = TryLoadMasterPatternOverride(_layout.Master2PatternImagePath, "M2");

                            var res1 = LocalMatcher.MatchInSearchROI(img, _layout.Master1Pattern, _layout.Master1Search,
                                _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax, m1Override,
                                LogToFileAndUI);
                            if (res1.center.HasValue) { c1 = new WPoint(res1.center.Value.X, res1.center.Value.Y); s1 = res1.score; }
                            else AppendLog("[LOCAL] M1 no encontrado");

                            var res2 = LocalMatcher.MatchInSearchROI(img, _layout.Master2Pattern, _layout.Master2Search,
                                _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax, m2Override,
                                LogToFileAndUI);
                            if (res2.center.HasValue) { c2 = new WPoint(res2.center.Value.X, res2.center.Value.Y); s2 = res2.score; }
                            else AppendLog("[LOCAL] M2 no encontrado");
                        }
                        finally
                        {
                            m1Override?.Dispose();
                            m2Override?.Dispose();
                        }
                    }
                    catch (DllNotFoundException ex)
                    {
                        AppendLog("[OpenCV] DllNotFound: " + ex.Message);
                        Snack("OpenCvSharp no está disponible. Desactivo 'matcher local'.");
                    ChkUseLocalMatcher.IsChecked = false;
                }
                catch (Exception ex)
                {
                    AppendLog("[local matcher] ERROR: " + ex.Message);
                }
            }

            // 2) Backend si falta alguno
            if (c1 is null || c2 is null)
            {
                AppendLog("[FLOW] Usando backend /infer para los masters");

                if (c1 is null)
                {
                    var inferM1 = await BackendAPI.InferAsync(_currentImagePathWin, _layout.Master1Pattern!, _preset, AppendLog);
                    if (inferM1.ok && inferM1.result != null)
                    {
                        var result = inferM1.result;
                        var (cx, cy) = _layout.Master1Pattern!.GetCenter();
                        c1 = new WPoint(cx, cy);
                        s1 = result.score;
                        string thrText = result.threshold.HasValue ? result.threshold.Value.ToString("0.###") : "n/a";
                        bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
                        AppendLog($"[BACKEND] M1 infer score={result.score:0.###} thr={thrText} regions={(result.regions?.Length ?? 0)} status={(pass ? "OK" : "NG")}");
                    }
                    else
                    {
                        AppendLog("[BACKEND] M1 FAIL :: " + (inferM1.error ?? "unknown"));
                    }
                }

                if (c2 is null)
                {
                    var inferM2 = await BackendAPI.InferAsync(_currentImagePathWin, _layout.Master2Pattern!, _preset, AppendLog);
                    if (inferM2.ok && inferM2.result != null)
                    {
                        var result = inferM2.result;
                        var (cx, cy) = _layout.Master2Pattern!.GetCenter();
                        c2 = new WPoint(cx, cy);
                        s2 = result.score;
                        string thrText = result.threshold.HasValue ? result.threshold.Value.ToString("0.###") : "n/a";
                        bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
                        AppendLog($"[BACKEND] M2 infer score={result.score:0.###} thr={thrText} regions={(result.regions?.Length ?? 0)} status={(pass ? "OK" : "NG")}");
                    }
                    else
                    {
                        AppendLog("[BACKEND] M2 FAIL :: " + (inferM2.error ?? "unknown"));
                    }
                }
            }


            // 3) Manejo de fallo
            if (c1 is null)
            {
                Snack("No se ha encontrado Master 1 en su zona de búsqueda");
                AppendLog("[FLOW] c1 null");
                return;
            }
            if (c2 is null)
            {
                Snack("No se ha encontrado Master 2 en su zona de búsqueda");
                AppendLog("[FLOW] c2 null");
                return;
            }

            // 4) Dibujar cruces siempre para la imagen actual
            var mid = new WPoint((c1.Value.X + c2.Value.X) / 2.0, (c1.Value.Y + c2.Value.Y) / 2.0);
            AppendLog($"[FLOW] mid=({mid.X:0.##},{mid.Y:0.##})");

            EnterAnalysisView();

            string master1Caption = ResolveRoiLabelText(_layout.Master1Pattern!) ?? "Master 1";
            string master2Caption = ResolveRoiLabelText(_layout.Master2Pattern!) ?? "Master 2";

            DrawMasterMatch(_layout.Master1Pattern!, c1.Value, $"{master1Caption} match", WBrushes.LimeGreen, withLabel: true);
            DrawMasterMatch(_layout.Master2Pattern!, c2.Value, $"{master2Caption} match", WBrushes.Red, withLabel: true);

            // 5) Reubicar inspección si existe
            if (_layout.Inspection == null)
            {
                Snack("Masters OK. Falta ROI de Inspección: dibújalo y guarda. Las cruces ya están dibujadas.");
                AppendLog("[FLOW] Inspection null");
                _state = MasterState.DrawInspection;
                UpdateWizardState();
                return;
            }

            MoveInspectionTo(_layout.Inspection, c1.Value, c2.Value);
            ClipInspectionROI(_layout.Inspection, _imgW, _imgH);
            AppendLog("[FLOW] Inspection movida y recortada");

            MasterLayoutManager.Save(_preset, _layout);
            AppendLog("[FLOW] Layout guardado");

            Snack($"Masters OK. Scores: M1={s1:0.000}, M2={s2:0.000}. ROI inspección reubicado.");
            _state = MasterState.Ready;
            UpdateWizardState();
            AppendLog("[FLOW] AnalyzeMastersAsync terminado");
        }












        // Log seguro desde cualquier hilo
        private void AppendLog(string line)
        {
            LogToFileAndUI(line);
        }

        private void AppendLogBulk(IEnumerable<string> lines)
        {
            foreach (var entry in lines)
            {
                LogToFileAndUI(entry);
            }
        }

        private void LogToFileAndUI(string message)
        {
            var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

            try
            {
                lock (_fileLogLock)
                {
                    var directory = System.IO.Path.GetDirectoryName(_fileLogPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    System.IO.File.AppendAllText(_fileLogPath, stamped + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // ignore file logging errors
            }

            if (TrainLogText == null)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                var copy = stamped;
                Dispatcher.BeginInvoke(new Action(() => AppendLogLine(copy)));
                return;
            }

            AppendLogLine(stamped);
        }

        private void AppendLogLine(string line)
        {
            if (TrainLogText == null)
            {
                return;
            }

            string entry = line + Environment.NewLine;
            TrainLogText.AppendText(entry);
            _logLineLengths.Enqueue(entry.Length);
            _logCharCount += entry.Length;

            TrimLogIfNeeded();

            TrainLogText.ScrollToEnd();
        }

        private void TrimLogIfNeeded()
        {
            if (TrainLogText == null)
            {
                return;
            }

            if (_logCharCount <= MaxLogCharacters)
            {
                return;
            }

            while (_logCharCount > TrimmedLogCharacters && _logLineLengths.Count > 0)
            {
                int expectedLength = _logLineLengths.Dequeue();
                int available = TrainLogText.Text.Length;
                if (available <= 0)
                {
                    _logCharCount = 0;
                    _logLineLengths.Clear();
                    break;
                }

                int removeLength = Math.Min(expectedLength, available);
                TrainLogText.Select(0, removeLength);
                TrainLogText.SelectedText = string.Empty;
                _logCharCount -= removeLength;

                if (removeLength < expectedLength)
                {
                    _logLineLengths.Clear();
                    _logCharCount = TrainLogText.Text.Length;
                    break;
                }
            }

            TrainLogText.CaretIndex = TrainLogText.Text.Length;
            _logCharCount = TrainLogText.Text.Length;
        }

        // --------- AppendLog (para evitar CS0119 en invocaciones) ---------

        private void MoveInspectionTo(RoiModel insp, WPoint master1, WPoint master2)
        {
            if (insp == null)
                return;

            var baseline = GetInspectionBaselineClone() ?? insp.Clone();

            InspectionAlignmentHelper.MoveInspectionTo(
                insp,
                baseline,
                _layout?.Master1Pattern,
                _layout?.Master2Pattern,
                master1,
                master2);

            SyncCurrentRoiFromInspection(insp);
        }

        private RoiModel? GetInspectionBaselineClone()
        {
            return _layout?.InspectionBaseline?.Clone();
        }

        private void SetInspectionBaseline(RoiModel? source)
        {
            if (_layout == null)
                return;

            _layout.InspectionBaseline = source?.Clone();
        }

        private void EnsureInspectionBaselineInitialized()
        {
            if (_layout?.InspectionBaseline == null && _layout?.Inspection != null)
            {
                SetInspectionBaseline(_layout.Inspection);
            }
        }

        private void ClipInspectionROI(RoiModel insp, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return;
            if (insp.Shape == RoiShape.Rectangle)
            {
                if (insp.Width < 1) insp.Width = 1;
                if (insp.Height < 1) insp.Height = 1;
                double left = Math.Max(0, insp.Left);
                double top = Math.Max(0, insp.Top);
                double right = Math.Min(imgW, left + insp.Width);
                double bottom = Math.Min(imgH, top + insp.Height);

                double newWidth = Math.Max(1, right - left);
                double newHeight = Math.Max(1, bottom - top);

                insp.Width = newWidth;
                insp.Height = newHeight;
                insp.Left = Math.Max(0, Math.Min(left, imgW - newWidth));
                insp.Top = Math.Max(0, Math.Min(top, imgH - newHeight));
            }
            else
            {
                var ro = insp.R; var ri = insp.RInner;
                if (insp.Shape == RoiShape.Annulus)
                {
                    if (ro < 2) ro = 2;
                    if (ri < 1) ri = 1;
                    if (ri >= ro) ri = ro - 1;
                    insp.R = ro; insp.RInner = ri;
                }
                if (insp.CX < ro) insp.CX = ro;
                if (insp.CY < ro) insp.CY = ro;
                if (insp.CX > imgW - ro) insp.CX = imgW - ro;
                if (insp.CY > imgH - ro) insp.CY = imgH - ro;
            }

            SyncCurrentRoiFromInspection(insp);
        }



        private RoiModel BuildCurrentRoiModel(RoiRole? roleOverride = null)
        {
            var model = new RoiModel
            {
                Shape = CurrentRoi.Shape,
                AngleDeg = CurrentRoi.AngleDeg,
                Role = roleOverride ?? RoiRole.Inspection
            };

            if (!string.IsNullOrWhiteSpace(CurrentRoi.Legend))
            {
                model.Label = CurrentRoi.Legend;
            }

            if (CurrentRoi.Shape == RoiShape.Rectangle)
            {
                model.X = CurrentRoi.X;
                model.Y = CurrentRoi.Y;
                model.Width = Math.Max(1.0, CurrentRoi.Width);
                model.Height = Math.Max(1.0, CurrentRoi.Height);
                model.CX = model.X;
                model.CY = model.Y;
                model.R = Math.Max(model.Width, model.Height) / 2.0;
                model.RInner = 0;
            }
            else
            {
                model.CX = CurrentRoi.CX;
                model.CY = CurrentRoi.CY;
                model.R = Math.Max(1.0, CurrentRoi.R);
                model.Width = model.R * 2.0;
                model.Height = model.Width;
                model.X = model.CX;
                model.Y = model.CY;
                model.RInner = CurrentRoi.Shape == RoiShape.Annulus
                    ? AnnulusDefaults.ClampInnerRadius(CurrentRoi.RInner, model.R)
                    : 0;
                if (CurrentRoi.Shape == RoiShape.Annulus && CurrentRoi.RInner > 0)
                {
                    model.Height = Math.Max(model.Height, CurrentRoi.R * 2.0);
                }
            }

            var role = roleOverride ?? _layout.Inspection?.Role ?? GetCurrentStateRole() ?? RoiRole.Inspection;
            model.Role = role;
            return model;
        }

        private Mat GetRotatedCrop(Mat source)
        {
            if (source == null || source.Empty())
                return new Mat();

            CurrentRoi.EnforceMinSize(10, 10);
            var currentModel = BuildCurrentRoiModel();
            if (!RoiCropUtils.TryBuildRoiCropInfo(currentModel, out var info))
                return new Mat();

            if (RoiCropUtils.TryGetRotatedCrop(source, info, currentModel.AngleDeg, out var crop, out _))
                return crop;

            return new Mat();
        }

        private bool LooksLikeCanvasCoords(RoiModel roi)
        {
            if (roi == null)
                return false;

            var disp = GetImageDisplayRect();
            if (disp.Width <= 0 || disp.Height <= 0)
                return false;

            var (pw, ph) = GetImagePixelSize();

            double width = roi.Shape == RoiShape.Rectangle ? roi.Width : Math.Max(1.0, roi.R * 2.0);
            double height = roi.Shape == RoiShape.Rectangle
                ? roi.Height
                : (roi.Shape == RoiShape.Annulus && roi.Height > 0 ? roi.Height : Math.Max(1.0, roi.R * 2.0));
            double left = roi.Shape == RoiShape.Rectangle ? roi.Left : roi.CX - width / 2.0;
            double top = roi.Shape == RoiShape.Rectangle ? roi.Top : roi.CY - height / 2.0;

            bool withinCanvas = left >= -1 && top >= -1 && width <= disp.Width + 2 && height <= disp.Height + 2;
            bool clearlyNotImageScale = width > pw + 2 || height > ph + 2;
            return withinCanvas && clearlyNotImageScale;
        }

        private Mat GetUiMatOrReadFromDisk()
        {
            if (ImgMain?.Source is BitmapSource bs)
            {
                return BitmapSourceConverter.ToMat(bs);
            }

            if (!string.IsNullOrWhiteSpace(_currentImagePathWin))
            {
                var mat = Cv2.ImRead(_currentImagePathWin, ImreadModes.Unchanged);
                if (!mat.Empty())
                    return mat;
            }

            throw new InvalidOperationException("No hay imagen disponible para exportar el ROI.");
        }

        private string EnsureAndGetPreviewDir()
        {
            var imgDir = Path.GetDirectoryName(_currentImagePathWin) ?? string.Empty;
            var previewDir = Path.Combine(imgDir, "roi_previews");
            Directory.CreateDirectory(previewDir);
            return previewDir;
        }

        private string EnsureAndGetMasterPatternDir()
        {
            var layoutPath = MasterLayoutManager.GetDefaultPath(_preset);
            var layoutDir = Path.GetDirectoryName(layoutPath);
            if (string.IsNullOrEmpty(layoutDir))
                layoutDir = _preset.Home;
            var masterDir = Path.Combine(layoutDir!, "master_patterns");
            Directory.CreateDirectory(masterDir);
            return masterDir;
        }

        private bool TryBuildRoiCrop(RoiModel roi, string logTag, out Mat? cropWithAlpha,
            out RoiCropInfo cropInfo, out Cv.Rect cropRect)
        {
            cropWithAlpha = null;
            cropInfo = default;
            cropRect = default;

            try
            {
                if (roi == null)
                {
                    AppendLog($"[{logTag}] ROI == null");
                    return false;
                }

                var roiImage = LooksLikeCanvasCoords(roi) ? CanvasToImage(roi) : roi.Clone();

                using var src = GetUiMatOrReadFromDisk();
                if (src.Empty())
                {
                    AppendLog($"[{logTag}] Imagen fuente vacía.");
                    return false;
                }

                if (!RoiCropUtils.TryBuildRoiCropInfo(roiImage, out cropInfo))
                {
                    AppendLog($"[{logTag}] ROI no soportado para recorte.");
                    return false;
                }

                if (!RoiCropUtils.TryGetRotatedCrop(src, cropInfo, roiImage.AngleDeg, out var cropMat, out var cropRectLocal))
                {
                    AppendLog($"[{logTag}] No se pudo obtener el recorte rotado.");
                    return false;
                }

                Mat? alphaMask = null;
                try
                {
                    alphaMask = RoiCropUtils.BuildRoiMask(cropInfo, cropRectLocal);
                    cropWithAlpha = RoiCropUtils.ConvertCropToBgra(cropMat, alphaMask);
                    cropRect = cropRectLocal;
                    return true;
                }
                finally
                {
                    alphaMask?.Dispose();
                    cropMat.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[{logTag}] error: " + ex.Message);
                return false;
            }
        }

        private void SaveRoiCropPreview(RoiModel roi, string tag)
        {
            if (!TryBuildRoiCrop(roi, "preview", out var cropWithAlpha, out var cropInfo, out var cropRect))
                return;

            using (cropWithAlpha)
            {
                var outDir = EnsureAndGetPreviewDir();
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                string fname = $"{tag}_{ts}.png";
                var outPath = Path.Combine(outDir, fname);

                Cv2.ImWrite(outPath, cropWithAlpha);
                AppendLog($"[preview] Guardado {fname} ROI=({cropInfo.Left:0.#},{cropInfo.Top:0.#},{cropInfo.Width:0.#},{cropInfo.Height:0.#}) " +
                          $"crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) ang={roi.AngleDeg:0.##}");
            }
        }

        private string? SaveMasterPatternCanonical(RoiModel roi, string fileNameBase)
        {
            if (!TryBuildRoiCrop(roi, "master", out var cropWithAlpha, out var cropInfo, out var cropRect))
                return null;

            using (cropWithAlpha)
            {
                var dir = EnsureAndGetMasterPatternDir();
                var fileName = fileNameBase + ".png";
                var outPath = Path.Combine(dir, fileName);
                Cv2.ImWrite(outPath, cropWithAlpha);
                AppendLog($"[master] Guardado {fileName} ROI=({cropInfo.Left:0.#},{cropInfo.Top:0.#},{cropInfo.Width:0.#},{cropInfo.Height:0.#}) " +
                          $"crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) ang={roi.AngleDeg:0.##}");
                return outPath;
            }
        }

        private Mat? TryLoadMasterPatternOverride(string? path, string tag)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                if (!File.Exists(path))
                {
                    AppendLog($"[master] PNG de patrón {tag} no encontrado: {path}");
                    return null;
                }

                var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
                if (mat.Empty())
                {
                    mat.Dispose();
                    AppendLog($"[master] PNG de patrón {tag} vacío: {path}");
                    return null;
                }

                return mat;
            }
            catch (Exception ex)
            {
                AppendLog($"[master] Error cargando patrón {tag}: {ex.Message}");
                return null;
            }
        }

        private async Task<Workflow.RoiExportResult?> ExportCurrentRoiCanonicalAsync()
        {
            RoiModel? roiImage = null;
            string? imagePath = null;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_tmpBuffer != null && _tmpBuffer.Role == RoiRole.Inspection)
                {
                    roiImage = _tmpBuffer.Clone();
                }
                else if (_layout.Inspection != null)
                {
                    roiImage = _layout.Inspection.Clone();
                }
                else
                {
                    roiImage = BuildCurrentRoiModel(RoiRole.Inspection);
                }

                imagePath = _currentImagePathWin;
            });

            if (roiImage == null)
            {
                Snack("No hay ROI de inspección definido.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                Snack("Carga primero una imagen válida.");
                return null;
            }

            return await Task.Run(() =>
            {
                var roiClone = roiImage!.Clone();
                if (!BackendAPI.TryPrepareCanonicalRoi(imagePath!, roiClone, out var payload, out _, AppendLog) || payload == null)
                {
                    AppendLog("[export] canonical ROI preparation failed");
                    return null;
                }

                var shapeJson = payload.ShapeJson ?? string.Empty;
                return new Workflow.RoiExportResult(payload.PngBytes, shapeJson, roiClone);
            }).ConfigureAwait(false);
        }

        private async Task<bool> VerifyPathsAndConnectivityAsync()
        {
            AppendLog("== VERIFY: comenzando verificación de paths/IP ==");
            bool ok = true;

            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            {
                Snack("Imagen no válida o no existe. Carga una imagen primero.");
                ok = false;
            }
            else
            {
                try
                {
                    using var bmp = new System.Drawing.Bitmap(_currentImagePathWin);
                    AppendLog($"[VERIFY] Imagen OK: {bmp.Width}x{bmp.Height}");
                }
                catch (Exception ex)
                {
                    Snack("No se pudo abrir la imagen: " + ex.Message);
                    ok = false;
                }
            }

            try
            {
                var uri = new Uri(BackendAPI.BaseUrl);
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                {
                    Snack("BaseUrl no es http/https");
                    ok = false;
                }
                AppendLog($"[VERIFY] BaseUrl OK: {uri}");
            }
            catch (Exception ex)
            {
                Snack("BaseUrl inválida: " + ex.Message);
                ok = false;
            }

            var url = BackendAPI.BaseUrl.TrimEnd('/') + "/" + BackendAPI.TrainStatusEndpoint.TrimStart('/');
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await hc.GetAsync(url).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                AppendLog($"[VERIFY] GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (resp.IsSuccessStatusCode)
                {
                    AppendLog($"[VERIFY] train_status body (tail): {body.Substring(0, Math.Min(body.Length, 200))}");
                }
                else
                {
                    Snack($"El backend respondió {resp.StatusCode} en /train_status");
                }
            }
            catch (Exception ex)
            {
                Snack("No hay conexión con el backend: " + ex.Message);
                ok = false;
            }

            AppendLog("== VERIFY: fin verificación ==");
            return ok;
        }
        private void LogPathSnapshot()
        {
            AppendLog("========== PATH SNAPSHOT ==========");
            try
            {
                AppendLog($"[CFG] BaseUrl={BackendAPI.BaseUrl}");
                AppendLog($"[CFG] InferEndpoint={BackendAPI.InferEndpoint} TrainStatusEndpoint={BackendAPI.TrainStatusEndpoint}");
                AppendLog($"[CFG] DefaultMmPerPx={BackendAPI.DefaultMmPerPx:0.###}");
                var exists = !string.IsNullOrWhiteSpace(_currentImagePathWin) && File.Exists(_currentImagePathWin);
                AppendLog($"[IMG] _currentImagePathWin='{_currentImagePathWin}'  exists={exists}");

                if (_layout.Master1Pattern != null)
                    AppendLog($"[ROI] M1 Pattern  {DescribeRoi(_layout.Master1Pattern)}");
                if (_layout.Master1Search != null)
                    AppendLog($"[ROI] M1 Search   {DescribeRoi(_layout.Master1Search)}");
                if (_layout.Master2Pattern != null)
                    AppendLog($"[ROI] M2 Pattern  {DescribeRoi(_layout.Master2Pattern)}");
                if (_layout.Master2Search != null)
                    AppendLog($"[ROI] M2 Search   {DescribeRoi(_layout.Master2Search)}");
                if (_layout.Inspection != null)
                    AppendLog($"[ROI] Inspection  {DescribeRoi(_layout.Inspection)}");

                AppendLog($"[PRESET] Feature='{_preset.Feature}' Thr={_preset.MatchThr} RotRange={_preset.RotRange} Scale=[{_preset.ScaleMin:0.###},{_preset.ScaleMax:0.###}] MmPerPx={_preset.MmPerPx:0.###}");
            }
            catch (Exception ex)
            {
                AppendLog("[SNAPSHOT] ERROR: " + ex.Message);
            }
            AppendLog("===================================");
        }

        private async void BtnAnalyzeMaster_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("[UI] BtnAnalyzeMaster_Click");

            // 1) (opcional) snapshot/verificación que ya tienes
            LogPathSnapshot();
            if (!await VerifyPathsAndConnectivityAsync())
            {
                AppendLog("[VERIFY] Falló verificación. Abortando Analyze.");
                return;
            }

            // 2) limpiar cruces de análisis anteriores (no borra los ROIs)
            ResetAnalysisMarks();

            // 3) Validaciones rápidas
            if (string.IsNullOrWhiteSpace(_currentImagePathWin))
            {
                Snack("No hay imagen actual"); return;
            }
            if (_layout.Master1Pattern == null || _layout.Master1Search == null ||
                _layout.Master2Pattern == null || _layout.Master2Search == null)
            {
                Snack("Faltan ROIs Master"); return;
            }

            // 4) Leer preset desde la UI
            SyncPresetFromUI();

            // 5) Lanzar análisis
            _ = AnalyzeMastersAsync();
        }



        private async void BtnAnalyzeROI_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentImagePathWin)) { Snack("No hay imagen actual"); return; }
            if (_layout.Inspection == null) { Snack("Falta ROI de Inspección"); return; }


            var resp = await BackendAPI.InferAsync(_currentImagePathWin, _layout.Inspection, _preset, AppendLog);
            if (!resp.ok || resp.result == null)
            {
                Snack(resp.error ?? "Error en Analyze");
                return;
            }

            var result = resp.result;
            bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
            string thrText = result.threshold.HasValue ? result.threshold.Value.ToString("0.###") : "n/a";
            Snack(pass
                ? $"Resultado OK (score={result.score:0.000}, thr={thrText})"
                : $"Resultado NG (score={result.score:0.000}, thr={thrText})");
        }



        // ====== Overlay persistente + Adorner ======
        private void OnRoiChanged(Shape shape, RoiModel roi)
        {
            MasterLayoutManager.Save(_preset, _layout);
            AppendLog($"[adorner] ROI actualizado: {roi.Role} => {DescribeRoi(roi)}");
        }


        // ====== Preset/Layout ======
        private static double ParseDoubleOrDefault(string? text, double defaultValue)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return value;
            return defaultValue;
        }

        private static string NormalizeFeature(string? feature)
        {
            return string.IsNullOrWhiteSpace(feature)
                ? "auto"
                : feature.Trim().ToLowerInvariant();
        }

        private static string GetFeatureLabel(object? item)
        {
            return item switch
            {
                ComboBoxItem cbi => (cbi.Content?.ToString() ?? string.Empty).Trim(),
                _ => (item?.ToString() ?? string.Empty).Trim()
            };
        }

        private string ReadFeatureFromUI()
        {
            return NormalizeFeature(GetFeatureLabel(ComboFeature.SelectedItem));
        }

        private void SetFeatureSelection(string feature)
        {
            string normalized = NormalizeFeature(feature);
            foreach (var item in ComboFeature.Items)
            {
                if (NormalizeFeature(GetFeatureLabel(item)) == normalized)
                {
                    ComboFeature.SelectedItem = item;
                    return;
                }
            }
            if (ComboFeature.Items.Count > 0 && ComboFeature.SelectedIndex < 0)
            {
                ComboFeature.SelectedIndex = 0;
            }
        }

        private void ApplyPresetToUI(PresetFile preset)
        {
            preset.Feature = NormalizeFeature(preset.Feature);
            TxtThr.Text = preset.MatchThr.ToString(CultureInfo.InvariantCulture);
            TxtRot.Text = preset.RotRange.ToString(CultureInfo.InvariantCulture);
            TxtSMin.Text = preset.ScaleMin.ToString(CultureInfo.InvariantCulture);
            TxtSMax.Text = preset.ScaleMax.ToString(CultureInfo.InvariantCulture);
            SetFeatureSelection(preset.Feature);
        }

        private void SyncPresetFromUI()
        {
            _preset.MatchThr = (int)Math.Round(ParseDoubleOrDefault(TxtThr.Text, _preset.MatchThr));
            _preset.RotRange = (int)Math.Round(ParseDoubleOrDefault(TxtRot.Text, _preset.RotRange));
            _preset.ScaleMin = ParseDoubleOrDefault(TxtSMin.Text, _preset.ScaleMin);
            _preset.ScaleMax = ParseDoubleOrDefault(TxtSMax.Text, _preset.ScaleMax);
            _preset.Feature = ReadFeatureFromUI();
        }

        private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            SyncPresetFromUI();
            var s = new SaveFileDialog { Filter = "Preset JSON|*.json", FileName = "preset.json" };
            if (s.ShowDialog() == true) PresetManager.Save(_preset, s.FileName);
            Snack("Preset guardado.");
        }

        private void BtnLoadPreset_Click(object sender, RoutedEventArgs e)
        {
            var o = new OpenFileDialog { Filter = "Preset JSON|*.json" };
            if (o.ShowDialog() != true) return;
            _preset = PresetManager.Load(o.FileName);
            ApplyPresetToUI(_preset);
            _layout = MasterLayoutManager.LoadOrNew(_preset);
            EnsureInspectionBaselineInitialized();
            ResetAnalysisMarks();
            UpdateWizardState();
            Snack("Preset cargado.");
        }

        private void BtnSaveLayout_Click(object sender, RoutedEventArgs e)
        {
            MasterLayoutManager.Save(_preset, _layout);
            Snack("Layout guardado.");
        }

        private void BtnLoadLayout_Click(object sender, RoutedEventArgs e)
        {
            _layout = MasterLayoutManager.LoadOrNew(_preset);
            EnsureInspectionBaselineInitialized();
            ResetAnalysisMarks();
            Snack("Layout cargado.");
            UpdateWizardState();
        }

        // ====== Logs / Polling ======
        private void InitTrainPollingTimer()
        {
            _trainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _trainTimer.Tick += async (s, e) =>
            {
                try
                {
                    var url = BackendAPI.BaseUrl.TrimEnd('/') + BackendAPI.TrainStatusEndpoint;
                    using var hc = new System.Net.Http.HttpClient();
                    var resp = await hc.GetAsync(url);
                    var text = await resp.Content.ReadAsStringAsync();
                    AppendLog("[train_status] " + text.Trim());
                }
                catch (Exception ex)
                {
                    AppendLog("[train_status] ERROR " + ex.Message);
                }
            };
            // _trainTimer.Start(); // opcional
        }

        private void Snack(string msg)
        {
            AppendLog("[INFO] " + msg);
            System.Diagnostics.Debug.WriteLine(msg);
        }

        private void SyncModelFromShape(Shape shape)
        {
            if (shape.Tag is not RoiModel roiCanvas) return;

            var x = Canvas.GetLeft(shape);
            var y = Canvas.GetTop(shape);
            var w = shape.Width;
            var h = shape.Height;

            double? renderAngle = null;
            if (shape.RenderTransform is RotateTransform rotateTransform)
            {
                renderAngle = rotateTransform.Angle;
            }
            else if (shape.RenderTransform is TransformGroup transformGroup)
            {
                var rotate = transformGroup.Children.OfType<RotateTransform>().FirstOrDefault();
                if (rotate != null)
                    renderAngle = rotate.Angle;
            }

            if (renderAngle.HasValue)
            {
                roiCanvas.AngleDeg = renderAngle.Value;
            }

            if (shape is System.Windows.Shapes.Rectangle)
            {
                roiCanvas.Shape = RoiShape.Rectangle;
                roiCanvas.Width = w;
                roiCanvas.Height = h;
                roiCanvas.Left = x;
                roiCanvas.Top = y;
                roiCanvas.CX = roiCanvas.X;
                roiCanvas.CY = roiCanvas.Y;
                roiCanvas.R = Math.Max(roiCanvas.Width, roiCanvas.Height) / 2.0;
                roiCanvas.RInner = 0;
            }
            else if (shape is AnnulusShape annulusShape)
            {
                double radius = Math.Max(w, h) / 2.0;
                roiCanvas.Shape = RoiShape.Annulus;
                roiCanvas.Width = w;
                roiCanvas.Height = h;
                roiCanvas.R = radius;
                roiCanvas.Left = x;
                roiCanvas.Top = y;
                roiCanvas.CX = roiCanvas.X;
                roiCanvas.CY = roiCanvas.Y;

                double inner = annulusShape.InnerRadius;
                double maxInner = radius > 0 ? radius : Math.Max(w, h) / 2.0;
                inner = Math.Max(0, Math.Min(inner, maxInner));
                roiCanvas.RInner = inner;
                annulusShape.InnerRadius = inner;
            }
            else if (shape is System.Windows.Shapes.Ellipse)
            {
                double radius = Math.Max(w, h) / 2.0;
                roiCanvas.Shape = RoiShape.Circle;
                roiCanvas.Width = w;
                roiCanvas.Height = h;
                roiCanvas.R = radius;
                roiCanvas.Left = x;
                roiCanvas.Top = y;
                roiCanvas.CX = roiCanvas.X;
                roiCanvas.CY = roiCanvas.Y;
                roiCanvas.RInner = 0;
            }

            var roiPixel = CanvasToImage(roiCanvas);

            if (ReferenceEquals(shape, _previewShape))
            {
                _tmpBuffer = roiPixel.Clone();
            }
            else
            {
                UpdateLayoutFromPixel(roiPixel);
            }

            UpdateOverlayFromPixelModel(roiPixel);

            AppendLog($"[model] sync {roiPixel.Role} => {DescribeRoi(roiPixel)}");
        }

        private void UpdateLayoutFromPixel(RoiModel roiPixel)
        {
            var clone = roiPixel.Clone();

            switch (roiPixel.Role)
            {
                case RoiRole.Master1Pattern:
                    _layout.Master1Pattern = clone;
                    break;
                case RoiRole.Master1Search:
                    _layout.Master1Search = clone;
                    break;
                case RoiRole.Master2Pattern:
                    _layout.Master2Pattern = clone;
                    break;
                case RoiRole.Master2Search:
                    _layout.Master2Search = clone;
                    break;
                case RoiRole.Inspection:
                    _layout.Inspection = clone;
                    if (!_analysisViewActive)
                    {
                        SetInspectionBaseline(clone);
                    }
                    SyncCurrentRoiFromInspection(clone);
                    break;
            }

            var currentRole = GetCurrentStateRole();
            if (currentRole.HasValue && roiPixel.Role == currentRole.Value)
            {
                _tmpBuffer = clone.Clone();
            }
        }

        private void ApplyPixelModelToCurrentRoi(RoiModel pixelModel)
        {
            if (pixelModel == null)
                return;

            if (pixelModel.Shape == RoiShape.Rectangle)
            {
                CurrentRoi.Shape = RoiShape.Rectangle;
                CurrentRoi.SetCenter(pixelModel.X, pixelModel.Y);
                CurrentRoi.Width = pixelModel.Width;
                CurrentRoi.Height = pixelModel.Height;
                CurrentRoi.R = Math.Max(CurrentRoi.Width, CurrentRoi.Height) / 2.0;
                CurrentRoi.RInner = 0;
            }
            else
            {
                var shape = pixelModel.Shape == RoiShape.Annulus ? RoiShape.Annulus : RoiShape.Circle;
                double radius = pixelModel.R > 0 ? pixelModel.R : Math.Max(pixelModel.Width, pixelModel.Height) / 2.0;
                if (radius <= 0)
                {
                    radius = Math.Max(CurrentRoi.R, Math.Max(CurrentRoi.Width, CurrentRoi.Height) / 2.0);
                }

                double diameter = radius * 2.0;
                CurrentRoi.Shape = shape;
                CurrentRoi.SetCenter(pixelModel.CX, pixelModel.CY);
                CurrentRoi.Width = diameter;
                CurrentRoi.Height = diameter;
                CurrentRoi.R = radius;

                if (shape == RoiShape.Annulus)
                {
                    double innerCandidate = pixelModel.RInner;
                    if (innerCandidate <= 0 && CurrentRoi.RInner > 0)
                    {
                        innerCandidate = CurrentRoi.RInner;
                    }

                    double inner = innerCandidate > 0
                        ? AnnulusDefaults.ClampInnerRadius(innerCandidate, radius)
                        : AnnulusDefaults.ResolveInnerRadius(innerCandidate, radius);
                    CurrentRoi.RInner = inner;
                }
                else
                {
                    CurrentRoi.RInner = 0;
                }
            }

            CurrentRoi.AngleDeg = pixelModel.AngleDeg;

            var legend = ResolveRoiLabelText(pixelModel);
            if (!string.IsNullOrWhiteSpace(legend))
            {
                CurrentRoi.Legend = legend!;
            }
        }

        private void UpdateOverlayFromCurrentRoi()
        {
            if (RoiOverlay == null)
                return;

            RoiOverlay.Roi = CurrentRoi;
            RoiOverlay.InvalidateOverlay();
        }

        private void UpdateOverlayFromPixelModel(RoiModel pixelModel)
        {
            if (pixelModel == null)
                return;

            ApplyPixelModelToCurrentRoi(pixelModel);
            UpdateOverlayFromCurrentRoi();
        }

        private void SyncCurrentRoiFromInspection(RoiModel inspectionPixel)
        {
            if (inspectionPixel == null) return;

            ApplyPixelModelToCurrentRoi(inspectionPixel);
            UpdateInspectionShapeRotation(CurrentRoi.AngleDeg);
            UpdateOverlayFromCurrentRoi();
            RefreshHeatmapOverlay();
        }

        private void InvalidateAdornerFor(Shape shape)
        {
            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null) return;
            var ads = layer.GetAdorners(shape);
            if (ads == null) return;
            foreach (var ad in ads.OfType<RoiAdorner>())
            {
                ad.InvalidateArrange(); // recoloca thumbs en la nueva bbox
            }
        }

        // =============================================================
        // Guarda imágenes de depuración (patrón, búsqueda, inspección, full)
        // =============================================================
        private void SaveDebugRoiImages(RoiModel pattern, RoiModel search, RoiModel inspection)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !System.IO.File.Exists(_currentImagePathWin)) return;

                string baseDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_currentImagePathWin) ?? "",
                    "debug_rois");
                System.IO.Directory.CreateDirectory(baseDir);

                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                System.Drawing.Bitmap? Crop(RoiModel roi)
                {
                    if (roi == null) return null;
                    var r = RoiToRect(roi);
                    using var src = new System.Drawing.Bitmap(_currentImagePathWin);
                    var rectInt = new System.Drawing.Rectangle(
                        (int)System.Math.Max(0, r.X),
                        (int)System.Math.Max(0, r.Y),
                        (int)System.Math.Min(r.Width, src.Width - r.X),
                        (int)System.Math.Min(r.Height, src.Height - r.Y));
                    if (rectInt.Width <= 0 || rectInt.Height <= 0) return null;
                    return src.Clone(rectInt, src.PixelFormat);
                }

                using (var patBmp = Crop(pattern))
                    patBmp?.Save(System.IO.Path.Combine(baseDir, $"pattern_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);
                using (var seaBmp = Crop(search))
                    seaBmp?.Save(System.IO.Path.Combine(baseDir, $"search_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);
                using (var insBmp = Crop(inspection))
                    insBmp?.Save(System.IO.Path.Combine(baseDir, $"inspection_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);

                using (var full = new System.Drawing.Bitmap(_currentImagePathWin))
                using (var g = System.Drawing.Graphics.FromImage(full))
                using (var penSearch = new System.Drawing.Pen(System.Drawing.Color.Yellow, 2))
                using (var penPattern = new System.Drawing.Pen(System.Drawing.Color.Cyan, 2))
                using (var penInspection = new System.Drawing.Pen(System.Drawing.Color.Lime, 2))
                using (var penCross = new System.Drawing.Pen(System.Drawing.Color.Magenta, 2))
                {
                    if (search != null) g.DrawRectangle(penSearch, ToDrawingRect(RoiToRect(search)));
                    if (pattern != null) g.DrawRectangle(penPattern, ToDrawingRect(RoiToRect(pattern)));
                    if (inspection != null) g.DrawRectangle(penInspection, ToDrawingRect(RoiToRect(inspection)));

                    if (pattern != null)
                    {
                        var r = RoiToRect(pattern);
                        var center = new System.Drawing.PointF((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2));
                        g.DrawLine(penCross, center.X - 20, center.Y, center.X + 20, center.Y);
                        g.DrawLine(penCross, center.X, center.Y - 20, center.X, center.Y + 20);
                    }

                    full.Save(System.IO.Path.Combine(baseDir, $"full_annotated_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Snack("Error guardando imágenes debug: " + ex.Message);
            }
        }

        // Helper: convertir WPF Rect -> System.Drawing.Rectangle
        private static System.Drawing.Rectangle ToDrawingRect(WRect r)
        {
            return new System.Drawing.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }

        // ====== Backend (multipart) helpers ======
        private static byte[] CropTemplatePng(string imagePathWin, WRect rect)
        {
            using var bmp = new System.Drawing.Bitmap(imagePathWin);
            var x = Math.Max(0, (int)rect.X);
            var y = Math.Max(0, (int)rect.Y);
            var w = Math.Max(1, (int)rect.Width);
            var h = Math.Max(1, (int)rect.Height);
            if (x + w > bmp.Width) w = Math.Max(1, bmp.Width - x);
            if (y + h > bmp.Height) h = Math.Max(1, bmp.Height - y);
            using var crop = bmp.Clone(new System.Drawing.Rectangle(x, y, w, h), bmp.PixelFormat);
            using var ms = new System.IO.MemoryStream();
            crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }

        private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1) Validaciones básicas
                var currentFrame = bgrFrame;
                if (currentFrame == null || currentFrame.Empty())
                {
                    MessageBox.Show("No hay imagen cargada.");
                    return;
                }

                // 2) Obtener el crop YA ROTADO desde tu ROI actual
                //    Nota: se asume que tienes implementado GetRotatedCrop(Mat bgr)
                using var crop = GetRotatedCrop(currentFrame);
                if (crop == null || crop.Empty())
                {
                    MessageBox.Show("No se pudo obtener el recorte.");
                    return;
                }

                // 3) Codificar PNG (SIN 'using'; ImEncode devuelve byte[])
                byte[] cropPng = crop.ImEncode(".png");

                // 4) (Opcional) parámetros de anillo perfecto (annulus) si quieres usarlos
                //    Si no usas annulus, deja 'annulus' en null y 'maskPng' en null.
                object annulus = null;
                // bool useAnnulus = false; // habilítalo según tu UI
                // if (useAnnulus)
                // {
                //     annulus = new
                //     {
                //         cx = crop.Width / 2,
                //         cy = crop.Height / 2,
                //         ri = 40,
                //         ro = 60
                //     };
                // }
                // 5) Llamada al backend /infer con el ROI canónico
                string? shapeJson = annulus != null ? System.Text.Json.JsonSerializer.Serialize(annulus) : null;
                var request = new InferRequest("DemoRole", "DemoROI", BackendAPI.DefaultMmPerPx, cropPng)
                {
                    ShapeJson = shapeJson
                };
                var resp = await BackendAPI.InferAsync(request);

                // 6) Mostrar texto (si tienes el TextBlock en XAML)
                if (ResultLabel != null)
                {
                    string thrText = resp.threshold.HasValue ? resp.threshold.Value.ToString("0.###") : "n/a";
                    ResultLabel.Text = $"score={resp.score:F3} / thr {thrText}";
                }

                // 7) Decodificar heatmap y pintarlo en el Image del XAML
                if (!string.IsNullOrWhiteSpace(resp.heatmap_png_base64))
                {
                    var heatBytes = Convert.FromBase64String(resp.heatmap_png_base64);
                    using var heat = OpenCvSharp.Cv2.ImDecode(heatBytes, OpenCvSharp.ImreadModes.Color);

                    if (HeatmapImage != null)
                        HeatmapImage.Source = WriteableBitmapConverter.ToWriteableBitmap(heat);
                }
                else if (HeatmapImage != null)
                {
                    HeatmapImage.Source = null;
                }

                // (Opcional) Log
                // AppendLog?.Invoke($"Infer -> score={resp.score:F3}, thr={resp.threshold?.ToString("0.###") ?? "n/a"}");
            }
            catch (Exception ex)
            {
                // (Opcional) Log
                // AppendLog?.Invoke("[Analyze] EX: " + ex.Message);
                MessageBox.Show("Error en Analyze: " + ex.Message);
            }
        }
        private System.Windows.Point GetCurrentRoiCenterOnCanvas()
        {
            return ImagePxToCanvasPt(CurrentRoi.X, CurrentRoi.Y);
        }

        private (double x, double y) GetCurrentRoiCornerImage(RoiCorner corner)
        {
            double halfW = CurrentRoi.Width / 2.0;
            double halfH = CurrentRoi.Height / 2.0;

            double rawOffsetX = corner switch
            {
                RoiCorner.TopLeft or RoiCorner.BottomLeft => -halfW,
                RoiCorner.TopRight or RoiCorner.BottomRight => halfW,
                _ => 0.0
            };

            double rawOffsetY = corner switch
            {
                RoiCorner.TopLeft or RoiCorner.TopRight => -halfH,
                RoiCorner.BottomLeft or RoiCorner.BottomRight => halfH,
                _ => 0.0
            };

            double angleRad = CurrentRoi.AngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);

            double rotatedX = rawOffsetX * cos - rawOffsetY * sin;
            double rotatedY = rawOffsetX * sin + rawOffsetY * cos;

            return (CurrentRoi.X + rotatedX, CurrentRoi.Y + rotatedY);
        }

        private string BuildShapeLogContext(Shape shape)
        {
            if (shape.Tag is RoiModel roiModel)
            {
                var (pivotCanvasX, pivotCanvasY, pivotLocalX, pivotLocalY, width, height) = GetShapePivotMetrics(shape, roiModel);
                return $"role={roiModel.Role} id={roiModel.Id} angle={roiModel.AngleDeg:0.##} pivotCanvas=({pivotCanvasX:0.##},{pivotCanvasY:0.##}) pivotLocal=({pivotLocalX:0.##},{pivotLocalY:0.##}) size=({width:0.##},{height:0.##})";
            }

            var tagText = shape.Tag != null ? shape.Tag.ToString() : "<null>";
            return $"shape={shape.GetType().Name} tag={tagText}";
        }

        private (double pivotCanvasX, double pivotCanvasY, double pivotLocalX, double pivotLocalY, double width, double height) GetShapePivotMetrics(Shape shape, RoiModel roiModel)
        {
            double width = !double.IsNaN(shape.Width) && shape.Width > 0 ? shape.Width : roiModel.Width;
            double height = !double.IsNaN(shape.Height) && shape.Height > 0 ? shape.Height : roiModel.Height;

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roiModel, width, height);
            double pivotLocalX = pivotLocal.X;
            double pivotLocalY = pivotLocal.Y;
            double pivotCanvasX = left + pivotLocalX;
            double pivotCanvasY = top + pivotLocalY;

            return (pivotCanvasX, pivotCanvasY, pivotLocalX, pivotLocalY, width, height);
        }

        private Shape? FindInspectionShapeOnCanvas()
        {
            if (CanvasROI == null)
            {
                AppendLog("[inspect] CanvasROI missing when searching inspection shape");
                return null;
            }

            if (_state == MasterState.DrawInspection && _previewShape != null)
            {
                AppendLog($"[inspect] using preview inspection shape {BuildShapeLogContext(_previewShape)}");
                return _previewShape;
            }

            var inspectionShapes = CanvasROI.Children
                .OfType<Shape>()
                .Where(shape =>
                    shape.Tag is RoiModel roi &&
                    roi.Role == RoiRole.Inspection)
                .ToList();

            var persisted = inspectionShapes.FirstOrDefault();
            if (persisted != null)
            {
                AppendLog($"[inspect] using persisted inspection shape {BuildShapeLogContext(persisted)}");
                return persisted;
            }

            int totalShapes = CanvasROI.Children.OfType<Shape>().Count();
            AppendLog($"[inspect] no inspection shape found (state={_state}, preview={_previewShape != null}, inspectionCount={inspectionShapes.Count}, totalShapes={totalShapes})");
            return null;
        }

        private void ApplyRoiRotationToShape(Shape shape, double angle)
        {
            if (shape.Tag is not RoiModel roiModel)
                return;

            roiModel.AngleDeg = angle;

            double width = !double.IsNaN(shape.Width) && shape.Width > 0 ? shape.Width : roiModel.Width;
            double height = !double.IsNaN(shape.Height) && shape.Height > 0 ? shape.Height : roiModel.Height;

            if (width <= 0 || height <= 0)
            {
                AppendLog($"[rotate] skip apply {roiModel.Role} width={width:0.##} height={height:0.##} angle={angle:0.##}");
                return;
            }

            var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roiModel, width, height);
            double pivotLocalX = pivotLocal.X;
            double pivotLocalY = pivotLocal.Y;

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            double pivotCanvasX = left + pivotLocalX;
            double pivotCanvasY = top + pivotLocalY;

            if (shape.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = angle;
                rotate.CenterX = pivotLocalX;
                rotate.CenterY = pivotLocalY;
                InvalidateAdornerFor(shape);
            }
            else
            {
                shape.RenderTransform = new RotateTransform(angle, pivotLocalX, pivotLocalY);
                InvalidateAdornerFor(shape);
            }

            AppendLog($"[rotate] apply role={roiModel.Role} shape={roiModel.Shape} pivotLocal=({pivotLocalX:0.##},{pivotLocalY:0.##}) pivotCanvas=({pivotCanvasX:0.##},{pivotCanvasY:0.##}) angle={angle:0.##}");
        }

        private void UpdateInspectionShapeRotation(double angle)
        {
            var inspectionShape = FindInspectionShapeOnCanvas();
            if (inspectionShape == null)
            {
                AppendLog($"[rotate] update skip angle={angle:0.##} target=none");
                return;
            }

            AppendLog($"[rotate] update target angle={angle:0.##} {BuildShapeLogContext(inspectionShape)}");

            ApplyRoiRotationToShape(inspectionShape, angle);

            if (_layout?.Inspection != null)
            {
                _layout.Inspection.AngleDeg = angle;
            }
        }

        // === Helpers para mapear coordenadas ===
        public (int pw, int ph) GetImagePixelSize()
        {
            if (ImgMain?.Source is System.Windows.Media.Imaging.BitmapSource b)
                return (b.PixelWidth, b.PixelHeight);
            return (0, 0);
        }

        /// Rect donde realmente se pinta la imagen dentro del control ImgMain (con letterbox)
        public System.Windows.Rect GetImageDisplayRect()
        {
            double cw = ImgMain.ActualWidth;
            double ch = ImgMain.ActualHeight;
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0 || cw <= 0 || ch <= 0)
                return new System.Windows.Rect(0, 0, 0, 0);

            double scale = Math.Min(cw / pw, ch / ph);
            double w = pw * scale;
            double h = ph * scale;
            double x = (cw - w) * 0.5;
            double y = (ch - h) * 0.5;
            return new System.Windows.Rect(x, y, w, h);
        }

        /// Convierte un punto en píxeles de imagen -> punto en CanvasROI
        private System.Windows.Point ImgToCanvas(System.Windows.Point pImg)
        {
            var displayRect = GetImageDisplayRect();
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0 || displayRect.Width <= 0 || displayRect.Height <= 0)
                return new System.Windows.Point(0, 0);

            double scale = displayRect.Width / pw; // escala uniforme usada al dibujar la imagen
            return new System.Windows.Point(
                displayRect.Left + pImg.X * scale,
                displayRect.Top + pImg.Y * scale);
        }

        /// Convierte un punto en píxeles de imagen -> punto en CanvasROI (coordenadas locales del Canvas)
        private System.Windows.Point ImagePxToCanvasPt(double px, double py)
        {
            var (scale, offsetX, offsetY) = GetImageToCanvasTransform();
            double x = px * scale + offsetX;
            double y = py * scale + offsetY;
            return new System.Windows.Point(x, y);
        }




        private System.Windows.Point CanvasToImage(System.Windows.Point pCanvas)
        {
            var (scale, offsetX, offsetY) = GetImageToCanvasTransform();
            if (scale <= 0) return new System.Windows.Point(0, 0);
            double ix = (pCanvas.X - offsetX) / scale;
            double iy = (pCanvas.Y - offsetY) / scale;
            return new System.Windows.Point(ix, iy);
        }


        private RoiModel CanvasToImage(RoiModel roiCanvas)
        {
            var result = roiCanvas.Clone();
            var (scale, offsetX, offsetY) = GetImageToCanvasTransform();
            if (scale <= 0) return result;

            result.AngleDeg = roiCanvas.AngleDeg;

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = (roiCanvas.X - offsetX) / scale;
                result.Y = (roiCanvas.Y - offsetY) / scale;
                result.Width = roiCanvas.Width / scale;
                result.Height = roiCanvas.Height / scale;

                result.CX = result.X;
                result.CY = result.Y;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = (roiCanvas.CX - offsetX) / scale;
                result.CY = (roiCanvas.CY - offsetY) / scale;

                result.R = roiCanvas.R / scale;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiCanvas.RInner / scale;

                result.X = result.CX;
                result.Y = result.CY;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }
            return result;
        }



        private RoiModel ImageToCanvas(RoiModel roiImage)
        {
            var result = roiImage.Clone();
            var (scale, offsetX, offsetY) = GetImageToCanvasTransform();
            if (scale <= 0) return result;

            result.AngleDeg = roiImage.AngleDeg;

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiImage.X * scale + offsetX;
                result.Y = roiImage.Y * scale + offsetY;
                result.Width = roiImage.Width * scale;
                result.Height = roiImage.Height * scale;

                result.CX = result.X;
                result.CY = result.Y;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiImage.CX * scale + offsetX;
                result.CY = roiImage.CY * scale + offsetY;

                result.R = roiImage.R * scale;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiImage.RInner * scale;

                result.X = result.CX;
                result.Y = result.CY;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }
            return result;
        }



        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen (letterbox) ===
        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen ===
        // === Sincroniza CanvasROI EXACTAMENTE al área visible de la imagen (letterbox) ===
        private void SyncOverlayToImage()
        {
            if (ImgMain == null || CanvasROI == null) return;
            if (ImgMain.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return;

            var displayRect = GetImageDisplayRect();
            if (displayRect.Width <= 0 || displayRect.Height <= 0) return;

            // Coordenadas de ImgMain respecto al mismo padre que CanvasROI
            if (CanvasROI.Parent is not FrameworkElement parent) return;
            var imgTopLeft = ImgMain.TranslatePoint(new System.Windows.Point(0, 0), parent);

            double left = imgTopLeft.X + displayRect.Left;
            double top = imgTopLeft.Y + displayRect.Top;
            double w = displayRect.Width;
            double h = displayRect.Height;

            CanvasROI.HorizontalAlignment = HorizontalAlignment.Left;
            CanvasROI.VerticalAlignment = VerticalAlignment.Top;
            CanvasROI.Margin = new Thickness(left, top, 0, 0);
            CanvasROI.Width = w;
            CanvasROI.Height = h;

            if (RoiOverlay != null)
            {
                RoiOverlay.HorizontalAlignment = HorizontalAlignment.Left;
                RoiOverlay.VerticalAlignment = VerticalAlignment.Top;
                RoiOverlay.Margin = new Thickness(left, top, 0, 0);
                RoiOverlay.Width = w;
                RoiOverlay.Height = h;
                RoiOverlay.SnapsToDevicePixels = true;
                RenderOptions.SetEdgeMode(RoiOverlay, EdgeMode.Aliased);
                RoiOverlay.InvalidateOverlay();
            }

            // Estabilidad visual
            CanvasROI.SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(CanvasROI, EdgeMode.Aliased);

            AppendLog($"[sync] Canvas px=({w:0}x{h:0}) Offset=({left:0},{top:0})  Img={bmp.PixelWidth}x{bmp.PixelHeight}");

            RedrawOverlay();

            // Dentro de SyncOverlayToImage(), al final:
            var disp = GetImageDisplayRect();
            AppendLog($"[sync] set width/height=({disp.Width:0}x{disp.Height:0}) margin=({CanvasROI.Margin.Left:0},{CanvasROI.Margin.Top:0})");
            AppendLog($"[sync] AFTER layout? canvasActual=({CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}) imgActual=({ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0})");
        }

        private void ScheduleSyncOverlay(bool force = false)
        {
            if (force)
            {
                _syncScheduled = false;
                _syncRetryCount = 0;
            }
            if (_syncScheduled) return;

            _syncScheduled = true;
            _syncRetryCount = 0;
            Dispatcher.BeginInvoke(new Action(SyncOverlayAfterLayout),
                System.Windows.Threading.DispatcherPriority.Render);
        }



        private void SyncOverlayAfterLayout()
        {
            var disp0 = GetImageDisplayRect();
            if (disp0.Width <= 0 || disp0.Height <= 0)
            {
                if (_syncRetryCount++ < MaxSyncRetries)
                {
                    AppendLog($"[sync-mismatch] disp=(0x0) canvasActual=({CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}) try={_syncRetryCount}");
                    _syncScheduled = true;
                    Dispatcher.BeginInvoke(new Action(SyncOverlayAfterLayout),
                        System.Windows.Threading.DispatcherPriority.Render);
                }
                else
                {
                    _syncScheduled = false; // no quedar “enganchado”
                }
                return;
            }

            _syncScheduled = false;
            SyncOverlayToImage(); // ← coloca CanvasROI exactamente sobre el letterbox

            var disp = GetImageDisplayRect();
            double dw = Math.Abs(CanvasROI.ActualWidth - disp.Width);
            double dh = Math.Abs(CanvasROI.ActualHeight - disp.Height);

            if ((dw > 0.5 || dh > 0.5) && _syncRetryCount++ < MaxSyncRetries)
            {
                AppendLog($"[sync-mismatch] disp=({disp.Width:0}x{disp.Height:0}) canvasActual=({CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}) try={_syncRetryCount}");
                _syncScheduled = true;
                Dispatcher.BeginInvoke(new Action(SyncOverlayAfterLayout),
                    System.Windows.Threading.DispatcherPriority.Render);
                return;
            }

            // ✅ Alineado: si había redibujo pendiente, hazlo ahora
            if (_overlayNeedsRedraw)
            {
                AppendLog("[sync] overlay pendiente → redibujar ahora");
                RedrawOverlay();
                _overlayNeedsRedraw = false;
            }

            RefreshHeatmapOverlay();
        }



        private (double scale, double offsetX, double offsetY) GetImageToCanvasTransform()
        {
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0)
                return (1.0, 0.0, 0.0);

            var displayRect = GetImageDisplayRect();
            bool overlayAligned = IsOverlayAligned();

            double canvasWidth = 0.0;
            double canvasHeight = 0.0;

            if (overlayAligned)
            {
                canvasWidth = CanvasROI?.ActualWidth ?? CanvasROI?.Width ?? 0.0;
                canvasHeight = CanvasROI?.ActualHeight ?? CanvasROI?.Height ?? 0.0;
            }

            if (!overlayAligned || canvasWidth <= 0 || canvasHeight <= 0)
            {
                canvasWidth = displayRect.Width;
                canvasHeight = displayRect.Height;
            }

            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                // Último recurso: usa las dimensiones actuales del canvas aunque no estén alineadas.
                canvasWidth = CanvasROI?.ActualWidth ?? CanvasROI?.Width ?? 0.0;
                canvasHeight = CanvasROI?.ActualHeight ?? CanvasROI?.Height ?? 0.0;
            }

            if (canvasWidth <= 0 || canvasHeight <= 0)
                return (1.0, 0.0, 0.0);

            double scaleX = canvasWidth / pw;
            double scaleY = canvasHeight / ph;

            if (Math.Abs(scaleX - scaleY) > 0.001)
            {
                AppendLog($"[sync] escala no uniforme detectada canvas=({canvasWidth:0.###}x{canvasHeight:0.###}) px=({pw}x{ph}) scaleX={scaleX:0.#####} scaleY={scaleY:0.#####}");
            }

            // Por construcción (Stretch=Uniform) ambos factores deberían coincidir.
            // Si hay pequeñas discrepancias por redondeo usamos el promedio para
            // mantener la coherencia bidireccional de las conversiones.
            double scale = (scaleX + scaleY) * 0.5;

            const double offsetX = 0.0;
            const double offsetY = 0.0;

            return (scale, offsetX, offsetY);
        }








    }
}
