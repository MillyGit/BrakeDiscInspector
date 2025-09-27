using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BrakeDiscInspector_GUI_ROI
{
    public class AnnulusShape : Shape
    {
        public static readonly DependencyProperty InnerRadiusProperty = DependencyProperty.Register(
            nameof(InnerRadius),
            typeof(double),
            typeof(AnnulusShape),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double InnerRadius
        {
            get => (double)GetValue(InnerRadiusProperty);
            set => SetValue(InnerRadiusProperty, value);
        }

        protected override Geometry DefiningGeometry
        {
            get
            {
                double width = Width;
                if (double.IsNaN(width) || width <= 0)
                {
                    width = ActualWidth;
                }

                if (double.IsNaN(width) || width <= 0)
                {
                    width = RenderSize.Width;
                }

                double height = Height;
                if (double.IsNaN(height) || height <= 0)
                {
                    height = ActualHeight;
                }

                if (double.IsNaN(height) || height <= 0)
                {
                    height = RenderSize.Height;
                }

                if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
                {
                    return Geometry.Empty;
                }

                double outerRadius = Math.Min(width, height) / 2.0;
                if (outerRadius <= 0)
                    return Geometry.Empty;

                double inner = Math.Max(0.0, Math.Min(InnerRadius, outerRadius));
                var center = new Point(width / 2.0, height / 2.0);

                var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var ctx = geometry.Open())
                {
                    // Outer circumference (clockwise)
                    ctx.BeginFigure(new Point(center.X + outerRadius, center.Y), true, true);
                    ctx.ArcTo(new Point(center.X - outerRadius, center.Y), new Size(outerRadius, outerRadius), 0, false,
                        SweepDirection.Clockwise, true, false);
                    ctx.ArcTo(new Point(center.X + outerRadius, center.Y), new Size(outerRadius, outerRadius), 0, false,
                        SweepDirection.Clockwise, true, false);

                    if (inner > 0)
                    {
                        // Inner circumference (counterclockwise to carve the hole)
                        ctx.BeginFigure(new Point(center.X + inner, center.Y), true, true);
                        ctx.ArcTo(new Point(center.X - inner, center.Y), new Size(inner, inner), 0, false,
                            SweepDirection.Counterclockwise, true, false);
                        ctx.ArcTo(new Point(center.X + inner, center.Y), new Size(inner, inner), 0, false,
                            SweepDirection.Counterclockwise, true, false);
                    }
                }

                geometry.Freeze();
                return geometry;
            }
        }
    }
}
