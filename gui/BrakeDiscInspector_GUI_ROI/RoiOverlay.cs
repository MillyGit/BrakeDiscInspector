using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrakeDiscInspector_GUI_ROI
{
    public class RoiOverlay : FrameworkElement
    {
        // === Exponer la transform actual del overlay ===
        public double Scale => _scale;
        public double OffsetX => _offX;
        public double OffsetY => _offY;

        // Evento para avisar de cambios en la transform (scale/offset)
        public event EventHandler? OverlayTransformChanged;

        public ROI? Roi { get; set; }

        // === Transformación imagen <-> pantalla ===
        private Image? _boundImage;
        private double _scale = 1.0;
        private double _offX = 0.0, _offY = 0.0;
        private int _imgW = 0, _imgH = 0;

        // Vincula este overlay con la Image real (ImgMain)
        public void BindToImage(Image image)
        {
            _boundImage = image;
            InvalidateOverlay();
        }

        // Fuerza recálculo y repintado
        public void InvalidateOverlay()
        {
            RecomputeImageTransform();
            InvalidateVisual();
        }

        // Recalcular scale/offset asumiendo Stretch=Uniform y letterbox centrado
        private void RecomputeImageTransform()
        {
            if (_boundImage?.Source is BitmapSource bmp)
            {
                _imgW = bmp.PixelWidth;
                _imgH = bmp.PixelHeight;
            }
            else
            {
                _imgW = _imgH = 0;
            }

            double sw = ActualWidth;
            double sh = ActualHeight;

            if (_boundImage != null)
            {
                if (_boundImage.ActualWidth > 0) sw = _boundImage.ActualWidth;
                if (_boundImage.ActualHeight > 0) sh = _boundImage.ActualHeight;
            }

            if (_imgW <= 0 || _imgH <= 0 || sw <= 0 || sh <= 0)
            {
                _scale = 1.0;
                _offX = _offY = 0.0;
                // Notificar identidad cuando no hay imagen/tamaño válido
                OverlayTransformChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _scale = Math.Min(sw / _imgW, sh / _imgH);
            _offX = (sw - _imgW * _scale) * 0.5;
            _offY = (sh - _imgH * _scale) * 0.5;

            // Notificar a quien se suscriba (Canvas/Adorner) que cambió la transform
            OverlayTransformChanged?.Invoke(this, EventArgs.Empty);
        }

        // Conversión coordenadas
        public Point ToScreen(double ix, double iy) => new Point(_offX + ix * _scale, _offY + iy * _scale);
        public double ToScreenLen(double ilen) => ilen * _scale;
        public Point ToImage(double sx, double sy)
        {
            if (_scale <= 0) return new Point();
            return new Point((sx - _offX) / _scale, (sy - _offY) / _scale);
        }

        public double ToImageLen(double slen) => _scale > 0 ? slen / _scale : 0.0;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            RecomputeImageTransform();

            var roi = Roi;
            if (roi == null || _scale <= 0 || _imgW <= 0 || _imgH <= 0)
                return;

            double centerImgX = roi.Shape == RoiShape.Rectangle ? roi.X : roi.CX;
            double centerImgY = roi.Shape == RoiShape.Rectangle ? roi.Y : roi.CY;
            var centerScreen = ToScreen(centerImgX, centerImgY);

            var dpi = VisualTreeHelper.GetDpi(this);
            var highlightPen = new Pen(Brushes.DeepSkyBlue, 2.0);

            if (roi.Shape == RoiShape.Annulus)
            {
                double outerImg = roi.R > 0 ? roi.R : Math.Max(roi.Width, roi.Height) / 2.0;
                if (outerImg <= 0)
                {
                    outerImg = Math.Max(roi.Width, roi.Height) / 2.0;
                }

                double innerImg = roi.RInner > 0
                    ? AnnulusDefaults.ClampInnerRadius(roi.RInner, outerImg)
                    : AnnulusDefaults.ResolveInnerRadius(roi.RInner, outerImg);

                double ro = ToScreenLen(outerImg);
                double ri = ToScreenLen(innerImg);

                if (ro <= 0)
                    return;

                dc.DrawEllipse(null, highlightPen, centerScreen, ro, ro);
                if (ri > 0)
                {
                    dc.DrawEllipse(null, highlightPen, centerScreen, ri, ri);
                }

                var dashedPen = new Pen(Brushes.OrangeRed, 1.5)
                {
                    DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
                };
                dc.DrawEllipse(null, dashedPen, centerScreen, ro, ro);

                string label = string.IsNullOrWhiteSpace(roi.Legend) ? "Annulus" : roi.Legend;
                var ft = new FormattedText(
                    label,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    12,
                    Brushes.White,
                    dpi.PixelsPerDip);

                var labelPos = new Point(centerScreen.X - ft.Width / 2.0, centerScreen.Y - ro - ft.Height - 6.0);
                var bgRect = new Rect(labelPos.X - 4.0, labelPos.Y - 2.0, ft.Width + 8.0, ft.Height + 4.0);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, bgRect);
                dc.DrawText(ft, labelPos);
                return;
            }

            double widthImg = roi.Width;
            double heightImg = roi.Height;

            if (roi.Shape == RoiShape.Circle)
            {
                double radiusImg = roi.R > 0 ? roi.R : Math.Max(roi.Width, roi.Height) / 2.0;
                double radiusScreen = ToScreenLen(radiusImg);
                dc.DrawEllipse(null, highlightPen, centerScreen, radiusScreen, radiusScreen);
                DrawLabel(dc, dpi, roi.Legend, centerScreen);
                return;
            }

            if (roi.Shape == RoiShape.Rectangle)
            {
                double widthScreen = ToScreenLen(widthImg);
                double heightScreen = ToScreenLen(heightImg);
                var rect = new Rect(centerScreen.X - widthScreen / 2.0, centerScreen.Y - heightScreen / 2.0, widthScreen, heightScreen);

                var rotate = new RotateTransform(roi.AngleDeg, centerScreen.X, centerScreen.Y);
                dc.PushTransform(rotate);
                dc.DrawRectangle(null, highlightPen, rect);
                dc.Pop();

                DrawLabel(dc, dpi, roi.Legend, new Point(centerScreen.X, rect.Top));
            }
        }

        private static void DrawLabel(DrawingContext dc, DpiScale dpi, string? legend, Point anchor)
        {
            string text = string.IsNullOrWhiteSpace(legend) ? "ROI" : legend;
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.White,
                dpi.PixelsPerDip);

            var labelPos = new Point(anchor.X - ft.Width / 2.0, anchor.Y - ft.Height - 6.0);
            var bgRect = new Rect(labelPos.X - 4.0, labelPos.Y - 2.0, ft.Width + 8.0, ft.Height + 4.0);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, bgRect);
            dc.DrawText(ft, labelPos);
        }
    }
}
