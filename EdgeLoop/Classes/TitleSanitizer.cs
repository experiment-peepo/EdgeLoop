using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Shared rules for turning scraped or extracted text into a display title, and for
    /// recognising "titles" that are really IDs, hashes or placeholders.
    /// </summary>
    public static class TitleSanitizer
    {
        private const int MaxTitleLength = 200;

        // Values yt-dlp and site pages return when no real title exists.
        private static readonly string[] Placeholders = { "na", "[na]", "view", "unknown" };

        private static readonly string[] SiteSuffixes =
        {
            " - Videos - Hypnotube",
            " - Hypnotube",
            " | Rule34Video",
            " - PMV Haven",
            " - PMVHaven",
            " - YouTube"
        };

        private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex TrailingCopyNumber = new(@"\s+\(\d\)$", RegexOptions.Compiled);
        private static readonly Regex PmvHavenUploader = new(@"\s+by\s+([^-|]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HexId = new(@"^[a-f0-9\-]{24,}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HexHash = new(@"^(?:[a-f0-9]{32}|[a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IdChars = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
        private static readonly Regex TrailingNumericId = new(@"-\d+$", RegexOptions.Compiled);

        /// <summary>True for the placeholder strings sites return instead of a title ("NA", "view", ...).</summary>
        public static bool IsPlaceholder(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.Trim();
            return Placeholders.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// True when the text is not a usable title: empty, too short, a placeholder,
        /// a UUID/hash, or an Iwara-style alphanumeric video ID.
        /// </summary>
        public static bool IsIdLike(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (text.Length < 4) return true;
            if (IsPlaceholder(text)) return true;
            if (text.StartsWith("[NA]", StringComparison.OrdinalIgnoreCase)) return true;

            if (HexId.IsMatch(text) || HexHash.IsMatch(text)) return true;

            // Alphanumeric IDs (10-20 chars, no spaces, mixing digits and letters), e.g. Iwara's
            if (text.Length >= 10 && text.Length <= 20 && !text.Contains(' ') && IdChars.IsMatch(text)
                && text.Any(char.IsDigit) && text.Any(char.IsLetter))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Cleans a scraped title: strips HTML, decodes entities, collapses whitespace,
        /// removes site-name suffixes and copy numbers. Returns null when nothing usable is left.
        /// </summary>
        public static string Clean(string title, string host = null)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            try
            {
                title = HtmlTag.Replace(title, string.Empty);
                title = WebUtility.HtmlDecode(title).Trim();

                if (title.Length > MaxTitleLength)
                {
                    title = title.Substring(0, MaxTitleLength - 3) + "...";
                }

                title = Whitespace.Replace(title, " ");

                foreach (var suffix in SiteSuffixes)
                {
                    if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        title = title.Substring(0, title.Length - suffix.Length).Trim();
                        break;
                    }
                }

                // Drop search-result copy numbers like "(1)" but keep years like "(2024)"
                title = TrailingCopyNumber.Replace(title, "");

                // PMVHaven appends the uploader: "Title by Uploader"
                if (!string.IsNullOrEmpty(host) && SiteRules.Is(host, Site.PmvHaven))
                {
                    var byMatch = PmvHavenUploader.Match(title);
                    if (byMatch.Success)
                    {
                        title = title.Substring(0, byMatch.Index).Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(title) || IsPlaceholder(title)) return null;

                return title;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error sanitizing title: {ex.Message}");
                return null;
            }
        }

        /// <summary>Turns a URL slug into a title: "my-cool-video-7132" becomes "My Cool Video".</summary>
        public static string DeSlugify(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return slug;

            var clean = TrailingNumericId.Replace(slug, "");
            clean = clean.Replace("-", " ").Replace("_", " ");
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean);
        }

        /// <summary>
        /// Builds a title from the slug in a video page URL (Iwara: /video/{id}/{slug},
        /// Hypnotube: /video/{slug}.html). Returns null if the URL has no slug.
        /// </summary>
        public static string FromUrlSlug(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

            // YouTube links carry an ID, not a slug ("watch", "shorts/abc"): nothing to make a title from
            if (SiteRules.Identify(uri) == Site.YouTube) return null;

            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            var slug = SiteRules.Identify(uri) == Site.Iwara && parts.Length >= 3
                ? parts[2]
                : parts[^1];

            if (slug.Contains('.')) slug = slug.Split('.')[0];

            return DeSlugify(slug);
        }
    }
}
