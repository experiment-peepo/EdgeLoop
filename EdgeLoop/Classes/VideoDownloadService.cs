using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Service for downloading and caching videos to disk for instant playback.
    /// Videos are cached in %LOCALAPPDATA%\EdgeLoop\VideoCache and auto-cleaned after 10 days.
    /// </summary>
    public class VideoDownloadService : IVideoDownloadService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10) // Allow long downloads for 4K content
        };

        private string CacheDirectory
        {
            get
            {
                var dir = EdgeLoop.App.Settings?.LocalCacheDirectory;
                if (string.IsNullOrEmpty(dir))
                {
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeLoop", "VideoCache");
                }
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return dir;
            }
        }

        private const int CLEANUP_DAYS = 10;

        static VideoDownloadService()
        {
            // Set user agent to avoid being blocked
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            );
        }

        public VideoDownloadService()
        {
            // Initialization is now dynamic via the CacheDirectory property
        }

        /// <summary>
        /// Gets the cache path for a URL (without downloading)
        /// </summary>
        public string GetCachePath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (EdgeLoop.App.Settings?.EnableLocalCaching == false) return null;
            var hash = ComputeUrlHash(url);
            return Path.Combine(CacheDirectory, $"{hash}.mp4");
        }

        /// <summary>
        /// Checks if a video is already cached
        /// </summary>
        public bool IsCached(string url)
        {
            var path = GetCachePath(url);
            return path != null && File.Exists(path);
        }

        /// <summary>
        /// Gets the cached file path if it exists (full or partial), otherwise null
        /// </summary>
        public string GetCachedFilePath(string url)
        {
            if (EdgeLoop.App.Settings?.EnableLocalCaching == false) return null;
            var path = GetCachePath(url);
            if (path == null) return null;

            if (File.Exists(path))
            {
                // Validate that the cached file isn't actually a manifest (common error)
                if (FileValidator.IsCorruptedCacheFile(path))
                {
                    Logger.Warning($"[VideoCache] Detected corrupted cache file (manifest): {path}. Deleting.");
                    try { File.Delete(path); } catch { }
                    return null;
                }
                return path;
            }

            // Check for active download (concurrent playback)
            var downloadingPath = path + ".downloading";
            if (File.Exists(downloadingPath)) return downloadingPath;

            // Check for partial cache
            var partialPath = path + ".partial";
            if (File.Exists(partialPath)) return partialPath;

            return null;
        }

        /// <summary>
        /// Downloads a video to the cache directory. Returns the local file path on success.
        /// Highest quality is always downloaded (determined by yt-dlp which provides the URL).
        /// </summary>
        public async Task<string> DownloadVideoAsync(string url, System.Collections.Generic.Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (EdgeLoop.App.Settings?.EnableLocalCaching == false) return null;

            try
            {
                var cachePath = GetCachePath(url);
                if (cachePath == null) return null;

                // Already cached
                if (File.Exists(cachePath))
                {
                    Logger.Debug($"[VideoCache] Cache hit: {Path.GetFileName(cachePath)}");
                    // Update last access time for cleanup tracking
                    File.SetLastAccessTime(cachePath, DateTime.Now);
                    return cachePath;
                }

                Logger.Debug($"[VideoCache] Downloading to cache: {url.Substring(0, Math.Min(100, url.Length))}...");

                // Download to temp file first
                var tempPath = cachePath + ".downloading";

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (headers != null)
                        {
                            foreach (var header in headers)
                            {
                                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }

                        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            response.EnsureSuccessStatusCode();

                            var totalBytes = response.Content.Headers.ContentLength ?? 0;
                            var contentType = response.Content.Headers.ContentType?.MediaType;

                            if (contentType != null && (contentType.Contains("mpegurl") || contentType.Contains("dash+xml")))
                            {
                                Logger.Warning($"[VideoCache] Aborting download: URL resolved to a manifest ({contentType}): {url}");
                                return null;
                            }

                            Logger.Debug($"[VideoCache] Starting download: {totalBytes / (1024 * 1024):F1} MB");

                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
                            {
                                var buffer = new byte[81920]; // 80KB buffer for efficient disk writes
                                long totalRead = 0;
                                int bytesRead;

                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                                {
                                    if (totalRead == 0 && bytesRead >= 7)
                                    {
                                        string head = Encoding.UTF8.GetString(buffer, 0, 7);
                                        if (head.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                                        {
                                            Logger.Warning($"[VideoCache] Aborting download: Content is a manifest (HLS/DASH).");
                                            return null;
                                        }
                                    }

                                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                                    totalRead += bytesRead;
                                }

                                Logger.Debug($"[VideoCache] Download complete: {totalRead / (1024 * 1024):F1} MB");
                            }
                        }
                    }

                    // Move temp to final location
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath); // Remove if somehow exists
                    }
                    File.Move(tempPath, cachePath);

                    Logger.Debug($"[VideoCache] Cached: {Path.GetFileName(cachePath)}");
                    return cachePath;

                }
                finally
                {
                    // Clean up temp file if it exists
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }

            }
            catch (OperationCanceledException)
            {
                Logger.Debug($"[VideoCache] Download cancelled");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[VideoCache] Download failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads the first portion of a video for pre-buffering.
        /// This enables faster startup by caching at least the beginning of the video.
        /// For large files (>150MB), only the first 150MB is downloaded.
        /// </summary>
        /// <param name="url">The video URL to download</param>
        /// <param name="maxBytes">Maximum bytes to download. Default is 150MB (middle of 100-200MB range)</param>
        /// <param name="headers">Optional HTTP headers</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Path to the partial cache file, or null on failure</returns>
        public async Task<string> DownloadPartialAsync(string url, long maxBytes = 150 * 1024 * 1024, System.Collections.Generic.Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (EdgeLoop.App.Settings?.EnableLocalCaching == false) return null;

            try
            {
                var cachePath = GetCachePath(url);
                if (cachePath == null) return null;
                var partialPath = cachePath + ".partial";

                // If full file exists, use it
                if (File.Exists(cachePath))
                {
                    Logger.Debug($"[VideoCache] Full cache hit: {Path.GetFileName(cachePath)}");
                    File.SetLastAccessTime(cachePath, DateTime.Now);
                    return cachePath;
                }

                // If partial file exists and is large enough, use it
                if (File.Exists(partialPath))
                {
                    var existingInfo = new FileInfo(partialPath);
                    if (existingInfo.Length >= maxBytes * 0.9)
                    { // Allow 10% tolerance
                        Logger.Debug($"[VideoCache] Partial cache hit: {existingInfo.Length / (1024 * 1024):F1} MB");
                        File.SetLastAccessTime(partialPath, DateTime.Now);
                        return partialPath;
                    }
                }

                Logger.Debug($"[VideoCache] Starting partial download (max {maxBytes / (1024 * 1024)}MB): {url.Substring(0, Math.Min(80, url.Length))}...");

                var tempPath = partialPath + ".downloading";

                try
                {
                    // Create request with Range header for partial download
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (headers != null)
                        {
                            foreach (var header in headers)
                            {
                                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maxBytes - 1);

                        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            // Accept both 200 (full content) and 206 (partial content)
                            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            {
                                response.EnsureSuccessStatusCode(); // Will throw with proper message
                            }

                            var totalBytes = response.Content.Headers.ContentLength ?? maxBytes;
                            var contentType = response.Content.Headers.ContentType?.MediaType;

                            if (contentType != null && (contentType.Contains("mpegurl") || contentType.Contains("dash+xml")))
                            {
                                Logger.Warning($"[VideoCache] Aborting partial: Resolved to manifest ({contentType})");
                                return null;
                            }

                            var isFullDownload = response.StatusCode == System.Net.HttpStatusCode.OK;

                            Logger.Debug($"[VideoCache] Partial download started: {totalBytes / (1024 * 1024):F1} MB (full: {isFullDownload})");

                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
                            {
                                var buffer = new byte[81920];
                                long totalRead = 0;
                                int bytesRead;

                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                                {
                                    if (totalRead == 0 && bytesRead >= 7)
                                    {
                                        string head = Encoding.UTF8.GetString(buffer, 0, 7);
                                        if (head.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                                        {
                                            Logger.Warning($"[VideoCache] Aborting partial: Content is a manifest.");
                                            return null;
                                        }
                                    }

                                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                                    totalRead += bytesRead;

                                    // Stop if we've reached our limit (server might ignore Range header)
                                    if (totalRead >= maxBytes)
                                    {
                                        Logger.Debug($"[VideoCache] Partial download limit reached: {totalRead / (1024 * 1024):F1} MB");
                                        break;
                                    }
                                }

                                Logger.Debug($"[VideoCache] Partial download complete: {totalRead / (1024 * 1024):F1} MB");
                            }
                        }
                    }

                    // Move temp to final location
                    var targetPath = partialPath;
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    File.Move(tempPath, targetPath);

                    Logger.Debug($"[VideoCache] Partial cached: {Path.GetFileName(targetPath)}");
                    return targetPath;

                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }

            }
            catch (OperationCanceledException)
            {
                Logger.Debug($"[VideoCache] Partial download cancelled");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[VideoCache] Partial download failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cleans up cached files older than the specified number of days
        /// </summary>
        public void CleanupOldFiles(int daysOld = CLEANUP_DAYS)
        {
            try
            {
                var dir = CacheDirectory;
                if (!Directory.Exists(dir)) return;

                var cutoffDate = DateTime.Now.AddDays(-daysOld);
                var deletedCount = 0;
                long freedBytes = 0;

                foreach (var file in Directory.GetFiles(dir, "*.mp4"))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        // Use last access time for cleanup (gets updated on cache hits)
                        if (fileInfo.LastAccessTime < cutoffDate)
                        {
                            freedBytes += fileInfo.Length;
                            File.Delete(file);
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[VideoCache] Failed to delete old file: {ex.Message}");
                    }
                }

                // Also clean up old .partial files
                foreach (var file in Directory.GetFiles(dir, "*.partial"))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastAccessTime < cutoffDate)
                        {
                            freedBytes += fileInfo.Length;
                            File.Delete(file);
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[VideoCache] Failed to delete old partial file: {ex.Message}");
                    }
                }

                // Also clean up any orphaned .downloading files
                foreach (var file in Directory.GetFiles(dir, "*.downloading"))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        // Delete downloading files older than 1 hour (likely abandoned)
                        if (fileInfo.CreationTime < DateTime.Now.AddHours(-1))
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }

                if (deletedCount > 0)
                {
                    Logger.Debug($"[VideoCache] Cleanup: deleted {deletedCount} files, freed {freedBytes / (1024 * 1024):F1} MB");
                }

            }
            catch (Exception ex)
            {
                Logger.Warning($"[VideoCache] Cleanup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the total size of the cache in bytes
        /// </summary>
        public long GetCacheSize()
        {
            try
            {
                var dir = CacheDirectory;
                if (!Directory.Exists(dir)) return 0;

                long totalSize = 0;
                foreach (var file in Directory.GetFiles(dir, "*.mp4"))
                {
                    try
                    {
                        totalSize += new FileInfo(file).Length;
                    }
                    catch { }
                }
                return totalSize;

            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Computes a hash of the URL for cache file naming
        /// </summary>
        private string ComputeUrlHash(string url)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(url);
                var hash = sha256.ComputeHash(bytes);
                // Use first 16 bytes (32 hex chars) for reasonable file name length
                return BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}

