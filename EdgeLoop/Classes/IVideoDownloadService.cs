using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    public interface IVideoDownloadService
    {
        /// <summary>
        /// Where the cached copy for <paramref name="cacheKey"/> lives. The key is the video's page URL
        /// when it has one: signed media links change every time they're looked up, so naming the copy
        /// after them would download the same video again each time.
        /// </summary>
        string GetCachePath(string cacheKey);
        bool IsCached(string cacheKey);
        string GetCachedFilePath(string cacheKey);
        /// <summary>Downloads <paramref name="url"/> to the cache, under <paramref name="cacheKey"/> (default: the URL itself).</summary>
        Task<string> DownloadVideoAsync(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default, string cacheKey = null);
        Task<string> DownloadPartialAsync(string url, long maxBytes = 150 * 1024 * 1024, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default, string cacheKey = null);
        void CleanupOldFiles(int daysOld = 10);
        /// <summary>Space EdgeLoop's own downloads take up, in bytes.</summary>
        long GetCacheSize();
        /// <summary>Deletes the least recently used downloads until they fit the size limit. Returns the bytes freed.</summary>
        long TrimToLimit();
        /// <summary>Deletes every download that isn't in use.</summary>
        (long FreedBytes, int KeptFiles) ClearAll();
    }
}

