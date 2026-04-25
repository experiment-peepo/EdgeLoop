using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes {
    public interface IVideoUrlExtractor {
        Task<VideoMetadata> ExtractVideoMetadataAsync(string pageUrl, CancellationToken cancellationToken = default);
        Task<string> ExtractVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default, System.IProgress<string> downloadProgress = null);
        Task<string> ExtractVideoTitleAsync(string pageUrl, CancellationToken cancellationToken = default);
        void ClearCache();
    }
}

