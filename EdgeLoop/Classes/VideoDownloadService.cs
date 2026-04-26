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

        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(4); // Limit to 4 concurrent downloads

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
            if (EdgeLoop.App.Settings?.EnableLocalCaching != true) return null;
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
            if (EdgeLoop.App.Settings?.EnableLocalCaching != true) return null;
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
            if (EdgeLoop.App.Settings?.EnableLocalCaching != true) return null;

            var cachePath = GetCachePath(url);
            if (cachePath == null) return null;

            // Already cached
            if (File.Exists(cachePath))
            {
                Logger.Debug($"[VideoCache] Cache hit: {Path.GetFileName(cachePath)}");
                try { File.SetLastAccessTime(cachePath, DateTime.Now); } catch { }
                return cachePath;
            }

            Logger.Debug($"[VideoCache] Downloading to cache: {url.Substring(0, Math.Min(100, url.Length))}...");
            var tempPath = cachePath + ".downloading";

            await _downloadSemaphore.WaitAsync(cancellationToken);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (headers != null)
                    {
                        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
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
                                        Logger.Warning($"[VideoCache] Aborting download: Content is a manifest.");
                                        return null;
                                    }
                                }

                                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                                totalRead += bytesRead;
                            }
                        }
                    }
                }

                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(tempPath, cachePath);
                return cachePath;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Logger.Warning($"[VideoCache] Download failed: {ex.Message}");
                return null;
            }
            finally
            {
                _downloadSemaphore.Release();
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }
        }

        public async Task<string> DownloadPartialAsync(string url, long maxBytes = 150 * 1024 * 1024, System.Collections.Generic.Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (EdgeLoop.App.Settings?.EnableLocalCaching != true) return null;

            var cachePath = GetCachePath(url);
            if (cachePath == null) return null;
            var partialPath = cachePath + ".partial";

            if (File.Exists(cachePath))
            {
                File.SetLastAccessTime(cachePath, DateTime.Now);
                return cachePath;
            }

            if (File.Exists(partialPath))
            {
                var info = new FileInfo(partialPath);
                if (info.Length >= maxBytes * 0.9)
                {
                    File.SetLastAccessTime(partialPath, DateTime.Now);
                    return partialPath;
                }
            }

            Logger.Debug($"[VideoCache] Partial download started: {url.Substring(0, Math.Min(80, url.Length))}");
            var tempPath = partialPath + ".downloading";

            await _downloadSemaphore.WaitAsync(cancellationToken);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (headers != null)
                    {
                        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maxBytes - 1);

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        {
                            response.EnsureSuccessStatusCode();
                        }

                        var totalBytes = response.Content.Headers.ContentLength ?? maxBytes;
                        var contentType = response.Content.Headers.ContentType?.MediaType;

                        if (contentType != null && (contentType.Contains("mpegurl") || contentType.Contains("dash+xml"))) return null;

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
                                    if (head.StartsWith("#EXTM3U") || head.StartsWith("<?xml")) return null;
                                }

                                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                                totalRead += bytesRead;
                                if (totalRead >= maxBytes) break;
                            }
                        }
                    }
                }

                if (File.Exists(partialPath)) File.Delete(partialPath);
                File.Move(tempPath, partialPath);
                return partialPath;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[VideoCache] Partial failed: {ex.Message}");
                return null;
            }
            finally
            {
                _downloadSemaphore.Release();
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }
        }

        public void CleanupOldFiles(int daysOld = CLEANUP_DAYS)
        {
            try
            {
                var dir = CacheDirectory;
                if (!Directory.Exists(dir)) return;
                var cutoff = DateTime.Now.AddDays(-daysOld);

                foreach (var file in Directory.GetFiles(dir, "*.mp4"))
                {
                    try { if (new FileInfo(file).LastAccessTime < cutoff) File.Delete(file); } catch { }
                }
                foreach (var file in Directory.GetFiles(dir, "*.partial"))
                {
                    try { if (new FileInfo(file).LastAccessTime < cutoff) File.Delete(file); } catch { }
                }
                foreach (var file in Directory.GetFiles(dir, "*.downloading"))
                {
                    try { if (new FileInfo(file).CreationTime < DateTime.Now.AddHours(-1)) File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        public long GetCacheSize()
        {
            try
            {
                var dir = CacheDirectory;
                if (!Directory.Exists(dir)) return 0;
                long size = 0;
                foreach (var file in Directory.GetFiles(dir, "*.mp4")) try { size += new FileInfo(file).Length; } catch { }
                return size;
            }
            catch { return 0; }
        }

        private string ComputeUrlHash(string url)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
                return BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
