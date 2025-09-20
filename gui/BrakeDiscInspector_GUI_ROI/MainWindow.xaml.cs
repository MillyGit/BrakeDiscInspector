// Dialogs
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Emit;
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

        // Marca para identificar las cruces de análisis y poder borrarlas sin tocar los ROIs
        private const string AnalysisTag = "analysis-mark";

        private Action<string>? _uiLog;

        // Si tu overlay se llama distinto, ajusta esta propiedad (o referencia directa en los métodos).
        // Por ejemplo, si en XAML tienes <Canvas x:Name="Overlay"> usa ese nombre aquí.
        private Canvas OverlayCanvas => CanvasROI;

        private const string ANALYSIS_TAG = "ANALYSIS_MARK";

        // === Helpers de overlay ===
        private const double LabelOffsetX = 10;   // desplazamiento a la derecha de la cruz
        private const double LabelOffsetY = -20;  // desplazamiento hacia arriba de la cruz

        private ROI CurrentRoi = new ROI { X = 200, Y = 150, Width = 100, Height = 80, AngleDeg = 0, Legend = "M1" };
        private RoiRotateAdorner _rotateAdorner;
        private bool _rotateAdornerInitialized;
        private Mat bgrFrame; // tu frame actual
        private bool UseAnnulus = false;

        public MainWindow()
        {
            InitializeComponent();
            _preset = PresetManager.LoadOrDefault(_preset);
            _uiLog = s => Dispatcher.BeginInvoke(new Action(() => AppendLog(s)));


            InitUI();
            InitTrainPollingTimer();
            HookCanvasInput();

            ImgMain.SizeChanged += ImgMain_SizeChanged;
            CanvasROI.SizeChanged += CanvasROI_SizeChanged;
            CanvasROI.Loaded += CanvasROI_Loaded;
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

            // Botón "Analizar Master" disponible en cuanto M1+M2 estén definidos
            BtnAnalyzeMaster.IsEnabled = mastersReady;
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
            _currentImagePathBackend = path; // no normalization needed for multipart
            _currentImagePath = _currentImagePathWin;

            _imgSourceBI = new BitmapImage();
            _imgSourceBI.BeginInit();
            _imgSourceBI.CacheOption = BitmapCacheOption.OnLoad;
            _imgSourceBI.UriSource = new Uri(_currentImagePathWin);
            _imgSourceBI.EndInit();

            ImgMain.Source = _imgSourceBI;
            _imgW = _imgSourceBI.PixelWidth;
            _imgH = _imgSourceBI.PixelHeight;

            SyncOverlayToImage();

            AppendLog($"Imagen cargada: {_imgW}x{_imgH}  (Canvas: {CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0})");
            RedrawOverlay();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SyncOverlayToImage();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SyncOverlayToImage();
        }

        private void ImgMain_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SyncOverlayToImage();
        }

        private void CanvasROI_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureRotateAdorner();
            RepositionRotateAdorner();
        }

        private void CanvasROI_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            EnsureRotateAdorner();
            RepositionRotateAdorner();
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

            // 1) Si hay un Thumb bajo el puntero → el adorner se encarga (resize/moveThumb)
            if (over is System.Windows.Controls.Primitives.Thumb)
            {
                AppendLog("[canvas+] Down ignorado (Thumb debajo) -> Adorner manejará");
                return;
            }

            // 2) Si clicas dentro de un ROI (Shape con RoiModel) → iniciamos ARRASTRE
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
                e.Handled = true; // prevenimos que otro control secuestre el drag
                return;
            }

            // 3) Si clicas en Canvas vacío → iniciamos DIBUJO
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
                RepositionRotateAdorner();

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
                RepositionRotateAdorner();
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
            if (_state == MasterState.DrawInspection)
            {
                canvasDraft.Role = RoiRole.Inspection;
                if (_tmpBuffer != null)
                {
                    _tmpBuffer.Role = RoiRole.Inspection;
                    SyncCurrentRoiFromInspection(_tmpBuffer);
                }
                RepositionRotateAdorner();
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

                var adorner = new RoiAdorner(_previewShape, (shapeUpdated, modelUpdated) =>
                {
                    var pixelModel = CanvasToImage(modelUpdated);
                    _tmpBuffer = pixelModel.Clone();
                    if (_state == MasterState.DrawInspection && _tmpBuffer != null)
                    {
                        modelUpdated.Role = RoiRole.Inspection;
                        _tmpBuffer.Role = RoiRole.Inspection;
                        SyncCurrentRoiFromInspection(_tmpBuffer);
                    }
                    AppendLog($"[preview] edit => {DescribeRoi(_tmpBuffer)}");
                    RepositionRotateAdorner();
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

        // ====== Guardar pasos del wizard ======
        private void BtnSaveMaster_Click(object sender, RoutedEventArgs e)
        {
            if (_tmpBuffer is null) { Snack("Dibuja un ROI válido antes de guardar"); return; }

            switch (_state)
            {
                case MasterState.DrawM1_Pattern:
                    if (!IsAllowedMasterShape(_tmpBuffer.Shape)) { Snack("Master: usa rectángulo o círculo"); return; }
                    _tmpBuffer.Role = RoiRole.Master1Pattern;
                    _layout.Master1Pattern = _tmpBuffer.Clone();

                    // Preview (si hay imagen cargada)
                    SaveRoiCropPreview(_layout.Master1Pattern, "M1_pattern");

                    _tmpBuffer = null;
                    _state = MasterState.DrawM1_Search;

                    // Auto-cambiar el combo de rol a "Inspección Master 1"
                    try { ComboMasterRoiRole.SelectedIndex = 1; } catch { }
                    break;

                case MasterState.DrawM1_Search:
                    _tmpBuffer.Role = RoiRole.Master1Search;
                    _layout.Master1Search = _tmpBuffer.Clone();

                    SaveRoiCropPreview(_layout.Master1Search, "M1_search");

                    _tmpBuffer = null;
                    _state = MasterState.DrawM2_Pattern;
                    break;

                case MasterState.DrawM2_Pattern:
                    if (!IsAllowedMasterShape(_tmpBuffer.Shape)) { Snack("Master: usa rectángulo o círculo"); return; }
                    _tmpBuffer.Role = RoiRole.Master2Pattern;
                    _layout.Master2Pattern = _tmpBuffer.Clone();

                    SaveRoiCropPreview(_layout.Master2Pattern, "M2_pattern");

                    _tmpBuffer = null;
                    _state = MasterState.DrawM2_Search;

                    // Auto-cambiar el combo de rol a "Inspección Master 2"
                    try { ComboM2Role.SelectedIndex = 1; } catch { }
                    break;

                case MasterState.DrawM2_Search:
                    _tmpBuffer.Role = RoiRole.Master2Search;
                    _layout.Master2Search = _tmpBuffer.Clone();

                    SaveRoiCropPreview(_layout.Master2Search, "M2_search");

                    _tmpBuffer = null;

                    // En este punto M1+M2 podrían estar completos → permite inspección pero NO la exige
                    _state = MasterState.DrawInspection; // Puedes seguir con inspección si quieres
                    break;

                case MasterState.DrawInspection:
                    _tmpBuffer.Role = RoiRole.Inspection;
                    _layout.Inspection = _tmpBuffer.Clone();
                    SyncCurrentRoiFromInspection(_layout.Inspection);

                    // (Opcional) también puedes guardar un preview de la inspección inicial:
                    SaveRoiCropPreview(_layout.Inspection, "INS_init");

                    _tmpBuffer = null;
                    _state = MasterState.Ready;
                    break;
            }

            // Limpia preview/adorner y persiste
            ClearPreview();
            MasterLayoutManager.Save(_preset, _layout);
            RedrawOverlay();

            // IMPORTANTE: recalcula habilitaciones (esto ya deja el botón "Analizar Master" activo si M1+M2 están listos)
            UpdateWizardState();

            Snack("Guardado.");
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
                        _currentImagePathWin, tpl1Rect,
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
                        _currentImagePathWin, tpl2Rect,
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

            // Limpiamos SOLO marcas de análisis anteriores y redibujamos ROIs persistentes
            RedrawOverlay();
            ClearAnalysisMarksOnly();

            // Cruces + etiquetas
            DrawLabeledCross(c1.Value.X, c1.Value.Y, "M1",
                             WBrushes.LimeGreen, Brushes.Black, Brushes.White, 20, 2);

            DrawLabeledCross(c2.Value.X, c2.Value.Y, "M2",
                             WBrushes.LimeGreen, Brushes.Black, Brushes.White, 20, 2);

            DrawLabeledCross(mid.X, mid.Y, "MID",
                             WBrushes.OrangeRed, Brushes.Black, Brushes.White, 24, 3);


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
            RedrawOverlay();
            AppendLog("[FLOW] Inspection movida y recortada");

            MasterLayoutManager.Save(_preset, _layout);
            AppendLog("[FLOW] Layout guardado");

            // Mantén las cruces visibles
            DrawCross(c1.Value.X, c1.Value.Y, 20, WBrushes.LimeGreen, 2);
            DrawCross(c2.Value.X, c2.Value.Y, 20, WBrushes.LimeGreen, 2);
            DrawCross(mid.X, mid.Y, 24, WBrushes.OrangeRed, 3);

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
            if (TrainLogText == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(line));
                return;
            }

            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            TrainLogText.Text += $"[{stamp}] {line}{Environment.NewLine}";
            TrainLogText.ScrollToEnd();
        }



        private void NetLog(string line)
        {
            try { AppendLog("[NET] " + line); } catch { /* noop */ }
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
            RepositionRotateAdorner();
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
            RepositionRotateAdorner();
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
            _preset.MatchThr = (int)Math.Round(double.TryParse(TxtThr.Text, out var t) ? t : 85);
            _preset.RotRange = (int)Math.Round(double.TryParse(TxtRot.Text, out var rr) ? rr : 10);
            _preset.ScaleMin = double.TryParse(TxtSMin.Text, out var smin) ? smin : 0.95;
            _preset.ScaleMax = double.TryParse(TxtSMax.Text, out var smax) ? smax : 1.05;

            // 5) Lanzar análisis
            _ = AnalyzeMastersAsync();
        }



        private async void BtnAnalyzeROI_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentImagePathWin)) { Snack("No hay imagen actual"); return; }
            if (_layout.Inspection == null) { Snack("Falta ROI de Inspección"); return; }


            var inspRect = RoiToRect(_layout.Inspection);
            var resp = await BackendAPI.AnalyzeAsync(_currentImagePathWin, inspRect, _preset, AppendLog);
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
        private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            var s = new SaveFileDialog { Filter = "Preset JSON|*.json", FileName = "preset.json" };
            if (s.ShowDialog() == true) PresetManager.Save(_preset, s.FileName);
            Snack("Preset guardado.");
        }

        private void BtnLoadPreset_Click(object sender, RoutedEventArgs e)
        {
            var o = new OpenFileDialog { Filter = "Preset JSON|*.json" };
            if (o.ShowDialog() != true) return;
            _preset = PresetManager.Load(o.FileName);
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
            RedrawOverlay();
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
            RepositionRotateAdorner();
        }

        private void UpdateLayoutFromPixel(RoiModel roiPixel)
        {
            switch (roiPixel.Role)
            {
                case RoiRole.Master1Pattern:
                    _layout.Master1Pattern = roiPixel.Clone();
                    break;
                case RoiRole.Master1Search:
                    _layout.Master1Search = roiPixel.Clone();
                    break;
                case RoiRole.Master2Pattern:
                    _layout.Master2Pattern = roiPixel.Clone();
                    break;
                case RoiRole.Master2Search:
                    _layout.Master2Search = roiPixel.Clone();
                    break;
                case RoiRole.Inspection:
                    _layout.Inspection = roiPixel.Clone();
                    SyncCurrentRoiFromInspection(roiPixel);
                    break;
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
            WRect templateRect,
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
                if (!BackendAPI.TryCropToPng(imagePathWin, templateRect, out tplStream, out var tplName, AppendLog))
                {
                    var msg = "crop template failed";
                    AppendLog($"[MATCH] {which} ERROR: {msg}");
                    return (false, null, 0, msg);
                }
                tplStream.Position = 0;
                AppendLog($"[MATCH] {which} bytes(template)={tplStream.Length:n0} rect=({(int)templateRect.X},{(int)templateRect.Y},{(int)templateRect.Width},{(int)templateRect.Height})");
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
                ["ri"] = roi.RInner
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
        private void SaveRoiCropPreview(RoiModel roi, string tag)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !System.IO.File.Exists(_currentImagePathWin))
                {
                    AppendLog("[preview] No hay imagen cargada; no se guarda preview.");
                    return;
                }

                string baseDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_currentImagePathWin) ?? "",
                    "roi_previews");
                System.IO.Directory.CreateDirectory(baseDir);

                var r = RoiToRect(roi);
                using var src = new System.Drawing.Bitmap(_currentImagePathWin);
                var rectInt = new System.Drawing.Rectangle(
                    (int)System.Math.Max(0, r.X),
                    (int)System.Math.Max(0, r.Y),
                    (int)System.Math.Min(r.Width, src.Width - r.X),
                    (int)System.Math.Min(r.Height, src.Height - r.Y));

                if (rectInt.Width <= 0 || rectInt.Height <= 0)
                {
                    AppendLog("[preview] ROI fuera de límites; no se guarda preview.");
                    return;
                }

                using var crop = src.Clone(rectInt, src.PixelFormat);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                string fname = $"{tag}_{ts}.png";
                crop.Save(System.IO.Path.Combine(baseDir, fname), System.Drawing.Imaging.ImageFormat.Png);
                AppendLog($"[preview] Guardado {fname} en {baseDir}");
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
                Tag = "analysis-mark"
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
                Tag = "analysis-mark"
            };
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
                Tag = "analysis-mark"   // para limpiar sólo marcas de análisis
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
            RedrawOverlay(); // tu método existente que repinta los ROIs
            AppendLog("[ANALYZE] Limpiadas marcas de análisis (cruces).");
        }



        private void ClearAnalysisMarks()
        {
            if (OverlayCanvas == null) return;

            // Elimina únicamente las formas con Tag == AnalysisTag (no toca los ROIs ni previews)
            for (int i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (OverlayCanvas.Children[i] is Shape sh &&
                    sh.Tag is string tag &&
                    tag == AnalysisTag)
                {
                    OverlayCanvas.Children.RemoveAt(i);
                }
            }
            AppendLog("[ANALYZE] Limpiadas marcas de análisis (cruces).");
        }

        private void ClearAnalysisMarksOnly()
        {
            // Quita TODO lo que tenga Tag="analysis-mark" y deja intactos los ROIs persistentes
            for (int i = CanvasROI.Children.Count - 1; i >= 0; i--)
            {
                if (CanvasROI.Children[i] is FrameworkElement fe && Equals(fe.Tag, "analysis-mark"))
                    CanvasROI.Children.RemoveAt(i);
            }
        }

        private void RedrawOverlay()
        {
            if (CanvasROI == null) return;

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
                RepositionRotateAdorner();
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
                if (roi.Role == RoiRole.Inspection)
                {
                    ApplyInspectionRotationToShape(shape, roi.AngleDeg);
                }
                AttachRoiAdorner(shape);
            }

            AddPersistentRoi(_layout.Master1Search);
            AddPersistentRoi(_layout.Master1Pattern);
            AddPersistentRoi(_layout.Master2Search);
            AddPersistentRoi(_layout.Master2Pattern);
            AddPersistentRoi(_layout.Inspection);

            if (_layout.Inspection != null)
                SyncCurrentRoiFromInspection(_layout.Inspection);

            RepositionRotateAdorner();
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
            Panel.SetZIndex(shape, style.zIndex);

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            AppendLog($"[overlay] build role={roi.Role} shape={canvasRoi.Shape} bounds=({left:0.##},{top:0.##},{shape.Width:0.##},{shape.Height:0.##}) angle={canvasRoi.AngleDeg:0.##}");

            if (roi.Role == RoiRole.Inspection)
            {
                ApplyInspectionRotationToShape(shape, canvasRoi.AngleDeg);
            }

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
            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null) return;

            var existing = layer.GetAdorners(shape);
            if (existing != null)
            {
                foreach (var ad in existing.OfType<RoiAdorner>())
                    layer.Remove(ad);
            }

            if (shape.Tag is not RoiModel)
                return;

            var adorner = new RoiAdorner(shape, (shapeUpdated, modelUpdated) =>
            {
                var pixelModel = CanvasToImage(modelUpdated);
                UpdateLayoutFromPixel(pixelModel);
                AppendLog($"[adorner] ROI actualizado: {pixelModel.Role} => {DescribeRoi(pixelModel)}");
            }, AppendLog);

            layer.Add(adorner);
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
                Tag = AnalysisTag
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
                Tag = AnalysisTag
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

        private void SetupRoiAdorner()
        {
            var layer = AdornerLayer.GetAdornerLayer(CanvasROI);
            System.Windows.Point CornerProvider() => GetCurrentRoiCornerOnCanvas(RoiCorner.TopRight);
            _rotateAdorner = new RoiRotateAdorner(
                CanvasROI,
                CornerProvider,
                CornerProvider,
                GetCurrentRoiCornerBaselineAngle,
                angle =>
                {
                    CurrentRoi.AngleDeg = angle; // rotación en tiempo real
                    UpdateInspectionShapeRotation(angle);
                    if (_layout?.Inspection != null)
                    {
                        _layout.Inspection.AngleDeg = angle;
                    }
                    CanvasROI.InvalidateVisual();
                },
                CurrentRoi.AngleDeg,
                AppendLog
            );
            layer.Add(_rotateAdorner);
        }

        private Mat GetRotatedCrop(Mat bgr)
        {
            CurrentRoi.EnforceMinSize(10, 10);

            var (cornerX, cornerY) = GetCurrentRoiCornerImage(RoiCorner.TopRight);
            var pivot = new Point2f((float)cornerX, (float)cornerY);

            using var rotMat = Cv2.GetRotationMatrix2D(pivot, CurrentRoi.AngleDeg, 1.0);

            Point2f TransformPoint(double x, double y)
            {
                double newX = rotMat.At<double>(0, 0) * x + rotMat.At<double>(0, 1) * y + rotMat.At<double>(0, 2);
                double newY = rotMat.At<double>(1, 0) * x + rotMat.At<double>(1, 1) * y + rotMat.At<double>(1, 2);
                return new Point2f((float)newX, (float)newY);
            }

            double halfW = CurrentRoi.Width / 2.0;
            double halfH = CurrentRoi.Height / 2.0;
            var rotatedCenter = TransformPoint(CurrentRoi.X, CurrentRoi.Y);

            Mat rotated = new Mat();
            Cv2.WarpAffine(bgr, rotated, rotMat, new OpenCvSharp.Size(bgr.Width, bgr.Height), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(0, 0, 0));

            int x = (int)Math.Round(rotatedCenter.X - halfW);
            int y = (int)Math.Round(rotatedCenter.Y - halfH);
            x = Math.Max(0, Math.Min(x, rotated.Width - 1));
            y = Math.Max(0, Math.Min(y, rotated.Height - 1));
            int w = (int)Math.Max(10, Math.Min(CurrentRoi.Width, rotated.Width - x));
            int h = (int)Math.Max(10, Math.Min(CurrentRoi.Height, rotated.Height - y));

            return new Mat(rotated, new OpenCvSharp.Rect(x, y, w, h));
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
                if (bgrFrame == null || bgrFrame.Empty())
                {
                    MessageBox.Show("No hay imagen cargada.");
                    return;
                }

                // 2) Obtener el crop YA ROTADO desde tu ROI actual
                //    Nota: se asume que tienes implementado GetRotatedCrop(Mat bgr)
                using var crop = GetRotatedCrop(bgrFrame);
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
        private void EnsureRotateAdorner()
        {
            if (_rotateAdornerInitialized)
                return;

            if (CanvasROI == null)
                return;

            double width = CanvasROI.ActualWidth > 0 ? CanvasROI.ActualWidth : CanvasROI.Width;
            double height = CanvasROI.ActualHeight > 0 ? CanvasROI.ActualHeight : CanvasROI.Height;
            if (width <= 0 || height <= 0)
                return;

            SetupRoiRotateAdorner();
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

        private System.Windows.Point GetCurrentRoiCornerOnCanvas(RoiCorner corner)
        {
            var (x, y) = GetCurrentRoiCornerImage(corner);
            return ImagePxToCanvasPt(x, y);
        }

        private double GetCurrentRoiCornerBaselineAngle()
        {
            double halfW = CurrentRoi.Width / 2.0;
            double halfH = CurrentRoi.Height / 2.0;

            if (halfW == 0 && halfH == 0)
                return 0.0;

            return Math.Atan2(halfH, -halfW);
        }

        private void SetupRoiRotateAdorner()
        {
            var layer = AdornerLayer.GetAdornerLayer(CanvasROI);
            if (layer == null) return;

            // Si ya existe, quítalo (evita duplicados)
            var prev = layer.GetAdorners(CanvasROI);
            if (prev != null)
                foreach (var ad in prev)
                    if (ad is RoiRotateAdorner) layer.Remove(ad);

            System.Windows.Point CornerProvider() => GetCurrentRoiCornerOnCanvas(RoiCorner.TopRight);

            var pivotAtSetup = CornerProvider();
            double baselineDeg = GetCurrentRoiCornerBaselineAngle() * 180.0 / Math.PI;
            AppendLog($"[rotate] setup pivot=({pivotAtSetup.X:0.##},{pivotAtSetup.Y:0.##}) handle=({pivotAtSetup.X:0.##},{pivotAtSetup.Y:0.##}) baselineDeg={baselineDeg:0.##} currentDeg={CurrentRoi.AngleDeg:0.##}");

            _rotateAdorner = new RoiRotateAdorner(
                CanvasROI,
                CornerProvider,
                CornerProvider,
                GetCurrentRoiCornerBaselineAngle,
                angle =>
                {
                    var pivot = GetCurrentRoiCornerOnCanvas(RoiCorner.TopRight);
                    AppendLog($"[rotate] callback angle={angle:0.##} pivot=({pivot.X:0.##},{pivot.Y:0.##}) handle=({pivot.X:0.##},{pivot.Y:0.##})");
                    CurrentRoi.AngleDeg = angle;   // rotación en tiempo real
                    if (_state == MasterState.DrawInspection)
                    {
                        if (_tmpBuffer != null)
                        {
                            _tmpBuffer.AngleDeg = angle;
                        }
                        if (_previewShape?.Tag is RoiModel preview)
                        {
                            preview.AngleDeg = angle;
                        }
                    }
                    UpdateInspectionShapeRotation(angle);
                    if (_layout?.Inspection != null)
                    {
                        _layout.Inspection.AngleDeg = angle;
                    }
                },
                CurrentRoi.AngleDeg,
                AppendLog
            );

            layer.Add(_rotateAdorner);
            _rotateAdornerInitialized = true;
        }

        private Shape? FindInspectionShapeOnCanvas()
        {
            if (CanvasROI == null)
                return null;

            if (_state == MasterState.DrawInspection && _previewShape != null)
                return _previewShape;

            return CanvasROI.Children
                .OfType<Shape>()
                .FirstOrDefault(shape =>
                    shape.Tag is RoiModel roi &&
                    roi.Role == RoiRole.Inspection);
        }

        private void ApplyInspectionRotationToShape(Shape shape, double angle)
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

            double pivotLocalX;
            double pivotLocalY;

            switch (roiModel.Shape)
            {
                case RoiShape.Rectangle:
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    pivotLocalX = width;
                    pivotLocalY = 0;
                    break;
                default:
                    pivotLocalX = width / 2.0;
                    pivotLocalY = height / 2.0;
                    break;
            }

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            double pivotCanvasX = left + pivotLocalX;
            double pivotCanvasY = top + pivotLocalY;

            if (shape.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = angle;
                rotate.CenterX = pivotLocalX;
                rotate.CenterY = pivotLocalY;
            }
            else
            {
                shape.RenderTransform = new RotateTransform(angle, pivotLocalX, pivotLocalY);
            }

            AppendLog($"[rotate] apply role={roiModel.Role} shape={roiModel.Shape} pivotLocal=({pivotLocalX:0.##},{pivotLocalY:0.##}) pivotCanvas=({pivotCanvasX:0.##},{pivotCanvasY:0.##}) angle={angle:0.##}");
        }

        private void UpdateInspectionShapeRotation(double angle)
        {
            var inspectionShape = FindInspectionShapeOnCanvas();
            if (inspectionShape == null)
                return;

            ApplyInspectionRotationToShape(inspectionShape, angle);

            if (_layout?.Inspection != null)
            {
                _layout.Inspection.AngleDeg = angle;
            }
        }

        private void RepositionRotateAdorner()
        {
            if (_rotateAdorner == null) return;
            // Forzar repintado para que OnRender del adorner recalcule la posición del handle
            _rotateAdorner.InvalidateVisual();
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

        private System.Windows.Point ImagePxToCanvasPt(double px, double py)
        {
            var displayRect = GetImageDisplayRect();
            if (displayRect.Width <= 0 || displayRect.Height <= 0)
                return new System.Windows.Point(0, 0);

            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0)
                return new System.Windows.Point(0, 0);

            // Traduce usando el mismo helper que otros cálculos y resta el offset del letterbox
            // para llevar el punto al sistema local del CanvasROI.
            var pointInImage = ImgToCanvas(new System.Windows.Point(px, py));
            return new System.Windows.Point(pointInImage.X - displayRect.Left, pointInImage.Y - displayRect.Top);
        }

        private System.Windows.Point CanvasToImage(System.Windows.Point pCanvas)
        {
            var displayRect = GetImageDisplayRect();
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0 || displayRect.Width <= 0 || displayRect.Height <= 0)
                return new System.Windows.Point(0, 0);

            double scale = displayRect.Width / pw;
            return new System.Windows.Point(
                pCanvas.X / scale,
                pCanvas.Y / scale);
        }

        private RoiModel CanvasToImage(RoiModel roiCanvas)
        {
            var result = roiCanvas.Clone();
            var displayRect = GetImageDisplayRect();
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0 || displayRect.Width <= 0 || displayRect.Height <= 0)
                return result;

            double scale = displayRect.Width / pw;

            result.AngleDeg = roiCanvas.AngleDeg;

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiCanvas.X / scale;
                result.Y = roiCanvas.Y / scale;
                result.Width = roiCanvas.Width / scale;
                result.Height = roiCanvas.Height / scale;
                result.CX = result.X + result.Width / 2.0;
                result.CY = result.Y + result.Height / 2.0;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiCanvas.CX / scale;
                result.CY = roiCanvas.CY / scale;
                result.R = roiCanvas.R / scale;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiCanvas.RInner / scale;
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
            var displayRect = GetImageDisplayRect();
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0 || displayRect.Width <= 0 || displayRect.Height <= 0)
                return result;

            double scale = displayRect.Width / pw;

            result.AngleDeg = roiImage.AngleDeg;

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiImage.X * scale;
                result.Y = roiImage.Y * scale;
                result.Width = roiImage.Width * scale;
                result.Height = roiImage.Height * scale;
                result.CX = result.X + result.Width / 2.0;
                result.CY = result.Y + result.Height / 2.0;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiImage.CX * scale;
                result.CY = roiImage.CY * scale;
                result.R = roiImage.R * scale;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiImage.RInner * scale;
                result.X = result.CX - result.R;
                result.Y = result.CY - result.R;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }

            return result;
        }

        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen (letterbox) ===
        private void SyncOverlayToImage()
        {
            if (ImgMain == null || CanvasROI == null) return;
            if (ImgMain.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return;

            var displayRect = GetImageDisplayRect();
            if (displayRect.Width <= 0 || displayRect.Height <= 0) return;

            CanvasROI.HorizontalAlignment = HorizontalAlignment.Left;
            CanvasROI.VerticalAlignment = VerticalAlignment.Top;
            CanvasROI.Margin = new Thickness(displayRect.Left, displayRect.Top, 0, 0);
            CanvasROI.Width = displayRect.Width;
            CanvasROI.Height = displayRect.Height;

            AppendLog($"[sync] Canvas={displayRect.Width:0}x{displayRect.Height:0}  Offset=({displayRect.Left:0},{displayRect.Top:0})  Image={bmp.PixelWidth}x{bmp.PixelHeight}");

            EnsureRotateAdorner();
            RedrawOverlay();
        }




    }
}
