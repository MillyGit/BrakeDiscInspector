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
        private readonly Func<Point> getPivot;
        private readonly Func<Point> getHandle;
        private readonly Func<double> getBaselineAngle;
        private readonly Action<double> onAngleChanged;
        private readonly Action<string>? log;
        private bool dragging = false;

        public double AngleDeg { get; private set; }

        public RoiRotateAdorner(
            UIElement adornedElement,
            Func<Point> getPivot,
            Func<Point> getHandle,
            Func<double> getBaselineAngle,
            Action<double> onAngleChanged,
            double initialAngle,
            Action<string>? logger = null)
            : base(adornedElement)
        {
            this.getPivot = getPivot ?? throw new ArgumentNullException(nameof(getPivot));
            this.getHandle = getHandle ?? throw new ArgumentNullException(nameof(getHandle));
            this.getBaselineAngle = getBaselineAngle ?? throw new ArgumentNullException(nameof(getBaselineAngle));
            this.onAngleChanged = onAngleChanged;
            log = logger;
            AngleDeg = initialAngle;
            IsHitTestVisible = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var handle = getHandle();
            dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), handle, HANDLE_RADIUS, HANDLE_RADIUS);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            if (IsOverHandle(p))
            {
                dragging = true;
                CaptureMouse();
                var pivot = getPivot();
                var handle = getHandle();
                log?.Invoke($"[rotate] mouse-down pivot=({pivot.X:0.##},{pivot.Y:0.##}) handle=({handle.X:0.##},{handle.Y:0.##}) pointer=({p.X:0.##},{p.Y:0.##}) angle={AngleDeg:0.##}");
                e.Handled = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                var p = e.GetPosition(this);
                var pivot = getPivot();
                var handle = getHandle();
                double dx = p.X - pivot.X;
                double dy = p.Y - pivot.Y;

                if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
                {
                    base.OnMouseMove(e);
                    return;
                }

                double pointerAngle = Math.Atan2(dy, dx);
                double baseAngle = getBaselineAngle();
                double angleDeg = NormalizeAngle((pointerAngle - baseAngle) * 180.0 / Math.PI);

                double pointerAngleDeg = pointerAngle * 180.0 / Math.PI;
                double baseAngleDeg = baseAngle * 180.0 / Math.PI;
                log?.Invoke($"[rotate] drag pivot=({pivot.X:0.##},{pivot.Y:0.##}) handle=({handle.X:0.##},{handle.Y:0.##}) pointer=({p.X:0.##},{p.Y:0.##}) pointerDeg={pointerAngleDeg:0.##} baseDeg={baseAngleDeg:0.##} resultDeg={angleDeg:0.##}");

                AngleDeg = angleDeg;                  // tiempo real
                onAngleChanged?.Invoke(AngleDeg);      // callback
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
                var pivot = getPivot();
                var handle = getHandle();
                var pointer = e.GetPosition(this);
                log?.Invoke($"[rotate] mouse-up pivot=({pivot.X:0.##},{pivot.Y:0.##}) handle=({handle.X:0.##},{handle.Y:0.##}) pointer=({pointer.X:0.##},{pointer.Y:0.##}) finalDeg={AngleDeg:0.##}");
                e.Handled = true;
            }
            base.OnMouseUp(e);
        }

        private bool IsOverHandle(Point p)
        {
            var handle = getHandle();
            return (p - handle).Length <= HANDLE_RADIUS * 1.5;
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
    }
}
