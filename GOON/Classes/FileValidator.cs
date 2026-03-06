using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GOON.Classes {
    /// <summary>
    /// Validates file paths, extensions, sizes, and sanitizes inputs
    /// </summary>
    public static class FileValidator {
        /// <summary>
        /// Validates if a file path is valid and safe
        /// </summary>
        /// <param name="filePath">The file path to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool IsValidPath(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            try {
                // Normalize and validate path - Path.GetFullPath will:
                // 1. Resolve ".." and "." segments
                // 2. Throw on invalid paths (malformed characters, etc.)
                var fullPath = Path.GetFullPath(filePath);
                
                // Ensure path is rooted (absolute) after normalization
                if (!Path.IsPathRooted(fullPath)) return false;
                
                // After normalization, ".." should not appear in a valid path
                // If it does, the path couldn't be fully normalized and is problematic
                if (fullPath.Contains("..")) return false;
                
                return true;
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Validates if a file has a supported video extension
        /// </summary>
        /// <param name="filePath">The file path to check</param>
        /// <returns>True if extension is supported, false otherwise</returns>
        public static bool HasValidExtension(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return Constants.VideoExtensions.Contains(extension);
        }

        /// <summary>
        /// Gets the file size in bytes, or null if file doesn't exist or can't be accessed
        /// </summary>
        /// <param name="filePath">The file path</param>
        /// <returns>File size in bytes, or null if unavailable</returns>
        public static long? GetFileSize(string filePath) {
            try {
                if (!File.Exists(filePath)) return null;
                var fileInfo = new FileInfo(filePath);
                return fileInfo.Length;
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Gets file size and warning threshold (no size limit enforced)
        /// </summary>
        /// <param name="filePath">The file path</param>
        /// <param name="sizeBytes">Output parameter for file size</param>
        /// <param name="warningThreshold">Output parameter indicating if file exceeds warning threshold</param>
        /// <returns>True if file exists and size was retrieved, false otherwise</returns>
        public static bool ValidateFileSize(string filePath, out long sizeBytes, out bool warningThreshold) {
            sizeBytes = 0;
            warningThreshold = false;
            
            var size = GetFileSize(filePath);
            if (!size.HasValue) return false;
            
            sizeBytes = size.Value;
            warningThreshold = sizeBytes > Constants.FileSizeWarningThreshold;
            
            // No size limit - always return true if file exists
            return true;
        }

        /// <summary>
        /// Sanitizes and normalizes a file path
        /// </summary>
        /// <param name="filePath">The file path to sanitize</param>
        /// <returns>Sanitized full path, or null if invalid</returns>
        public static string SanitizePath(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            
            try {
                // Path.GetFullPath normalizes the path and resolves ".." segments
                var fullPath = Path.GetFullPath(filePath);
                
                // After normalization, ".." should not appear - if it does, something went wrong
                if (fullPath.Contains("..")) return null;
                
                // Ensure path is rooted (absolute)
                if (!Path.IsPathRooted(fullPath)) return null;
                
                return fullPath;
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Gets a user-friendly list of supported file extensions
        /// </summary>
        public static string GetSupportedExtensionsList() {
            return string.Join(", ", Constants.VideoExtensions.Select(ext => ext.ToUpperInvariant().TrimStart('.')));
        }

        /// <summary>
        /// Compares the first few bytes of a file to detect if it's a text manifest (HLS/DASH)
        /// disguised as a video file (common in partial/failed downloads).
        /// </summary>
        public static bool IsCorruptedCacheFile(string filePath) {
            try {
                if (!File.Exists(filePath)) return false;
                
                var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
                bool isManifestExtension = extension == ".m3u8" || extension == ".mpd";

                var info = new FileInfo(filePath);
                if (info.Length == 0) return true; // Empty file is useless
                if (info.Length < 10) return false;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream)) {
                    // Try to read first few characters
                    char[] buffer = new char[10];
                    int read = reader.Read(buffer, 0, 10);
                    if (read < 7) return false;
                    
                    string header = new string(buffer, 0, read).TrimStart();
                    
                    // IF it's a manifest header...
                    bool isManifestHeader = header.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || 
                                           header.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
                    
                    if (isManifestHeader) {
                        // It's only "corrupted" if it's NOT a manifest extension
                        // (e.g. an HLS manifest saved with an .mp4 extension)
                        return !isManifestExtension;
                    }
                }
            } catch {
                // Ignore errors reading file
            }
            return false;
        }

        /// <summary>
        /// Comprehensive validation of a video file
        /// </summary>
        /// <param name="filePath">The file path to validate</param>
        /// <param name="errorMessage">Output parameter for error message if validation fails</param>
        /// <returns>True if file is valid, false otherwise</returns>
        public static bool ValidateVideoFile(string filePath, out string errorMessage) {
            errorMessage = null;
            
            if (!IsValidPath(filePath)) {
                errorMessage = "Invalid file path. Please check the path and try again.";
                return false;
            }
            
            if (!HasValidExtension(filePath)) {
                var extension = Path.GetExtension(filePath)?.ToUpperInvariant() ?? "unknown";
                var supportedList = GetSupportedExtensionsList();
                errorMessage = $"File format '{extension}' is not supported. Supported formats: {supportedList}";
                return false;
            }
            
            if (!File.Exists(filePath)) {
                errorMessage = "File does not exist. The file may have been moved or deleted.";
                return false;
            }

            if (IsCorruptedCacheFile(filePath)) {
                errorMessage = "The cached video file appears to be a stream manifest (HLS/DASH) or is corrupted. It cannot be played as a local file.";
                return false;
            }
            
            // Check if path points to a directory instead of a file
            try {
                var attributes = File.GetAttributes(filePath);
                if ((attributes & FileAttributes.Directory) == FileAttributes.Directory) {
                    errorMessage = "The specified path points to a directory, not a file.";
                    return false;
                }
            } catch (Exception ex) {
                // If we can't check attributes, log but don't fail validation
                Logger.Warning($"Could not check file attributes for: {filePath}", ex);
            }
            
            // File size validation removed - no limit enforced
            
            return true;
        }

        /// <summary>
        /// Validates if a string is a valid HTTP/HTTPS URL
        /// </summary>
        /// <param name="url">The URL to validate</param>
        /// <returns>True if valid URL, false otherwise</returns>
        public static bool IsValidUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) return false;
            
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Determines if a URL is a page URL (needs extraction) vs a direct video URL
        /// </summary>
        /// <param name="url">The URL to check</param>
        /// <returns>True if page URL, false if direct video URL or invalid</returns>
        public static bool IsPageUrl(string url) {
            if (!IsValidUrl(url)) return false;
            
            try {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                
                // Check if URL is from a supported domain
                bool isSupportedDomain = Constants.SupportedVideoDomains.Any(domain => 
                    host == domain || host.EndsWith("." + domain));
                
                if (!isSupportedDomain) return false;
                
                // Check if URL has a video file extension (direct video URL)
                var path = uri.AbsolutePath.ToLowerInvariant().TrimEnd('/');
                
                // EXPLICIT CHECK for streaming protocols to avoid extraction loop (redundant with Constants but safe)
                if (path.EndsWith(".m3u8") || path.EndsWith(".mpd")) return false;

                bool hasVideoExtension = Constants.VideoExtensions.Any(ext => 
                    path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                
                if (hasVideoExtension) return false;

                // Exclude common web assets from being treated as page URLs
                var assetExtensions = new[] { 
                    ".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".woff", ".woff2", ".ttf", ".eot", ".json", ".xml" 
                };
                if (assetExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) return false;
                
                return true;
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Normalizes a URL by removing fragments and normalizing query parameters
        /// </summary>
        /// <param name="url">The URL to normalize</param>
        /// <returns>Normalized URL, or original if invalid</returns>
        public static string NormalizeUrl(string url) {
            if (!IsValidUrl(url)) return url;
            
            try {
                var uri = new Uri(url);
                var builder = new UriBuilder(uri) {
                    Fragment = string.Empty // Remove fragment
                };
                return builder.Uri.ToString();
            } catch {
                return url;
            }
        }

        /// <summary>
        /// Comprehensive validation of a video URL
        /// </summary>
        /// <param name="url">The URL to validate</param>
        /// <param name="errorMessage">Output parameter for error message if validation fails</param>
        /// <returns>True if URL is valid, false otherwise</returns>
        public static bool ValidateVideoUrl(string url, out string errorMessage) {
            errorMessage = null;
            
            if (!IsValidUrl(url)) {
                errorMessage = "Invalid URL. Please provide a valid HTTP or HTTPS URL.";
                return false;
            }
            
            try {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.ToLowerInvariant();
                
                // Check if URL has a video file extension
                bool hasVideoExtension = Constants.VideoExtensions.Any(ext => 
                    path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                
                // Check if URL is from a supported domain
                var host = uri.Host.ToLowerInvariant();
                bool isSupportedDomain = Constants.SupportedVideoDomains.Any(domain => 
                    host == domain || host.EndsWith("." + domain));
                
                // Accept if it has a video extension OR is from a supported domain
                // (supported domains will be handled by URL extraction)
                if (hasVideoExtension || isSupportedDomain) {
                    return true;
                }
                
                // For other URLs, be permissive - MediaElement can handle various formats
                // But warn the user
                errorMessage = "URL may not be a video. MediaElement will attempt to play it.";
                return true; // Still accept it, but warn
            } catch (Exception ex) {
                errorMessage = $"Invalid URL format: {ex.Message}";
                return false;
            }
        }
    }
}

