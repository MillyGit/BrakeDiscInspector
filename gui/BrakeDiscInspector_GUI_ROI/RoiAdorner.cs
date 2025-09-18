// ROI/RoiAdorner.cs  (ADORNER DE EDICIÓN / PREVIEW, NO DE ROTACIÓN)
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
    /// - Sin rotación (la rotación es RoiRotateAdorner).
    /// callback: onChanged(shapeUpdated, modelUpdated)
    public class RoiAdorner : Adorner
    {
        private readonly Shape _shape;
        private readonly Action<bool, RoiModel> _onChanged;
        private readonly Action<string> _log;

        // Thumbs
        private readonly Thumb _moveThumb = new Thumb();
        private readonly Thumb[] _corners = new Thumb[4]; // NW, NE, SE, SW
        private readonly Thumb[] _edges = new Thumb[4];   // N, E, S, W

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
                StyleThumb(_corners[i], 8, 8, 8, 8, Cursors.SizeAll);
            }
            for (int i = 0; i < 4; i++)
            {
                _edges[i] = new Thumb();
                StyleThumb(_edges[i], 6, 6, 6, 6, Cursors.SizeAll);
            }

            // Eventos
            _moveThumb.DragDelta += MoveThumb_DragDelta;

            _corners[0].DragDelta += (s, e) => ResizeByCorner(-e.HorizontalChange, -e.VerticalChange, Corner.NW);
            _corners[1].DragDelta += (s, e) => ResizeByCorner(+e.HorizontalChange, -e.VerticalChange, Corner.NE);
            _corners[2].DragDelta += (s, e) => ResizeByCorner(+e.HorizontalChange, +e.VerticalChange, Corner.SE);
            _corners[3].DragDelta += (s, e) => ResizeByCorner(-e.HorizontalChange, +e.VerticalChange, Corner.SW);

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
            // Bounding del Shape en el canvas (Left/Top/Width/Height)
            double x = Canvas.GetLeft(_shape); if (double.IsNaN(x)) x = 0;
            double y = Canvas.GetTop(_shape); if (double.IsNaN(y)) y = 0;
            double w = _shape.Width; if (double.IsNaN(w) || w < 1) w = 1;
            double h = _shape.Height; if (double.IsNaN(h) || h < 1) h = 1;

            // 1) MoveThumb cubre toda el área del ROI (transparente)
            _moveThumb.Arrange(new Rect(x, y, w, h));

            // 2) Corners y edges (posicionados alrededor)
            double r = 6;
            // NW NE SE SW
            _corners[0].Arrange(new Rect(x - r, y - r, 2 * r, 2 * r));
            _corners[1].Arrange(new Rect(x + w - r, y - r, 2 * r, 2 * r));
            _corners[2].Arrange(new Rect(x + w - r, y + h - r, 2 * r, 2 * r));
            _corners[3].Arrange(new Rect(x - r, y + h - r, 2 * r, 2 * r));

            // N E S W
            _edges[0].Arrange(new Rect(x + w / 2 - r, y - r, 2 * r, 2 * r));
            _edges[1].Arrange(new Rect(x + w - r, y + h / 2 - r, 2 * r, 2 * r));
            _edges[2].Arrange(new Rect(x + w / 2 - r, y + h - r, 2 * r, 2 * r));
            _edges[3].Arrange(new Rect(x - r, y + h / 2 - r, 2 * r, 2 * r));

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

            SyncModelFromShape(_shape, roi);
            InvalidateArrange();

            _onChanged(true, roi);
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
