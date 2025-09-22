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

            double width = Math.Max(1.0, info.Width);
            double height = Math.Max(1.0, info.Height);

            var pivot = new Point2f((float)info.PivotX, (float)info.PivotY);
            // WPF angles are clockwise, while OpenCV expects counter-clockwise angles.
            // Invert the sign so that rotations match the ROI drawn in the UI.
            using var rotMat = Cv2.GetRotationMatrix2D(pivot, -angleDeg, 1.0);
            using var rotated = new Mat();
            Scalar border = source.Channels() == 4 ? new Scalar(0, 0, 0, 0) : Scalar.All(0);
            Cv2.WarpAffine(source, rotated, rotMat, new Size(source.Width, source.Height),
                InterpolationFlags.Linear, BorderTypes.Constant, border);

            int x = (int)Math.Floor(info.Left);
            int y = (int)Math.Floor(info.Top);
            int w = (int)Math.Ceiling(info.Left + width) - x;
            int h = (int)Math.Ceiling(info.Top + height) - y;

            w = Math.Max(w, 1);
            h = Math.Max(h, 1);

            if (rotated.Width <= 0 || rotated.Height <= 0)
                return false;

            x = Math.Clamp(x, 0, rotated.Width - 1);
            y = Math.Clamp(y, 0, rotated.Height - 1);
            w = Math.Clamp(w, 1, rotated.Width - x);
            h = Math.Clamp(h, 1, rotated.Height - y);

            if (w <= 0 || h <= 0)
                return false;

            cropRect = new Rect(x, y, w, h);
            crop.Dispose();
            crop = new Mat(rotated, cropRect).Clone();
            return true;
        }

        public static Mat BuildRoiMask(RoiCropInfo info, Rect cropRect)
        {
            int width = Math.Max(1, cropRect.Width);
            int height = Math.Max(1, cropRect.Height);
            var mask = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.All(0));

            switch (info.Shape)
            {
                case RoiShape.Rectangle:
                    mask.SetTo(Scalar.All(255));
                    break;

                case RoiShape.Circle:
                case RoiShape.Annulus:
                    {
                        mask.SetTo(Scalar.All(0));
                        var center = new Point(width / 2, height / 2);
                        double scaleX = width / Math.Max(info.Width, 1.0);
                        double scaleY = height / Math.Max(info.Height, 1.0);
                        double scale = Math.Min(scaleX, scaleY);
                        double baseOuter = info.Radius > 0 ? info.Radius : Math.Min(info.Width, info.Height) / 2.0;
                        int outerRadius = (int)Math.Round(baseOuter * scale);
                        int maxRadius = Math.Max(1, Math.Min(width, height) / 2);
                        outerRadius = Math.Clamp(Math.Max(outerRadius, 1), 1, maxRadius);

                        if (outerRadius <= 0)
                        {
                            mask.SetTo(Scalar.All(255));
                            break;
                        }

                        Cv2.Circle(mask, center, outerRadius, Scalar.All(255), -1, LineTypes.AntiAlias);

                        if (info.Shape == RoiShape.Annulus)
                        {
                            double baseInner = Math.Min(info.InnerRadius, baseOuter);
                            int innerRadius = (int)Math.Round(baseInner * scale);
                            innerRadius = Math.Clamp(innerRadius, 0, Math.Max(outerRadius - 1, 0));
                            if (innerRadius > 0)
                            {
                                Cv2.Circle(mask, center, innerRadius, Scalar.All(0), -1, LineTypes.AntiAlias);
                            }
                        }
                        break;
                    }
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
