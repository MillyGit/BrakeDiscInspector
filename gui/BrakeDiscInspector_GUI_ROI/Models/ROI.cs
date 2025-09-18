namespace BrakeDiscInspector_GUI_ROI
{
    public class ROI
    {
        public double X { get; set; }      // centro
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double AngleDeg { get; set; } = 0.0;
        public string Legend { get; set; } = "M1";

        public void EnforceMinSize(double minW = 10, double minH = 10)
        {
            if (Width < minW) Width = minW;
            if (Height < minH) Height = minH;
        }
    }
}
