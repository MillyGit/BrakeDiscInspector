using BrakeDiscInspector_GUI_ROI;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class RoiExportResult
    {
        public RoiExportResult(byte[] pngBytes, string shapeJson, RoiModel roiImage)
        {
            PngBytes = pngBytes;
            ShapeJson = shapeJson;
            RoiImage = roiImage;
        }

        public byte[] PngBytes { get; }
        public string ShapeJson { get; }
        public RoiModel RoiImage { get; }
    }
}
