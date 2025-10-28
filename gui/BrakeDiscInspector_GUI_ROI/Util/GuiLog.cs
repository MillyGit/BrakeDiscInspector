using System;
using System.IO;

namespace BrakeDiscInspector_GUI_ROI.Util
{
    internal static class GuiLog
    {
        private static readonly object Sync = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector",
            "logs");

        private static readonly string LogFilePath = Path.Combine(LogDirectory, "gui.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception exception)
            => Write("ERROR", $"{message} :: {exception}");

        private static void Write(string level, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line);
                }
            }
            catch
            {
                // Intentionally ignore logging failures
            }
        }
    }
}
