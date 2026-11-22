using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EdgeLoop.Classes
{
    /// <summary>Why a site won't show a page, when its own message says so.</summary>
    public enum SiteRestriction
    {
        None,
        /// <summary>Hypnotube: "You must be a VIP member to view this video!"</summary>
        MembersOnly,
        /// <summary>Hypnotube: "You must be friends with ... to watch this video"</summary>
        FriendsOnly,
        /// <summary>Hypnotube: "Access to this content is restricted in your location."</summary>
        LocationBlocked,
        /// <summary>Iwara (through yt-dlp): "Video may need login to view", "Private video. Login if ..."</summary>
        SignInRequired,
        /// <summary>Iwara (through yt-dlp): "Invalid login credentials"</summary>
        SignInRejected,
        /// <summary>YouTube (through yt-dlp): "n challenge solving failed ... Ensure you have a supported JavaScript runtime"</summary>
        JavaScriptRuntimeMissing,
    }

    /// <summary>
    /// Recognises a site's "you can't see this" pages, and remembers which page showed one so the
    /// player and launcher can say why a video or playlist failed instead of blaming the site's layout.
    /// </summary>
    public static class SiteRestrictions
    {
        private static readonly ConcurrentDictionary<string, SiteRestriction> _seen = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<SiteRestriction, string> Sentences = new()
        {
            [SiteRestriction.MembersOnly] = "It's for Hypnotube VIP members only.",
            [SiteRestriction.FriendsOnly] = "Only the uploader's friends on Hypnotube can watch it.",
            [SiteRestriction.LocationBlocked] = "Hypnotube blocks this page in the country your connection comes from (a VPN can cause this).",
            [SiteRestriction.SignInRequired] = "Iwara only shows it to signed-in accounts: add yours in Settings › Privacy & Cookies.",
            [SiteRestriction.SignInRejected] = "Iwara didn't accept the email and password in Settings › Privacy & Cookies.",
            [SiteRestriction.JavaScriptRuntimeMissing] = "YouTube needs Node.js 22 or newer (or Deno) installed on this PC: yt-dlp uses it to unlock YouTube's videos.",
        };

        /// <summary>
        /// What a yt-dlp error says stands in the way and won't change on a retry: an account Iwara wants,
        /// or the JavaScript runtime YouTube needs. None for other errors.
        /// </summary>
        public static SiteRestriction FromYtDlpError(Site site, string error)
        {
            if (string.IsNullOrEmpty(error)) return SiteRestriction.None;

            // yt-dlp solves YouTube's "n challenge" with Node 22+ or Deno; without one, no formats come back
            if (site == Site.YouTube &&
                (error.Contains("challenge solving failed", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("supported JavaScript runtime", StringComparison.OrdinalIgnoreCase)) &&
                error.Contains("No video formats found", StringComparison.OrdinalIgnoreCase))
                return SiteRestriction.JavaScriptRuntimeMissing;

            if (site != Site.Iwara) return SiteRestriction.None;
            if (error.Contains("Invalid login credentials", StringComparison.OrdinalIgnoreCase)) return SiteRestriction.SignInRejected;
            if (error.Contains("need login", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Login if you have permissions", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("login required", StringComparison.OrdinalIgnoreCase))
                return SiteRestriction.SignInRequired;
            return SiteRestriction.None;
        }

        /// <summary>The restriction a page's HTML announces, or None.</summary>
        public static SiteRestriction Detect(Site site, string html)
        {
            if (site != Site.Hypnotube || string.IsNullOrEmpty(html)) return SiteRestriction.None;
            bool Has(string text) => html.Contains(text, StringComparison.OrdinalIgnoreCase);

            if (Has("restricted in your location")) return SiteRestriction.LocationBlocked;
            if (Has("must be a VIP member to view")) return SiteRestriction.MembersOnly;
            if (Has("You must be friends with") && Has("to watch this video")) return SiteRestriction.FriendsOnly;
            return SiteRestriction.None;
        }

        /// <summary>Remembers what a page showed; None forgets an earlier restriction.</summary>
        public static void Record(string pageUrl, SiteRestriction restriction)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return;
            if (restriction == SiteRestriction.None) _seen.TryRemove(Key(pageUrl), out _);
            else _seen[Key(pageUrl)] = restriction;
        }

        /// <summary>The restriction last seen on a page, or None.</summary>
        public static SiteRestriction For(string pageUrl) =>
            !string.IsNullOrWhiteSpace(pageUrl) && _seen.TryGetValue(Key(pageUrl), out var r) ? r : SiteRestriction.None;

        /// <summary>A sentence for the restriction last seen on a page ("It's for Hypnotube VIP members only."), or null.</summary>
        public static string Describe(string pageUrl) => Describe(For(pageUrl));

        public static string Describe(SiteRestriction restriction) =>
            Sentences.TryGetValue(restriction, out var sentence) ? sentence : null;

        /// <summary>The sentence a failure message carries, if it carries one; for on-screen notices.</summary>
        public static string FindIn(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            foreach (var sentence in Sentences.Values)
            {
                if (message.Contains(sentence, StringComparison.Ordinal)) return sentence;
            }
            return null;
        }

        private static string Key(string pageUrl) => FileValidator.NormalizeUrl(SiteRules.NormalizePageUrl(pageUrl.Trim()));
    }
}
