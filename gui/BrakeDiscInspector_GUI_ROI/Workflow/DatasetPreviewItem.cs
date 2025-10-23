using System.Windows.Media.Imaging;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public class DatasetPreviewItem
    {
        public string Path { get; set; } = string.Empty;
        public BitmapSource? Thumbnail { get; set; }
    }
}
