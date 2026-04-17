using System.IO;

namespace EdgeLoop.Classes {
    /// <summary>
    /// Helper for safely reading JSON data files with size limits
    /// to prevent OutOfMemoryException from corrupted/malicious files.
    /// </summary>
    public static class SafeFileReader {
        /// <summary>
        /// Maximum size in bytes for data files (50 MB).
        /// This is far larger than any legitimate data file would be.
        /// </summary>
        public const long MaxDataFileSizeBytes = 50 * 1024 * 1024;

        /// <summary>
        /// Reads all text from a file, but only if the file is within the safety size limit.
        /// Returns null if the file exceeds the limit or doesn't exist.
        /// </summary>
        public static string ReadAllTextSafe(string path, long maxSizeBytes = MaxDataFileSizeBytes) {
            try {
                if (!File.Exists(path)) return null;

                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > maxSizeBytes) {
                    Logger.Error($"[SafeFileReader] File exceeds safety limit ({fileInfo.Length / (1024 * 1024):F1} MB > {maxSizeBytes / (1024 * 1024)} MB): {Path.GetFileName(path)}");
                    return null;
                }

                return File.ReadAllText(path);
            } catch (System.Exception ex) {
                Logger.Warning($"[SafeFileReader] Failed to read file {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }
}

