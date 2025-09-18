// LocalMatcher.cs
using System;
using System.Linq;

// Alias OpenCvSharp: evita choques con System.Windows.Window / WPF
using Cv = OpenCvSharp;
using CvPoint2f = OpenCvSharp.Point2f;
using CvPoint2d = OpenCvSharp.Point2d;
using CvRect = OpenCvSharp.Rect;
using CvMat = OpenCvSharp.Mat;
using CvVec2f = OpenCvSharp.Vec2f;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class LocalMatcher
    {
        // Correlación->0..100
        private static int ToScore(double corr) => (int)Math.Round(Math.Clamp(corr, 0, 1) * 100);

        /// <summary>
        /// Template matching con rotación (escala fija=1.0 por rendimiento).
        /// </summary>
        public static (CvPoint2d? center, int score) MatchTemplateRot(CvMat imageGray, CvMat patternGray, int rotRangeDeg, double scaleMin, double scaleMax)
        {
            double best = -1;
            CvPoint2d? bestPt = null;

            for (int ang = -rotRangeDeg; ang <= rotRangeDeg; ang += 2)
            {
                double scale = 1.0; // si quieres multi-escala, muestrea entre [scaleMin, scaleMax]
                using var rotPat = RotateAndScale(patternGray, ang, scale);
                if (rotPat.Width >= imageGray.Width || rotPat.Height >= imageGray.Height)
                    continue;

                using var res = new CvMat();
                Cv.Cv2.MatchTemplate(imageGray, rotPat, res, Cv.TemplateMatchModes.CCoeffNormed);
                Cv.Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                if (maxVal > best)
                {
                    best = maxVal;
                    bestPt = new CvPoint2d(maxLoc.X + rotPat.Width / 2.0, maxLoc.Y + rotPat.Height / 2.0);
                }
            }
            return (bestPt, ToScore(best));
        }

        /// <summary>
        /// Matching por características: ORB (si piden "sift" o "auto", también usamos ORB para asegurar disponibilidad).
        /// </summary>
        public static (CvPoint2d? center, int score) MatchFeatures(CvMat imageGray, CvMat patternGray, string feature, int rotRangeDeg, double scaleMin, double scaleMax)
        {
            // En tu build SIFT no está disponible; forzamos ORB (robusto y rápido)
            Cv.Feature2D f2d = Cv.ORB.Create(nFeatures: 1000);

            using var bf = new Cv.BFMatcher(normType: Cv.NormTypes.Hamming, crossCheck: true);

            // Detectar + describir
            var kpsImg = f2d.Detect(imageGray);
            using var desImg = new CvMat();
            f2d.Compute(imageGray, ref kpsImg, desImg);

            var kpsPat = f2d.Detect(patternGray);
            using var desPat = new CvMat();
            f2d.Compute(patternGray, ref kpsPat, desPat);

            if (desImg.Empty() || desPat.Empty() || kpsImg.Length == 0 || kpsPat.Length == 0)
                return (null, 0);

            var matches = bf.Match(desPat, desImg)
                            .OrderBy(m => m.Distance)
                            .Take(50)
                            .ToArray();

            if (matches.Length < 6)
                return (null, 0);

            // Puntos emparejados -> Mat CV_32FC2 (Nx1x2)
            var src = matches.Select(m => new CvPoint2f(kpsPat[m.QueryIdx].Pt.X, kpsPat[m.QueryIdx].Pt.Y)).ToArray();
            var dst = matches.Select(m => new CvPoint2f(kpsImg[m.TrainIdx].Pt.X, kpsImg[m.TrainIdx].Pt.Y)).ToArray();

            using var srcMat = PackPoints(src);
            using var dstMat = PackPoints(dst);

            using var H = Cv.Cv2.FindHomography(srcMat, dstMat, Cv.HomographyMethods.Ransac, 3.0, mask: null);
            if (H.Empty())
                return (null, 0);

            // Proyecta el rectángulo del patrón y toma su centro
            var rect = new[]
            {
                new CvPoint2f(0, 0),
                new CvPoint2f(patternGray.Cols, 0),
                new CvPoint2f(patternGray.Cols, patternGray.Rows),
                new CvPoint2f(0, patternGray.Rows)
            };
            using var rectMat = PackPoints(rect);
            using var rectOut = new CvMat();
            Cv.Cv2.PerspectiveTransform(rectMat, rectOut, H);
            var rectTr = UnpackPoints(rectOut);

            var cx = rectTr.Average(p => p.X);
            var cy = rectTr.Average(p => p.Y);

            // Score heurístico: inverso de la distancia media
            double avgDist = matches.Average(m => m.Distance);
            int score = (int)Math.Clamp(100 - avgDist, 0, 100);

            return (new CvPoint2d(cx, cy), score);
        }

        /// <summary>
        /// Empaqueta Point2f[] en Mat Nx1 de tipo CV_32FC2 (compatible con FindHomography/PerspectiveTransform).
        /// </summary>
        private static CvMat PackPoints(CvPoint2f[] pts)
        {
            var mat = new CvMat(pts.Length, 1, Cv.MatType.CV_32FC2);
            for (int i = 0; i < pts.Length; i++)
                mat.Set(i, 0, new CvVec2f(pts[i].X, pts[i].Y));
            return mat;
        }

        /// <summary>
        /// Desempaqueta Mat Nx1 (CV_32FC2) a Point2f[].
        /// </summary>
        private static CvPoint2f[] UnpackPoints(CvMat mat)
        {
            var n = mat.Rows;
            var arr = new CvPoint2f[n];
            for (int i = 0; i < n; i++)
            {
                var v = mat.Get<CvVec2f>(i, 0);
                arr[i] = new CvPoint2f(v.Item0, v.Item1);
            }
            return arr;
        }

        private static CvMat RotateAndScale(CvMat srcGray, double angleDeg, double scale)
        {
            var ctr = new CvPoint2f(srcGray.Cols / 2f, srcGray.Rows / 2f);
            using var M = Cv.Cv2.GetRotationMatrix2D(ctr, angleDeg, scale);
            var bbox = new Cv.RotatedRect(ctr, new OpenCvSharp.Size2f(srcGray.Cols, srcGray.Rows), (float)angleDeg).BoundingRect(); // ángulo float
            var dst = new CvMat();
            Cv.Cv2.WarpAffine(srcGray, dst, M, bbox.Size, Cv.InterpolationFlags.Linear, Cv.BorderTypes.Constant);
            return dst;
        }

        /// <summary>
        /// Ejecuta matching dentro de la ROI de búsqueda y devuelve centro en coords globales.
        /// </summary>
        public static (CvPoint2d? center, int score) MatchInSearchROI(CvMat fullImageBgr, RoiModel patternRoi, RoiModel searchRoi,
            string feature, int thr, int rotRange, double scaleMin, double scaleMax)
        {
            // Recortes
            var searchRect = RectFromRoi(fullImageBgr, searchRoi);
            if (searchRect.Width < 5 || searchRect.Height < 5)
                return (null, 0);

            using var searchBgr = new CvMat(fullImageBgr, searchRect);
            using var imgGray = new CvMat();
            Cv.Cv2.CvtColor(searchBgr, imgGray, Cv.ColorConversionCodes.BGR2GRAY);

            var patRect = RectFromRoi(fullImageBgr, patternRoi);
            using var patBgr = new CvMat(fullImageBgr, patRect);
            using var patGray = new CvMat();
            Cv.Cv2.CvtColor(patBgr, patGray, Cv.ColorConversionCodes.BGR2GRAY);

            (CvPoint2d? center, int score) res =
                string.Equals(feature, "tm_rot", StringComparison.OrdinalIgnoreCase)
                ? MatchTemplateRot(imgGray, patGray, rotRange, scaleMin, scaleMax)
                : MatchFeatures(imgGray, patGray, feature, rotRange, scaleMin, scaleMax);

            if (res.center is null || res.score < thr)
                return (null, res.score);

            // Convertir a coords globales sumando el desplazamiento del recorte searchRect
            var global = new CvPoint2d(res.center.Value.X + searchRect.X, res.center.Value.Y + searchRect.Y);
            return (global, res.score);
        }

        /// <summary>
        /// Convierte RoiModel a OpenCvSharp.Rect con clipping seguro al tamaño de la imagen.
        /// </summary>
        private static CvRect RectFromRoi(CvMat img, RoiModel r)
        {
            if (r.Shape == RoiShape.Rectangle)
            {
                int x = (int)Math.Round(r.X);
                int y = (int)Math.Round(r.Y);
                int w = (int)Math.Round(r.Width);
                int h = (int)Math.Round(r.Height);
                x = Math.Clamp(x, 0, img.Width - 1);
                y = Math.Clamp(y, 0, img.Height - 1);
                w = Math.Clamp(w, 1, img.Width - x);
                h = Math.Clamp(h, 1, img.Height - y);
                return new CvRect(x, y, w, h);
            }
            else
            {
                int rOut = (int)Math.Round(r.R);
                int x = (int)Math.Round(r.CX - rOut);
                int y = (int)Math.Round(r.CY - rOut);
                int w = 2 * rOut;
                int h = 2 * rOut;
                x = Math.Clamp(x, 0, img.Width - 1);
                y = Math.Clamp(y, 0, img.Height - 1);
                w = Math.Clamp(w, 1, img.Width - x);
                h = Math.Clamp(h, 1, img.Height - y);
                return new CvRect(x, y, w, h);
            }
        }
    }
}
