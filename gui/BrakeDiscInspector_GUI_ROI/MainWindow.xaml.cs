// Dialogs
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using BrakeDiscInspector_GUI_ROI.Workflow;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using LegacyROI = BrakeDiscInspector_GUI_ROI.ROI;
using ROI = BrakeDiscInspector_GUI_ROI.RoiModel;
using RoiShapeType = BrakeDiscInspector_GUI_ROI.RoiShape;

using CvPoint  = OpenCvSharp.Point;
using CvRect   = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;
using WpfRect  = System.Windows.Rect;

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
        private bool _hasLoadedImage;

        private WorkflowViewModel? _workflowViewModel;
        private double _heatmapOverlayOpacity = 0.6;

        // === ROI diagnostics ===
        private static readonly string RoiDiagLogPath =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrakeDiscInspector", "logs", "roi_load_coords.log");

        private readonly object _roiDiagLock = new object();
        private string _roiDiagSessionId = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
        private int _roiDiagEventSeq = 0;
        private bool _roiDiagEnabled = true;   // flip to false to silence

        private System.Windows.Media.Imaging.BitmapSource _lastHeatmapBmp;          // heatmap image in image space
        private HeatmapRoiModel _lastHeatmapRoi;              // ROI (image-space) that defines the heatmap clipping area
        private bool _lockAnalyzeScale = true;  // if true, sizes are preserved (scale forced to 1.0) during Analyze Master

        // IMAGE-space centers (pixels) of found masters
        private CvPoint? _lastM1CenterPx;
        private CvPoint? _lastM2CenterPx;

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
        private static readonly string _resizeLogDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrakeDiscInspector", "logs");
        // Si tu overlay se llama distinto, ajusta esta propiedad (o referencia directa en los métodos).
        // Por ejemplo, si en XAML tienes <Canvas x:Name="Overlay"> usa ese nombre aquí.
        private Canvas OverlayCanvas => CanvasROI;

        private const string ANALYSIS_TAG = "analysis-mark";
        // === Helpers de overlay ===
        private const double LabelOffsetX = 10;   // desplazamiento a la derecha de la cruz
        private const double LabelOffsetY = -20;  // desplazamiento hacia arriba de la cruz

        private LegacyROI CurrentRoi = new LegacyROI
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
        private readonly Dictionary<Shape, TextBlock> _roiLabels = new();
        private readonly Dictionary<RoiRole, CheckBox> _roiVisibilityCheckboxes = new();
        private readonly Dictionary<RoiRole, bool> _roiCheckboxHasRoi = new();
        private bool _roiVisibilityRefreshPending;

        private System.Windows.Controls.StackPanel _roiChecksPanel;
        private System.Windows.Controls.CheckBox _chkHeatmap;
        private double _heatmapNormMax = 1.0; // Global heatmap scale (1.0 = default). Lower -> brighter, Higher -> darker.
        private Slider? _sldHeatmapScale;
        private TextBlock? _lblHeatmapScale;

        // Cache of last gray heatmap to recolor on-the-fly
        private byte[]? _lastHeatmapGray;
        private int _lastHeatmapW, _lastHeatmapH;

        private IEnumerable<RoiModel> SavedRois => new[]
        {
            _layout.Master1Pattern,
            _layout.Master1Search,
            _layout.Master2Pattern,
            _layout.Master2Search,
            _layout.Inspection
        }.OfType<RoiModel>();

        private sealed class HeatmapRoiModel : RoiModel
        {
            public static HeatmapRoiModel From(RoiModel src)
            {
                if (src == null)
                    return null;

                if (src is HeatmapRoiModel existing)
                    return existing;

                return new HeatmapRoiModel
                {
                    Id = src.Id,
                    Label = src.Label,
                    Shape = src.Shape,
                    Role = src.Role,
                    AngleDeg = src.AngleDeg,
                    X = src.X,
                    Y = src.Y,
                    Width = src.Width,
                    Height = src.Height,
                    CX = src.CX,
                    CY = src.CY,
                    R = src.R,
                    RInner = src.RInner
                };
            }

            public RoiShapeType ShapeType => (RoiShapeType)Shape;
        }
        // ---------- Logging helpers ----------

        private void RoiDiagLog(string line)
        {
            if (!_roiDiagEnabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RoiDiagLogPath)!);
                var sb = new StringBuilder(256);
                sb.Append("[").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                sb.Append("#").Append(++_roiDiagEventSeq).Append(" ");
                sb.Append("[").Append(_roiDiagSessionId).Append("] ");
                sb.Append(line);
                var text = sb.ToString() + Environment.NewLine;
                lock (_roiDiagLock)
                {
                    File.AppendAllText(RoiDiagLogPath, text, Encoding.UTF8);
                }
            }
            catch { /* never throw from logging */ }
        }

        // Pretty print rectangle and center
        private static string FRect(double L, double T, double W, double H)
            => $"L={L:F3},T={T:F3},W={W:F3},H={H:F3},CX={(L+W*0.5):F3},CY={(T+H*0.5):F3}";

        private static string FRoiImg(RoiModel r)
        {
            if (r == null) return "<null>";
            return $"Role={r.Role} Img(L={r.Left:F3},T={r.Top:F3},W={r.Width:F3},H={r.Height:F3},CX={r.CX:F3},CY={r.CY:F3},R={r.R:F3},Rin={r.RInner:F3})";
        }

        // Dump current Image→Canvas transform and related surfaces
        private void RoiDiagDumpTransform(string where)
        {
            try
            {
                // image source size
                int srcW = 0, srcH = 0;
                try
                {
                    var bs = ImgMain?.Source as System.Windows.Media.Imaging.BitmapSource;
                    if (bs != null) { srcW = bs.PixelWidth; srcH = bs.PixelHeight; }
                } catch {}

                // viewport and canvas sizes
                double imgVW = ImgMain?.ActualWidth  ?? 0;
                double imgVH = ImgMain?.ActualHeight ?? 0;
                double canW  = CanvasROI?.ActualWidth  ?? 0;
                double canH  = CanvasROI?.ActualHeight ?? 0;

                // project’s transform (sx,sy,offX,offY)
                var t = GetImageToCanvasTransform();
                double sx = t.Item1, sy = t.Item2, offX = t.Item3, offY = t.Item4;

                RoiDiagLog($"[{where}] ImgSrc={srcW}x{srcH} ImgView={imgVW:F3}x{imgVH:F3} CanvasROI={canW:F3}x{canH:F3}  Transform: sx={sx:F9}, sy={sy:F9}, offX={offX:F3}, offY={offY:F3}  Stretch={ImgMain?.Stretch}");
            }
            catch (System.Exception ex)
            {
                RoiDiagLog($"[{where}] DumpTransform EX: {ex.Message}");
            }
        }

        // Convert image→canvas for a RoiModel using existing project conversion
        private System.Windows.Rect RoiDiagImageToCanvasRect(RoiModel r)
        {
            // Use existing method that the app already relies on
            var rc = ImageToCanvas(r);
            return new System.Windows.Rect(rc.Left, rc.Top, rc.Width, rc.Height);
        }

        // Try to find a UI element for a ROI and read its actual canvas placement
        private bool RoiDiagTryFindUiRect(RoiModel r, out System.Windows.Rect uiRect, out string name)
        {
            uiRect = new System.Windows.Rect(); name = "";
            try
            {
                if (CanvasROI == null || r == null) return false;
                // Heuristics: children that are FrameworkElement with Name/Tag containing the Role or "roi"
                foreach (var o in CanvasROI.Children)
                {
                    if (o is System.Windows.FrameworkElement fe)
                    {
                        var tag = fe.Tag as string;
                        var nm  = fe.Name ?? "";
                        bool matches =
                            (!string.IsNullOrEmpty(tag) && tag.IndexOf("roi", System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (!string.IsNullOrEmpty(nm)  && nm.IndexOf(r.Role.ToString(), System.StringComparison.OrdinalIgnoreCase) >= 0);

                        if (!matches) continue;

                        double L = System.Windows.Controls.Canvas.GetLeft(fe);
                        double T = System.Windows.Controls.Canvas.GetTop(fe);
                        double W = fe.ActualWidth;
                        double H = fe.ActualHeight;
                        if (double.IsNaN(L)) L = 0;
                        if (double.IsNaN(T)) T = 0;
                        uiRect = new System.Windows.Rect(L, T, W, H);
                        name = string.IsNullOrEmpty(nm) ? (tag ?? fe.GetType().Name) : nm;
                        return true;
                    }
                }
            }
            catch {}
            return false;
        }

        // Dump expected canvas rect vs. actual UI rect (if any)
        private void RoiDiagDumpRoi(string where, string label, RoiModel r)
        {
            try
            {
                if (r == null)
                {
                    RoiDiagLog($"[{where}] {label}: <null>");
                    return;
                }
                var rcExp = RoiDiagImageToCanvasRect(r);
                string line = $"[{where}] {label}: IMG({FRoiImg(r)})  EXP-CANVAS({FRect(rcExp.Left, rcExp.Top, rcExp.Width, rcExp.Height)})";

                if (RoiDiagTryFindUiRect(r, out var rcUi, out var nm))
                {
                    double dx = rcUi.Left - rcExp.Left;
                    double dy = rcUi.Top  - rcExp.Top;
                    double dw = rcUi.Width  - rcExp.Width;
                    double dh = rcUi.Height - rcExp.Height;
                    line += $"  UI[{nm}]({FRect(rcUi.Left, rcUi.Top, rcUi.Width, rcUi.Height)})  Δpos=({dx:F3},{dy:F3}) Δsize=({dw:F3},{dh:F3})";
                }
                RoiDiagLog(line);
            }
            catch (System.Exception ex)
            {
                RoiDiagLog($"[{where}] {label}: EX: {ex.Message}");
            }
        }

        // Snapshot of ALL canvas children (for forensic inspection)
        private void RoiDiagDumpCanvasChildren(string where)
        {
            try
            {
                if (CanvasROI == null) { RoiDiagLog($"[{where}] CanvasROI=<null>"); return; }
                RoiDiagLog($"[{where}] CanvasROI children count = {CanvasROI.Children.Count}");
                foreach (var o in CanvasROI.Children)
                {
                    if (o is System.Windows.FrameworkElement fe)
                    {
                        double L = System.Windows.Controls.Canvas.GetLeft(fe);
                        double T = System.Windows.Controls.Canvas.GetTop(fe);
                        double W = fe.ActualWidth;
                        double H = fe.ActualHeight;
                        if (double.IsNaN(L)) L = 0;
                        if (double.IsNaN(T)) T = 0;
                        string nm = fe.Name ?? fe.GetType().Name;
                        string tg = fe.Tag?.ToString() ?? "";
                        RoiDiagLog($"    FE: {nm}  Tag='{tg}'  {FRect(L,T,W,H)}  Z={System.Windows.Controls.Panel.GetZIndex(fe)}");
                    }
                    else
                    {
                        RoiDiagLog($"    Child: {o?.GetType().Name ?? "<null>"}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoiDiagLog($"[{where}] DumpCanvasChildren EX: {ex.Message}");
            }
        }

        private static readonly string HeatmapLogPath = @"C:\BDI\logs\gui_heatmap.log";

        private static void LogHeatmap(string msg)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(HeatmapLogPath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
                System.IO.File.AppendAllText(HeatmapLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { /* swallow logging errors */ }
        }

        private void KeepOnlyMaster2InCanvas()
        {
            if (CanvasROI == null) return;

            var toRemove = new System.Collections.Generic.List<System.Windows.UIElement>();
            int kept = 0, removed = 0;

            foreach (var el in CanvasROI.Children.Cast<System.Windows.UIElement>())
            {
                switch (el)
                {
                    case System.Windows.Shapes.Shape s:
                    {
                        // Shapes de ROI llevan Tag = RoiModel; los de análisis suelen llevar string ("analysis-mark"/"AnalysisCross") o null
                        if (s.Tag is RoiModel rm)
                        {
                            bool keep = (rm.Role == RoiRole.Master2Pattern) || (rm.Role == RoiRole.Master2Search);
                            if (!keep) { toRemove.Add(s); removed++; } else { kept++; }
                        }
                        else
                        {
                            // Cualquier shape sin RoiModel en Tag NO pertenece a ROI Master 2 (líneas/analysis/etc.) → eliminar
                            toRemove.Add(s); removed++;
                        }
                        break;
                    }

                    case System.Windows.Controls.TextBlock tb:
                    {
                        // Las etiquetas de ROI no usan Tag; se nombran como "roiLabel_<texto_sin_espacios>"
                        // Para "Master 2" el Name es "roiLabel_Master_2"
                        string name = tb.Name ?? string.Empty;
                        bool keep = name.StartsWith("roiLabel_Master_2", System.StringComparison.OrdinalIgnoreCase);
                        if (!keep) { toRemove.Add(tb); removed++; } else { kept++; }
                        break;
                    }

                    default:
                        // Cualquier otro UIElement (Borders de análisis, etc.) → eliminar
                        toRemove.Add(el); removed++;
                        break;
                }
            }

            foreach (var el in toRemove)
                CanvasROI.Children.Remove(el);

            try { LogHeatmap($"KeepOnlyMaster2InCanvas: kept={kept}, removed={removed}"); } catch {}
        }

        private static string RoiDebug(RoiModel r)
        {
            if (r == null) return "<null>";
            string shape = InferShapeName(r);
            return $"Role={r.Role}, Shape={shape}, Img(L={r.Left},T={r.Top},W={r.Width},H={r.Height},CX={r.CX},CY={r.CY},R={r.R},Rin={r.RInner})";
        }

        private static string InferShapeName(RoiModel r)
        {
            try
            {
                // Annulus if both outer and inner radii are present/positive
                if (r.RInner > 0 && r.R > 0) return "Annulus";

                // Circle if we have a positive radius OR bbox looks square-ish
                if (r.R > 0) return "Circle";

                // As a fallback, detect square-ish bbox as circle, else rectangle
                // (Tolerance = 2% of the larger side)
                double w = r.Width, h = r.Height;
                double tol = 0.02 * System.Math.Max(System.Math.Abs(w), System.Math.Abs(h));
                if (System.Math.Abs(w - h) <= tol && w > 0 && h > 0) return "Circle";

                return "Rectangle";
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string RectDbg(System.Windows.Rect rc)
            => $"(X={rc.X:F2},Y={rc.Y:F2},W={rc.Width:F2},H={rc.Height:F2})";
        // ---------- End helpers ----------


        private void UpdateRoiLabelPosition(Shape shape)
        {
            if (shape == null) return;

            // Accept both ROI (Legend) and RoiModel (Label)
            object tag = shape.Tag;
            string legendOrLabel = null;
            if (tag is ROI rTag)        legendOrLabel = rTag.Legend;
            else if (tag is RoiModel m) legendOrLabel = m.Label;

            string labelName = "roiLabel_" + ((legendOrLabel ?? string.Empty).Replace(" ", "_"));
            var label = CanvasROI.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Name == labelName);
            if (label == null) return;

            // If Left/Top are not ready yet, defer positioning to next layout pass
            double left = Canvas.GetLeft(shape);
            double top  = Canvas.GetTop(shape);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateRoiLabelPosition(shape)), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            // Measure and place label just above ROI bbox (4 px gap)
            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double textH = label.DesiredSize.Height;
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label,  top - (textH + 4));
        }

        private void EnsureRoiLabel(Shape shape, object roi)
        {
            if (CanvasROI == null)
                return;

            _roiLabels.TryGetValue(shape, out var previous);

            // Build label text and key supporting both types
            string _lbl;
            if (roi is LegacyROI rObj)
                _lbl = string.IsNullOrWhiteSpace(rObj.Legend) ? "ROI" : rObj.Legend;
            else if (roi is RoiModel mObj)
                _lbl = string.IsNullOrWhiteSpace(mObj.Label) ? "ROI" : mObj.Label;
            else
                _lbl = "ROI";

            string labelName = "roiLabel_" + _lbl.Replace(" ", "_");
            var existing = CanvasROI.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Name == labelName);
            var label = existing ?? new TextBlock { Name = labelName };

            label.Text = _lbl;
            EnsureRoiCheckbox(_lbl);

            if (existing == null)
            {
                label.FontFamily = new FontFamily("Segoe UI");
                label.FontSize = 12;
                label.FontWeight = FontWeights.SemiBold;
                label.IsHitTestVisible = false;
                label.Foreground = Brushes.White;

                CanvasROI.Children.Add(label);
                Panel.SetZIndex(label, int.MaxValue);
            }

            if (previous != null && !ReferenceEquals(previous, label))
            {
                bool usedElsewhere = _roiLabels.Any(kv => !ReferenceEquals(kv.Key, shape) && ReferenceEquals(kv.Value, previous));
                if (!usedElsewhere && CanvasROI.Children.Contains(previous))
                {
                    CanvasROI.Children.Remove(previous);
                }
            }

            _roiLabels[shape] = label;
        }

        private System.Windows.Controls.StackPanel GetOrCreateRoiChecksHost()
        {
            if (_roiChecksPanel != null) return _roiChecksPanel;

            // Intentar ubicar un panel de controles existente por nombre común
            string[] knownHosts = { "ControlsPanel", "RightPanel", "SidebarPanel", "RightToolbar" };
            foreach (var hostName in knownHosts)
            {
                if (this.FindName(hostName) is System.Windows.Controls.Panel p)
                {
                    var sp = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        Margin = new System.Windows.Thickness(6)
                    };
                    p.Children.Add(new System.Windows.Controls.GroupBox
                    {
                        Header = "Overlays",
                        Content = sp,
                        Margin = new System.Windows.Thickness(6,12,6,6)
                    });
                    _roiChecksPanel = sp;
                    return _roiChecksPanel;
                }
            }

            // Fallback: crear un host flotante en la esquina superior derecha del root Grid
            if (this.Content is System.Windows.Controls.Grid rootGrid)
            {
                var sp = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Margin = new System.Windows.Thickness(8)
                };
                var box = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 30, 30, 30)),
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Padding = new System.Windows.Thickness(8),
                    Child = new System.Windows.Controls.GroupBox
                    {
                        Header = "Overlays",
                        Content = sp
                    },
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top
                };
                System.Windows.Controls.Grid.SetRow(box, 0);
                rootGrid.Children.Add(box);
                System.Windows.Controls.Panel.SetZIndex(box, 9999);
                _roiChecksPanel = sp;
                return _roiChecksPanel;
            }

            // Último recurso: crear un panel local (no persistente visualmente si no hay contenedor)
            _roiChecksPanel = new System.Windows.Controls.StackPanel();
            return _roiChecksPanel;
        }

        private void EnsureHeatmapScaleSlider()
        {
            var host = GetOrCreateRoiChecksHost();

            if (_sldHeatmapScale != null && _lblHeatmapScale != null)
            {
                _lblHeatmapScale.Text = $"Heatmap Scale: {_heatmapNormMax:0.00}";
                return;
            }

            // Header label
            _lblHeatmapScale = new TextBlock
            {
                Text = $"Heatmap Scale: {_heatmapNormMax:0.00}",
                Margin = new Thickness(2, 6, 2, 2),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            };

            // Slider: range 0.10 .. 2.00 (avoid zero)
            _sldHeatmapScale = new Slider
            {
                Minimum = 0.10,
                Maximum = 2.00,
                Value = _heatmapNormMax,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = false,
                Margin = new Thickness(2, 0, 2, 8),
                Width = 180
            };

            _sldHeatmapScale.ValueChanged += (s, e) =>
            {
                _heatmapNormMax = _sldHeatmapScale!.Value;
                _lblHeatmapScale!.Text = $"Heatmap Scale: {_heatmapNormMax:0.00}";
                try { RebuildHeatmapOverlayFromCache(); } catch {}
            };

            // Insert near the top (after Heatmap checkbox if present)
            int insertAt = 0;
            for (int i = 0; i < host.Children.Count; i++)
            {
                if (host.Children[i] is CheckBox cb && (cb.Content as string) == "Heatmap")
                {
                    insertAt = i + 1;
                    break;
                }
            }
            host.Children.Insert(insertAt, _lblHeatmapScale);
            host.Children.Insert(insertAt + 1, _sldHeatmapScale);
        }

        private void EnsureHeatmapCheckbox()
        {
            var host = GetOrCreateRoiChecksHost();
            if (_chkHeatmap != null) return;

            _chkHeatmap = new System.Windows.Controls.CheckBox
            {
                Content = "Heatmap",
                IsChecked = true,
                Margin = new System.Windows.Thickness(2, 0, 2, 6)
            };
            _chkHeatmap.Foreground = Brushes.White;
            _chkHeatmap.FontSize = 13;
            _chkHeatmap.Checked += (s, e) => { if (HeatmapOverlay != null) HeatmapOverlay.Visibility = System.Windows.Visibility.Visible; };
            _chkHeatmap.Unchecked += (s, e) => { if (HeatmapOverlay != null) HeatmapOverlay.Visibility = System.Windows.Visibility.Collapsed; };

            host.Children.Insert(0, _chkHeatmap);
        }

        private void EnsureRoiCheckbox(string labelText)
        {
            if (string.IsNullOrWhiteSpace(labelText)) return;
            var host = GetOrCreateRoiChecksHost();

            // Buscar si ya existe un CheckBox con ese mismo texto
            foreach (var child in host.Children)
            {
                if (child is System.Windows.Controls.CheckBox cb && cb.Content is string s && s.Equals(labelText, System.StringComparison.OrdinalIgnoreCase))
                {
                    // Ya existe; nada que hacer
                    return;
                }
            }

            // Crear nuevo checkbox (sin lógica de toggle sobre shapes; solo UI, según requisito)
            var chk = new System.Windows.Controls.CheckBox
            {
                Content = labelText,
                IsChecked = true,
                Margin = new System.Windows.Thickness(2, 0, 2, 2)
            };
            chk.Foreground = Brushes.White;
            chk.FontSize = 13;
            host.Children.Add(chk);
        }

        private void RemoveRoiLabel(Shape shape)
        {
            if (CanvasROI == null)
                return;

            if (_roiLabels.TryGetValue(shape, out var label))
            {
                CanvasROI.Children.Remove(label);
                _roiLabels.Remove(shape);
            }
        }

        private bool _syncScheduled;
        private int _syncRetryCount;
        private const int MaxSyncRetries = 3;
        // overlay diferido
        private bool _overlayNeedsRedraw;
        private bool _adornerHadDelta;
        private bool _analysisViewActive;

        private void AppendResizeLog(string msg)
        {
            try
            {
                if (!System.IO.Directory.Exists(_resizeLogDir))
                    System.IO.Directory.CreateDirectory(_resizeLogDir);
                string path = System.IO.Path.Combine(_resizeLogDir, $"resize-debug-{DateTime.Now:yyyyMMdd}.txt");
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // swallow logging errors
            }
        }
        public MainWindow()
        {
            InitializeComponent();
            this.SizeChanged += (s,e) =>
            {
                try
                {
                    RoiDiagDumpTransform("sizechanged");
                    if (_layout != null)
                    {
                        RoiDiagDumpRoi("sizechanged", "Master1Pattern", _layout.Master1Pattern);
                        RoiDiagDumpRoi("sizechanged", "Master1Search ", _layout.Master1Search);
                        RoiDiagDumpRoi("sizechanged", "Master2Pattern", _layout.Master2Pattern);
                        RoiDiagDumpRoi("sizechanged", "Master2Search ", _layout.Master2Search);
                        RoiDiagDumpRoi("sizechanged", "Inspection   ", _layout.Inspection);
                    }
                    if (_lastHeatmapRoi != null)
                        RoiDiagDumpRoi("sizechanged", "HeatmapROI   ", _lastHeatmapRoi);
                    Dispatcher.InvokeAsync(() => RoiDiagDumpCanvasChildren("sizechanged:UI-snapshot"),
                                           System.Windows.Threading.DispatcherPriority.Render);
                }
                catch {}
            };
            EnsureHeatmapCheckbox();
            EnsureHeatmapScaleSlider();
            if (_sldHeatmapScale != null)
                _sldHeatmapScale.ValueChanged += HeatmapScaleSlider_ValueChangedSync;

            try
            {
                var ps = System.Windows.PresentationSource.FromVisual(this);
                if (ps?.CompositionTarget != null)
                {
                    Matrix m = ps.CompositionTarget.TransformToDevice;
                    LogHeatmap($"DPI Scale = ({m.M11:F3}, {m.M22:F3})");
                }
            }
            catch {}

            // RoiOverlay disabled: labels are now drawn on Canvas only
            // RoiOverlay.BindToImage(ImgMain);

            // RoiOverlay disabled: labels are now drawn on Canvas only
            // ImgMain.SizeChanged += (_, __) => RoiOverlay.InvalidateOverlay();
            // SizeChanged += (_, __) => RoiOverlay.InvalidateOverlay();
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

            InitRoiVisibilityControls();

            UpdateWizardState();
            ApplyPresetToUI(_preset);
        }

        private void EnablePresetsTab(bool enable)
        {
            if (FindName("TabPresets") is System.Windows.Controls.TabItem t)
                t.IsEnabled = enable;
        }

        private void InitRoiVisibilityControls()
        {
            _roiVisibilityCheckboxes.Clear();
            _roiCheckboxHasRoi.Clear();

            MapRoiCheckbox(RoiRole.Master1Pattern, ChkShowMaster1Pattern);
            MapRoiCheckbox(RoiRole.Master1Search, ChkShowMaster1Inspection);
            MapRoiCheckbox(RoiRole.Master2Pattern, ChkShowMaster2Pattern);
            MapRoiCheckbox(RoiRole.Master2Search, ChkShowMaster2Inspection);
            MapRoiCheckbox(RoiRole.Inspection, ChkShowInspectionRoi);

            UpdateRoiVisibilityControls();
        }

        private void MapRoiCheckbox(RoiRole role, CheckBox? checkbox)
        {
            if (checkbox == null)
                return;

            _roiVisibilityCheckboxes[role] = checkbox;
            _roiCheckboxHasRoi[role] = false;
            checkbox.IsEnabled = false;
            checkbox.IsChecked = false;
        }

        private void UpdateRoiVisibilityControls()
        {
            if (_roiVisibilityCheckboxes.Count == 0)
                return;

            UpdateRoiVisibilityCheckbox(RoiRole.Master1Pattern, _layout.Master1Pattern);
            UpdateRoiVisibilityCheckbox(RoiRole.Master1Search, _layout.Master1Search);
            UpdateRoiVisibilityCheckbox(RoiRole.Master2Pattern, _layout.Master2Pattern);
            UpdateRoiVisibilityCheckbox(RoiRole.Master2Search, _layout.Master2Search);
            UpdateRoiVisibilityCheckbox(RoiRole.Inspection, _layout.Inspection);

            RequestRoiVisibilityRefresh();
        }

        private void UpdateRoiVisibilityCheckbox(RoiRole role, RoiModel? model)
        {
            if (!_roiVisibilityCheckboxes.TryGetValue(role, out var checkbox) || checkbox == null)
                return;

            bool hasRoi = model != null;
            bool prevHasRoi = _roiCheckboxHasRoi.TryGetValue(role, out var prev) && prev;

            checkbox.IsEnabled = hasRoi;

            if (!hasRoi)
            {
                checkbox.IsChecked = false;
            }
            else if (!prevHasRoi && checkbox.IsChecked != true)
            {
                checkbox.IsChecked = true;
            }

            _roiCheckboxHasRoi[role] = hasRoi;
        }

        private void RoiVisibilityCheckChanged(object sender, RoutedEventArgs e)
        {
            RequestRoiVisibilityRefresh();
        }

        private void RequestRoiVisibilityRefresh()
        {
            if (_roiVisibilityRefreshPending)
                return;

            _roiVisibilityRefreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _roiVisibilityRefreshPending = false;
                ApplyRoiVisibilityFromCheckboxes();
            }), DispatcherPriority.Render);
        }

        private void ApplyRoiVisibilityFromCheckboxes()
        {
            if (CanvasROI == null)
                return;

            foreach (var shape in CanvasROI.Children.OfType<Shape>())
            {
                if (shape.Tag is not RoiModel roi)
                    continue;

                bool visible = IsRoiRoleVisible(roi.Role);
                shape.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

                if (_roiLabels.TryGetValue(shape, out var label) && label != null)
                {
                    label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private bool IsRoiRoleVisible(RoiRole role)
        {
            if (_roiVisibilityCheckboxes.TryGetValue(role, out var checkbox) && checkbox != null)
            {
                return checkbox.IsChecked != false;
            }

            return true;
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
            EnablePresetsTab(mastersReady || _hasLoadedImage);     // permite presets tras cargar imagen o completar masters

            // Selección de tab acorde a estado
            if (_state == MasterState.DrawM1_Pattern || _state == MasterState.DrawM1_Search)
                Tabs.SelectedItem = TabMaster1;
            else if (_state == MasterState.DrawM2_Pattern || _state == MasterState.DrawM2_Search)
                Tabs.SelectedItem = TabMaster2;
            else if (_state == MasterState.DrawInspection)
                Tabs.SelectedItem = TabInspection;
            else
                Tabs.SelectedItem = TabPresets;

            if (_analysisViewActive && _state != MasterState.Ready)
            {
                ResetAnalysisMarks();
            }

            // Botón "Analizar Master" disponible en cuanto M1+M2 estén definidos
            BtnAnalyzeMaster.IsEnabled = mastersReady;

            UpdateRoiVisibilityControls();
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
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // RoiOverlay.InvalidateOverlay();
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

            ClearCanvasShapesAndLabels();   // remove all shapes & labels from previous image
            _roiShapesById.Clear();
            _roiLabels.Clear();
            try { if (_previewShape != null) { CanvasROI.Children.Remove(_previewShape); _previewShape = null; } } catch { }
            RedrawOverlay();

            ScheduleSyncOverlay(force: true);

            AppendLog($"Imagen cargada: {_imgW}x{_imgH}  (Canvas: {CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0})");
            RedrawOverlaySafe();
            ClearHeatmapOverlay();

            // === ROI DIAG: after image load & overlay sync ===
            try
            {
                RoiDiagDumpTransform("imgload:after-sync");

                // Dump all known ROIs from layout
                if (_layout != null)
                {
                    RoiDiagDumpRoi("imgload", "Master1Pattern", _layout.Master1Pattern);
                    RoiDiagDumpRoi("imgload", "Master1Search ", _layout.Master1Search);
                    RoiDiagDumpRoi("imgload", "Master2Pattern", _layout.Master2Pattern);
                    RoiDiagDumpRoi("imgload", "Master2Search ", _layout.Master2Search);
                    RoiDiagDumpRoi("imgload", "Inspection   ", _layout.Inspection);
                }
                // Heatmap ROI if available
                if (_lastHeatmapRoi != null)
                    RoiDiagDumpRoi("imgload", "HeatmapROI   ", _lastHeatmapRoi);

                // Snapshot of canvas children after layout
                Dispatcher.InvokeAsync(() => RoiDiagDumpCanvasChildren("imgload:UI-snapshot"),
                                       System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (System.Exception ex)
            {
                RoiDiagLog("imgload: diagnostics EX: " + ex.Message);
            }

            _hasLoadedImage = true;
            EnablePresetsTab(true);
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





        private void RemoveRoiShape(Shape shape)
        {
            if (CanvasROI == null)
                return;

            RemoveRoiAdorners(shape);
            RemoveRoiLabel(shape);
            CanvasROI.Children.Remove(shape);
        }

        private void ClearCanvasShapesAndLabels()
        {
            try
            {
                var shapes = CanvasROI.Children.OfType<System.Windows.Shapes.Shape>().ToList();
                foreach (var s in shapes) CanvasROI.Children.Remove(s);
                var labels = CanvasROI.Children.OfType<System.Windows.Controls.TextBlock>().ToList();
                foreach (var l in labels) CanvasROI.Children.Remove(l);
            }
            catch { /* ignore */ }
        }

        private void ClearCanvasInternalMaps()
        {
            try
            {
                _roiShapesById?.Clear();
                _roiLabels?.Clear();
            }
            catch { /* ignore */ }
        }

        private void DetachPreviewAndAdorner()
        {
            try
            {
                if (_previewShape != null)
                {
                    try
                    {
                        var al = AdornerLayer.GetAdornerLayer(_previewShape);
                        if (al != null)
                        {
                            var adorners = al.GetAdorners(_previewShape);
                            if (adorners != null)
                            {
                                foreach (var ad in adorners.OfType<RoiAdorner>())
                                    al.Remove(ad);
                            }
                        }
                    }
                    catch { /* ignore */ }

                    CanvasROI.Children.Remove(_previewShape);
                    _previewShape = null;
                }
            }
            catch { }
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
                RemoveRoiShape(shape);
            }

            _roiShapesById.Clear();
            _roiLabels.Clear();
        }

        private void RedrawOverlay()
        {
            AppendResizeLog($"[redraw] start: CanvasROI={CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}");
            if (CanvasROI == null || _imgW <= 0 || _imgH <= 0)
                return;

            // Remove orphan labels whose ROI no longer exists
            try
            {
                var validKeys = new HashSet<string>(SavedRois.Select(rm =>
                {
                    string lbl = (!string.IsNullOrWhiteSpace(rm.Label)) ? rm.Label : ResolveRoiLabelText(rm);
                    lbl = string.IsNullOrWhiteSpace(lbl) ? "ROI" : lbl;
                    return "roiLabel_" + lbl.Replace(" ", "_");
                }));

                var toRemove = CanvasROI.Children.OfType<TextBlock>()
                    .Where(tb => tb.Name.StartsWith("roiLabel_") && !validKeys.Contains(tb.Name))
                    .ToList();
                foreach (var tb in toRemove) CanvasROI.Children.Remove(tb);
            }
            catch { /* ignore */ }

            var activeRois = SavedRois.Where(roi => roi != null).ToList();
            var activeIds = new HashSet<string>(activeRois.Select(roi => roi.Id));

            foreach (var kv in _roiShapesById.ToList())
            {
                if (!activeIds.Contains(kv.Key))
                {
                    RemoveRoiShape(kv.Value);
                    _roiShapesById.Remove(kv.Key);
                }
            }

            var (sx, sy, ox, oy) = GetImageToCanvasTransform();
            if (sx <= 0.0 || sy <= 0.0)
            {
                AppendLog("[overlay] skipped redraw (transform invalid)");
                return;
            }

            double k = Math.Min(sx, sy);

            foreach (var roi in activeRois)
            {
                if (!_roiShapesById.TryGetValue(roi.Id, out var shape) || shape == null)
                {
                    shape = CreateLayoutShape(roi);
                    if (shape == null)
                    {
                        AppendLog($"[overlay] build failed for {roi.Role} ({roi.Label})");
                        continue;
                    }

                    CanvasROI.Children.Add(shape);
                    _roiShapesById[roi.Id] = shape;

                    if (ShouldEnableRoiEditing(roi.Role))
                    {
                        AttachRoiAdorner(shape);
                    }
                }

                // Keep saved ROI visible (do not hide stroke/fill)
                try
                {
                    var style = GetRoiStyle(roi.Role);
                    shape.Stroke = style.stroke;
                    shape.Fill = style.fill;
                    shape.StrokeThickness = Math.Max(1.0, shape.StrokeThickness);
                    if (style.dash != null)
                        shape.StrokeDashArray = style.dash;
                    else
                        shape.StrokeDashArray = null;
                    shape.IsHitTestVisible = true;
                    Panel.SetZIndex(shape, style.zIndex);
                }
                catch
                {
                    // Fallback if style not available
                    shape.Stroke = Brushes.White;
                    shape.Fill = Brushes.Transparent;
                    shape.StrokeThickness = 1.0;
                    shape.StrokeDashArray = null;
                    shape.IsHitTestVisible = true;
                    Panel.SetZIndex(shape, 5);
                }

                if (shape.Tag is not RoiModel canvasRoi)
                {
                    canvasRoi = roi.Clone();
                    shape.Tag = canvasRoi;
                }

                // Ensure the saved ROI label is synced to the canvas clone
                if (canvasRoi is RoiModel)
                {
                    canvasRoi.Label = roi.Label;
                }

                canvasRoi.Role = roi.Role;
                canvasRoi.AngleDeg = roi.AngleDeg;
                canvasRoi.Shape = roi.Shape;

                switch (roi.Shape)
                {
                    case RoiShape.Rectangle:
                        {
                            double left = ox + roi.Left * sx;
                            double top = oy + roi.Top * sy;
                            double width = Math.Max(1.0, roi.Width * sx);
                            double height = Math.Max(1.0, roi.Height * sy);
                            double centerX = left + width / 2.0;
                            double centerY = top + height / 2.0;

                            Canvas.SetLeft(shape, left);
                            Canvas.SetTop(shape, top);
                            shape.Width = width;
                            shape.Height = height;

                            canvasRoi.Width = width;
                            canvasRoi.Height = height;
                            canvasRoi.Left = left;
                            canvasRoi.Top = top;
                            canvasRoi.X = centerX;
                            canvasRoi.Y = centerY;
                            canvasRoi.CX = centerX;
                            canvasRoi.CY = centerY;
                            canvasRoi.R = Math.Max(width, height) / 2.0;
                            canvasRoi.RInner = 0;
                            break;
                        }
                    case RoiShape.Circle:
                        {
                            double cxImg = roi.CX;
                            double cyImg = roi.CY;
                            double dImg = roi.R * 2.0;

                            double cx = ox + cxImg * sx;
                            double cy = oy + cyImg * sy;
                            double d = Math.Max(1.0, dImg * k);

                            Canvas.SetLeft(shape, cx - d / 2.0);
                            Canvas.SetTop(shape, cy - d / 2.0);
                            shape.Width = d;
                            shape.Height = d;

                            canvasRoi.Width = d;
                            canvasRoi.Height = d;
                            canvasRoi.Left = cx - d / 2.0;
                            canvasRoi.Top = cy - d / 2.0;
                            canvasRoi.CX = cx;
                            canvasRoi.CY = cy;
                            canvasRoi.X = cx;
                            canvasRoi.Y = cy;
                            canvasRoi.R = d / 2.0;
                            canvasRoi.RInner = 0;
                            break;
                        }
                    case RoiShape.Annulus:
                        {
                            double cxImg = roi.CX;
                            double cyImg = roi.CY;
                            double dImg = roi.R * 2.0;

                            double cx = ox + cxImg * sx;
                            double cy = oy + cyImg * sy;
                            double d = Math.Max(1.0, dImg * k);

                            Canvas.SetLeft(shape, cx - d / 2.0);
                            Canvas.SetTop(shape, cy - d / 2.0);
                            shape.Width = d;
                            shape.Height = d;

                            if (shape is AnnulusShape ann)
                            {
                                double innerCanvas = Math.Max(0.0, Math.Min(roi.RInner * k, d / 2.0));
                                ann.InnerRadius = innerCanvas;
                                canvasRoi.RInner = innerCanvas;
                            }

                            canvasRoi.Width = d;
                            canvasRoi.Height = d;
                            canvasRoi.Left = cx - d / 2.0;
                            canvasRoi.Top = cy - d / 2.0;
                            canvasRoi.CX = cx;
                            canvasRoi.CY = cy;
                            canvasRoi.X = cx;
                            canvasRoi.Y = cy;
                            canvasRoi.R = d / 2.0;
                            break;
                        }
                }

                // Ensure shape.Tag references the ROI clone for downstream logic
                shape.Tag = canvasRoi;

                string _lbl;
                if (roi is RoiModel mObj)
                {
                    string resolved = null;
                    try { resolved = ResolveRoiLabelText(mObj); } catch { /* ignore */ }

                    _lbl = !string.IsNullOrWhiteSpace(resolved) ? resolved
                         : (!string.IsNullOrWhiteSpace(mObj.Label) ? mObj.Label : "ROI");

                    // Persist resolved label so future redraws reuse same text/key
                    if (string.IsNullOrWhiteSpace(mObj.Label) && !string.IsNullOrWhiteSpace(resolved))
                        mObj.Label = resolved;
                }
                else
                {
                    _lbl = "ROI";
                }

                if (canvasRoi is RoiModel)
                {
                    canvasRoi.Label = _lbl;
                }

                // Use unique TextBlock name derived from label text
                string _labelName = "roiLabel_" + (_lbl ?? string.Empty).Replace(" ", "_");
                var _existing = CanvasROI.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Name == _labelName);
                var _label = _existing ?? new TextBlock { Name = _labelName };
                _label.Text = string.IsNullOrWhiteSpace(_lbl) ? "ROI" : _lbl;
                _label.FontFamily = new FontFamily("Segoe UI");
                _label.FontSize = 12;
                _label.FontWeight = FontWeights.SemiBold;
                _label.Foreground = Brushes.White;
                _label.IsHitTestVisible = false;

                if (_existing == null)
                {
                    CanvasROI.Children.Add(_label);
                    Panel.SetZIndex(_label, int.MaxValue);
                }

                // Place label next to the ROI shape (may defer via Dispatcher if geometry not ready)
                UpdateRoiLabelPosition(shape);

                if (shape != null)
                {
                    double l = Canvas.GetLeft(shape), t = Canvas.GetTop(shape);
                    AppendResizeLog($"[roi] {roi.Label ?? "ROI"}: L={l:0} T={t:0} W={shape.Width:0} H={shape.Height:0}");
                }

                ApplyRoiRotationToShape(shape, roi.AngleDeg);
            }

            if (_layout.Inspection != null)
            {
                SyncCurrentRoiFromInspection(_layout.Inspection);
            }
        }

        private Shape? CreateLayoutShape(RoiModel roi)
        {
            var canvasRoi = roi.Clone();

            Shape shape = roi.Shape switch
            {
                RoiShape.Rectangle => new WRectShape(),
                RoiShape.Annulus => new AnnulusShape(),
                _ => new WEllipse()
            };

            var style = GetRoiStyle(roi.Role);

            shape.Stroke = style.stroke;
            shape.Fill = style.fill;
            shape.StrokeThickness = style.thickness;
            if (style.dash != null)
                shape.StrokeDashArray = style.dash;
            shape.SnapsToDevicePixels = true;
            shape.IsHitTestVisible = ShouldEnableRoiEditing(roi.Role);
            Panel.SetZIndex(shape, style.zIndex);

            // Persist canvas ROI info on Tag; geometry will be updated during RedrawOverlay().
            shape.Tag = canvasRoi;

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

        private (WBrush stroke, WBrush fill, double thickness, DoubleCollection? dash, int zIndex) GetRoiStyle(ROI roi)
        {
            return GetRoiStyle(RoiRole.Inspection);
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
                // Evitamos redibujar en DragStarted (click en adorner sin mover)
                HandleAdornerChange(changeKind, updatedModel, pixelModel, "[adorner]");
                UpdateRoiLabelPosition(shape);
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


        private void UpdateHeatmapOverlayLayoutAndClip()
        {
            LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: BEGIN ----");

            if (HeatmapOverlay == null)
            {
                LogHeatmap("HeatmapOverlay control is null.");
                LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: END ----");
                return;
            }

            if (_lastHeatmapBmp == null || _lastHeatmapRoi == null)
            {
                LogHeatmap("No heatmap or ROI to overlay.");
                HeatmapOverlay.Source = null;
                HeatmapOverlay.Visibility = Visibility.Collapsed;
                HeatmapOverlay.Clip = null;
                LogHeatmap("Clip = null");
                LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: END ----");
                return;
            }

            // 1) Rectángulo de la imagen en pantalla (letterboxing)
            var disp = GetImageDisplayRect();
            LogHeatmap($"DisplayRect = (X={disp.X:F2},Y={disp.Y:F2},W={disp.Width:F2},H={disp.Height:F2})");

            // 2) Transformación imagen→canvas actualmente en uso
            var (sx, sy, offX, offY) = GetImageToCanvasTransform();
            LogHeatmap($"Transform Img→Canvas: sx={sx:F6}, sy={sy:F6}, offX={offX:F4}, offY={offY:F4}]");

            // 3) ROI en espacio de imagen (si tienes RoiDebug)
            try { LogHeatmap("ROI (image space): " + RoiDebug(_lastHeatmapRoi)); } catch {}

            // 4) ROI en espacio de CANVAS
            var rc = ImageToCanvas(_lastHeatmapRoi);
            LogHeatmap($"ROI canvas rect rc = (L={rc.Left:F2},T={rc.Top:F2},W={rc.Width:F2},H={rc.Height:F2})");

            // 5) Margen del Canvas de las ROI (CanvasROI) y su offset visual real
            if (CanvasROI != null)
            {
                var cm = CanvasROI.Margin;
                LogHeatmap($"CanvasROI.Margin = (L={cm.Left:F0},T={cm.Top:F0})");
                var cofs = System.Windows.Media.VisualTreeHelper.GetOffset(CanvasROI);
                LogHeatmap($"CanvasROI.VisualOffset = (X={cofs.X:F4},Y={cofs.Y:F4})");
            }

            // 6) Tipo de padre del heatmap y ruta de posicionamiento
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(HeatmapOverlay);
            bool parentIsCanvas = parent is System.Windows.Controls.Canvas;
            LogHeatmap($"HeatmapOverlay.Parent = {parent?.GetType().Name ?? "<null>"} ; ParentIsCanvas={parentIsCanvas}");

            // 2) Anchor overlay to ROI rect (canvas absolute via Canvas coords when available)
            // Size to ROI rect
            HeatmapOverlay.Source = _lastHeatmapBmp;
            HeatmapOverlay.Width  = rc.Width;
            HeatmapOverlay.Height = rc.Height;

            // Prefer Canvas positioning to avoid Margin rounding drift. Fallback to Margin
            // only if parent is not a Canvas.
            if (parentIsCanvas)
            {
                // Clear Margin so Canvas.Left/Top are not compounded
                HeatmapOverlay.Margin = new System.Windows.Thickness(0);
                System.Windows.Controls.Canvas.SetLeft(HeatmapOverlay, rc.Left);
                System.Windows.Controls.Canvas.SetTop(HeatmapOverlay,  rc.Top);
            }
            else
            {
                // SUMAR el margen del Canvas de las ROI para alinear origen,
                // y redondear a enteros para evitar subpíxeles (misma política que CanvasROI).
                double leftRounded = System.Math.Round((CanvasROI?.Margin.Left ?? 0) + rc.Left);
                double topRounded  = System.Math.Round((CanvasROI?.Margin.Top  ?? 0) + rc.Top);
                HeatmapOverlay.Margin = new System.Windows.Thickness(leftRounded, topRounded, 0, 0);
            }

            LogHeatmap($"Heatmap by Margin with CanvasROI.Margin sum: finalMargin=({HeatmapOverlay.Margin.Left},{HeatmapOverlay.Margin.Top})");

            HeatmapOverlay.Visibility = System.Windows.Visibility.Visible;

            // 3) Build Clip in OVERLAY-LOCAL coordinates (0..Width, 0..Height)
            //    Determine shape ratios from ROI model
            System.Windows.Media.Geometry clipGeo = null;

            // Optional: role-based mismatch detection (keep existing skipClip logic if you already added it)
            bool skipClip = false; // keep your previous mismatch logic if present to possibly set this true
            string heatmapShape = InferShapeName(_lastHeatmapRoi);
            string modelShape = null;
            RoiModel modelRoi = null;

            // Choose the expected model ROI by role
            try
            {
                // If the heatmap is for Master 1 inspection, compare against Master1Pattern
                if (_lastHeatmapRoi.Role == RoiRole.Master1Search)
                    modelRoi = _layout?.Master1Pattern;
                // If for Master 2 inspection, compare against Master2Pattern
                else if (_lastHeatmapRoi.Role == RoiRole.Master2Search)
                    modelRoi = _layout?.Master2Pattern;
                // For final Inspection, we may not have a single model to compare; leave null
            }
            catch { /* ignore */ }

            if (modelRoi != null)
            {
                modelShape = InferShapeName(modelRoi);
                if (!string.Equals(modelShape, heatmapShape, StringComparison.OrdinalIgnoreCase))
                {
                    skipClip = true;
                    LogHeatmap($"[WARN] ROI shape mismatch — model={modelShape}, heatmap={heatmapShape}. " +
                               "Skipping clip to show full heatmap.");
                }
                else
                {
                    LogHeatmap($"ROI shape match — {heatmapShape}.");
                }
            }
            else
            {
                LogHeatmap($"No model ROI available for mismatch check (heatmap shape={heatmapShape}).");
            }
            // --- end mismatch detection block ---

            // Compute outer ellipse based on the overlay bounds:
            // Note: overlay is exactly the ROI bounding box. "Outer radius" is half of the max side.
            double ow = HeatmapOverlay.Width;
            double oh = HeatmapOverlay.Height;
            double outerR = System.Math.Max(ow, oh) * 0.5;
            var center = new System.Windows.Point(ow * 0.5, oh * 0.5);

            // If ROI has both R and RInner > 0 => Annulus
            if (!skipClip && _lastHeatmapRoi.R > 0 && _lastHeatmapRoi.RInner > 0)
            {
                // Inner radius is proportional to outer radius by model ratio
                double innerR = outerR * (_lastHeatmapRoi.RInner / _lastHeatmapRoi.R);

                var outer = new System.Windows.Media.EllipseGeometry(center, outerR, outerR);
                var inner = new System.Windows.Media.EllipseGeometry(center, innerR, innerR);
                clipGeo = new System.Windows.Media.CombinedGeometry(System.Windows.Media.GeometryCombineMode.Exclude, outer, inner);
            }
            // If ROI has only R > 0 => Circle
            else if (!skipClip && _lastHeatmapRoi.R > 0)
            {
                clipGeo = new System.Windows.Media.EllipseGeometry(center, outerR, outerR);
            }
            // Otherwise treat as rectangle ROI (no need to clip; the overlay bounds already match)
            else
            {
                // leave clipGeo = null
                // clipGeo = new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, ow, oh));
            }

            // 4) Apply Clip (or disable if mismatch logic set skipClip)
            HeatmapOverlay.Clip = skipClip ? null : clipGeo;

            var hOfs = System.Windows.Media.VisualTreeHelper.GetOffset(HeatmapOverlay);
            LogHeatmap($"HeatmapOverlay.VisualOffset = (X={hOfs.X:F4},Y={hOfs.Y:F4})");

            if (HeatmapOverlay?.Clip != null)
            {
                var b = HeatmapOverlay.Clip.Bounds;
                LogHeatmap($"Clip.Bounds = (X={b.X:F2},Y={b.Y:F2},W={b.Width:F2},H={b.Height:F2})");
            }
            else
            {
                LogHeatmap("Clip = null");
            }

            // 5) (Optional) Log overlay rect in canvas space & local clip bounds
            LogHeatmap($"Overlay anchored to ROI: Left={rc.Left:F2}, Top={rc.Top:F2}, W={rc.Width:F2}, H={rc.Height:F2}");
            if (HeatmapOverlay?.Clip != null)
            {
                var b = HeatmapOverlay.Clip.Bounds;
                LogHeatmap($"Clip.Bounds = (X={b.X:F2},Y={b.Y:F2},W={b.Width:F2},H={b.Height:F2})");
            }
            else
            {
                LogHeatmap("Clip = null");
            }

            LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: END ----");
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
                    _lastHeatmapRoi = HeatmapRoiModel.From(export.RoiImage.Clone());
                    _heatmapOverlayOpacity = Math.Clamp(opacity, 0.0, 1.0);
                    EnterAnalysisView();
                    EnsureHeatmapScaleSlider();

                    CacheHeatmapGrayFromBitmapSource(heatmapSource);
                    RebuildHeatmapOverlayFromCache();
                    SyncHeatmapBitmapFromOverlay();

                    if (_lastHeatmapBmp != null)
                    {
                        LogHeatmap($"Heatmap Source: {_lastHeatmapBmp.PixelWidth}x{_lastHeatmapBmp.PixelHeight}, Fmt={_lastHeatmapBmp.Format}");
                    }
                    else
                    {
                        LogHeatmap("Heatmap Source: <null>");
                    }

                    // OPTIONAL: bump overlay opacity a bit (visual only)
                    if (HeatmapOverlay != null) HeatmapOverlay.Opacity = 0.90;

                    UpdateHeatmapOverlayLayoutAndClip();

                    _heatmapOverlayOpacity = HeatmapOverlay?.Opacity ?? _heatmapOverlayOpacity;
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

            _lastHeatmapBmp = null;
            _lastHeatmapRoi = null;
            _lastHeatmapGray = null;
            _lastHeatmapW = 0;
            _lastHeatmapH = 0;
            LogHeatmap("Heatmap Source: <null>");
            UpdateHeatmapOverlayLayoutAndClip();
        }

        private void RefreshHeatmapOverlay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshHeatmapOverlay);
                return;
            }
            UpdateHeatmapOverlayLayoutAndClip();
        }

        private void RebuildHeatmapOverlayFromCache()
        {
            if (HeatmapOverlay == null) return;
            if (_lastHeatmapGray == null || _lastHeatmapW <= 0 || _lastHeatmapH <= 0) return;

            // Build Turbo colormap LUT (once per rebuild; fast enough at this size)
            byte[] turboR = new byte[256], turboG = new byte[256], turboB = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                double rr = 0.13572138 + 4.61539260*t - 42.66032258*t*t + 132.13108234*t*t*t - 152.94239396*t*t*t*t + 59.28637943*t*t*t*t*t;
                double gg = 0.09140261 + 2.19418839*t + 4.84296658*t*t - 14.18503333*t*t*t + 14.13815831*t*t*t*t - 4.21519726*t*t*t*t*t;
                double bb = 0.10667330 + 12.64194608*t - 60.58204836*t*t + 139.27510080*t*t*t - 150.21747690*t*t*t*t + 59.17006120*t*t*t*t*t;
                turboR[i] = (byte)Math.Round(255.0 * Math.Clamp(rr, 0.0, 1.0));
                turboG[i] = (byte)Math.Round(255.0 * Math.Clamp(gg, 0.0, 1.0));
                turboB[i] = (byte)Math.Round(255.0 * Math.Clamp(bb, 0.0, 1.0));
            }

            // Normalize with global _heatmapNormMax (1.0=identity). If <1 -> brighter; if >1 -> darker.
            double denom = Math.Max(0.0001, _heatmapNormMax) * 255.0;

            byte[] bgra = new byte[_lastHeatmapW * _lastHeatmapH * 4];
            int idx = 0;
            for (int i = 0; i < _lastHeatmapGray.Length; i++)
            {
                // Map gray -> 0..255 index using the global normalization
                double v = _lastHeatmapGray[i];                 // 0..255
                int lut = (int)Math.Round(255.0 * Math.Clamp(v / denom, 0.0, 1.0));
                bgra[idx++] = turboB[lut];
                bgra[idx++] = turboG[lut];
                bgra[idx++] = turboR[lut];
                bgra[idx++] = 255; // opaque
            }

            // Create bitmap and assign
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(
                _lastHeatmapW, _lastHeatmapH, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, _lastHeatmapW, _lastHeatmapH), bgra, _lastHeatmapW * 4, 0);
            HeatmapOverlay.Source = wb;
        }

        private void CacheHeatmapGrayFromBitmapSource(BitmapSource src)
        {
            if (src == null)
            {
                _lastHeatmapGray = null;
                _lastHeatmapW = 0;
                _lastHeatmapH = 0;
                return;
            }

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            byte[] gray;

            if (src.Format == PixelFormats.Gray8 || src.Format == PixelFormats.Indexed8)
            {
                gray = CopyGrayBytes(src);
            }
            else if (src.Format == PixelFormats.Gray16)
            {
                int stride = ((src.Format.BitsPerPixel * w) + 7) / 8;
                byte[] raw = new byte[h * stride];
                src.CopyPixels(raw, stride, 0);
                gray = new byte[w * h];
                for (int i = 0, j = 0; i < gray.Length; i++, j += 2)
                {
                    ushort val = (ushort)(raw[j] | (raw[j + 1] << 8));
                    gray[i] = (byte)Math.Round((val / 65535.0) * 255.0);
                }
            }
            else
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                conv.Freeze();
                gray = CopyGrayBytes(conv);
            }

            _lastHeatmapGray = gray;
            _lastHeatmapW = w;
            _lastHeatmapH = h;
        }

        private static byte[] CopyGrayBytes(BitmapSource src)
        {
            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = ((src.Format.BitsPerPixel * w) + 7) / 8;
            byte[] raw = new byte[h * stride];
            src.CopyPixels(raw, stride, 0);
            if (stride == w)
                return raw;

            byte[] trimmed = new byte[w * h];
            for (int row = 0; row < h; row++)
            {
                Buffer.BlockCopy(raw, row * stride, trimmed, row * w, w);
            }
            return trimmed;
        }

        private void SyncHeatmapBitmapFromOverlay()
        {
            if (HeatmapOverlay?.Source is BitmapSource bmp)
            {
                if (bmp.CanFreeze && !bmp.IsFrozen)
                {
                    try { bmp.Freeze(); } catch { }
                }
                _lastHeatmapBmp = bmp;
            }
        }

        private void HeatmapScaleSlider_ValueChangedSync(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SyncHeatmapBitmapFromOverlay();
        }

        // Map a [0,1] value to Turbo colormap (approx), returning (B,G,R) tuple
        private static (byte B, byte G, byte R) TurboLUT(double t)
        {
            if (double.IsNaN(t)) t = 0;
            if (t < 0) t = 0; if (t > 1) t = 1;
            // Polynomial approximation of Turbo (McIlroy 2019), clamped
            double r = 0.13572138 + 4.61539260*t - 42.66032258*t*t + 132.13108234*t*t*t - 152.94239396*t*t*t*t + 59.28637943*t*t*t*t*t;
            double g = 0.09140261 + 2.19418839*t + 4.84296658*t*t - 14.18503333*t*t*t + 14.13815831*t*t*t*t - 4.21519726*t*t*t*t*t;
            double b = 0.10667330 + 12.64194608*t - 60.58204836*t*t + 139.27510080*t*t*t - 150.21747690*t*t*t*t + 59.17006120*t*t*t*t*t;
            r = System.Math.Clamp(r, 0.0, 1.0);
            g = System.Math.Clamp(g, 0.0, 1.0);
            b = System.Math.Clamp(b, 0.0, 1.0);
            return ((byte)System.Math.Round(255*b), (byte)System.Math.Round(255*g), (byte)System.Math.Round(255*r));
        }

        // Build a visible BGRA32 heatmap with robust min/max normalization and optional colorization
        private static System.Windows.Media.Imaging.WriteableBitmap BuildVisibleHeatmap(
            System.Windows.Media.Imaging.BitmapSource src,
            bool useTurbo = true,   // set true for vivid colors
            double gamma = 0.9      // slight gamma to lift mid-tones
        )
        {
            if (src == null) return null;

            var fmt = src.Format;
            int w = src.PixelWidth, h = src.PixelHeight;

            // Extract raw buffer according to source format
            if (fmt == System.Windows.Media.PixelFormats.Gray8)
            {
                int stride = w;
                byte[] g8 = new byte[h * stride];
                src.CopyPixels(g8, stride, 0);

                // Compute min/max ignoring zeros
                int minv = 255, maxv = 0, countNZ = 0;
                for (int i = 0; i < g8.Length; i++)
                {
                    int v = g8[i];
                    if (v <= 0) continue;
                    if (v < minv) minv = v;
                    if (v > maxv) maxv = v;
                    countNZ++;
                }
                if (countNZ == 0) { minv = 0; maxv = 0; }

                double inv = (maxv > minv) ? 1.0 / (maxv - minv) : 0.0;

                byte[] bgra = new byte[w*h*4];
                for (int p = 0, q = 0; p < g8.Length; p++, q += 4)
                {
                    double t = (inv == 0.0) ? 0.0 : (g8[p] - minv) * inv;
                    if (gamma != 1.0) t = System.Math.Pow(t, 1.0 / System.Math.Max(1e-6, gamma));
                    if (useTurbo)
                    {
                        var (B,G,R) = TurboLUT(t);
                        bgra[q+0] = B; bgra[q+1] = G; bgra[q+2] = R; bgra[q+3] = 255;
                    }
                    else
                    {
                        byte u = (byte)System.Math.Round(255.0 * t);
                        bgra[q+0] = u; bgra[q+1] = u; bgra[q+2] = u; bgra[q+3] = 255;
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h, src.DpiX, src.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w*4, 0);
                wb.Freeze();
                return wb;
            }
            else if (fmt == System.Windows.Media.PixelFormats.Gray16)
            {
                int stride = w * 2;
                byte[] raw = new byte[h * stride];
                src.CopyPixels(raw, stride, 0);

                // Convert bytes→ushort (Little Endian)
                int N = w*h;
                ushort[] g16 = new ushort[N];
                for (int i = 0, j = 0; i < N; i++, j += 2)
                    g16[i] = (ushort)(raw[j] | (raw[j+1] << 8));

                int minv = ushort.MaxValue, maxv = 0, countNZ = 0;
                for (int i = 0; i < N; i++)
                {
                    int v = g16[i];
                    if (v <= 0) continue;
                    if (v < minv) minv = v;
                    if (v > maxv) maxv = v;
                    countNZ++;
                }
                if (countNZ == 0) { minv = 0; maxv = 0; }

                double inv = (maxv > minv) ? 1.0 / (maxv - minv) : 0.0;

                byte[] bgra = new byte[N*4];
                for (int i = 0, q = 0; i < N; i++, q += 4)
                {
                    double t = (inv == 0.0) ? 0.0 : (g16[i] - minv) * inv;
                    if (gamma != 1.0) t = System.Math.Pow(t, 1.0 / System.Math.Max(1e-6, gamma));
                    if (useTurbo)
                    {
                        var (B,G,R) = TurboLUT(t);
                        bgra[q+0] = B; bgra[q+1] = G; bgra[q+2] = R; bgra[q+3] = 255;
                    }
                    else
                    {
                        byte u = (byte)System.Math.Round(255.0 * t);
                        bgra[q+0] = u; bgra[q+1] = u; bgra[q+2] = u; bgra[q+3] = 255;
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h, src.DpiX, src.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w*4, 0);
                wb.Freeze();
                return wb;
            }
            else
            {
                // Convert to BGRA32 and compute luminance min/max ignoring zeros
                var conv = (fmt != System.Windows.Media.PixelFormats.Bgra32)
                    ? new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                    : src;

                int stride = w * 4;
                byte[] buf = new byte[h * stride];
                conv.CopyPixels(buf, stride, 0);

                int minv = 255, maxv = 0, countNZ = 0;
                for (int q = 0; q < buf.Length; q += 4)
                {
                    // premultiplied alpha is fine: we treat zeros as background
                    byte b = buf[q+0], g = buf[q+1], r = buf[q+2];
                    int lum = (int)System.Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b);
                    if (lum <= 0) continue;
                    if (lum < minv) minv = lum;
                    if (lum > maxv) maxv = lum;
                    countNZ++;
                }
                if (countNZ == 0) { minv = 0; maxv = 0; }

                double inv = (maxv > minv) ? 1.0 / (maxv - minv) : 0.0;

                byte[] bgra = new byte[h * stride];
                for (int q = 0; q < buf.Length; q += 4)
                {
                    byte b = buf[q+0], g = buf[q+1], r = buf[q+2];
                    int lum = (int)System.Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b);
                    double t = (inv == 0.0) ? 0.0 : (lum - minv) * inv;
                    if (t < 0) t = 0; if (t > 1) t = 1;
                    if (gamma != 1.0) t = System.Math.Pow(t, 1.0 / System.Math.Max(1e-6, gamma));
                    if (useTurbo)
                    {
                        var (B,G,R) = TurboLUT(t);
                        bgra[q+0] = B; bgra[q+1] = G; bgra[q+2] = R; bgra[q+3] = 255;
                    }
                    else
                    {
                        byte u = (byte)System.Math.Round(255.0 * t);
                        bgra[q+0] = u; bgra[q+1] = u; bgra[q+2] = u; bgra[q+3] = 255;
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h, conv.DpiX, conv.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w*4, 0);
                wb.Freeze();
                return wb;
            }
        }


        private void ResetAnalysisMarks()
        {
            RemoveAnalysisMarks();
            _lastM1CenterPx = null;
            _lastM2CenterPx = null;
            RedrawAnalysisCrosses();
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

        private void RedrawAnalysisCrosses()
        {
            if (CanvasROI == null)
                return;

            // 1) Remove previous crosses
            var old = CanvasROI.Children.OfType<System.Windows.Shapes.Shape>()
                      .Where(s => (s.Tag as string) == "AnalysisCross")
                      .ToList();
            foreach (var s in old) CanvasROI.Children.Remove(s);

            // 2) Helper to draw a cross at a canvas point
            void DrawCrossAt(WpfPoint p, double size = 12.0, double th = 2.0)
            {
                var h = new System.Windows.Shapes.Line
                {
                    X1 = p.X - size, Y1 = p.Y,
                    X2 = p.X + size, Y2 = p.Y,
                    Stroke = System.Windows.Media.Brushes.Lime,
                    StrokeThickness = th,
                    IsHitTestVisible = false,
                    Tag = "AnalysisCross"
                };
                var v = new System.Windows.Shapes.Line
                {
                    X1 = p.X, Y1 = p.Y - size,
                    X2 = p.X, Y2 = p.Y + size,
                    Stroke = System.Windows.Media.Brushes.Lime,
                    StrokeThickness = th,
                    IsHitTestVisible = false,
                    Tag = "AnalysisCross"
                };
                CanvasROI.Children.Add(h);
                CanvasROI.Children.Add(v);
                System.Windows.Controls.Panel.SetZIndex(h, int.MaxValue - 1);
                System.Windows.Controls.Panel.SetZIndex(v, int.MaxValue - 1);
            }

            // 3) Convert IMAGE-space → CANVAS and draw
            if (_lastM1CenterPx.HasValue)
            {
                var p = ImagePxToCanvasPt(_lastM1CenterPx.Value);
                DrawCrossAt(p);
            }
            if (_lastM2CenterPx.HasValue)
            {
                var p = ImagePxToCanvasPt(_lastM2CenterPx.Value);
                DrawCrossAt(p);
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
            if (Tabs != null && TabPresets != null)
            {
                Tabs.SelectedItem = TabPresets;
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
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AppendResizeLog($"[window] SizeChanged: window={ActualWidth:0}x{ActualHeight:0} ImgMain={ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0}");
            ScheduleSyncOverlay(force: true);
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();
        }

        private void ImgMain_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AppendResizeLog($"[image] SizeChanged: ImgMain={ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0}");
            ScheduleSyncOverlay(force: true);
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();
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

                string lbl;
                RoiModel previewModel;
                try
                {
                    var role = GetCurrentStateRole() ?? RoiRole.Inspection;
                    previewModel = new RoiModel { Shape = shape, Role = role };
                    lbl = ResolveRoiLabelText(previewModel) ?? "ROI";
                    previewModel.Label = lbl;
                }
                catch
                {
                    lbl = "ROI";
                    previewModel = new RoiModel { Shape = shape, Role = GetCurrentStateRole() ?? RoiRole.Inspection, Label = lbl };
                }

                string labelName = "roiLabel_" + lbl.Replace(" ", "_");
                var tb = CanvasROI.Children.OfType<TextBlock>()
                    .FirstOrDefault(t => t.Name == labelName);
                if (tb == null)
                {
                    tb = new TextBlock
                    {
                        Name = labelName,
                        Text = lbl,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                        IsHitTestVisible = false
                    };
                    CanvasROI.Children.Add(tb);
                    Panel.SetZIndex(tb, int.MaxValue);
                }
                else
                {
                    tb.Text = lbl;
                }

                _previewShape.Tag = previewModel;
                UpdateRoiLabelPosition(_previewShape);
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
            }
            else
            {
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
                    if (ShouldLogAnnulusValue(ref _lastLoggedAnnulusOuterRadius, outer))
                        AppendLog($"[annulus] outer radius preview={outer:0.##} px");

                    // Conserva proporción si el usuario ya la ha cambiado; si no, usa el default & clamp.
                    double proposedInner = annulus.InnerRadius;
                    double resolvedInner = AnnulusDefaults.ResolveInnerRadius(proposedInner, outer);
                    double finalInner = AnnulusDefaults.ClampInnerRadius(resolvedInner, outer);

                    if (ShouldLogAnnulusInner(proposedInner, finalInner))
                        AppendLog($"[annulus] outer={outer:0.##} px, proposed inner={proposedInner:0.##} px -> final inner={finalInner:0.##} px");

                    annulus.InnerRadius = finalInner;
                }
            }

            UpdateRoiLabelPosition(_previewShape);
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
            if (e.OriginalSource is Shape sShape && sShape.Tag is RoiModel roiHit && ShouldEnableRoiEditing(roiHit.Role))
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
                UpdateRoiLabelPosition(_dragShape);

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

            string? previewLabel = null;
            if (_previewShape.Tag is RoiModel existingTag && !string.IsNullOrWhiteSpace(existingTag.Label))
            {
                previewLabel = existingTag.Label;
            }

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

            if (!string.IsNullOrWhiteSpace(previewLabel))
            {
                canvasDraft.Label = previewLabel;
            }

            var pixelDraft = CanvasToImage(canvasDraft);
            if (!string.IsNullOrWhiteSpace(previewLabel) && pixelDraft != null)
            {
                pixelDraft.Label = previewLabel;
            }
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
            UpdateRoiLabelPosition(_previewShape);
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
                {
                    AppendLog("[adorner] overlay no disponible para preview");
                    return;
                }

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
                        UpdateRoiLabelPosition(_previewShape);
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
                    _adornerHadDelta = false;
                    HandleDragStarted(canvasModel, pixelModel, contextLabel);
                    return;

                case RoiAdornerChangeKind.Delta:
                    _adornerHadDelta = true;
                    HandleDragDelta(canvasModel, pixelModel, contextLabel);
                    // Redibujamos solo cuando hay delta real
                    UpdateOverlayFromPixelModel(pixelModel);
                    return;

                case RoiAdornerChangeKind.DragCompleted:
                    HandleDragCompleted(canvasModel, pixelModel, contextLabel);
                    // Si no hubo delta (click sin mover), NO redibujamos → evita “salto”
                    if (_adornerHadDelta)
                        UpdateOverlayFromPixelModel(pixelModel);
                    _adornerHadDelta = false;
                    return;
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

                    KeepOnlyMaster2InCanvas();
                    LogHeatmap("KeepOnlyMaster2InCanvas called after saving Master2Pattern.");

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

                    KeepOnlyMaster2InCanvas();

                    // Ensure overlay fully refreshed so Master2 doesn't appear missing
                    try { ScheduleSyncOverlay(true); }
                    catch
                    {
                        SyncOverlayToImage();
                        try { RedrawOverlaySafe(); } catch { RedrawOverlay(); }
                        UpdateHeatmapOverlayLayoutAndClip();
                        try { RedrawAnalysisCrosses(); } catch {}
                    }
                    AppendLog("[UI] Redraw forced after saving Master2-Search.");

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

            var savedRoiModel = savedRoi;

            bool skipRedrawForMasterInspection = savedRoiModel != null &&
                (savedRoiModel.Role == RoiRole.Master1Search);

            if (skipRedrawForMasterInspection)
            {
                ClearCanvasShapesAndLabels();
                ClearCanvasInternalMaps();
                DetachPreviewAndAdorner();

                // IMPORTANT: Do NOT call RedrawOverlay / RedrawOverlaySafe here.
                // The model/layout remains intact, but UI stays blank as requested.
            }

            // Ensure saved ROI has a stable Label for unique TextBlock names
            try
            {
                if (savedRoiModel != null)
                {
                    string resolved = ResolveRoiLabelText(savedRoiModel);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        savedRoiModel.Label = resolved;
                }
            }
            catch { /* ignore */ }

            // Clear preview if present (so it doesn’t overlay)
            try
            {
                if (_previewShape != null)
                {
                    CanvasROI.Children.Remove(_previewShape);
                    _previewShape = null;
                }
            }
            catch { /* ignore */ }

            // Redraw saved ROIs (now with visible stroke/fill and unique labels)
            if (!skipRedrawForMasterInspection)
            {
                RedrawOverlay();
            }

            // If we can find the shape for this saved ROI, position its label explicitly
            try
            {
                if (savedRoiModel != null)
                {
                    var shape = CanvasROI.Children.OfType<Shape>()
                        .FirstOrDefault(s => s.Tag is RoiModel rm && rm.Id == savedRoiModel.Id);
                    if (shape != null) UpdateRoiLabelPosition(shape);
                }
            }
            catch { /* ignore */ }

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

            if (!skipRedrawForMasterInspection)
            {
                RedrawOverlaySafe();
            }
            RedrawAnalysisCrosses();

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

            _lastM1CenterPx = new CvPoint((int)System.Math.Round(c1.Value.X), (int)System.Math.Round(c1.Value.Y));
            _lastM2CenterPx = new CvPoint((int)System.Math.Round(c2.Value.X), (int)System.Math.Round(c2.Value.Y));
            RedrawAnalysisCrosses();

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

            try
            {
                // Si el flujo de inferencia ha dejado _lastHeatmapRoi, persiste en el layout según su rol.
                if (_lastHeatmapRoi != null)
                {
                    switch (_lastHeatmapRoi.Role)
                    {
                        case RoiRole.Inspection:
                            _layout.Inspection = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master1Pattern:
                            _layout.Master1Pattern = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master1Search:
                            _layout.Master1Search = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master2Pattern:
                            _layout.Master2Pattern = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master2Search:
                            _layout.Master2Search = _lastHeatmapRoi.Clone();
                            break;
                        default:
                            // Si no encaja en roles conocidos, no sobrescribir nada
                            break;
                    }
                    AppendLog("[UI] Persisted detected ROI into layout: " + _lastHeatmapRoi.Role.ToString());
                    UpdateRoiVisibilityControls();
                }
            }
            catch (Exception ex)
            {
                AppendLog("[UI] Persist layout with detected ROI failed: " + ex.Message);
            }

            MasterLayoutManager.Save(_preset, _layout);
            AppendLog("[FLOW] Layout guardado");

            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Lanza el pipeline: SyncOverlayToImage → RedrawOverlay → UpdateHeatmapOverlayLayoutAndClip → RedrawAnalysisCrosses
                    ScheduleSyncOverlay(true);
                    AppendLog("[UI] Post-Analyze refresh scheduled (ScheduleSyncOverlay(true)).");
                }
                catch (Exception ex)
                {
                    AppendLog("[UI] ScheduleSyncOverlay failed: " + ex.Message);
                }
            });

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

            TrainLogText.AppendText(line + Environment.NewLine);
            TrainLogText.ScrollToEnd();
        }

        // --------- AppendLog (para evitar CS0119 en invocaciones) ---------

        // Return a robust center for any RoiModel (prefer CX/CY; fallback to Left/Top/Width/Height)
        private static (double cx, double cy) CenterOf(RoiModel r)
        {
            if (r == null) return (0, 0);
            // Prefer explicit center if available
            if (!double.IsNaN(r.CX) && !double.IsNaN(r.CY)) return (r.CX, r.CY);
            return (r.Left + r.Width * 0.5, r.Top + r.Height * 0.5);
        }

        // Apply translation/rotation/scale to a target ROI using a baseline ROI and an old->new pivot
        // angleDelta in RADIANS; pivotOld/new in IMAGE coordinates
        private static void ApplyRoiTransform(RoiModel target, RoiModel baseline,
                                              double pivotOldX, double pivotOldY,
                                              double pivotNewX, double pivotNewY,
                                              double scale, double angleDeltaRad)
        {
            if (target == null || baseline == null) return;

            // Baseline center (image space)
            var cBase = CenterOf(baseline);
            double relX = cBase.cx - pivotOldX;
            double relY = cBase.cy - pivotOldY;

            double cos = Math.Cos(angleDeltaRad), sin = Math.Sin(angleDeltaRad);
            double relXr = scale * (cos * relX - sin * relY);
            double relYr = scale * (sin * relX + cos * relY);

            // New center
            double newCX = pivotNewX + relXr;
            double newCY = pivotNewY + relYr;

            // Scale size (generic: Width/Height; for circles/annulus R, RInner)
            double newW = baseline.Width  * scale;
            double newH = baseline.Height * scale;

            target.Width  = newW;
            target.Height = newH;

            // Update center & box
            target.CX  = newCX;
            target.CY  = newCY;
            target.Left = newCX - (newW * 0.5);
            target.Top  = newCY - (newH * 0.5);

            // If circular radii exist, scale them (no-ops if zero)
            target.R      = baseline.R      * scale;
            target.RInner = baseline.RInner * scale;

            // NOTE: we do NOT modify any angle property on the ROI shape,
            // because circles/annulus are rotation-invariant and rectangles in this app are axis-aligned.
        }

        private void MoveInspectionTo(RoiModel insp, WPoint master1, WPoint master2)
        {
            // -- lock-scale: capture pre-move size of Inspection ROI --
            double __inspW0  = insp?.Width  ?? 0;
            double __inspH0  = insp?.Height ?? 0;
            double __inspR0  = insp?.R      ?? 0;
            double __inspRin0= insp?.RInner ?? 0;

            if (insp == null)
                return;

            // === BEGIN: capture baselines for unified transform ===
            var __baseM1P = _layout?.Master1Pattern?.Clone();
            var __baseM1S = _layout?.Master1Search ?.Clone();
            var __baseM2P = _layout?.Master2Pattern?.Clone();
            var __baseM2S = _layout?.Master2Search ?.Clone();
            var __baseHeat = _lastHeatmapRoi          ?.Clone();
            bool __haveM1 = (__baseM1P != null);
            bool __haveM2 = (__baseM2P != null);
            // ===  END: capture baselines for unified transform  ===

            var baseline = GetInspectionBaselineClone() ?? insp.Clone();

            InspectionAlignmentHelper.MoveInspectionTo(
                insp,
                baseline,
                _layout?.Master1Pattern,
                _layout?.Master2Pattern,
                master1,
                master2);

            // -- lock-scale: restore inspection size, keep its new center --
            if (_lockAnalyzeScale && insp != null)
            {
                double cx = insp.CX;
                double cy = insp.CY;
                // restore size
                insp.Width  = __inspW0;
                insp.Height = __inspH0;
                insp.R      = __inspR0;
                insp.RInner = __inspRin0;
                // re-center bounding box to preserved size
                insp.Left = cx - (__inspW0 * 0.5);
                insp.Top  = cy - (__inspH0 * 0.5);
            }

            try
            {
                // Refresh baseline to avoid cumulative drift in subsequent runs
                SetInspectionBaseline(insp.Clone());
                AppendLog("[UI] Inspection baseline refreshed after relocation.");
            }
            catch (Exception ex)
            {
                AppendLog("[UI] Failed to refresh inspection baseline: " + ex.Message);
            }

            // === BEGIN: apply the SAME transform to Masters + Heatmap (no inaccessible calls) ===
            try
            {
                if (__haveM1 && __haveM2 && _layout != null)
                {
                    // Old centers from baselines (tuples: use deconstruction, NOT .X/.Y)
                    var (m1OldX, m1OldY) = CenterOf(__baseM1P);
                    var (m2OldX, m2OldY) = CenterOf(__baseM2P);
                    double dxOld = m2OldX - m1OldX, dyOld = m2OldY - m1OldY;
                    double lenOld = Math.Sqrt(dxOld*dxOld + dyOld*dyOld);

                    // New centers from detection (WPoint master1/master2)
                    double m1NewX = master1.X, m1NewY = master1.Y;
                    double m2NewX = master2.X, m2NewY = master2.Y;
                    double dxNew = m2NewX - m1NewX, dyNew = m2NewY - m1NewY;
                    double lenNew = Math.Sqrt(dxNew*dxNew + dyNew*dyNew);

                    double scale = (lenOld > 1e-9) ? (lenNew / lenOld) : 1.0;
                    // -- lock-scale: override scale if requested --
                    double effectiveScale = _lockAnalyzeScale ? 1.0 : scale;
                    AppendLog($"[UI] AnalyzeMaster scale lock={_lockAnalyzeScale}, scale={scale:F6} -> eff={effectiveScale:F6}");
                    double angOld = Math.Atan2(dyOld, dxOld);
                    double angNew = Math.Atan2(dyNew, dxNew);
                    double angDelta = angNew - angOld; // RADIANS

                    // Apply to Master1/2 Pattern and Search using SAME pivot old->new (Master1)
                    if (_layout.Master1Pattern != null && __baseM1P != null)
                        ApplyRoiTransform(_layout.Master1Pattern, __baseM1P, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    if (_layout.Master2Pattern != null && __baseM2P != null)
                        ApplyRoiTransform(_layout.Master2Pattern, __baseM2P, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    if (_layout.Master1Search != null && __baseM1S != null)
                        ApplyRoiTransform(_layout.Master1Search,  __baseM1S, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    if (_layout.Master2Search != null && __baseM2S != null)
                        ApplyRoiTransform(_layout.Master2Search,  __baseM2S, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    if (_lastHeatmapRoi != null && __baseHeat != null)
                        ApplyRoiTransform(_lastHeatmapRoi,        __baseHeat, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    // Refresh overlays with the standard pipeline (NO args for RedrawOverlaySafe)
                    try { ScheduleSyncOverlay(true); }
                    catch
                    {
                        SyncOverlayToImage();
                        try { RedrawOverlaySafe(); }
                        catch { RedrawOverlay(); }
                        UpdateHeatmapOverlayLayoutAndClip();
                        try { RedrawAnalysisCrosses(); } catch {}
                    }

                    AppendLog("[UI] Unified transform applied to Masters + Heatmap (same as Inspection).");
                }
            }
            catch (Exception ex)
            {
                AppendLog("[UI] Unified transform failed: " + ex.Message);
            }
            // ===  END: apply the SAME transform to Masters + Heatmap ===

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

                // Centro correcto del bounding box
                double cx = x + w / 2.0;
                double cy = y + h / 2.0;
                roiCanvas.CX = cx;
                roiCanvas.CY = cy;
                roiCanvas.X = cx;   // En este proyecto X,Y representan el centro
                roiCanvas.Y = cy;

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

                // Centro correcto del bounding box
                double cx = x + w / 2.0;
                double cy = y + h / 2.0;
                roiCanvas.CX = cx;
                roiCanvas.CY = cy;
                roiCanvas.X = cx;   // X,Y = centro
                roiCanvas.Y = cy;

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

                // Centro correcto del bounding box
                double cx = x + w / 2.0;
                double cy = y + h / 2.0;
                roiCanvas.CX = cx;
                roiCanvas.CY = cy;
                roiCanvas.X = cx;   // X,Y = centro
                roiCanvas.Y = cy;

                roiCanvas.RInner = 0;
            }

            UpdateRoiLabelPosition(shape);

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

            bool hadRoiBefore = roiPixel.Role switch
            {
                RoiRole.Master1Pattern => _layout.Master1Pattern != null,
                RoiRole.Master1Search => _layout.Master1Search != null,
                RoiRole.Master2Pattern => _layout.Master2Pattern != null,
                RoiRole.Master2Search => _layout.Master2Search != null,
                RoiRole.Inspection => _layout.Inspection != null,
                _ => true
            };

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

            if (!hadRoiBefore)
            {
                UpdateRoiVisibilityControls();
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
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // if (RoiOverlay == null)
            //     return;

            // RoiOverlay disabled: labels are now drawn on Canvas only
            // RoiOverlay.Roi = CurrentRoi;
            // RoiOverlay.InvalidateOverlay();
        }

        private void UpdateOverlayFromPixelModel(RoiModel pixelModel)
        {
            if (pixelModel == null)
                return;

            ApplyPixelModelToCurrentRoi(pixelModel);
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // UpdateOverlayFromCurrentRoi();
        }

        private void SyncCurrentRoiFromInspection(RoiModel inspectionPixel)
        {
            if (inspectionPixel == null) return;

            ApplyPixelModelToCurrentRoi(inspectionPixel);
            UpdateInspectionShapeRotation(CurrentRoi.AngleDeg);
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // UpdateOverlayFromCurrentRoi();
            UpdateHeatmapOverlayLayoutAndClip();
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
                    using var heatGray = new Mat();
                    OpenCvSharp.Cv2.CvtColor(heat, heatGray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
                    _lastHeatmapRoi = HeatmapRoiModel.From(BuildCurrentRoiModel());
                    EnsureHeatmapScaleSlider();

                    byte[] gray = new byte[heatGray.Rows * heatGray.Cols];
                    heatGray.GetArray(out byte[]? tmpGray);
                    if (tmpGray != null && tmpGray.Length == gray.Length)
                    {
                        gray = tmpGray;
                    }
                    else if (tmpGray != null)
                    {
                        Array.Copy(tmpGray, gray, Math.Min(gray.Length, tmpGray.Length));
                    }

                    _lastHeatmapGray = gray;
                    _lastHeatmapW = heatGray.Cols;
                    _lastHeatmapH = heatGray.Rows;

                    RebuildHeatmapOverlayFromCache();
                    SyncHeatmapBitmapFromOverlay();

                    if (_lastHeatmapBmp != null)
                    {
                        LogHeatmap($"Heatmap Source: {_lastHeatmapBmp.PixelWidth}x{_lastHeatmapBmp.PixelHeight}, Fmt={_lastHeatmapBmp.Format}");
                    }
                    else
                    {
                        LogHeatmap("Heatmap Source: <null>");
                    }

                    // OPTIONAL: bump overlay opacity a bit (visual only)
                    if (HeatmapOverlay != null) HeatmapOverlay.Opacity = 0.90;

                    UpdateHeatmapOverlayLayoutAndClip();

                    _heatmapOverlayOpacity = HeatmapOverlay?.Opacity ?? _heatmapOverlayOpacity;
                }
                else
                {
                    _lastHeatmapBmp = null;
                    _lastHeatmapRoi = null;
                    LogHeatmap("Heatmap Source: <null>");
                    UpdateHeatmapOverlayLayoutAndClip();
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
            var (scaleX, scaleY, offsetX, offsetY) = GetImageToCanvasTransform();
            double x = px * scaleX + offsetX;
            double y = py * scaleY + offsetY;
            return new System.Windows.Point(x, y);
        }

        private System.Windows.Point ImagePxToCanvasPt(CvPoint px)
        {
            return ImagePxToCanvasPt(px.X, px.Y);
        }




        private System.Windows.Point CanvasToImage(System.Windows.Point pCanvas)
        {
            var (scaleX, scaleY, offsetX, offsetY) = GetImageToCanvasTransform();
            if (scaleX <= 0 || scaleY <= 0) return new System.Windows.Point(0, 0);
            double ix = (pCanvas.X - offsetX) / scaleX;
            double iy = (pCanvas.Y - offsetY) / scaleY;
            return new System.Windows.Point(ix, iy);
        }


        private RoiModel CanvasToImage(RoiModel roiCanvas)
        {
            var result = roiCanvas.Clone();
            var (scaleX, scaleY, offsetX, offsetY) = GetImageToCanvasTransform();
            if (scaleX <= 0 || scaleY <= 0) return result;

            result.AngleDeg = roiCanvas.AngleDeg;
            double k = Math.Min(scaleX, scaleY);

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = (roiCanvas.X - offsetX) / scaleX;
                result.Y = (roiCanvas.Y - offsetY) / scaleY;
                result.Width = roiCanvas.Width / scaleX;
                result.Height = roiCanvas.Height / scaleY;

                result.CX = result.X;
                result.CY = result.Y;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = (roiCanvas.CX - offsetX) / scaleX;
                result.CY = (roiCanvas.CY - offsetY) / scaleY;

                result.R = roiCanvas.R / k;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiCanvas.RInner / k;

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
            var (scaleX, scaleY, offsetX, offsetY) = GetImageToCanvasTransform();
            if (scaleX <= 0 || scaleY <= 0) return result;

            result.AngleDeg = roiImage.AngleDeg;
            double k = Math.Min(scaleX, scaleY);

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiImage.X * scaleX + offsetX;
                result.Y = roiImage.Y * scaleY + offsetY;
                result.Width = roiImage.Width * scaleX;
                result.Height = roiImage.Height * scaleY;

                result.CX = result.X;
                result.CY = result.Y;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiImage.CX * scaleX + offsetX;
                result.CY = roiImage.CY * scaleY + offsetY;

                result.R = roiImage.R * k;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiImage.RInner * k;

                result.X = result.CX;
                result.Y = result.CY;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }
            return result;
        }

        private void RecomputePreviewShapeAfterSync()
        {
            if (_previewShape == null) return;
            if (_tmpBuffer == null) return; // image-space ROI model while drawing

            // Map image-space preview ROI → canvas-space model
            var rc = ImageToCanvas(_tmpBuffer); // rc is canvas-space ROI

            // Position the preview shape according to rc
            if (_previewShape is System.Windows.Shapes.Rectangle)
            {
                Canvas.SetLeft(_previewShape, rc.Left);
                Canvas.SetTop(_previewShape,  rc.Top);
                _previewShape.Width  = Math.Max(1.0, rc.Width);
                _previewShape.Height = Math.Max(1.0, rc.Height);
            }
            else if (_previewShape is System.Windows.Shapes.Ellipse)
            {
                double d = Math.Max(1.0, rc.R * 2.0);
                Canvas.SetLeft(_previewShape, rc.CX - d / 2.0);
                Canvas.SetTop(_previewShape,  rc.CY - d / 2.0);
                _previewShape.Width  = d;
                _previewShape.Height = d;
            }
            else if (_previewShape is AnnulusShape ann)
            {
                double d = Math.Max(1.0, rc.R * 2.0);
                Canvas.SetLeft(ann, rc.CX - d / 2.0);
                Canvas.SetTop(ann,  rc.CY - d / 2.0);
                ann.Width  = d;
                ann.Height = d;
                double inner = Math.Max(0.0, Math.Min(rc.RInner, rc.R)); // clamp
                ann.InnerRadius = inner;
            }

            // Rotation (if applicable on preview)
            try { ApplyRoiRotationToShape(_previewShape, rc.AngleDeg); } catch { /* ignore */ }

            // Optional: reposition label tied to preview shape, if in use
            try { UpdateRoiLabelPosition(_previewShape); } catch { /* ignore if labels disabled */ }

            AppendResizeLog($"[preview] recomputed: img({(_tmpBuffer.Left):0},{(_tmpBuffer.Top):0},{(_tmpBuffer.Width):0},{(_tmpBuffer.Height):0}) → canvas L={Canvas.GetLeft(_previewShape):0},T={Canvas.GetTop(_previewShape):0}, W={_previewShape.Width:0}, H={_previewShape.Height:0}");
        }



        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen (letterbox) ===
        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen ===
        // === Sincroniza CanvasROI EXACTAMENTE al área visible de la imagen (letterbox) ===
        private void SyncOverlayToImage()
        {
            SyncOverlayToImage(scheduleResync: true);
        }

        private void SyncOverlayToImage(bool scheduleResync)
        {
            if (ImgMain == null || CanvasROI == null) return;
            if (ImgMain.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return;

            var displayRect = GetImageDisplayRect();
            if (displayRect.Width <= 0 || displayRect.Height <= 0) return;

            if (CanvasROI.Parent is not FrameworkElement parent) return;
            var imgTopLeft = ImgMain.TranslatePoint(new System.Windows.Point(0, 0), parent);

            double left = imgTopLeft.X + displayRect.Left;
            double top = imgTopLeft.Y + displayRect.Top;
            double w = displayRect.Width;
            double h = displayRect.Height;

            double roundedLeft = Math.Round(left);
            double roundedTop = Math.Round(top);
            double roundedWidth = Math.Round(w);
            double roundedHeight = Math.Round(h);

            CanvasROI.HorizontalAlignment = HorizontalAlignment.Left;
            CanvasROI.VerticalAlignment = VerticalAlignment.Top;
            CanvasROI.Margin = new Thickness(roundedLeft, roundedTop, 0, 0);
            LogHeatmap($"SyncOverlayToImage: roundedLeft={roundedLeft:F0}, roundedTop={roundedTop:F0}");
            CanvasROI.Width = roundedWidth;
            CanvasROI.Height = roundedHeight;

            if (RoiOverlay != null)
            {
                RoiOverlay.HorizontalAlignment = HorizontalAlignment.Left;
                RoiOverlay.VerticalAlignment = VerticalAlignment.Top;
                RoiOverlay.Margin = new Thickness(roundedLeft, roundedTop, 0, 0);
                RoiOverlay.Width = roundedWidth;
                RoiOverlay.Height = roundedHeight;
                RoiOverlay.SnapsToDevicePixels = true;
                RenderOptions.SetEdgeMode(RoiOverlay, EdgeMode.Aliased);
            }

            CanvasROI.SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(CanvasROI, EdgeMode.Aliased);

            AppendLog($"[sync] Canvas px=({roundedWidth:0}x{roundedHeight:0}) Offset=({roundedLeft:0},{roundedTop:0})  Img={bmp.PixelWidth}x{bmp.PixelHeight}");

            if (scheduleResync)
            {
                ScheduleSyncOverlay(force: true);
            }

            var disp = GetImageDisplayRect();
            AppendLog($"[sync] set width/height=({disp.Width:0}x{disp.Height:0}) margin=({CanvasROI.Margin.Left:0},{CanvasROI.Margin.Top:0})");
            AppendLog($"[sync] AFTER layout? canvasActual=({CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}) imgActual=({ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0})");

            UpdateHeatmapOverlayLayoutAndClip();
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
            SyncOverlayToImage(scheduleResync: false); // ← coloca CanvasROI exactamente sobre el letterbox

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

            AppendLog("[sync] post-layout redraw");
            AppendResizeLog($"[after_sync] CanvasROI={CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0} displayRect={GetImageDisplayRect().Width:0}x{GetImageDisplayRect().Height:0}");

            RedrawOverlay();            // saved ROIs
            RecomputePreviewShapeAfterSync(); // PREVIEW ROI (unsaved)
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();
            _overlayNeedsRedraw = false;
            return;
        }



        private (double scaleX, double scaleY, double offsetX, double offsetY) GetImageToCanvasTransform()
        {
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0)
                return (1.0, 1.0, 0.0, 0.0);

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
                return (1.0, 1.0, 0.0, 0.0);

            double scaleX = canvasWidth / pw;
            double scaleY = canvasHeight / ph;

            if (Math.Abs(scaleX - scaleY) > 0.001)
            {
                AppendLog($"[sync] escala no uniforme detectada canvas=({canvasWidth:0.###}x{canvasHeight:0.###}) px=({pw}x{ph}) scaleX={scaleX:0.#####} scaleY={scaleY:0.#####}");
            }

            double offsetX = displayRect.X;
            double offsetY = displayRect.Y;

            return (scaleX, scaleY, offsetX, offsetY);
        }








    }
}
