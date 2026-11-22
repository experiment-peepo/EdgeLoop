using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using HtmlAgilityPack;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Service for extracting direct video URLs from page URLs
    /// </summary>
    public class VideoUrlExtractor : IVideoUrlExtractor
    {
        private readonly IHtmlFetcher _htmlFetcher;
        // private readonly LruCache<string, string> _urlCache; // Replaced by PersistentUrlCache
        private readonly YtDlpService _ytDlpService;
        private readonly SiteSignIn _signIn;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _activeExtractions = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>>();

        public VideoUrlExtractor(IHtmlFetcher htmlFetcher = null, YtDlpService ytDlpService = null, SiteSignIn signIn = null)
        {
            _signIn = signIn ?? SiteSignIn.Shared;
            _htmlFetcher = htmlFetcher ?? new StandardHtmlFetcher();
            _ytDlpService = ytDlpService ?? (ServiceContainer.TryGet<YtDlpService>(out var service) ? service : null);
            // _urlCache = new LruCache<string, string>(Constants.MaxFileCacheSize, ttl);
        }

        public virtual async Task<VideoMetadata> ExtractVideoMetadataAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return new VideoMetadata();

            // Clean URL for Hypnotube/Iwara (strip playlist params that break things)
            pageUrl = SiteRules.NormalizePageUrl(pageUrl);

            var normalizedUrl = FileValidator.NormalizeUrl(pageUrl);
            var uri = new Uri(normalizedUrl);
            var host = uri.Host.ToLowerInvariant();

            if (FileValidator.IsDirectMediaUrl(normalizedUrl))
            {
                return new VideoMetadata(normalizedUrl, Path.GetFileNameWithoutExtension(uri.AbsolutePath), normalizedUrl);
            }

            // Check Persistent Cache
            var cachedUrl = PersistentUrlCache.Instance.Get(pageUrl);
            if (!string.IsNullOrEmpty(cachedUrl))
            {
                // We have the URL, but maybe not the title. 
                // Optimization: If we have a cached URL, return it immediately with a placeholder or cached title if we had it.
                // For now, we accept fetching HTML just for title if needed, OR we could skip title fetch if performance is key.
                // Let's assume title is less critical or we can extract it from the cached URL if possible.
                // But wait, ExtractVideoMetadataAsync is often called when adding a NEW video.
                // If we reload from session, we use ExtractVideoUrlAsync (JIT).
                // So this method is mostly for "Add URL". We *do* want the title then.
                // But if we already cached it, we might skip the heavy lifting of video extraction.
            }

            if (host.Contains("redgifs.com"))
            {
                Logger.Warning($"[VideoUrlExtractor] RedGifs support removed. Skipping: {normalizedUrl}");
                return new VideoMetadata();
            }

            try
            {
                string videoUrl = null;
                string title = null;
                string html = null;
                bool hasHtml = false;

                // Never downloads: this runs for every video in an imported playlist. Sites that need a
                // download (YouTube, Vimeo, ...) are downloaded when they play; here they only give a title.
                bool downloadRequired = YtDlpService.IsDownloadRequiredSite(host);
                bool ytDlpAvailable = _ytDlpService != null && _ytDlpService.IsAvailable;

                // yt-dlp for Iwara (its pages are an app with no video in the HTML) and download-required sites
                bool useYtDlpFirst = ytDlpAvailable && (SiteRules.Is(host, Site.Iwara) || downloadRequired);

                // If we're going to use yt-dlp first (e.g. YouTube), don't scrape with HttpClient first to avoid 429s
                if (!useYtDlpFirst)
                {
                    html = await FetchHtmlAsync(normalizedUrl, cancellationToken);
                    hasHtml = !string.IsNullOrWhiteSpace(html);
                    Logger.Debug($"[VideoUrlExtractor] Fetched HTML for {host}, success: {hasHtml}, length: {html?.Length ?? 0}");
                }

                if (useYtDlpFirst)
                {
                    try
                    {
                        Logger.Debug($"[VideoUrlExtractor] Extracting metadata using yt-dlp for {host}");
                        var info = await _ytDlpService.ExtractVideoInfoAsync(normalizedUrl, cancellationToken);
                        if (info != null)
                        {
                            // A download-required site's streaming link is low quality; it's downloaded when it plays
                            if (!downloadRequired && !string.IsNullOrWhiteSpace(info.Url)) videoUrl = info.Url;
                            // Clean() drops generic titles like "view" often returned by iwara's media server
                            title = TitleSanitizer.Clean(info.Title, host);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[VideoUrlExtractor] yt-dlp metadata extraction failed for {host}: {ex.Message}");
                    }
                }

                // If yt-dlp failed or was skipped, try getting the URL normally
                if (string.IsNullOrEmpty(videoUrl) && !downloadRequired)
                {
                    if (!string.IsNullOrEmpty(cachedUrl))
                    {
                        videoUrl = cachedUrl;
                    }
                    else if (SiteRules.Is(host, Site.Rule34Video))
                    {
                        videoUrl = await ExtractRule34VideoUrlAsync(normalizedUrl, cancellationToken);
                    }
                    else if (SiteRules.Is(host, Site.PmvHaven))
                    {
                        videoUrl = await ExtractPmvHavenUrlAsync(normalizedUrl, cancellationToken);
                    }
                    else if (SiteRules.Is(host, Site.Hypnotube))
                    {
                        videoUrl = await ExtractHypnotubeUrlAsync(normalizedUrl, cancellationToken);
                    }
                    else if (hasHtml)
                    {
                        videoUrl = await ExtractGenericVideoUrlAsync(normalizedUrl, cancellationToken);
                    }
                }

                if (string.IsNullOrEmpty(title))
                {
                    // Fallback 1: Extract from URL slug (No network request)
                    title = ExtractTitleFromUrlSlug(normalizedUrl);

                    // Fallback 2: Fetch HTML (Iwara's pages are an app shell with no title in them)
                    bool canScrape = !SiteRules.Is(host, Site.Iwara);

                    if (string.IsNullOrEmpty(title) && canScrape)
                    {
                        if (!hasHtml)
                        {
                            html = await FetchHtmlAsync(normalizedUrl, cancellationToken);
                            hasHtml = !string.IsNullOrWhiteSpace(html);
                        }
                        if (hasHtml) title = ExtractTitleFromHtml(html, host);
                    }
                }

                if (videoUrl != null)
                {
                    // NEW: Don't cache the temporary streaming URL for sites that require download+mux.
                    // This ensures that when we actually play the video, we trigger the high-quality resolution
                    // rather than reusing a low-quality link cached during metadata extraction.
                    if (!YtDlpService.IsDownloadRequiredSite(host))
                    {
                        PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                    }
                }

                return new VideoMetadata(videoUrl, title, normalizedUrl);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error extracting metadata from {pageUrl}", ex);
                return new VideoMetadata();
            }
        }
        public virtual async Task<string> ExtractVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default, System.IProgress<string> downloadProgress = null)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return null;

            try
            {
                // Coalesce multiple simultaneous requests for the same URL 
                // using a Task-based dictionary.
                Task<string> extractionTask;
                lock (_activeExtractions)
                {
                    if (!_activeExtractions.TryGetValue(pageUrl, out extractionTask) || extractionTask.IsFaulted || extractionTask.IsCanceled)
                    {
                        extractionTask = ExtractVideoUrlInternalAsync(pageUrl, cancellationToken, downloadProgress);
                        _activeExtractions[pageUrl] = extractionTask;

                        // Self-cleanup after completion
                        extractionTask.ContinueWith(t =>
                        {
                            _activeExtractions.TryRemove(pageUrl, out _);
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                return await extractionTask;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> ExtractVideoUrlInternalAsync(string pageUrl, CancellationToken cancellationToken, System.IProgress<string> downloadProgress = null)
        {
            // Re-check cache inside the task just in case it was updated while waiting in GetOrAdd
            var cachedUrl = PersistentUrlCache.Instance.Get(pageUrl);
            if (!string.IsNullOrEmpty(cachedUrl))
            {
                // NEW: For download-required sites (YouTube, Vimeo), only use the cache if it's a local file.
                // If the cache contains a remote URL, it's likely a low-quality/expired link we want to bypass.
                if (YtDlpService.IsDownloadRequiredSite(new Uri(FileValidator.NormalizeUrl(pageUrl)).Host))
                {
                    if (!cachedUrl.StartsWith("http")) return cachedUrl;
                    Logger.Debug($"[VideoUrlExtractor] Ignoring cached streaming URL for download-required site: {pageUrl}");
                }
                else
                {
                    return cachedUrl;
                }
            }

            try
            {
                // Without the site's playlist/tracking parameters (items saved before they were stripped at import)
                var normalizedUrl = FileValidator.NormalizeUrl(SiteRules.NormalizePageUrl(pageUrl));
                var uri = new Uri(normalizedUrl);
                var host = uri.Host.ToLowerInvariant();

                // Already a video file (e.g. an Iwara file link saved by an earlier session): nothing to extract
                if (FileValidator.IsDirectMediaUrl(normalizedUrl))
                {
                    return normalizedUrl;
                }

                string videoUrl = null;

                // NEW: Download+mux for sites with separate streams (YouTube, Vimeo, etc.)
                if (_ytDlpService != null && _ytDlpService.IsAvailable && YtDlpService.IsDownloadRequiredSite(host))
                {
                    try
                    {
                        Logger.Debug($"[VideoUrlExtractor] Download+mux extraction for {host}");
                        videoUrl = await _ytDlpService.DownloadBestQualityAsync(normalizedUrl, cancellationToken, downloadProgress);

                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            SiteRestrictions.Record(normalizedUrl, SiteRestriction.None);
                            PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                            return videoUrl;  // Returns local file path
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[VideoUrlExtractor] Download+mux failed for {host}: {ex.Message}");
                    }

                    // yt-dlp named a reason streaming won't get past (e.g. no JavaScript runtime for YouTube)
                    if (SiteRestrictions.For(normalizedUrl) is var blocked && blocked != SiteRestriction.None)
                    {
                        Logger.Warning($"[VideoUrlExtractor] Not streaming {host} either: {blocked}");
                        return null;
                    }
                    // Fall through to streaming path as fallback
                }

                // Sites the app's own scrapers handle; yt-dlp is only their fallback
                // (it can't do PMVHaven or Hypnotube, and its Rule34Video support is broken as of 2026.10.26)
                var hasSpecializedScraper = SiteRules.Is(host, Site.PmvHaven) ||
                                           SiteRules.Is(host, Site.Hypnotube) ||
                                           SiteRules.Is(host, Site.Rule34Video);

                // Explicitly block RedGifs support as requested
                if (host.Contains("redgifs.com"))
                {
                    Logger.Warning($"[VideoUrlExtractor] RedGifs support has been removed. Skipping: {normalizedUrl}");
                    return null;
                }

                var useYtDlp = !hasSpecializedScraper;

                if (_ytDlpService != null && _ytDlpService.IsAvailable && useYtDlp)
                {
                    try
                    {
                        Logger.Debug($"[VideoUrlExtractor] Attempting yt-dlp extraction for {normalizedUrl}");
                        videoUrl = await _ytDlpService.GetBestVideoUrlAsync(normalizedUrl, cancellationToken);

                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            // Cache success
                            PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                            return videoUrl;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[VideoUrlExtractor] yt-dlp extraction failed: {ex.Message}, falling back to scraping");
                    }
                }
                else if (hasSpecializedScraper)
                {
                    Logger.Debug($"[VideoUrlExtractor] Using specialized scraper for {host}");
                }

                if (SiteRules.Is(host, Site.Hypnotube))
                {
                    videoUrl = await ExtractHypnotubeUrlAsync(normalizedUrl, cancellationToken);
                }
                else if (SiteRules.Is(host, Site.PmvHaven))
                {
                    videoUrl = await ExtractPmvHavenUrlAsync(normalizedUrl, cancellationToken);
                }
                else if (SiteRules.Is(host, Site.Rule34Video))
                {
                    videoUrl = await ExtractRule34VideoUrlAsync(normalizedUrl, cancellationToken);
                }
                else
                {
                    videoUrl = await ExtractGenericVideoUrlAsync(normalizedUrl, cancellationToken);
                }

                if (videoUrl != null)
                {
                    PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                }
                else if (SiteRestrictions.For(normalizedUrl) is var restriction && restriction != SiteRestriction.None)
                {
                    // The page said why there's no video (VIP-only, location-blocked, ...); yt-dlp won't do better
                    Logger.Debug($"[VideoUrlExtractor] Not trying yt-dlp for {host}: {restriction}");
                }
                else if (_ytDlpService != null && _ytDlpService.IsAvailable)
                {
                    // FALLBACK: If specialized scrapers failed, try yt-dlp as a last resort
                    try
                    {
                        Logger.Debug($"[VideoUrlExtractor] Specialized scraper for {host} failed, falling back to yt-dlp");
                        videoUrl = await _ytDlpService.GetBestVideoUrlAsync(normalizedUrl, cancellationToken);
                        if (videoUrl != null)
                        {
                            PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[VideoUrlExtractor] yt-dlp fallback failing for {host}: {ex.Message}");
                    }
                }

                return videoUrl;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error extracting video URL from {pageUrl}", ex);
                return null;
            }
        }

        private async Task<string> ExtractHypnotubeUrlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                // Signed-in accounts get 720p; signs in (again) with the account in Settings when needed
                await _signIn.EnsureSignedInAsync(Site.Hypnotube, cancellationToken);
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return null;

                Logger.Debug($"ExtractHypnotubeUrlAsync: Processing HTML from {url}");

                // VIP-only, friends-only and location-blocked pages say so; there's no video to find
                var restriction = SiteRestrictions.Detect(Site.Hypnotube, html);
                SiteRestrictions.Record(url, restriction);
                if (restriction != SiteRestriction.None)
                {
                    Logger.Warning($"[Hypnotube] {restriction}: {url}");
                    return null;
                }

                // Signed out although the account's session should be signed in: it ran out, so check it next time
                if (_signIn.HasAccount(Site.Hypnotube) && !SiteSignIn.ShowsSignedIn(html))
                {
                    _signIn.NoteSignedOutPage(Site.Hypnotube);
                }

                // 1. Try multi-source extraction (Higher Priority)
                Logger.Debug("Hypnotube: Trying multi-source extraction");
                var sources = StashPatternExtractor.ExtractAllVideoSources(html, url);
                if (sources.Any())
                {
                    var best = QualitySelector.SelectBest(sources);
                    if (best != null)
                    {
                        Logger.Debug($"Hypnotube: Selected quality {best.Label} from {sources.Count} sources");
                        return ResolveUrl(best.Url, url);
                    }
                }

                // 2. Try Open Graph video (fallback if multi-source didn't find anything)
                Logger.Debug("Hypnotube: Trying og:video extraction");
                var ogVideo = StashPatternExtractor.ExtractOgVideo(html);
                if (!string.IsNullOrEmpty(ogVideo))
                {
                    Logger.Debug($"Hypnotube: Found og:video URL");
                    return ResolveUrl(ogVideo, url);
                }

                // 3. Try generic extraction (Method 4 handles JS variables with extensions)
                Logger.Debug("Hypnotube: Trying generic extraction");
                var videoUrl = ExtractVideoFromHtml(html, url);
                if (videoUrl != null) return videoUrl;

                // 4. Stricter site-specific fallback: only match scripts/configs if they hint at a video extension
                var playerPattern = @"(?:video_url|file|src)\s*[:=]\s*[""']([^""']+\.(?:mp4|webm|mkv|m3u8)(?:\?[^""']*)?)[""']";
                var match = Regex.Match(html, playerPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var resolved = ResolveUrl(match.Groups[1].Value, url);
                    if (HasVideoExtension(resolved))
                    {
                        Logger.Debug($"Hypnotube: Found video URL in player config fallback: {resolved}");
                        return resolved;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting Hypnotube URL: {ex.Message}");
                return null;
            }
        }



        private async Task<string> ExtractRule34VideoUrlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                await _signIn.EnsureSignedInAsync(Site.Rule34Video, cancellationToken);
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html))
                {
                    Logger.Warning($"Rule34Video: Failed to fetch HTML for {url}");
                    return null;
                }

                Logger.Debug($"ExtractRule34VideoUrlAsync: Starting specialized extraction for {url} ({html.Length} chars)");

                // 1. Extract tokens (rnd, license_code, etc)
                var tokens = new Dictionary<string, string>();
                var tokenPatterns = new[] {
                    @"\brnd\s*[:=]\s*['""](\d+)['""]",
                    @"\blicense_code\s*[:=]\s*['""]([^'""]+)['""]",
                    @"\bvid_id\s*[:=]\s*['""](\d+)['""]"
                };

                foreach (var pattern in tokenPatterns)
                {
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string key = match.Value.Split(new[] { ':', '=' })[0].Trim();
                        tokens[key] = match.Groups[1].Value;
                        Logger.Debug($"Rule34Video: Found token {key}: {tokens[key]}");
                    }
                }

                // 2. PRIORITY 1: Search for direct download links in the HTML
                // These are often the most reliable as they are what the user sees
                var getFilePattern = @"href=[""']([^""']*\/get_file\/[^""']+\.mp4\/?(?:[^""']*)?)[""']";
                var getFileMatches = Regex.Matches(html, getFilePattern, RegexOptions.IgnoreCase);

                string bestFoundUrl = null;
                int bestFoundQuality = 0;

                foreach (Match linkMatch in getFileMatches)
                {
                    var link = linkMatch.Groups[1].Value;
                    int quality = 360; // Default
                    if (link.Contains("1080p")) quality = 1080;
                    else if (link.Contains("720p")) quality = 720;
                    else if (link.Contains("480p")) quality = 480;
                    else if (link.Contains("2160p") || link.Contains("4k")) quality = 2160;

                    if (quality > bestFoundQuality)
                    {
                        bestFoundQuality = quality;
                        bestFoundUrl = ResolveUrl(link, url);
                    }
                }

                if (bestFoundUrl != null)
                {
                    Logger.Debug($"Rule34Video: Found direct get_file link with {bestFoundQuality}p quality: {bestFoundUrl}");
                    var resolvedUrl = await _htmlFetcher.ResolveRedirectUrlAsync(bestFoundUrl, url, cancellationToken);
                    return resolvedUrl ?? bestFoundUrl;
                }

                // 3. PRIORITY 2: Extract from player variables (Legacy/Fallback)
                var priorities = new[] {
                    ("video_alt_url3", 1080),
                    ("video_alt_url2", 720),
                    ("video_alt_url", 480),
                    ("video_url", 360)
                };

                string bestUrl = null;
                int selectedQuality = 0;

                foreach (var (key, quality) in priorities)
                {
                    var pattern = $@"\b{key}\s*[:=]\s*['""]([^'""]+)['""]";
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var rawUrl = match.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(rawUrl) && rawUrl.Contains("mp4"))
                        {
                            bestUrl = CleanExtractedUrl(rawUrl);
                            selectedQuality = quality;
                            Logger.Debug($"Rule34Video: Found {quality}p quality from {key} variable");
                            break;
                        }
                    }
                }

                if (bestUrl != null)
                {
                    bestUrl = ResolveUrl(bestUrl, url);

                    // Append tokens if not present
                    foreach (var token in tokens)
                    {
                        if (!bestUrl.Contains($"{token.Key}="))
                        {
                            var separator = bestUrl.Contains("?") ? "&" : "?";
                            bestUrl += $"{separator}{token.Key}={token.Value}";
                        }
                    }

                    Logger.Debug($"Rule34Video: Resolving player variable URL: {bestUrl}");
                    var resolvedUrl = await _htmlFetcher.ResolveRedirectUrlAsync(bestUrl, url, cancellationToken);
                    return resolvedUrl ?? bestUrl;
                }

                Logger.Warning("Rule34Video: specialized extraction failed, falling back to generic");
                return await ExtractGenericVideoUrlAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting RULE34Video URL: {ex.Message}");
                return null;
            }
        }

        private async Task<string> ExtractPmvHavenUrlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return null;

                // 1. The video's own HLS stream, from the page's data script
                var stream = FindPmvHavenStream(html);
                if (stream != null)
                {
                    Logger.Debug($"PMVHaven: Found HLS stream: {stream}");
                    return stream;
                }

                // 2. og:video
                var ogVideo = StashPatternExtractor.ExtractOgVideo(html);
                if (!string.IsNullOrEmpty(ogVideo) && !IsPreviewClip(ogVideo))
                {
                    Logger.Debug("PMVHaven: Found og:video URL");
                    return ResolveUrl(CleanExtractedUrl(ogVideo), url);
                }

                // 3. Multi-source extraction with quality selection
                var sources = StashPatternExtractor.ExtractAllVideoSources(html, url)
                    .Where(s => !IsPreviewClip(s.Url))
                    .ToList();
                if (sources.Any())
                {
                    var best = QualitySelector.SelectBest(sources);
                    if (best != null)
                    {
                        Logger.Debug($"PMVHaven: Selected {best.Label} quality from {sources.Count} sources");
                        return ResolveUrl(best.Url, url);
                    }
                }

                // 4. Generic extraction, but never a few-second preview clip from the related-videos list
                var videoUrl = ExtractVideoFromHtml(html, url);
                if (IsPreviewClip(videoUrl))
                {
                    Logger.Warning($"PMVHaven: Only preview clips found on {url}");
                    return null;
                }
                return videoUrl;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting PMVHaven URL: {ex.Message}");
                return null;
            }
        }


        private static readonly Regex HlsLinkPattern = new Regex(@"[""']([^""'\s<>]+\.m3u8(?:\?[^""'\s<>]*)?)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "…/1080p.m3u8" (current uploads) or "…/<id>_720p.m3u8" (older ones)
        private static readonly Regex HlsVariantHeight = new Regex(@"[/_-](\d{3,4})p\.m3u8(?:\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PmvHavenCdnBase = new Regex(@"https?://[^/""'\s]*pmvhavencloud[^/""'\s]*/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Finds a PMVHaven video's own stream in its page. The page data escapes slashes
        /// (https://...) and lists the stream as absolute and relative links, next to
        /// preview clips of related videos. Returns the highest "{N}p.m3u8" variant of the
        /// first stream folder on the page, else that folder's master.m3u8, or null.
        /// </summary>
        internal static string FindPmvHavenStream(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            var decoded = DecodeScriptEscapes(html);

            var links = HlsLinkPattern.Matches(decoded)
                .Select(m => m.Groups[1].Value)
                .Where(link => !IsPreviewClip(link))
                .ToList();
            if (links.Count == 0) return null;

            // Relative links ("videos/x.mp4/master.m3u8") belong to the CDN the page's other files come from
            var cdnBase = links.Select(l => Uri.TryCreate(l, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}/" : null)
                              .FirstOrDefault(b => b != null);
            if (cdnBase == null)
            {
                var cdn = PmvHavenCdnBase.Match(decoded);
                if (cdn.Success) cdnBase = cdn.Value;
            }

            var absolute = links
                .Select(link => Uri.TryCreate(link, UriKind.Absolute, out var u) ? u
                    : cdnBase != null && Uri.TryCreate(new Uri(cdnBase), link.TrimStart('/'), out var r) ? r
                    : null)
                .Where(u => u != null && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                .Select(u => u.ToString())
                .ToList();
            if (absolute.Count == 0) return null;

            // The page's own video comes first; related videos only carry preview clips
            string Folder(string link) => link.Substring(0, link.LastIndexOf('/') + 1);
            var folder = Folder(absolute[0]);
            var streams = absolute.Where(l => Folder(l).Equals(folder, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();

            var best = streams
                .Select(l => (Link: l, Match: HlsVariantHeight.Match(l)))
                .Where(x => x.Match.Success)
                .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
                .Select(x => x.Link)
                .FirstOrDefault();

            return best
                ?? streams.FirstOrDefault(l => l.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase))
                ?? streams[0];
        }

        /// <summary>Short hover previews ("..._preview.mp4", ".../videoPreview/...") rather than the video itself.</summary>
        private static bool IsPreviewClip(string url) =>
            !string.IsNullOrEmpty(url) && url.Contains("preview", StringComparison.OrdinalIgnoreCase);

        private static readonly Regex UnicodeEscape = new Regex(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

        /// <summary>Turns script string escapes ("/", "\/") back into the characters they stand for.</summary>
        internal static string DecodeScriptEscapes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;
            text = UnicodeEscape.Replace(text, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            return text.Replace("\\/", "/");
        }

        private async Task<string> ExtractGenericVideoUrlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return null;

                var videoUrl = ExtractVideoFromHtml(html, url);
                return videoUrl;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting generic video URL: {ex.Message}");
                return null;
            }
        }

        private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
        {
            return await FetchHtmlWithRetryAsync(url, cancellationToken);
        }

        /// <summary>
        /// Fetches HTML with retry logic for transient failures
        /// </summary>
        private async Task<string> FetchHtmlWithRetryAsync(string url, CancellationToken cancellationToken, int maxRetries = 2)
        {
            for (int i = 0; i <= maxRetries; i++)
            {
                try
                {
                    var result = await _htmlFetcher.FetchHtmlAsync(url, cancellationToken);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                    // If result is empty/null but no exception, still retry
                    if (i < maxRetries)
                    {
                        Logger.Debug($"[VideoUrlExtractor] Empty response for {url}, retrying ({i + 1}/{maxRetries})...");
                        await Task.Delay(500 * (i + 1), cancellationToken);
                    }
                }
                catch (HttpRequestException ex) when (i < maxRetries)
                {
                    Logger.Debug($"[VideoUrlExtractor] HTTP error for {url}: {ex.Message}, retrying ({i + 1}/{maxRetries})...");
                    await Task.Delay(500 * (i + 1), cancellationToken); // Exponential backoff
                }
                catch (TaskCanceledException)
                {
                    throw; // Don't retry on cancellation
                }
                catch (Exception ex) when (i < maxRetries)
                {
                    Logger.Warning($"[VideoUrlExtractor] Error fetching {url}: {ex.Message}, retrying ({i + 1}/{maxRetries})...");
                    await Task.Delay(500 * (i + 1), cancellationToken);
                }
            }
            return null;
        }

        private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(5);

        private string ExtractVideoFromHtml(string html, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            Logger.Debug($"ExtractVideoFromHtml: Processing {html.Length} characters of HTML from {baseUrl}");

            try
            {
                // Method 1: Look for <video> tags with src attribute
                Logger.Debug("Trying Method 1: <video> tag with src attribute");
                var videoSrcPattern = @"<video[^>]+src\s*=\s*[""']([^""']+)[""']";
                var match = Regex.Match(html, videoSrcPattern, RegexOptions.IgnoreCase, _regexTimeout);
                if (match.Success && match.Groups.Count > 1)
                {
                    var videoUrl = match.Groups[1].Value;
                    var resolved = ResolveUrl(videoUrl, baseUrl);
                    Logger.Debug($"Method 1: Found video URL: {resolved}");
                    return resolved;
                }

                // Method 2: Look for <source> tags within video elements
                Logger.Debug("Trying Method 2: <source> tags");
                var sourcePattern = @"<source[^>]+src\s*=\s*[""']([^""']+)[""']";
                var sourceMatches = Regex.Matches(html, sourcePattern, RegexOptions.IgnoreCase, _regexTimeout);
                Logger.Debug($"Method 2: Found {sourceMatches.Count} <source> tags");
                foreach (Match sourceMatch in sourceMatches)
                {
                    if (sourceMatch.Success && sourceMatch.Groups.Count > 1)
                    {
                        var videoUrl = CleanExtractedUrl(sourceMatch.Groups[1].Value);
                        // Check for video extensions, allowing for query parameters
                        if (HasVideoExtension(videoUrl))
                        {
                            var resolved = ResolveUrl(videoUrl, baseUrl);
                            Logger.Debug($"Method 2: Found video URL: {resolved}");
                            return resolved;
                        }
                    }
                }

                // Method 3: Look for data-url or data-src attributes
                Logger.Debug("Trying Method 3: data attributes");
                var dataUrlPattern = @"(?:data-url|data-src|data-video-src|data-config)\s*=\s*[""']([^""']+)[""']";
                var dataMatches = Regex.Matches(html, dataUrlPattern, RegexOptions.IgnoreCase, _regexTimeout);
                foreach (Match dataMatch in dataMatches)
                {
                    if (dataMatch.Success && dataMatch.Groups.Count > 1)
                    {
                        var url = CleanExtractedUrl(dataMatch.Groups[1].Value);
                        if (HasVideoExtension(url))
                        {
                            var resolved = ResolveUrl(url, baseUrl);
                            Logger.Debug($"Method 3: Found video URL: {resolved}");
                            return resolved;
                        }
                    }
                }

                // Method 4: Look for video URLs in JavaScript variables
                var jsVideoPatterns = new[] {
                    @"(?:src|url|source|videoUrl|file|video_url|video_alt_url|video_alt_url2|video_alt_url3)\s*[:=]\s*[""']([^""']*\.(?:mp4|webm|mkv|avi|mov|wmv|m4v|mpg|mpeg|ts|m2ts)(?:\?[^""']*)?)[""']",
                    @"[""']([^""']*\.(?:mp4|webm|mkv|avi|mov|wmv|m4v|mpg|mpeg|ts|m2ts)(?:\?[^""']*)?)[""']"
                };

                // Pattern to identify .ts segments (e.g. video_000.ts) which we want to avoid
                var segmentPattern = @"_00\d\.ts";

                // Store potential matches to find the best quality if multiple exist
                var potentialUrls = new List<string>();

                foreach (var pattern in jsVideoPatterns)
                {
                    var jsMatches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase, _regexTimeout);
                    Logger.Debug($"Method 4: Pattern {pattern} found {jsMatches.Count} potential matches");
                    foreach (Match jsMatch in jsMatches)
                    {
                        if (jsMatch.Success && jsMatch.Groups.Count > 1)
                        {
                            var videoUrl = CleanExtractedUrl(jsMatch.Groups[1].Value);
                            var resolvedUrl = ResolveUrl(videoUrl, baseUrl);
                            if (IsValidVideoUrl(resolvedUrl))
                            {
                                potentialUrls.Add(resolvedUrl);
                            }
                        }
                    }
                }

                if (potentialUrls.Any())
                {
                    // Try to find the highest quality (often denoted by _1080p, _720p, etc. in the URL)
                    // Or just pick the last one if it's from variables like video_alt_url3
                    var bestUrl = potentialUrls.OrderByDescending(u =>
                    {
                        // Heavily penalize segments or HLS manifests when full containers are available
                        int score = 0;
                        if (u.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) score += 10000;
                        if (u.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) score += 9000;

                        // Penalize segments (e.g. pmvhaven's _000.ts)
                        if (Regex.IsMatch(u, segmentPattern, RegexOptions.IgnoreCase)) score -= 50000;
                        else if (u.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) score -= 1000;

                        if (u.Contains("_1080p") || u.Contains("1080")) score += 1080;
                        if (u.Contains("_720p") || u.Contains("720")) score += 720;
                        if (u.Contains("_480p") || u.Contains("480")) score += 480;
                        if (u.Contains("_360p") || u.Contains("360")) score += 360;
                        return score;
                    }).First();

                    Logger.Debug($"Method 4: Selected best video URL: {bestUrl}");
                    return bestUrl;
                }

                // Method 5: Look for HLS streams (m3u8)
                Logger.Debug("Trying Method 5: HLS streams");
                var hlsPattern = @"[""']([^""']+\.m3u8(?:[^""']*)?)[""']";
                var hlsMatches = Regex.Matches(html, hlsPattern, RegexOptions.IgnoreCase, _regexTimeout);
                Logger.Debug($"Method 5: Found {hlsMatches.Count} potential HLS streams");
                foreach (Match hlsMatch in hlsMatches)
                {
                    if (hlsMatch.Success && hlsMatch.Groups.Count > 1)
                    {
                        var hlsUrl = CleanExtractedUrl(hlsMatch.Groups[1].Value);
                        var resolved = ResolveUrl(hlsUrl, baseUrl);
                        Logger.Debug($"Method 5: Found HLS URL: {resolved}");
                        return resolved;
                    }
                }

                // Method 6: Look for Plyr source configuration
                Logger.Debug("Trying Method 6: Plyr configuration");
                var plyrPattern = @"plyr_player\.source\s*=\s*\{[\s\S]*?sources\s*:\s*\[\s*\{\s*[""']?src[""']?\s*:\s*[""']([^""']+)[""']";
                var plyrMatch = Regex.Match(html, plyrPattern, RegexOptions.IgnoreCase, _regexTimeout);
                if (plyrMatch.Success && plyrMatch.Groups.Count > 1)
                {
                    var videoUrl = CleanExtractedUrl(plyrMatch.Groups[1].Value);
                    var resolved = ResolveUrl(videoUrl, baseUrl);
                    Logger.Debug($"Method 6: Found Plyr URL: {resolved}");
                    return resolved;
                }

                // Method 7: Look for video URLs in JSON data
                Logger.Debug("Trying Method 7: JSON data");
                var jsonVideoUrl = ExtractVideoFromJson(html, baseUrl);
                if (jsonVideoUrl != null)
                {
                    Logger.Debug($"Method 7: Found JSON video URL: {jsonVideoUrl}");
                    return jsonVideoUrl;
                }

                Logger.Warning("ExtractVideoFromHtml: All extraction methods failed");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting video from HTML: {ex.Message}");
                return null;
            }
        }

        private bool HasVideoExtension(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Extract the path part before query parameters/fragments
            string path = url;
            int queryIndex = url.IndexOf('?');
            if (queryIndex != -1) path = url.Substring(0, queryIndex);

            int fragmentIndex = path.IndexOf('#');
            if (fragmentIndex != -1) path = path.Substring(0, fragmentIndex);

            // Remove trailing slash if present (common in some Hypnotube video links)
            path = path.TrimEnd('/');

            return Constants.VideoExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractVideoFromJson(string html, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                // Look for JSON-like structures that might contain video URLs
                // This version is more lenient with whitespace and escaped characters
                var jsonPattern = @"""(?:src|url|source|file|videoUrl)""\s*:\s*""([^""]+\.(?:mp4|webm|mkv|avi|mov|wmv|m4v|mpg|mpeg|ts|m2ts)[^""]*)""";
                var matches = Regex.Matches(html, jsonPattern, RegexOptions.IgnoreCase, _regexTimeout);

                foreach (Match match in matches)
                {
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var videoUrl = CleanExtractedUrl(match.Groups[1].Value);

                        var resolvedUrl = ResolveUrl(videoUrl, baseUrl);
                        if (IsValidVideoUrl(resolvedUrl))
                        {
                            return resolvedUrl;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting video from JSON: {ex.Message}");
                return null;
            }
        }

        private string CleanExtractedUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            // Remove whitespace
            url = url.Trim();

            // Handle escaped characters from scripts ("\/", "/")
            url = DecodeScriptEscapes(url);

            // Some sites (like Rule34Video) prefix URLs with metadata like 'function/0/'
            // If the URL contains 'http' but not at the start, try to isolate the actual URL
            int httpIndex = url.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (httpIndex > 0)
            {
                var potentialUrl = url.Substring(httpIndex);
                // Check if the isolated part is a valid absolute URL
                if (Uri.TryCreate(potentialUrl, UriKind.Absolute, out _))
                {
                    Logger.Debug($"CleanExtractedUrl: Isolated URL from prefix: {potentialUrl} (original: {url})");
                    return potentialUrl;
                }
            }

            return url;
        }

        private string ResolveUrl(string url, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri absoluteUri))
                {
                    return absoluteUri.ToString();
                }

                if (Uri.TryCreate(new Uri(baseUrl), url, out Uri resolvedUri))
                {
                    return resolvedUri.ToString();
                }

                return url;
            }
            catch
            {
                return url;
            }
        }

        private bool IsValidVideoUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Check if it's a valid URL with video extension
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                var path = uri.AbsolutePath.ToLowerInvariant();
                return Constants.VideoExtensions.Any(ext => path.EndsWith(ext)) ||
                       uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            }

            return false;
        }

        /// <summary>
        /// Extracts video title from a page URL using multiple methods
        /// </summary>
        /// <param name="pageUrl">The page URL to extract title from</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The video title, or null if extraction failed</returns>
        public async Task<string> ExtractVideoTitleAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return null;

            try
            {
                var normalizedUrl = FileValidator.NormalizeUrl(pageUrl);
                var uri = new Uri(normalizedUrl);
                var host = uri.Host.ToLowerInvariant();

                // Iwara's pages are an app shell with no title in them; yt-dlp reads its API
                if (SiteRules.Is(host, Site.Iwara))
                {
                    if (_ytDlpService != null && _ytDlpService.IsAvailable)
                    {
                        try
                        {
                            Logger.Debug($"[VideoUrlExtractor] Extracting title using yt-dlp for {host}");
                            var info = await _ytDlpService.ExtractVideoInfoAsync(normalizedUrl, cancellationToken);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Title) && info.Title != "Unknown")
                            {
                                return info.Title;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"[VideoUrlExtractor] yt-dlp title extraction failed for {host}: {ex.Message}");
                        }
                    }
                }

                var html = await FetchHtmlAsync(normalizedUrl, cancellationToken);
                if (string.IsNullOrWhiteSpace(html))
                {
                    // Fallback to yt-dlp if HTML fetch failed and yt-dlp is available
                    if (_ytDlpService != null && _ytDlpService.IsAvailable)
                    {
                        try
                        {
                            Logger.Debug($"[VideoUrlExtractor] HTML fetch failed, fallback to yt-dlp title extraction for {normalizedUrl}");
                            var info = await _ytDlpService.ExtractVideoInfoAsync(normalizedUrl, cancellationToken);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Title) && info.Title != "Unknown")
                            {
                                return info.Title;
                            }
                        }
                        catch { }
                    }
                    return null;
                }

                return ExtractTitleFromHtml(html, uri.Host);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting video title from {pageUrl}: {ex.Message}");
                return null;
            }
        }

        private string ExtractTitleFromUrlSlug(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                var path = uri.AbsolutePath;

                // YouTube links have no slug ("/watch?v=…"): leave the title to the page or yt-dlp
                if (SiteRules.Is(host, Site.YouTube)) return null;

                // Handle specific sites
                if (SiteRules.Is(host, Site.Iwara))
                {
                    // https://www.iwara.tv/video/UZcPefzEvubIa5/bengugu-hmv-sfx -> bengugu-hmv-sfx
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length >= 3 && segments[0] == "video")
                    {
                        return TitleSanitizer.Clean(segments[2], host);
                    }
                }
                else if (SiteRules.Is(host, Site.Hypnotube))
                {
                    // https://hypnotube.com/video/seen-my-boyfriend-kpop-wbch-pmv-78110.html -> seen-my-boyfriend-kpop-wbch-pmv
                    var slug = Path.GetFileNameWithoutExtension(path);
                    return TitleSanitizer.Clean(slug, host);
                }
                else if (SiteRules.Is(host, Site.Rule34Video))
                {
                    // https://rule34video.com/video/3067406/slug/ -> slug
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length >= 3 && segments[0] == "video")
                    {
                        return TitleSanitizer.Clean(segments[2], host);
                    }
                }

                // Generic fallback for any slug
                var genericSlug = Path.GetFileNameWithoutExtension(path.TrimEnd('/'));
                if (!string.IsNullOrEmpty(genericSlug) && genericSlug.Length > 3)
                {
                    return TitleSanitizer.Clean(genericSlug, host);
                }
            }
            catch { }

            return null;
        }

        private string ExtractTitleFromHtml(string html, string host)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                host = host.ToLowerInvariant();

                // Method 0: Aggressive Regex for common meta tags (Highest Priority for reliability)
                var ogRegex = Regex.Match(html, @"<meta[^>]*(?:property|name)\s*=\s*[""']og:title[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!ogRegex.Success)
                {
                    ogRegex = Regex.Match(html, @"<meta[^>]*content\s*=\s*[""']([^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']og:title[""']", RegexOptions.IgnoreCase);
                }

                if (ogRegex.Success)
                {
                    var title = WebUtility.HtmlDecode(ogRegex.Groups[1].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        var sanitized = TitleSanitizer.Clean(title, host);
                        if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
                    }
                }

                // Method 1: Try site-specific title extraction (Highest Priority)
                if (SiteRules.Is(host, Site.Hypnotube))
                {
                    var hTitle = StashPatternExtractor.ExtractHypnotubeTitle(html);
                    if (!string.IsNullOrEmpty(hTitle))
                    {
                        var sanitized = TitleSanitizer.Clean(hTitle, host);
                        if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
                    }
                }
                else if (SiteRules.Is(host, Site.Rule34Video))
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    var h1 = doc.DocumentNode.SelectSingleNode("//h1");
                    if (h1 != null)
                    {
                        var title = WebUtility.HtmlDecode(h1.InnerText?.Trim());
                        if (!string.IsNullOrWhiteSpace(title)) return title;
                    }
                }

                // Method 2: Try Open Graph meta tags (Robust via HtmlAgilityPack)
                try
                {
                    var ogTitle = StashPatternExtractor.ExtractOgTitle(html);
                    Logger.Debug($"[VideoUrlExtractor] ExtractOgTitle returned: '{ogTitle}'");
                    if (!string.IsNullOrWhiteSpace(ogTitle))
                    {
                        var sanitized = TitleSanitizer.Clean(ogTitle, host);
                        if (!string.IsNullOrWhiteSpace(sanitized))
                        {
                            return sanitized;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[VideoUrlExtractor] ExtractOgTitle exception: {ex.Message}");
                }

                // Method 3: Try Twitter meta tags
                try
                {
                    var twitterTitle = ExtractMetaTag(html, "twitter:title");
                    if (!string.IsNullOrWhiteSpace(twitterTitle))
                    {
                        var sanitized = TitleSanitizer.Clean(twitterTitle, host);
                        if (!string.IsNullOrWhiteSpace(sanitized))
                        {
                            return sanitized;
                        }
                    }
                }
                catch { }

                // Method 3: Try HTML title tag
                try
                {
                    var htmlTitle = ExtractHtmlTitle(html);
                    if (!string.IsNullOrWhiteSpace(htmlTitle))
                    {
                        var sanitized = TitleSanitizer.Clean(htmlTitle, host);
                        if (!string.IsNullOrWhiteSpace(sanitized))
                        {
                            return sanitized;
                        }
                    }
                }
                catch { }
#pragma warning disable CS0618
                // Method 4: Try page elements (site-specific)
                try
                {
                    // We need a dummy URL for site-specific logic if we don't have one, 
                    // but usually host is enough or we use generic elements
                    var elementTitle = ExtractTitleFromPageElements(html, host);
                    if (!string.IsNullOrWhiteSpace(elementTitle))
                    {
                        var sanitized = TitleSanitizer.Clean(elementTitle, host);
                        if (!string.IsNullOrWhiteSpace(sanitized))
                        {
                            return sanitized;
                        }
                    }
                }
                catch { }
#pragma warning restore CS0618
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error in ExtractTitleFromHtml: {ex.Message}");
                return null;
            }
        }

        private string ExtractMetaTag(string html, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                var pattern = $@"<meta\s+[^>]*(?:property|name)\s*=\s*[""']{Regex.Escape(propertyName)}[""'][^>]*content\s*=\s*[""']([^""']+)[""']";
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return WebUtility.HtmlDecode(match.Groups[1].Value);
                }

                // Alternative pattern: content before property/name
                pattern = $@"<meta\s+[^>]*content\s*=\s*[""']([^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']{Regex.Escape(propertyName)}[""']";
                match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return WebUtility.HtmlDecode(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting meta tag {propertyName}: {ex.Message}");
            }

            return null;
        }

        private string ExtractHtmlTitle(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                var titlePattern = @"<title[^>]*>([^<]+)</title>";
                var match = Regex.Match(html, titlePattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    var title = WebUtility.HtmlDecode(match.Groups[1].Value);

                    // Clean up common site suffixes
                    var suffixes = new[] { " - Hypnotube", " | Hypnotube", " - RULE34VIDEO", " | RULE34VIDEO",
                                          " - PMVHaven", " | PMVHaven" };
                    foreach (var suffix in suffixes)
                    {
                        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        {
                            title = title.Substring(0, title.Length - suffix.Length).Trim();
                            break;
                        }
                    }

                    return title;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting HTML title: {ex.Message}");
            }

            return null;
        }

        private string ExtractTitleFromPageElements(string html, string url)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                // Site-specific extraction patterns
                if (SiteRules.Is(host, Site.Hypnotube))
                {
                    // Try h1 with video-title class or similar
                    var pattern = @"<h1[^>]*class\s*=\s*[""'][^""']*video[^""']*title[^""']*[""'][^>]*>([^<]+)</h1>";
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        return WebUtility.HtmlDecode(match.Groups[1].Value);
                    }
                }
                else if (SiteRules.Is(host, Site.Rule34Video))
                {
                    // Try common title patterns
                    var patterns = new[] {
                        @"<h1[^>]*>([^<]+)</h1>",
                        @"<div[^>]*class\s*=\s*[""'][^""']*title[^""']*[""'][^>]*>([^<]+)</div>"
                    };
                    foreach (var pattern in patterns)
                    {
                        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups.Count > 1)
                        {
                            var title = WebUtility.HtmlDecode(match.Groups[1].Value);
                            if (!string.IsNullOrWhiteSpace(title) && title.Length > 3)
                            {
                                return title;
                            }
                        }
                    }
                }
                else if (SiteRules.Is(host, Site.PmvHaven))
                {
                    // Try h1 or title div
                    var patterns = new[] {
                        @"<h1[^>]*>([^<]+)</h1>",
                        @"<div[^>]*class\s*=\s*[""'][^""']*video[^""']*title[^""']*[""'][^>]*>([^<]+)</div>"
                    };
                    foreach (var pattern in patterns)
                    {
                        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups.Count > 1)
                        {
                            var title = WebUtility.HtmlDecode(match.Groups[1].Value);
                            if (!string.IsNullOrWhiteSpace(title) && title.Length > 3)
                            {
                                return title;
                            }
                        }
                    }
                }

                // Generic fallback: try first h1
                var genericPattern = @"<h1[^>]*>([^<]+)</h1>";
                var genericMatch = Regex.Match(html, genericPattern, RegexOptions.IgnoreCase);
                if (genericMatch.Success && genericMatch.Groups.Count > 1)
                {
                    var title = WebUtility.HtmlDecode(genericMatch.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(title) && title.Length > 3)
                    {
                        return title;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting title from page elements: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Clears the URL cache
        /// </summary>
        public void ClearCache()
        {
            PersistentUrlCache.Instance.Clear();
        }
    }
}




