using System;
using System.IO;
using System.Text.RegularExpressions;
using EdgeLoop.Classes;

namespace EdgeLoop.ViewModels
{
    /// <summary>
    /// Validation status for a video file
    /// </summary>
    public enum FileValidationStatus
    {
        Unknown,
        Valid,
        Missing,
        Invalid
    }

    public class VideoItem : ObservableObject
    {
        private string _filePath;
        public string FilePath
        {
            get => _filePath;
            set
            {
                if (SetProperty(ref _filePath, value))
                {
                    OnPropertyChanged(nameof(FileName));
                    OnPropertyChanged(nameof(SourceName));
                    OnPropertyChanged(nameof(IsUrl));
                    OnPropertyChanged(nameof(IsCached));
                    OnPropertyChanged(nameof(TrackingPath));
                }
            }
        }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        /// <summary>
        /// Gets whether this item is a URL (not a local file path)
        /// </summary>
        public bool IsUrl => Uri.TryCreate(FilePath, UriKind.Absolute, out Uri uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>
        /// Gets a stable identifier for tracking playback position.
        /// Prioritizes OriginalPageUrl, then normalized FilePath.
        /// </summary>
        public string TrackingPath
        {
            get
            {
                var path = !string.IsNullOrEmpty(OriginalPageUrl) ? OriginalPageUrl : FilePath;
                if (string.IsNullOrEmpty(path)) return string.Empty;

                // Basic normalization: trim and lowercase
                // PlaybackPositionTracker will do deeper normalization (like Path.GetFullPath for files)
                return path.Trim();
            }
        }

        /// <summary>
        /// Optional title extracted from the video page. If null or empty, FileName will fall back to URL-based extraction.
        /// </summary>
        private string _title;
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// Gets the display name for the video item
        /// </summary>
        public string FileName
        {
            get
            {
                // Prefer Title if available and not empty/generic
                if (!string.IsNullOrWhiteSpace(Title) && !TitleSanitizer.IsIdLike(Title))
                {
                    return Title;
                }

                // If cached, use the original page URL or original file path for naming
                // instead of the hash-based local cache filename.
                string namingPath = !string.IsNullOrEmpty(OriginalPageUrl) ? OriginalPageUrl : FilePath;
                bool isWebNaming = Uri.TryCreate(namingPath, UriKind.Absolute, out Uri uri) &&
                                 (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

                // Fall back to URL-based or file-based extraction
                if (isWebNaming)
                {
                    try
                    {
                        var segments = uri.Segments;
                        if (segments.Length > 0)
                        {
                            var lastSegment = segments[segments.Length - 1].TrimEnd('/');
                            if (!string.IsNullOrWhiteSpace(lastSegment))
                            {
                                var slug = Uri.UnescapeDataString(lastSegment);

                                // Remove extension if present (e.g. .html, .mp4)
                                if (slug.Contains("."))
                                {
                                    slug = slug.Substring(0, slug.LastIndexOf('.'));
                                }

                                // Remove common trailing ID patterns (e.g. shock-409 -> shock)
                                slug = Regex.Replace(slug, @"-\d+$", "");

                                // Capitalize and replace separators with spaces
                                if (slug.Length > 0)
                                {
                                    return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                                        slug.Replace("-", " ").Replace("_", " ")
                                    );
                                }
                            }
                        }
                        // Fallback to domain + path if slug extraction failed
                        return $"{uri.Host}{uri.AbsolutePath}";
                    }
                    catch
                    {
                        return namingPath;
                    }
                }
                return Path.GetFileName(namingPath);
            }
        }

        private ScreenViewer _assignedScreen;
        public ScreenViewer AssignedScreen
        {
            get => _assignedScreen;
            set => SetProperty(ref _assignedScreen, value);
        }

        private double _opacity = 0.9;
        public double Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, value);
        }

        private double _volume = 0.5;
        public double Volume
        {
            get => _volume;
            set => SetProperty(ref _volume, value);
        }

