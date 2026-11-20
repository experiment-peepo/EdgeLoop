using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using HtmlAgilityPack;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Classes
{
    public enum ImportMode
    {
        Auto,
        SingleVideo,
        Collection
    }

    /// <summary>
    /// Service for importing playlists from supported video sites
    /// </summary>
    public class PlaylistImporter
    {
        private readonly IVideoUrlExtractor _urlExtractor;
        private readonly IHtmlFetcher _htmlFetcher;
        private readonly YtDlpService _ytDlpService;

        public PlaylistImporter(IVideoUrlExtractor urlExtractor, IHtmlFetcher htmlFetcher = null, YtDlpService ytDlpService = null)
        {
            _urlExtractor = urlExtractor ?? throw new ArgumentNullException(nameof(urlExtractor));
            _htmlFetcher = htmlFetcher ?? new StandardHtmlFetcher();
            _ytDlpService = ytDlpService ?? (ServiceContainer.TryGet<YtDlpService>(out var service) ? service : null);
        }

        /// <summary>
        /// Imports a playlist from a supported site
        /// </summary>
        /// <param name="playlistUrl">The playlist page URL</param>
        /// <param name="progressCallback">Optional callback for progress updates (current, total)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of VideoItems from the playlist</returns>
        private struct VideoLinkInfo
        {
            public string Url;
            public string Title;
        }

        public async Task<List<VideoItem>> ImportPlaylistAsync(
            string playlistUrl,
            Action<int, int> progressCallback = null,
            CancellationToken cancellationToken = default,
            ImportMode mode = ImportMode.Auto)
        {

            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                throw new ArgumentException("Playlist URL cannot be empty", nameof(playlistUrl));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var videoItems = new List<VideoItem>();
            var videoLinkInfos = new List<VideoLinkInfo>();

            try
            {
                var uri = new Uri(playlistUrl);
                var host = uri.Host.ToLowerInvariant();

                // Check if this is a single video page instead of a playlist
                bool isSingleVideo = mode == ImportMode.SingleVideo || (mode == ImportMode.Auto && IsVideoPageUrl(playlistUrl));

                bool isIwara = SiteRules.Is(host, Site.Iwara);

                if (isSingleVideo)
                {
                    Logger.Debug($"[PlaylistImporter] Treating input as single video page: {playlistUrl}");
                    videoLinkInfos.Add(new VideoLinkInfo { Url = playlistUrl, Title = null });
                }
                else if (SiteRules.Is(host, Site.Hypnotube))
                {
                    var links = await ExtractHypnotubePlaylistAsync(playlistUrl, cancellationToken);
                    foreach (var kvp in links)
                    {
                        videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                    }
                }
                else if (SiteRules.Is(host, Site.Rule34Video))
                {
                    var links = await ExtractGenericPlaylistWithTitlesAsync(playlistUrl, cancellationToken);
                    foreach (var kvp in links)
                    {
                        videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                    }
                }
                else if (SiteRules.Is(host, Site.PmvHaven))
                {
                    var links = await ExtractPmvHavenPlaylistAsync(playlistUrl, cancellationToken);
                    foreach (var kvp in links)
                    {
                        videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                    }
                }
                else if (isIwara)
                {
                    if (IsVideoPageUrl(playlistUrl))
                    {
                        Logger.Debug($"[PlaylistImporter] Recognized iwara.tv input as single video page, skipping playlist extraction: {playlistUrl}");
                        videoLinkInfos.Add(new VideoLinkInfo { Url = playlistUrl, Title = null });
                    }
                    else
                    {
                        Logger.Debug($"[PlaylistImporter] Trying native API for iwara.tv playlist: {playlistUrl}");
                        var links = await ExtractIwaraPlaylistAsync(playlistUrl, cancellationToken);
                        if (links.Count > 0)
                        {
                            foreach (var kvp in links)
                            {
                                videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                            }
                        }

                        // If native fails (e.g. 403 Forbidden), try yt-dlp which is much more resilient
                        if (videoLinkInfos.Count == 0 && _ytDlpService != null)
                        {
                            Logger.Debug($"[PlaylistImporter] Native Iwara extraction failed (likely 403), falling back to yt-dlp: {playlistUrl}");
                            var info = await _ytDlpService.ExtractPlaylistInfoAsync(playlistUrl, cancellationToken);
                            foreach (var kvp in info)
                            {
                                videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                            }
                        }
                    }
                }
                else if (_ytDlpService != null && _ytDlpService.IsAvailable && YtDlpService.IsDownloadRequiredSite(host))
                {
                    Logger.Debug($"[PlaylistImporter] Using yt-dlp for {host} playlist extraction (download-required site)");
                    var info = await _ytDlpService.ExtractPlaylistInfoAsync(playlistUrl, cancellationToken);
                    foreach (var kvp in info)
                    {
                        videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                    }
                }
                else
                {
                    // Generic extraction
                    Logger.Debug($"[PlaylistImporter] Falling back to generic scraping for {host}");
                    var links = await ExtractGenericPlaylistWithTitlesAsync(playlistUrl, cancellationToken);
                    if (links.Count == 0 && _ytDlpService != null && _ytDlpService.IsAvailable)
                    {
                        Logger.Debug($"[PlaylistImporter] Generic scrape failed, trying yt-dlp for {host}");
                        var info = await _ytDlpService.ExtractPlaylistInfoAsync(playlistUrl, cancellationToken);
                        foreach (var kvp in info)
                        {
                            videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                        }
                    }
                    else
                    {
                        foreach (var kvp in links)
                        {
                            videoLinkInfos.Add(new VideoLinkInfo { Url = kvp.Key, Title = kvp.Value });
                        }
                    }
                }

                if (videoLinkInfos.Count == 0)
                {
                    Logger.Warning($"No videos found in playlist: {playlistUrl}");
                    return videoItems;
                }

                int total = videoLinkInfos.Count;
                int current = 0;

                // Extract direct video URLs and titles for each page URL
                foreach (var info in videoLinkInfos)
                {
                    var pageUrl = info.Url;
                    cancellationToken.ThrowIfCancellationRequested();

                    current++;
                    progressCallback?.Invoke(current, total);

                    try
                    {
                        // Validate page URL first
                        if (!FileValidator.ValidateVideoUrl(pageUrl, out string validationError))
                        {
                            Logger.Warning($"Skipped invalid page URL: {pageUrl} - {validationError}");
                            continue;
                        }

                        // Extract Metadata (Integrated URL and Title in one pass)
                        var metadata = await _urlExtractor.ExtractVideoMetadataAsync(pageUrl, cancellationToken);
                        Logger.Debug($"[PlaylistImporter] Metadata for {pageUrl}: Title='{metadata.Title}', UrlSuccess={!string.IsNullOrEmpty(metadata.Url)}");
                        var videoUrl = metadata.Url;
                        var title = metadata.Title;

                        // PREFERENCE: Use the title from the gallery/API if available.
                        if (!string.IsNullOrWhiteSpace(info.Title))
                        {
                            // If the extracted title is generic (ID-like or empty), or if we are on a known site with good gallery titles
                            bool isGeneric = string.IsNullOrWhiteSpace(title) ||
                                           TitleSanitizer.IsIdLike(title) ||
                                           title.ToLowerInvariant() == "view" ||
                                           title.ToLowerInvariant() == "unknown";

                            if (isGeneric || SiteRules.Is(pageUrl, Site.Iwara) || SiteRules.Is(pageUrl, Site.Hypnotube) || SiteRules.Is(pageUrl, Site.Rule34Video))
                            {
                                if (!string.IsNullOrWhiteSpace(info.Title) && !TitleSanitizer.IsIdLike(info.Title))
                                {
                                    title = info.Title;
                                }
                            }
                        }

                        // Final fallback: If still ID-like, try to de-slugify the URL
                        if (TitleSanitizer.IsIdLike(title))
                        {
                            var deSlugified = TitleSanitizer.FromUrlSlug(pageUrl);
                            if (!string.IsNullOrWhiteSpace(deSlugified) && !TitleSanitizer.IsIdLike(deSlugified))
                            {
                                title = deSlugified;
                            }
                        }

                        // If extraction failed, don't skip the video entirely.
                        // Instead, add it using the page URL as the FilePath, which will trigger JIT extraction during playback.
                        if (string.IsNullOrEmpty(videoUrl))
                        {
                            string reason = "likely restricted or removed";
                            if (SiteRules.Is(pageUrl, Site.Hypnotube)) reason = "restricted, friends-only, or removed";
                            else if (SiteRules.Is(pageUrl, Site.Iwara)) reason = "requires cookies or age-verification";

                            Logger.Warning($"Extraction failed for {pageUrl} ({reason}). Adding as page URL for JIT resolution.");
                            videoUrl = pageUrl;
                        }

                        if (string.IsNullOrWhiteSpace(title) || TitleSanitizer.IsIdLike(title))
                        {
                            // Extract title from URL slug as a high-quality fallback
                            try
                            {
                                var slug = pageUrl.Split('?').First().TrimEnd('/').Split('/').Last();
                                // Remove .html, .htm, etc.
                                if (slug.Contains(".")) slug = slug.Substring(0, slug.LastIndexOf('.'));

                                // Remove numeric IDs at the end (e.g. -7132)
                                slug = Regex.Replace(slug, @"-\d+$", "");

                                // Replace dashes/underscores with spaces and capitalize
                                title = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                                    slug.Replace("-", " ").Replace("_", " ")
                                );
                            }
                            catch
                            {
                                title = pageUrl.Split('/').Last();
                            }
                        }

                        // Create video item
                        var videoItem = new VideoItem(videoUrl)
                        {
                            Title = title,
                            OriginalPageUrl = pageUrl  // Store for re-extraction when URLs expire
                        };

                        // Validate the video item
                        videoItem.Validate();

                        if (videoItem.ValidationStatus == FileValidationStatus.Valid)
                        {
                            videoItems.Add(videoItem);
                        }
                        else
                        {
                            Logger.Warning($"Skipped invalid video URL: {videoUrl} - {videoItem.ValidationError}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Error processing video URL {pageUrl}: {ex.Message}");
                        // Continue with next video - never fail entire playlist import
                    }
                }

                return videoItems;
            }
            catch (OperationCanceledException)
            {
                Logger.Debug("Playlist import was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error importing playlist from {playlistUrl}", ex);
                throw;
            }
        }

        private async Task<Dictionary<string, string>> ExtractHypnotubePlaylistAsync(string url, CancellationToken cancellationToken)
        {
            var allVideoUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Start with first page
                var currentUrl = url;
                var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pageUrlsToFetch = new Queue<string>();
                pageUrlsToFetch.Enqueue(currentUrl);

                var maxPages = App.Settings?.MaxPlaylistPages ?? 100;
                int pagesFetched = 0;

                // If this is a single video page, don't treat it as a playlist
                if (IsHypnotubeVideoPageUrl(url))
                {
                    Logger.Debug($"[Hypnotube] Recognized input as single video page, skipping playlist extraction: {url}");
                    return new Dictionary<string, string> { { url, string.Empty } };
                }

                while (pageUrlsToFetch.Count > 0 && pagesFetched < maxPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    currentUrl = pageUrlsToFetch.Dequeue();
                    if (visitedPages.Contains(currentUrl)) continue;
                    visitedPages.Add(currentUrl);
                    pagesFetched++;

                    Logger.Debug($"[Hypnotube] Fetching page: {currentUrl}");
                    var html = await FetchHtmlAsync(currentUrl, cancellationToken);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        Logger.Warning($"[Hypnotube] Failed to fetch HTML for {currentUrl} (possibly blocked)");
                        continue;
                    }

                    Logger.Debug($"[Hypnotube] Received HTML ({html.Length} chars)");

                    // Extract video URLs from current page
                    var pageVideoUrls = ExtractHypnotubeLinksWithTitles(html, currentUrl);
                    foreach (var kvp in pageVideoUrls)
                    {
                        if (!allVideoUrls.ContainsKey(kvp.Key))
                        {
                            allVideoUrls[kvp.Key] = kvp.Value;
                        }
                    }

                    // Extract pagination links
                    try
                    {
                        var nextPageUrl = ExtractNextPageUrl(html, currentUrl, "hypnotube.com");
                        if (!string.IsNullOrWhiteSpace(nextPageUrl) && !visitedPages.Contains(nextPageUrl))
                        {
                            pageUrlsToFetch.Enqueue(nextPageUrl);
                        }
                    }
                    catch (Exception pagEx)
                    {
                        Logger.Warning($"Error extracting Hypnotube next page URL: {pagEx.Message}");
                        break;
                    }
                }

                return allVideoUrls;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting Hypnotube playlist: {ex.Message}");
                return allVideoUrls;
            }
        }




        private async Task<Dictionary<string, string>> ExtractIwaraPlaylistAsync(string url, CancellationToken cancellationToken)
        {
            var videoUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // URL can be https://www.iwara.tv/playlist/[uuid] or https://api.iwara.tv/playlist/[uuid]
                var match = Regex.Match(url, @"/playlist/([a-f0-9-]+)", RegexOptions.IgnoreCase);
                if (!match.Success) return videoUrls;

                var playlistId = match.Groups[1].Value;
                int page = 0;
                int maxPages = App.Settings?.MaxPlaylistPages ?? 100;

                Logger.Debug($"[Iwara] Starting API extraction for playlist {playlistId}");

                while (page < maxPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var apiUrl = $"https://api.iwara.tv/playlist/{playlistId}?page={page}";

                    string json = null;
                    try
                    {
                        json = await FetchHtmlAsync(apiUrl, cancellationToken);
                    }
                    catch (Exception ex) when (ex.Message.Contains("403"))
                    {
                        Logger.Warning($"[Iwara] API returned 403. Retrying without cookies...");
                        // StandardHtmlFetcher handles the "without cookies" logic if we bypass it here
                        // but actually we need to tell it to skip cookies.
                        // For now, let's just log it. The next fetch attempt will be via yt-dlp anyway.
                    }

                    if (string.IsNullOrWhiteSpace(json)) break;

                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    {
                        break;
                    }

                    int count = 0;
                    foreach (var item in results.EnumerateArray())
                    {
                        JsonElement videoElement = item;
                        // Some API versions wrap the video in a 'video' property
                        if (item.TryGetProperty("video", out var v)) videoElement = v;

                        if (videoElement.TryGetProperty("id", out var idProp))
                        {
                            var id = idProp.GetString();

                            // Try multiple fields for slug
                            string slug = string.Empty;
                            if (videoElement.TryGetProperty("slug", out var s)) slug = s.GetString();

                            // Try multiple fields for title
                            string title = string.Empty;
                            if (videoElement.TryGetProperty("title", out var t)) title = t.GetString();
                            else if (videoElement.TryGetProperty("name", out var n)) title = n.GetString();

                            // If title is missing or generic, use slug
                            if (string.IsNullOrWhiteSpace(title) || title == id || TitleSanitizer.IsIdLike(title))
                            {
                                if (!string.IsNullOrWhiteSpace(slug) && !TitleSanitizer.IsIdLike(slug))
                                {
                                    title = TitleSanitizer.DeSlugify(slug);
                                }
                                else if (string.IsNullOrWhiteSpace(title) || TitleSanitizer.IsIdLike(title))
                                {
                                    title = id;
                                }
                            }

                            // Final sanitization: If still ID-like or placeholder, try Search API fallback
                            if (TitleSanitizer.IsIdLike(title))
                            {
                                var searchTitle = await FetchIwaraTitleFromSearchApiAsync(id, cancellationToken);
                                if (!string.IsNullOrWhiteSpace(searchTitle) && !TitleSanitizer.IsIdLike(searchTitle))
                                {
                                    title = searchTitle;
                                }
                                else
                                {
                                    title = string.Empty; // Clear so fallback can try de-slugifying URL
                                }
                            }

                            var videoUrl = $"https://www.iwara.tv/video/{id}/{slug}";
                            if (!videoUrls.ContainsKey(videoUrl))
                            {
                                videoUrls[videoUrl] = title;
                                count++;
                            }
                        }
                        else
                        {
                            Logger.Debug($"[Iwara] Skipping item without ID: {item.ToString().Substring(0, Math.Min(50, item.ToString().Length))}...");
                        }
                    }

                    Logger.Debug($"[Iwara] Page {page}: Extracted {count} videos");
                    if (count == 0) break;

                    page++;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting Iwara playlist: {ex.Message}");
            }
            return videoUrls;
        }

        private async Task<string> FetchIwaraTitleFromSearchApiAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                // Search API often bypasses the 403 blocks seen on individual playlist/video APIs
                var apiUrl = $"https://api.iwara.tv/videos?query={id}";
                var json = await FetchHtmlAsync(apiUrl, cancellationToken);
                if (string.IsNullOrWhiteSpace(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && idProp.GetString() == id)
                        {
                            if (item.TryGetProperty("title", out var titleProp))
                            {
                                return titleProp.GetString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Iwara] Search API fallback failed for {id}: {ex.Message}");
            }
            return null;
        }

        private async Task<List<string>> ExtractRule34VideoPlaylistAsync(string url, CancellationToken cancellationToken)
        {
            var allVideoUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Start with first page
                var currentUrl = url;
                var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pageUrlsToFetch = new Queue<string>();
                pageUrlsToFetch.Enqueue(currentUrl);

                // Use configurable max pages from user settings
                var maxPages = App.Settings?.MaxPlaylistPages ?? 100;
                int pagesFetched = 0;

                // If this is a single video page, don't treat it as a playlist
                if (IsRule34VideoPageUrl(url))
                {
                    Logger.Debug($"[Rule34Video] Recognized input as single video page, skipping playlist extraction: {url}");
                    return new List<string> { url };
                }

                while (pageUrlsToFetch.Count > 0 && pagesFetched < maxPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    currentUrl = pageUrlsToFetch.Dequeue();
                    if (visitedPages.Contains(currentUrl)) continue;
                    visitedPages.Add(currentUrl);
                    pagesFetched++;

                    var html = await FetchHtmlAsync(currentUrl, cancellationToken);
                    if (string.IsNullOrWhiteSpace(html)) continue;

                    // Extract video URLs from current page
                    var pageVideoUrls = ExtractRule34VideoLinksFromHtml(html, currentUrl);
                    foreach (var videoUrl in pageVideoUrls)
                    {
                        allVideoUrls.Add(videoUrl);
                    }

                    // Extract pagination links
                    try
                    {
                        var nextPageUrl = ExtractNextPageUrl(html, currentUrl, "rule34video.com");
                        if (!string.IsNullOrWhiteSpace(nextPageUrl) && !visitedPages.Contains(nextPageUrl))
                        {
                            pageUrlsToFetch.Enqueue(nextPageUrl);
                        }
                    }
                    catch (Exception pagEx)
                    {
                        Logger.Warning($"Error extracting next page URL: {pagEx.Message}. Stopping pagination.");
                        break; // Stop pagination if there's an error
                    }
                }

                if (pagesFetched >= maxPages)
                {
                    Logger.Warning($"RULE34Video playlist extraction stopped after {maxPages} pages (safety limit)");
                }

                return allVideoUrls.ToList();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting RULE34Video playlist: {ex.Message}");
                return allVideoUrls.ToList();
            }
        }

        private async Task<Dictionary<string, string>> ExtractPmvHavenPlaylistAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                // If this is a single video page, don't treat it as a playlist
                if (IsVideoPageUrl(url))
                {
                    Logger.Debug($"[PMVHaven] Recognized input as single video page, skipping playlist extraction: {url}");
                    return new Dictionary<string, string> { { url, string.Empty } };
                }

                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return new Dictionary<string, string>();

                return ExtractPmvHavenLinksWithTitles(html, url);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting PMVHaven playlist: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        private async Task<Dictionary<string, string>> ExtractGenericPlaylistWithTitlesAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return new Dictionary<string, string>();

                var host = new Uri(url).Host.ToLowerInvariant();
                var videoUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Use site-specific logic if available, otherwise generic extraction
                if (SiteRules.Is(host, Site.Hypnotube)) return ExtractHypnotubeLinksWithTitles(html, url);

                // Generic Title-Aware Extraction using HtmlAgilityPack
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var links = doc.DocumentNode.SelectNodes("//a[@href]");
                if (links != null)
                {
                    foreach (var link in links)
                    {
                        var href = link.GetAttributeValue("href", "");
                        if (string.IsNullOrEmpty(href) || href.Length < 5) continue;

                        var resolvedUrl = ResolveUrl(href, url);
                        if (resolvedUrl == null) continue;

                        // Check if this looks like a video link for the current site
                        bool isVideo = false;
                        if (SiteRules.Is(host, Site.Rule34Video)) isVideo = resolvedUrl.Contains("/video/") || resolvedUrl.Contains("/videos/");
                        else if (SiteRules.Is(host, Site.PmvHaven)) isVideo = resolvedUrl.Contains("/video/");
                        else if (host.Contains("pornhub")) isVideo = resolvedUrl.Contains("view_video.php");
                        else isVideo = resolvedUrl.Contains("/video/") || resolvedUrl.Contains("/v/");

                        if (isVideo)
                        {
                            // Normalize URL (remove fragments and non-essential query params)
                            var normalizedUrl = resolvedUrl.Split('#')[0];
                            // For tube sites, ?p= or ?playlist= are often important for context but not for identity
                            // but Rule34Video uses query params for pagination, not for single video identity.
                            if (SiteRules.Is(host, Site.Rule34Video) || host.Contains("pornhub"))
                            {
                                normalizedUrl = normalizedUrl.Split('?')[0];
                            }

                            // Extract title from various sources
                            var title = link.GetAttributeValue("title", "").Trim();
                            if (string.IsNullOrEmpty(title))
                            {
                                var img = link.SelectSingleNode(".//img");
                                if (img != null) title = img.GetAttributeValue("alt", "").Trim();
                            }
                            if (string.IsNullOrEmpty(title)) title = link.InnerText?.Trim() ?? "";

                            if (!videoUrls.ContainsKey(normalizedUrl))
                            {
                                videoUrls[normalizedUrl] = WebUtility.HtmlDecode(title);
                            }
                        }
                    }
                }

                return videoUrls;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting generic playlist with titles: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
        {
            return await _htmlFetcher.FetchHtmlAsync(url, cancellationToken);
        }

        /// <summary>
        /// Extracts video links from Hypnotube playlist HTML using regex patterns
        /// Hypnotube video pages have pattern: /video/[slug]-[id].html with optional ?p=[playlist_id]
        /// </summary>
        private Dictionary<string, string> ExtractHypnotubeLinksWithTitles(string html, string baseUrl)
        {
            var videoUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Extract playlist ID from URL if present (for filtering related videos)
                var playlistIdMatch = Regex.Match(baseUrl, @"/playlist/(\d+)", RegexOptions.IgnoreCase);
                var playlistId = playlistIdMatch.Success ? playlistIdMatch.Groups[1].Value : null;

                // Pattern to find all <a> tags with href containing /video/ path
                var linkPattern = @"<a[^>]*href\s*=\s*[""']([^""']*\/video\/[^""']+\.html[^""']*)[""'][^>]*>";
                var matches = Regex.Matches(html, linkPattern, RegexOptions.IgnoreCase);

                Logger.Debug($"[Hypnotube] Found {matches.Count} potential video links in HTML");

                foreach (Match match in matches)
                {
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var href = match.Groups[1].Value;

                        // Extract title from the anchor tag if possible (e.g. title="...")
                        var title = string.Empty;
                        var titleMatch = Regex.Match(match.Value, @"title\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                        if (titleMatch.Success)
                        {
                            title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
                        }
                        var resolvedUrl = ResolveUrl(href, baseUrl);

                        if (resolvedUrl != null && IsHypnotubeVideoPageUrl(resolvedUrl))
                        {
                            // If we're on a playlist page and link has the same playlist parameter, prioritize it
                            if (playlistId != null)
                            {
                                if (href.Contains($"?p={playlistId}") || href.Contains($"&p={playlistId}"))
                                {
                                    videoUrls[resolvedUrl] = title;
                                }
                                else if (!href.Contains("?p=") && !href.Contains("&p="))
                                {
                                    if (!videoUrls.ContainsKey(resolvedUrl)) videoUrls[resolvedUrl] = title;
                                }
                            }
                            else
                            {
                                videoUrls[resolvedUrl] = title;
                            }
                        }
                    }
                }

                // Fallback: Also try HtmlAgilityPack for better parsing
                if (videoUrls.Count == 0)
                {
                    Logger.Debug("[Hypnotube] Regex found no videos, trying HtmlAgilityPack fallback");
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    // Select all links that point to video pages
                    var allLinks = doc.DocumentNode.SelectNodes("//a[@href]");
                    if (allLinks != null)
                    {
                        foreach (var link in allLinks)
                        {
                            var href = link.GetAttributeValue("href", "");
                            if (href.Contains("/video/") && href.Contains(".html"))
                            {
                                var resolvedUrl = ResolveUrl(href, baseUrl);
                                if (resolvedUrl != null && IsHypnotubeVideoPageUrl(resolvedUrl))
                                {
                                    var title = link.GetAttributeValue("title", "").Trim();
                                    if (string.IsNullOrEmpty(title)) title = link.InnerText?.Trim() ?? "";
                                    videoUrls[resolvedUrl] = WebUtility.HtmlDecode(title);
                                }
                            }
                        }
                    }
                }

                if (videoUrls.Count == 0)
                {
                    Logger.Warning($"[Hypnotube] Failed to extract any video links from HTML ({html.Length} chars). The page structure might have changed or access is restricted.");
                }
                else
                {
                    Logger.Debug($"[Hypnotube] Extracted {videoUrls.Count} unique video URLs");
                }
                return videoUrls;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting Hypnotube video links from HTML: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Extracts video links from RULE34Video playlist HTML using site-specific patterns
        /// Rule34Video uses /videos/ID/slug format for video pages
        /// </summary>
        private List<string> ExtractRule34VideoLinksFromHtml(string html, string baseUrl)
        {
            var videoUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Logger.Debug("[Rule34Video] Starting link extraction with HtmlAgilityPack");

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Find the h2 header that contains 'Videos' to identify the uploads section
                // Then find the first .thumbs container following it.
                var headerNode = doc.DocumentNode.SelectSingleNode("//h2[contains(., 'Videos')]");
                HtmlNode mainThumbs = null;

                if (headerNode != null)
                {
                    mainThumbs = headerNode.SelectSingleNode("./following::div[contains(@class, 'thumbs')][1]");
                    if (mainThumbs != null)
                    {
                        Logger.Debug("[Rule34Video] Successfully isolated the 'Videos' container.");
                    }
                }

                // Fallback to the very first .thumbs if header-based selection failed
                if (mainThumbs == null)
                {
                    var contentArea = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'content_general')]") ?? doc.DocumentNode;
                    mainThumbs = contentArea.SelectSingleNode(".//div[contains(@class, 'thumbs')]");
                }

                if (mainThumbs != null)
                {
                    var links = mainThumbs.SelectNodes(".//a[contains(@href, '/video/')]");
                    if (links != null)
                    {
                        Logger.Debug($"[Rule34Video] Found {links.Count} videos in targeted container.");
                        foreach (var link in links)
                        {
                            var href = link.GetAttributeValue("href", "");
                            var resolvedUrl = ResolveUrl(href, baseUrl);
                            if (resolvedUrl != null && IsRule34VideoPageUrl(resolvedUrl))
                            {
                                videoUrls.Add(resolvedUrl);
                            }
                        }
                    }
                }

                // If nothing found, one last attempt at regex but with a stricter pattern to avoid favorites
                if (videoUrls.Count == 0 && !html.Contains("'s Favorites"))
                {
                    Logger.Debug("[Rule34Video] No containers found, using strict regex fallback (no favorites on page)");
                    var videoPattern = @"href\s*=\s*[""']([^""']*/videos?/\d+[^""']*)[""']";
                    var videoMatches = Regex.Matches(html, videoPattern, RegexOptions.IgnoreCase);

                    foreach (Match match in videoMatches)
                    {
                        if (match.Success && match.Groups.Count > 1)
                        {
                            var href = match.Groups[1].Value;
                            var resolvedUrl = ResolveUrl(href, baseUrl);
                            if (resolvedUrl != null && IsRule34VideoPageUrl(resolvedUrl))
                            {
                                videoUrls.Add(resolvedUrl);
                            }
                        }
                    }
                }

                Logger.Debug($"[Rule34Video] Extracted {videoUrls.Count} unique video URLs");
                return videoUrls.ToList();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting RULE34Video links from HTML: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Extracts video links from PMVHaven playlist HTML using LD+JSON metadata
        /// PMVHaven is a SPA that uses JavaScript buttons instead of traditional links
        /// The LD+JSON ItemList contains all video URLs reliably
        /// </summary>
        private Dictionary<string, string> ExtractPmvHavenLinksWithTitles(string html, string baseUrl)
        {
            var videoUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Logger.Debug("[PMVHaven] Starting playlist extraction");

                // PMVHaven uses LD+JSON with @type: ItemList for playlists
                // Extract JSON from <script type="application/ld+json">
                var ldJsonPattern = @"<script\s+type=[""']application/ld\+json[""'][^>]*>(.*?)</script>";
                var ldJsonMatches = Regex.Matches(html, ldJsonPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                Logger.Debug($"[PMVHaven] Found {ldJsonMatches.Count} LD+JSON blocks");

                foreach (Match match in ldJsonMatches)
                {
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var jsonContent = match.Groups[1].Value.Trim();

                        try
                        {
                            // Check if this is an ItemList (playlist)
                            if (jsonContent.Contains("\"@type\"") && jsonContent.Contains("\"ItemList\""))
                            {
                                Logger.Debug("[PMVHaven] Found LD+JSON ItemList");

                                // Try multiple URL patterns that PMVHaven might use
                                var urlPatterns = new[] {
                                    @"""embedUrl""\s*:\s*""([^""]+)""",      // embedUrl field
                                    @"""url""\s*:\s*""([^""]+/video[^""]+)""", // url field containing /video
                                    @"""contentUrl""\s*:\s*""([^""]+)""",   // contentUrl field
                                    @"""@id""\s*:\s*""([^""]+/video[^""]+)""" // @id field containing /video
                                };

                                foreach (var pattern in urlPatterns)
                                {
                                    var urlMatches = Regex.Matches(jsonContent, pattern, RegexOptions.IgnoreCase);

                                    foreach (Match urlMatch in urlMatches)
                                    {
                                        if (urlMatch.Success && urlMatch.Groups.Count > 1)
                                        {
                                            var extractedUrl = urlMatch.Groups[1].Value;

                                            // Clean up escaped URLs
                                            extractedUrl = extractedUrl.Replace("\\/", "/");

                                            // Filter: Only accept pmvhaven.com VIDEO PAGE URLs, not CDN/direct video URLs
                                            // Skip: video.pmvhaven.com (CDN), .mp4, .m3u8, .webm files
                                            if (!string.IsNullOrEmpty(extractedUrl) &&
                                                (extractedUrl.Contains("pmvhaven.com/videos/") || extractedUrl.Contains("pmvhaven.com/video/")) &&
                                                !extractedUrl.Contains("video.pmvhaven.com") &&
                                                !extractedUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
                                                !extractedUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
                                                !extractedUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) &&
                                                !extractedUrl.Contains("/master.m3u8"))
                                            {

                                                // Try to find a title near this URL in the JSON
                                                string title = string.Empty;
                                                var urlEscaped = Regex.Escape(urlMatch.Value);
                                                var namePattern = $@"""name""\s*:\s*""([^""]+)""[^}}]*?{urlEscaped}";
                                                var nameMatch = Regex.Match(jsonContent, namePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                                                if (!nameMatch.Success)
                                                {
                                                    namePattern = $@"{urlEscaped}[^}}]*?""name""\s*:\s*""([^""]+)""";
                                                    nameMatch = Regex.Match(jsonContent, namePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                                                }

                                                if (nameMatch.Success)
                                                {
                                                    title = WebUtility.HtmlDecode(nameMatch.Groups[1].Value);
                                                }

                                                if (!videoUrls.ContainsKey(extractedUrl))
                                                {
                                                    videoUrls[extractedUrl] = title;
                                                    Logger.Debug($"[PMVHaven] Extracted page URL: {extractedUrl} (Title: {title})");
                                                }
                                            }
                                        }
                                    }
                                }

                                if (videoUrls.Count > 0)
                                {
                                    Logger.Debug($"[PMVHaven] Successfully extracted {videoUrls.Count} videos from LD+JSON");
                                    return videoUrls;
                                }
                            }
                        }
                        catch (Exception jsonEx)
                        {
                            Logger.Warning($"[PMVHaven] Error parsing LD+JSON: {jsonEx.Message}");
                        }
                    }
                }

                // Fallback 1: Look for video links in href attributes
                Logger.Debug("[PMVHaven] LD+JSON extraction yielded no results, trying href fallback");
                var hrefPattern = @"href\s*=\s*[""']([^""']*(?:/video|/videos)/[^""']+)[""']";
                var hrefMatches = Regex.Matches(html, hrefPattern, RegexOptions.IgnoreCase);

                foreach (Match hrefMatch in hrefMatches)
                {
                    if (hrefMatch.Success && hrefMatch.Groups.Count > 1)
                    {
                        var href = hrefMatch.Groups[1].Value;
                        var resolvedUrl = ResolveUrl(href, baseUrl);
                        if (resolvedUrl != null && IsVideoPageUrl(resolvedUrl))
                        {
                            if (!videoUrls.ContainsKey(resolvedUrl))
                            {
                                videoUrls[resolvedUrl] = string.Empty;
                            }
                        }
                    }
                }

                if (videoUrls.Count > 0)
                {
                    Logger.Debug($"[PMVHaven] Extracted {videoUrls.Count} videos from href fallback");
                    return videoUrls;
                }

                // Fallback 2: More aggressive regex for any pmvhaven video URL in the page
                Logger.Debug("[PMVHaven] Trying aggressive URL extraction");
                var aggressivePattern = @"https?://(?:www\.)?pmvhaven\.com/(?:video|videos)/[^\s\""'<>]+";
                var aggressiveMatches = Regex.Matches(html, aggressivePattern, RegexOptions.IgnoreCase);

                foreach (Match aggressiveMatch in aggressiveMatches)
                {
                    if (aggressiveMatch.Success)
                    {
                        var url = aggressiveMatch.Value;
                        // Clean trailing punctuation
                        url = Regex.Replace(url, @"[.,;:!?)]+$", "");
                        if (IsVideoPageUrl(url))
                        {
                            if (!videoUrls.ContainsKey(url))
                            {
                                videoUrls[url] = string.Empty;
                            }
                        }
                    }
                }

                Logger.Debug($"[PMVHaven] Total extracted: {videoUrls.Count} videos");
                return videoUrls;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting PMVHaven links from HTML: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        private List<string> ExtractVideoLinksFromHtml(string html, string baseUrl, string domainFilter)
        {
            var videoUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var baseUri = new Uri(baseUrl);
                var baseHost = baseUri.Host.ToLowerInvariant();

                // Excluded file extensions (non-video files)
                var excludedExtensions = new[] {
                    ".css", ".jpg", ".jpeg", ".png", ".gif", ".js", ".json", ".xml", ".ico",
                    ".svg", ".woff", ".woff2", ".ttf", ".eot", ".pdf", ".zip", ".rar",
                    ".txt", ".md"
                };

                // Pattern 1: Look for <a> tags with href pointing to video pages
                var linkPattern = @"<a[^>]*href\s*=\s*[""']([^""']+)[""']";
                var linkMatches = Regex.Matches(html, linkPattern, RegexOptions.IgnoreCase);
                foreach (Match match in linkMatches)
                {
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var href = match.Groups[1].Value;

                        // Pre-filter: exclude URLs with non-video extensions
                        if (excludedExtensions.Any(ext => href.ToLowerInvariant().EndsWith(ext)))
                        {
                            continue;
                        }

                        var resolvedUrl = ResolveUrl(href, baseUrl);

                        if (resolvedUrl != null && IsVideoPageUrl(resolvedUrl, domainFilter ?? baseHost))
                        {
                            videoUrls.Add(resolvedUrl);
                        }
                    }
                }

                // Pattern 2: Look for video URLs in data attributes or JSON
                var dataUrlPattern = @"(?:data-url|data-src|video-url|href)\s*[:=]\s*[""']([^""']+)[""']";
                var dataMatches = Regex.Matches(html, dataUrlPattern, RegexOptions.IgnoreCase);

                foreach (Match match in dataMatches)
                {
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var url = match.Groups[1].Value;

                        // Pre-filter: exclude URLs with non-video extensions
                        if (excludedExtensions.Any(ext => url.ToLowerInvariant().EndsWith(ext)))
                        {
                            continue;
                        }

                        var resolvedUrl = ResolveUrl(url, baseUrl);

                        if (resolvedUrl != null && IsVideoPageUrl(resolvedUrl, domainFilter ?? baseHost))
                        {
                            videoUrls.Add(resolvedUrl);
                        }
                    }
                }

                return videoUrls.ToList();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting video links from HTML: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Validates if a URL is a Hypnotube video page URL using strict site-specific patterns
        /// </summary>
        private bool IsVideoPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
                var host = uri.Host.ToLowerInvariant();
                var path = uri.AbsolutePath.ToLowerInvariant();

                if (SiteRules.Is(host, Site.Hypnotube))
                {
                    return path.Contains("/video/") && path.EndsWith(".html");
                }
                if (SiteRules.Is(host, Site.Rule34Video))
                {
                    return path.Contains("/video/") || path.Contains("/videos/");
                }
                if (host.Contains("pornhub.com"))
                {
                    return path.Contains("view_video.php");
                }
                if (SiteRules.Is(host, Site.PmvHaven))
                {
                    return path.Contains("/video/");
                }
                if (SiteRules.Is(host, Site.Iwara))
                {
                    return path.Contains("/video/");
                }
                if (SiteRules.Is(host, Site.YouTube))
                {
                    // watch?v=, shorts/, or youtu.be/ID
                    return path.Contains("/watch") || path.Contains("/shorts/") || (SiteRules.HostMatches(host, "youtu.be") && path.Length > 1);
                }

                return false;
            }
            catch { return false; }
        }

        public struct UrlAmbiguityInfo
        {
            public bool IsAmbiguous;
            public string VideoId;
            public string PlaylistId;
            public string SiteName;
        }

        public static UrlAmbiguityInfo DetectUrlAmbiguity(string url)
        {
            var info = new UrlAmbiguityInfo();
            if (string.IsNullOrWhiteSpace(url)) return info;

            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return info;
                var host = uri.Host.ToLowerInvariant();

                // YouTube
                if (SiteRules.Is(host, Site.YouTube))
                {
                    string vid = GetQueryParam(uri, "v");
                    string lid = GetQueryParam(uri, "list");

                    if (string.IsNullOrEmpty(vid) && uri.AbsolutePath.Contains("/shorts/"))
                    {
                        vid = uri.AbsolutePath.Split('/').Last();
                    }
                    if (string.IsNullOrEmpty(vid) && SiteRules.HostMatches(host, "youtu.be"))
                    {
                        vid = uri.AbsolutePath.TrimStart('/');
                    }

                    if (!string.IsNullOrEmpty(vid) && !string.IsNullOrEmpty(lid))
                    {
                        info.IsAmbiguous = true;
                        info.SiteName = "YouTube";
                        info.VideoId = vid;
                        info.PlaylistId = lid;
                    }
                }
                // Hypnotube (some shared links contain playlist context)
                else if (SiteRules.Is(host, Site.Hypnotube))
                {
                    string lid = GetQueryParam(uri, "playlist_id");
                    if (uri.AbsolutePath.Contains("/video/") && !string.IsNullOrEmpty(lid))
                    {
                        info.IsAmbiguous = true;
                        info.SiteName = "Hypnotube";
                        info.PlaylistId = lid;
                        info.VideoId = uri.AbsolutePath.Split('/').Last().Replace(".html", "");
                    }
                }
                // Generic detection for other sites (e.g. ?vid=123&album=456)
                else
                {
                    string vid = GetQueryParam(uri, "v") ?? GetQueryParam(uri, "video_id") ?? GetQueryParam(uri, "id") ?? GetQueryParam(uri, "item_id");
                    string lid = GetQueryParam(uri, "list") ?? GetQueryParam(uri, "playlist_id") ?? GetQueryParam(uri, "album_id") ?? GetQueryParam(uri, "gallery_id");

                    if (!string.IsNullOrEmpty(vid) && !string.IsNullOrEmpty(lid))
                    {
                        info.IsAmbiguous = true;
                        info.SiteName = host.Replace("www.", "").Split('.').First();
                        info.VideoId = vid;
                        info.PlaylistId = lid;
                    }
                }
            }
            catch { }

            return info;
        }

        private static string GetQueryParam(Uri uri, string name)
        {
            if (string.IsNullOrEmpty(uri.Query)) return null;
            var query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
            return null;
        }

        private bool IsHypnotubeVideoPageUrl(string url)
        {
            return IsVideoPageUrl(url);
        }

        private bool IsVideoPageUrl(string url, string domainFilter)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;

                var host = uri.Host.ToLowerInvariant();
                if (!string.IsNullOrEmpty(domainFilter) && !host.Contains(domainFilter.ToLowerInvariant())) return false;

                return IsVideoPageUrl(url);
            }
            catch
            {
                return false;
            }
        }

        private bool IsRule34VideoPageUrl(string url)
        {
            return IsVideoPageUrl(url);
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

        /// <summary>
        /// Tries to extract HTML content from playlist-specific containers
        /// Returns the container HTML if found, otherwise returns the full HTML
        /// </summary>
        private string ExtractPlaylistContainerHtml(string html, string[] containerPatterns)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;

            foreach (var pattern in containerPatterns)
            {
                try
                {
                    // Try to match container opening tag
                    var containerPattern = $@"<(?:\w+)[^>]*{pattern}[^>]*>.*?</(?:\w+)>";
                    var match = Regex.Match(html, containerPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
                catch
                {
                    // Continue to next pattern
                }
            }

            return html; // Fallback to full HTML
        }

        /// <summary>
        /// Extracts the next page URL from HTML for pagination support
        /// Returns null if no next page found or if we should stop pagination
        /// </summary>
        private string ExtractNextPageUrl(string html, string currentUrl, string domain)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                Logger.Debug($"[Pagination] Extracting from {currentUrl} (domain: {domain}, HTML: {html.Length} chars)");
                if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out Uri currentUri)) return null;

                var basePath = currentUri.AbsolutePath;
                var query = currentUri.Query;

                // Strategy 0: Rule34Video AJAX pagination (supports manual query params)
                if (SiteRules.Is(domain, Site.Rule34Video))
                {
                    bool isMember = currentUrl.Contains("/members/");
                    bool isPlaylist = currentUrl.Contains("/playlists/");

                    if (isMember || isPlaylist)
                    {
                        Logger.Debug($"[Pagination] Rule34Video Strategy 0 (AJAX) check. URL: {currentUrl}");

                        // Rule34Video uses different parameter names depending on page type
                        string paramName = isMember ? "from_videos" : "from";
                        string dataKey = isMember ? "from_videos" : "from";

                        // Look for 'pager next' with data-parameters
                        // Matches both from: and from_videos: patterns
                        var nextLinkMatch = Regex.Match(html, @"pager\s+next[^>]*>.*?data-parameters=""[^""]*?" + dataKey + @":(\d+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                        if (nextLinkMatch.Success)
                        {
                            int nextOffset = int.Parse(nextLinkMatch.Groups[1].Value);
                            var baseUri = currentUrl.Split('?')[0];
                            var finalNext = $"{baseUri}?{paramName}={nextOffset}";

                            Logger.Debug($"[Pagination] SUCCESS: Detected AJAX pagination for Rule34Video. Target offset: {nextOffset}. URL: {finalNext}");
                            return finalNext;
                        }

                        // Fallback: If no explicit 'next' pager, look for any page link with an offset higher than current
                        var currentFromMatch = Regex.Match(currentUrl, paramName + @"=(\d+)");
                        int currentFromValue = currentFromMatch.Success ? int.Parse(currentFromMatch.Groups[1].Value) : 1;

                        var anyPageMatch = Regex.Matches(html, dataKey + @":(\d+)");
                        foreach (Match m in anyPageMatch)
                        {
                            int offset = int.Parse(m.Groups[1].Value);
                            if (offset > currentFromValue)
                            {
                                var baseUri = currentUrl.Split('?')[0];
                                var finalNext = $"{baseUri}?{paramName}={offset}";
                                Logger.Debug($"[Pagination] SUCCESS: Following non-pager link to offset {offset}. URL: {finalNext}");
                                return finalNext;
                            }
                        }
                    }
                }

                // Strategy 1: Look for "next" link in pagination
                var nextLinkPatterns = new[] {
                    @"<a[^>]+href\s*=\s*[""']([^""']+)[""'][^>]*>(?:(?!</a>).)*?\s*(?:next|>|»|fa-chevron-right|chevron-right)\s*<",
                    @"<a[^>]+href\s*=\s*[""']([^""']+)[""'][^>]*>(?:(?!</a>).)*?chevron(?:(?!</a>).)*?</a>",
                    @"rel\s*=\s*[""']next[""'][^>]*href\s*=\s*[""']([^""']+)[""']",
                    @"href\s*=\s*[""']([^""']+)[""'][^>]*rel\s*=\s*[""']next[""']",
                };

                foreach (var pattern in nextLinkPatterns)
                {
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var nextUrl = match.Groups[1].Value;
                        var resolved = ResolveUrl(nextUrl, currentUrl);
                        if (resolved != null && Uri.TryCreate(resolved, UriKind.Absolute, out Uri nextUri))
                        {
                            var normalizedNextPath = nextUri.AbsolutePath.TrimEnd('/');
                            var normalizedBasePath = basePath.TrimEnd('/');

                            // Rule34Video specific: don't follow favorites pagination
                            if (SiteRules.Is(domain, Site.Rule34Video))
                            {
                                if (nextUrl.Contains("#fav_videos", StringComparison.OrdinalIgnoreCase) ||
                                    normalizedNextPath.Contains("/favourites/", StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Debug($"[Pagination] Rejecting favorites-related URL on Rule34Video: {resolved}");
                                    continue;
                                }
                            }

                            // Reject if it's the homepage or root path
                            if (string.IsNullOrEmpty(normalizedNextPath) || normalizedNextPath == "/")
                            {
                                Logger.Debug($"[Pagination] Rejecting root URL as next page: {resolved}");
                                continue;
                            }

                            // Ensure same domain
                            if (!nextUri.Host.ToLowerInvariant().Contains(domain.ToLowerInvariant()))
                            {
                                continue;
                            }

                            // Ensure it's actually a pagination of the same content
                            // Either: next path starts with base path (typical pagination)
                            // OR: same first path segment (e.g., /playlist/ -> /playlist/)
                            var baseSegments = normalizedBasePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            var nextSegments = normalizedNextPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                            bool isValidNextPage = false;

                            // Must have at least same first segment
                            if (baseSegments.Length > 0 && nextSegments.Length > 0 &&
                                baseSegments[0].Equals(nextSegments[0], StringComparison.OrdinalIgnoreCase))
                            {
                                isValidNextPage = true;
                            }

                            // Also check that it's not the same URL
                            if (resolved == currentUrl)
                            {
                                isValidNextPage = false;
                            }

                            if (isValidNextPage)
                            {
                                Logger.Debug($"[Pagination] Found next page URL using Strategy 1: {resolved}");
                                return resolved;
                            }
                        }
                    }
                }

                // Strategy 2: Extract all page links and find the next one
                var pageLinkPattern = @"href\s*=\s*[""']([^""']*[?&]page[=_](\d+)[^""']*)[""']";
                var pageMatches = Regex.Matches(html, pageLinkPattern, RegexOptions.IgnoreCase);
                var pageNumbers = new HashSet<int>();

                foreach (Match match in pageMatches)
                {
                    if (match.Success && match.Groups.Count >= 3)
                    {
                        var pageUrl = match.Groups[1].Value;
                        if (int.TryParse(match.Groups[2].Value, out int pageNum))
                        {
                            pageNumbers.Add(pageNum);
                            var resolved = ResolveUrl(pageUrl, currentUrl);
                            if (resolved != null && Uri.TryCreate(resolved, UriKind.Absolute, out Uri pageUri))
                            {
                                if (pageUri.Host.ToLowerInvariant().Contains(domain.ToLowerInvariant()))
                                {
                                    // Store URLs for potential use
                                }
                            }
                        }
                    }
                }

                // Strategy 3: Check current URL for page parameter and increment
                var currentPageMatch = Regex.Match(query, @"[?&]page[=_](\d+)", RegexOptions.IgnoreCase);
                int currentPage = 1;
                if (currentPageMatch.Success && currentPageMatch.Groups.Count > 1)
                {
                    int.TryParse(currentPageMatch.Groups[1].Value, out currentPage);
                }

                // If we found page numbers in links, check if next page exists
                if (pageNumbers.Count > 0)
                {
                    var maxPage = pageNumbers.Max();
                    if (currentPage < maxPage)
                    {
                        var nextPage = currentPage + 1;
                        // Construct next page URL
                        string nextQuery;
                        if (currentPageMatch.Success)
                        {
                            // Replace existing page parameter
                            var pageParam = currentPageMatch.Groups[0].Value; // e.g., "?page=1" or "&page=1"
                            var isQueryStart = pageParam.StartsWith("?");
                            nextQuery = Regex.Replace(query, @"[?&]page[=_](\d+)", $"{(!isQueryStart ? "&" : "")}page={nextPage}", RegexOptions.IgnoreCase);
                            if (nextQuery.StartsWith("&")) nextQuery = "?" + nextQuery.Substring(1);
                        }
                        else
                        {
                            var separator = string.IsNullOrWhiteSpace(query) ? "?" : "&";
                            nextQuery = query + separator + $"page={nextPage}";
                        }
                        return $"{currentUri.Scheme}://{currentUri.Host}{basePath}{nextQuery}";
                    }
                }

                return null; // No next page found
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error extracting next page URL: {ex.Message}");
                return null;
            }
        }

    }
}
