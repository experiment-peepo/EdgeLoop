using System;
using System.Collections.Generic;
using System.Linq;

namespace EdgeLoop.Classes
{
    /// <summary>Video sites with site-specific handling.</summary>
    public enum Site
    {
        Unknown,
        Iwara,
        Hypnotube,
        Rule34Video,
        PmvHaven,
        YouTube
    }

    /// <summary>
    /// Everything that differs per site when requesting pages or media: which hosts belong
    /// to it, which headers and cookies to send, and how to clean up its URLs.
    /// </summary>
    public sealed class SiteProfile
    {
        public Site Site { get; init; }

        /// <summary>Registrable domains; subdomains (www., cdn., ...) match too.</summary>
        public string[] Domains { get; init; } = Array.Empty<string>();

        /// <summary>Referer for HTML/API page requests and redirect resolution.</summary>
        public string PageReferer { get; init; }

        /// <summary>Extra headers for HTML/API page requests (the shared client already sends the User-Agent).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> PageHeaders { get; init; } = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>Referer for the player's media requests. Null means "use the video's page URL".</summary>
        public string MediaReferer { get; init; }

        /// <summary>Origin header for the player's media requests, if the CDN checks it.</summary>
        public string MediaOrigin { get; init; }

        /// <summary>Whether the player sends the site's session cookies with media requests.</summary>
        public bool SendCookiesWithMedia { get; init; }

        /// <summary>Strip the query string from page URLs before extraction (playlist/tracking params break it).</summary>
        public bool StripQueryFromPageUrl { get; init; }

        /// <summary>Headers passed to yt-dlp with --add-header ("Name: value").</summary>
        public IReadOnlyList<string> YtDlpHeaders { get; init; } = Array.Empty<string>();

        /// <summary>When no cookies are configured, let yt-dlp read them from the user's browser.</summary>
        public bool UseBrowserCookiesFallback { get; init; }

        /// <summary>Reads this site's session cookies from settings (null if the site has none).</summary>
        public Func<UserSettings, string> CookieSetting { get; init; }

        /// <summary>The site's session cookies from settings, or null if none are configured.</summary>
        public string GetCookies()
        {
            var settings = App.Settings;
            if (settings == null || CookieSetting == null) return null;
            var cookies = CookieSetting(settings)?.Trim();
            return string.IsNullOrEmpty(cookies) ? null : cookies;
        }

        /// <summary>Domain used when writing cookies to a Netscape cookie file for yt-dlp.</summary>
        public string CookieDomain => Domains.FirstOrDefault();
    }

    /// <summary>
    /// Single source of truth for site detection and per-site request rules. Detection uses
    /// the URL's host, so a site name appearing in a path or query string does not count.
    /// </summary>
    public static class SiteRules
    {
        private static readonly SiteProfile[] Profiles =
        {
            new SiteProfile
            {
                Site = Site.Iwara,
                Domains = new[] { "iwara.tv" },
                PageReferer = "https://www.iwara.tv/",
                PageHeaders = new[]
                {
                    Header("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"),
                    Header("Accept-Language", "en-US,en;q=0.9"),
                    Header("Upgrade-Insecure-Requests", "1"),
                    Header("Origin", "https://www.iwara.tv"),
                    Header("X-Site", "www.iwara.tv"),
                },
                MediaReferer = "https://iwara.tv/",
                MediaOrigin = "https://iwara.tv",
                StripQueryFromPageUrl = true,
                // Iwara sits behind Cloudflare and checks these on API calls
                YtDlpHeaders = new[] { $"User-Agent: {Constants.BrowserUserAgent}", "X-Site: www.iwara.tv" },
                UseBrowserCookiesFallback = true,
                CookieSetting = s => s.IwaraCookies,
            },
            new SiteProfile
            {
                Site = Site.Hypnotube,
                Domains = new[] { "hypnotube.com" },
                PageReferer = "https://hypnotube.com/",
                StripQueryFromPageUrl = true,
                SendCookiesWithMedia = true,
                UseBrowserCookiesFallback = true,
                CookieSetting = s => s.HypnotubeCookies,
            },
            new SiteProfile
            {
                Site = Site.Rule34Video,
                Domains = new[] { "rule34video.com" },
                // Rule34Video only redirects to the media file when a Referer is present
                PageReferer = "https://rule34video.com/",
                MediaReferer = "https://rule34video.com/",
                CookieSetting = s => s.Rule34VideoCookies,
            },
            new SiteProfile
            {
                Site = Site.PmvHaven,
                Domains = new[] { "pmvhaven.com" },
                MediaReferer = "https://pmvhaven.com/",
            },
            new SiteProfile
            {
                Site = Site.YouTube,
                Domains = new[] { "youtube.com", "youtu.be" },
                UseBrowserCookiesFallback = true,
            },
        };