        private FileValidationStatus _validationStatus = FileValidationStatus.Unknown;
        public FileValidationStatus ValidationStatus
        {
            get => _validationStatus;
            set => SetProperty(ref _validationStatus, value);
        }

        private string _validationError;
        public string ValidationError
        {
            get => _validationError;
            set => SetProperty(ref _validationError, value);
        }

        /// <summary>
        /// The original page URL with full path (including slug). Used for re-extraction when video URLs expire.
        /// </summary>
        private string _originalPageUrl;
        public string OriginalPageUrl
        {
            get => _originalPageUrl;
            set
            {
                if (SetProperty(ref _originalPageUrl, value))
                {
                    OnPropertyChanged(nameof(FileName));
                    OnPropertyChanged(nameof(SourceName));
                    OnPropertyChanged(nameof(IsUrl));
                    OnPropertyChanged(nameof(IsCached));
                    OnPropertyChanged(nameof(TrackingPath));
                }
            }
        }

        /// <summary>
        /// Gets the domain/source name for URLs, or "Local" for files
        /// </summary>
        public string SourceName
        {
            get
            {
                // Prioritize original page URL for source identification
                // This ensures that even when playing from local cache, the source is shown correctly (e.g. "Hypnotube")
                string effectivePath = !string.IsNullOrEmpty(OriginalPageUrl) ? OriginalPageUrl : FilePath;
                bool isWeb = Uri.TryCreate(effectivePath, UriKind.Absolute, out Uri uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

                if (isWeb)
                {
                    try
                    {
                        var host = uri.Host.ToLowerInvariant();
                        if (host.StartsWith("www.")) host = host.Substring(4);

                        // Capitalize first letter of domain for better display
                        if (host.Contains("."))
                        {
                            var parts = host.Split('.');
                            if (parts.Length >= 2)
                            {
                                var mainPart = parts[parts.Length - 2];
                                if (!string.IsNullOrEmpty(mainPart))
                                {
                                    if (mainPart.Equals("pmvhaven", StringComparison.OrdinalIgnoreCase)) return "PMVHaven";
                                    if (mainPart.Equals("rule34video", StringComparison.OrdinalIgnoreCase)) return "RULE34Video";
                                    return char.ToUpper(mainPart[0]) + mainPart.Substring(1);
                                }
                            }
                        }
                        return char.ToUpper(host[0]) + host.Substring(1);
                    }
                    catch
                    {
                        return "Web";
                    }
                }
                return "Local File";
            }
        }

        /// <summary>
        /// Gets whether the file is currently being played from local cache
        /// </summary>
        public bool IsCached => !string.IsNullOrEmpty(OriginalPageUrl) && !IsUrl;

        /// <summary>
        /// Gets whether the file is valid and exists
        /// </summary>
        public bool IsValid => ValidationStatus == FileValidationStatus.Valid;

        public VideoItem(string filePath, ScreenViewer defaultScreen = null)
        {
            FilePath = filePath;
            AssignedScreen = defaultScreen;
        }

        public override string ToString()
        {
            return FileName;
        }

        /// <summary>
        /// Validates the file or URL and updates the validation status
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                ValidationStatus = FileValidationStatus.Invalid;
                ValidationError = "File path or URL is empty";
                return;
            }

            if (IsUrl)
            {
                // Validate URL
                if (!FileValidator.ValidateVideoUrl(FilePath, out string errorMessage))
                {
                    ValidationStatus = FileValidationStatus.Invalid;
                    ValidationError = errorMessage;
                }
                else
                {
                    ValidationStatus = FileValidationStatus.Valid;
                    ValidationError = null;
                }
            }
            else
            {
                // Validate local file
                if (!FileValidator.ValidateVideoFile(FilePath, out string errorMessage))
                {
                    ValidationStatus = File.Exists(FilePath) ? FileValidationStatus.Invalid : FileValidationStatus.Missing;
                    ValidationError = errorMessage;
                }
                else
                {
                    ValidationStatus = FileValidationStatus.Valid;
                    ValidationError = null;
                }
            }
        }
    }
}

