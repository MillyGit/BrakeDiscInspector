using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class DebugRoiExporter
    {
        public static void SaveAll(
            string imagePathWindows,
            MasterLayout layout,
            Func<RoiModel, Rect> roiToRect,
            string outDir
        )
        {
            if (string.IsNullOrWhiteSpace(imagePathWindows)) return;
            if (!File.Exists(imagePathWindows)) return;
            Directory.CreateDirectory(outDir);

            using var baseBmp = new Bitmap(imagePathWindows);
            using var g = Graphics.FromImage(baseBmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

            using var penSearch  = new System.Drawing.Pen(System.Drawing.Color.Lime, 2);
            using var penPattern = new System.Drawing.Pen(System.Drawing.Color.DeepSkyBlue, 2);
            using var penInspect = new System.Drawing.Pen(System.Drawing.Color.OrangeRed, 2);

            void DrawAndCrop(RoiModel roi, System.Drawing.Pen pen, string tag)
            {
                if (roi == null) return;
                Rect r = roiToRect(roi);
                var dr = new System.Drawing.Rectangle((int)r.X, (int)r.Y, Math.Max(1,(int)r.Width), Math.Max(1,(int)r.Height));
                g.DrawRectangle(pen, dr);
                var x = Math.Max(0, dr.X);
                var y = Math.Max(0, dr.Y);
                var w = Math.Max(1, Math.Min(baseBmp.Width  - x, dr.Width));
                var h = Math.Max(1, Math.Min(baseBmp.Height - y, dr.Height));
                if (w > 1 && h > 1)
                {
                    using var crop = baseBmp.Clone(new System.Drawing.Rectangle(x, y, w, h), baseBmp.PixelFormat);
                    crop.Save(Path.Combine(outDir, $"{tag}_crop.png"), ImageFormat.Png);
                }
            }

            DrawAndCrop(layout?.Master1Search,  penSearch,  "m1_search");
            DrawAndCrop(layout?.Master1Pattern, penPattern, "m1_pattern");
            DrawAndCrop(layout?.Master2Search,  penSearch,  "m2_search");
            DrawAndCrop(layout?.Master2Pattern, penPattern, "m2_pattern");
            DrawAndCrop(layout?.Inspection,     penInspect, "inspection");

            baseBmp.Save(Path.Combine(outDir, "00_full_overlay.png"), ImageFormat.Png);
        }
    }
}
