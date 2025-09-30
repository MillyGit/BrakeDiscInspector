
using System;
using System.Globalization;
using System.Linq;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class LocalMatcher
    {
        private static void Log(Action<string>? log, string message) => log?.Invoke(message);

        private static Mat PackPoints(Point2f[] pts)
        {
            var mat = new Mat(pts.Length, 1, MatType.CV_32FC2);
            for (int i = 0; i < pts.Length; i++) mat.Set(i, 0, pts[i]);
            return mat;
        }

        private static Point2f[] UnpackPoints(Mat mat)
        {
            var pts = new Point2f[mat.Rows];
            for (int i = 0; i < mat.Rows; i++) pts[i] = mat.Get<Point2f>(i);
            return pts;
        }

        private static int ToScore(double value) => (int)Math.Round(100.0 * Math.Clamp(value, 0.0, 1.0));

        private static Mat ToGray(Mat src)
        {
            if (src.Channels() == 1) return src.Clone();
            var dst = new Mat();
            if (src.Channels() == 3) Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4) Cv2.CvtColor(src, dst, ColorConversionCodes.BGRA2GRAY);
            else dst = src.Clone();
            return dst;
        }

        private static Mat ClaheBoost(Mat gray)
        {
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

        private static (Point2d? center, int score, string? failure, double bestCorr) MatchTemplateRot(
            Mat imageGray, Mat patternGray, int rotRangeDeg, double scaleMin, double scaleMax, Action<string>? log)
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

            string? failure = bestPoint == null ? "sin correlación" : $"maxCorr={Math.Max(best, 0):F4}";
            return (bestPoint, ToScore(best), failure, best);
        }

        private static (Point2d? center, int score, string? failure, int imgKps, int patKps, int goodCount, int inliers, double avgDist) MatchFeatures(
            Mat imageGray, Mat patternGray, Action<string>? log)
        {
            using var orb = ORB.Create(
                2000,   // nFeatures
                1.2f,   // scaleFactor
                8,      // nLevels
                15,     // edgeThreshold
                0,      // firstLevel
                2,      // WTA_K
                ORBScoreType.Harris,
                31,     // patchSize
                10      // fastThreshold
            );

            var imgKp = orb.Detect(imageGray);
            var patKp = orb.Detect(patternGray);

            bool imgSmall = imageGray.Width * imageGray.Height <= 64 * 64;
            bool patSmall = patternGray.Width * patternGray.Height <= 64 * 64;

            if (imgKp.Length < 12 || imgSmall)
            {
                var boosted = ClaheBoost(imageGray);
                imageGray.Dispose();
                imageGray = boosted;
                imgKp = orb.Detect(imageGray);
                Log(log, $"[FEATURE] CLAHE imagen -> kps={imgKp.Length}");
            }
            if (patKp.Length < 12 || patSmall)
            {
                var boosted = ClaheBoost(patternGray);
                patternGray.Dispose();
                patternGray = boosted;
                patKp = orb.Detect(patternGray);
                Log(log, $"[FEATURE] CLAHE patrón -> kps={patKp.Length}");
            }

            using var imgDesc = new Mat();
            using var patDesc = new Mat();
            orb.Compute(imageGray, ref imgKp, imgDesc);
            orb.Compute(patternGray, ref patKp, patDesc);

            if (imgDesc.Empty() || patDesc.Empty() || imgKp.Length < 8 || patKp.Length < 8)
            {
                Log(log, "[FEATURE] sin descriptores suficientes");
                return (null, 0, "sin descriptores suficientes", imgKp.Length, patKp.Length, 0, 0, 256);
            }

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            var knnMatches = matcher.KnnMatch(patDesc, imgDesc, k: 2);

            double[] ratios = new[] { 0.75, 0.80, 0.85, 0.90, 0.95 };
            DMatch[] good = Array.Empty<DMatch>();
            double usedRatio = ratios[0];
            foreach (var r in ratios)
            {
                var cand = knnMatches.Where(m => m.Length == 2 && m[0].Distance < r * m[1].Distance).Select(m => m[0]).ToArray();
                if (cand.Length >= 8) { good = cand; usedRatio = r; break; }
                good = cand;
                usedRatio = r;
            }

            Log(log, $"[FEATURE] kps(pattern,img)=({patKp.Length},{imgKp.Length}) good={good.Length} ratio={usedRatio:F2}");

            if (good.Length < 8)
            {
                return (null, 0, "pocos good matches", imgKp.Length, patKp.Length, good.Length, 0, 256);
            }

            var srcPts = good.Select(m => patKp[m.QueryIdx].Pt).ToArray();
            var dstPts = good.Select(m => imgKp[m.TrainIdx].Pt).ToArray();

            using var srcMat = PackPoints(srcPts);
            using var dstMat = PackPoints(dstPts);
            using var mask = new Mat();
            using var H = Cv2.FindHomography(srcMat, dstMat, HomographyMethods.Ransac, 3.0, mask);
            if (H.Empty())
            {
                Log(log, "[FEATURE] homografía vacía");
                return (null, 0, "homografía vacía", imgKp.Length, patKp.Length, good.Length, 0, 256);
            }

            int inliers = Cv2.CountNonZero(mask);
            double avgDist = good.Average(m => m.Distance);
            int scoreInliers = ToScore((double)inliers / Math.Max(good.Length, 1));
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
            Cv2.PerspectiveTransform(rectMat, rectOut, H);
            var transformed = UnpackPoints(rectOut);
            double cx = transformed.Average(p => p.X);
            double cy = transformed.Average(p => p.Y);

            Log(log, $"[FEATURE] inliers={inliers}/{good.Length} avgDist={avgDist:F1} score={score}");
            return (new Point2d(cx, cy), score, null, imgKp.Length, patKp.Length, good.Length, inliers, avgDist);
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

            // --- DBG: calcular rectángulos reales desde el full image ---
            var searchRect = RectFromRoi(fullImageBgr, searchRoi);
            var patternRect = patternOverride == null ? RectFromRoi(fullImageBgr, patternRoi) : new Rect(0,0,patternOverride.Width, patternOverride.Height);

            // --- DBG: "misma imagen" => comprobar si patternRect cae dentro de searchRect ---
            bool contained = patternOverride == null &&
                             patternRect.X >= searchRect.X &&
                             patternRect.Y >= searchRect.Y &&
                             patternRect.Right <= searchRect.Right &&
                             patternRect.Bottom <= searchRect.Bottom;

            Log(log, $"[DBG] searchRect={searchRect.X},{searchRect.Y},{searchRect.Width},{searchRect.Height} patternRect={patternRect.X},{patternRect.Y},{patternRect.Width},{patternRect.Height} contained={contained}");

            // Grises base SIN CLAHE para diagnósticos
            using var fullGrayBase = ToGray(fullImageBgr);
            using var searchGrayBase = new Mat(fullGrayBase, searchRect);
            Mat? expectedPatch = null;
            Rect? expectedLocalRect = null;
            if (contained)
            {
                expectedLocalRect = new Rect(patternRect.X - searchRect.X, patternRect.Y - searchRect.Y, patternRect.Width, patternRect.Height);
                expectedPatch = new Mat(searchGrayBase, expectedLocalRect.Value);
                using var patFromFull = new Mat(fullGrayBase, patternRect);
                // Métricas de igualdad/correlación en la posición "ground-truth"
                using var diff = new Mat();
                Cv2.Absdiff(patFromFull, expectedPatch, diff);
                Scalar sum = Cv2.Sum(diff);
                double mad = (sum.Val0 + sum.Val1 + sum.Val2 + sum.Val3) / (patternRect.Width * patternRect.Height);
                Log(log, $"[DBG] GT offset=({expectedLocalRect.Value.X},{expectedLocalRect.Value.Y}) MAD={mad:F4}");

                using var resp = new Mat();
                Cv2.MatchTemplate(searchGrayBase, patFromFull, resp, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(resp, out _, out double maxVal, out _, out Point maxLoc);
                Log(log, $"[DBG] TM@0deg scale=1: max={maxVal:F4} loc=({maxLoc.X},{maxLoc.Y}) vs expected=({expectedLocalRect.Value.X},{expectedLocalRect.Value.Y})");
            }

            if (searchRect.Width < 5 || searchRect.Height < 5)
            {
                Log(log, "[INPUT] search ROI demasiado pequeño");
                expectedPatch?.Dispose();
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
                    var pr = patternRect;
                    if (pr.Width < 3 || pr.Height < 3)
                    {
                        Log(log, "[INPUT] patrón demasiado pequeño");
                        return (null, 0);
                    }
                    patternRegion = new Mat(fullImageBgr, pr);
                    patternGray = ToGray(patternRegion);
                }

                if (patternGray == null || patternGray.Empty())
                {
                    Log(log, "[INPUT] patrón vacío/pequeño");
                    return (null, 0);
                }

                Log(log, $"[INPUT] feature={feature} thr={threshold} search={searchRect.Width}x{searchRect.Height} pattern={patternGray.Width}x{patternGray.Height}");

                var mode = (feature ?? "").Trim().ToLowerInvariant();

                // 1) FEATURES
                var feat = MatchFeatures(searchGray, patternGray, log);

                // 2) AUTO: fallback a TM si fallan features
                if (mode == "auto" && (feat.center is null || feat.score < threshold))
                {
                    Log(log, $"[AUTO] fallback TM: causeFeat={feat.failure} kpsImg={feat.imgKps} kpsPat={feat.patKps} good={feat.goodCount} inliers={feat.inliers} avgDist={feat.avgDist:F1}");
                    var tm = MatchTemplateRot(searchGray, patternGray, rotRange, scaleMin, scaleMax, log);

                    if (tm.center is null || tm.score < threshold)
                    {
                        Log(log, $"[RESULT] no-hit scoreFeat={feat.score} scoreTM={tm.score} (<{threshold}) causeFeat={feat.failure} causeTM={tm.failure}");
                        return (null, Math.Max(feat.score, tm.score));
                    }

                    var globalTM = new Point2d(searchRect.X + tm.center.Value.X, searchRect.Y + tm.center.Value.Y);
                    Log(log, $"[RESULT] HIT (TM) center=({globalTM.X.ToString("F1", CultureInfo.InvariantCulture)},{globalTM.Y.ToString("F1", CultureInfo.InvariantCulture)}) score={tm.score} corr={tm.bestCorr:F3}");
                    return (globalTM, tm.score);
                }

                if (feat.center is null || feat.score < threshold)
                {
                    var reason = feat.failure ?? (feat.center is null ? "sin coincidencias" : $"score={feat.score}");
                    Log(log, $"[RESULT] no-hit score={feat.score} (<{threshold}) cause={reason}");
                    return (null, feat.score);
                }

                var global = new Point2d(searchRect.X + feat.center.Value.X, searchRect.Y + feat.center.Value.Y);
                Log(log, $"[RESULT] HIT (FEATURES) center=({global.X.ToString("F1", CultureInfo.InvariantCulture)},{global.Y.ToString("F1", CultureInfo.InvariantCulture)}) score={feat.score} inliers={feat.inliers}/{Math.Max(feat.goodCount,1)}");
                return (global, feat.score);
            }
            finally
            {
                expectedPatch?.Dispose();
                patternGray?.Dispose();
                patternRegion?.Dispose();
            }
        }

        private static Rect RectFromRoi(Mat img, RoiModel roi)
        {
            double left, top, right, bottom;
            if (roi.Shape == RoiShape.Rectangle)
            {
                left = roi.Left; top = roi.Top; right = left + roi.Width; bottom = top + roi.Height;
            }
            else
            {
                left = roi.CX - roi.R; top = roi.CY - roi.R; right = roi.CX + roi.R; bottom = roi.CY + roi.R;
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
