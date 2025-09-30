using OpenCvSharp;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests
{
    public class LocalMatcherTests
    {
        [Fact]
        public void MatchInSearchROI_AllowsEqualSizedTemplate()
        {
            using var fullImage = new Mat(new Size(80, 80), MatType.CV_8UC3, Scalar.Black);

            // Draw a distinctive pattern inside the ROI so template matching has a clear maximum.
            Cv2.Rectangle(fullImage, new Rect(10, 10, 60, 60), new Scalar(40, 80, 160), -1);
            Cv2.Circle(fullImage, new Point(40, 40), 12, new Scalar(200, 30, 60), -1);

            var patternRoi = new RoiModel
            {
                Shape = RoiShape.Rectangle,
                X = 40,
                Y = 40,
                Width = 60,
                Height = 60
            };

            var searchRoi = patternRoi.Clone();

            var (center, score) = LocalMatcher.MatchInSearchROI(
                fullImage,
                patternRoi,
                searchRoi,
                feature: "tm_rot",
                thr: 0,
                rotRange: 0,
                scaleMin: 1.0,
                scaleMax: 1.0);

            Assert.NotNull(center);
            Assert.InRange(center.Value.X, 39.5, 40.5);
            Assert.InRange(center.Value.Y, 39.5, 40.5);
            Assert.InRange(score, 90, 100);
        }
    }
}
