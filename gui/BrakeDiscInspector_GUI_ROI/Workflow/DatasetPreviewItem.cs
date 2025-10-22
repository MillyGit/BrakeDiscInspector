using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class DatasetPreviewItem
    {
        private DatasetPreviewItem(string path, bool isOk, BitmapImage thumbnail)
        {
            Path = path;
            IsOk = isOk;
            Thumbnail = thumbnail;
        }

        public string Path { get; }
        public bool IsOk { get; }
        public BitmapImage Thumbnail { get; }

        public static DatasetPreviewItem? Create(string path, bool isOk)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = 160;
                bitmap.EndInit();
                bitmap.Freeze();
                return new DatasetPreviewItem(path, isOk, bitmap);
            }
            catch
            {
                return null;
            }
        }
    }
}
