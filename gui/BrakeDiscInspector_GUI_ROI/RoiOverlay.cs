﻿using System.Windows;
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

            double cx = Roi.X * sx;
            double cy = Roi.Y * sy;
            double w = Roi.Width * sx;
            double h = Roi.Height * sy;

            var rect = new System.Windows.Rect(cx - w / 2, cy - h / 2, w, h);
            var rotate = new RotateTransform(Roi.AngleDeg, cx, cy);

            dc.PushTransform(rotate);
            dc.DrawRectangle(null, new Pen(Brushes.Lime, 2), rect);

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
