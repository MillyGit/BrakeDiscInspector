
using System;
using System.Linq;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class LocalMatcher
    {
        private static void Log(Action<string>? log, string message)
        {
            log?.Invoke(message);
        }

        private static Mat PackPoints(Point2f[] pts)
        {
            var mat = new Mat(pts.Length, 1, MatType.CV_32FC2);
            for (int i = 0; i < pts.Length; i++)
            {
                mat.Set(i, 0, pts[i]);
            }
            return mat;
        }

        private static Point2f[] UnpackPoints(Mat mat)
        {
            var pts = new Point2f[mat.Rows];
            for (int i = 0; i < mat.Rows; i++)
            {
                pts[i] = mat.Get<Point2f>(i);
            }
            return pts;
        }

        private static int ToScore(double value)
        {
            return (int)Math.Round(100.0 * Math.Clamp(value, 0.0, 1.0));
        }

        private static Mat ToGray(Mat src)
        {
            if (src.Channels() == 1) return src.Clone();

            var dst = new Mat();
            if (src.Channels() == 3)
                Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4)
                Cv2.CvtColor(src, dst, ColorConversionCodes.BGRA2GRAY);
            else
                dst = src.Clone();
            return dst;
        }

        /// <summary>
        /// Refuerzo de contraste con CLAHE cuando hay pocos keypoints o el patrón es pequeño.
        /// </summary>
        private static Mat MaybeClahe(Mat gray, int currentKpCount, int kpThreshold = 10)
        {
            bool small = gray.Width * gray.Height <= 64 * 64;
            if (!small && currentKpCount >= kpThreshold) return gray;

            var dst = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
            clahe.Apply(gray, dst);
            return dst;
        }

        private static Mat RotateAndScale(Mat srcGray, double angleDeg, double scale)
        {
            var center = new Point2f(srcGray.Cols / 2f, srcGray.Rows / 2f);
            using var matrix = Cv2.GetRotationMatrix2D(center, angleDeg, scale);
            var dst = new Mat(srcGray.Size(), srcGray.Type());
            Cv2.WarpAffine(srcGray, dst, matrix, srcGray.Size(), InterpolationFlags.Linear, BorderTypes.Reflect101);
            return dst;
        }

        private static (Point2d? center, int score, string? failure) MatchTemplateRot(
            Mat imageGray,
            Mat patternGray,
            int rotRangeDeg,
            double scaleMin,
            double scaleMax,
            Action<string>? log)
        {
            double best = -1.0;
            Point2d? bestPoint = null;

            var minScale = Math.Min(scaleMin, scaleMax);
            var maxScale = Math.Max(scaleMin, scaleMax);
            int steps = 5;
            var scales = Enumerable.Range(0, steps + 1)
                                    .Select(i => minScale + i * (maxScale - minScale) / Math.Max(steps, 1))
                                    .Distinct();

            foreach (var scale in scales)
            {
                for (int angle = -rotRangeDeg; angle <= rotRangeDeg; angle += 2)
                {
                    using var rotated = RotateAndScale(patternGray, angle, scale);
                    if (rotated.Width > imageGray.Width || rotated.Height > imageGray.Height)
                    {
                        Log(log, $"[TM] skip: rotPat({rotated.Width}x{rotated.Height}) > img({imageGray.Width}x{imageGray.Height}) @ang={angle},scale={scale:F3}");
                        continue;
                    }

                    using var response = new Mat();
                    Cv2.MatchTemplate(imageGray, rotated, response, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(response, out _, out double maxVal, out _, out Point maxLoc);
                    Log(log, $"[TM] ang={angle,3} scale={scale:F3} max={maxVal:F4} loc=({maxLoc.X},{maxLoc.Y})");

                    if (maxVal > best)
                    {
                        best = maxVal;
                        bestPoint = new Point2d(maxLoc.X + rotated.Width / 2.0, maxLoc.Y + rotated.Height / 2.0);
                    }
                }
            }

            string? failure = bestPoint == null
                ? "sin correlación"
                : $"maxCorr={Math.Max(best, 0):F4}";

            return (bestPoint, ToScore(best), failure);
        }

        private static (Point2d? center, int score, string? failure) MatchFeatures(
            Mat imageGray,
            Mat patternGray,
            Action<string>? log)
        {
            // Evitamos nombres de argumentos para máxima compatibilidad entre versiones de OpenCvSharp.
            using var orb = ORB.Create(
                2000,   // nFeatures
                1.2f,   // scaleFactor
                8,      // nLevels
                15,     // edgeThreshold
                0,      // firstLevel
                2,      // WTA_K
                ORBScoreType.Harris, // scoreType (usa ORBScoreType.HARRIS_SCORE si tu versión lo requiere)
                31,     // patchSize
                10      // fastThreshold
            );

            var imgKp = orb.Detect(imageGray);
            var patKp = orb.Detect(patternGray);

            if (imgKp.Length < 10)
            {
                using var imgClahe = MaybeClahe(imageGray, imgKp.Length, kpThreshold: 10);
                imgKp = orb.Detect(imgClahe);
                imageGray = imgClahe;
                Log(log, $"[FEATURE] CLAHE aplicado a imagen -> kps={imgKp.Length}");
            }
            if (patKp.Length < 10)
            {
                using var patClahe = MaybeClahe(patternGray, patKp.Length, kpThreshold: 10);
                patKp = orb.Detect(patClahe);
                patternGray = patClahe;
                Log(log, $"[FEATURE] CLAHE aplicado a patrón -> kps={patKp.Length}");
            }

            using var imgDesc = new Mat();
            using var patDesc = new Mat();
            orb.Compute(imageGray, ref imgKp, imgDesc);
            orb.Compute(patternGray, ref patKp, patDesc);

            if (imgDesc.Empty() || patDesc.Empty() || imgKp.Length < 8 || patKp.Length < 8)
            {
                Log(log, "[FEATURE] sin descriptores suficientes");
                return (null, 0, "sin descriptores suficientes");
            }

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            var knnMatches = matcher.KnnMatch(patDesc, imgDesc, k: 2);

            double[] ratios = new[] { 0.75, 0.80, 0.85, 0.90 };
            DMatch[] good = Array.Empty<DMatch>();
            foreach (var r in ratios)
            {
                good = knnMatches
                    .Where(m => m.Length == 2 && m[0].Distance < r * m[1].Distance)
                    .Select(m => m[0])
                    .ToArray();
                if (good.Length >= 8) { Log(log, $"[FEATURE] ratio={r:F2} good={good.Length}"); break; }
            }
            if (good.Length < 8)
            {
                Log(log, "[FEATURE] pocos good matches");
                return (null, 0, "pocos good matches");
            }

            var srcPts = good.Select(match => patKp[match.QueryIdx].Pt).ToArray();
            var dstPts = good.Select(match => imgKp[match.TrainIdx].Pt).ToArray();

            using var srcMat = PackPoints(srcPts);
            using var dstMat = PackPoints(dstPts);
            using var mask = new Mat();
            using var homography = Cv2.FindHomography(srcMat, dstMat, HomographyMethods.Ransac, 3.0, mask);
            if (homography.Empty())
            {
                Log(log, "[FEATURE] homografía vacía");
                return (null, 0, "homografía vacía");
            }

            int inliers = Cv2.CountNonZero(mask);
            int scoreInliers = ToScore((double)inliers / Math.Max(good.Length, 1));
            double avgDist = good.Average(match => match.Distance);
            int scoreDistance = ToScore(1.0 - Math.Clamp(avgDist / 256.0, 0.0, 1.0));
            int score = (int)Math.Round(0.7 * scoreInliers + 0.3 * scoreDistance);

            var rect = new[]
            {
                new Point2f(0, 0),
                new Point2f(patternGray.Cols, 0),
                new Point2f(patternGray.Cols, patternGray.Rows),
                new Point2f(0, patternGray.Rows)
            };

            using var rectMat = PackPoints(rect);
            using var rectOut = new Mat();
            Cv2.PerspectiveTransform(rectMat, rectOut, homography);
            var transformed = UnpackPoints(rectOut);
            double cx = transformed.Average(p => p.X);
            double cy = transformed.Average(p => p.Y);

            Log(log, $"[FEATURE] inliers={inliers}/{good.Length} avgDist={avgDist:F1} score={score}");
            return (new Point2d(cx, cy), score, null);
        }

        public static (Point2d? center, int score) MatchInSearchROI(
            Mat fullImageBgr,
            RoiModel patternRoi,
            RoiModel searchRoi,
            string feature,
            int threshold,
            int rotRange,
            double scaleMin,
            double scaleMax,
            Mat? patternOverride = null,
            Action<string>? log = null)
        {
            if (fullImageBgr == null) throw new ArgumentNullException(nameof(fullImageBgr));
            if (searchRoi == null) throw new ArgumentNullException(nameof(searchRoi));

            var searchRect = RectFromRoi(fullImageBgr, searchRoi);
            if (searchRect.Width < 5 || searchRect.Height < 5)
            {
                Log(log, "[INPUT] search ROI demasiado pequeño");
                return (null, 0);
            }

            using var searchRegion = new Mat(fullImageBgr, searchRect);
            using var searchGray = ToGray(searchRegion);

            Mat? patternRegion = null;
            Mat? patternGray = null;

            try
            {
                if (patternOverride != null)
                {
                    if (patternOverride.Empty() || patternOverride.Width < 3 || patternOverride.Height < 3)
                    {
                        Log(log, "[INPUT] patrón override vacío/pequeño");
                        return (null, 0);
                    }
                    patternGray = ToGray(patternOverride);
                }
                else
                {
                    if (patternRoi == null)
                    {
                        Log(log, "[INPUT] patrón ROI null");
                        return (null, 0);
                    }

                    var patternRect = RectFromRoi(fullImageBgr, patternRoi);
                    if (patternRect.Width < 3 || patternRect.Height < 3)
                    {
                        Log(log, "[INPUT] patrón demasiado pequeño");
                        return (null, 0);
                    }

                    patternRegion = new Mat(fullImageBgr, patternRect);
                    patternGray = ToGray(patternRegion);
                }

                if (patternGray == null || patternGray.Empty())
                {
                    Log(log, "[INPUT] patrón vacío/pequeño");
                    return (null, 0);
                }

                Log(log, $"[INPUT] feature={feature} thr={threshold} search={searchRect.Width}x{searchRect.Height} pattern={patternGray.Width}x{patternGray.Height}");

                string featureMode = feature?.Trim().ToLowerInvariant() ?? string.Empty;
                (Point2d? center, int score, string? cause) result = featureMode switch
                {
                    "tm" or "tm_rot" => MatchTemplateRot(searchGray, patternGray, rotRange, scaleMin, scaleMax, log),
                    _ => MatchFeatures(searchGray, patternGray, log)
                };

                if (result.center is null || result.score < threshold)
                {
                    var reason = result.cause ?? (result.center is null ? "sin coincidencias" : $"score={result.score}");
                    Log(log, $"[RESULT] no-hit score={result.score} (<{threshold}) cause={reason}");
                    return (null, result.score);
                }

                var global = new Point2d(searchRect.X + result.center.Value.X, searchRect.Y + result.center.Value.Y);
                Log(log, $"[RESULT] HIT center=({global.X:F1},{global.Y:F1}) score={result.score}");
                return (global, result.score);
            }
            finally
            {
                patternGray?.Dispose();
                patternRegion?.Dispose();
            }
        }

        private static Rect RectFromRoi(Mat img, RoiModel roi)
        {
            double left, top, right, bottom;
            if (roi.Shape == RoiShape.Rectangle)
            {
                left = roi.Left;
                top = roi.Top;
                right = left + roi.Width;
                bottom = top + roi.Height;
            }
            else
            {
                left = roi.CX - roi.R;
                top = roi.CY - roi.R;
                right = roi.CX + roi.R;
                bottom = roi.CY + roi.R;
            }

            int x = (int)Math.Floor(left);
            int y = (int)Math.Floor(top);
            int w = (int)Math.Ceiling(right - x);
            int h = (int)Math.Ceiling(bottom - y);

            int maxWidth = Math.Max(img.Width - 1, 0);
            int maxHeight = Math.Max(img.Height - 1, 0);

            x = Math.Clamp(x, 0, maxWidth);
            y = Math.Clamp(y, 0, maxHeight);
            w = Math.Clamp(w, 1, Math.Max(img.Width - x, 1));
            h = Math.Clamp(h, 1, Math.Max(img.Height - y, 1));

            return new Rect(x, y, w, h);
        }
    }
}
