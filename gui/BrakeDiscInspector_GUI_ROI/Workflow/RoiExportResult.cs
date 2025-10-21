using System.Windows;
using BrakeDiscInspector_GUI_ROI;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class RoiExportResult
    {
        public RoiExportResult(byte[] pngBytes, string shapeJson, RoiModel roiImage, string imageHash, string cropHash, Int32Rect cropRect)
        {
            PngBytes = pngBytes;
            ShapeJson = shapeJson;
            RoiImage = roiImage;
            ImageHash = imageHash;
            CropHash = cropHash;
            CropRect = cropRect;
        }

        public byte[] PngBytes { get; }
        public string ShapeJson { get; }
        public RoiModel RoiImage { get; }
        public string ImageHash { get; }
        public string CropHash { get; }
        public Int32Rect CropRect { get; }
    }
}
