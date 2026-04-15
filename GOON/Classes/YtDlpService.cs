using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace GOON.Classes {
    /// <summary>
    /// Service for extracting video URLs using yt-dlp
    /// Simplified version focusing on URL extraction only
    /// </summary>
    public class YtDlpService : IDisposable {
        private readonly YoutubeDL _ytdl;
        private bool _isAvailable;
        private readonly string _ytDlpPath;
        private string _detectedVersion;
        private const int MaxRetries = 2;
        private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(60);

        public YtDlpService(string ytDlpPath = null) {
            // Try to find yt-dlp.exe in common locations
            _ytDlpPath = ytDlpPath ?? FindYtDlpExecutable();
            
            if (string.IsNullOrEmpty(_ytDlpPath) || !File.Exists(_ytDlpPath)) {
                Logger.Warning("yt-dlp.exe not found. Video extraction will fall back to scraping.");
                _isAvailable = false;
                return;
            }

            try {
                _ytdl = new YoutubeDL();
                _ytdl.YoutubeDLPath = _ytDlpPath;
                _ytdl.OutputFolder = Path.GetTempPath();
                _isAvailable = true;
                
                // Detect version asynchronously (non-blocking)
                _ = DetectVersionAsync();
                
                Logger.Info($"yt-dlp service initialized: {_ytDlpPath}");
            } catch (Exception ex) {
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
        public async Task<string> GetBestVideoUrlAsync(string url, CancellationToken cancellationToken = default) {
            if (!_isAvailable) return null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++) {
                string cookieFile = null;
                try {
                    if (attempt > 0) {
                        Logger.Info($"[yt-dlp] Retry attempt {attempt}/{MaxRetries} for: {url}");
                        await Task.Delay(500 * attempt, cancellationToken);
                    }

                    Logger.Info($"[yt-dlp] Extracting video URL: {url}");
                    
                    OptionSet options = BuildOptionsForUrl(url, out cookieFile);
                    
                    // Enforce a timeout to prevent hung processes
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(ExtractionTimeout);
                    
                    var result = await _ytdl.RunVideoDataFetch(url, ct: timeoutCts.Token, overrideOptions: options);
                    
                    if (!result.Success) {
                        var errorMsg = result.ErrorOutput?.FirstOrDefault() ?? "Unknown error";
                        Logger.Warning($"[yt-dlp] Extraction failed for {url}: {errorMsg}");
                        
                        // Don't retry on definitive errors (e.g., "Video unavailable" or "Video requires login")
                        if (IsDefinitiveError(errorMsg) || errorMsg.Contains("requires login", StringComparison.OrdinalIgnoreCase)) {
                            // Let the UI know about the definitive error
                            if (errorMsg.Contains("requires login", StringComparison.OrdinalIgnoreCase)) {
                                Logger.Error($"[VideoUrlExtractor] Authentication required for {url}. Video cannot be downloaded unconditionally.");
                            }
                            break;
                        }
                        continue;
                    }

                    var videoUrl = result.Data?.Url;
                    if (!string.IsNullOrEmpty(videoUrl)) {
                        Logger.Info($"[yt-dlp] Successfully extracted URL");
                        LogVideoQuality(result.Data);
                    }
                    
                    return videoUrl;
                } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                    Logger.Warning($"[yt-dlp] Extraction timed out after {ExtractionTimeout.TotalSeconds}s");
                    continue; // Retry on timeout
                } catch (OperationCanceledException) {
                    return null; // Caller cancelled — don't retry
                } catch (Exception ex) {
                    Logger.Warning($"[yt-dlp] Exception during extraction (attempt {attempt}): {ex.Message}");
                    if (attempt >= MaxRetries) return null;
                } finally {
                    CleanupCookieFile(cookieFile);
                }
            }
            return null;
        }

        private OptionSet BuildOptionsForUrl(string url, out string cookieFile) {
            cookieFile = null;
            OptionSet options = new OptionSet();
            if (!string.IsNullOrEmpty(App.Settings?.UserAgent)) {
                options.AddHeaders = new[] { $"User-Agent:{App.Settings.UserAgent}" };
            }
            
            try {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                
                if (host.Contains("hypnotube.com") && !string.IsNullOrEmpty(App.Settings?.HypnotubeCookies)) {
                    Logger.Debug($"[yt-dlp] Using Hypnotube cookies for extraction");
                    cookieFile = CreateTempCookieFile(App.Settings.HypnotubeCookies, "hypnotube.com");
                } else if (!string.IsNullOrEmpty(App.Settings?.Cookies)) {
                    if (App.Settings.Cookies.Contains(host) || host.Split('.').Any(part => App.Settings.Cookies.Contains(part))) {
                        Logger.Debug($"[yt-dlp] Using global cookies for extraction on {host}");
                        cookieFile = CreateTempCookieFile(App.Settings.Cookies, host);
                    }
                }
                
                if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile)) {
                    options.Cookies = cookieFile;
                }
                
                // Fix for Iwara Cloudflare JSONDecodeError
                if (host.Contains("iwara.tv")) {
                    options.AddCustomOption<string>("--impersonate", "chrome");
                    Logger.Info($"[yt-dlp] Added --impersonate chrome for {host} to bypass Cloudflare");
                }
            } catch (Exception ex) {
                Logger.Warning($"[yt-dlp] Error setting up cookies/options: {ex.Message}");
            }
            return options;
        }

        /// <summary>
        /// Extracts video title and basic metadata
        /// </summary>
        public async Task<YtDlpVideoInfo> ExtractVideoInfoAsync(string url, CancellationToken cancellationToken = default) {
            if (!_isAvailable) return null;

            string cookieFile = null;
            try {
                Logger.Info($"[yt-dlp] Extracting video info: {url}");
                
                OptionSet options = BuildOptionsForUrl(url, out cookieFile);
                
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ExtractionTimeout);
                
                var result = await _ytdl.RunVideoDataFetch(url, ct: timeoutCts.Token, overrideOptions: options);
                
                if (!result.Success || result.Data == null) {
                    Logger.Warning($"[yt-dlp] Info extraction failed: {result.ErrorOutput?.FirstOrDefault()}");
                    return null;
                }

                var data = result.Data;
                var info = new YtDlpVideoInfo {
                    Url = SafeGetProperty(data, nameof(data.Url), string.Empty),
                    Title = SafeGetProperty(data, nameof(data.Title), "Unknown"),
                    Duration = (int)(SafeGetProperty<float?>(data, nameof(data.Duration), null) ?? 0),
                    Thumbnail = SafeGetProperty<string>(data, nameof(data.Thumbnail), null),
                    Description = SafeGetProperty<string>(data, nameof(data.Description), null),
                    Uploader = SafeGetProperty<string>(data, nameof(data.Uploader), null)
                };

                Logger.Info($"[yt-dlp] Successfully extracted info: {info.Title}");
                return info;
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                Logger.Warning($"[yt-dlp] Info extraction timed out");
                return null;
            } catch (Exception ex) {
                Logger.Warning($"[yt-dlp] Exception during info extraction: {ex.Message}");
                return null;
            } finally {
                CleanupCookieFile(cookieFile);
            }
        }

        /// <summary>
        /// Extracts a list of video URLs from a playlist or profile URL using yt-dlp natively.
        /// </summary>
        public async Task<List<string>> ExtractPlaylistUrlsAsync(string url, CancellationToken cancellationToken = default) {
            var urls = new List<string>();
            if (!_isAvailable) return urls;

            string cookieFile = null;
            try {
                Logger.Info($"[yt-dlp] Extracting playlist URLs: {url}");
                
                var args = new List<string> {
                    "--flat-playlist",
                    "--print", "url"
                };

                // Manually handle User-Agent if configured
                if (!string.IsNullOrEmpty(App.Settings?.UserAgent)) {
                    args.Add("--add-header");
                    args.Add($"User-Agent:{App.Settings.UserAgent}");
                }

                // Add configured options manually
                OptionSet options = BuildOptionsForUrl(url, out cookieFile);
                if (!string.IsNullOrEmpty(options.Cookies)) {
                    args.Add("--cookies");
                    args.Add(options.Cookies);
                }
                
                // Add Cloudflare bypass logic
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                if (host.Contains("iwara.tv")) {
                    args.Add("--impersonate");
                    args.Add("chrome");
                }

                args.Add(url);

                var psi = new ProcessStartInfo {
                    FileName = _ytDlpPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                foreach (var arg in args) {
                    psi.ArgumentList.Add(arg);
                }

                using var proc = Process.Start(psi);
                if (proc == null) return urls;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

                using (timeoutCts.Token.Register(() => {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                })) {
                    string line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null) {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var trimmed = line.Trim();
                        // Assume any URL returned is a valid video link (yt-dlp extracts proper links)
                        if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)) {
                            urls.Add(trimmed);
                        }
                    }
                    
                    await proc.WaitForExitAsync(cancellationToken);
                    if (proc.ExitCode != 0) {
                        var err = await proc.StandardError.ReadToEndAsync();
                        Logger.Warning($"[yt-dlp] Playlist extraction returned {proc.ExitCode}: {err}");
                    }
                }

                Logger.Info($"[yt-dlp] Successfully extracted {urls.Count} URLs from playlist.");
                return urls;
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                Logger.Warning($"[yt-dlp] Playlist extraction timed out");
                return urls;
            } catch (Exception ex) {
                Logger.Warning($"[yt-dlp] Exception during playlist extraction: {ex.Message}");
                return urls;
            } finally {
                CleanupCookieFile(cookieFile);
            }
        }

        /// <summary>
        /// Finds yt-dlp.exe in common locations
        /// </summary>
        private string FindYtDlpExecutable() {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var searchPaths = new List<string> {
                Path.Combine(appDir, "yt-dlp.exe"),
                Path.Combine(appDir, "bin", "yt-dlp.exe"),
                Path.Combine(appDir, "tools", "yt-dlp.exe")
            };

            // Developer fallback: Look for Dependencies folder in parent directories
            try {
                var current = appDir;
                for (int i = 0; i < 5; i++) {
                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent)) break;
                    
                    var depPath = Path.Combine(parent, "Dependencies", "yt-dlp.exe");
                    if (File.Exists(depPath)) {
                        searchPaths.Add(depPath);
                        break;
                    }
                    current = parent;
                }
            } catch { }

            foreach (var path in searchPaths) {
                if (File.Exists(path)) {
                    var fullPath = Path.GetFullPath(path);
                    Logger.Debug($"[YtDlpService] Found yt-dlp.exe at: {fullPath}");
                    return fullPath;
                }
            }

            // Try to find in PATH
            try {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv)) {
                    foreach (var dir in pathEnv.Split(';')) {
                        var trimmedDir = dir.Trim();
                        if (string.IsNullOrEmpty(trimmedDir)) continue;
                        
                        var fullPath = Path.Combine(trimmedDir, "yt-dlp.exe");
                        if (File.Exists(fullPath)) {
                            Logger.Debug($"[YtDlpService] Found yt-dlp.exe in PATH: {fullPath}");
                            return fullPath;
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Debug($"[YtDlpService] Error searching PATH: {ex.Message}");
            }

            Logger.Warning($"[YtDlpService] yt-dlp.exe not found in searched locations: {string.Join(", ", searchPaths)}");
            return null;
        }

        /// <summary>
        /// Creates a temporary cookie file in Netscape format from a cookie string
        /// Cookie string format: name1=value1; name2=value2
        /// </summary>
        private string CreateTempCookieFile(string cookieString, string domain) {
            try {
                var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
                var tempFile = Path.Combine(Path.GetTempPath(), $"goon_cookies_{domain.Replace(".", "_")}_{Environment.ProcessId}_{uniqueId}.txt");
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
                
                foreach (var cookie in cookies) {
                    var trimmed = cookie.Trim();
                    var eqIndex = trimmed.IndexOf('=');
                    if (eqIndex > 0) {
                        var name = trimmed.Substring(0, eqIndex).Trim();
                        var value = trimmed.Substring(eqIndex + 1).Trim();
                        
                // Add both versions – yt-dlp/curl can be picky
                        lines.Add($"{dottedDomain}\tTRUE\t/\tTRUE\t2147483647\t{name}\t{value}");
                        lines.Add($"{exactDomain}\tFALSE\t/\tTRUE\t2147483647\t{name}\t{value}");
                    }
                }
                
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Encrypted))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8)) {
                    foreach (var line in lines) writer.WriteLine(line);
                }
                
                return tempFile;
            } catch (Exception ex) {
                Logger.Warning($"[yt-dlp] Failed to create temp cookie file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Detects the installed yt-dlp version by running `--version`
        /// </summary>
        private async Task DetectVersionAsync() {
            try {
                var psi = new ProcessStartInfo {
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
                
                if (completed && proc.ExitCode == 0 && !string.IsNullOrEmpty(version)) {
                    _detectedVersion = version;
                    Logger.Info($"[yt-dlp] Detected version: {version}");
                } else {
                    Logger.Warning($"[yt-dlp] Version detection failed (exit code: {(completed ? proc.ExitCode.ToString() : "timeout")})");
                }
            } catch (Exception ex) {
                Logger.Debug($"[yt-dlp] Version detection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to self-update yt-dlp to the latest version.
        /// Returns true if updated successfully.
        /// </summary>
        public async Task<bool> TryUpdateAsync(CancellationToken cancellationToken = default) {
            if (string.IsNullOrEmpty(_ytDlpPath) || !File.Exists(_ytDlpPath)) return false;
            
            try {
                Logger.Info("[yt-dlp] Attempting self-update...");
                
                var psi = new ProcessStartInfo {
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
                
                if (completed && proc.ExitCode == 0) {
                    Logger.Info($"[yt-dlp] Update output: {output.Trim()}");
                    // Re-detect version after update
                    await DetectVersionAsync();
                    return true;
                } else {
                    Logger.Warning($"[yt-dlp] Update failed: {error.Trim()}");
                    return false;
                }
            } catch (Exception ex) {
                Logger.Warning($"[yt-dlp] Update error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Safely accesses a property on the yt-dlp data model, returning a default
        /// value if the property doesn't exist or has changed type in a newer version.
        /// </summary>
        private static T SafeGetProperty<T>(object obj, string propertyName, T defaultValue) {
            if (obj == null) return defaultValue;
            try {
                var prop = obj.GetType().GetProperty(propertyName, 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (prop == null) return defaultValue;
                var value = prop.GetValue(obj);
                if (value == null) return defaultValue;
                if (value is T typed) return typed;
                // Try conversion for numeric types
                return (T)Convert.ChangeType(value, typeof(T));
            } catch {
                return defaultValue;
            }
        }
        
        private static string SafeGetProperty(object obj, string propertyName, string defaultValue) {
            return SafeGetProperty<string>(obj, propertyName, defaultValue);
        }

        /// <summary>
        /// Logs video quality information from the extraction result using safe property access.
        /// </summary>
        private void LogVideoQuality(object data) {
            try {
                var w = SafeGetProperty<object>(data, "Width", null);
                var h = SafeGetProperty<object>(data, "Height", null);
                var f = SafeGetProperty<string>(data, "Format", null);
                if (w != null || h != null || f != null) {
                    Logger.Info($"[yt-dlp] Quality Info: {w}x{h}, Format: {f}");
                }
            } catch { /* ignore quality logging errors — non-critical */ }
        }

        /// <summary>
        /// Determines if an error message indicates a permanent failure that shouldn't be retried.
        /// </summary>
        private static bool IsDefinitiveError(string errorMsg) {
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
        private static void CleanupCookieFile(string cookieFile) {
            if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile)) {
                try { File.Delete(cookieFile); } catch { /* ignore */ }
            }
        }

        public void Dispose() {
            // YoutubeDL doesn't require disposal
            _isAvailable = false;
        }
    }
}
