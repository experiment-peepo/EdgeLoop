using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Service for downloading and caching videos to disk for instant playback.
    /// Videos are cached in EdgeLoop's own folder (Data\VideoCache) unless the user picks another, and
    /// auto-cleaned 10 days after their last use.
    /// </summary>
    public class VideoDownloadService : IVideoDownloadService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10) // Allow long downloads for 4K content
        };

        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(4); // Limit to 4 concurrent downloads

        // Files EdgeLoop wrote, by name. The folder is the user's choice and may hold their own
        // videos, so anything that deletes files checks these names first (IsOwnCacheFile).
        private const string CacheFilePrefix = "edgeloop-";
        private static readonly Regex OwnCacheFileName = new Regex("^" + CacheFilePrefix + "[0-9a-f]{32}\\.", RegexOptions.CultureInvariant);
        // Before the prefix: a bare hash. Other programs name files that way too, so only
        // the folder EdgeLoop kept them in is trusted to hold them.
        private static readonly Regex OldCacheFileName = new Regex("^[0-9a-f]{32}\\.", RegexOptions.CultureInvariant);

        // A copy used this recently may be playing right now
        private static readonly TimeSpan RecentlyUsed = TimeSpan.FromMinutes(10);

        private string CacheDirectory
        {
            get
            {
                var dir = AppPaths.ResolveVideoCacheDirectory(EdgeLoop.App.Settings?.LocalCacheDirectory);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return dir;
            }
        }

        private const int CLEANUP_DAYS = 10;
        // Moving downloads to another drive must not fill it
        private const long FreeSpaceToLeave = 5L * 1024 * 1024 * 1024;

        static VideoDownloadService()
        {
            // Set user agent to avoid being blocked
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.BrowserUserAgent);
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
            return CacheFilePath(CacheDirectory, ComputeUrlHash(url));
        }

        /// <summary>
        /// Where the copy with this hash lives in a cache folder: the name EdgeLoop gives new
        /// downloads, or the old one (a bare hash) when only that copy exists.
        /// </summary>
        internal static string CacheFilePath(string cacheDirectory, string hash)
        {
            var path = Path.Combine(cacheDirectory, $"{CacheFilePrefix}{hash}.mp4");
            var old = Path.Combine(cacheDirectory, $"{hash}.mp4");
            return !File.Exists(path) && File.Exists(old) ? old : path;
        }

        /// <summary>
        /// Whether EdgeLoop made this file: by its name, or by having the old name in the folder downloads
        /// used to be kept in. Nothing else in the folder is ever deleted: it may be the user's own video.
        /// </summary>
        public static bool IsOwnCacheFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var name = Path.GetFileName(path);
            if (OwnCacheFileName.IsMatch(name)) return true;

            return OldCacheFileName.IsMatch(name)
                && AppPaths.IsSameDirectory(Path.GetDirectoryName(path), AppPaths.ProfileVideoCacheDirectory);
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

                // Used now: the size limit and the 10-day cleanup remove the least recently used first
                try { File.SetLastAccessTime(path, DateTime.Now); } catch { }
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
        public async Task<string> DownloadVideoAsync(string url, System.Collections.Generic.Dictionary<string, string> headers = null, CancellationToken cancellationToken = default, string cacheKey = null)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (EdgeLoop.App.Settings?.EnableLocalCaching != true) return null;

            var cachePath = GetCachePath(string.IsNullOrEmpty(cacheKey) ? url : cacheKey);
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
                TrimToLimit();
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

        public async Task<string> DownloadPartialAsync(string url, long maxBytes = 150 * 1024 * 1024, System.Collections.Generic.Dictionary<string, string> headers = null, CancellationToken cancellationToken = default, string cacheKey = null)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (EdgeLoop.App.Settings?.EnableLocalCaching != true) return null;

            var cachePath = GetCachePath(string.IsNullOrEmpty(cacheKey) ? url : cacheKey);
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
                DeleteUnused(OwnFiles(CacheDirectory), daysOld);
                TrimToLimit();
            }
            catch { }
        }

        private static void DeleteUnused(List<FileInfo> files, int daysOld)
        {
            var cutoff = DateTime.Now.AddDays(-daysOld);
            foreach (var file in files)
            {
                try
                {
                    // Leftovers of a download that never finished go after an hour
                    if (IsUnfinished(file) ? file.CreationTime < DateTime.Now.AddHours(-1) : LastUsed(file) < cutoff)
                        file.Delete();
                }
                catch { }
            }
        }

        /// <summary>
        /// Brings downloads over from the folder in the user's profile, where they were kept before
        /// EdgeLoop's own folder, unless the user picked a folder. Run at startup, off the UI thread:
        /// across drives the files are copied.
        /// </summary>
        public void MoveOldDownloads()
        {
            if (!UsesOwnFolder) return;

            var from = AppPaths.ProfileVideoCacheDirectory;
            var to = CacheDirectory;
            bool sameDrive = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
            MoveDownloads(from, to, sameDrive, IsOwnCacheFile);
        }

        /// <summary>Whether downloads go to EdgeLoop's own folder, not one the user picked.</summary>
        internal bool UsesOwnFolder => AppPaths.IsSameDirectory(CacheDirectory, AppPaths.VideoCacheDirectory);

        internal static void MoveDownloads(string from, string to, bool sameDrive, Func<string, bool> isOwn)
        {
            if (!Directory.Exists(from) || AppPaths.IsSameDirectory(from, to)) return;

            DeleteUnused(OwnFiles(from, isOwn), CLEANUP_DAYS); // Not worth copying
            int moved = 0;

            foreach (var file in OwnFiles(from, isOwn).Where(f => !IsUnfinished(f)))
            {
                try
                {
                    var target = Path.Combine(to, file.Name.StartsWith(CacheFilePrefix, StringComparison.Ordinal) ? file.Name : CacheFilePrefix + file.Name);
                    if (File.Exists(target)) // Downloaded again since
                    {
                        file.Delete();
                        continue;
                    }

                    if (sameDrive)
                    {
                        file.MoveTo(target);
                    }
                    else
                    {
                        // What doesn't fit stays, and goes when it's been unused for 10 days
                        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(to))).AvailableFreeSpace - file.Length < FreeSpaceToLeave) continue;

                        // Under another name until it's complete, so a half-copied file is never played
                        var lastUsed = LastUsed(file);
                        var partial = target + ".move_downloading";
                        File.Copy(file.FullName, partial, overwrite: true);
                        File.SetLastAccessTime(partial, lastUsed);
                        File.Move(partial, target);
                        file.Delete();
                    }
                    moved++;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[VideoCache] Couldn't move {file.Name}: {ex.Message}");
                }
            }

            if (moved > 0) Logger.Info($"[VideoCache] Moved {moved} downloaded videos from {from} to {to}");
            try
            {
                if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
            }
            catch { }
        }

        private static List<FileInfo> OwnFiles(string dir) => OwnFiles(dir, IsOwnCacheFile);

        private static List<FileInfo> OwnFiles(string dir, Func<string, bool> isOwn) =>
            !Directory.Exists(dir) ? new List<FileInfo>()
            : new DirectoryInfo(dir).GetFiles().Where(f => isOwn(f.FullName)).ToList();

        private static bool IsUnfinished(FileInfo file) => file.Name.Contains("downloading", StringComparison.OrdinalIgnoreCase);

        private static DateTime LastUsed(FileInfo file) => file.LastAccessTime > file.LastWriteTime ? file.LastAccessTime : file.LastWriteTime;

        /// <summary>The size limit in Settings, in bytes; 0 means no limit.</summary>
        private static long LimitBytes => Math.Max(0, EdgeLoop.App.Settings?.MaxCacheSizeGb ?? 0) * 1024L * 1024 * 1024;

        private static readonly object _trimLock = new object();

        /// <summary>
        /// Deletes the least recently used copies until the downloads are under the size limit in
        /// Settings. Only EdgeLoop's own files, and never one used in the last few minutes.
        /// </summary>
        /// <returns>Bytes freed.</returns>
        public long TrimToLimit() => TrimToLimit(LimitBytes);

        internal long TrimToLimit(long maxBytes)
        {
            if (maxBytes <= 0) return 0;

            lock (_trimLock)
            {
                long freed = 0;
                try
                {
                    // A file still being downloaded is left to its download
                    var files = OwnFiles(CacheDirectory)
                        .Where(f => !IsUnfinished(f))
                        .OrderBy(LastUsed)
                        .ToList();
                    long total = files.Sum(f => f.Length);
                    var protectedAfter = DateTime.Now - RecentlyUsed;

                    foreach (var file in files)
                    {
                        if (total <= maxBytes) break;
                        if (LastUsed(file) >= protectedAfter) continue; // May be playing

                        try
                        {
                            long length = file.Length;
                            file.Delete();
                            total -= length;
                            freed += length;
                            Logger.Debug($"[VideoCache] Over the size limit, deleted {file.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"[VideoCache] Couldn't delete {file.Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[VideoCache] Trimming the cache failed: {ex.Message}");
                }
                return freed;
            }
        }

        /// <summary>
        /// Deletes every copy EdgeLoop downloaded. Files in use (playing, or still downloading) stay.
        /// </summary>
        public (long FreedBytes, int KeptFiles) ClearAll()
        {
            long freed = 0;
            int kept = 0;
            foreach (var file in OwnFiles(CacheDirectory))
            {
                try
                {
                    long length = file.Length;
                    file.Delete();
                    freed += length;
                }
                catch (Exception ex)
                {
                    kept++;
                    Logger.Debug($"[VideoCache] Couldn't delete {file.Name}: {ex.Message}");
                }
            }
            return (freed, kept);
        }

        public long GetCacheSize()
        {
            try
            {
                return OwnFiles(CacheDirectory).Sum(f => f.Length);
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
