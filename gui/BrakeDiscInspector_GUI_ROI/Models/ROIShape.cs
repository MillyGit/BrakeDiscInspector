using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Models
{
    public enum ROIShape { Circle, Square, Annulus }

    public static class ROIShapeExtensions
    {
        public static ROIShape Parse(string? s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "círculo":
                case "circle":
                case "circulo": return ROIShape.Circle;
                case "annulus": return ROIShape.Annulus;
                case "square":
                case "cuadrado":
                default: return ROIShape.Square;
            }
        }

        public static string ToPersistedString(this ROIShape shape) =>
            shape switch
            {
                ROIShape.Circle => "circle",
                ROIShape.Annulus => "annulus",
                _ => "square"
            };
    }
}
