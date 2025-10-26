// Dialogs
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using BrakeDiscInspector_GUI_ROI.Workflow;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
using WRectShape = System.Windows.Shapes.Rectangle;
using Path = System.IO.Path;
using WSize = System.Windows.Size;
using LegacyROI = BrakeDiscInspector_GUI_ROI.ROI;
using ROI = BrakeDiscInspector_GUI_ROI.RoiModel;
using RoiShapeType = BrakeDiscInspector_GUI_ROI.RoiShape;
// --- BEGIN: UI/OCV type aliases ---
using SW = System.Windows;
using SWM = System.Windows.Media;
using SWShapes = System.Windows.Shapes;
using SWPoint = System.Windows.Point;
using SWRect = System.Windows.Rect;
using SWVector = System.Windows.Vector;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
// --- END: UI/OCV type aliases ---

namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged
    {
        static MainWindow()
        {
            if (RoiHudAccentBrush.CanFreeze && !RoiHudAccentBrush.IsFrozen)
            {
                RoiHudAccentBrush.Freeze();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void SetInspectionSlot(ref RoiModel? slot, RoiModel? value, string propertyName)
        {
            if (ReferenceEquals(slot, value))
            {
                return;
            }

            slot = value;
            OnPropertyChanged(propertyName);
        }

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
        private BitmapSource? _currentImageSource;
        private int _imgW, _imgH;
        private bool _hasLoadedImage;
        private bool _isFirstImageLoaded = false;

        private WorkflowViewModel? _workflowViewModel;
        private WorkflowViewModel? ViewModel => _workflowViewModel;
        private string? _dataRoot;
        private double _heatmapOverlayOpacity = 0.6;

        private RoiModel? _inspectionSlot1;
        private RoiModel? _inspectionSlot2;
        private RoiModel? _inspectionSlot3;
        private RoiModel? _inspectionSlot4;

        // Expose layout to XAML bindings (ItemsControl -> Layout.InspectionRois)
        public MasterLayout Layout => _layout;

        // Available model keys (stub: will be replaced by ModelRegistry keys)
        public System.Collections.Generic.IReadOnlyList<string> AvailableModels { get; }
            = new string[] { "default" };

        public bool IsImageLoaded => _workflowViewModel?.IsImageLoaded ?? false;

        public RoiModel? Inspection1
        {
            get => _inspectionSlot1;
            private set => SetInspectionSlot(ref _inspectionSlot1, value, nameof(Inspection1));
        }

        public RoiModel? Inspection2
        {
            get => _inspectionSlot2;
            private set => SetInspectionSlot(ref _inspectionSlot2, value, nameof(Inspection2));
        }

        public RoiModel? Inspection3
        {
            get => _inspectionSlot3;
            private set => SetInspectionSlot(ref _inspectionSlot3, value, nameof(Inspection3));
        }

        public RoiModel? Inspection4
        {
            get => _inspectionSlot4;
            private set => SetInspectionSlot(ref _inspectionSlot4, value, nameof(Inspection4));
        }

        // Available shapes (enum values)
        public System.Collections.Generic.IReadOnlyList<RoiShape> AvailableShapes { get; }
            = (RoiShape[])System.Enum.GetValues(typeof(RoiShape));

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
        // --- Fixed baseline per image (no drift) ---
        private bool _useFixedInspectionBaseline = true;        // keep using fixed baseline (must be true)
        private RoiModel? _inspectionBaselineFixed = null;      // the fixed baseline for the current image
        private bool _inspectionBaselineSeededForImage = false; // has the baseline been seeded for the current image?
        private string _lastImageSeedKey = "";                  // “signature” of the currently loaded image

        // --- Per-image master baselines (seeded on load) ---
        private string _imageKeyForMasters = "";
        private bool _mastersSeededForImage = false;
        private double _m1BaseX = 0, _m1BaseY = 0;
        private double _m2BaseX = 0, _m2BaseY = 0;

        // --- Last accepted detections on this image (for idempotence) ---
        private double _lastAccM1X = double.NaN, _lastAccM1Y = double.NaN;
        private double _lastAccM2X = double.NaN, _lastAccM2Y = double.NaN;

        // Tolerances (pixels / degrees). Tune if needed.
        private const double ANALYZE_POS_TOL_PX = 1.0;    // <=1 px considered the same
        private const double ANALYZE_ANG_TOL_DEG = 0.5;   // <=0.5° considered the same

        private bool _lockAnalyzeScale = true;                  // Size lock already in use; keep it true

        // ============================
        // Global switches / options
        // ============================

        // Respect "scale lock" by default. If you want to allow scaling of the Inspection ROI
        // *even when* the lock is ON, set this to true at runtime (e.g., via a checkbox).
        private bool _allowInspectionScaleOverride = false;

        // Freeze Master*Search movement on Analyze Master
        private const bool FREEZE_MASTER_SEARCH_ON_ANALYZE = true;

        private static readonly SolidColorBrush RoiHudAccentBrush = new SolidColorBrush(Color.FromRgb(0x39, 0xFF, 0x14));

        // ============================
        // Logging helpers (safe everywhere)
        // ============================
        [System.Diagnostics.Conditional("DEBUG")]
        private void Dbg(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
        }

        // -------- Logging shim (works with or without _log) --------
        [System.Diagnostics.Conditional("DEBUG")]
        private void LogDebug(string msg)
        {
            try
            {
                var loggerField = typeof(MainWindow).GetField("_log", BindingFlags.Instance | BindingFlags.NonPublic);
                var logger = loggerField?.GetValue(this);
                if (logger != null)
                {
                    var infoMethod = logger.GetType().GetMethod("Info", new[] { typeof(string) });
                    infoMethod?.Invoke(logger, new object[] { msg });
                }
            }
            catch
            {
                // ignore shim errors
            }

            Debug.WriteLine(msg);
        }

        // -------- Hashing / Pixel helpers --------
        private static string HashSHA256(byte[] data)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(data);
            return BitConverter.ToString(h).Replace("-", string.Empty);
        }

        private static byte[] GetPixels(BitmapSource bmp)
        {
            int stride = (bmp.PixelWidth * bmp.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        private static BitmapSource CropToBitmap(BitmapSource src, System.Windows.Int32Rect rect)
        {
            rect.X = Math.Max(0, Math.Min(rect.X, src.PixelWidth - 1));
            rect.Y = Math.Max(0, Math.Min(rect.Y, src.PixelHeight - 1));
            rect.Width = Math.Max(1, Math.Min(rect.Width, src.PixelWidth - rect.X));
            rect.Height = Math.Max(1, Math.Min(rect.Height, src.PixelHeight - rect.Y));
            var cropped = new CroppedBitmap(src, rect);
            if (cropped.CanFreeze && !cropped.IsFrozen)
            {
                try { cropped.Freeze(); } catch { }
            }
            return cropped;
        }

        private void OnImageLoaded_SetCurrentSource(BitmapSource? src)
        {
            if (src == null)
            {
                _currentImageSource = null;
                LogDebug("[ImageLoad] currentImage cleared (null).");
                return;
            }

            BitmapSource snapshot = src;
            if (!snapshot.IsFrozen)
            {
                try
                {
                    if (snapshot.CanFreeze)
                    {
                        snapshot.Freeze();
                    }
                    else
                    {
                        snapshot = snapshot.Clone();
                        if (snapshot.CanFreeze && !snapshot.IsFrozen)
                        {
                            snapshot.Freeze();
                        }
                    }
                }
                catch
                {
                    try
                    {
                        snapshot = snapshot.Clone();
                        if (snapshot.CanFreeze && !snapshot.IsFrozen)
                        {
                            snapshot.Freeze();
                        }
                    }
                    catch
                    {
                        // swallow clone/freeze issues
                    }
                }
            }

            _currentImageSource = snapshot;
            LogDebug($"[ImageLoad] currentImage set: {_currentImageSource?.PixelWidth}x{_currentImageSource?.PixelHeight} fmt={_currentImageSource?.Format}");
        }

        private static System.Windows.Int32Rect RoiRectImageSpace(RoiModel r)
        {
            double cx = !double.IsNaN(r.CX) ? r.CX : r.X;
            double cy = !double.IsNaN(r.CY) ? r.CY : r.Y;
            double w = r.Width > 0 ? r.Width : (r.R > 0 ? 2 * r.R : 1);
            double h = r.Height > 0 ? r.Height : (r.R > 0 ? 2 * r.R : 1);

            int x = (int)Math.Round(cx - w / 2.0);
            int y = (int)Math.Round(cy - h / 2.0);
            int W = Math.Max(1, (int)Math.Round(w));
            int H = Math.Max(1, (int)Math.Round(h));
            return new System.Windows.Int32Rect(x, y, W, H);
        }

        private static double NormalizeAngleRad(double ang)
        {
            // Normalize to [-pi, pi)
            ang = (ang + Math.PI) % (2.0 * Math.PI);
            if (ang < 0) ang += 2.0 * Math.PI;
            return ang - Math.PI;
        }

        private static double AngleOf(SWVector v) => Math.Atan2(v.Y, v.X);

        private static SWVector Normalize(SWVector v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 1e-12) return new SWVector(0, 0);
            return new SWVector(v.X / len, v.Y / len);
        }

        // True geometric center by shape; safe even if RoiModel lacks a GetCenter() helper.
        private static (double cx, double cy) GetCenterShapeAware(RoiModel r)
        {
            switch (r.Shape)
            {
                case RoiShape.Rectangle:
                    return (r.X, r.Y);   // X,Y used as rectangle center in this project
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    // CX/CY store circle/annulus center
                    return (r.CX, r.CY);
                default:
                    // Fallback: prefer CX/CY if set; else X/Y
                    if (!double.IsNaN(r.CX) && !double.IsNaN(r.CY)) return (r.CX, r.CY);
                    return (r.X, r.Y);
            }
        }

        // Set center in IMAGE space; updates CX/CY and Left/Top
        private static void SetRoiCenterImg(RoiModel r, double cx, double cy)
        {
            r.CX = cx; r.CY = cy;
            r.Left = cx - r.Width * 0.5;
            r.Top  = cy - r.Height * 0.5;
        }

        private static SWPoint MapBySt(SWPoint m1Base, SWPoint m2Base, SWPoint m1New, SWPoint m2New,
                                       SWPoint roiBase, bool scaleLock)
        {
            var u0 = m2Base - m1Base;
            var v0 = new SWVector(-u0.Y, u0.X);
            var L0 = Math.Sqrt(u0.X * u0.X + u0.Y * u0.Y);
            if (L0 < 1e-9) return roiBase;

            u0 = new SWVector(u0.X / L0, u0.Y / L0);
            var v0n = Normalize(v0);

            var d0 = roiBase - m1Base;
            double s = d0.X * u0.X + d0.Y * u0.Y;
            double t = d0.X * v0n.X + d0.Y * v0n.Y;

            var u1 = m2New - m1New;
            var L1 = Math.Sqrt(u1.X * u1.X + u1.Y * u1.Y);
            if (L1 < 1e-9) return roiBase;

            var u1n = new SWVector(u1.X / L1, u1.Y / L1);
            var v1n = new SWVector(-u1n.Y, u1n.X);

            if (!scaleLock)
            {
                double k = L1 / L0;
                s *= k;
                t *= k;
            }

            return new SWPoint(
                m1New.X + s * u1n.X + t * v1n.X,
                m1New.Y + s * u1n.Y + t * v1n.Y);
        }

        private static double DeltaAngleFromFrames(SWPoint m1Base, SWPoint m2Base, SWPoint m1New, SWPoint m2New)
        {
            var a0 = AngleOf(m2Base - m1Base);
            var a1 = AngleOf(m2New - m1New);
            return NormalizeAngleRad(a1 - a0);
        }

        private void RepositionMastersToCrosses(SWPoint m1Cross, SWPoint m2Cross, bool scaleLock,
                                                RoiModel? master1Baseline = null, RoiModel? master2Baseline = null)
        {
            if (_layout == null)
                return;

            var baselineM1 = master1Baseline ?? _layout.Master1Pattern?.Clone();
            var baselineM2 = master2Baseline ?? _layout.Master2Pattern?.Clone();

            if (_layout.Master1Pattern != null)
                SetRoiCenterImg(_layout.Master1Pattern, m1Cross.X, m1Cross.Y);
            if (_layout.Master2Pattern != null)
                SetRoiCenterImg(_layout.Master2Pattern, m2Cross.X, m2Cross.Y);

            if (baselineM1 == null || baselineM2 == null)
                return;

            var (m1bX, m1bY) = GetCenterShapeAware(baselineM1);
            var (m2bX, m2bY) = GetCenterShapeAware(baselineM2);
            var m1Base = new SWPoint(m1bX, m1bY);
            var m2Base = new SWPoint(m2bX, m2bY);

            double dAng = DeltaAngleFromFrames(m1Base, m2Base, m1Cross, m2Cross);

            if (_layout.Master1Pattern != null && _layout.Master1Pattern.Shape == RoiShape.Rectangle)
                _layout.Master1Pattern.AngleDeg = baselineM1.AngleDeg + dAng * (180.0 / Math.PI);

            if (_layout.Master2Pattern != null && _layout.Master2Pattern.Shape == RoiShape.Rectangle)
                _layout.Master2Pattern.AngleDeg = baselineM2.AngleDeg + dAng * (180.0 / Math.PI);
        }

        private void RepositionInspectionUsingSt(SWPoint m1Cross, SWPoint m2Cross, bool scaleLock,
                                                 RoiModel? master1Baseline = null, RoiModel? master2Baseline = null)
        {
            if (_layout == null)
                return;

            var baselineM1 = master1Baseline ?? _layout.Master1Pattern?.Clone();
            var baselineM2 = master2Baseline ?? _layout.Master2Pattern?.Clone();
            if (baselineM1 == null || baselineM2 == null)
                return;

            var (m1bX, m1bY) = GetCenterShapeAware(baselineM1);
            var (m2bX, m2bY) = GetCenterShapeAware(baselineM2);
            var m1Base = new SWPoint(m1bX, m1bY);
            var m2Base = new SWPoint(m2bX, m2bY);

            var baseVec = m2Base - m1Base;
            var newVec = m2Cross - m1Cross;
            double len0 = Math.Sqrt(baseVec.X * baseVec.X + baseVec.Y * baseVec.Y);
            double len1 = Math.Sqrt(newVec.X * newVec.X + newVec.Y * newVec.Y);
            if (len0 < 1e-9 || len1 < 1e-9)
                return;

            double scaleRatio = len1 / len0;
            bool effectiveScaleLock = scaleLock && !_allowInspectionScaleOverride;
            double angleDelta = DeltaAngleFromFrames(m1Base, m2Base, m1Cross, m2Cross);

            var roisToMove = new List<(RoiModel target, RoiModel baseline)>();
            if (_layout.Inspection != null && !_layout.Inspection.IsFrozen)
                roisToMove.Add((_layout.Inspection, _layout.Inspection.Clone()));

            foreach (var (target, baseline) in roisToMove)
            {
                if (target == null || baseline == null)
                    continue;

                double scaleFactor = effectiveScaleLock ? 1.0 : scaleRatio;

                switch (target.Shape)
                {
                    case RoiShape.Rectangle:
                        target.Width = baseline.Width * scaleFactor;
                        target.Height = baseline.Height * scaleFactor;
                        break;
                    case RoiShape.Circle:
                        target.R = baseline.R * scaleFactor;
                        target.Width = baseline.Width * scaleFactor;
                        target.Height = baseline.Height * scaleFactor;
                        break;
                    case RoiShape.Annulus:
                        target.R = baseline.R * scaleFactor;
                        target.RInner = baseline.RInner * scaleFactor;
                        target.Width = baseline.Width * scaleFactor;
                        target.Height = baseline.Height * scaleFactor;
                        break;
                }

                var (cx, cy) = GetCenterShapeAware(baseline);
                var mapped = MapBySt(m1Base, m2Base, m1Cross, m2Cross, new SWPoint(cx, cy), effectiveScaleLock);
                SetRoiCenterImg(target, mapped.X, mapped.Y);

                if (target.Shape == RoiShape.Rectangle)
                    target.AngleDeg = baseline.AngleDeg + angleDelta * (180.0 / Math.PI);
            }
        }

        private static bool IsRoiSaved(RoiModel? r)
        {
            if (r == null) return false;

            static bool finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

            switch (r.Shape)
            {
                case RoiShape.Rectangle:
                    return finite(r.X) && finite(r.Y) && r.Width > 0 && r.Height > 0;
                case RoiShape.Circle:
                    return finite(r.CX) && finite(r.CY) && r.R > 0;
                case RoiShape.Annulus:
                    return finite(r.CX) && finite(r.CY) && r.R > 0 && r.RInner >= 0 && r.R > r.RInner;
                default:
                    return false;
            }
        }

        private bool HasAllMastersAndInspectionsDefined()
        {
            if (_layout == null)
                return false;

            if (!IsRoiSaved(_layout.Master1Pattern) || !IsRoiSaved(_layout.Master2Pattern))
                return false;

            if (!TryGetMasterInspection(1, out var master1Inspection) || !IsRoiSaved(master1Inspection))
                return false;

            if (!TryGetMasterInspection(2, out var master2Inspection) || !IsRoiSaved(master2Inspection))
                return false;

            var savedInspectionRois = CollectSavedInspectionRois();
            return savedInspectionRois.Count > 0;
        }

        // Place a label tangent to a circle/annulus at angle thetaDeg (IMAGE -> CANVAS)
        private void PlaceLabelOnCircle(FrameworkElement label, RoiModel circle, double thetaDeg)
        {
            double theta = thetaDeg * Math.PI / 180.0;
            double r = circle.Shape == RoiShape.Annulus
                ? Math.Max(circle.R, circle.RInner)
                : (circle.R > 0 ? circle.R : Math.Max(circle.Width, circle.Height) * 0.5);

            double px = circle.CX + r * Math.Cos(theta);
            double py = circle.CY + r * Math.Sin(theta);
            var canvasPt = ImagePxToCanvasPt(px, py);

            // Center the label and orient tangent
            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, canvasPt.X - (label.DesiredSize.Width * 0.5));
            Canvas.SetTop(label,  canvasPt.Y - (label.DesiredSize.Height * 0.5));
            label.RenderTransform = new System.Windows.Media.RotateTransform(thetaDeg + 90.0,
                label.DesiredSize.Width * 0.5, label.DesiredSize.Height * 0.5);
        }

        // Modern label factory: Border(black + neon-green) + TextBlock(white)
        private FrameworkElement CreateStyledLabel(string text)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.Lime,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0),
                Padding = new System.Windows.Thickness(6, 2, 6, 2)
            };

            var neon = (System.Windows.Media.SolidColorBrush)
                (new System.Windows.Media.BrushConverter().ConvertFromString("#39FF14"));

            var border = new System.Windows.Controls.Border
            {
                Child = tb,
                Background = System.Windows.Media.Brushes.Black,
                BorderBrush = neon,
                BorderThickness = new System.Windows.Thickness(1.5),
                CornerRadius = new System.Windows.CornerRadius(4)
            };
            System.Windows.Controls.Panel.SetZIndex(border, int.MaxValue);
            return border;
        }

        private static readonly string InspAlignLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector", "logs", "roi_analyze_master.log");
        private readonly object _inspLogLock = new object();

        // IMAGE-space centers (pixels) of found masters
        private CvPoint? _lastM1CenterPx;
        private CvPoint? _lastM2CenterPx;

        private Shape? _previewShape;
        private bool _isDrawing;
        private SWPoint _p0;
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
        private readonly Dictionary<Shape, FrameworkElement> _roiLabels = new();
        private readonly Dictionary<RoiRole, bool> _roiCheckboxHasRoi = new();
        private bool _roiVisibilityRefreshPending;

        // Overlay visibility flags (do not affect freeze/geometry)
        private bool _showMaster1PatternOverlay = true;
        private bool _showMaster2PatternOverlay = true;
        private bool _showMaster1SearchOverlay  = true;
        private bool _showMaster2SearchOverlay  = true;

        private System.Windows.Controls.StackPanel _roiChecksPanel;
        private CheckBox? _chkHeatmap;
        private Slider? _sldHeatmapScale;
        private TextBlock? _lblHeatmapScale;
        private bool _heatmapCheckboxEventsHooked;
        private bool _heatmapSliderEventsHooked;
        private double _heatmapNormMax = 1.0; // Global heatmap scale (1.0 = default). Lower -> brighter, Higher -> darker.

        // Cache of last gray heatmap to recolor on-the-fly
        private byte[]? _lastHeatmapGray;
        private int _lastHeatmapW, _lastHeatmapH;

        private IEnumerable<RoiModel> SavedRois
        {
            get
            {
                if (_layout.Master1Pattern != null && _showMaster1PatternOverlay)
                    yield return _layout.Master1Pattern;

                if (_layout.Master2Pattern != null && _showMaster2PatternOverlay)
                    yield return _layout.Master2Pattern;

                if (_layout.Master1Search != null && _showMaster1SearchOverlay)
                    yield return _layout.Master1Search;

                if (_layout.Master2Search != null && _showMaster2SearchOverlay)
                    yield return _layout.Master2Search;

                if (_layout.Inspection != null)
                    yield return _layout.Inspection;
            }
        }

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
            => $"L={L:F3},T={T:F3},W={W:F3},H={H:F3},CX={(L+W*0.5):F3},CY={(T+H*0.5):F3})";

        private static string FRoiImg(RoiModel r)
        {
            if (r == null) return "<null>";
            return $"Role={r.Role} Img(L={r.Left:F3},T={r.Top:F3},W={r.Width:F3},H={r.Height:F3},CX={r.CX:F3},CY={r.CY:F3},R={r.R:F3},Rin={r.RInner:F3})";
        }

        private struct ImgToCanvas
        {
            public double sx, sy, offX, offY;
        }

        private ImgToCanvas GetImageToCanvasTransform()
        {
            var bs = ImgMain?.Source as BitmapSource;
            if (bs == null || ImgMain == null)
                return new ImgToCanvas { sx = 1, sy = 1, offX = 0, offY = 0 };

            double imgW = bs.PixelWidth;
            double imgH = bs.PixelHeight;
            double viewW = ImgMain.ActualWidth;
            double viewH = ImgMain.ActualHeight;
            if (imgW <= 0 || imgH <= 0 || viewW <= 0 || viewH <= 0)
                return new ImgToCanvas { sx = 1, sy = 1, offX = 0, offY = 0 };

            double scale = Math.Min(viewW / imgW, viewH / imgH);
            double drawnW = imgW * scale;
            double drawnH = imgH * scale;
            double offX = (viewW - drawnW) * 0.5;
            double offY = (viewH - drawnH) * 0.5;

            return new ImgToCanvas
            {
                sx = scale,
                sy = scale,
                offX = offX,
                offY = offY
            };
        }

        private static double R(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

        private SWRect MapImageRectToCanvas(SWRect imageRect)
        {
            var t = GetImageToCanvasTransform();
            double L = t.offX + t.sx * imageRect.X;
            double T = t.offY + t.sy * imageRect.Y;
            double W = t.sx * imageRect.Width;
            double H = t.sy * imageRect.Height;
            return new SWRect(R(L), R(T), R(W), R(H));
        }

        private SWPoint MapImagePointToCanvas(SWPoint p)
        {
            var t = GetImageToCanvasTransform();
            return new SWPoint(R(t.offX + t.sx * p.X), R(t.offY + t.sy * p.Y));
        }

        private (SWPoint c, double rOuter, double rInner) MapImageCircleToCanvas(double cx, double cy, double rOuter, double rInner)
        {
            var t = GetImageToCanvasTransform();
            var c = new SWPoint(R(t.offX + t.sx * cx), R(t.offY + t.sy * cy));
            return (c, R(t.sx * rOuter), R(t.sx * rInner));
        }

        private void RoiDiagDumpTransform(string where)
        {
            try
            {
                int srcW = 0, srcH = 0;
                try
                {
                    var bs = ImgMain?.Source as System.Windows.Media.Imaging.BitmapSource;
                    if (bs != null) { srcW = bs.PixelWidth; srcH = bs.PixelHeight; }
                }
                catch { }

                double imgVW = ImgMain?.ActualWidth ?? 0;
                double imgVH = ImgMain?.ActualHeight ?? 0;
                double canW = CanvasROI?.ActualWidth ?? 0;
                double canH = CanvasROI?.ActualHeight ?? 0;

                var t = GetImageToCanvasTransform();
                double sx = t.sx, sy = t.sy, offX = t.offX, offY = t.offY;

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

                    case FrameworkElement fe when fe.Name != null && fe.Name.StartsWith("roiLabel_"):
                    {
                        // Las etiquetas de ROI no usan Tag; se nombran como "roiLabel_<texto_sin_espacios>"
                        // Para "Master 2" el Name es "roiLabel_Master_2"
                        string name = fe.Name;
                        bool keep = name.StartsWith("roiLabel_Master_2", System.StringComparison.OrdinalIgnoreCase);
                        if (!keep) { toRemove.Add(fe); removed++; } else { kept++; }
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
            var label = CanvasROI.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(fe => fe.Name == labelName);
            if (label == null) return;

            // If Left/Top are not ready yet, defer positioning to next layout pass
            double left = Canvas.GetLeft(shape);
            double top  = Canvas.GetTop(shape);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateRoiLabelPosition(shape)), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            RoiModel? roi = null;
            if (tag is RoiModel modelTag)
                roi = modelTag;
            else if (tag is ROI legacy && legacy is not null)
            {
                // legacy ROI lacks shape info; best-effort fallback using rectangle bbox
                roi = new RoiModel
                {
                    Shape = RoiShape.Rectangle,
                    Left = Canvas.GetLeft(shape),
                    Top = Canvas.GetTop(shape),
                    Width = shape.Width,
                    Height = shape.Height
                };
            }

            if (roi == null)
                return;

            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            switch (roi.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    var roiImg = CanvasToImage(roi);
                    PlaceLabelOnCircle(label, roiImg, 30.0);
                    break;
                default:
                    Canvas.SetLeft(label, roi.Left + 6);
                    Canvas.SetTop(label,  roi.Top - 6 - label.DesiredSize.Height);
                    label.RenderTransform = null;
                    break;
            }
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
            var existing = CanvasROI.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(fe => fe.Name == labelName);
            FrameworkElement label;

            if (existing == null)
            {
                label = CreateStyledLabel(_lbl);
                label.Name = labelName;
                label.IsHitTestVisible = false;
            }
            else
            {
                label = existing;
                label.IsHitTestVisible = false;
                if (label is System.Windows.Controls.Border border && border.Child is System.Windows.Controls.TextBlock tb)
                {
                    tb.Text = _lbl;
                }
            }

            EnsureRoiCheckbox(_lbl);

            if (existing == null)
            {
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

        private void WireExistingHeatmapControls()
        {
            _chkHeatmap ??= FindName("ChkHeatmap") as CheckBox;
            if (_chkHeatmap != null && !_heatmapCheckboxEventsHooked)
            {
                _chkHeatmap.Checked += (_, __) =>
                {
                    if (HeatmapOverlay != null) HeatmapOverlay.Visibility = Visibility.Visible;
                };
                _chkHeatmap.Unchecked += (_, __) =>
                {
                    if (HeatmapOverlay != null) HeatmapOverlay.Visibility = Visibility.Collapsed;
                };
                _heatmapCheckboxEventsHooked = true;
            }

            _sldHeatmapScale ??= FindName("HeatmapScaleSlider") as Slider;
            _lblHeatmapScale ??= FindName("HeatmapScaleLabel") as TextBlock;

            if (_sldHeatmapScale != null)
            {
                _sldHeatmapScale.Minimum = 0.10;
                _sldHeatmapScale.Maximum = 2.00;
                _sldHeatmapScale.Value = _heatmapNormMax;

                if (!_heatmapSliderEventsHooked)
                {
                    _sldHeatmapScale.ValueChanged += (_, __) =>
                    {
                        _heatmapNormMax = _sldHeatmapScale!.Value;
                        if (_lblHeatmapScale != null)
                            _lblHeatmapScale.Text = $"Heatmap Scale: {_heatmapNormMax:0.00}";
                        try { RebuildHeatmapOverlayFromCache(); } catch { /* safe no-op */ }
                    };
                    _sldHeatmapScale.ValueChanged += HeatmapScaleSlider_ValueChangedSync;
                    _heatmapSliderEventsHooked = true;
                }
            }

            if (_lblHeatmapScale != null)
            {
                _lblHeatmapScale.Text = $"Heatmap Scale: {_heatmapNormMax:0.00}";
            }

            if (_chkHeatmap != null && HeatmapOverlay != null)
            {
                HeatmapOverlay.Visibility = _chkHeatmap.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
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
            if (this.DataContext == null) this.DataContext = this;
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

        private string EnsureDataRoot()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(root);

            var imagesRoot = Path.Combine(root, "images");
            Directory.CreateDirectory(imagesRoot);
            Directory.CreateDirectory(Path.Combine(imagesRoot, "ok"));
            Directory.CreateDirectory(Path.Combine(imagesRoot, "ng"));

            Directory.CreateDirectory(Path.Combine(root, "rois"));

            return root;
        }

        private void EnsureInspectionDatasetStructure()
        {
            if (_layout?.InspectionRois == null)
            {
                return;
            }

            _dataRoot ??= EnsureDataRoot();

            var roisRoot = Path.Combine(_dataRoot, "rois");
            Directory.CreateDirectory(roisRoot);

            foreach (var roi in _layout.InspectionRois)
            {
                var folderName = $"Inspection_{roi.Index}";
                var roiDir = Path.Combine(roisRoot, folderName);
                Directory.CreateDirectory(roiDir);
                Directory.CreateDirectory(Path.Combine(roiDir, "ok"));
                Directory.CreateDirectory(Path.Combine(roiDir, "ng"));

                if (!string.Equals(roi.DatasetPath, roiDir, StringComparison.OrdinalIgnoreCase))
                {
                    roi.DatasetPath = roiDir;
                }
            }
        }

        private void InitWorkflow()
        {
            try
            {
                _dataRoot = EnsureDataRoot();

                var backendClient = new Workflow.BackendClient();
                if (!string.IsNullOrWhiteSpace(BackendAPI.BaseUrl))
                {
                    backendClient.BaseUrl = BackendAPI.BaseUrl;
                }

                var datasetManager = new DatasetManager(_dataRoot);
                if (_workflowViewModel != null)
                {
                    _workflowViewModel.PropertyChanged -= WorkflowViewModelOnPropertyChanged;
                    _workflowViewModel.OverlayVisibilityChanged -= WorkflowViewModelOnOverlayVisibilityChanged;
                }

                _workflowViewModel = new WorkflowViewModel(
                    backendClient,
                    datasetManager,
                    ExportCurrentRoiCanonicalAsync,
                    () => _currentImagePathWin,
                    AppendLog,
                    ShowHeatmapOverlayAsync,
                    ClearHeatmapOverlay,
                    UpdateGlobalBadge);

                _workflowViewModel.PropertyChanged += WorkflowViewModelOnPropertyChanged;
                _workflowViewModel.OverlayVisibilityChanged += WorkflowViewModelOnOverlayVisibilityChanged;
                _workflowViewModel.IsImageLoaded = _hasLoadedImage;

                if (WorkflowHost != null)
                {
                    WorkflowHost.DataContext = _workflowViewModel;
                }

                EnsureInspectionDatasetStructure();
                _workflowViewModel.SetInspectionRoisCollection(_layout?.InspectionRois);

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


            ComboM2Role.ItemsSource = new[] { "ROI Master 2", "ROI Inspección Master 2" };
            ComboM2Role.SelectedIndex = 0;

            InitRoiVisibilityControls();

            UpdateWizardState();
            ApplyPresetToUI(_preset);
        }

        private string GetInspectionShapeFromModel()
            => ViewModel?.SelectedInspectionShape ?? "square";

        private bool GetShowMaster1Pattern() => ViewModel?.ShowMaster1Pattern ?? true;
        private bool GetShowMaster1Inspection() => ViewModel?.ShowMaster1Inspection ?? true;
        private bool GetShowMaster2Pattern() => ViewModel?.ShowMaster2Pattern ?? true;
        private bool GetShowMaster2Inspection() => ViewModel?.ShowMaster2Inspection ?? true;
        private bool GetShowInspectionRoi() => ViewModel?.ShowInspectionRoi ?? true;

        private bool GetRoiVisibility(RoiRole role) => role switch
        {
            RoiRole.Master1Pattern => GetShowMaster1Pattern(),
            RoiRole.Master1Search => GetShowMaster1Inspection(),
            RoiRole.Master2Pattern => GetShowMaster2Pattern(),
            RoiRole.Master2Search => GetShowMaster2Inspection(),
            RoiRole.Inspection => GetShowInspectionRoi(),
            _ => true
        };

        private void SetRoiVisibility(RoiRole role, bool visible)
        {
            if (ViewModel == null)
            {
                return;
            }

            switch (role)
            {
                case RoiRole.Master1Pattern:
                    if (ViewModel.ShowMaster1Pattern != visible)
                    {
                        ViewModel.ShowMaster1Pattern = visible;
                    }
                    break;
                case RoiRole.Master1Search:
                    if (ViewModel.ShowMaster1Inspection != visible)
                    {
                        ViewModel.ShowMaster1Inspection = visible;
                    }
                    break;
                case RoiRole.Master2Pattern:
                    if (ViewModel.ShowMaster2Pattern != visible)
                    {
                        ViewModel.ShowMaster2Pattern = visible;
                    }
                    break;
                case RoiRole.Master2Search:
                    if (ViewModel.ShowMaster2Inspection != visible)
                    {
                        ViewModel.ShowMaster2Inspection = visible;
                    }
                    break;
                case RoiRole.Inspection:
                    if (ViewModel.ShowInspectionRoi != visible)
                    {
                        ViewModel.ShowInspectionRoi = visible;
                    }
                    break;
            }
        }

        private void EnablePresetsTab(bool enable)
        {
            if (TabInspection != null)
            {
                TabInspection.IsEnabled = enable;
            }
        }

        private void InitRoiVisibilityControls()
        {
            _roiCheckboxHasRoi.Clear();

            UpdateRoiVisibilityControls();
        }

        private void UpdateRoiVisibilityControls()
        {
            UpdateRoiVisibilityState(RoiRole.Master1Pattern, _layout.Master1Pattern);
            UpdateRoiVisibilityState(RoiRole.Master1Search, _layout.Master1Search);
            UpdateRoiVisibilityState(RoiRole.Master2Pattern, _layout.Master2Pattern);
            UpdateRoiVisibilityState(RoiRole.Master2Search, _layout.Master2Search);
            UpdateRoiVisibilityState(RoiRole.Inspection, _layout.Inspection);

            RequestRoiVisibilityRefresh();
            UpdateRoiHud();
        }

        private void UpdateRoiVisibilityState(RoiRole role, RoiModel? model)
        {
            bool hasRoi = model != null;
            bool prevHasRoi = _roiCheckboxHasRoi.TryGetValue(role, out var prev) && prev;

            if (!hasRoi)
            {
                SetRoiVisibility(role, false);
            }
            else if (!prevHasRoi && !GetRoiVisibility(role))
            {
                SetRoiVisibility(role, true);
            }

            _roiCheckboxHasRoi[role] = hasRoi;
        }

        private void RoiVisibilityCheckChanged(object sender, RoutedEventArgs e)
        {
            RequestRoiVisibilityRefresh();
            UpdateRoiHud();
        }

        private void RequestRoiVisibilityRefresh()
        {
            if (_roiVisibilityRefreshPending)
                return;

            _roiVisibilityRefreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _roiVisibilityRefreshPending = false;
                ApplyRoiVisibilityFromViewModel();
            }), DispatcherPriority.Render);
        }

        private void ApplyRoiVisibilityFromViewModel()
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
            return GetRoiVisibility(role);
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
            EnablePresetsTab(mastersReady || _hasLoadedImage);     // permite la pestaña de inspección tras cargar imagen o completar masters

            // Selección de tab acorde a estado
            if (_state == MasterState.DrawM1_Pattern || _state == MasterState.DrawM1_Search)
                MainTabs.SelectedItem = TabMaster1;
            else if (_state == MasterState.DrawM2_Pattern || _state == MasterState.DrawM2_Search)
                MainTabs.SelectedItem = TabMaster2;
            else if (_state == MasterState.DrawInspection)
                MainTabs.SelectedItem = TabInspection;
            else
                MainTabs.SelectedItem = TabInspection;

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
            OnImageLoaded_SetCurrentSource(_imgSourceBI);
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

            if (_isFirstImageLoaded)
            {
                ClearViewerForNewImage();
            }
            else
            {
                _isFirstImageLoaded = true;
            }

            RedrawOverlay();

            ScheduleSyncOverlay(force: true);

            AppendLog($"Imagen cargada: {_imgW}x{_imgH}  (Canvas: {CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0})");
            RedrawOverlaySafe();
            ClearHeatmapOverlay();

            if (HasAllMastersAndInspectionsDefined())
            {
                try
                {
                    _ = AnalyzeMastersAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AutoAnalyze] Failed: {ex.Message}");
                }
            }

            UpdateRoiHud();

            {
                var seedKey = ComputeImageSeedKey();
                // New image => reset and seed
                if (!string.Equals(seedKey, _lastImageSeedKey, System.StringComparison.Ordinal))
                {
                    _inspectionBaselineFixed = null;
                    _inspectionBaselineSeededForImage = false;
                    InspLog($"[Seed] New image detected, oldKey='{_lastImageSeedKey}' newKey='{seedKey}' -> reset baseline.");
                    try
                    {
                        // Prefer persisted inspection ROI if available; else current on-screen inspection
                        SeedInspectionBaselineOnce(_layout?.InspectionBaseline ?? _layout?.Inspection, seedKey);
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    // Same image => do NOTHING (no re-seed)
                    InspLog($"[Seed] Same image key='{seedKey}', no re-seed.");
                }
            }

            {
                string key = ComputeImageSeedKey();
                var m1p = _layout?.Master1Pattern;
                var m2p = _layout?.Master2Pattern;

                if (!string.Equals(key, _imageKeyForMasters, System.StringComparison.Ordinal))
                {
                    _imageKeyForMasters = key;
                    _mastersSeededForImage = false;
                    _lastAccM1X = _lastAccM1Y = _lastAccM2X = _lastAccM2Y = double.NaN;
                    InspLog("[Analyze] Reset last-accepted M1/M2 for new image.");
                }
                else
                {
                    InspLog($"[Seed-M] Same image key='{key}', keep current masters baseline.");
                }

                // Seed masters BASE pivots once per image using true geometric centers (shape-aware)
                if (!_mastersSeededForImage && _layout?.Master1Pattern != null && _layout?.Master2Pattern != null)
                {
                    var (m1cx, m1cy) = GetCenterShapeAware(_layout.Master1Pattern);
                    var (m2cx, m2cy) = GetCenterShapeAware(_layout.Master2Pattern);

                    _m1BaseX = m1cx; _m1BaseY = m1cy;
                    _m2BaseX = m2cx; _m2BaseY = m2cy;
                    _mastersSeededForImage = true;

                    Dbg($"[Seed-M] New image: M1_base=({m1cx:F3},{m1cy:F3}) M2_base=({m2cx:F3},{m2cy:F3})");
                }

                if (!_mastersSeededForImage)
                {
                    InspLog("[Seed-M] WARNING: Cannot seed masters baseline (missing Master1Pattern/Master2Pattern).");
                }
            }

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
            if (_workflowViewModel != null)
            {
                _workflowViewModel.IsImageLoaded = true;
            }
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
                var labels = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .Where(fe => fe.Name != null && fe.Name.StartsWith("roiLabel_"))
                    .ToList();
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

        private void ClearViewerForNewImage()
        {
            try { ClearCanvasShapesAndLabels(); } catch { }
            try { ClearCanvasInternalMaps(); } catch { }
            try { DetachPreviewAndAdorner(); } catch { }
            try { ResetAnalysisMarks(); } catch { }

            if (RoiHudStack != null)
            {
                RoiHudStack.Children.Clear();
            }

            if (RoiHudOverlay != null)
            {
                RoiHudOverlay.Visibility = Visibility.Collapsed;
            }
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

                var toRemove = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .Where(fe => fe.Name.StartsWith("roiLabel_") && !validKeys.Contains(fe.Name))
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

            var transform = GetImageToCanvasTransform();
            double sx = transform.sx;
            double sy = transform.sy;
            double ox = transform.offX;
            double oy = transform.offY;
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

                bool isMasterRole = roi.Role == RoiRole.Master1Pattern ||
                                    roi.Role == RoiRole.Master1Search ||
                                    roi.Role == RoiRole.Master2Pattern ||
                                    roi.Role == RoiRole.Master2Search;

                switch (roi.Shape)
                {
                    case RoiShape.Rectangle:
                        {
                            double left;
                            double top;
                            double width;
                            double height;
                            double centerX;
                            double centerY;

                            if (isMasterRole)
                            {
                                var canvasRect = MapImageRectToCanvas(new SWRect(roi.Left, roi.Top, roi.Width, roi.Height));
                                left = canvasRect.X;
                                top = canvasRect.Y;
                                width = Math.Max(1.0, canvasRect.Width);
                                height = Math.Max(1.0, canvasRect.Height);
                            }
                            else
                            {
                                left = ox + roi.Left * sx;
                                top = oy + roi.Top * sy;
                                width = Math.Max(1.0, roi.Width * sx);
                                height = Math.Max(1.0, roi.Height * sy);
                            }

                            centerX = left + width / 2.0;
                            centerY = top + height / 2.0;

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
                            double cx;
                            double cy;
                            double d;

                            if (isMasterRole)
                            {
                                var mapped = MapImageCircleToCanvas(roi.CX, roi.CY, roi.R, 0);
                                cx = mapped.c.X;
                                cy = mapped.c.Y;
                                d = Math.Max(1.0, mapped.rOuter * 2.0);
                            }
                            else
                            {
                                double cxImg = roi.CX;
                                double cyImg = roi.CY;
                                double dImg = roi.R * 2.0;

                                cx = ox + cxImg * sx;
                                cy = oy + cyImg * sy;
                                d = Math.Max(1.0, dImg * k);
                            }

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
                            double cx;
                            double cy;
                            double d;
                            double innerCanvas;

                            if (isMasterRole)
                            {
                                var mapped = MapImageCircleToCanvas(roi.CX, roi.CY, roi.R, roi.RInner);
                                cx = mapped.c.X;
                                cy = mapped.c.Y;
                                double outerRadius = Math.Max(0.0, mapped.rOuter);
                                d = Math.Max(1.0, outerRadius * 2.0);
                                innerCanvas = Math.Max(0.0, Math.Min(mapped.rInner, d / 2.0));
                            }
                            else
                            {
                                double cxImg = roi.CX;
                                double cyImg = roi.CY;
                                double dImg = roi.R * 2.0;

                                cx = ox + cxImg * sx;
                                cy = oy + cyImg * sy;
                                d = Math.Max(1.0, dImg * k);
                                innerCanvas = Math.Max(0.0, Math.Min(roi.RInner * k, d / 2.0));
                            }

                            Canvas.SetLeft(shape, cx - d / 2.0);
                            Canvas.SetTop(shape, cy - d / 2.0);
                            shape.Width = d;
                            shape.Height = d;

                            if (shape is AnnulusShape ann)
                            {
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
                var _existing = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(fe => fe.Name == _labelName);
                FrameworkElement _label;
                string finalText = string.IsNullOrWhiteSpace(_lbl) ? "ROI" : _lbl;

                if (_existing == null)
                {
                    _label = CreateStyledLabel(finalText);
                    _label.Name = _labelName;
                    _label.IsHitTestVisible = false;
                    CanvasROI.Children.Add(_label);
                    Panel.SetZIndex(_label, int.MaxValue);
                }
                else
                {
                    _label = _existing;
                    _label.IsHitTestVisible = false;
                    if (_label is System.Windows.Controls.Border border && border.Child is System.Windows.Controls.TextBlock tb)
                    {
                        tb.Text = finalText;
                    }
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
            var heatmapTransform = GetImageToCanvasTransform();
            LogHeatmap($"Transform Img→Canvas: sx={heatmapTransform.sx:F6}, sy={heatmapTransform.sy:F6}, offX={heatmapTransform.offX:F4}, offY={heatmapTransform.offY:F4}]");

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
                    ClearHeatmapOverlay();
                    _lastHeatmapRoi = HeatmapRoiModel.From(export.RoiImage.Clone());
                    _heatmapOverlayOpacity = Math.Clamp(opacity, 0.0, 1.0);
                    EnterAnalysisView();
                    WireExistingHeatmapControls();

                    if (_chkHeatmap != null)
                    {
                        _chkHeatmap.IsChecked = true;
                    }

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
                    if (HeatmapOverlay != null)
                    {
                        HeatmapOverlay.Visibility = Visibility.Visible;
                        HeatmapOverlay.Opacity = 0.90;
                    }

                    UpdateHeatmapOverlayLayoutAndClip();

                    _heatmapOverlayOpacity = HeatmapOverlay?.Opacity ?? _heatmapOverlayOpacity;
                    LogDebug("[Eval] Heatmap overlay rebuilt and shown.");
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                AppendLog("[heatmap] error: " + ex.Message);
            }
        }

        public void UpdateGlobalBadge(bool? ok)
        {
            if (DiskStatusHUD == null || DiskStatusText == null)
            {
                return;
            }

            if (ok == null)
            {
                DiskStatusHUD.Visibility = Visibility.Collapsed;
                return;
            }

            DiskStatusHUD.Visibility = Visibility.Visible;
            if (ok.Value)
            {
                DiskStatusText.Text = "✅  DISK OK";
                if (DiskStatusPanel != null)
                {
                    DiskStatusPanel.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#39FF14");
                }
            }
            else
            {
                DiskStatusText.Text = "❌  DISK NOK";
                if (DiskStatusPanel != null)
                {
                    DiskStatusPanel.BorderBrush = Brushes.Red;
                }
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

        private void DrawMasterMatch(RoiModel roi, SWPoint matchImagePoint, string caption, Brush brush, bool withLabel)
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
            if (MainTabs != null && TabInspection != null)
            {
                MainTabs.SelectedItem = TabInspection;
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

        private IReadOnlyList<RoiModel> CollectSavedInspectionRois()
        {
            var results = new List<RoiModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddIfValid(RoiModel? roi)
            {
                if (roi == null)
                    return;
                if (!IsRoiSaved(roi))
                    return;

                var key = roi.Id;
                if (string.IsNullOrEmpty(key))
                    key = $"roi-{roi.GetHashCode():X}";

                if (seen.Add(key))
                    results.Add(roi);
            }

            if (_layout != null)
            {
                AddIfValid(_layout.Inspection);

                // Include any additional inspection ROIs persisted on the layout
                foreach (var prop in _layout.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!typeof(RoiModel).IsAssignableFrom(prop.PropertyType))
                        continue;

                    if (prop.GetValue(_layout) is RoiModel roi && roi.Role == RoiRole.Inspection)
                        AddIfValid(roi);
                }
            }

            if (_preset?.Rois != null)
            {
                foreach (var roi in _preset.Rois)
                {
                    if (roi?.Role == RoiRole.Inspection)
                        AddIfValid(roi);
                }
            }

            int inspectionIndex = 1;
            foreach (var roi in results.Where(r => r.Role == RoiRole.Inspection))
            {
                string fallback = $"Inspection {inspectionIndex}";
                string label = fallback;
                if (_layout?.InspectionRois != null && _layout.InspectionRois.Count >= inspectionIndex)
                {
                    label = _layout.InspectionRois[inspectionIndex - 1].DisplayName;
                }
                roi.Label = label;
                inspectionIndex++;
            }

            return results;
        }

        private void UpdateRoiHud()
        {
            if (RoiHudStack == null || RoiHudOverlay == null || _layout == null)
                return;

            int count = 0;
            RoiHudStack.Children.Clear();

            // Masters (Patterns)
            if (_layout.Master1Pattern != null && IsRoiSaved(_layout.Master1Pattern))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 1 (Pattern)",
                    () => _showMaster1PatternOverlay,
                    v  => _showMaster1PatternOverlay = v));
                count++;
            }
            if (_layout.Master2Pattern != null && IsRoiSaved(_layout.Master2Pattern))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 2 (Pattern)",
                    () => _showMaster2PatternOverlay,
                    v  => _showMaster2PatternOverlay = v));
                count++;
            }

            // Master Searches
            if (_layout.Master1Search != null && IsRoiSaved(_layout.Master1Search))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 1 Search",
                    () => _showMaster1SearchOverlay,
                    v  => _showMaster1SearchOverlay = v));
                count++;
            }
            if (_layout.Master2Search != null && IsRoiSaved(_layout.Master2Search))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 2 Search",
                    () => _showMaster2SearchOverlay,
                    v  => _showMaster2SearchOverlay = v));
                count++;
            }

            // Inspection ROIs (saved only)
            var savedInspectionRois = CollectSavedInspectionRois();
            RefreshInspectionRoiSlots(savedInspectionRois);
            foreach (var roi in savedInspectionRois)
            {
                RoiHudStack.Children.Add(CreateRoiHudItem(roi));
                count++;
            }

            RoiHudOverlay.Visibility = (count > 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement CreateHudItem(string label, Func<bool> getVisible, Action<bool> setVisible)
        {
            bool isVisible = getVisible();

            var text = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var eye = new TextBlock
            {
                Text = isVisible ? "👁" : "🚫",
                Foreground = isVisible ? Brushes.Lime : Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(6, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(text);
            sp.Children.Add(eye);

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Black,
                BorderBrush = isVisible ? (Brush)new BrushConverter().ConvertFromString("#39FF14") : Brushes.DimGray,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = sp,
                Cursor = Cursors.Hand
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                bool now = !getVisible();
                setVisible(now);
                eye.Text = now ? "👁" : "🚫";
                border.BorderBrush = now ? (Brush)new BrushConverter().ConvertFromString("#39FF14") : Brushes.DimGray;
                try { RedrawAllRois(); } catch { }
                e.Handled = true;
            };

            return border;
        }

        private FrameworkElement CreateRoiHudItem(RoiModel roi)
        {
            var labelText = ResolveRoiLabelText(roi) ?? roi.Label ?? "Inspection";
            bool isVisible = IsRoiRoleVisible(roi.Role);

            var text = new TextBlock
            {
                Text = labelText,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var eye = new TextBlock
            {
                Text = isVisible ? "👁" : "🚫",
                Foreground = isVisible ? (Brush)RoiHudAccentBrush : Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(6, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(text);
            stack.Children.Add(eye);

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Black,
                BorderBrush = isVisible ? (Brush)RoiHudAccentBrush : Brushes.DimGray,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = stack,
                Tag = roi,
                Cursor = Cursors.Hand
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                ToggleRoiVisibility(roi.Role);
                e.Handled = true;
            };

            return border;
        }

        private void ToggleRoiVisibility(RoiRole role)
        {
            if (!_roiCheckboxHasRoi.TryGetValue(role, out var hasRoi) || !hasRoi)
            {
                return;
            }

            bool newState = !GetRoiVisibility(role);
            SetRoiVisibility(role, newState);
            RedrawAllRois();
        }

        private void RefreshInspectionRoiSlots(IReadOnlyList<RoiModel>? rois = null)
        {
            IReadOnlyList<RoiModel> source = rois ?? CollectSavedInspectionRois();

            Inspection1 = source.Count > 0 ? source[0] : null;
            Inspection2 = source.Count > 1 ? source[1] : null;
            Inspection3 = source.Count > 2 ? source[2] : null;
            Inspection4 = source.Count > 3 ? source[3] : null;

            _workflowViewModel?.SetInspectionRoiModels(Inspection1, Inspection2, Inspection3, Inspection4);
        }

        private void ToggleInspectionFrozen(RoiModel? roi)
        {
            if (roi == null)
            {
                return;
            }

            roi.IsFrozen = !roi.IsFrozen;

            SetRoiAdornersVisible(roi.Id, visible: !roi.IsFrozen);
            SetRoiInteractive(roi.Id, interactive: !roi.IsFrozen);

            if (int.TryParse(roi.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                _workflowViewModel?.SetInspectionAutoRepositionEnabled(numericId, enable: !roi.IsFrozen);
            }
            else
            {
                _workflowViewModel?.SetInspectionAutoRepositionEnabled(roi.Id, enable: !roi.IsFrozen);
            }
        }

        private void BtnToggleInspection1_Click(object sender, RoutedEventArgs e)
            => ToggleInspectionFrozen(Inspection1);

        private void BtnToggleInspection2_Click(object sender, RoutedEventArgs e)
            => ToggleInspectionFrozen(Inspection2);

        private void BtnToggleInspection3_Click(object sender, RoutedEventArgs e)
            => ToggleInspectionFrozen(Inspection3);

        private void BtnToggleInspection4_Click(object sender, RoutedEventArgs e)
            => ToggleInspectionFrozen(Inspection4);

        private void SetRoiAdornersVisible(string? roiId, bool visible)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return;
            }

            var shape = FindRoiShapeById(roiId);
            if (shape == null)
            {
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
            {
                return;
            }

            if (visible)
            {
                AttachRoiAdorner(shape);
            }
            else
            {
                RemoveRoiAdorners(shape);
            }
        }

        private void SetRoiInteractive(string? roiId, bool interactive)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return;
            }

            var shape = FindRoiShapeById(roiId);
            if (shape == null)
            {
                return;
            }

            shape.IsHitTestVisible = interactive;
        }

        private Shape? FindRoiShapeById(string roiId)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return null;
            }

            return _roiShapesById.TryGetValue(roiId, out var shape) ? shape : null;
        }

        private void WorkflowViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.IsImageLoaded), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsImageLoaded));
            }
        }

        private void WorkflowViewModelOnOverlayVisibilityChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                RequestRoiVisibilityRefresh();
                UpdateRoiHud();
            });
        }

        private void OnPresetLoaded()
        {
            TryCollapseMasterEditors();
            GoToInspectionTab();
        }

        private void OnLayoutLoaded()
        {
            TryCollapseMasterEditors();
            GoToInspectionTab();
        }

        private void TryCollapseMasterEditors()
        {
            if (Master1EditorGroup != null)
            {
                Master1EditorGroup.Visibility = Visibility.Collapsed;
            }

            if (Master2EditorGroup != null)
            {
                Master2EditorGroup.Visibility = Visibility.Collapsed;
            }
        }

        private void GoToInspectionTab()
        {
            if (TabInspection != null)
            {
                TabInspection.IsEnabled = true;
            }

            if (MainTabs != null && TabInspection != null)
            {
                MainTabs.SelectedItem = TabInspection;
            }
        }

        private void RedrawAllRois()
        {
            try
            {
                RedrawOverlaySafe();
                RequestRoiVisibilityRefresh();
                UpdateRoiHud();
                RedrawAnalysisCrosses();
            }
            catch
            {
                // no-op
            }
        }
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var tray = FindName("TopLeftTray") as ToolBarTray ?? FindVisualChildByName<ToolBarTray>(this, "TopLeftTray");
            if (tray != null)
            {
                Panel.SetZIndex(tray, 1000);
                tray.Visibility = Visibility.Visible;

                if (VisualTreeHelper.GetParent(tray) is Canvas)
                {
                    Canvas.SetLeft(tray, 8);
                    Canvas.SetTop(tray, 8);
                }
            }

            ScheduleSyncOverlay(force: true);
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();

            WireExistingHeatmapControls();
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
                var t = ToLower(GetInspectionShapeFromModel());
                if (t.Contains("círculo") || t.Contains("circulo")) return RoiShape.Circle;
                if (t.Contains("annulus")) return RoiShape.Annulus;
                return RoiShape.Rectangle;
            }
        }

        private void BeginDraw(RoiShape shape, SWPoint p0)
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
                var existingLabel = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(t => t.Name == labelName);
                FrameworkElement label;
                if (existingLabel == null)
                {
                    label = CreateStyledLabel(lbl);
                    label.Name = labelName;
                    label.IsHitTestVisible = false;
                    CanvasROI.Children.Add(label);
                    Panel.SetZIndex(label, int.MaxValue);
                }
                else
                {
                    label = existingLabel;
                    label.IsHitTestVisible = false;
                    if (label is System.Windows.Controls.Border border && border.Child is System.Windows.Controls.TextBlock tbChild)
                    {
                        tbChild.Text = lbl;
                    }
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
            var pc = new SWPoint(patRect.X + patRect.Width / 2, patRect.Y + patRect.Height / 2);

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

        private SWRect RoiToRect(RoiModel r)
        {
            if (r.Shape == RoiShape.Rectangle) return new SWRect(r.Left, r.Top, r.Width, r.Height);
            var ro = r.R; return new SWRect(r.CX - ro, r.CY - ro, 2 * ro, 2 * ro);
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

            SWPoint? c1 = null, c2 = null;
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
                            if (res1.center.HasValue) { c1 = new SWPoint(res1.center.Value.X, res1.center.Value.Y); s1 = res1.score; }
                            else AppendLog("[LOCAL] M1 no encontrado");

                            var res2 = LocalMatcher.MatchInSearchROI(img, _layout.Master2Pattern, _layout.Master2Search,
                                _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax, m2Override,
                                LogToFileAndUI);
                            if (res2.center.HasValue) { c2 = new SWPoint(res2.center.Value.X, res2.center.Value.Y); s2 = res2.score; }
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
                        c1 = new SWPoint(cx, cy);
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
                        c2 = new SWPoint(cx, cy);
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
            var mid = new SWPoint((c1.Value.X + c2.Value.X) / 2.0, (c1.Value.Y + c2.Value.Y) / 2.0);
            AppendLog($"[FLOW] mid=({mid.X:0.##},{mid.Y:0.##})");

            EnterAnalysisView();

            _lastM1CenterPx = new CvPoint((int)System.Math.Round(c1.Value.X), (int)System.Math.Round(c1.Value.Y));
            _lastM2CenterPx = new CvPoint((int)System.Math.Round(c2.Value.X), (int)System.Math.Round(c2.Value.Y));
            RedrawAnalysisCrosses();

            // === BEGIN: reposition Masters & Inspections ===
            try
            {
                var crossM1 = c1.Value;
                var crossM2 = c2.Value;
                var master1Baseline = _layout?.Master1Pattern?.Clone();
                var master2Baseline = _layout?.Master2Pattern?.Clone();
                bool scaleLock = _lockAnalyzeScale;

                RepositionMastersToCrosses(crossM1, crossM2, scaleLock, master1Baseline, master2Baseline);
                RepositionInspectionUsingSt(crossM1, crossM2, scaleLock, master1Baseline, master2Baseline);

                RedrawAllRois();
                UpdateRoiHud();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnalyzeMaster-Reposition] {ex.Message}");
            }
            // === END: reposition Masters & Inspections ===

            // 5) Reubicar inspección si existe
            var inspectionRoi = _layout.Inspection;
            if (inspectionRoi == null)
            {
                Snack("Masters OK. Falta ROI de Inspección: dibújalo y guarda. Las cruces ya están dibujadas.");
                AppendLog("[FLOW] Inspection null");
                _state = MasterState.DrawInspection;
                UpdateWizardState();
                return;
            }

            if (inspectionRoi.IsFrozen)
            {
                AppendLog("[FLOW] Inspection ROI frozen; skipping auto reposition.");
            }
            else
            {
                MoveInspectionTo(inspectionRoi, c1.Value, c2.Value);
                ClipInspectionROI(inspectionRoi, _imgW, _imgH);
                AppendLog("[FLOW] Inspection movida y recortada");
            }

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
#if DEBUG
            var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Debug.WriteLine(stamped);
#endif
        }

        private void AppendLogLine(string line)
        {
#if DEBUG
            Debug.WriteLine(line);
#endif
        }

        // --------- AppendLog (para evitar CS0119 en invocaciones) ---------

        private static string FInsp(RoiModel? r) =>
            r == null ? "<null>"
                      : $"L={r.Left:F3},T={r.Top:F3},W={r.Width:F3},H={r.Height:F3},CX={r.CX:F3},CY={r.CY:F3},R={r.R:F3},Rin={r.RInner:F3},Ang={r.AngleDeg:F3}";

        private void InspLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InspAlignLogPath)!);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
                lock (_inspLogLock) { File.AppendAllText(InspAlignLogPath, line, Encoding.UTF8); }
            }
            catch { /* never throw from logging */ }
        }

        private string ComputeImageSeedKey()
        {
            try
            {
                var bs = ImgMain?.Source as BitmapSource;
                if (bs == null) return "0x0|0|0|";
                int w = bs.PixelWidth;
                int h = bs.PixelHeight;
                double dpiX = bs.DpiX;
                double dpiY = bs.DpiY;
                // PixelFormat is a struct; do NOT use '?.' here
                string fmt = bs.Format.ToString();
                return $"{w}x{h}|{dpiX:F2}|{dpiY:F2}|{fmt}";
            }
            catch
            {
                return "0x0|0|0|";
            }
        }

        private static double AngleDeg(double dy, double dx)
            => (System.Math.Atan2(dy, dx) * 180.0 / System.Math.PI);

        private static double Dist(double x1, double y1, double x2, double y2)
            => System.Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

        private void SeedInspectionBaselineOnce(RoiModel? insp, string seedKey)
        {
            if (!_useFixedInspectionBaseline) return;
            if (_inspectionBaselineSeededForImage)
            {
                InspLog($"[Seed] Skip: already seeded for key='{_lastImageSeedKey}'");
                return;
            }
            // Always prefer the persisted inspection baseline when seeding.
            var baseline = _layout?.InspectionBaseline;
            if (baseline == null)
            {
                // Fallback only if the persisted baseline is not available yet.
                baseline = insp ?? _layout?.Inspection;
            }

            if (baseline == null)
            {
                InspLog("[Seed] Skip: baseline is null");
                return;
            }

            _inspectionBaselineFixed = baseline.Clone();
            _inspectionBaselineSeededForImage = true;
            _lastImageSeedKey = seedKey;
            InspLog($"[Seed] Fixed baseline SEEDED (key='{seedKey}') from: {FInsp(_inspectionBaselineFixed)}");
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
            var (baseCx, baseCy) = GetCenterShapeAware(baseline);
            double relX = baseCx - pivotOldX;
            double relY = baseCy - pivotOldY;

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

            // Apply angle rotation for rectangular ROIs
            double dAngDeg = (angleDeltaRad * 180.0 / Math.PI);
            if (target.Shape == RoiShape.Rectangle)
                target.AngleDeg = baseline.AngleDeg + dAngDeg;
        }

        private void MoveInspectionTo(RoiModel insp, SWPoint master1, SWPoint master2)
        {
            if (insp?.IsFrozen == true)
            {
                AppendLog("[Analyze] Inspection ROI frozen; skipping MoveInspectionTo.");
                return;
            }

            // === Analyze: BEFORE state & current image key ===
            var __seedKeyNow = ComputeImageSeedKey();
            InspLog($"[Analyze] Key='{__seedKeyNow}' BEFORE insp: {FInsp(insp)}  M1=({master1.X:F3},{master1.Y:F3}) M2=({master2.X:F3},{master2.Y:F3})");

            // DEFENSIVE: do NOT re-seed here if already seeded for this image
            var fallbackBaseline = _layout?.InspectionBaseline ?? insp;
            if (_useFixedInspectionBaseline && !_inspectionBaselineSeededForImage && fallbackBaseline != null)
            {
                // Only seed if the image key is different (should not happen if [3] ran properly)
                if (!string.Equals(__seedKeyNow, _lastImageSeedKey, System.StringComparison.Ordinal))
                {
                    SeedInspectionBaselineOnce(fallbackBaseline, __seedKeyNow);
                    InspLog("[Analyze] Fallback seed performed (unexpected), key differed.");
                }
                else
                {
                    InspLog("[Analyze] Fallback seed skipped (already seeded for current image key).");
                    _inspectionBaselineSeededForImage = true;
                }
            }

            // Keep original size to restore after move (size lock)
            double __inspW0   = insp?.Width  ?? 0;
            double __inspH0   = insp?.Height ?? 0;
            double __inspR0   = insp?.R      ?? 0;
            double __inspRin0 = insp?.RInner ?? 0;

            if (insp == null)
                return;

            RoiModel? baselineInspection;
            if (_useFixedInspectionBaseline)
            {
                baselineInspection = _inspectionBaselineFixed;
                if (baselineInspection == null)
                {
                    var persistedBaseline = GetInspectionBaselineClone();
                    if (persistedBaseline != null)
                    {
                        _inspectionBaselineFixed = persistedBaseline;
                        baselineInspection = _inspectionBaselineFixed;
                        if (!_inspectionBaselineSeededForImage)
                        {
                            _inspectionBaselineSeededForImage = true;
                            _lastImageSeedKey = __seedKeyNow;
                            InspLog("[Analyze] Fallback seed from persisted baseline.");
                        }
                    }
                }
            }
            else
            {
                baselineInspection = GetInspectionBaselineClone() ?? insp.Clone();
            }

            RoiModel? __baseM1S = _layout?.Master1Search ?.Clone();
            RoiModel? __baseM2S = _layout?.Master2Search ?.Clone();
            var __baseHeat = _lastHeatmapRoi?.Clone();

            double m1NewX = master1.X, m1NewY = master1.Y;
            double m2NewX = master2.X, m2NewY = master2.Y;
            var m1_new = new SWPoint(m1NewX, m1NewY);
            var m2_new = new SWPoint(m2NewX, m2NewY);

            bool haveLast = !double.IsNaN(_lastAccM1X) && !double.IsNaN(_lastAccM2X);
            if (haveLast)
            {
                double dM1 = Dist(m1NewX, m1NewY, _lastAccM1X, _lastAccM1Y);
                double dM2 = Dist(m2NewX, m2NewY, _lastAccM2X, _lastAccM2Y);
                double angOld = AngleDeg(_lastAccM2Y - _lastAccM1Y, _lastAccM2X - _lastAccM1X);
                double angNew = AngleDeg(m2NewY - m1NewY, m2NewX - m1NewX);
                double dAng = System.Math.Abs(angNew - angOld);
                if (dAng > 180.0) dAng = 360.0 - dAng;

                if (dM1 <= ANALYZE_POS_TOL_PX && dM2 <= ANALYZE_POS_TOL_PX && dAng <= ANALYZE_ANG_TOL_DEG)
                {
                    InspLog($"[Analyze] NO-OP: detection within tolerance (dM1={dM1:F3}px, dM2={dM2:F3}px, dAng={dAng:F3}°).");
                    return;
                }
            }

            double m1OldX = _m1BaseX, m1OldY = _m1BaseY;
            double m2OldX = _m2BaseX, m2OldY = _m2BaseY;
            var m1_base = new SWPoint(_m1BaseX, _m1BaseY);
            var m2_base = new SWPoint(_m2BaseX, _m2BaseY);
            double scale = 1.0;
            double effectiveScale = 1.0;
            double angDelta = 0.0;
            bool __canTransform = baselineInspection != null && _mastersSeededForImage;
            SWVector eB = new SWVector(0, 0);
            SWVector eN = new SWVector(0, 0);

            if (__canTransform)
            {
                double dxOld = m2OldX - m1OldX;
                double dyOld = m2OldY - m1OldY;
                double lenOld = Math.Sqrt(dxOld * dxOld + dyOld * dyOld);

                double dxNew = m2NewX - m1NewX;
                double dyNew = m2NewY - m1NewY;
                double lenNew = Math.Sqrt(dxNew * dxNew + dyNew * dyNew);

                scale = (lenOld > 1e-9) ? (lenNew / lenOld) : 1.0;
                effectiveScale = _lockAnalyzeScale ? 1.0 : scale;
                AppendLog($"[UI] AnalyzeMaster scale lock={_lockAnalyzeScale}, scale={scale:F6} -> eff={effectiveScale:F6}");

                double angOldRad = Math.Atan2(dyOld, dxOld);
                double angNewRad = Math.Atan2(dyNew, dxNew);
                angDelta = angNewRad - angOldRad;

                eB = Normalize(new SWVector(dxOld, dyOld));
                eN = Normalize(new SWVector(dxNew, dyNew));

                // Normalize angle delta to [-180°, +180°)
                double deg = angDelta * 180.0 / Math.PI;
                deg = (deg + 540.0) % 360.0 - 180.0;
                angDelta = deg * Math.PI / 180.0;

                InspLog($"[Transform] BASE→NEW: M1_base=({m1OldX:F3},{m1OldY:F3}) → M1_new=({m1NewX:F3},{m1NewY:F3}); " +
                        $"M2_base=({m2OldX:F3},{m2OldY:F3}) → M2_new=({m2NewX:F3},{m2NewY:F3}); angΔ={angDelta * 180 / Math.PI:F3}°, effScale={effectiveScale:F6}");
            }

            if (!__canTransform && _lockAnalyzeScale && insp != null)
            {
                double cx = insp.CX, cy = insp.CY;
                insp.Width  = __inspW0;
                insp.Height = __inspH0;
                insp.R      = __inspR0;
                insp.RInner = __inspRin0;
                insp.Left = cx - (__inspW0 * 0.5);
                insp.Top  = cy - (__inspH0 * 0.5);
            }

            if (!_useFixedInspectionBaseline)
            {
                try
                {
                    SetInspectionBaseline(insp.Clone());
                    AppendLog("[UI] Inspection baseline refreshed (rolling mode).");
                }
                catch (Exception ex)
                {
                    AppendLog("[UI] Failed to refresh inspection baseline: " + ex.Message);
                }
            }
            else
            {
                AppendLog("[UI] Fixed Inspection baseline in use (no refresh after Analyze).");
            }

            try
            {
                if (__canTransform && _layout != null)
                {
                    // Congelar los ROIs Master Search durante Analyze (no desplazar ni rotar)
                    if (!FREEZE_MASTER_SEARCH_ON_ANALYZE)
                    {
                        if (_layout.Master1Search != null && __baseM1S != null)
                            ApplyRoiTransform(_layout.Master1Search,  __baseM1S, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                        if (_layout.Master2Search != null && __baseM2S != null)
                            ApplyRoiTransform(_layout.Master2Search,  __baseM2S, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);
                    }
                    else
                    {
                        Dbg("[Analyze] Master Search ROIs frozen: no transform applied");
                    }


                    if (_lastHeatmapRoi != null && __baseHeat != null)
                        ApplyRoiTransform(_lastHeatmapRoi, __baseHeat, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    try { ScheduleSyncOverlay(true); }
                    catch
                    {
                        SyncOverlayToImage();
                        try { RedrawOverlaySafe(); }
                        catch { RedrawOverlay(); }
                        UpdateHeatmapOverlayLayoutAndClip();
                        try { RedrawAnalysisCrosses(); } catch { }
                    }

                    AppendLog("[UI] Unified transform applied to search/heatmap ROIs.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("[UI] Unified transform failed: " + ex.Message);
            }

            SyncCurrentRoiFromInspection(insp);

            // === AnalyzeMaster: AFTER state + delta (vs FIXED baseline) ===
            InspLog($"[Analyze] AFTER  insp: {FInsp(insp)}");
            if (_inspectionBaselineFixed != null)
            {
                InspLog($"[Analyze] DELTA  : dCX={(insp.CX - _inspectionBaselineFixed.CX):F3}, dCY={(insp.CY - _inspectionBaselineFixed.CY):F3}  (fixedBaseline={_useFixedInspectionBaseline})");
            }

            _lastAccM1X = m1NewX; _lastAccM1Y = m1NewY;
            _lastAccM2X = m2NewX; _lastAccM2Y = m2NewY;
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
            BitmapSource? imageSource = null;

            await Dispatcher.InvokeAsync(() =>
            {
                RoiModel? candidate;
                if (_tmpBuffer != null && _tmpBuffer.Role == RoiRole.Inspection)
                {
                    candidate = _tmpBuffer.Clone();
                }
                else if (_layout.Inspection != null)
                {
                    candidate = _layout.Inspection.Clone();
                }
                else
                {
                    candidate = BuildCurrentRoiModel(RoiRole.Inspection);
                }

                if (candidate != null)
                {
                    roiImage = LooksLikeCanvasCoords(candidate)
                        ? CanvasToImage(candidate)
                        : candidate.Clone();
                }

                var src = _currentImageSource ?? ImgMain?.Source as BitmapSource;
                if (src != null)
                {
                    if (!src.IsFrozen)
                    {
                        try
                        {
                            if (src.CanFreeze)
                            {
                                src.Freeze();
                            }
                            else
                            {
                                var clone = src.Clone();
                                if (clone.CanFreeze && !clone.IsFrozen)
                                {
                                    clone.Freeze();
                                }
                                src = clone;
                            }
                        }
                        catch
                        {
                            try
                            {
                                var clone = src.Clone();
                                if (clone.CanFreeze && !clone.IsFrozen)
                                {
                                    clone.Freeze();
                                }
                                src = clone;
                            }
                            catch
                            {
                                // swallow clone issues
                            }
                        }
                    }

                    imageSource = src;
                }
            });

            if (roiImage == null)
            {
                Snack("No hay ROI de inspección definido.");
                return null;
            }

            if (imageSource == null)
            {
                Snack("Carga primero una imagen válida.");
                LogDebug("[Eval] currentImageSource is NULL. Aborting export.");
                return null;
            }

            return await Task.Run(() =>
            {
                var roiForExport = roiImage!.Clone();
                var roiRect = RoiRectImageSpace(roiForExport);

                using var srcMat = BitmapSourceConverter.ToMat(imageSource);
                if (srcMat.Empty())
                {
                    LogDebug("[Eval] Source Mat empty.");
                    return null;
                }

                if (!RoiCropUtils.TryBuildRoiCropInfo(roiForExport, out var cropInfo))
                {
                    LogDebug("[Eval] TryBuildRoiCropInfo failed for inspection ROI.");
                    return null;
                }

                if (!RoiCropUtils.TryGetRotatedCrop(srcMat, cropInfo, roiForExport.AngleDeg, out var cropMat, out var cropRect))
                {
                    LogDebug("[Eval] TryGetRotatedCrop failed for inspection ROI.");
                    return null;
                }

                Mat? maskMat = null;
                Mat? encodeMat = null;
                try
                {
                    bool needsMask = roiForExport.Shape == RoiShape.Circle || roiForExport.Shape == RoiShape.Annulus;
                    if (needsMask)
                    {
                        maskMat = RoiCropUtils.BuildRoiMask(cropInfo, cropRect);
                    }

                    encodeMat = RoiCropUtils.ConvertCropToBgra(cropMat, maskMat);
                    if (!Cv2.ImEncode(".png", encodeMat, out var pngBytes) || pngBytes == null || pngBytes.Length == 0)
                    {
                        LogDebug("[Eval] Failed to encode ROI crop to PNG.");
                        return null;
                    }

                    var cropBitmap = BitmapSourceConverter.ToBitmapSource(encodeMat);
                    if (cropBitmap.CanFreeze && !cropBitmap.IsFrozen)
                    {
                        try { cropBitmap.Freeze(); } catch { }
                    }

                    var imgHash = HashSHA256(GetPixels(imageSource));
                    var cropHash = HashSHA256(GetPixels(cropBitmap));
                    var cropRectInt = new System.Windows.Int32Rect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);

                    LogDebug($"[Eval] imgHash={imgHash} cropHash={cropHash} rect=({cropRectInt.X},{cropRectInt.Y},{cropRectInt.Width},{cropRectInt.Height}) roiRect=({roiRect.X},{roiRect.Y},{roiRect.Width},{roiRect.Height}) angle={roiForExport.AngleDeg:0.##}");

                    var shapeJson = BuildShapeJsonForExport(roiForExport, cropInfo, cropRect);
                    return new Workflow.RoiExportResult(pngBytes, shapeJson, roiForExport.Clone(), imgHash, cropHash, cropRectInt);
                }
                finally
                {
                    if (encodeMat != null && !ReferenceEquals(encodeMat, cropMat))
                    {
                        encodeMat.Dispose();
                    }
                    maskMat?.Dispose();
                    cropMat.Dispose();
                }
            }).ConfigureAwait(false);
        }

        private static string BuildShapeJsonForExport(RoiModel roi, RoiCropInfo cropInfo, Cv.Rect cropRect)
        {
            double w = cropRect.Width;
            double h = cropRect.Height;

            object shape = roi.Shape switch
            {
                RoiShape.Rectangle => new { kind = "rect", x = 0, y = 0, w, h },
                RoiShape.Circle => new
                {
                    kind = "circle",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = Math.Min(w, h) / 2.0
                },
                RoiShape.Annulus => new
                {
                    kind = "annulus",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = ResolveOuterRadiusPx(cropInfo, cropRect),
                    r_inner = ResolveInnerRadiusPx(cropInfo, cropRect)
                },
                _ => new { kind = "rect", x = 0, y = 0, w, h }
            };

            return JsonSerializer.Serialize(shape);
        }

        private static double ResolveOuterRadiusPx(RoiCropInfo cropInfo, Cv.Rect cropRect)
        {
            double outer = cropInfo.Radius > 0 ? cropInfo.Radius : Math.Max(cropInfo.Width, cropInfo.Height) / 2.0;
            double scale = Math.Min(
                cropRect.Width / Math.Max(cropInfo.Width, 1.0),
                cropRect.Height / Math.Max(cropInfo.Height, 1.0));
            double result = outer * scale;
            if (result <= 0)
            {
                result = Math.Min(cropRect.Width, cropRect.Height) / 2.0;
            }

            return result;
        }

        private static double ResolveInnerRadiusPx(RoiCropInfo cropInfo, Cv.Rect cropRect)
        {
            if (cropInfo.Shape != RoiShape.Annulus)
            {
                return 0;
            }

            double scale = Math.Min(
                cropRect.Width / Math.Max(cropInfo.Width, 1.0),
                cropRect.Height / Math.Max(cropInfo.Height, 1.0));
            double inner = Math.Clamp(cropInfo.InnerRadius, 0, cropInfo.Radius);
            double result = inner * scale;
            if (result < 0)
            {
                result = 0;
            }

            return result;
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
            var dlg = new OpenFileDialog
            {
                Title = "Seleccionar preset",
                Filter = "Preset JSON (*.json)|*.json",
                InitialDirectory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets")
            };

            if (dlg.ShowDialog() == true)
            {
                LoadPresetFromFile(dlg.FileName);
            }
        }

        private void LoadPresetFromFile(string filePath)
        {
            try
            {
                var preset = PresetSerializer.LoadMastersPreset(filePath);
                ApplyMastersPreset(preset);
                OnPresetLoaded();
                Snack("Preset cargado.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo cargar el preset: {ex.Message}", "Preset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyMastersPreset(MastersPreset preset)
        {
            if (preset == null)
            {
                return;
            }

            _preset.MmPerPx = preset.mm_per_px;
            ChkScaleLock.IsChecked = preset.scale_lock;
            ChkUseLocalMatcher.IsChecked = preset.use_local_matcher;

            _layout ??= new MasterLayout();

            _layout.Master1Pattern = ConvertDtoToModel(preset.Master1, RoiRole.Master1Pattern);
            _layout.Master1Search = ConvertDtoToModel(preset.Master1Inspection, RoiRole.Master1Search);
            _layout.Master2Pattern = ConvertDtoToModel(preset.Master2, RoiRole.Master2Pattern);
            _layout.Master2Search = ConvertDtoToModel(preset.Master2Inspection, RoiRole.Master2Search);

            EnsureInspectionDatasetStructure();
            _workflowViewModel?.SetInspectionRoisCollection(_layout?.InspectionRois);
            RedrawAllRois();
        }

        private static RoiModel? ConvertDtoToModel(RoiDto? dto, RoiRole role)
        {
            if (dto == null)
            {
                return null;
            }

            var model = new RoiModel
            {
                Role = role,
                Shape = MapShape(dto.Shape),
                AngleDeg = dto.AngleDeg
            };

            switch (model.Shape)
            {
                case RoiShape.Rectangle:
                    model.X = dto.CenterX;
                    model.Y = dto.CenterY;
                    model.Width = dto.Width;
                    model.Height = dto.Height;
                    break;
                case RoiShape.Circle:
                    model.CX = dto.CenterX;
                    model.CY = dto.CenterY;
                    model.R = dto.Width > 0 ? dto.Width / 2.0 : dto.Height / 2.0;
                    model.Width = dto.Width;
                    model.Height = dto.Height;
                    break;
                case RoiShape.Annulus:
                    model.CX = dto.CenterX;
                    model.CY = dto.CenterY;
                    model.R = dto.Width > 0 ? dto.Width / 2.0 : dto.Height / 2.0;
                    if (dto.InnerRadius.HasValue)
                    {
                        model.RInner = dto.InnerRadius.Value;
                    }
                    else if (dto.InnerDiameter > 0)
                    {
                        model.RInner = dto.InnerDiameter / 2.0;
                    }
                    else
                    {
                        model.RInner = dto.Height > 0 ? dto.Height / 2.0 : 0.0;
                    }
                    model.Width = dto.Width;
                    model.Height = dto.Height;
                    break;
            }

            return model;
        }

        private static RoiShape MapShape(string? shape)
        {
            return (shape ?? string.Empty).ToLowerInvariant() switch
            {
                "circle" => RoiShape.Circle,
                "annulus" => RoiShape.Annulus,
                _ => RoiShape.Rectangle
            };
        }

        private void BtnSaveLayout_Click(object sender, RoutedEventArgs e)
        {
            MasterLayoutManager.Save(_preset, _layout);
            Snack("Layout guardado.");
        }

        private void BtnLoadLayout_Click(object sender, RoutedEventArgs e)
        {
            _layout = MasterLayoutManager.LoadOrNew(_preset);
            EnsureInspectionDatasetStructure();
            _workflowViewModel?.SetInspectionRoisCollection(_layout?.InspectionRois);
            RefreshInspectionRoiSlots();
            EnsureInspectionBaselineInitialized();
            {
                var seedKey = ComputeImageSeedKey();
                if (!string.Equals(seedKey, _lastImageSeedKey, System.StringComparison.Ordinal))
                {
                    _inspectionBaselineFixed = null;
                    _inspectionBaselineSeededForImage = false;
                    InspLog($"[Seed] New image detected, oldKey='{_lastImageSeedKey}' newKey='{seedKey}' -> reset baseline.");
                    try
                    {
                        SeedInspectionBaselineOnce(_layout?.InspectionBaseline ?? _layout?.Inspection, seedKey);
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    InspLog($"[Seed] Same image key='{seedKey}', no re-seed.");
                }
            }
            ResetAnalysisMarks();
            Snack("Layout cargado.");
            UpdateWizardState();
            OnLayoutLoaded();
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
        private static System.Drawing.Rectangle ToDrawingRect(SWRect r)
        {
            return new System.Drawing.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }

        // ====== Backend (multipart) helpers ======
        private static byte[] CropTemplatePng(string imagePathWin, SWRect rect)
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
                    WireExistingHeatmapControls();

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

        /// Convierte un punto en píxeles de imagen -> punto en CanvasROI (coordenadas locales del Canvas)
        private SWPoint ImagePxToCanvasPt(double px, double py)
        {
            var transform = GetImageToCanvasTransform();
            double x = px * transform.sx + transform.offX;
            double y = py * transform.sy + transform.offY;
            return new SWPoint(x, y);
        }

        private SWPoint ImagePxToCanvasPt(CvPoint px)
        {
            return ImagePxToCanvasPt(px.X, px.Y);
        }




        private SWPoint CanvasToImage(SWPoint pCanvas)
        {
            var transform = GetImageToCanvasTransform();
            double scaleX = transform.sx;
            double scaleY = transform.sy;
            double offsetX = transform.offX;
            double offsetY = transform.offY;
            if (scaleX <= 0 || scaleY <= 0) return new SWPoint(0, 0);
            double ix = (pCanvas.X - offsetX) / scaleX;
            double iy = (pCanvas.Y - offsetY) / scaleY;
            return new SWPoint(ix, iy);
        }


        private RoiModel CanvasToImage(RoiModel roiCanvas)
        {
            var result = roiCanvas.Clone();
            var transform = GetImageToCanvasTransform();
            double scaleX = transform.sx;
            double scaleY = transform.sy;
            double offsetX = transform.offX;
            double offsetY = transform.offY;
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
            var transform = GetImageToCanvasTransform();
            double scaleX = transform.sx;
            double scaleY = transform.sy;
            double offsetX = transform.offX;
            double offsetY = transform.offY;
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

        // Helper moved to class scope so it is visible at call sites (fixes CS0103)
        private void DrawCrossAt(SWPoint p, double size = 12.0, double th = 2.0)
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
            CanvasROI?.Children.Add(h);
            CanvasROI?.Children.Add(v);
            System.Windows.Controls.Panel.SetZIndex(h, int.MaxValue - 1);
            System.Windows.Controls.Panel.SetZIndex(v, int.MaxValue - 1);
        }

        /* ======================
         * Repositioning helpers (class scope)
         * ====================== */
        // ===== Helpers: logging, centering and anchored reposition (class scope) =====
        [System.Diagnostics.Conditional("DEBUG")]
        private void LogInfo(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
        }

        private void LogDeltaToCross(string label, double roiCxImg, double roiCyImg, double crossCxImg, double crossCyImg)
        {
            var crossCanvas = ImagePxToCanvasPt(crossCxImg, crossCyImg);
            var roiCanvas   = ImagePxToCanvasPt(roiCxImg,   roiCyImg);
            double dx = roiCanvas.X - crossCanvas.X;
            double dy = roiCanvas.Y - crossCanvas.Y;
            LogInfo($"[AlignCheck] {label}: Cross(canvas)=({crossCanvas.X:F3},{crossCanvas.Y:F3}) " +
                    $"ROI(canvas)=({roiCanvas.X:F3},{roiCanvas.Y:F3}) Δ=({dx:F3},{dy:F3})");
        }

        private void RecenterAnchoredToPivot(
            RoiModel roi,
            System.Windows.Point pivotBase,
            System.Windows.Point pivotNew,
            double scale,
            double cosΔ,
            double sinΔ)
        {
            double vx = roi.CX - pivotBase.X;
            double vy = roi.CY - pivotBase.Y;
            double vxr = scale * (cosΔ * vx - sinΔ * vy);
            double vyr = scale * (sinΔ * vx + cosΔ * vy);
            SetRoiCenterImg(roi, pivotNew.X + vxr, pivotNew.Y + vyr);
        }

        private bool TryGetMasterInspection(int masterId, out RoiModel roi)
        {
            roi = null;
            if (_layout == null) return false;

            string[] candidates = new[]
            {
                masterId == 1 ? "Master1Search"      : "Master2Search",
                masterId == 1 ? "Master1Inspection" : "Master2Inspection",
                masterId == 1 ? "Master1Inspect"    : "Master2Inspect",
                masterId == 1 ? "InspectionMaster1" : "InspectionMaster2",
                masterId == 1 ? "M1Inspection"      : "M2Inspection",
                masterId == 1 ? "M1Inspect"         : "M2Inspect"
            };

            var t = _layout.GetType();
            foreach (var name in candidates)
            {
                var p = t.GetProperty(name,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.IgnoreCase);
                if (p != null && typeof(RoiModel).IsAssignableFrom(p.PropertyType))
                {
                    var val = p.GetValue(_layout) as RoiModel;
                    if (val != null) { roi = val; return true; }
                }
            }
            return false;
        }

        private void RepositionMastersAndSubRois(System.Windows.Point m1_new, System.Windows.Point m2_new)
        {
            var m1_base = new System.Windows.Point(_m1BaseX, _m1BaseY);
            var m2_base = new System.Windows.Point(_m2BaseX, _m2BaseY);

            double dxB = m2_base.X - m1_base.X, dyB = m2_base.Y - m1_base.Y;
            double dxN = m2_new.X - m1_new.X, dyN = m2_new.Y - m1_new.Y;
            double lenB = System.Math.Sqrt(dxB * dxB + dyB * dyB);
            double lenN = System.Math.Sqrt(dxN * dxN + dyN * dyN);
            double scale = (lenB > 1e-6) ? (lenN / lenB) : 1.0; // respects analyze scale lock: we don't change ROI sizes
            double angB = System.Math.Atan2(dyB, dxB);
            double angN = System.Math.Atan2(dyN, dxN);
            double angΔ = angN - angB;
            double cosΔ = System.Math.Cos(angΔ), sinΔ = System.Math.Sin(angΔ);

            // Center master pattern ROIs on detected cross centers
            if (_layout?.Master1Pattern != null) SetRoiCenterImg(_layout.Master1Pattern, m1_new.X, m1_new.Y);
            if (_layout?.Master2Pattern != null) SetRoiCenterImg(_layout.Master2Pattern, m2_new.X, m2_new.Y);

            // Anchor master inspections to their master (if present). We do NOT touch Width/Height here.
            if (TryGetMasterInspection(1, out var m1Insp)) RecenterAnchoredToPivot(m1Insp, m1_base, m1_new, scale, cosΔ, sinΔ);
            if (TryGetMasterInspection(2, out var m2Insp)) RecenterAnchoredToPivot(m2Insp, m2_base, m2_new, scale, cosΔ, sinΔ);

            // Logs for quick visual verification in canvas pixels
            if (_layout?.Master1Pattern != null) LogDeltaToCross("M1 Pattern", _layout.Master1Pattern.CX, _layout.Master1Pattern.CY, m1_new.X, m1_new.Y);
            if (_layout?.Master2Pattern != null) LogDeltaToCross("M2 Pattern", _layout.Master2Pattern.CX, _layout.Master2Pattern.CY, m2_new.X, m2_new.Y);

            if (TryGetMasterInspection(1, out m1Insp)) LogDeltaToCross("M1 Insp", m1Insp.CX, m1Insp.CY, m1_new.X, m1_new.Y);
            if (TryGetMasterInspection(2, out m2Insp)) LogDeltaToCross("M2 Insp", m2Insp.CX, m2Insp.CY, m2_new.X, m2_new.Y);
        }

        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T frameworkElement && frameworkElement.Name == name)
                {
                    return frameworkElement;
                }

                var result = FindVisualChildByName<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }





    }
}
