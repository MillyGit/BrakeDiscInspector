﻿// ROI/RoiAdorner.cs  (ADORNER DE EDICIÓN / PREVIEW, CON ROTACIÓN BÁSICA)
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BrakeDiscInspector_GUI_ROI
{
    public enum RoiAdornerChangeKind
    {
        DragStarted,
        Delta,
        DragCompleted
    }

    /// Adorner de edición para el Shape de preview:
    /// - Permite mover (Thumb central transparente).
    /// - Permite redimensionar (Thumbs en esquinas/lados) para Rect y Circle/Annulus.
    /// - Incluye rotación con el thumb NE.
    /// callback: onChanged(changeKind, modelUpdated)
    public class RoiAdorner : Adorner
    {
        private readonly RoiOverlay _overlay;
        private readonly Shape _shape;
        private readonly Action<RoiAdornerChangeKind, RoiModel> _onChanged;
        private readonly Action<string> _log;

        // Thumbs
        private readonly Thumb _moveThumb = new Thumb();
        private readonly Thumb[] _corners = new Thumb[4]; // NW, NE (rot), SE, SW
        private readonly Thumb[] _edges = new Thumb[4];   // N, E, S, W
        private readonly Thumb _rotationThumb;
        private readonly Thumb _innerRadiusThumb;

        private bool _isRotating;
        private double _rotationAngleAtDragStart;
        private double _rotationAccumulatedAngle;
        private Point _rotationPivotWorld;
        private UIElement? _rotationReferenceElement;
        private double _rotationPointerAngleAtDragStartDeg;

        public RoiAdorner(UIElement adornedElement, RoiOverlay overlay, Action<RoiAdornerChangeKind, RoiModel> onChanged, Action<string> log)
            : base(adornedElement)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _shape = adornedElement as Shape ?? throw new ArgumentException("RoiAdorner requiere Shape.", nameof(adornedElement));
            _onChanged = onChanged ?? ((_, __) => { });
            _log = log ?? (_ => { });

            IsHitTestVisible = true;

            // Estilos básicos
            StyleThumb(_moveThumb, 0, 0, 0, 0, Cursors.SizeAll, 0.0, 0.0, 0.0, 0.0);
            _moveThumb.Background = Brushes.Transparent; // grande e invisible

            for (int i = 0; i < 4; i++)
            {
                _corners[i] = new Thumb();
            }

            StyleThumb(_corners[0], 8, 8, 8, 8, Cursors.SizeAll);
            StyleThumb(_corners[2], 8, 8, 8, 8, Cursors.SizeAll);
            StyleThumb(_corners[3], 8, 8, 8, 8, Cursors.SizeAll);

            _rotationThumb = _corners[1];
            StyleRotationThumb(_rotationThumb);
            for (int i = 0; i < 4; i++)
            {
                _edges[i] = new Thumb();
                StyleThumb(_edges[i], 6, 6, 6, 6, Cursors.SizeAll);
            }

            _innerRadiusThumb = new Thumb();
            StyleThumb(_innerRadiusThumb, 10, 10, 10, 10, Cursors.SizeWE);
            _innerRadiusThumb.Visibility = Visibility.Collapsed;

            // Eventos
            _moveThumb.DragStarted += OnThumbDragStarted;
            _moveThumb.DragDelta += MoveThumb_DragDelta;
            _moveThumb.DragCompleted += OnThumbDragCompleted;

            _corners[0].DragStarted += OnThumbDragStarted;
            _corners[0].DragDelta += (s, e) => ResizeByCorner(e.HorizontalChange, e.VerticalChange, Corner.NW);
            _corners[0].DragCompleted += OnThumbDragCompleted;

            _corners[2].DragStarted += OnThumbDragStarted;
            _corners[2].DragDelta += (s, e) => ResizeByCorner(e.HorizontalChange, e.VerticalChange, Corner.SE);
            _corners[2].DragCompleted += OnThumbDragCompleted;

            _corners[3].DragStarted += OnThumbDragStarted;
            _corners[3].DragDelta += (s, e) => ResizeByCorner(e.HorizontalChange, e.VerticalChange, Corner.SW);
            _corners[3].DragCompleted += OnThumbDragCompleted;

            _rotationThumb.DragStarted += RotationThumb_DragStarted;
            _rotationThumb.DragDelta += RotationThumb_DragDelta;
            _rotationThumb.DragCompleted += RotationThumb_DragCompleted;

            _edges[0].DragStarted += OnThumbDragStarted;
            _edges[0].DragDelta += (s, e) => ResizeByEdge(e.HorizontalChange, e.VerticalChange, Edge.N); // N
            _edges[0].DragCompleted += OnThumbDragCompleted;

            _edges[1].DragStarted += OnThumbDragStarted;
            _edges[1].DragDelta += (s, e) => ResizeByEdge(e.HorizontalChange, e.VerticalChange, Edge.E); // E
            _edges[1].DragCompleted += OnThumbDragCompleted;

            _edges[2].DragStarted += OnThumbDragStarted;
            _edges[2].DragDelta += (s, e) => ResizeByEdge(e.HorizontalChange, e.VerticalChange, Edge.S); // S
            _edges[2].DragCompleted += OnThumbDragCompleted;

            _edges[3].DragStarted += OnThumbDragStarted;
            _edges[3].DragDelta += (s, e) => ResizeByEdge(e.HorizontalChange, e.VerticalChange, Edge.W); // W
            _edges[3].DragCompleted += OnThumbDragCompleted;

            _innerRadiusThumb.DragStarted += OnThumbDragStarted;
            _innerRadiusThumb.DragDelta += InnerRadiusThumb_DragDelta;
            _innerRadiusThumb.DragCompleted += OnThumbDragCompleted;

            AddVisualChild(_moveThumb);
            foreach (var t in _corners) AddVisualChild(t);
            foreach (var t in _edges) AddVisualChild(t);
            AddVisualChild(_innerRadiusThumb);
        }

        // === Layout ===
        protected override int VisualChildrenCount => 1 + _corners.Length + _edges.Length + 1;
        protected override Visual GetVisualChild(int index)
        {
            if (index == 0) return _moveThumb;

            int cornerStart = 1;
            int edgeStart = cornerStart + _corners.Length;
            int innerIndex = edgeStart + _edges.Length;

            if (index >= cornerStart && index < edgeStart)
                return _corners[index - cornerStart];

            if (index >= edgeStart && index < innerIndex)
                return _edges[index - edgeStart];

            if (index == innerIndex)
                return _innerRadiusThumb;

            throw new ArgumentOutOfRangeException(nameof(index));
        }

        protected override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            var baseT = base.GetDesiredTransform(transform);

            // Matriz: primero escalar, luego trasladar
            var m = new Matrix();
            m.ScaleAt(_overlay.Scale, _overlay.Scale, 0, 0);
            m.Translate(_overlay.OffsetX, _overlay.OffsetY);

            var gt = new GeneralTransformGroup();
            if (baseT != null) gt.Children.Add(baseT);
            gt.Children.Add(new MatrixTransform(m));
            return gt;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size renderSize = AdornedElement?.RenderSize ?? _shape.RenderSize;
            double w = renderSize.Width;
            if (double.IsNaN(w) || w <= 0)
            {
                w = _shape.Width;
            }
            if (double.IsNaN(w) || w <= 0)
            {
                w = finalSize.Width;
            }
            if (double.IsNaN(w) || w <= 0)
            {
                w = 1;
            }

            double h = renderSize.Height;
            if (double.IsNaN(h) || h <= 0)
            {
                h = _shape.Height;
            }
            if (double.IsNaN(h) || h <= 0)
            {
                h = finalSize.Height;
            }
            if (double.IsNaN(h) || h <= 0)
            {
                h = 1;
            }

            // 1) MoveThumb cubre toda el área del ROI (transparente)
            _moveThumb.Arrange(new Rect(0, 0, w, h));

            // 2) Corners y edges (posicionados alrededor)
            double r = 6;

            RoiModel? roi = _shape.Tag as RoiModel;
            bool hasRotateTransform = _shape.RenderTransform is RotateTransform;

            Point pivotLocal = GetRotationPivotLocalPoint(roi, w, h);
            bool applyManualRotation = !hasRotateTransform;
            double angleRad = applyManualRotation ? GetCurrentAngle() * Math.PI / 180.0 : 0.0;

            Point TransformPoint(Point local)
            {
                return applyManualRotation ? RotatePointAroundPivot(local, pivotLocal, angleRad) : local;
            }

            bool isAnnulus = roi?.Shape == RoiShape.Annulus;

            Point[] cornerPositions = new Point[4];
            cornerPositions[0] = TransformPoint(GetCornerLocalPoint(Corner.NW, w, h));
            cornerPositions[1] = TransformPoint(GetCornerLocalPoint(Corner.NE, w, h));
            cornerPositions[2] = TransformPoint(GetCornerLocalPoint(Corner.SE, w, h));
            cornerPositions[3] = TransformPoint(GetCornerLocalPoint(Corner.SW, w, h));

            for (int i = 0; i < _corners.Length; i++)
            {
                Point corner = cornerPositions[i];
                _corners[i].Arrange(new Rect(corner.X - r, corner.Y - r, 2 * r, 2 * r));
            }

            if (isAnnulus)
            {
                for (int i = 0; i < _edges.Length; i++)
                {
                    _edges[i].Visibility = Visibility.Collapsed;
                    _edges[i].IsHitTestVisible = false;
                    _edges[i].Arrange(new Rect(0, 0, 0, 0));
                }
            }
            else
            {
                Point[] edgePositions = new Point[4];
                edgePositions[0] = MidPoint(cornerPositions[0], cornerPositions[1]);
                edgePositions[1] = MidPoint(cornerPositions[1], cornerPositions[2]);
                edgePositions[2] = MidPoint(cornerPositions[2], cornerPositions[3]);
                edgePositions[3] = MidPoint(cornerPositions[3], cornerPositions[0]);

                for (int i = 0; i < _edges.Length; i++)
                {
                    Point edge = edgePositions[i];
                    _edges[i].Visibility = Visibility.Visible;
                    _edges[i].IsHitTestVisible = true;
                    _edges[i].Arrange(new Rect(edge.X - r, edge.Y - r, 2 * r, 2 * r));
                }
            }

            if (isAnnulus && roi != null)
            {
                double handleRadius = ResolveInnerRadiusForLayout(roi, _shape as AnnulusShape, w, h);
                Point handleLocal = new Point(w / 2.0 + handleRadius, h / 2.0);
                Point handle = TransformPoint(handleLocal);
                _innerRadiusThumb.Visibility = Visibility.Visible;
                _innerRadiusThumb.IsHitTestVisible = true;
                _innerRadiusThumb.Arrange(new Rect(handle.X - r, handle.Y - r, 2 * r, 2 * r));
            }
            else
            {
                _innerRadiusThumb.Visibility = Visibility.Collapsed;
                _innerRadiusThumb.IsHitTestVisible = false;
                _innerRadiusThumb.Arrange(new Rect(0, 0, 0, 0));
            }

            return finalSize;
        }

        // === Interacciones ===

        private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_shape.Tag is not RoiModel roi)
                return;

            if (roi.Shape == RoiShape.Annulus || roi.Shape == RoiShape.Circle)
            {
                var mouseOverlay = Mouse.GetPosition(_overlay);
                var mouseImg = _overlay.ToImage(mouseOverlay.X, mouseOverlay.Y);
                var centerScreen = _overlay.ToScreen(mouseImg.X, mouseImg.Y);

                double width = _shape.Width;
                if (double.IsNaN(width) || width <= 0)
                    width = _shape.RenderSize.Width;
                if (double.IsNaN(width) || width <= 0)
                    width = roi.Width;

                double height = _shape.Height;
                if (double.IsNaN(height) || height <= 0)
                    height = _shape.RenderSize.Height;
                if (double.IsNaN(height) || height <= 0)
                    height = roi.Height;

                double angle = GetCurrentAngle();

                double diameter = Math.Max(width, height);
                if (diameter <= 0)
                {
                    double radiusCanvas = roi.R > 0 ? roi.R : Math.Max(roi.Width, roi.Height) / 2.0;
                    diameter = Math.Max(1.0, radiusCanvas * 2.0);
                }

                ApplyResizeResult(centerScreen, diameter, diameter, angle, roi);
                _overlay.InvalidateOverlay();
                return;
            }

            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;

            double nx = x + e.HorizontalChange;
            double ny = y + e.VerticalChange;

            Canvas.SetLeft(_shape, nx);
            Canvas.SetTop(_shape, ny);

            SyncModelFromShape(_shape, roi);
            InvalidateArrange();

            _onChanged(RoiAdornerChangeKind.Delta, roi);
            _overlay.InvalidateOverlay();
        }

        private void OnThumbDragStarted(object? sender, DragStartedEventArgs e)
        {
            if (_shape.Tag is RoiModel roi)
            {
                SyncModelFromShape(_shape, roi);
                _onChanged(RoiAdornerChangeKind.DragStarted, roi);
            }
        }

        private void OnThumbDragCompleted(object? sender, DragCompletedEventArgs e)
        {
            if (_shape.Tag is RoiModel roi)
            {
                SyncModelFromShape(_shape, roi);
                _onChanged(RoiAdornerChangeKind.DragCompleted, roi);
            }
        }

        private enum Corner { NW, NE, SE, SW }
        private enum Edge { N, E, S, W }

        private void ResizeByCorner(double dragDx, double dragDy, Corner movingCorner)
        {
            var roi = _shape.Tag as RoiModel;
            if (roi == null) return;

            if (roi.Shape == RoiShape.Annulus)
            {
                ResizeAnnulusOuterFromOverlay(roi);
                return;
            }

            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;
            double w = _shape.Width; if (double.IsNaN(w)) w = 0;
            double h = _shape.Height; if (double.IsNaN(h)) h = 0;

            double angleDeg = GetCurrentAngle();
            double angleRad = angleDeg * Math.PI / 180.0;

            Vector deltaLocal = RotateVector(new Vector(dragDx, dragDy), -angleRad);

            double newWidth = w;
            double newHeight = h;

            switch (movingCorner)
            {
                case Corner.NW:
                    newWidth = w - deltaLocal.X;
                    newHeight = h - deltaLocal.Y;
                    break;
                case Corner.NE:
                    newWidth = w + deltaLocal.X;
                    newHeight = h - deltaLocal.Y;
                    break;
                case Corner.SE:
                    newWidth = w + deltaLocal.X;
                    newHeight = h + deltaLocal.Y;
                    break;
                case Corner.SW:
                    newWidth = w - deltaLocal.X;
                    newHeight = h + deltaLocal.Y;
                    break;
            }

            if (roi.Shape == RoiShape.Annulus)
            {
                double widthCandidate = newWidth;
                double heightCandidate = newHeight;
                double widthDelta = widthCandidate - w;
                double heightDelta = heightCandidate - h;

                double uniform = Math.Abs(widthDelta) >= Math.Abs(heightDelta)
                    ? widthCandidate
                    : heightCandidate;

                newWidth = uniform;
                newHeight = uniform;
            }

            const double minSize = 10.0;
            newWidth = Math.Max(minSize, newWidth);
            newHeight = Math.Max(minSize, newHeight);

            Corner anchorCorner = GetOppositeCorner(movingCorner);
            Point anchorWorld = GetCornerWorldPoint(anchorCorner, roi, x, y, w, h, angleRad);
            Point newCenter = ComputeCenterFromAnchor(anchorWorld, anchorCorner, roi, newWidth, newHeight, angleRad);

            ApplyResizeResult(newCenter, newWidth, newHeight, angleDeg, roi);
        }

        private void ResizeAnnulusOuterFromOverlay(RoiModel roi)
        {
            double width = _shape.Width;
            if (double.IsNaN(width) || width <= 0)
                width = _shape.RenderSize.Width;
            if (double.IsNaN(width) || width <= 0)
                width = roi.Width;

            double height = _shape.Height;
            if (double.IsNaN(height) || height <= 0)
                height = _shape.RenderSize.Height;
            if (double.IsNaN(height) || height <= 0)
                height = roi.Height;

            var centerLocal = new Point(width / 2.0, height / 2.0);
            var centerOverlay = _shape.TranslatePoint(centerLocal, _overlay);

            var mouseOverlay = Mouse.GetPosition(_overlay);

            var centerImg = _overlay.ToImage(centerOverlay.X, centerOverlay.Y);
            var mouseImg = _overlay.ToImage(mouseOverlay.X, mouseOverlay.Y);

            double dx = mouseImg.X - centerImg.X;
            double dy = mouseImg.Y - centerImg.Y;
            double outerImg = Math.Sqrt(dx * dx + dy * dy);
            if (outerImg <= 0)
                return;

            double innerCanvas = roi.RInner;
            double innerImg = _overlay.ToImageLen(innerCanvas);
            double innerResolved = outerImg > 0
                ? AnnulusDefaults.ClampInnerRadius(innerImg, outerImg)
                : 0;

            double outerScreen = _overlay.ToScreenLen(outerImg);
            if (outerScreen <= 0)
                outerScreen = 1.0;

            double innerScreen = _overlay.ToScreenLen(innerResolved);

            if (_shape is AnnulusShape annulusShape)
            {
                annulusShape.InnerRadius = innerScreen;
            }

            var centerScreen = _overlay.ToScreen(centerImg.X, centerImg.Y);
            double diameterScreen = outerScreen * 2.0;
            double angle = GetCurrentAngle();

            ApplyResizeResult(centerScreen, diameterScreen, diameterScreen, angle, roi);
            _overlay.InvalidateOverlay();
        }

        private void ResizeByEdge(double dragDx, double dragDy, Edge edge)
        {
            var roi = _shape.Tag as RoiModel;
            if (roi == null || roi.Shape == RoiShape.Annulus) return;

            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;
            double w = _shape.Width; if (double.IsNaN(w)) w = 0;
            double h = _shape.Height; if (double.IsNaN(h)) h = 0;

            double angleDeg = GetCurrentAngle();
            double angleRad = angleDeg * Math.PI / 180.0;

            Vector deltaLocal = RotateVector(new Vector(dragDx, dragDy), -angleRad);

            double newWidth = w;
            double newHeight = h;

            switch (edge)
            {
                case Edge.N:
                    newHeight = h - deltaLocal.Y;
                    break;
                case Edge.E:
                    newWidth = w + deltaLocal.X;
                    break;
                case Edge.S:
                    newHeight = h + deltaLocal.Y;
                    break;
                case Edge.W:
                    newWidth = w - deltaLocal.X;
                    break;
            }

            const double minSize = 10.0;
            newWidth = Math.Max(minSize, newWidth);
            newHeight = Math.Max(minSize, newHeight);

            var (anchorA, anchorB) = GetEdgeAnchorCorners(edge);
            Point anchorAWorld = GetCornerWorldPoint(anchorA, roi, x, y, w, h, angleRad);
            Point anchorBWorld = GetCornerWorldPoint(anchorB, roi, x, y, w, h, angleRad);

            Point centerA = ComputeCenterFromAnchor(anchorAWorld, anchorA, roi, newWidth, newHeight, angleRad);
            Point centerB = ComputeCenterFromAnchor(anchorBWorld, anchorB, roi, newWidth, newHeight, angleRad);
            Point newCenter = new Point((centerA.X + centerB.X) / 2.0, (centerA.Y + centerB.Y) / 2.0);

            ApplyResizeResult(newCenter, newWidth, newHeight, angleDeg, roi);
        }

        private void InnerRadiusThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_shape.Tag is not RoiModel roi || roi.Shape != RoiShape.Annulus)
                return;

            var (width, height) = GetShapeSize();
            double angleRad = GetCurrentAngle() * Math.PI / 180.0;
            Vector deltaLocal = RotateVector(new Vector(e.HorizontalChange, e.VerticalChange), -angleRad);

            double available = Math.Min(width, height) / 2.0;
            if (double.IsNaN(available) || available <= 0)
            {
                available = 0;
            }

            double outerRadius = roi.R > 0 ? roi.R : available;
            if (outerRadius <= 0)
                outerRadius = available;

            double currentInner = roi.RInner > 0
                ? AnnulusDefaults.ClampInnerRadius(roi.RInner, outerRadius)
                : AnnulusDefaults.ResolveInnerRadius(roi.RInner, outerRadius);

            double newInner = currentInner + deltaLocal.X;
            newInner = AnnulusDefaults.ClampInnerRadius(newInner, outerRadius);
            if (available > 0)
                newInner = Math.Min(newInner, available);

            if (_shape is AnnulusShape annulus)
            {
                annulus.InnerRadius = newInner;
            }

            roi.RInner = newInner;
            SyncModelFromShape(_shape, roi);
            InvalidateArrange();
            _onChanged(RoiAdornerChangeKind.Delta, roi);
            _overlay.InvalidateOverlay();
        }

        // === Rotación ===
        private void RotationThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (_shape.Tag is not RoiModel roi)
            {
                _isRotating = false;
                return;
            }

            _isRotating = true;
            _rotationAngleAtDragStart = NormalizeAngle(GetCurrentAngle());
            _rotationAccumulatedAngle = 0;

            _rotationReferenceElement = GetRotationReferenceElement();
            UIElement referenceElement = _rotationReferenceElement ?? _shape;

            var (width, height) = GetShapeSize();
            Point pivotLocal = GetRotationPivotLocalPoint(roi, width, height);
            UpdateRotationCenterIfNeeded(roi, width, height);
            _rotationPivotWorld = _shape.TranslatePoint(pivotLocal, referenceElement);

            Point pointerStart = Mouse.GetPosition(referenceElement);
            Vector pointerVector = new Vector(pointerStart.X - _rotationPivotWorld.X, pointerStart.Y - _rotationPivotWorld.Y);
            _rotationPointerAngleAtDragStartDeg = Math.Atan2(pointerVector.Y, pointerVector.X) * 180.0 / Math.PI;

            SetNonRotationThumbsEnabled(false);
            _rotationThumb.IsHitTestVisible = true;

            _log($"[rotate] start roi={roi.Id} angle={_rotationAngleAtDragStart:0.##} pivot=({_rotationPivotWorld.X:0.##},{_rotationPivotWorld.Y:0.##}) pointerAngle={_rotationPointerAngleAtDragStartDeg:0.##}");

            _onChanged(RoiAdornerChangeKind.DragStarted, roi);
        }

        private void RotationThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isRotating || _shape.Tag is not RoiModel roi)
                return;

            UIElement referenceElement = _rotationReferenceElement ?? _shape;

            Point pointerPosition = Mouse.GetPosition(referenceElement);
            Vector pointerVector = new Vector(pointerPosition.X - _rotationPivotWorld.X, pointerPosition.Y - _rotationPivotWorld.Y);
            double pointerAngleDeg = Math.Atan2(pointerVector.Y, pointerVector.X) * 180.0 / Math.PI;

            double angleDeltaDeg = NormalizeAngle(pointerAngleDeg - _rotationPointerAngleAtDragStartDeg);
            _rotationAccumulatedAngle = angleDeltaDeg;

            double newAngle = NormalizeAngle(_rotationAngleAtDragStart + _rotationAccumulatedAngle);
            ApplyRotation(newAngle, roi);

            _onChanged(RoiAdornerChangeKind.Delta, roi);

            _log($"[rotate] delta roi={roi.Id} pointer=({pointerPosition.X:0.##},{pointerPosition.Y:0.##}) pointerAngle={pointerAngleDeg:0.##} delta={angleDeltaDeg:0.##} angle={newAngle:0.##}");
        }

        private void RotationThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            SetNonRotationThumbsEnabled(true);

            if (!_isRotating)
                return;

            _isRotating = false;
            _rotationAccumulatedAngle = 0;
            _rotationReferenceElement = null;

            if (_shape.Tag is RoiModel roi)
            {
                double finalAngle = NormalizeAngle(GetCurrentAngle());
                ApplyRotation(finalAngle, roi);
                _onChanged(RoiAdornerChangeKind.DragCompleted, roi);

                _log($"[rotate] end roi={roi.Id} angle={finalAngle:0.##}");
            }
        }

        // === Utilidades ===
        private static void StyleThumb(Thumb t, double w, double h, double mw, double mh, Cursor cursor, double mL = 0, double mT = 0, double mR = 0, double mB = 0)
        {
            t.Cursor = cursor;
            t.Width = w > 0 ? w : 20;  // moveThumb será grande por defecto
            t.Height = h > 0 ? h : 20;
            t.MinWidth = mw;
            t.MinHeight = mh;
            t.Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            t.BorderBrush = Brushes.White;
            t.BorderThickness = new Thickness(1);
            t.Opacity = (w == 0 && h == 0) ? 0.0 : 0.8; // moveThumb transparente
            t.Margin = new Thickness(mL, mT, mR, mB);
        }

        private static void StyleRotationThumb(Thumb thumb)
        {
            thumb.Cursor = Cursors.Hand;
            thumb.Width = 14;
            thumb.Height = 14;
            thumb.MinWidth = 14;
            thumb.MinHeight = 14;
            thumb.Background = Brushes.Transparent;
            thumb.BorderBrush = Brushes.Transparent;
            thumb.BorderThickness = new Thickness(0);
            thumb.Opacity = 1.0;
            thumb.Margin = new Thickness(0);
            thumb.Template = CreateCircularThumbTemplate(Brushes.White, Brushes.SteelBlue, 1.5);
        }

        private static ControlTemplate CreateCircularThumbTemplate(Brush fill, Brush stroke, double thickness)
        {
            var template = new ControlTemplate(typeof(Thumb));
            var ellipseFactory = new FrameworkElementFactory(typeof(Ellipse));
            ellipseFactory.SetValue(Shape.FillProperty, fill);
            ellipseFactory.SetValue(Shape.StrokeProperty, stroke);
            ellipseFactory.SetValue(Shape.StrokeThicknessProperty, thickness);
            template.VisualTree = ellipseFactory;
            return template;
        }

        private UIElement? GetRotationReferenceElement()
        {
            if (_shape.Parent is UIElement directParent)
            {
                return directParent;
            }

            DependencyObject? parent = VisualTreeHelper.GetParent(_shape);
            while (parent != null && !(parent is UIElement))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent as UIElement;
        }

        private void SetNonRotationThumbsEnabled(bool enabled)
        {
            _moveThumb.IsHitTestVisible = enabled;
            foreach (var thumb in _corners)
            {
                if (thumb == _rotationThumb)
                    continue;
                thumb.IsHitTestVisible = enabled;
            }

            foreach (var thumb in _edges)
                thumb.IsHitTestVisible = enabled;

            bool annulusActive = (_shape.Tag as RoiModel)?.Shape == RoiShape.Annulus;
            _innerRadiusThumb.IsHitTestVisible = enabled && annulusActive;
        }

        private void ApplyRotation(double angleDeg, RoiModel roi)
        {
            var (width, height) = GetShapeSize();
            Point pivot = GetRotationPivotLocalPoint(roi, width, height);
            double centerX = pivot.X;
            double centerY = pivot.Y;

            if (_shape.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = angleDeg;
                rotate.CenterX = centerX;
                rotate.CenterY = centerY;
            }
            else
            {
                _shape.RenderTransform = new RotateTransform(angleDeg, centerX, centerY);
            }

            roi.AngleDeg = angleDeg;
            InvalidateArrange();
        }

        private double GetCurrentAngle()
        {
            if (_shape.RenderTransform is RotateTransform rotate)
                return rotate.Angle;

            if (_shape.Tag is RoiModel roi)
                return roi.AngleDeg;

            return 0.0;
        }

        private (double width, double height) GetShapeSize()
        {
            double width = _shape.Width;
            if (double.IsNaN(width) || width <= 0)
                width = _shape.RenderSize.Width;
            if (double.IsNaN(width) || width <= 0)
                width = _shape.DesiredSize.Width;

            if ((double.IsNaN(width) || width <= 0) && _shape.Tag is RoiModel roi)
            {
                width = roi.Shape switch
                {
                    RoiShape.Rectangle => roi.Width,
                    RoiShape.Circle or RoiShape.Annulus => roi.R > 0 ? roi.R * 2.0 : roi.Width,
                    _ => width
                };
            }

            double height = _shape.Height;
            if (double.IsNaN(height) || height <= 0)
                height = _shape.RenderSize.Height;
            if (double.IsNaN(height) || height <= 0)
                height = _shape.DesiredSize.Height;

            if ((double.IsNaN(height) || height <= 0) && _shape.Tag is RoiModel roiModel)
            {
                height = roiModel.Shape switch
                {
                    RoiShape.Rectangle => roiModel.Height,
                    RoiShape.Circle or RoiShape.Annulus => roiModel.R > 0 ? roiModel.R * 2.0 : roiModel.Height,
                    _ => height
                };
            }

            if (double.IsNaN(width) || width <= 0) width = 1;
            if (double.IsNaN(height) || height <= 0) height = 1;

            return (width, height);
        }

        private static double NormalizeAngle(double angleDeg)
        {
            angleDeg %= 360.0;
            if (angleDeg <= -180.0)
                angleDeg += 360.0;
            else if (angleDeg > 180.0)
                angleDeg -= 360.0;
            return angleDeg;
        }

        private static Corner GetOppositeCorner(Corner corner)
        {
            return corner switch
            {
                Corner.NW => Corner.SE,
                Corner.NE => Corner.SW,
                Corner.SE => Corner.NW,
                Corner.SW => Corner.NE,
                _ => Corner.SE
            };
        }

        private static Vector GetCornerLocalVector(Corner corner, double width, double height)
        {
            double halfWidth = width / 2.0;
            double halfHeight = height / 2.0;

            return corner switch
            {
                Corner.NW => new Vector(-halfWidth, -halfHeight),
                Corner.NE => new Vector(halfWidth, -halfHeight),
                Corner.SE => new Vector(halfWidth, halfHeight),
                Corner.SW => new Vector(-halfWidth, halfHeight),
                _ => new Vector(0, 0)
            };
        }

        private static Point GetCornerLocalPoint(Corner corner, double width, double height)
        {
            Point center = new Point(width / 2.0, height / 2.0);
            Vector offset = GetCornerLocalVector(corner, width, height);
            return new Point(center.X + offset.X, center.Y + offset.Y);
        }

        private static double ResolveInnerRadiusForLayout(RoiModel roi, AnnulusShape? annulus, double width, double height)
        {
            double available = Math.Min(width, height) / 2.0;
            if (double.IsNaN(available) || available <= 0)
                return 0;

            double outer = roi.R > 0 ? roi.R : available;
            if (outer <= 0)
                outer = available;

            double currentInner = annulus?.InnerRadius ?? roi.RInner;
            double resolved = currentInner > 0
                ? AnnulusDefaults.ClampInnerRadius(currentInner, outer)
                : AnnulusDefaults.ResolveInnerRadius(currentInner, outer);

            if (available > 0)
                resolved = Math.Min(resolved, available);

            roi.RInner = resolved;

            if (annulus != null)
            {
                annulus.InnerRadius = resolved;
            }

            return resolved;
        }

        private static (Corner a, Corner b) GetEdgeAnchorCorners(Edge edge)
        {
            return edge switch
            {
                Edge.N => (Corner.SW, Corner.SE),
                Edge.E => (Corner.NW, Corner.SW),
                Edge.S => (Corner.NW, Corner.NE),
                Edge.W => (Corner.NE, Corner.SE),
                _ => (Corner.SW, Corner.SE)
            };
        }

        private static Vector RotateVector(Vector vector, double angleRad)
        {
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            return new Vector(vector.X * cos - vector.Y * sin, vector.X * sin + vector.Y * cos);
        }

        private static Point RotatePointAroundPivot(Point point, Point pivot, double angleRad)
        {
            Vector relative = point - pivot;
            Vector rotated = RotateVector(relative, angleRad);
            return new Point(pivot.X + rotated.X, pivot.Y + rotated.Y);
        }

        internal static Point GetRotationPivotLocalPoint(RoiModel? roi, double width, double height)
        {
            double safeWidth = double.IsNaN(width) || width <= 0 ? 0 : width;
            double safeHeight = double.IsNaN(height) || height <= 0 ? 0 : height;

            return new Point(safeWidth / 2.0, safeHeight / 2.0);
        }

        internal static Point GetRotationPivotWorldPoint(RoiModel? roi, double left, double top, double width, double height)
        {
            Point pivotLocal = GetRotationPivotLocalPoint(roi, width, height);
            return new Point(left + pivotLocal.X, top + pivotLocal.Y);
        }

        private static Point MidPoint(Point a, Point b)
        {
            return new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
        }

        private static Point GetCornerWorldPoint(Corner corner, RoiModel? roi, double left, double top, double width, double height, double angleRad)
        {
            Point pivotLocal = GetRotationPivotLocalPoint(roi, width, height);
            Point cornerLocal = GetCornerLocalPoint(corner, width, height);
            Vector relative = cornerLocal - pivotLocal;
            Vector rotated = RotateVector(relative, angleRad);
            Point pivotWorld = new Point(left + pivotLocal.X, top + pivotLocal.Y);
            return new Point(pivotWorld.X + rotated.X, pivotWorld.Y + rotated.Y);
        }

        private static Point ComputeCenterFromAnchor(Point anchorWorld, Corner anchorCorner, RoiModel? roi, double width, double height, double angleRad)
        {
            Point pivotLocal = GetRotationPivotLocalPoint(roi, width, height);
            Point cornerLocal = GetCornerLocalPoint(anchorCorner, width, height);
            Vector relative = cornerLocal - pivotLocal;
            Vector rotated = RotateVector(relative, angleRad);
            Point pivotWorld = new Point(anchorWorld.X - rotated.X, anchorWorld.Y - rotated.Y);
            Point topLeft = new Point(pivotWorld.X - pivotLocal.X, pivotWorld.Y - pivotLocal.Y);
            return new Point(topLeft.X + width / 2.0, topLeft.Y + height / 2.0);
        }

        private void ApplyResizeResult(Point center, double width, double height, double angleDeg, RoiModel roi)
        {
            if (roi.Shape == RoiShape.Annulus)
            {
                double uniform = (width + height) / 2.0;
                width = uniform;
                height = uniform;
            }

            double left = center.X - width / 2.0;
            double top = center.Y - height / 2.0;

            Canvas.SetLeft(_shape, left);
            Canvas.SetTop(_shape, top);
            _shape.Width = width;
            _shape.Height = height;

            if (_shape is AnnulusShape annulusShape)
            {
                double maxInner = Math.Min(width, height) / 2.0;
                annulusShape.InnerRadius = Math.Max(0, Math.Min(annulusShape.InnerRadius, maxInner));
            }

            ApplyRotation(angleDeg, roi);
            SyncModelFromShape(_shape, roi);
            InvalidateArrange();

            _onChanged(RoiAdornerChangeKind.Delta, roi);
        }

        private void UpdateRotationCenterIfNeeded(RoiModel? roi, double width, double height)
        {
            if (_shape.RenderTransform is not RotateTransform rotate)
                return;

            Point pivot = GetRotationPivotLocalPoint(roi, width, height);
            rotate.CenterX = pivot.X;
            rotate.CenterY = pivot.Y;
        }

        private void SyncModelFromShape(Shape shape, RoiModel roi)
        {
            double x = Canvas.GetLeft(shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(shape); if (double.IsNaN(y)) y = 0;
            double w = shape.Width; if (double.IsNaN(w)) w = 0;
            double h = shape.Height; if (double.IsNaN(h)) h = 0;

            if (shape is Rectangle)
            {
                roi.Shape = RoiShape.Rectangle;
                roi.Width = w;
                roi.Height = h;
                roi.Left = x;
                roi.Top = y;
                roi.CX = roi.X;
                roi.CY = roi.Y;
                roi.R = Math.Max(roi.Width, roi.Height) / 2.0;
                roi.RInner = 0.0;
            }
            else if (shape is AnnulusShape annulus)
            {
                double radiusX = w / 2.0;
                double radiusY = h / 2.0;
                double centerX = x + radiusX;
                double centerY = y + radiusY;
                double radius = Math.Max(radiusX, radiusY);

                roi.Shape = RoiShape.Annulus;
                roi.Width = w;
                roi.Height = h;
                roi.Left = x;
                roi.Top = y;
                roi.CX = centerX;
                roi.CY = centerY;
                roi.R = radius;

                double inner = annulus.InnerRadius;
                double maxInner = radius > 0 ? radius : Math.Max(radiusX, radiusY);
                inner = Math.Max(0, Math.Min(inner, maxInner));
                roi.RInner = inner;
                annulus.InnerRadius = inner;
            }
            else if (shape is Ellipse)
            {
                double radiusX = w / 2.0;
                double radiusY = h / 2.0;
                double centerX = x + radiusX;
                double centerY = y + radiusY;

                roi.Shape = RoiShape.Circle;
                roi.Width = w;
                roi.Height = h;
                roi.Left = x;
                roi.Top = y;
                roi.CX = centerX;
                roi.CY = centerY;
                roi.R = Math.Max(radiusX, radiusY);
                roi.RInner = 0.0;
            }
        }
    }
}
