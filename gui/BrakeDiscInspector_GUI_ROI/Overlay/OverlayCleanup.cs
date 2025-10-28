using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BrakeDiscInspector_GUI_ROI.Overlay
{
    internal static class OverlayCleanup
    {
        public static void RemoveForRoi(Canvas overlayCanvas, FrameworkElement shape, string roiId)
        {
            if (overlayCanvas == null || shape == null)
            {
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer != null)
            {
                var adorners = layer.GetAdorners(shape);
                if (adorners != null)
                {
                    foreach (var adorner in adorners)
                    {
                        layer.Remove(adorner);
                    }
                }
            }

            var tag = $"roi:{roiId}";
            var doomed = overlayCanvas.Children
                .OfType<FrameworkElement>()
                .Where(el => Equals(el.Tag, tag))
                .ToList();

            foreach (var element in doomed)
            {
                overlayCanvas.Children.Remove(element);
            }
        }
    }
}
