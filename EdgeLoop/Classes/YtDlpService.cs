using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Service for extracting video URLs using yt-dlp
    /// Simplified version focusing on URL extraction only
    /// </summary>
    public class YtDlpService : IDisposable
    {
        private readonly YoutubeDL _ytdl;
        private bool _isAvailable;
        private readonly string _ytDlpPath;
        private string _detectedVersion;
        private const int MaxRetries = 2;
        private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(60);

        public YtDlpService(string ytDlpPath = null)
        {
            // Try to find yt-dlp.exe in common locations
            _ytDlpPath = ytDlpPath ?? FindYtDlpExecutable();

            if (string.IsNullOrEmpty(_ytDlpPath) || !File.Exists(_ytDlpPath))
            {
                Logger.Warning("yt-dlp.exe not found. Video extraction will fall back to scraping.");
                _isAvailable = false;
                return;
            }

            try
            {
                _ytdl = new YoutubeDL();
                _ytdl.YoutubeDLPath = _ytDlpPath;
                _ytdl.OutputFolder = Path.GetTempPath();
                _isAvailable = true;

                // Detect version asynchronously (non-blocking)
                _ = DetectVersionAsync();

                Logger.Debug($"yt-dlp service initialized: {_ytDlpPath}");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize yt-dlp service", ex);
                _isAvailable = false;
            }
        }

        /// <summary>
        /// The detected yt-dlp version string (e.g. "2026.03.03"), or null if not yet detected
        /// </summary>
        public string DetectedVersion => _detectedVersion;

        public bool IsAvailable => _isAvailable;

        /// <summary>
        /// Extracts the best quality video URL from a page URL
        /// </summary>
        public async Task<string> GetBestVideoUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            if (!_isAvailable) return null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                string cookieFile = null;
                try
                {
                    if (attempt > 0)
                    {
                        Logger.Debug($"[yt-dlp] Retry attempt {attempt}/{MaxRetries} for: {url}");
                        await Task.Delay(500 * attempt, cancellationToken);
                    }

                    Logger.Debug($"[yt-dlp] Extracting video URL: {url}");

                    OptionSet options = BuildOptionsForUrl(url, out cookieFile);

                    // Enforce a timeout to prevent hung processes
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(ExtractionTimeout);

                    var result = await _ytdl.RunVideoDataFetch(url, ct: timeoutCts.Token, overrideOptions: options);

                    if (!result.Success)
                    {
                        var errorMsg = result.ErrorOutput?.FirstOrDefault() ?? "Unknown error";
                        Logger.Warning($"[yt-dlp] Extraction failed for {url}: {errorMsg}");

                        // FALLBACK: If cookie database is locked, retry WITHOUT cookies
                        if (options.CookiesFromBrowser != null && IsCookieLockError(errorMsg))
                        {
                            Logger.Info($"[yt-dlp] Cookie database locked (browser open?). Retrying without cookies...");
                            options.CookiesFromBrowser = null;
                            result = await _ytdl.RunVideoDataFetch(url, ct: timeoutCts.Token, overrideOptions: options);
                            if (result.Success)
                            {
                                Logger.Debug($"[yt-dlp] Fallback successful (fetched without cookies)");
                            }
                            else
                            {
                                errorMsg = result.ErrorOutput?.FirstOrDefault() ?? "Unknown error";
                            }
                        }

                        if (!result.Success)
                        {
                            // Don't retry on definitive errors (e.g., "Video unavailable" or "Video requires login")
                            if (IsDefinitiveError(errorMsg) || errorMsg.Contains("requires login", StringComparison.OrdinalIgnoreCase))
                            {
                                if (errorMsg.Contains("requires login", StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Error($"[VideoUrlExtractor] Authentication required for {url}. Video cannot be downloaded unconditionally.");
                                }
                                break;
                            }
                            continue;
                        }
                    }

                    var videoData = result.Data;
                    var videoUrl = videoData?.Url;

                    // Fallback: check ManifestUrl if direct URL is null
                    if (string.IsNullOrEmpty(videoUrl) && videoData != null)
                    {
                        videoUrl = SafeGetProperty<string>(videoData, "ManifestUrl", null);
                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            Logger.Debug($"[yt-dlp] No direct URL, using ManifestUrl");
                        }
                    }

                    if (!string.IsNullOrEmpty(videoUrl))
                    {
                        Logger.Debug($"[yt-dlp] Successfully extracted URL");
                        LogVideoQuality(videoData);
                        return videoUrl;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Logger.Warning($"[yt-dlp] Extraction timed out after {ExtractionTimeout.TotalSeconds}s");
                    continue; // Retry on timeout
                }
                catch (OperationCanceledException)
                {
                    return null; // Caller cancelled — don't retry
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[yt-dlp] Exception during extraction (attempt {attempt}): {ex.Message}");
                    if (attempt >= MaxRetries) return null;
                }
                finally
                {
                    CleanupCookieFile(cookieFile);
                }
            }
            return null;
        }

        private OptionSet BuildOptionsForUrl(string url, out string cookieFile)
        {
            cookieFile = null;
            OptionSet options = new OptionSet();

            // Request best combined stream for player compatibility (for streaming path)
            options.Format = "best";

            if (!string.IsNullOrEmpty(App.Settings?.UserAgent))
            {
                options.AddHeaders = new[] { $"User-Agent:{App.Settings.UserAgent}" };
            }

            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                // Impersonate Chrome for all sites to help bypass bot detection
                options.AddCustomOption<string>("--impersonate", "chrome");

                // Global high-resolution enforcement
                options.AddCustomOption<string>("--format-sort", "res,br,fps,quality");
                options.AddCustomOption<string>("--no-check-certificates", null);

                // YouTube EJS Challenge Solver (Requires Node.js installed on host)
                options.AddCustomOption<string>("--js-runtimes", "node");
                options.AddCustomOption<string>("--remote-components", "ejs:github");

                bool isYouTube = host.Contains("youtube.com") || host.Contains("youtu.be");
                bool isIwara = host.Contains("iwara.tv");
                bool isHypno = host.Contains("hypnotube.com");

                if (isHypno && !string.IsNullOrEmpty(App.Settings?.HypnotubeCookies))
                {
                    Logger.Debug($"[yt-dlp] Using Hypnotube cookies for extraction");
                    cookieFile = CreateTempCookieFile(App.Settings.HypnotubeCookies, "hypnotube.com");
                }
                else if (isYouTube || isIwara || isHypno)
                {
                    // Use browser cookies for major sites that benefit from being logged in
                    if (!string.IsNullOrEmpty(App.Settings?.Cookies) && (App.Settings.Cookies.Contains(host) || host.Split('.').Any(part => App.Settings.Cookies.Contains(part))))
                    {
                        Logger.Debug($"[yt-dlp] Using global cookies for {host}");
                        cookieFile = CreateTempCookieFile(App.Settings.Cookies, host);
                    }
                    else
                    {
                        var browser = App.Settings?.BrowserForCookies ?? "chrome";
                        options.AddCustomOption<string>("--cookies-from-browser", browser);
                        Logger.Debug($"[yt-dlp] Using --cookies-from-browser {browser} for {host}");
                    }
                }
                else if (!string.IsNullOrEmpty(App.Settings?.Cookies))
                {
                    if (App.Settings.Cookies.Contains(host) || host.Split('.').Any(part => App.Settings.Cookies.Contains(part)))
                    {
                        Logger.Debug($"[yt-dlp] Using global cookies for extraction on {host}");
                        cookieFile = CreateTempCookieFile(App.Settings.Cookies, host);
                    }
                }

                if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile))
                {
                    options.Cookies = cookieFile;
                }

                // Fix for Iwara Cloudflare JSONDecodeError and Quality Selection
                if (isIwara)
                {
                    options.Format = "Source/bestvideo+bestaudio/best";
                    Logger.Debug($"[yt-dlp] Prioritizing 'Source' format for Iwara.tv");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[yt-dlp] Error setting up cookies/options: {ex.Message}");
            }
            return options;
        }

        /// <summary>
        /// Extracts video title and basic metadata
        /// </summary>
        public async Task<YtDlpVideoInfo> ExtractVideoInfoAsync(string url, CancellationToken cancellationToken = default)
        {
            if (!_isAvailable) return null;

            string cookieFile = null;
            try
            {
                Logger.Debug($"[yt-dlp] Extracting video info: {url}");

                OptionSet options = BuildOptionsForUrl(url, out cookieFile);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ExtractionTimeout);

                var result = await _ytdl.RunVideoDataFetch(url, ct: timeoutCts.Token, overrideOptions: options);

                if (!result.Success || result.Data == null)
                {
                    Logger.Warning($"[yt-dlp] Info extraction failed: {result.ErrorOutput?.FirstOrDefault()}");
                    return null;
                }

                var data = result.Data;
                var info = new YtDlpVideoInfo
                {
                    Url = SafeGetProperty(data, nameof(data.Url), string.Empty),
                    Title = SafeGetProperty(data, nameof(data.Title), "Unknown"),
                    Duration = (int)(SafeGetProperty<float?>(data, nameof(data.Duration), null) ?? 0),
                    Thumbnail = SafeGetProperty<string>(data, nameof(data.Thumbnail), null),
                    Description = SafeGetProperty<string>(data, nameof(data.Description), null),
                    Uploader = SafeGetProperty<string>(data, nameof(data.Uploader), null)
                };

                Logger.Debug($"[yt-dlp] Successfully extracted info: {info.Title}");
                return info;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Warning($"[yt-dlp] Info extraction timed out");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[yt-dlp] Exception during info extraction: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupCookieFile(cookieFile);
            }
        }

        /// <summary>
        /// Extracts a list of video URLs from a playlist or profile URL using yt-dlp natively.
        /// </summary>
        public async Task<List<string>> ExtractPlaylistUrlsAsync(string url, CancellationToken cancellationToken = default)
        {
            var urls = new List<string>();
            if (!_isAvailable) return urls;

            string cookieFile = null;
            try
            {
                Logger.Debug($"[yt-dlp] Extracting playlist URLs: {url}");

                var args = new List<string> {
                    "--flat-playlist",
                    "--print", "url",
                    "--impersonate", "chrome"
                };

                // Manually handle User-Agent if configured
                if (!string.IsNullOrEmpty(App.Settings?.UserAgent))
                {
                    args.Add("--add-header");
                    args.Add($"User-Agent:{App.Settings.UserAgent}");
                }

                // Add configured options manually
                OptionSet options = BuildOptionsForUrl(url, out cookieFile);
                if (!string.IsNullOrEmpty(options.Cookies))
                {
                    args.Add("--cookies");
                    args.Add(options.Cookies);
                }

                // Add Cloudflare/Bot bypass logic
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                bool isYouTube = host.Contains("youtube.com") || host.Contains("youtu.be");

                if (host.Contains("iwara.tv") || isYouTube)
                {
                    args.Add("--impersonate");
                    args.Add("chrome");
                }

                if (isYouTube && string.IsNullOrEmpty(options.Cookies))
                {
                    var browser = App.Settings?.BrowserForCookies ?? "chrome";
                    args.Add("--cookies-from-browser");
                    args.Add(browser);
                }

                // Add impersonation to metadata extraction too
                args.Add("--impersonate");
                args.Add("chrome");

                // YouTube EJS Challenge Solver
                args.Add("--js-runtimes");
                args.Add("node");
                args.Add("--remote-components");
                args.Add("ejs:github");

                args.Add(url);

                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }

                using var proc = Process.Start(psi);
                if (proc == null) return urls;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

                using (timeoutCts.Token.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                }))
                {
                    string line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var trimmed = line.Trim();
                        // Assume any URL returned is a valid video link (yt-dlp extracts proper links)
                        if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            urls.Add(trimmed);
                        }
                    }

                    await proc.WaitForExitAsync(cancellationToken);
                    if (proc.ExitCode != 0)
                    {
                        var err = await proc.StandardError.ReadToEndAsync();
                        Logger.Warning($"[yt-dlp] Playlist extraction returned {proc.ExitCode}: {err}");

                        // FALLBACK: Retry without cookies if database is locked
                        if (args.Contains("--cookies-from-browser") && IsCookieLockError(err))
                        {
                            Logger.Info($"[yt-dlp] Cookie database locked during playlist extraction. Retrying without cookies...");

                            // Remove --cookies-from-browser and its value
                            int idx = args.IndexOf("--cookies-from-browser");
                            if (idx >= 0)
                            {
                                args.RemoveAt(idx + 1); // Remove the browser name
                                args.RemoveAt(idx);     // Remove the flag
                            }

                            // Re-run the process
                            var fallbackPsi = new ProcessStartInfo
                            {
                                FileName = _ytDlpPath,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            foreach (var arg in args) fallbackPsi.ArgumentList.Add(arg);

                            using var fallbackProc = Process.Start(fallbackPsi);
                            if (fallbackProc != null)
                            {
                                urls.Clear(); // Clear any partial results
                                while ((line = await fallbackProc.StandardOutput.ReadLineAsync()) != null)
                                {
                                    if (string.IsNullOrWhiteSpace(line)) continue;
                                    var trimmed = line.Trim();
                                    if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)) urls.Add(trimmed);
                                }
                                await fallbackProc.WaitForExitAsync(cancellationToken);
                                if (fallbackProc.ExitCode == 0)
                                {
                                    Logger.Debug($"[yt-dlp] Playlist fallback successful (fetched {urls.Count} URLs)");
                                }
                            }
                        }
                    }
                }

                Logger.Debug($"[yt-dlp] Successfully extracted {urls.Count} URLs from playlist.");
                return urls;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Warning($"[yt-dlp] Playlist extraction timed out");
                return urls;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[yt-dlp] Exception during playlist extraction: {ex.Message}");
                return urls;
            }
            finally
            {
                CleanupCookieFile(cookieFile);
            }
        }

        /// <summary>
        /// Sites that serve separate video+audio streams, requiring download+mux for best quality.
        /// Other sites work via streaming.
        /// </summary>
        public static bool IsDownloadRequiredSite(string host)
        {
            return host.Contains("youtube.com") || host.Contains("youtu.be") ||
                   host.Contains("vimeo.com") ||
                   host.Contains("iwara.tv") ||
                   host.Contains("hypnotube.com") ||
                   host.Contains("rule34video.com") ||
                   host.Contains("dailymotion.com") || host.Contains("dai.ly") ||
                   host.Contains("twitch.tv") ||
                   host.Contains("twitter.com") || host.Contains("x.com");
        }

        /// <summary>
        /// Computes a hash of the URL for cache file naming (matches VideoDownloadService)
        /// </summary>
        private static string ComputeUrlHash(string url)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(url);
            var hash = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Downloads the best quality video and audio streams and muxes them using FFmpeg.
        /// Returns the local file path to the cached video.
        /// </summary>
        public async Task<string> DownloadBestQualityAsync(string url, CancellationToken cancellationToken = default, IProgress<string> downloadProgress = null)
        {
            if (!_isAvailable) return null;
            if (App.Settings?.EnableLocalCaching == false) return null;

            // Compute cache path using same hash as VideoDownloadService
            var cacheDir = App.Settings?.LocalCacheDirectory ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeLoop", "VideoCache");
            Directory.CreateDirectory(cacheDir);

            var hash = ComputeUrlHash(url);
            var finalPath = Path.Combine(cacheDir, $"{hash}.mp4");

            // Already cached?
            if (File.Exists(finalPath))
            {
                Logger.Debug($"[yt-dlp] Cache hit: {Path.GetFileName(finalPath)}");
                return finalPath;
            }

            var tempPath = finalPath + ".ytdl_downloading";

            var args = new List<string> {
                "--format", "bestvideo+bestaudio/best",
                "--format-sort", "res,br,fps,quality",
                "--merge-output-format", "mp4",
                "--output", tempPath,
                "--no-part",
                "--no-playlist",
                "--newline",
                "--impersonate", "chrome",
                "--no-check-certificates",
                "--js-runtimes", "node",
                "--remote-components", "ejs:github"
            };

            if (url.Contains("iwara.tv"))
            {
                // Ensure Source is at the very front for Iwara
                args[1] = "Source/bestvideo+bestaudio/best";
            }

            // Add ffmpeg location if available
            var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dependencies", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                try
                {
                    // Try to find project root from bin\Debug\net10.0-windows
                    var dir = AppDomain.CurrentDomain.BaseDirectory;
                    for (int i = 0; i < 4; i++)
                    {
                        dir = Path.GetDirectoryName(dir);
                        if (dir == null) break;
                        var check = Path.Combine(dir, "Dependencies", "ffmpeg.exe");
                        if (File.Exists(check))
                        {
                            ffmpegPath = check;
                            break;
                        }
                    }
                }
                catch { }
            }
            if (File.Exists(ffmpegPath))
            {
                args.Add("--ffmpeg-location");
                args.Add(ffmpegPath);
                Logger.Debug($"[yt-dlp] Using FFmpeg at: {ffmpegPath}");
            }
            else
            {
                Logger.Warning("[yt-dlp] ffmpeg.exe not found. Muxing of high-quality streams may fail.");
            }

            // Add cookies if configured
            OptionSet options = BuildOptionsForUrl(url, out string cookieFile);
            if (!string.IsNullOrEmpty(options.Cookies))
            {
                args.Add("--cookies");
                args.Add(options.Cookies);
            }
            else if (url.Contains("youtube.com") || url.Contains("youtu.be") || url.Contains("iwara.tv"))
            {
                // Simplified: Just always add it for major sites if no explicit cookie file
                var browser = App.Settings?.BrowserForCookies ?? "Firefox";
                args.Add("--cookies-from-browser");
                args.Add(browser);
                Logger.Debug($"[yt-dlp] Using --cookies-from-browser {browser} for high-quality download");
            }

            args.Add(url);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));  // 30-minute download timeout

                using (timeoutCts.Token.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                }))
                {
                    // Log stdout for download progress
                    var stdoutTask = Task.Run(async () =>
                    {
                        string line;
                        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                        {
                            if (line.Contains("[download]"))
                            {
                                var cleanLine = line.Replace("[download]", "").Trim();
                                if (cleanLine.Contains("%"))
                                {
                                    downloadProgress?.Report(cleanLine);
                                    // Logger.Debug($"[yt-dlp] Progress: {cleanLine}"); // Muted to prevent log spam
                                }
                            }
                        }
                    });

                    // Log stderr for errors
                    var stderrTask = Task.Run(async () =>
                    {
                        string line;
                        while ((line = await proc.StandardError.ReadLineAsync()) != null)
                        {
                            if (line.Contains("ERROR")) Logger.Warning($"[yt-dlp] {line.Trim()}");
                        }
                    });

                    await proc.WaitForExitAsync(timeoutCts.Token);
                    await Task.WhenAll(stdoutTask, stderrTask);
                }

                if (proc.ExitCode != 0)
                {
                    Logger.Warning($"[yt-dlp] Download failed with exit code {proc.ExitCode}");
                    return null;
                }

                // yt-dlp may append format extension — find the actual output file
                if (File.Exists(tempPath))
                {
                    File.Move(tempPath, finalPath, overwrite: true);
                    Logger.Debug($"[yt-dlp] Downloaded and muxed: {Path.GetFileName(finalPath)}");
                    return finalPath;
                }

                // yt-dlp sometimes appends extension even with --output
                var possibleFiles = Directory.GetFiles(cacheDir, $"{hash}.*")
                    .Where(f => !f.EndsWith(".ytdl_downloading"))
                    .FirstOrDefault();
                if (possibleFiles != null)
                {
                    File.Move(possibleFiles, finalPath, overwrite: true);
                    Logger.Debug($"[yt-dlp] Downloaded and muxed (resolved extension): {Path.GetFileName(finalPath)}");
                    return finalPath;
                }

                Logger.Warning("[yt-dlp] Download completed but output file not found");
                return null;

            }
            catch (OperationCanceledException)
            {
                Logger.Debug("[yt-dlp] Download cancelled");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[yt-dlp] Download error: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupCookieFile(cookieFile);
                // Clean up temp file on failure
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// Finds yt-dlp.exe in common locations
        /// </summary>
        private string FindYtDlpExecutable()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var searchPaths = new List<string> {
                Path.Combine(appDir, "yt-dlp.exe"),
                Path.Combine(appDir, "Dependencies", "yt-dlp.exe"),
                Path.Combine(appDir, "bin", "yt-dlp.exe"),
                Path.Combine(appDir, "tools", "yt-dlp.exe")
            };

            // Developer fallback: Look for Dependencies folder in parent directories
            try
            {
                var current = appDir;
                for (int i = 0; i < 5; i++)
                {
                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent)) break;

                    var depPath = Path.Combine(parent, "Dependencies", "yt-dlp.exe");
                    if (File.Exists(depPath))
                    {
                        searchPaths.Add(depPath);
                        break;
                    }
                    current = parent;
                }
            }
            catch { }

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    Logger.Debug($"[YtDlpService] Found yt-dlp.exe at: {fullPath}");
                    return fullPath;
                }
            }

            // Try to find in PATH
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(';'))
                    {
                        var trimmedDir = dir.Trim();
                        if (string.IsNullOrEmpty(trimmedDir)) continue;

                        var fullPath = Path.Combine(trimmedDir, "yt-dlp.exe");
                        if (File.Exists(fullPath))
                        {
                            Logger.Debug($"[YtDlpService] Found yt-dlp.exe in PATH: {fullPath}");
                            return fullPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[YtDlpService] Error searching PATH: {ex.Message}");
            }

            Logger.Warning($"[YtDlpService] yt-dlp.exe not found in searched locations: {string.Join(", ", searchPaths)}");
            return null;
        }

        /// <summary>
        /// Creates a temporary cookie file in Netscape format from a cookie string
        /// Cookie string format: name1=value1; name2=value2
        /// </summary>
        private string CreateTempCookieFile(string cookieString, string domain)
        {
            try
            {
                var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
                var tempFile = Path.Combine(Path.GetTempPath(), $"edgeloop_cookies_{domain.Replace(".", "_")}_{Environment.ProcessId}_{uniqueId}.txt");
                var lines = new System.Collections.Generic.List<string> {
                    "# Netscape HTTP Cookie File",
                    "# https://curl.haxx.se/docs/http-cookies.html"
                };

                // Parse cookies from string format: name=value; name2=value2
                var cookies = cookieString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                // Netscape format: domain\tflag\tpath\tsecure\texpiration\tname\tvalue
                // Ensure the domain is correctly formatted
                // Use both dotted and non-dotted to be safe
                var dottedDomain = domain.StartsWith(".") ? domain : "." + domain;
                var exactDomain = domain.TrimStart('.');

                foreach (var cookie in cookies)
                {
                    var trimmed = cookie.Trim();
                    var eqIndex = trimmed.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var name = trimmed.Substring(0, eqIndex).Trim();
                        var value = trimmed.Substring(eqIndex + 1).Trim();

                        // Add both versions – yt-dlp/curl can be picky
                        lines.Add($"{dottedDomain}\tTRUE\t/\tTRUE\t2147483647\t{name}\t{value}");
                        lines.Add($"{exactDomain}\tFALSE\t/\tTRUE\t2147483647\t{name}\t{value}");
                    }
                }

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Encrypted))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
                {
                    foreach (var line in lines) writer.WriteLine(line);
                }

                return tempFile;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[yt-dlp] Failed to create temp cookie file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Detects the installed yt-dlp version by running `--version`
        /// </summary>
        private async Task DetectVersionAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = "--version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                var version = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                var completed = proc.WaitForExit(5000);

                if (completed && proc.ExitCode == 0 && !string.IsNullOrEmpty(version))
                {
                    _detectedVersion = version;
                    Logger.Debug($"[yt-dlp] Detected version: {version}");
                }
                else
                {
                    Logger.Warning($"[yt-dlp] Version detection failed (exit code: {(completed ? proc.ExitCode.ToString() : "timeout")})");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[yt-dlp] Version detection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to self-update yt-dlp to the latest version.
        /// Returns true if updated successfully.
        /// </summary>
        public async Task<bool> TryUpdateAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_ytDlpPath) || !File.Exists(_ytDlpPath)) return false;

            try
            {
                Logger.Debug("[yt-dlp] Attempting self-update...");

                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = "--update",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                await Task.WhenAll(outputTask, errorTask);

                var output = outputTask.Result;
                var error = errorTask.Result;
                var completed = proc.WaitForExit(30000); // 30s timeout for update

                if (completed && proc.ExitCode == 0)
                {
                    Logger.Debug($"[yt-dlp] Update output: {output.Trim()}");
                    // Re-detect version after update
                    await DetectVersionAsync();
                    return true;
                }
                else
                {
                    Logger.Warning($"[yt-dlp] Update failed: {error.Trim()}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[yt-dlp] Update error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Safely accesses a property on the yt-dlp data model, returning a default
        /// value if the property doesn't exist or has changed type in a newer version.
        /// </summary>
        private static T SafeGetProperty<T>(object obj, string propertyName, T defaultValue)
        {
            if (obj == null) return defaultValue;
            try
            {
                var prop = obj.GetType().GetProperty(propertyName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (prop == null) return defaultValue;
                var value = prop.GetValue(obj);
                if (value == null) return defaultValue;
                if (value is T typed) return typed;
                // Try conversion for numeric types
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        private static string SafeGetProperty(object obj, string propertyName, string defaultValue)
        {
            return SafeGetProperty<string>(obj, propertyName, defaultValue);
        }

        /// <summary>
        /// Logs video quality information from the extraction result using safe property access.
        /// </summary>
        private void LogVideoQuality(object data)
        {
            try
            {
                var w = SafeGetProperty<object>(data, "Width", null);
                var h = SafeGetProperty<object>(data, "Height", null);
                var f = SafeGetProperty<string>(data, "Format", null);
                if (w != null || h != null || f != null)
                {
                    Logger.Debug($"[yt-dlp] Quality Info: {w}x{h}, Format: {f}");
                }
            }
            catch { /* ignore quality logging errors — non-critical */ }
        }

        private bool IsCookieLockError(string errorMsg)
        {
            if (string.IsNullOrEmpty(errorMsg)) return false;
            return errorMsg.Contains("Could not copy", StringComparison.OrdinalIgnoreCase) &&
                   errorMsg.Contains("cookie database", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines if an error message indicates a permanent failure that shouldn't be retried.
        /// </summary>
        private static bool IsDefinitiveError(string errorMsg)
        {
            if (string.IsNullOrEmpty(errorMsg)) return false;
            return errorMsg.Contains("video unavailable", StringComparison.OrdinalIgnoreCase) ||
                   errorMsg.Contains("this video is private", StringComparison.OrdinalIgnoreCase) ||
                   errorMsg.Contains("this video has been removed", StringComparison.OrdinalIgnoreCase) ||
                   errorMsg.Contains("unsupported url", StringComparison.OrdinalIgnoreCase) ||
                   errorMsg.Contains("has been terminated", StringComparison.OrdinalIgnoreCase) ||
                   errorMsg.Contains("geo restricted", StringComparison.OrdinalIgnoreCase) ||
                   errorMsg.Contains("no video formats found", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Safely cleans up a temporary cookie file.
        /// </summary>
        private static void CleanupCookieFile(string cookieFile)
        {
            if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile))
            {
                try { File.Delete(cookieFile); } catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            // YoutubeDL doesn't require disposal
            _isAvailable = false;
        }
    }
}


