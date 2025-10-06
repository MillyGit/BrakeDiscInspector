using System;

namespace BrakeDiscInspector_GUI_ROI
{
    internal static class AnnulusDefaults
    {
        public const double DefaultInnerRadiusRatio = 0.6;
        public const double MinimumInnerRadius = 10.0; // pixels (=> 20 px diameter)

        public static double ResolveInnerRadius(double requestedInnerRadius, double outerRadius)
        {
            if (outerRadius <= 0 || double.IsNaN(outerRadius))
                return 0;

            if (double.IsNaN(requestedInnerRadius) || requestedInnerRadius <= 0)
            {
                return ClampInnerRadius(outerRadius * DefaultInnerRadiusRatio, outerRadius);
            }

            return ClampInnerRadius(requestedInnerRadius, outerRadius);
        }

        public static double ClampInnerRadius(double innerRadius, double outerRadius)
        {
            if (outerRadius <= 0 || double.IsNaN(outerRadius))
                return 0;

            double inner = innerRadius;
            if (double.IsNaN(inner))
                inner = 0;

            if (inner < 0)
                inner = 0;
            if (inner > outerRadius)
                inner = outerRadius;

            double minAllowed = outerRadius >= MinimumInnerRadius ? MinimumInnerRadius : 0.0;
            if (inner < minAllowed)
                inner = minAllowed;

            return inner;
        }
    }
}
