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

                double strokeThickness = StrokeThickness;
                double strokeOffset = strokeThickness / 2.0;
                double minDimension = Math.Min(width, height);
                double outerRadius = Math.Max(0.0, minDimension - strokeThickness) / 2.0;
                if (outerRadius <= 0)
                {
                    return Geometry.Empty;
                }

                double horizontalOffset = (width - minDimension) / 2.0 + strokeOffset;
                double verticalOffset = (height - minDimension) / 2.0 + strokeOffset;
                var center = new Point(horizontalOffset + outerRadius, verticalOffset + outerRadius);

                double maxInnerRadius = Math.Max(0.0, outerRadius - strokeOffset);
                double inner = Math.Max(0.0, Math.Min(InnerRadius, maxInnerRadius));

                var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var ctx = geometry.Open())
                {
                    // Outer circumference (clockwise)
                    Point rightOuter = new Point(center.X + outerRadius, center.Y);
                    Point leftOuter = new Point(center.X - outerRadius, center.Y);

                    ctx.BeginFigure(rightOuter, true, true);
                    ctx.ArcTo(leftOuter, new Size(outerRadius, outerRadius), 0, false,
                        SweepDirection.Clockwise, true, false);
                    ctx.ArcTo(rightOuter, new Size(outerRadius, outerRadius), 0, false,
                        SweepDirection.Clockwise, true, false);

                    if (inner > 0)
                    {
                        // Inner circumference (counterclockwise to carve the hole)
                        Point rightInner = new Point(center.X + inner, center.Y);
                        Point leftInner = new Point(center.X - inner, center.Y);

                        ctx.BeginFigure(rightInner, true, true);
                        ctx.ArcTo(leftInner, new Size(inner, inner), 0, false,
                            SweepDirection.Counterclockwise, true, false);
                        ctx.ArcTo(rightInner, new Size(inner, inner), 0, false,
                            SweepDirection.Counterclockwise, true, false);
                    }
                }

                geometry.Freeze();
                return geometry;
            }
        }
    }
}
