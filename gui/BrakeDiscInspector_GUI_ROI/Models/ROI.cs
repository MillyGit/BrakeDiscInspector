using System;

namespace BrakeDiscInspector_GUI_ROI
{
    public class ROI
    {
        public double X { get; set; }      // centro
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double AngleDeg { get; set; } = 0.0;
        public string Legend { get; set; } = string.Empty;

        public RoiShape Shape { get; set; } = RoiShape.Rectangle;
        public double R { get; set; }
        public double RInner { get; set; }

        public void EnforceMinSize(double minW = 10, double minH = 10)
        {
            if (Width < minW) Width = minW;
            if (Height < minH) Height = minH;

            if (Shape == RoiShape.Circle || Shape == RoiShape.Annulus)
            {
                double diameter = Math.Max(Width, Height);
                if (R > 0)
                {
                    diameter = Math.Max(diameter, R * 2.0);
                }
                if (diameter <= 0)
                {
                    diameter = Math.Max(minW, minH);
                }

                R = diameter / 2.0;
                Width = diameter;
                Height = diameter;

                if (Shape == RoiShape.Annulus)
                {
                    if (RInner < 0) RInner = 0;
                    if (RInner > R) RInner = R;
                }
                else
                {
                    RInner = 0;
                }
            }
            else
            {
                R = 0;
                RInner = 0;
            }
        }
    }
}
