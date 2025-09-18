
using System;
using System.IO;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class PathUtils
    {
        /// <summary>
        /// Normalizes a Windows path so a Linux/WSL backend can open it.
        /// Example: C:\Users\me\img.png -> /home/millylinux/shared/c/Users/me/img.png
        /// If the path already looks Linux-like, it's returned as-is.
        /// Adjust the base prefix to your WSL share if needed.
        /// </summary>
        public static string NormalizeForBackend(string pathWindows)
        {
            if (string.IsNullOrWhiteSpace(pathWindows)) return pathWindows;
            // Already linux-like?
            if (pathWindows.StartsWith("/")) return pathWindows;

            // UNC wsl$ -> strip the \\wsl$\Ubuntu part
            if (pathWindows.StartsWith(@"\\wsl$", StringComparison.OrdinalIgnoreCase))
            {
                var p = pathWindows.Replace(@"\\wsl$\Ubuntu", "", StringComparison.OrdinalIgnoreCase)
                                   .Replace('\\', '/');
                if (!p.StartsWith("/")) p = "/" + p;
                return p;
            }

            // Drive letter
            if (pathWindows.Length > 2 && pathWindows[1] == ':' &&
                (pathWindows[2] == '\\' || pathWindows[2] == '/'))
            {
                var drive = char.ToLowerInvariant(pathWindows[0]);
                var tail = pathWindows.Substring(3).Replace('\\', '/');
                return $"/home/millylinux/shared/{drive}/{tail}";
            }

            return pathWindows.Replace('\\', '/');
        }
    }
}
