using System;
using System.Windows;
using System.Windows.Media;

namespace BrakeDiscInspector_GUI_ROI
{
    public class RoiOverlay : FrameworkElement
    {
        public ROI Roi { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            if (Roi == null) return;

            Roi.EnforceMinSize(10, 10);

            // Mapear píxeles de imagen → coords del CanvasROI (mismo que usa el adorner)
            var mw = Window.GetWindow(this) as MainWindow;
            if (mw == null) return;

            if (mw.ImgMain.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return;

            double canvasWidth = mw.CanvasROI?.ActualWidth ?? 0;
            double canvasHeight = mw.CanvasROI?.ActualHeight ?? 0;

            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                canvasWidth = this.ActualWidth;
                canvasHeight = this.ActualHeight;
            }

            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            double sx = canvasWidth / bmp.PixelWidth;
            double sy = canvasHeight / bmp.PixelHeight;

            double centerImageX = Roi.Shape == RoiShape.Rectangle ? Roi.X : Roi.CX;
            double centerImageY = Roi.Shape == RoiShape.Rectangle ? Roi.Y : Roi.CY;

            double widthPx = Roi.Width;
            double heightPx = Roi.Height;

            if (Roi.Shape == RoiShape.Circle || Roi.Shape == RoiShape.Annulus)
            {
                double radius = Roi.R > 0 ? Roi.R : Math.Max(Roi.Width, Roi.Height) / 2.0;
                double diameter = radius * 2.0;
                widthPx = diameter;
                heightPx = diameter;
            }

            double cx = centerImageX * sx;
            double cy = centerImageY * sy;
            double w = widthPx * sx;
            double h = heightPx * sy;

            var rect = new System.Windows.Rect(cx - w / 2, cy - h / 2, w, h);
            var rotate = new RotateTransform(Roi.AngleDeg, cx, cy);
            var pen = new Pen(Brushes.Lime, 2);

            dc.PushTransform(rotate);

            switch (Roi.Shape)
            {
                case RoiShape.Rectangle:
                    dc.DrawRectangle(null, pen, rect);
                    break;
                case RoiShape.Circle:
                    dc.DrawEllipse(null, pen, new System.Windows.Point(cx, cy), w / 2.0, h / 2.0);
                    break;
                case RoiShape.Annulus:
                    {
                        double outerRadius = Roi.R > 0 ? Roi.R : Math.Max(Roi.Width, Roi.Height) / 2.0;
                        if (outerRadius <= 0)
                            outerRadius = Math.Max(Roi.Width, Roi.Height) / 2.0;

                        double outerRadiusX = outerRadius * sx;
                        double outerRadiusY = outerRadius * sy;

                        double innerCandidate = Roi.RInner;
                        double innerRadius = innerCandidate > 0
                            ? AnnulusDefaults.ClampInnerRadius(innerCandidate, outerRadius)
                            : AnnulusDefaults.ResolveInnerRadius(innerCandidate, outerRadius);

                        double innerRadiusX = innerRadius * sx;
                        double innerRadiusY = innerRadius * sy;

                        var center = new System.Windows.Point(cx, cy);
                        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                        using (var ctx = geometry.Open())
                        {
                            ctx.BeginFigure(new System.Windows.Point(center.X + outerRadiusX, center.Y), false, false);
                            ctx.ArcTo(new System.Windows.Point(center.X - outerRadiusX, center.Y), new Size(outerRadiusX, outerRadiusY), 0,
                                false, SweepDirection.Clockwise, true, false);
                            ctx.ArcTo(new System.Windows.Point(center.X + outerRadiusX, center.Y), new Size(outerRadiusX, outerRadiusY), 0,
                                false, SweepDirection.Clockwise, true, false);

                            if (innerRadius > 0)
                            {
                                ctx.BeginFigure(new System.Windows.Point(center.X + innerRadiusX, center.Y), false, false);
                                ctx.ArcTo(new System.Windows.Point(center.X - innerRadiusX, center.Y), new Size(innerRadiusX, innerRadiusY), 0,
                                    false, SweepDirection.Counterclockwise, true, false);
                                ctx.ArcTo(new System.Windows.Point(center.X + innerRadiusX, center.Y), new Size(innerRadiusX, innerRadiusY), 0,
                                    false, SweepDirection.Counterclockwise, true, false);
                            }
                        }

                        geometry.Freeze();
                        dc.DrawGeometry(null, pen, geometry);
                        break;
                    }
                default:
                    dc.DrawRectangle(null, pen, rect);
                    break;
            }

            var dpi = VisualTreeHelper.GetDpi(this);

            var ft = new FormattedText(
                Roi.Legend,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.Lime,
                dpi.PixelsPerDip);

            dc.DrawText(ft, new System.Windows.Point(rect.X, rect.Y - 16));
            dc.Pop();
        }


    }
}
