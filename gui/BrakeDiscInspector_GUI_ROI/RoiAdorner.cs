// ROI/RoiAdorner.cs  (ADORNER DE EDICIÓN / PREVIEW, CON ROTACIÓN BÁSICA)
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
    /// Adorner de edición para el Shape de preview:
    /// - Permite mover (Thumb central transparente).
    /// - Permite redimensionar (Thumbs en esquinas/lados) para Rect y Circle/Annulus.
    /// - Incluye rotación con el thumb NE.
    /// callback: onChanged(shapeUpdated, modelUpdated)
    public class RoiAdorner : Adorner
    {
        private readonly Shape _shape;
        private readonly Action<bool, RoiModel> _onChanged;
        private readonly Action<string> _log;

        // Thumbs
        private readonly Thumb _moveThumb = new Thumb();
        private readonly Thumb[] _corners = new Thumb[4]; // NW, NE (rot), SE, SW
        private readonly Thumb[] _edges = new Thumb[4];   // N, E, S, W
        private readonly Thumb _rotationThumb;

        private bool _isRotating;
        private double _rotationAngleAtDragStart;
        private double _rotationAccumulatedAngle;

        public RoiAdorner(UIElement adornedElement, Action<bool, RoiModel> onChanged, Action<string> log)
            : base(adornedElement)
        {
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

            // Eventos
            _moveThumb.DragDelta += MoveThumb_DragDelta;

            _corners[0].DragDelta += (s, e) => ResizeByCorner(-e.HorizontalChange, -e.VerticalChange, Corner.NW);
            _corners[2].DragDelta += (s, e) => ResizeByCorner(+e.HorizontalChange, +e.VerticalChange, Corner.SE);
            _corners[3].DragDelta += (s, e) => ResizeByCorner(-e.HorizontalChange, +e.VerticalChange, Corner.SW);

            _rotationThumb.DragStarted += RotationThumb_DragStarted;
            _rotationThumb.DragDelta += RotationThumb_DragDelta;
            _rotationThumb.DragCompleted += RotationThumb_DragCompleted;

            _edges[0].DragDelta += (s, e) => ResizeByEdge(0, -e.VerticalChange, Edge.N); // N
            _edges[1].DragDelta += (s, e) => ResizeByEdge(+e.HorizontalChange, 0, Edge.E); // E
            _edges[2].DragDelta += (s, e) => ResizeByEdge(0, +e.VerticalChange, Edge.S); // S
            _edges[3].DragDelta += (s, e) => ResizeByEdge(-e.HorizontalChange, 0, Edge.W); // W

            AddVisualChild(_moveThumb);
            foreach (var t in _corners) AddVisualChild(t);
            foreach (var t in _edges) AddVisualChild(t);
        }

        // === Layout ===
        protected override int VisualChildrenCount => 1 + _corners.Length + _edges.Length;
        protected override Visual GetVisualChild(int index)
        {
            if (index == 0) return _moveThumb;
            if (index >= 1 && index <= 4) return _corners[index - 1];
            return _edges[index - 5];
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
            // NW NE SE SW
            _corners[0].Arrange(new Rect(-r, -r, 2 * r, 2 * r));
            _corners[1].Arrange(new Rect(w - r, -r, 2 * r, 2 * r));
            _corners[2].Arrange(new Rect(w - r, h - r, 2 * r, 2 * r));
            _corners[3].Arrange(new Rect(-r, h - r, 2 * r, 2 * r));

            // N E S W
            _edges[0].Arrange(new Rect(w / 2 - r, -r, 2 * r, 2 * r));
            _edges[1].Arrange(new Rect(w - r, h / 2 - r, 2 * r, 2 * r));
            _edges[2].Arrange(new Rect(w / 2 - r, h - r, 2 * r, 2 * r));
            _edges[3].Arrange(new Rect(-r, h / 2 - r, 2 * r, 2 * r));

            return finalSize;
        }

        // === Interacciones ===

        private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var roi = _shape.Tag as RoiModel;
            if (roi == null) return;

            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;

            double nx = x + e.HorizontalChange;
            double ny = y + e.VerticalChange;

            Canvas.SetLeft(_shape, nx);
            Canvas.SetTop(_shape, ny);

            SyncModelFromShape(_shape, roi);
            InvalidateArrange(); // recoloca thumbs

            _onChanged(true, roi);
        }

        private enum Corner { NW, NE, SE, SW }
        private enum Edge { N, E, S, W }

        private void ResizeByCorner(double dx, double dy, Corner c)
        {
            var roi = _shape.Tag as RoiModel;
            if (roi == null) return;

            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;
            double w = _shape.Width; if (double.IsNaN(w)) w = 0;
            double h = _shape.Height; if (double.IsNaN(h)) h = 0;

            switch (c)
            {
                case Corner.NW: x -= dx; y -= dy; w += dx; h += dy; break;
                case Corner.NE: y -= dy; w += dx; h += dy; break;
                case Corner.SE: w += dx; h += dy; break;
                case Corner.SW: x -= dx; w += dx; h += dy; break;
            }

            // Mínimos 10x10
            if (w < 10) { x += (w - 10); w = 10; }
            if (h < 10) { y += (h - 10); h = 10; }

            Canvas.SetLeft(_shape, x);
            Canvas.SetTop(_shape, y);
            _shape.Width = w;
            _shape.Height = h;

            UpdateRotationCenterIfNeeded();
            SyncModelFromShape(_shape, roi);
            InvalidateArrange();

            _onChanged(true, roi);
        }

        private void ResizeByEdge(double dx, double dy, Edge e)
        {
            var roi = _shape.Tag as RoiModel;
            if (roi == null) return;

            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;
            double w = _shape.Width; if (double.IsNaN(w)) w = 0;
            double h = _shape.Height; if (double.IsNaN(h)) h = 0;

            switch (e)
            {
                case Edge.N: y -= dy; h += dy; break;
                case Edge.E: w += dx; break;
                case Edge.S: h += dy; break;
                case Edge.W: x -= dx; w += dx; break;
            }

            if (w < 10) { x += (w - 10); w = 10; }
            if (h < 10) { y += (h - 10); h = 10; }

            Canvas.SetLeft(_shape, x);
            Canvas.SetTop(_shape, y);
            _shape.Width = w;
            _shape.Height = h;

            UpdateRotationCenterIfNeeded();
            SyncModelFromShape(_shape, roi);
            InvalidateArrange();

            _onChanged(true, roi);
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

            SetNonRotationThumbsEnabled(false);
            _rotationThumb.IsHitTestVisible = true;

            _log($"[adorner] rotate start roi={roi.Id} angle={_rotationAngleAtDragStart:0.##}");
        }

        private void RotationThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isRotating || _shape.Tag is not RoiModel roi)
                return;

            double radius = GetRotationRadius();
            if (radius <= 1e-3)
                radius = 1;

            double angleDeltaDeg = (-e.VerticalChange / radius) * 180.0 / Math.PI;
            _rotationAccumulatedAngle += angleDeltaDeg;

            double newAngle = NormalizeAngle(_rotationAngleAtDragStart + _rotationAccumulatedAngle);
            ApplyRotation(newAngle, roi);

            _onChanged(true, roi);
        }

        private void RotationThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            SetNonRotationThumbsEnabled(true);

            if (!_isRotating)
                return;

            _isRotating = false;
            _rotationAccumulatedAngle = 0;

            if (_shape.Tag is RoiModel roi)
            {
                double finalAngle = NormalizeAngle(GetCurrentAngle());
                ApplyRotation(finalAngle, roi);
                _onChanged(true, roi);

                _log($"[adorner] rotate end roi={roi.Id} angle={finalAngle:0.##}");
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
        }

        private void ApplyRotation(double angleDeg, RoiModel roi)
        {
            var (width, height) = GetShapeSize();
            double centerX = width / 2.0;
            double centerY = height / 2.0;

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

        private double GetRotationRadius()
        {
            var (width, height) = GetShapeSize();
            double centerX = width / 2.0;
            double centerY = height / 2.0;
            double handleX = width;
            double handleY = 0;

            double dx = handleX - centerX;
            double dy = handleY - centerY;

            return Math.Sqrt(dx * dx + dy * dy);
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

        private void UpdateRotationCenterIfNeeded()
        {
            if (_shape.RenderTransform is not RotateTransform rotate)
                return;

            var (width, height) = GetShapeSize();
            rotate.CenterX = width / 2.0;
            rotate.CenterY = height / 2.0;
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
                roi.X = x; roi.Y = y; roi.Width = w; roi.Height = h;
            }
            else if (shape is Ellipse)
            {
                var r = w / 2.0;
                roi.Shape = roi.Shape == RoiShape.Annulus ? RoiShape.Annulus : RoiShape.Circle;
                roi.CX = x + r; roi.CY = y + r; roi.R = r;
                if (roi.Shape == RoiShape.Annulus && (roi.RInner <= 0 || roi.RInner >= roi.R))
                    roi.RInner = roi.R * 0.6;
            }
        }
    }
}
