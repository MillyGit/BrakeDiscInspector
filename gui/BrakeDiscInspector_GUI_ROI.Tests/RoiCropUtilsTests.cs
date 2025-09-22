using System;
using BrakeDiscInspector_GUI_ROI;
using OpenCvSharp;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class RoiCropUtilsTests
{
    [Fact]
    public void TryGetRotatedCrop_RespectsClockwiseAngles()
    {
        using var source = new Mat(new Size(160, 160), MatType.CV_8UC1, Scalar.All(0));

        const double left = 60;
        const double top = 40;
        const double width = 40;
        const double height = 30;
        var roiRect = new Rect((int)left, (int)top, (int)width, (int)height);

        // Create an asymmetric pattern inside the ROI so the rotation direction matters.
        Cv2.Rectangle(source, roiRect, Scalar.All(30), -1);
        Cv2.Circle(source, new Point(roiRect.Left + 5, roiRect.Top + 8), 4, Scalar.All(200), -1);
        Cv2.Line(source, new Point(roiRect.Right - 2, roiRect.Top), new Point(roiRect.Right - 2, roiRect.Bottom - 1),
            Scalar.All(120), 2);

        const double angleDeg = 37.0;

        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = left,
            Y = top,
            Width = width,
            Height = height
        };

        Assert.True(RoiCropUtils.TryBuildRoiCropInfo(roi, out var info));
        Assert.Equal(left, info.Left);
        Assert.Equal(top, info.Top);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);

        Assert.True(RoiCropUtils.TryGetRotatedCrop(source, info, angleDeg, out var actualCrop, out var actualRect));
        using var actual = actualCrop;

        var expectedRect = BuildCropRect(info, source.Size());
        Assert.Equal(expectedRect.X, actualRect.X);
        Assert.Equal(expectedRect.Y, actualRect.Y);
        Assert.Equal(expectedRect.Width, actualRect.Width);
        Assert.Equal(expectedRect.Height, actualRect.Height);

        using var expected = BuildExpectedCrop(source, info, angleDeg, expectedRect);
        using var diff = new Mat();
        Cv2.Absdiff(expected, actual, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    private static Rect BuildCropRect(RoiCropInfo info, Size sourceSize)
    {
        int x = (int)Math.Floor(info.Left);
        int y = (int)Math.Floor(info.Top);
        int w = (int)Math.Ceiling(info.Left + Math.Max(info.Width, 1.0)) - x;
        int h = (int)Math.Ceiling(info.Top + Math.Max(info.Height, 1.0)) - y;

        w = Math.Max(w, 1);
        h = Math.Max(h, 1);

        x = Math.Clamp(x, 0, sourceSize.Width - 1);
        y = Math.Clamp(y, 0, sourceSize.Height - 1);
        w = Math.Clamp(w, 1, sourceSize.Width - x);
        h = Math.Clamp(h, 1, sourceSize.Height - y);

        return new Rect(x, y, w, h);
    }

    private static Mat BuildExpectedCrop(Mat source, RoiCropInfo info, double angleDeg, Rect expectedRect)
    {
        var pivot = new Point2f((float)info.PivotX, (float)info.PivotY);
        using var rotation = Cv2.GetRotationMatrix2D(pivot, -angleDeg, 1.0);
        var border = source.Channels() == 4 ? new Scalar(0, 0, 0, 0) : Scalar.All(0);
        using var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, rotation, source.Size(), InterpolationFlags.Linear, BorderTypes.Constant, border);
        return new Mat(rotated, expectedRect).Clone();
    }
}
