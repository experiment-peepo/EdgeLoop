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

namespace EdgeLoop.Classes {
    /// <summary>
    /// Service for extracting direct video URLs from page URLs
    /// </summary>
    public class VideoUrlExtractor : IVideoUrlExtractor {
        private readonly IHtmlFetcher _htmlFetcher;
        // private readonly LruCache<string, string> _urlCache; // Replaced by PersistentUrlCache
        private readonly YtDlpService _ytDlpService;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _activeExtractions = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>>();

        public VideoUrlExtractor(IHtmlFetcher htmlFetcher = null, YtDlpService ytDlpService = null) {
            _htmlFetcher = htmlFetcher ?? new StandardHtmlFetcher();
            _ytDlpService = ytDlpService ?? (ServiceContainer.TryGet<YtDlpService>(out var service) ? service : null);
            // _urlCache = new LruCache<string, string>(Constants.MaxFileCacheSize, ttl);
        }

        public virtual async Task<VideoMetadata> ExtractVideoMetadataAsync(string pageUrl, CancellationToken cancellationToken = default) {
            if (string.IsNullOrWhiteSpace(pageUrl)) return new VideoMetadata();
            
            var normalizedUrl = FileValidator.NormalizeUrl(pageUrl);
            var uri = new Uri(normalizedUrl);
            var host = uri.Host.ToLowerInvariant();
            
            if (Constants.VideoExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) {
                return new VideoMetadata(normalizedUrl, Path.GetFileNameWithoutExtension(uri.AbsolutePath), normalizedUrl);
            }

            // Check Persistent Cache
            var cachedUrl = PersistentUrlCache.Instance.Get(pageUrl);
            if (!string.IsNullOrEmpty(cachedUrl)) {
                 // We have the URL, but maybe not the title. 
                 // Optimization: If we have a cached URL, return it immediately with a placeholder or cached title if we had it.
                 // For now, we accept fetching HTML just for title if needed, OR we could skip title fetch if performance is key.
                 // Let's assume title is less critical or we can extract it from the cached URL if possible.
                 // But wait, ExtractVideoMetadataAsync is often called when adding a NEW video.
                 // If we reload from session, we use ExtractVideoUrlAsync (JIT).
                 // So this method is mostly for "Add URL". We *do* want the title then.
                 // But if we already cached it, we might skip the heavy lifting of video extraction.
            }

            if (host.Contains("redgifs.com")) {
                Logger.Warning($"[VideoUrlExtractor] RedGifs support removed. Skipping: {normalizedUrl}");
                return new VideoMetadata();
            }

            try {
                // Fetch HTML but don't fail immediately, some sites block scrapers but work with yt-dlp
                var html = await FetchHtmlAsync(normalizedUrl, cancellationToken);
                bool hasHtml = !string.IsNullOrWhiteSpace(html);

                string videoUrl = null;
                string title = null;

                // Use yt-dlp for video metadata if site is supported by it and blocks scraping
                bool useYtDlpFirst = host.Contains("rule34video.com") || host.Contains("iwara.tv");

                if (useYtDlpFirst && _ytDlpService != null && _ytDlpService.IsAvailable) {
                    try {
                        Logger.Info($"[VideoUrlExtractor] Extracting full metadata using yt-dlp for {host}");
                        var info = await _ytDlpService.ExtractVideoInfoAsync(normalizedUrl, cancellationToken);
                        if (info != null) {
                            if (!string.IsNullOrWhiteSpace(info.Url)) videoUrl = info.Url;
                            if (!string.IsNullOrWhiteSpace(info.Title) && info.Title != "Unknown") title = info.Title;
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"[VideoUrlExtractor] yt-dlp full metadata extraction failed for {host}: {ex.Message}");
                    }
                }

                // If yt-dlp failed or was skipped, try getting the URL normally
                if (string.IsNullOrEmpty(videoUrl)) {
                    if (!string.IsNullOrEmpty(cachedUrl)) {
                        videoUrl = cachedUrl;
                    } else if (useYtDlpFirst) {
                        // We already tried ExtractVideoInfoAsync and it failed, but let's strictly fallback
                        if (host.Contains("rule34video.com")) {
                            videoUrl = await ExtractRule34VideoUrlAsync(normalizedUrl, cancellationToken);
                        } else {
                            if (hasHtml) videoUrl = await ExtractGenericVideoUrlAsync(normalizedUrl, cancellationToken);
                        }
                    } else if (host.Contains("pmvhaven.com")) {
                        videoUrl = await ExtractPmvHavenUrlAsync(normalizedUrl, cancellationToken);
                    } else if (host.Contains("hypnotube.com")) {
                        videoUrl = await ExtractHypnotubeUrlAsync(normalizedUrl, cancellationToken);
                    } else {
                        if (hasHtml) videoUrl = await ExtractGenericVideoUrlAsync(normalizedUrl, cancellationToken);
                    }
                }

                if (string.IsNullOrEmpty(title) && hasHtml) title = ExtractTitleFromHtml(html, host);
                
                if (videoUrl != null) {
                    PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                }

                return new VideoMetadata(videoUrl, title, normalizedUrl);
            } catch (Exception ex) {
                Logger.Error($"Error extracting metadata from {pageUrl}", ex);
                return new VideoMetadata();
            }
        }
        public virtual async Task<string> ExtractVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default) {
            if (string.IsNullOrWhiteSpace(pageUrl)) return null;

            try {
                // Coalesce multiple simultaneous requests for the same URL 
                // using a Task-based dictionary.
                Task<string> extractionTask;
                lock (_activeExtractions) {
                    if (!_activeExtractions.TryGetValue(pageUrl, out extractionTask) || extractionTask.IsFaulted || extractionTask.IsCanceled) {
                        extractionTask = ExtractVideoUrlInternalAsync(pageUrl, cancellationToken);
                        _activeExtractions[pageUrl] = extractionTask;
                        
                        // Self-cleanup after completion
                        extractionTask.ContinueWith(t => {
                            _activeExtractions.TryRemove(pageUrl, out _);
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                return await extractionTask;
            } catch {
                return null;
            }
        }

        private async Task<string> ExtractVideoUrlInternalAsync(string pageUrl, CancellationToken cancellationToken) {
            // Re-check cache inside the task just in case it was updated while waiting in GetOrAdd
            var cachedUrl = PersistentUrlCache.Instance.Get(pageUrl);
            if (!string.IsNullOrEmpty(cachedUrl)) return cachedUrl;

            try {
                var normalizedUrl = FileValidator.NormalizeUrl(pageUrl);
                var uri = new Uri(normalizedUrl);
                var host = uri.Host.ToLowerInvariant();
                
                if (Constants.VideoExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) {
                    return normalizedUrl;
                }
                
                string videoUrl = null;
                
                var hasSpecializedScraper = host.Contains("pmvhaven.com") || 
                                           host.Contains("hypnotube.com");
                
                // Explicitly block RedGifs support as requested
                if (host.Contains("redgifs.com")) {
                    Logger.Warning($"[VideoUrlExtractor] RedGifs support has been removed. Skipping: {normalizedUrl}");
                    return null;
                }

                var useYtDlp = !hasSpecializedScraper;

                if (_ytDlpService != null && _ytDlpService.IsAvailable && useYtDlp) {
                    try {
                        Logger.Info($"[VideoUrlExtractor] Attempting yt-dlp extraction for {normalizedUrl}");
                        videoUrl = await _ytDlpService.GetBestVideoUrlAsync(normalizedUrl, cancellationToken);
                        
                        if (!string.IsNullOrEmpty(videoUrl)) {
                            // Cache success
                            PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                            return videoUrl;
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"[VideoUrlExtractor] yt-dlp extraction failed: {ex.Message}, falling back to scraping");
                    }
                } else if (hasSpecializedScraper) {
                    Logger.Info($"[VideoUrlExtractor] Using specialized scraper for {host}");
                }
                
                if (host.Contains("hypnotube.com")) {
                    videoUrl = await ExtractHypnotubeUrlAsync(normalizedUrl, cancellationToken);
                } else if (host.Contains("pmvhaven.com")) {
                    videoUrl = await ExtractPmvHavenUrlAsync(normalizedUrl, cancellationToken);
                } else if (host.Contains("rule34video.com")) {
                    videoUrl = await ExtractRule34VideoUrlAsync(normalizedUrl, cancellationToken);
                } else {
                    videoUrl = await ExtractGenericVideoUrlAsync(normalizedUrl, cancellationToken);
                }

                if (videoUrl != null) {
                    PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                } else if (_ytDlpService != null && _ytDlpService.IsAvailable) {
                    // FALLBACK: If specialized scrapers failed, try yt-dlp as a last resort
                    try {
                        Logger.Info($"[VideoUrlExtractor] Specialized scraper for {host} failed, falling back to yt-dlp");
                        videoUrl = await _ytDlpService.GetBestVideoUrlAsync(normalizedUrl, cancellationToken);
                        if (videoUrl != null) {
                            PersistentUrlCache.Instance.Set(pageUrl, videoUrl);
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"[VideoUrlExtractor] yt-dlp fallback failing for {host}: {ex.Message}");
                    }
                }

                return videoUrl;
            } catch (Exception ex) {
                Logger.Error($"Error extracting video URL from {pageUrl}", ex);
                return null;
            }
        }

        private async Task<string> ExtractHypnotubeUrlAsync(string url, CancellationToken cancellationToken) {
            try {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return null;

                Logger.Info($"ExtractHypnotubeUrlAsync: Processing HTML from {url}");

                // 1. Try multi-source extraction (Higher Priority)
                Logger.Info("Hypnotube: Trying multi-source extraction");
                var sources = StashPatternExtractor.ExtractAllVideoSources(html, url);
                if (sources.Any()) {
                    var best = QualitySelector.SelectBest(sources);
                    if (best != null) {
                        Logger.Info($"Hypnotube: Selected quality {best.Label} from {sources.Count} sources");
                        return ResolveUrl(best.Url, url);
                    }
                }

                // 2. Try Open Graph video (fallback if multi-source didn't find anything)
                Logger.Info("Hypnotube: Trying og:video extraction");
                var ogVideo = StashPatternExtractor.ExtractOgVideo(html);
                if (!string.IsNullOrEmpty(ogVideo)) {
                    Logger.Info($"Hypnotube: Found og:video URL");
                    return ResolveUrl(ogVideo, url);
                }

                // 3. Try generic extraction (Method 4 handles JS variables with extensions)
                Logger.Info("Hypnotube: Trying generic extraction");
                var videoUrl = ExtractVideoFromHtml(html, url);
                if (videoUrl != null) return videoUrl;

                // 4. Stricter site-specific fallback: only match scripts/configs if they hint at a video extension
                var playerPattern = @"(?:video_url|file|src)\s*[:=]\s*[""']([^""']+\.(?:mp4|webm|mkv|m3u8)(?:\?[^""']*)?)[""']";
                var match = Regex.Match(html, playerPattern, RegexOptions.IgnoreCase);
                if (match.Success) {
                    var resolved = ResolveUrl(match.Groups[1].Value, url);
                    if (HasVideoExtension(resolved)) {
                        Logger.Info($"Hypnotube: Found video URL in player config fallback: {resolved}");
                        return resolved;
                    }
                }

                return null;
            } catch (Exception ex) {
                Logger.Warning($"Error extracting Hypnotube URL: {ex.Message}");
                return null;
            }
        }



        private async Task<string> ExtractRule34VideoUrlAsync(string url, CancellationToken cancellationToken) {
            try {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) {
                    Logger.Warning($"Rule34Video: Failed to fetch HTML for {url}");
                    return null;
                }

                Logger.Info($"ExtractRule34VideoUrlAsync: Starting specialized extraction for {url} ({html.Length} chars)");

                // 1. Extract tokens (rnd, license_code, etc)
                var tokens = new Dictionary<string, string>();
                var tokenPatterns = new[] { 
                    @"\brnd\s*[:=]\s*['""](\d+)['""]",
                    @"\blicense_code\s*[:=]\s*['""]([^'""]+)['""]",
                    @"\bvid_id\s*[:=]\s*['""](\d+)['""]"
                };

                foreach (var pattern in tokenPatterns) {
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success) {
                        string key = match.Value.Split(new[] { ':', '=' })[0].Trim();
                        tokens[key] = match.Groups[1].Value;
                        Logger.Info($"Rule34Video: Found token {key}: {tokens[key]}");
                    }
                }

                // 2. PRIORITY 1: Search for direct download links in the HTML
                // These are often the most reliable as they are what the user sees
                var getFilePattern = @"href=[""']([^""']*\/get_file\/[^""']+\.mp4\/?(?:[^""']*)?)[""']";
                var getFileMatches = Regex.Matches(html, getFilePattern, RegexOptions.IgnoreCase);
                
                string bestFoundUrl = null;
                int bestFoundQuality = 0;
                
                foreach (Match linkMatch in getFileMatches) {
                    var link = linkMatch.Groups[1].Value;
                    int quality = 360; // Default
                    if (link.Contains("1080p")) quality = 1080;
                    else if (link.Contains("720p")) quality = 720;
                    else if (link.Contains("480p")) quality = 480;
                    else if (link.Contains("2160p") || link.Contains("4k")) quality = 2160;
                    
                    if (quality > bestFoundQuality) {
                        bestFoundQuality = quality;
                        bestFoundUrl = ResolveUrl(link, url);
                    }
                }

                if (bestFoundUrl != null) {
                    Logger.Info($"Rule34Video: Found direct get_file link with {bestFoundQuality}p quality: {bestFoundUrl}");
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

                foreach (var (key, quality) in priorities) {
                    var pattern = $@"\b{key}\s*[:=]\s*['""]([^'""]+)['""]";
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups.Count > 1) {
                        var rawUrl = match.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(rawUrl) && rawUrl.Contains("mp4")) {
                            bestUrl = CleanExtractedUrl(rawUrl);
                            selectedQuality = quality;
                            Logger.Info($"Rule34Video: Found {quality}p quality from {key} variable");
                            break; 
                        }
                    }
                }

                if (bestUrl != null) {
                    bestUrl = ResolveUrl(bestUrl, url);
                    
                    // Append tokens if not present
                    foreach (var token in tokens) {
                        if (!bestUrl.Contains($"{token.Key}=")) {
                            var separator = bestUrl.Contains("?") ? "&" : "?";
                            bestUrl += $"{separator}{token.Key}={token.Value}";
                        }
                    }
                    
                    Logger.Info($"Rule34Video: Resolving player variable URL: {bestUrl}");
                    var resolvedUrl = await _htmlFetcher.ResolveRedirectUrlAsync(bestUrl, url, cancellationToken);
                    return resolvedUrl ?? bestUrl;
                }

                Logger.Warning("Rule34Video: specialized extraction failed, falling back to generic");
                return await ExtractGenericVideoUrlAsync(url, cancellationToken);
            } catch (Exception ex) {
                Logger.Warning($"Error extracting RULE34Video URL: {ex.Message}");
                return null;
            }
        }

        private async Task<string> ExtractPmvHavenUrlAsync(string url, CancellationToken cancellationToken) {
            try {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return null;

                // 1. Try JSON-LD contentUrl (Highest Priority for PMVHaven)
                // Look for "contentUrl":"...m3u8"
                var jsonLdPattern = @"""contentUrl""\s*:\s*[""']([^""']+\.m3u8(?:[^""']*)?)[""']";
                var jsonLdMatch = Regex.Match(html, jsonLdPattern, RegexOptions.IgnoreCase);
                if (jsonLdMatch.Success && jsonLdMatch.Groups.Count > 1) {
                    var hlsUrl = CleanExtractedUrl(jsonLdMatch.Groups[1].Value);
                    Logger.Info($"PMVHaven: Found HLS URL in JSON-LD: {hlsUrl}");
                    return ResolveUrl(hlsUrl, url);
                }

                // 2. Try generic HLS (.m3u8) patterns
                var hlsPattern = @"[""']([^""']+\.m3u8(?:[^""']*)?)[""']";
                var hlsMatches = Regex.Matches(html, hlsPattern, RegexOptions.IgnoreCase);
                foreach (Match match in hlsMatches) {
                     if (match.Success && match.Groups.Count > 1) {
                        var hlsUrl = CleanExtractedUrl(match.Groups[1].Value);
                        // Filter out common false positives if necessary, but m3u8 is usually good
                         if (!hlsUrl.Contains("preview", StringComparison.OrdinalIgnoreCase)) {
                             Logger.Info($"PMVHaven: Found HLS URL in HTML: {hlsUrl}");
                             return ResolveUrl(hlsUrl, url);
                         }
                     }
                }

                // 3. Try og:video (often highest quality direct file if not HLS)
                var ogVideo = StashPatternExtractor.ExtractOgVideo(html);
                if (!string.IsNullOrEmpty(ogVideo)) {
                    Logger.Info("PMVHaven: Found og:video URL (likely highest quality)");
                    return ResolveUrl(ogVideo, url);
                }

                // 4. Try multi-source extraction with quality selection
                var sources = StashPatternExtractor.ExtractAllVideoSources(html, url);
                if (sources.Any()) {
                    var best = QualitySelector.SelectBest(sources);
                    if (best != null) {
                        Logger.Info($"PMVHaven: Selected {best.Label} quality from {sources.Count} sources");
                        return ResolveUrl(best.Url, url);
                    }
                }

                // 5. Fallback to generic extraction
                // Explicitly warn or filter if we think generic might pick a preview
                var videoUrl = ExtractVideoFromHtml(html, url);
                return videoUrl;
            } catch (Exception ex) {
                Logger.Warning($"Error extracting PMVHaven URL: {ex.Message}");
                return null;
            }
        }


        private async Task<string> ExtractGenericVideoUrlAsync(string url, CancellationToken cancellationToken) {
            try {
                var html = await FetchHtmlAsync(url, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) return null;

                var videoUrl = ExtractVideoFromHtml(html, url);
                return videoUrl;
            } catch (Exception ex) {
                Logger.Warning($"Error extracting generic video URL: {ex.Message}");
                return null;
            }
        }

        private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken) {
            return await FetchHtmlWithRetryAsync(url, cancellationToken);
        }

        /// <summary>
        /// Fetches HTML with retry logic for transient failures
        /// </summary>
        private async Task<string> FetchHtmlWithRetryAsync(string url, CancellationToken cancellationToken, int maxRetries = 2) {
            for (int i = 0; i <= maxRetries; i++) {
                try {
                    var result = await _htmlFetcher.FetchHtmlAsync(url, cancellationToken);
                    if (!string.IsNullOrEmpty(result)) {
                        return result;
                    }
                    // If result is empty/null but no exception, still retry
                    if (i < maxRetries) {
                        Logger.Info($"[VideoUrlExtractor] Empty response for {url}, retrying ({i + 1}/{maxRetries})...");
                        await Task.Delay(500 * (i + 1), cancellationToken);
                    }
                } catch (HttpRequestException ex) when (i < maxRetries) {
                    Logger.Info($"[VideoUrlExtractor] HTTP error for {url}: {ex.Message}, retrying ({i + 1}/{maxRetries})...");
                    await Task.Delay(500 * (i + 1), cancellationToken); // Exponential backoff
                } catch (TaskCanceledException) {
                    throw; // Don't retry on cancellation
                } catch (Exception ex) when (i < maxRetries) {
                    Logger.Warning($"[VideoUrlExtractor] Error fetching {url}: {ex.Message}, retrying ({i + 1}/{maxRetries})...");
                    await Task.Delay(500 * (i + 1), cancellationToken);
                }
            }
            return null;
        }

        private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(5);

        private string ExtractVideoFromHtml(string html, string baseUrl) {
            if (string.IsNullOrWhiteSpace(html)) return null;

            Logger.Info($"ExtractVideoFromHtml: Processing {html.Length} characters of HTML from {baseUrl}");

            try {
                // Method 1: Look for <video> tags with src attribute
                Logger.Info("Trying Method 1: <video> tag with src attribute");
                var videoSrcPattern = @"<video[^>]+src\s*=\s*[""']([^""']+)[""']";
                var match = Regex.Match(html, videoSrcPattern, RegexOptions.IgnoreCase, _regexTimeout);
                if (match.Success && match.Groups.Count > 1) {
                    var videoUrl = match.Groups[1].Value;
                    var resolved = ResolveUrl(videoUrl, baseUrl);
                    Logger.Info($"Method 1: Found video URL: {resolved}");
                    return resolved;
                }

                // Method 2: Look for <source> tags within video elements
                Logger.Info("Trying Method 2: <source> tags");
                var sourcePattern = @"<source[^>]+src\s*=\s*[""']([^""']+)[""']";
                var sourceMatches = Regex.Matches(html, sourcePattern, RegexOptions.IgnoreCase, _regexTimeout);
                Logger.Info($"Method 2: Found {sourceMatches.Count} <source> tags");
                foreach (Match sourceMatch in sourceMatches) {
                    if (sourceMatch.Success && sourceMatch.Groups.Count > 1) {
                        var videoUrl = CleanExtractedUrl(sourceMatch.Groups[1].Value);
                        // Check for video extensions, allowing for query parameters
                        if (HasVideoExtension(videoUrl)) {
                            var resolved = ResolveUrl(videoUrl, baseUrl);
                            Logger.Info($"Method 2: Found video URL: {resolved}");
                            return resolved;
                        }
                    }
                }

                // Method 3: Look for data-url or data-src attributes
                Logger.Info("Trying Method 3: data attributes");
                var dataUrlPattern = @"(?:data-url|data-src|data-video-src|data-config)\s*=\s*[""']([^""']+)[""']";
                var dataMatches = Regex.Matches(html, dataUrlPattern, RegexOptions.IgnoreCase, _regexTimeout);
                foreach (Match dataMatch in dataMatches) {
                    if (dataMatch.Success && dataMatch.Groups.Count > 1) {
                        var url = CleanExtractedUrl(dataMatch.Groups[1].Value);
                        if (HasVideoExtension(url)) {
                            var resolved = ResolveUrl(url, baseUrl);
                            Logger.Info($"Method 3: Found video URL: {resolved}");
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

                foreach (var pattern in jsVideoPatterns) {
                    var jsMatches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase, _regexTimeout);
                    Logger.Info($"Method 4: Pattern {pattern} found {jsMatches.Count} potential matches");
                    foreach (Match jsMatch in jsMatches) {
                        if (jsMatch.Success && jsMatch.Groups.Count > 1) {
                            var videoUrl = CleanExtractedUrl(jsMatch.Groups[1].Value);
                            var resolvedUrl = ResolveUrl(videoUrl, baseUrl);
                            if (IsValidVideoUrl(resolvedUrl)) {
                                potentialUrls.Add(resolvedUrl);
                            }
                        }
                    }
                }

                if (potentialUrls.Any()) {
                    // Try to find the highest quality (often denoted by _1080p, _720p, etc. in the URL)
                    // Or just pick the last one if it's from variables like video_alt_url3
                    var bestUrl = potentialUrls.OrderByDescending(u => {
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

                    Logger.Info($"Method 4: Selected best video URL: {bestUrl}");
                    return bestUrl;
                }

                // Method 5: Look for HLS streams (m3u8)
                Logger.Info("Trying Method 5: HLS streams");
                var hlsPattern = @"[""']([^""']+\.m3u8(?:[^""']*)?)[""']";
                var hlsMatches = Regex.Matches(html, hlsPattern, RegexOptions.IgnoreCase, _regexTimeout);
                Logger.Info($"Method 5: Found {hlsMatches.Count} potential HLS streams");
                foreach (Match hlsMatch in hlsMatches) {
                    if (hlsMatch.Success && hlsMatch.Groups.Count > 1) {
                        var hlsUrl = CleanExtractedUrl(hlsMatch.Groups[1].Value);
                        var resolved = ResolveUrl(hlsUrl, baseUrl);
                        Logger.Info($"Method 5: Found HLS URL: {resolved}");
                        return resolved;
                    }
                }

                // Method 6: Look for Plyr source configuration
                Logger.Info("Trying Method 6: Plyr configuration");
                var plyrPattern = @"plyr_player\.source\s*=\s*\{[\s\S]*?sources\s*:\s*\[\s*\{\s*[""']?src[""']?\s*:\s*[""']([^""']+)[""']";
                var plyrMatch = Regex.Match(html, plyrPattern, RegexOptions.IgnoreCase, _regexTimeout);
                if (plyrMatch.Success && plyrMatch.Groups.Count > 1) {
                    var videoUrl = CleanExtractedUrl(plyrMatch.Groups[1].Value);
                    var resolved = ResolveUrl(videoUrl, baseUrl);
                    Logger.Info($"Method 6: Found Plyr URL: {resolved}");
                    return resolved;
                }

                // Method 7: Look for video URLs in JSON data
                Logger.Info("Trying Method 7: JSON data");
                var jsonVideoUrl = ExtractVideoFromJson(html, baseUrl);
                if (jsonVideoUrl != null) {
                    Logger.Info($"Method 7: Found JSON video URL: {jsonVideoUrl}");
                    return jsonVideoUrl;
                }

                Logger.Warning("ExtractVideoFromHtml: All extraction methods failed");
                return null;
            } catch (Exception ex) {
                Logger.Warning($"Error extracting video from HTML: {ex.Message}");
                return null;
            }
        }

        private bool HasVideoExtension(string url) {
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

        private string ExtractVideoFromJson(string html, string baseUrl) {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try {
                // Look for JSON-like structures that might contain video URLs
                // This version is more lenient with whitespace and escaped characters
                var jsonPattern = @"""(?:src|url|source|file|videoUrl)""\s*:\s*""([^""]+\.(?:mp4|webm|mkv|avi|mov|wmv|m4v|mpg|mpeg|ts|m2ts)[^""]*)""";
                var matches = Regex.Matches(html, jsonPattern, RegexOptions.IgnoreCase, _regexTimeout);
                
                foreach (Match match in matches) {
                    if (match.Success && match.Groups.Count > 1) {
                        var videoUrl = CleanExtractedUrl(match.Groups[1].Value);
                        
                        var resolvedUrl = ResolveUrl(videoUrl, baseUrl);
                        if (IsValidVideoUrl(resolvedUrl)) {
                            return resolvedUrl;
                        }
                    }
                }

                return null;
            } catch (Exception ex) {
                Logger.Warning($"Error extracting video from JSON: {ex.Message}");
                return null;
            }
        }

        private string CleanExtractedUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) return url;

            // Remove whitespace
            url = url.Trim();

            // Handle escaped slashes
            url = url.Replace("\\/", "/");

            // Some sites (like Rule34Video) prefix URLs with metadata like 'function/0/'
            // If the URL contains 'http' but not at the start, try to isolate the actual URL
            int httpIndex = url.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (httpIndex > 0) {
                var potentialUrl = url.Substring(httpIndex);
                // Check if the isolated part is a valid absolute URL
                if (Uri.TryCreate(potentialUrl, UriKind.Absolute, out _)) {
                    Logger.Info($"CleanExtractedUrl: Isolated URL from prefix: {potentialUrl} (original: {url})");
                    return potentialUrl;
                }
            }

            return url;
        }

        private string ResolveUrl(string url, string baseUrl) {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri absoluteUri)) {
                    return absoluteUri.ToString();
                }

                if (Uri.TryCreate(new Uri(baseUrl), url, out Uri resolvedUri)) {
                    return resolvedUri.ToString();
                }

                return url;
            } catch {
                return url;
            }
        }

        private bool IsValidVideoUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) return false;
            
            // Check if it's a valid URL with video extension
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) {
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
        public async Task<string> ExtractVideoTitleAsync(string pageUrl, CancellationToken cancellationToken = default) {
            if (string.IsNullOrWhiteSpace(pageUrl)) return null;

            try {
                var normalizedUrl = FileValidator.NormalizeUrl(pageUrl);
                var uri = new Uri(normalizedUrl);
                var host = uri.Host.ToLowerInvariant();

                // Fast path for sites that require CF bypass / yt-dlp to get title
                if (host.Contains("rule34video.com") || host.Contains("iwara.tv")) {
                    if (_ytDlpService != null && _ytDlpService.IsAvailable) {
                        try {
                            Logger.Info($"[VideoUrlExtractor] Extracting title using yt-dlp for {host}");
                            var info = await _ytDlpService.ExtractVideoInfoAsync(normalizedUrl, cancellationToken);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Title) && info.Title != "Unknown") {
                                return info.Title;
                            }
                        } catch (Exception ex) {
                            Logger.Warning($"[VideoUrlExtractor] yt-dlp title extraction failed for {host}: {ex.Message}");
                        }
                    }
                }

                var html = await FetchHtmlAsync(normalizedUrl, cancellationToken);
                if (string.IsNullOrWhiteSpace(html)) {
                    // Fallback to yt-dlp if HTML fetch failed and yt-dlp is available
                    if (_ytDlpService != null && _ytDlpService.IsAvailable) {
                        try {
                            Logger.Info($"[VideoUrlExtractor] HTML fetch failed, fallback to yt-dlp title extraction for {normalizedUrl}");
                            var info = await _ytDlpService.ExtractVideoInfoAsync(normalizedUrl, cancellationToken);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Title) && info.Title != "Unknown") {
                                return info.Title;
                            }
                        } catch { }
                    }
                    return null;
                }

                return ExtractTitleFromHtml(html, uri.Host);
            } catch (Exception ex) {
                Logger.Warning($"Error extracting video title from {pageUrl}: {ex.Message}");
                return null;
            }
        }

        private string ExtractTitleFromHtml(string html, string host) {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try {
                host = host.ToLowerInvariant();

                // Method 1: Try Open Graph meta tags
                try {
                    var ogTitle = ExtractMetaTag(html, "og:title");
                    if (!string.IsNullOrWhiteSpace(ogTitle)) {
                        var sanitized = SanitizeTitle(ogTitle);
                        if (!string.IsNullOrWhiteSpace(sanitized)) {
                            return sanitized;
                        }
                    }
                } catch { }

                // Method 2: Try site-specific title extraction
                if (host.Contains("hypnotube.com")) {
                    var hTitle = StashPatternExtractor.ExtractHypnotubeTitle(html);
                    if (!string.IsNullOrEmpty(hTitle)) return hTitle;
                }

                // Method 3: Try HTML title tag
                try {
                    var htmlTitle = ExtractHtmlTitle(html);
                    if (!string.IsNullOrWhiteSpace(htmlTitle)) {
                        var sanitized = SanitizeTitle(htmlTitle);
                        if (!string.IsNullOrWhiteSpace(sanitized)) {
                            return sanitized;
                        }
                    }
                } catch { }
#pragma warning disable CS0618
                // Method 4: Try page elements (site-specific)
                try {
                    // We need a dummy URL for site-specific logic if we don't have one, 
                    // but usually host is enough or we use generic elements
                    var elementTitle = ExtractTitleFromPageElements(html, host);
                    if (!string.IsNullOrWhiteSpace(elementTitle)) {
                        var sanitized = SanitizeTitle(elementTitle);
                        if (!string.IsNullOrWhiteSpace(sanitized)) {
                            return sanitized;
                        }
                    }
                } catch { }
#pragma warning restore CS0618
                return null;
            } catch (Exception ex) {
                Logger.Warning($"Error in ExtractTitleFromHtml: {ex.Message}");
                return null;
            }
        }

        private string ExtractMetaTag(string html, string propertyName) {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try {
                var pattern = $@"<meta\s+[^>]*(?:property|name)\s*=\s*[""']{Regex.Escape(propertyName)}[""'][^>]*content\s*=\s*[""']([^""']+)[""']";
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1) {
                    return WebUtility.HtmlDecode(match.Groups[1].Value);
                }

                // Alternative pattern: content before property/name
                pattern = $@"<meta\s+[^>]*content\s*=\s*[""']([^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']{Regex.Escape(propertyName)}[""']";
                match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1) {
                    return WebUtility.HtmlDecode(match.Groups[1].Value);
                }
            } catch (Exception ex) {
                Logger.Warning($"Error extracting meta tag {propertyName}: {ex.Message}");
            }

            return null;
        }

        private string ExtractHtmlTitle(string html) {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try {
                var titlePattern = @"<title[^>]*>([^<]+)</title>";
                var match = Regex.Match(html, titlePattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1) {
                    var title = WebUtility.HtmlDecode(match.Groups[1].Value);
                    
                    // Clean up common site suffixes
                    var suffixes = new[] { " - Hypnotube", " | Hypnotube", " - RULE34VIDEO", " | RULE34VIDEO", 
                                          " - PMVHaven", " | PMVHaven" };
                    foreach (var suffix in suffixes) {
                        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                            title = title.Substring(0, title.Length - suffix.Length).Trim();
                            break;
                        }
                    }
                    
                    return title;
                }
            } catch (Exception ex) {
                Logger.Warning($"Error extracting HTML title: {ex.Message}");
            }

            return null;
        }

        private string ExtractTitleFromPageElements(string html, string url) {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                // Site-specific extraction patterns
                if (host.Contains("hypnotube.com")) {
                    // Try h1 with video-title class or similar
                    var pattern = @"<h1[^>]*class\s*=\s*[""'][^""']*video[^""']*title[^""']*[""'][^>]*>([^<]+)</h1>";
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups.Count > 1) {
                        return WebUtility.HtmlDecode(match.Groups[1].Value);
                    }
                } else if (host.Contains("rule34video.com")) {
                    // Try common title patterns
                    var patterns = new[] {
                        @"<h1[^>]*>([^<]+)</h1>",
                        @"<div[^>]*class\s*=\s*[""'][^""']*title[^""']*[""'][^>]*>([^<]+)</div>"
                    };
                    foreach (var pattern in patterns) {
                        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups.Count > 1) {
                            var title = WebUtility.HtmlDecode(match.Groups[1].Value);
                            if (!string.IsNullOrWhiteSpace(title) && title.Length > 3) {
                                return title;
                            }
                        }
                    }
                } else if (host.Contains("pmvhaven.com")) {
                    // Try h1 or title div
                    var patterns = new[] {
                        @"<h1[^>]*>([^<]+)</h1>",
                        @"<div[^>]*class\s*=\s*[""'][^""']*video[^""']*title[^""']*[""'][^>]*>([^<]+)</div>"
                    };
                    foreach (var pattern in patterns) {
                        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups.Count > 1) {
                            var title = WebUtility.HtmlDecode(match.Groups[1].Value);
                            if (!string.IsNullOrWhiteSpace(title) && title.Length > 3) {
                                return title;
                            }
                        }
                    }
                }

                // Generic fallback: try first h1
                var genericPattern = @"<h1[^>]*>([^<]+)</h1>";
                var genericMatch = Regex.Match(html, genericPattern, RegexOptions.IgnoreCase);
                if (genericMatch.Success && genericMatch.Groups.Count > 1) {
                    var title = WebUtility.HtmlDecode(genericMatch.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(title) && title.Length > 3) {
                        return title;
                    }
                }
            } catch (Exception ex) {
                Logger.Warning($"Error extracting title from page elements: {ex.Message}");
            }

            return null;
        }

        private string SanitizeTitle(string title) {
            if (string.IsNullOrWhiteSpace(title)) return null;

            try {
                // Remove HTML tags
                title = Regex.Replace(title, @"<[^>]+>", string.Empty);
                
                // Decode HTML entities
                title = WebUtility.HtmlDecode(title);
                
                // Trim whitespace
                title = title.Trim();
                
                // Limit length (reasonable max for display)
                if (title.Length > 200) {
                    title = title.Substring(0, 197) + "...";
                }
                
                // Remove excessive whitespace
                title = Regex.Replace(title, @"\s+", " ");
                
                return string.IsNullOrWhiteSpace(title) ? null : title;
            } catch (Exception ex) {
                Logger.Warning($"Error sanitizing title: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// Clears the URL cache
        /// </summary>
        public void ClearCache() {
            PersistentUrlCache.Instance.Clear();
        }
    }
}




