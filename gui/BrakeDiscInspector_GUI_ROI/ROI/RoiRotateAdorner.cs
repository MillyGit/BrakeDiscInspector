// ROI/RoiRotateAdorner.cs
using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BrakeDiscInspector_GUI_ROI
{
    /// Adorner SOLO para rotación (no edita tamaño/posición).
    public class RoiRotateAdorner : Adorner
    {
        private const double HANDLE_RADIUS = 8;
        private readonly Func<Point> getCenter;
        private readonly Action<double> onAngleChanged;
        private bool dragging = false;

        public double AngleDeg { get; private set; }

        public RoiRotateAdorner(UIElement adornedElement, Func<Point> getCenter, Action<double> onAngleChanged, double initialAngle)
            : base(adornedElement)
        {
            this.getCenter = getCenter;
            this.onAngleChanged = onAngleChanged;
            this.AngleDeg = initialAngle;
            this.IsHitTestVisible = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var center = getCenter();
            double r = 40; // distancia del asa
            double rad = AngleDeg * Math.PI / 180.0;
            var handle = new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
            dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), handle, HANDLE_RADIUS, HANDLE_RADIUS);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            if (IsOverHandle(p))
            {
                dragging = true;
                CaptureMouse();
                e.Handled = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                var center = getCenter();
                var p = e.GetPosition(this);
                double angle = Math.Atan2(p.Y - center.Y, p.X - center.X) * 180.0 / Math.PI;
                AngleDeg = angle;                  // tiempo real
                onAngleChanged?.Invoke(AngleDeg);  // callback
                InvalidateVisual();
                e.Handled = true;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }
            base.OnMouseUp(e);
        }

        private bool IsOverHandle(Point p)
        {
            var center = getCenter();
            double r = 40;
            double rad = AngleDeg * Math.PI / 180.0;
            var h = new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
            return (p - h).Length <= HANDLE_RADIUS * 1.5;
        }
    }
}
