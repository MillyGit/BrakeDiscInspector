using System;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI
{
    public readonly struct RoiCropInfo
    {
        public RoiCropInfo(RoiShape shape, double left, double top, double width, double height,
            double pivotX, double pivotY, double radius, double innerRadius)
        {
            Shape = shape;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            PivotX = pivotX;
            PivotY = pivotY;
            Radius = radius;
            InnerRadius = innerRadius;
        }

        public RoiShape Shape { get; }
        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
        public double PivotX { get; }
        public double PivotY { get; }
        public double Radius { get; }
        public double InnerRadius { get; }
        public double CenterX => Left + Width / 2.0;
        public double CenterY => Top + Height / 2.0;
    }

    public static class RoiCropUtils
    {
        public static bool TryBuildRoiCropInfo(RoiModel roi, out RoiCropInfo info)
        {
            info = default;

            if (roi == null)
                return false;

            switch (roi.Shape)
            {
                case RoiShape.Rectangle:
                    {
                        double width = Math.Max(1.0, roi.Width);
                        double height = Math.Max(1.0, roi.Height);
                        double left = roi.X;
                        double top = roi.Y;
                        var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roi, width, height);
                        double pivotX = left + pivotLocal.X;
                        double pivotY = top + pivotLocal.Y;
                        info = new RoiCropInfo(roi.Shape, left, top, width, height, pivotX, pivotY, 0, 0);
                        return true;
                    }
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    {
                        double radius = Math.Max(roi.R, 0.5);
                        double width = roi.Width > 0 ? roi.Width : radius * 2.0;
                        double height = roi.Height > 0 ? roi.Height : radius * 2.0;
                        width = Math.Max(width, radius * 2.0);
                        height = Math.Max(height, radius * 2.0);
                        double centerX = roi.CX;
                        double centerY = roi.CY;
                        double left = centerX - width / 2.0;
                        double top = centerY - height / 2.0;
                        var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roi, width, height);
                        double pivotX = left + pivotLocal.X;
                        double pivotY = top + pivotLocal.Y;
                        double inner = Math.Clamp(roi.RInner, 0, radius);
                        info = new RoiCropInfo(roi.Shape, left, top, width, height, pivotX, pivotY, radius, inner);
                        return true;
                    }
                default:
                    return false;
            }
        }

        public static bool TryGetRotatedCrop(Mat source, RoiCropInfo info, double angleDeg,
            out Mat crop, out Rect cropRect)
        {
            crop = new Mat();
            cropRect = default;

            if (source.Empty())
                return false;

            // Dimensiones EXACTAS del ROI en coords de imagen (las que queremos mantener tras el giro)
            int roiW = Math.Max(1, (int)Math.Round(info.Width));
            int roiH = Math.Max(1, (int)Math.Round(info.Height));

            // 1) Girar la imagen alrededor del pivote del ROI.
            // WPF: +angle = horario; OpenCV: +angle = antihorario.
            // Para "deshacer" la rotación visible del ROI (que es horario en WPF),
            // rotamos la IMAGEN en sentido antihorario por +angleDeg (OpenCV).
            var pivot = new Point2f((float)info.PivotX, (float)info.PivotY);
            using var rotMat = Cv2.GetRotationMatrix2D(pivot, +angleDeg, 1.0);
            using var rotated = new Mat();
            Scalar border = source.Channels() == 4 ? new Scalar(0, 0, 0, 0) : Scalar.All(0);
            Cv2.WarpAffine(source, rotated, rotMat, new Size(source.Width, source.Height),
                InterpolationFlags.Linear, BorderTypes.Constant, border);

            // 2) Centro del ROI transformado a la imagen girada
            static Point2f Apply(Mat m, double x, double y)
            {
                float nx = (float)(m.Get<double>(0, 0) * x + m.Get<double>(0, 1) * y + m.Get<double>(0, 2));
                float ny = (float)(m.Get<double>(1, 0) * x + m.Get<double>(1, 1) * y + m.Get<double>(1, 2));
                return new Point2f(nx, ny);
            }
            double cx0 = info.Left + info.Width * 0.5;
            double cy0 = info.Top + info.Height * 0.5;
            var cc = Apply(rotMat, cx0, cy0);

            // 3) Recorte centrado con MISMAS dimensiones que el ROI original (evita inflado de altura)
            int x = (int)Math.Round(cc.X - roiW * 0.5);
            int y = (int)Math.Round(cc.Y - roiH * 0.5);
            int w = roiW;
            int h = roiH;

            // 4) Ajuste a límites de imagen
            if (rotated.Width <= 0 || rotated.Height <= 0) return false;
            x = Math.Clamp(x, 0, rotated.Width - 1);
            y = Math.Clamp(y, 0, rotated.Height - 1);
            w = Math.Clamp(w, 1, rotated.Width - x);
            h = Math.Clamp(h, 1, rotated.Height - y);
            if (w <= 0 || h <= 0) return false;

            cropRect = new Rect(x, y, w, h);
            crop = new Mat(rotated, cropRect).Clone();
            return true;
        }




        public static Mat BuildRoiMask(RoiCropInfo info, Rect cropRect)
        {
            int w = Math.Max(1, cropRect.Width);
            int h = Math.Max(1, cropRect.Height);
            var mask = new Mat(new Size(w, h), MatType.CV_8UC1, Scalar.All(0));

            switch (info.Shape)
            {
                case RoiShape.Rectangle:
                    mask.SetTo(Scalar.All(255));
                    break;

                case RoiShape.Circle:
                case RoiShape.Annulus:
                    {
                        // El recorte ya está alineado; centramos la máscara y escalamos el radio
                        int cx = w / 2;
                        int cy = h / 2;

                        // Radio “base” del ROI en coords de imagen antes de redondeos
                        double baseOuter = info.Radius > 0 ? info.Radius : Math.Min(info.Width, info.Height) / 2.0;

                        // El recorte puede ser algo mayor que el ROI exacto por redondeos → escalamos
                        double scaleX = w / Math.Max(info.Width, 1.0);
                        double scaleY = h / Math.Max(info.Height, 1.0);
                        int rOuter = (int)Math.Round(baseOuter * Math.Min(scaleX, scaleY));
                        rOuter = Math.Clamp(rOuter, 1, Math.Min(w, h) / 2);

                        // Exterior
                        Cv2.Circle(mask, new Point(cx, cy), rOuter, Scalar.All(255), thickness: -1, lineType: LineTypes.AntiAlias);

                        if (info.Shape == RoiShape.Annulus)
                        {
                            int rInner = (int)Math.Round(Math.Clamp(info.InnerRadius, 0, baseOuter) * Math.Min(scaleX, scaleY));
                            rInner = Math.Clamp(rInner, 0, rOuter - 1);
                            // Vaciar el interior
                            Cv2.Circle(mask, new Point(cx, cy), rInner, Scalar.All(0), thickness: -1, lineType: LineTypes.AntiAlias);
                        }
                        break;
                    }

                default:
                    mask.SetTo(Scalar.All(255));
                    break;
            }

            return mask;
        }


        public static Mat ConvertCropToBgra(Mat crop, Mat? alphaMask)
        {
            Mat output;
            if (crop.Channels() == 4)
            {
                output = crop.Clone();
            }
            else if (crop.Channels() == 3)
            {
                output = new Mat();
                Cv2.CvtColor(crop, output, ColorConversionCodes.BGR2BGRA);
            }
            else
            {
                output = new Mat();
                Cv2.CvtColor(crop, output, ColorConversionCodes.GRAY2BGRA);
            }

            if (alphaMask != null)
            {
                using var alphaChannel = output.ExtractChannel(3);
                if (alphaMask.Rows == output.Rows && alphaMask.Cols == output.Cols)
                {
                    alphaMask.CopyTo(alphaChannel);
                }
                else
                {
                    using var resized = new Mat();
                    Cv2.Resize(alphaMask, resized, new Size(output.Cols, output.Rows), 0, 0, InterpolationFlags.Nearest);
                    resized.CopyTo(alphaChannel);
                }
            }

            return output;
        }
    }
}