        private static readonly SiteProfile UnknownProfile = new SiteProfile { Site = Site.Unknown };

        /// <summary>Browser yt-dlp reads cookies from when none are configured for a site.</summary>
        public static string CookieBrowser => App.Settings?.BrowserForCookies ?? "Firefox";

        /// <summary>Identifies the site from a URL, or from a bare host name.</summary>
        public static Site Identify(string urlOrHost)
        {
            if (string.IsNullOrWhiteSpace(urlOrHost)) return Site.Unknown;

            if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                return IdentifyHost(uri.Host);
            }

            // Bare host such as "www.iwara.tv"
            return urlOrHost.Contains('/') || urlOrHost.Contains('\\') ? Site.Unknown : IdentifyHost(urlOrHost);
        }

        public static Site Identify(Uri uri) => uri == null || string.IsNullOrEmpty(uri.Host) ? Site.Unknown : IdentifyHost(uri.Host);

        public static bool Is(string urlOrHost, Site site) => site != Site.Unknown && Identify(urlOrHost) == site;

        /// <summary>The site's profile; an empty profile (no special handling) for unknown sites.</summary>
        public static SiteProfile Get(Site site) => Profiles.FirstOrDefault(p => p.Site == site) ?? UnknownProfile;

        public static SiteProfile Get(string urlOrHost) => Get(Identify(urlOrHost));

        /// <summary>Removes query strings that break extraction on sites that need it.</summary>
        public static string NormalizePageUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var profile = Get(url);
            if (profile.StripQueryFromPageUrl)
            {
                var queryStart = url.IndexOf('?');
                if (queryStart >= 0) return url.Substring(0, queryStart);
            }
            return url;
        }

        /// <summary>
        /// Raw HTTP header block (CRLF-terminated lines) for the player's media requests,
        /// or null when there is nothing to send. Pass includeCookies only when the media
        /// is served from the site's own hosts, so session cookies never reach a third party.
        /// </summary>
        public static string BuildMediaHeaders(SiteProfile profile, string referer, bool includeCookies)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(referer))
            {
                lines.Add($"Referer: {referer}");
                if (!string.IsNullOrEmpty(profile.MediaOrigin)) lines.Add($"Origin: {profile.MediaOrigin}");
            }
            if (includeCookies && profile.SendCookiesWithMedia && profile.GetCookies() is string cookies)
            {
                lines.Add($"Cookie: {cookies}");
            }
            return lines.Count == 0 ? null : string.Concat(lines.Select(l => l + "\r\n"));
        }

        /// <summary>True when the host is the domain itself or one of its subdomains ("www.x.com" matches "x.com", "netflix.com" does not).</summary>
        public static bool HostMatches(string host, string domain)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(domain)) return false;
            host = host.Trim().TrimEnd('.');
            return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        private static Site IdentifyHost(string host)
        {
            foreach (var profile in Profiles)
            {
                if (profile.Domains.Any(domain => HostMatches(host, domain)))
                {
                    return profile.Site;
                }
            }
            return Site.Unknown;
        }

        private static KeyValuePair<string, string> Header(string name, string value) => new(name, value);
    }
}
