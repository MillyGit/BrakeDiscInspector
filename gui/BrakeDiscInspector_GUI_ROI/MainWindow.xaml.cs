// Dialogs
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Emit;
using System.Text;
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

        private Action<string>? _uiLog;

        // Cache de la última sincronización del overlay
        private double _canvasLeftPx = 0;
        private double _canvasTopPx = 0;
        private double _canvasWpx = 0;
        private double _canvasHpx = 0;
        private double _sx = 1.0;   // escala imagen->canvas en X
        private double _sy = 1.0;   // escala imagen->canvas en Y


        // === File Logger ===
        private static readonly object _fileLogLock = new object();
        private static string _fileLogPath = string.Empty;
        // Si tu overlay se llama distinto, ajusta esta propiedad (o referencia directa en los métodos).
        // Por ejemplo, si en XAML tienes <Canvas x:Name="Overlay"> usa ese nombre aquí.
        private Canvas OverlayCanvas => CanvasROI;

        private const string ANALYSIS_TAG = "analysis-mark";

        // === Helpers de overlay ===
        private const double LabelOffsetX = 10;   // desplazamiento a la derecha de la cruz
        private const double LabelOffsetY = -20;  // desplazamiento hacia arriba de la cruz

        private ROI CurrentRoi = new ROI { X = 200, Y = 150, Width = 100, Height = 80, AngleDeg = 0, Legend = "M1" };
        private Mat? bgrFrame; // tu frame actual
        private bool UseAnnulus = false;

        private bool _syncScheduled;
        private int _syncRetryCount;
        private const int MaxSyncRetries = 3;
        // overlay diferido
        private bool _overlayNeedsRedraw;
        private bool _analysisViewActive;
        public MainWindow()
        {
            InitializeComponent();
            _preset = PresetManager.LoadOrDefault(_preset);
            _uiLog = s => Dispatcher.BeginInvoke(new Action(() => AppendLog(s)));

            // start file logger early
            InitFileLogger();
            AppendLog($"[LOG] File initialized at '{_fileLogPath}'");



            InitUI();
            InitTrainPollingTimer();
            HookCanvasInput();

            ImgMain.SizeChanged += ImgMain_SizeChanged;
            this.SizeChanged += MainWindow_SizeChanged;
            this.Loaded += MainWindow_Loaded;
        }

        private void InitUI()
        {
            ComboFeature.SelectedIndex = 0;
            ComboMasterRoiRole.ItemsSource = new[] { "ROI Master 1", "ROI Inspección Master 1" };
            ComboMasterRoiRole.SelectedIndex = 0;


            ComboMasterRoiShape.Items.Clear();
            ComboMasterRoiShape.Items.Add("Rectángulo");
            ComboMasterRoiShape.Items.Add("Círculo");
            ComboMasterRoiShape.Items.Add("Annulus");
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

        private bool TryClearCurrentStatePersistedRoi(out RoiRole? clearedRole)
        {
            clearedRole = GetCurrentStateRole();

            switch (_state)
            {
                case MasterState.DrawM1_Pattern:
                    if (_layout.Master1Pattern != null)
                    {
                        _layout.Master1Pattern = null;
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
                        return true;
                    }
                    break;

                case MasterState.Ready:
                    if (_layout.Inspection != null)
                    {
                        _layout.Inspection = null;
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



        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ScheduleSyncOverlay(force: true);
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleSyncOverlay(force: true);
        }

        private void ImgMain_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleSyncOverlay(force: true);
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
            if (_previewShape != null)
            {
                CanvasROI.Children.Remove(_previewShape);
                _previewShape = null;
            }

            _previewShape = shape switch
            {
                RoiShape.Rectangle => new WRectShape
                {
                    Stroke = WBrushes.Cyan,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(40, 0, 255, 255))
                },
                RoiShape.Circle or RoiShape.Annulus => new WEllipse
                {
                    Stroke = WBrushes.Lime,
                    StrokeThickness = shape == RoiShape.Annulus ? 6 : 2,
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

        private void UpdateDraw(RoiShape shape, WPoint p0, WPoint p1)
        {
            if (_previewShape == null) return;
            if (shape == RoiShape.Rectangle)
            {
                var x = Math.Min(p0.X, p1.X); var y = Math.Min(p0.Y, p1.Y);
                var w = Math.Abs(p1.X - p0.X); var h = Math.Abs(p1.Y - p0.Y);
                Canvas.SetLeft(_previewShape, x); Canvas.SetTop(_previewShape, y);
                _previewShape.Width = w; _previewShape.Height = h;
            }
            else
            {
                var dx = p1.X - p0.X; var dy = p1.Y - p0.Y;
                var r = Math.Sqrt(dx * dx + dy * dy) / 2.0;
                var cx = (p0.X + p1.X) / 2.0; var cy = (p0.Y + p1.Y) / 2.0;
                Canvas.SetLeft(_previewShape, cx - r); Canvas.SetTop(_previewShape, cy - r);
                _previewShape.Width = 2 * r; _previewShape.Height = 2 * r;
            }
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
                canvasDraft = new RoiModel { Shape = RoiShape.Rectangle, X = x, Y = y, Width = w, Height = h };
            }
            else
            {
                var x = Canvas.GetLeft(_previewShape);
                var y = Canvas.GetTop(_previewShape);
                var w = _previewShape.Width;
                var r = w / 2.0;
                var cx = x + r; var cy = y + r;
                canvasDraft = new RoiModel
                {
                    Shape = shape,
                    CX = cx,
                    CY = cy,
                    R = r,
                    RInner = shape == RoiShape.Annulus ? r * 0.6 : 0,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = _previewShape.Height
                };
            }

            var pixelDraft = CanvasToImage(canvasDraft);
            _tmpBuffer = pixelDraft;
            AppendLog($"[draw] ROI draft = {DescribeRoi(_tmpBuffer)}");

            _previewShape.Tag = canvasDraft;
            ApplyRoiRotationToShape(_previewShape, canvasDraft.AngleDeg);
            if (_state == MasterState.DrawInspection)
            {
                canvasDraft.Role = RoiRole.Inspection;
                if (_tmpBuffer != null)
                {
                    _tmpBuffer.Role = RoiRole.Inspection;
                    SyncCurrentRoiFromInspection(_tmpBuffer);
                }
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

                var adorner = new RoiAdorner(_previewShape, (changeKind, modelUpdated) =>
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
            if (r.Shape == RoiShape.Rectangle) return new WRect(r.X, r.Y, r.Width, r.Height);
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

                    var res1 = LocalMatcher.MatchInSearchROI(img, _layout.Master1Pattern, _layout.Master1Search,
                        _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax);
                    if (res1.center.HasValue) { c1 = new WPoint(res1.center.Value.X, res1.center.Value.Y); s1 = res1.score; }
                    else AppendLog("[LOCAL] M1 no encontrado");

                    var res2 = LocalMatcher.MatchInSearchROI(img, _layout.Master2Pattern, _layout.Master2Search,
                        _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax);
                    if (res2.center.HasValue) { c2 = new WPoint(res2.center.Value.X, res2.center.Value.Y); s2 = res2.score; }
                    else AppendLog("[LOCAL] M2 no encontrado");
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
                AppendLog("[FLOW] Usando backend matcher");

                var tpl1Rect = RoiToRect(_layout.Master1Pattern!);
                var tpl2Rect = RoiToRect(_layout.Master2Pattern!);

                // Intentar usar el PNG guardado del patrón (mejor que recortar de la imagen actual)
                var tpl1Path = TryGetSavedPatternPath("M1");
                var tpl2Path = TryGetSavedPatternPath("M2");

                // --- M1 ---
                AppendLog($"[BACKEND] M1 ROI=({tpl1Rect.X:0},{tpl1Rect.Y:0},{tpl1Rect.Width:0},{tpl1Rect.Height:0})");

                (bool ok, System.Windows.Point? center, double score, string? error) r1;

                if (!string.IsNullOrEmpty(tpl1Path) && File.Exists(tpl1Path))
                {
                    // Enviar template DESDE ARCHIVO (patrón guardado)
                    r1 = await BackendAPI.MatchOneViaTemplateAsync(
                        _currentImagePathWin, tpl1Path,
                        _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax,
                        string.IsNullOrWhiteSpace(_preset.Feature) ? "auto" : _preset.Feature,
                        0.8, "M1", AppendLog);
                }
                else
                {
                    // Fallback: recorte desde la imagen actual (no ideal, pero seguimos)
                    AppendLog("[WARN] M1 sin patrón PNG guardado. Fallback a recorte de la imagen actual.");
                    r1 = await BackendAPI.MatchOneViaFilesAsync(
                        _currentImagePathWin, _layout.Master1Pattern!,
                        _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax,
                        string.IsNullOrWhiteSpace(_preset.Feature) ? "auto" : _preset.Feature,
                        0.8, false, "M1", AppendLog);
                }

                if (r1.ok && r1.center.HasValue)
                {
                    c1 = new WPoint(r1.center.Value.X, r1.center.Value.Y);
                    s1 = r1.score;
                    AppendLog($"[MATCH] M1 FOUND stage=backend center=({c1.Value.X:0.##},{c1.Value.Y:0.##}) score={s1:0.###}");
                }
                else
                {
                    AppendLog("[BACKEND] M1 FAIL :: " + (r1.error ?? "unknown"));
                }

                // --- M2 ---
                AppendLog($"[BACKEND] M2 ROI=({tpl2Rect.X:0},{tpl2Rect.Y:0},{tpl2Rect.Width:0},{tpl2Rect.Height:0})");

                (bool ok, System.Windows.Point? center, double score, string? error) r2;

                if (!string.IsNullOrEmpty(tpl2Path) && File.Exists(tpl2Path))
                {
                    r2 = await BackendAPI.MatchOneViaTemplateAsync(
                        _currentImagePathWin, tpl2Path,
                        _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax,
                        string.IsNullOrWhiteSpace(_preset.Feature) ? "auto" : _preset.Feature,
                        0.8, "M2", AppendLog);
                }
                else
                {
                    AppendLog("[WARN] M2 sin patrón PNG guardado. Fallback a recorte de la imagen actual.");
                    r2 = await BackendAPI.MatchOneViaFilesAsync(
                        _currentImagePathWin, _layout.Master2Pattern!,
                        _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax,
                        string.IsNullOrWhiteSpace(_preset.Feature) ? "auto" : _preset.Feature,
                        0.8, false, "M2", AppendLog);
                }

                if (r2.ok && r2.center.HasValue)
                {
                    c2 = new WPoint(r2.center.Value.X, r2.center.Value.Y);
                    s2 = r2.score;
                    AppendLog($"[MATCH] M2 FOUND stage=backend center=({c2.Value.X:0.##},{c2.Value.Y:0.##}) score={s2:0.###}");
                }
                else
                {
                    AppendLog("[BACKEND] M2 FAIL :: " + (r2.error ?? "unknown"));
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

            DrawMasterMatch(_layout.Master1Pattern!, c1.Value, "M1", WBrushes.LimeGreen, withLabel: false);
            DrawMasterMatch(_layout.Master2Pattern!, c2.Value, "M2", WBrushes.Red, withLabel: false);

            // 5) Reubicar inspección si existe
            if (_layout.Inspection == null)
            {
                Snack("Masters OK. Falta ROI de Inspección: dibújalo y guarda. Las cruces ya están dibujadas.");
                AppendLog("[FLOW] Inspection null");
                _state = MasterState.DrawInspection;
                UpdateWizardState();
                return;
            }

            MoveInspectionTo(_layout.Inspection, mid.X, mid.Y);
            ClipInspectionROI(_layout.Inspection, _imgW, _imgH);
            AppendLog("[FLOW] Inspection movida y recortada");

            MasterLayoutManager.Save(_preset, _layout);
            AppendLog("[FLOW] Layout guardado");

            Snack($"Masters OK. Scores: M1={s1:0.000}, M2={s2:0.000}. ROI inspección reubicado.");
            _state = MasterState.Ready;
            UpdateWizardState();
            AppendLog("[FLOW] AnalyzeMastersAsync terminado");
        }












        // Ajuste de AppendLog como método, no como delegado inválido
        // Siempre escribe en el hilo de UI
        // Log seguro desde cualquier hilo
        private void AppendLog(string line)
        {
            AppendLogBulk(new[] { line });
            try
            {
                // Mirror the exact text shown in UI into the file
                WriteFileLog($"[{DateTime.Now:HH:mm:ss.fff}] " + line);
            }
            catch { /* noop */ }

        }

        private void AppendLogBulk(IEnumerable<string> lines)
        {
            if (TrainLogText == null) return;

            if (!Dispatcher.CheckAccess())
            {
                var copy = lines.ToList();
                Dispatcher.Invoke(() => AppendLogBulk(copy));
                return;
            }

            var list = lines as IList<string> ?? lines.ToList();
            if (list.Count == 0)
                return;

            var sb = new StringBuilder();
            foreach (var entry in list)
            {
                var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                sb.Append('[').Append(stamp).Append("] ").Append(entry).AppendLine();
            }

            TrainLogText.AppendText(sb.ToString());
            TrainLogText.ScrollToEnd();
        }




        // Initialize file logger; creates logs\ui_*.log under the app base directory
        private void InitFileLogger()
        {
            try
            {
                var baseDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                System.IO.Directory.CreateDirectory(baseDir);
                _fileLogPath = System.IO.Path.Combine(baseDir, $"ui_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                var header = $"[{DateTime.Now:HH:mm:ss.fff}] === UI LOG START ==={Environment.NewLine}";
                lock (_fileLogLock)
                {
                    System.IO.File.AppendAllText(_fileLogPath, header);
                }
                try { System.Diagnostics.Debug.WriteLine("[LOG] File: " + _fileLogPath); } catch { /* noop */ }
            }
            catch { /* ignore */ }
        }


        // Write a single line to the file log (thread-safe). Adds newline automatically.
        private void WriteFileLog(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(_fileLogPath)) return;
                lock (_fileLogLock)
                {
                    System.IO.File.AppendAllText(_fileLogPath, line + System.Environment.NewLine);
                }
            }
            catch { /* noop */ }
        }

        private void NetLog(string line)
        {
            try { AppendLog("[NET] " + line); } catch { /* noop */ }
            try
            {
                WriteFileLog("[NET] " + line);
            }
            catch { /* noop */ }

        }

        // --------- AppendLog (para evitar CS0119 en invocaciones) ---------

        private void MoveInspectionTo(RoiModel insp, double cx, double cy)
        {
            if (insp.Shape == RoiShape.Rectangle)
            {
                insp.X = cx - insp.Width / 2.0; insp.Y = cy - insp.Height / 2.0;
            }
            else
            {
                insp.CX = cx; insp.CY = cy;
            }

            SyncCurrentRoiFromInspection(insp);
        }

        private void ClipInspectionROI(RoiModel insp, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return;
            if (insp.Shape == RoiShape.Rectangle)
            {
                if (insp.Width < 1) insp.Width = 1;
                if (insp.Height < 1) insp.Height = 1;
                if (insp.X < 0) insp.X = 0;
                if (insp.Y < 0) insp.Y = 0;
                if (insp.X + insp.Width > imgW) insp.X = Math.Max(0, imgW - insp.Width);
                if (insp.Y + insp.Height > imgH) insp.Y = Math.Max(0, imgH - insp.Height);
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


            var resp = await BackendAPI.AnalyzeAsync(_currentImagePathWin, _layout.Inspection, _preset, AppendLog);
            if (!resp.ok)
            {
                Snack(resp.error ?? "Error en Analyze");
                return;
            }
            Snack($"Resultado: {resp.label} (score={resp.score:0.000})");
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
                roiCanvas.X = x; roiCanvas.Y = y; roiCanvas.Width = w; roiCanvas.Height = h;
                roiCanvas.CX = roiCanvas.X + roiCanvas.Width / 2.0;
                roiCanvas.CY = roiCanvas.Y + roiCanvas.Height / 2.0;
                roiCanvas.R = Math.Max(roiCanvas.Width, roiCanvas.Height) / 2.0;
            }
            else if (shape is System.Windows.Shapes.Ellipse)
            {
                var r = w / 2.0;
                roiCanvas.Shape = roiCanvas.Shape == RoiShape.Annulus ? RoiShape.Annulus : RoiShape.Circle;
                roiCanvas.CX = x + r; roiCanvas.CY = y + r; roiCanvas.R = r;
                roiCanvas.X = x; roiCanvas.Y = y; roiCanvas.Width = w; roiCanvas.Height = h;
                if (roiCanvas.Shape == RoiShape.Annulus && (roiCanvas.RInner <= 0 || roiCanvas.RInner >= roiCanvas.R))
                    roiCanvas.RInner = roiCanvas.R * 0.6;
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
                    SyncCurrentRoiFromInspection(clone);
                    break;
            }

            var currentRole = GetCurrentStateRole();
            if (currentRole.HasValue && roiPixel.Role == currentRole.Value)
            {
                _tmpBuffer = clone.Clone();
            }
        }

        private void SyncCurrentRoiFromInspection(RoiModel inspectionPixel)
        {
            if (inspectionPixel == null) return;

            if (inspectionPixel.Shape == RoiShape.Rectangle)
            {
                CurrentRoi.X = inspectionPixel.X + inspectionPixel.Width / 2.0;
                CurrentRoi.Y = inspectionPixel.Y + inspectionPixel.Height / 2.0;
                CurrentRoi.Width = inspectionPixel.Width;
                CurrentRoi.Height = inspectionPixel.Height;
            }
            else
            {
                var diameter = inspectionPixel.R * 2.0;
                CurrentRoi.X = inspectionPixel.CX;
                CurrentRoi.Y = inspectionPixel.CY;
                CurrentRoi.Width = diameter;
                CurrentRoi.Height = diameter;
            }

            CurrentRoi.AngleDeg = inspectionPixel.AngleDeg;
            UpdateInspectionShapeRotation(CurrentRoi.AngleDeg);
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

        private class MatchOneResult
        {
            public bool ok { get; set; }
            public double x { get; set; }
            public double y { get; set; }
            public double score { get; set; }
            public string error { get; set; } = "";
        }

        // Reemplazo robusto: acepta {ok,x,y,score} y también {found,center_x,center_y,confidence}
        // ✅ Versión correcta: envía image + template (PNG recortado) al backend.
        //    Acepta tanto respuestas {ok,x,y,score} como {found,center_x,center_y,confidence}.
        // ✅ Envía image + template (PNG) y usa timeout corto por petición.
        //    Acepta respuestas {ok,x,y,score} o {found,center_x,center_y,confidence}.
        // ✅ Envía image + template (PNG) y, opcionalmente, el rectángulo de búsqueda (search_*).
        // ===== En MainWindow.xaml.cs =====
        private async Task<(bool ok, System.Windows.Point? center, double score, string? error)> TryMatchOneViaFilesAsync(
            string imagePathWin,
            RoiModel templateRoi,
            string which,
            double thr,
            double rotRange,
            double scaleMin,
            double scaleMax,
            string feature,
            double tmThr,
            bool debug)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            MemoryStream? tplStream = null; // NO usar 'using var' si se va a pasar como contenido del multipart
            MemoryStream? maskStream = null;

            try
            {
                string url = BackendAPI.BaseUrl.TrimEnd('/') + BackendAPI.MatchEndpoint;
                AppendLog($"[MATCH] Preparando POST {which} → {url}");

                // Log tamaño de la imagen (opcional)
                try
                {
                    using var tmpBmp = new System.Drawing.Bitmap(imagePathWin);
                    AppendLog($"[MATCH] {which} imagen='{System.IO.Path.GetFileName(imagePathWin)}' size={tmpBmp.Width}x{tmpBmp.Height}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[MATCH] {which} WARN: no se pudo abrir imagen para info: {ex.Message}");
                }

                using var hc = new System.Net.Http.HttpClient();
                using var mp = new System.Net.Http.MultipartFormDataContent();

                // 1) Imagen completa
                var imgBytes = System.IO.File.ReadAllBytes(imagePathWin);
                AppendLog($"[MATCH] {which} bytes(image)={imgBytes.Length:n0}");
                mp.Add(new System.Net.Http.ByteArrayContent(imgBytes), "image", System.IO.Path.GetFileName(imagePathWin));

                // 2) Template (crop) — NO usar 'using var' con out/ref
                var templateRect = RoiToRect(templateRoi);
                if (!BackendAPI.TryCropToPng(imagePathWin, templateRoi, out tplStream, out maskStream, out var tplName, AppendLog))
                {
                    var msg = "crop template failed";
                    AppendLog($"[MATCH] {which} ERROR: {msg}");
                    return (false, null, 0, msg);
                }
                tplStream.Position = 0;
                AppendLog($"[MATCH] {which} bytes(template)={tplStream.Length:n0} rect=({(int)templateRect.X},{(int)templateRect.Y},{(int)templateRect.Width},{(int)templateRect.Height})");
                if (maskStream != null)
                {
                    AppendLog($"[MATCH] {which} mask bytes={maskStream.Length:n0} (embebida como alfa)");
                }
                mp.Add(new System.Net.Http.StreamContent(tplStream), "template", string.IsNullOrWhiteSpace(tplName) ? "template.png" : tplName);

                // 3) Parámetros
                string feat = string.IsNullOrWhiteSpace(feature) ? "auto" : feature;
                mp.Add(new System.Net.Http.StringContent(which), "tag");
                mp.Add(new System.Net.Http.StringContent(thr.ToString(System.Globalization.CultureInfo.InvariantCulture)), "thr");
                mp.Add(new System.Net.Http.StringContent(rotRange.ToString(System.Globalization.CultureInfo.InvariantCulture)), "rot_range");
                mp.Add(new System.Net.Http.StringContent(scaleMin.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_min");
                mp.Add(new System.Net.Http.StringContent(scaleMax.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_max");
                mp.Add(new System.Net.Http.StringContent(feat), "feature");
                mp.Add(new System.Net.Http.StringContent(tmThr.ToString(System.Globalization.CultureInfo.InvariantCulture)), "tm_thr");
                mp.Add(new System.Net.Http.StringContent(debug ? "1" : "0"), "debug");

                // 4) POST con timeout acotado
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var swHttp = System.Diagnostics.Stopwatch.StartNew();
                AppendLog($"[MATCH] POST {which} lanzado...");
                System.Net.Http.HttpResponseMessage resp;
                try
                {
                    resp = await hc.PostAsync(url, mp, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    AppendLog($"[MATCH] {which} TIMEOUT (30s)");
                    return (false, null, 0, "timeout");
                }
                swHttp.Stop();

                var body = await resp.Content.ReadAsStringAsync();
                AppendLog($"[MATCH] {which} HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({swHttp.ElapsedMilliseconds} ms)");
                AppendLog($"[MATCH] {which} BODY: {(body.Length > 800 ? body.Substring(0, 800) + "...(trunc)" : body)}");

                if (!resp.IsSuccessStatusCode)
                    return (false, null, 0, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                bool found = root.TryGetProperty("found", out var fEl) && fEl.GetBoolean();
                string? stage = root.TryGetProperty("stage", out var st) ? st.GetString() : null;

                if (!found)
                {
                    string reason = root.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "not found") : "not found";
                    double tm_best = root.TryGetProperty("tm_best", out var tb) ? tb.GetDouble() : double.NaN;
                    double tm_thr2 = root.TryGetProperty("tm_thr", out var tt) ? tt.GetDouble() : double.NaN;
                    AppendLog($"[MATCH] {which} NOT FOUND stage={stage} reason={reason} tm_best={tm_best:0.###} tm_thr={tm_thr2:0.###}");
                    return (false, null, 0, reason);
                }

                double cx = root.GetProperty("center_x").GetDouble();
                double cy = root.GetProperty("center_y").GetDouble();
                double score = 0.0;
                if (root.TryGetProperty("confidence", out var cEl)) score = cEl.GetDouble();
                else if (root.TryGetProperty("score", out var sEl)) score = sEl.GetDouble();

                AppendLog($"[MATCH] {which} FOUND stage={stage} center=({cx:0.##},{cy:0.##}) score={score:0.###}");
                return (true, new System.Windows.Point(cx, cy), score, null);
            }
            catch (Exception ex)
            {
                AppendLog($"[MATCH] EX {which}: {ex.Message}");
                return (false, null, 0, ex.Message);
            }
            finally
            {
                swTotal.Stop();
                AppendLog($"[MATCH] total {which} elapsed={swTotal.ElapsedMilliseconds} ms");

                // Liberar el stream del template manualmente (no se pudo usar 'using var' por el out)
                try { tplStream?.Dispose(); } catch { /* noop */ }
                try { maskStream?.Dispose(); } catch { /* noop */ }
            }
        }








        private class AnalyzeResp
        {
            public bool ok { get; set; }
            public string label { get; set; } = "";
            public double score { get; set; }
            public string error { get; set; } = "";
        }

        private static string SerializeRoiToJson(RoiModel roi)
        {
            var obj = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["shape"] = roi.Shape.ToString().ToLowerInvariant(),
                ["role"] = roi.Role.ToString(),
                ["x"] = roi.X,
                ["y"] = roi.Y,
                ["w"] = roi.Width,
                ["h"] = roi.Height,
                ["cx"] = roi.CX,
                ["cy"] = roi.CY,
                ["r"] = roi.R,
                ["ri"] = roi.RInner,
                ["angle_deg"] = roi.AngleDeg
            };
            return System.Text.Json.JsonSerializer.Serialize(obj);
        }

        private async System.Threading.Tasks.Task<(bool ok, string? label, double score, string? error)> AnalyzeRoiViaFilesAsync(
            string imagePathWin, RoiModel inspection, PresetFile preset, System.Action<string>? log)
        {
            try
            {
                var url = BackendAPI.BaseUrl.TrimEnd('/') + BackendAPI.AnalyzeEndpoint;
                var imgBytes = System.IO.File.ReadAllBytes(imagePathWin);

                using var hc = new System.Net.Http.HttpClient();
                using var mp = new System.Net.Http.MultipartFormDataContent();

                mp.Add(new System.Net.Http.ByteArrayContent(imgBytes), "image", System.IO.Path.GetFileName(imagePathWin));
                mp.Add(new System.Net.Http.StringContent(SerializeRoiToJson(inspection),
                        System.Text.Encoding.UTF8, "application/json"), "roi");

                // parámetros opcionales por si el backend los usa
                mp.Add(new System.Net.Http.StringContent(preset.Feature ?? "auto"), "feature");
                mp.Add(new System.Net.Http.StringContent((preset.MatchThr > 0 ? preset.MatchThr : 85).ToString()), "thr");
                mp.Add(new System.Net.Http.StringContent((preset.RotRange > 0 ? preset.RotRange : 10).ToString()), "rot_range");
                mp.Add(new System.Net.Http.StringContent(preset.ScaleMin.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_min");
                mp.Add(new System.Net.Http.StringContent(preset.ScaleMax.ToString(System.Globalization.CultureInfo.InvariantCulture)), "scale_max");

                var resp = await hc.PostAsync(url, mp);
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (false, null, 0, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                try
                {
                    var obj = System.Text.Json.JsonSerializer.Deserialize<AnalyzeResp>(json);
                    if (obj != null && obj.ok) return (true, obj.label, obj.score, null);
                    return (false, null, 0, obj?.error ?? "Respuesta no válida");
                }
                catch (System.Exception ex)
                {
                    log?.Invoke("[analyze] parse error: " + ex.Message);
                    return (false, null, 0, "parse error: " + ex.Message);
                }
            }
            catch (System.Exception ex)
            {
                return (false, null, 0, ex.Message);
            }
        }

        // ===== NET LOG ==========
        private static readonly object _netLogLock = new object();
        private static string _netLogFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"BrakeDiscInspector_NET_{System.Diagnostics.Process.GetCurrentProcess().Id}.log");

        // Guarda un recorte PNG del ROI (patrón/búsqueda) en roi_previews/, junto a la imagen cargada.

        private Mat GetUiMatOrReadFromDisk()
        {
            if (ImgMain?.Source is System.Windows.Media.Imaging.BitmapSource bs)
            {
                // Misma imagen que ve la UI
                return OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToMat(bs);
            }

            if (!string.IsNullOrWhiteSpace(_currentImagePathWin))
            {
                var m = Cv2.ImRead(_currentImagePathWin, ImreadModes.Unchanged);
                if (!m.Empty()) return m;
            }

            throw new InvalidOperationException("No hay imagen en la UI ni ruta válida para leer.");
        }

        private string EnsureAndGetPreviewDir()
        {
            var imgDir = System.IO.Path.GetDirectoryName(_currentImagePathWin) ?? "";
            var previewDir = System.IO.Path.Combine(imgDir, "roi_previews");
            System.IO.Directory.CreateDirectory(previewDir);
            return previewDir;
        }



        private void SaveRoiCropPreview(RoiModel roi, string tag)
        {
            try
            {
                if (roi == null)
                {
                    AppendLog("[preview] ROI == null");
                    return;
                }

                // Asegurar que el ROI está en coordenadas de IMAGEN (no canvas)
                RoiModel roiImage = LooksLikeCanvasCoords(roi) ? CanvasToImage(roi) : roi.Clone();

                // 1) Construir la info del recorte desde el ROI en coords de imagen
                if (!RoiCropUtils.TryBuildRoiCropInfo(roiImage, out var cropInfo))
                {
                    AppendLog("[preview] ROI no soportado para recorte.");
                    return;
                }

                // 2) Usar EXACTAMENTE la misma imagen que ve la UI
                using var src = GetUiMatOrReadFromDisk();
                if (src.Empty())
                {
                    AppendLog("[preview] Imagen fuente vacía.");
                    return;
                }

                // 3) Recorte rotado (respeta ángulo del ROI). WPF horario vs OpenCV antihorario ya se corrige en utils
                if (!RoiCropUtils.TryGetRotatedCrop(src, cropInfo, roiImage.AngleDeg,
                    out var cropMat, out var cropRect))
                {
                    AppendLog("[preview] No se pudo obtener el recorte rotado.");
                    return;
                }

                // 4) Máscara alfa según forma (Rect / Circle / Annulus)
                Mat? alphaMask = null;
                try
                {
                    alphaMask = RoiCropUtils.BuildRoiMask(cropInfo, cropRect);
                    using var cropWithAlpha = RoiCropUtils.ConvertCropToBgra(cropMat, alphaMask);

                    var outDir = EnsureAndGetPreviewDir();
                    string ts = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                    string fname = $"{tag}_{ts}.png";
                    var outPath = System.IO.Path.Combine(outDir, fname);

                    Cv2.ImWrite(outPath, cropWithAlpha);
                    AppendLog($"[preview] Guardado {fname} ROI=({cropInfo.Left:0.#},{cropInfo.Top:0.#},{cropInfo.Width:0.#},{cropInfo.Height:0.#}) " +
                              $"crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) ang={roiImage.AngleDeg:0.##}");
                }
                finally
                {
                    alphaMask?.Dispose();
                    cropMat.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppendLog("[preview] error: " + ex.Message);
            }
        }




        // === 1) Snapshot de configuración y paths ===
        private void LogPathSnapshot()
        {
            AppendLog("========== PATH SNAPSHOT ==========");
            try
            {
                AppendLog($"[CFG] BaseUrl={BackendAPI.BaseUrl}");
                AppendLog($"[CFG] MatchEndpoint={BackendAPI.MatchEndpoint}  AnalyzeEndpoint={BackendAPI.AnalyzeEndpoint}");
                AppendLog($"[IMG] _currentImagePathWin='{_currentImagePathWin}'  exists={System.IO.File.Exists(_currentImagePathWin)}");

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

                AppendLog($"[PRESET] Feature='{_preset.Feature}' Thr={_preset.MatchThr} RotRange={_preset.RotRange} Scale=[{_preset.ScaleMin:0.###},{_preset.ScaleMax:0.###}]");
            }
            catch (Exception ex)
            {
                AppendLog("[SNAPSHOT] ERROR: " + ex.Message);
            }
            AppendLog("===================================");
        }

        // === 2) Verifica rutas y conectividad con el backend ===
        private async Task<bool> VerifyPathsAndConnectivityAsync()
        {
            AppendLog("== VERIFY: comenzando verificación de paths/IP ==");
            bool ok = true;

            // Imagen
            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !System.IO.File.Exists(_currentImagePathWin))
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

            // BaseUrl
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

            // Ping rápido a /train_status (GET) para comprobar que el backend responde
            try
            {
                var url = BackendAPI.BaseUrl.TrimEnd('/') + BackendAPI.TrainStatusEndpoint;
                using var hc = new System.Net.Http.HttpClient();
                hc.Timeout = TimeSpan.FromSeconds(5);
                var resp = await hc.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                AppendLog($"[VERIFY] GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (resp.IsSuccessStatusCode)
                {
                    AppendLog($"[VERIFY] train_status body (tail): {body.Substring(0, Math.Min(body.Length, 200))}");
                }
                else
                {
                    Snack($"El backend respondió {resp.StatusCode} en /train_status");
                    // No marcamos ok=false porque /train_status es opcional, pero lo dejamos anotado.
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
        private bool LooksLikeCanvasCoords(RoiModel r)
        {
            var (pw, ph) = GetImagePixelSize();
            var disp = GetImageDisplayRect();

            // Si el ROI “cabe” dentro del tamaño del canvas/área visible y no excede claramente,
            // asumimos que está en coords CANVAS (no imagen).
            bool withinCanvas =
                r.X >= -1 && r.Y >= -1 &&
                r.Width <= disp.Width + 2 &&
                r.Height <= disp.Height + 2;

            // Si además el tamaño aparente es similar al canvas (no a la imagen en píxeles), reforzamos la sospecha.
            bool notImageScale =
                r.Width > 0 && r.Height > 0 &&
                (r.Width > pw || r.Height > ph);

            return withinCanvas && notImageScale;
        }


        private void DrawMasterMatch(RoiModel pattern, System.Windows.Point matchImageCenter, string label, Brush color, bool withLabel)
        {
            if (CanvasROI == null) return;

            var centerCanvas = ImagePxToCanvasPt(matchImageCenter.X, matchImageCenter.Y);

            if (withLabel)
            {
                DrawLabeledCross(centerCanvas.X, centerCanvas.Y, label,
                                 color, Brushes.Black, Brushes.White, 24, 2.5);
            }
            else
            {
                DrawCross(centerCanvas.X, centerCanvas.Y, 24, color, 2.5);
            }

            DrawMatchSilhouette(pattern, matchImageCenter, color);
        }

        private void DrawMatchSilhouette(RoiModel pattern, System.Windows.Point matchImageCenter, Brush stroke)
        {
            if (CanvasROI == null) return;

            var silhouetteImage = pattern.Clone();
            silhouetteImage.AngleDeg = pattern.AngleDeg;

            if (silhouetteImage.Shape == RoiShape.Rectangle)
            {
                silhouetteImage.Width = pattern.Width;
                silhouetteImage.Height = pattern.Height;
                silhouetteImage.X = matchImageCenter.X - silhouetteImage.Width / 2.0;
                silhouetteImage.Y = matchImageCenter.Y - silhouetteImage.Height / 2.0;
            }
            else
            {
                silhouetteImage.R = pattern.R;
                silhouetteImage.RInner = pattern.RInner;
                silhouetteImage.CX = matchImageCenter.X;
                silhouetteImage.CY = matchImageCenter.Y;
                silhouetteImage.X = silhouetteImage.CX - silhouetteImage.R;
                silhouetteImage.Y = silhouetteImage.CY - silhouetteImage.R;
                silhouetteImage.Width = silhouetteImage.R * 2.0;
                silhouetteImage.Height = silhouetteImage.R * 2.0;
            }

            var canvasRoi = ImageToCanvas(silhouetteImage);

            void SetCommonShapeProps(Shape shape, double thickness, DoubleCollection? dash = null)
            {
                shape.Stroke = stroke;
                shape.StrokeThickness = thickness;
                shape.Fill = Brushes.Transparent;
                shape.SnapsToDevicePixels = true;
                shape.Tag = ANALYSIS_TAG;
                if (dash != null)
                    shape.StrokeDashArray = dash;
                Panel.SetZIndex(shape, 9996);
                CanvasROI.Children.Add(shape);
            }

            if (canvasRoi.Shape == RoiShape.Rectangle)
            {
                var rect = new WRectShape
                {
                    Width = Math.Max(0, canvasRoi.Width),
                    Height = Math.Max(0, canvasRoi.Height)
                };
                Canvas.SetLeft(rect, canvasRoi.X);
                Canvas.SetTop(rect, canvasRoi.Y);
                rect.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                if (Math.Abs(canvasRoi.AngleDeg) > 0.01)
                {
                    rect.RenderTransform = new RotateTransform(canvasRoi.AngleDeg);
                }
                SetCommonShapeProps(rect, 2.4, new DoubleCollection { 6, 3 });
            }
            else if (canvasRoi.Shape == RoiShape.Circle || canvasRoi.Shape == RoiShape.Annulus)
            {
                void AddCircle(double radius, double thickness, DoubleCollection? dash = null)
                {
                    if (radius <= 0) return;
                    double diameter = radius * 2.0;
                    var ellipse = new WEllipse
                    {
                        Width = diameter,
                        Height = diameter
                    };
                    Canvas.SetLeft(ellipse, canvasRoi.CX - radius);
                    Canvas.SetTop(ellipse, canvasRoi.CY - radius);
                    SetCommonShapeProps(ellipse, thickness, dash);
                }

                AddCircle(canvasRoi.R, 2.4, new DoubleCollection { 6, 3 });
                if (canvasRoi.Shape == RoiShape.Annulus && canvasRoi.RInner > 0)
                {
                    AddCircle(canvasRoi.RInner, 1.8);
                }
            }
        }

        // Si tu DrawCross no marca Tag, añade esta variante para que sea limpiable selectivamente
        private void DrawCross(double x, double y, int size, Brush brush, double thickness)
        {
            var line1 = new System.Windows.Shapes.Line
            {
                X1 = x - size,
                Y1 = y,
                X2 = x + size,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                Tag = ANALYSIS_TAG
            };
            var line2 = new System.Windows.Shapes.Line
            {
                X1 = x,
                Y1 = y - size,
                X2 = x,
                Y2 = y + size,
                Stroke = brush,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                Tag = ANALYSIS_TAG
            };
            Panel.SetZIndex(line1, 9999);
            Panel.SetZIndex(line2, 9999);
            CanvasROI.Children.Add(line1);
            CanvasROI.Children.Add(line2);
        }

        private void DrawLabeledCross(double x, double y, string label,
                                      Brush crossColor, Brush labelBg, Brush labelFg,
                                      int crossSize = 20, double thickness = 2)
        {
            // 1) Cruz
            DrawCross(x, y, crossSize, crossColor, thickness);

            // 2) Etiqueta (Border + TextBlock) pegada a la cruz
            var tb = new TextBlock
            {
                Text = label,
                Foreground = labelFg,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2)
            };

            // Configuración de TextOptions (forma correcta en WPF)
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);

            var border = new Border
            {
                Background = labelBg,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(0.8),
                Child = tb,
                Opacity = 0.92,
                Tag = ANALYSIS_TAG   // para limpiar sólo marcas de análisis
            };

            Canvas.SetLeft(border, x + LabelOffsetX);
            Canvas.SetTop(border, y + LabelOffsetY);

            CanvasROI.Children.Add(border);
        }



        private void RemoveAnalysisMarks()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RemoveAnalysisMarks);
                return;
            }

            var toRemove = CanvasROI.Children
                .OfType<FrameworkElement>()
                .Where(el => (el.Tag as string) == ANALYSIS_TAG)
                .ToList();

            foreach (var el in toRemove)
                CanvasROI.Children.Remove(el);
        }

        // Limpia cruces y redibuja SOLO ROIs persistentes (si tu método ya lo hace)
        private void ResetAnalysisMarks()
        {
            RemoveAnalysisMarks();
            ExitAnalysisView();
            AppendLog("[ANALYZE] Limpiadas marcas de análisis (cruces).");
        }



        private void ClearAnalysisMarks()
        {
            if (OverlayCanvas == null) return;

            // Elimina únicamente las formas con Tag == ANALYSIS_TAG (no toca los ROIs ni previews)
            for (int i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (OverlayCanvas.Children[i] is Shape sh &&
                    sh.Tag is string tag &&
                    tag == ANALYSIS_TAG)
                {
                    OverlayCanvas.Children.RemoveAt(i);
                }
            }
            AppendLog("[ANALYZE] Limpiadas marcas de análisis (cruces).");
        }

        private void ClearAnalysisMarksOnly()
        {
            // Quita TODO lo que tenga Tag=ANALYSIS_TAG y deja intactos los ROIs persistentes
            for (int i = CanvasROI.Children.Count - 1; i >= 0; i--)
            {
                if (CanvasROI.Children[i] is FrameworkElement fe && Equals(fe.Tag, ANALYSIS_TAG))
                    CanvasROI.Children.RemoveAt(i);
            }
        }

        private void ClearPersistedRoisFromCanvas()
        {
            if (CanvasROI == null) return;

            var persistedShapes = CanvasROI.Children
                .OfType<Shape>()
                .Where(shape => shape.Tag is RoiModel)
                .ToList();

            foreach (var shape in persistedShapes)
            {
                RemoveRoiAdorners(shape);
                CanvasROI.Children.Remove(shape);
            }
        }

        private void EnterAnalysisView()
        {
            _analysisViewActive = true;
            ClearAnalysisMarksOnly();
            ClearPersistedRoisFromCanvas();

            if (_previewShape != null)
            {
                CanvasROI.Children.Remove(_previewShape);
                _previewShape = null;
            }
        }

        private void ExitAnalysisView()
        {
            _analysisViewActive = false;
            RedrawOverlaySafe();
        }

        private void RedrawOverlay()
        {
            if (CanvasROI == null) return;

            if (_analysisViewActive)
            {
                ClearPersistedRoisFromCanvas();
                return;
            }

            var roiShapes = CanvasROI.Children
                .OfType<Shape>()
                .Where(s => !ReferenceEquals(s, _previewShape) && s.Tag is RoiModel)
                .ToList();

            foreach (var shape in roiShapes)
            {
                RemoveRoiAdorners(shape);
                CanvasROI.Children.Remove(shape);
            }

            if (_imgW <= 0 || _imgH <= 0)
            {
                return;
            }

            void AddPersistentRoi(RoiModel? roi)
            {
                if (roi == null) return;
                var shape = CreateLayoutShape(roi);
                if (shape == null)
                {
                    AppendLog($"[overlay] build failed for {roi.Role} ({roi.Label})");
                    return;
                }
                CanvasROI.Children.Add(shape);
                double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
                double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
                AppendLog($"[overlay] add role={roi.Role} bounds=({left:0.##},{top:0.##},{shape.Width:0.##},{shape.Height:0.##}) angle={roi.AngleDeg:0.##}");
                AttachRoiAdorner(shape);
            }

            AddPersistentRoi(_layout.Master1Search);
            AddPersistentRoi(_layout.Master1Pattern);
            AddPersistentRoi(_layout.Master2Search);
            AddPersistentRoi(_layout.Master2Pattern);
            AddPersistentRoi(_layout.Inspection);

            if (_layout.Inspection != null)
                SyncCurrentRoiFromInspection(_layout.Inspection);
        }

        private Shape? CreateLayoutShape(RoiModel roi)
        {
            var canvasRoi = ImageToCanvas(roi);
            canvasRoi.Role = roi.Role;
            canvasRoi.Label = roi.Label;
            canvasRoi.Id = roi.Id;
            Shape shape = canvasRoi.Shape == RoiShape.Rectangle ? new WRectShape() : new WEllipse();

            var style = GetRoiStyle(canvasRoi.Role);

            shape.Stroke = style.stroke;
            shape.Fill = style.fill;
            shape.StrokeThickness = style.thickness;
            if (style.dash != null)
                shape.StrokeDashArray = style.dash;
            shape.SnapsToDevicePixels = true;
            shape.IsHitTestVisible = true;

            if (canvasRoi.Shape == RoiShape.Rectangle)
            {
                Canvas.SetLeft(shape, canvasRoi.X);
                Canvas.SetTop(shape, canvasRoi.Y);
                shape.Width = canvasRoi.Width;
                shape.Height = canvasRoi.Height;
            }
            else
            {
                var diameter = canvasRoi.Width > 0 ? canvasRoi.Width : canvasRoi.R * 2.0;
                Canvas.SetLeft(shape, canvasRoi.CX - canvasRoi.R);
                Canvas.SetTop(shape, canvasRoi.CY - canvasRoi.R);
                shape.Width = diameter;
                shape.Height = canvasRoi.Height > 0 ? canvasRoi.Height : diameter;
            }

            shape.Tag = canvasRoi;
            ApplyRoiRotationToShape(shape, canvasRoi.AngleDeg);
            Panel.SetZIndex(shape, style.zIndex);

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            AppendLog($"[overlay] build role={roi.Role} shape={canvasRoi.Shape} bounds=({left:0.##},{top:0.##},{shape.Width:0.##},{shape.Height:0.##}) angle={canvasRoi.AngleDeg:0.##}");

            return shape;
        }

        private (WBrush stroke, WBrush fill, double thickness, DoubleCollection? dash, int zIndex) GetRoiStyle(RoiRole role)
        {
            WBrush transparent = Brushes.Transparent;
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
            var roiInfo = shape.Tag as RoiModel;
            string shapeContext = roiInfo != null ? BuildShapeLogContext(shape) : $"shape={shape.GetType().Name} tag={(shape.Tag ?? "<null>")}";

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
            {
                AppendLog($"[adorner] layer missing for {shapeContext}");
                return;
            }

            var existing = layer.GetAdorners(shape);
            if (existing != null)
            {
                foreach (var ad in existing.OfType<RoiAdorner>())
                {
                    layer.Remove(ad);
                    AppendLog($"[adorner] removed existing roi adorner for {shapeContext}");
                }
            }

            if (roiInfo == null)
            {
                AppendLog($"[adorner] skip attach (no RoiModel) for {shapeContext}");
                return;
            }

            var adorner = new RoiAdorner(shape, (changeKind, modelUpdated) =>
            {
                var pixelModel = CanvasToImage(modelUpdated);
                UpdateLayoutFromPixel(pixelModel);
                var context = $"[adorner:{pixelModel.Role}]";
                HandleAdornerChange(changeKind, modelUpdated, pixelModel, context);
            }, AppendLog);

            layer.Add(adorner);
            AppendLog($"[adorner] attached {BuildShapeLogContext(shape)}");
        }

        private void RemoveRoiAdorners(Shape shape)
        {
            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null) return;
            var adorners = layer.GetAdorners(shape);
            if (adorners == null) return;
            foreach (var ad in adorners.OfType<RoiAdorner>())
                layer.Remove(ad);
        }

        private void DrawAnalysisCross(double x, double y, double size, Brush color, double thickness)
        {
            if (OverlayCanvas == null) return;

            double h = size / 2.0;

            var l1 = new Line
            {
                X1 = x - h,
                Y1 = y,
                X2 = x + h,
                Y2 = y,
                Stroke = color,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                Tag = ANALYSIS_TAG
            };
            var l2 = new Line
            {
                X1 = x,
                Y1 = y - h,
                X2 = x,
                Y2 = y + h,
                Stroke = color,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                Tag = ANALYSIS_TAG
            };

            // Aseguramos que las cruces queden por encima de los ROIs
            Panel.SetZIndex(l1, 9999);
            Panel.SetZIndex(l2, 9999);

            OverlayCanvas.Children.Add(l1);
            OverlayCanvas.Children.Add(l2);
        }

        // Devuelve el último patrón guardado (PNG) para M1/M2 en la carpeta roi_previews,
        // junto a la imagen actual. Si no lo encuentra, devuelve null.
        private string? TryGetSavedPatternPath(string tag /* "M1" | "M2" */)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentImagePathWin)) return null;

                var imgDir = System.IO.Path.GetDirectoryName(_currentImagePathWin)!;
                var previewDir = System.IO.Path.Combine(imgDir, "roi_previews");
                if (!Directory.Exists(previewDir)) return null;

                // busca por prefijo "M1_pattern_" o "M2_pattern_"
                var files = Directory.GetFiles(previewDir, $"{tag}_pattern_*.png")
                                     .OrderByDescending(f => File.GetCreationTimeUtc(f))
                                     .ToList();
                var found = files.FirstOrDefault();
                if (found != null)
                    AppendLog($"[PATTERN] {tag} usando patrón guardado: {found}");
                else
                    AppendLog($"[PATTERN] {tag} no tiene patrón PNG guardado en {previewDir}");

                return found;
            }
            catch (Exception ex)
            {
                AppendLog($"[PATTERN] error resolviendo patrón {tag}: {ex.Message}");
                return null;
            }
        }

        private RoiModel BuildCurrentRoiModel(RoiRole? roleOverride = null)
        {
            double width = Math.Max(CurrentRoi.Width, 0.0);
            double height = Math.Max(CurrentRoi.Height, 0.0);
            double halfW = width / 2.0;
            double halfH = height / 2.0;

            var roiModel = new RoiModel
            {
                Shape = RoiShape.Rectangle,
                X = CurrentRoi.X - halfW,
                Y = CurrentRoi.Y - halfH,
                Width = width,
                Height = height,
                AngleDeg = CurrentRoi.AngleDeg,
                CX = CurrentRoi.X,
                CY = CurrentRoi.Y,
                R = Math.Max(width, height) / 2.0
            };

            if (!string.IsNullOrWhiteSpace(CurrentRoi.Legend))
                roiModel.Label = CurrentRoi.Legend;

            var role = roleOverride ?? _layout.Inspection?.Role ?? GetCurrentStateRole();
            if (role.HasValue)
                roiModel.Role = role.Value;

            return roiModel;
        }

        private Mat GetRotatedCrop(Mat bgr)
        {
            CurrentRoi.EnforceMinSize(10, 10);
            var currentModel = BuildCurrentRoiModel();
            if (!RoiCropUtils.TryBuildRoiCropInfo(currentModel, out var info))
                return new Mat();

            if (RoiCropUtils.TryGetRotatedCrop(bgr, info, currentModel.AngleDeg, out var crop, out _))
                return crop;

            return new Mat();
        }

        // using necesarios (asegúrate de tenerlos al inicio del archivo)
        // using OpenCvSharp;
        // using OpenCvSharp.WpfExtensions;
        // using System.Windows.Media.Imaging;
        // using System;

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
                byte[]? maskPng = null;

                // 5) Llamada al backend /analyze
                var resp = await BackendAPI.AnalyzeAsync(cropPng, maskPng, annulus);

                // 6) Mostrar texto (si tienes el TextBlock en XAML)
                if (ResultLabel != null)
                    ResultLabel.Text = $"{resp.label} ({resp.score:F3} / thr {resp.threshold:F3})";

                // 7) Decodificar heatmap y pintarlo en el Image del XAML
                var heatBytes = Convert.FromBase64String(resp.heatmap_png_b64);
                using var heat = OpenCvSharp.Cv2.ImDecode(heatBytes, OpenCvSharp.ImreadModes.Color);

                if (HeatmapImage != null)
                    HeatmapImage.Source = WriteableBitmapConverter.ToWriteableBitmap(heat);

                // (Opcional) Log
                // AppendLog?.Invoke($"Analyze -> {resp.label} (score={resp.score:F3}, thr={resp.threshold:F3})");
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
            var (sx, sy) = GetCanvasScales();
            // Redondeamos al DIP final para que no haya medias coordenadas visuales
            double x = Math.Round(px * sx);
            double y = Math.Round(py * sy);
            return new System.Windows.Point(x, y);
        }




        private System.Windows.Point CanvasToImage(System.Windows.Point pCanvas)
        {
            var (sx, sy) = GetCanvasScales();
            if (sx <= 0 || sy <= 0) return new System.Windows.Point(0, 0);
            return new System.Windows.Point(pCanvas.X / sx, pCanvas.Y / sy);
        }


        private RoiModel CanvasToImage(RoiModel roiCanvas)
        {
            var result = roiCanvas.Clone();
            var (sx, sy) = GetCanvasScales();
            if (sx <= 0 || sy <= 0) return result;

            result.AngleDeg = roiCanvas.AngleDeg;

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiCanvas.X / sx;
                result.Y = roiCanvas.Y / sy;
                result.Width = roiCanvas.Width / sx;
                result.Height = roiCanvas.Height / sy;

                result.CX = result.X + result.Width / 2.0;
                result.CY = result.Y + result.Height / 2.0;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiCanvas.CX / sx;
                result.CY = roiCanvas.CY / sy;
                double s = (sx + sy) * 0.5; // robusto para círculos
                result.R = roiCanvas.R / s;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiCanvas.RInner / s;

                result.X = result.CX - result.R;
                result.Y = result.CY - result.R;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }
            return result;
        }



        private RoiModel ImageToCanvas(RoiModel roiImage)
        {
            var result = roiImage.Clone();
            var (sx, sy) = GetCanvasScales();
            if (sx <= 0 || sy <= 0) return result;

            result.AngleDeg = roiImage.AngleDeg;

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiImage.X * sx;
                result.Y = roiImage.Y * sy;
                result.Width = roiImage.Width * sx;
                result.Height = roiImage.Height * sy;

                result.CX = result.X + result.Width / 2.0;
                result.CY = result.Y + result.Height / 2.0;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiImage.CX * sx;
                result.CY = roiImage.CY * sy;
                double s = (sx + sy) * 0.5;
                result.R = roiImage.R * s;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiImage.RInner * s;

                result.X = result.CX - result.R;
                result.Y = result.CY - result.R;
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
        }



        private (double sx, double sy) GetCanvasScales()
        {
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0)
                return (1.0, 1.0);

            // 1ª opción: lo que REALMENTE se está pintando (CanvasROI ya sincr.)
            double cw = CanvasROI?.ActualWidth ?? 0;
            double ch = CanvasROI?.ActualHeight ?? 0;
            if (cw > 0 && ch > 0)
                return (cw / pw, ch / ph);

            // Fallback: letterbox calculado (por si el Canvas aún no está listo)
            var disp = GetImageDisplayRect();
            if (disp.Width > 0 && disp.Height > 0)
                return (disp.Width / pw, disp.Height / ph);

            return (1.0, 1.0);
        }








    }
}
