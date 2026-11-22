using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdgeLoop.Classes
{
    /// <summary>The cookies a pasted text held for one site, as a Cookie header value, and a sentence saying what was found.</summary>
    public sealed record PastedCookies(string Header, string Summary)
    {
        public bool Found => Header != null;
    }

    /// <summary>
    /// Reads a site's cookies from whatever the user copied, so they don't have to find one exact value:
    /// an extension's JSON export, a cookies.txt file (including "#HttpOnly_" lines), "Copy as cURL" from
    /// Firefox or Chrome/Edge (bash or Windows quoting), Chrome's "Copy as PowerShell", a block of request
    /// headers, a "Cookie:" line, plain "name=value; ..." pairs, or just the PHPSESSID value on its own.
    /// Cookies for other sites (exports often hold every site) are left out.
    /// </summary>
    public static class CookiePaste
    {
        private sealed record Item(string Name, string Value, string Domain);

        /// <param name="siteDomain">The site's domain, e.g. "hypnotube.com"; its subdomains count too.</param>
        public static PastedCookies Parse(string text, string siteDomain)
        {
            if (string.IsNullOrWhiteSpace(text)) return new(null, "The clipboard is empty: copy the cookies first.");
            text = text.Trim();

            string format;
            List<Item> items;
            string requestHost = null;

            if ((items = FromJson(text)) != null) format = "an extension's JSON export";
            else if ((items = FromCurl(text, out requestHost)) != null) format = "a cURL command";
            else if ((items = FromPowerShell(text)) != null) format = "a PowerShell command";
            else if ((items = FromCookiesTxt(text)) != null) format = "a cookies.txt file";
            else if ((items = FromHeaders(text)) != null) format = "request headers";
            else if ((items = FromPairs(text)) != null) format = "a cookie list";
            else if (Regex.IsMatch(text, @"^[A-Za-z0-9,\-]{16,}$"))
            {
                items = new List<Item> { new("PHPSESSID", text, null) };
                format = "a PHPSESSID value";
            }
            else
            {
                return new(null, "Couldn't find any cookies in what was pasted.");
            }

            var mine = items.Where(i => i.Domain == null || DomainMatches(i.Domain, siteDomain)).ToList();
            var others = items.Count - mine.Count;

            // Later duplicates (e.g. a cookie set for both "site.com" and ".site.com") win, as in a browser
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in mine) byName[item.Name] = item.Value;

            if (byName.Count == 0)
            {
                var elsewhere = items.Select(i => i.Domain?.TrimStart('.')).Where(d => d != null).Distinct().Take(3).ToList();
                return new(null, $"Found {format}, but no cookies for {siteDomain} in it" +
                                 (elsewhere.Count > 0 ? $" (only {string.Join(", ", elsewhere)})." : "."));
            }

            var header = string.Join("; ", byName.Select(kv => $"{kv.Key}={kv.Value}"));
            var summary = $"Found {Cookies(byName.Count)} for {siteDomain} in {format}" +
                          (others > 0 ? $"; left out {Cookies(others)} for other sites" : "") + ".";
            if (requestHost != null && !SiteRules.HostMatches(requestHost, siteDomain))
                summary += $" Note: that request went to {requestHost}, not {siteDomain}.";
            return new(header, summary);
        }

        private static string Cookies(int n) => n == 1 ? "1 cookie" : $"{n} cookies";

        /// <summary>"hypnotube.com", ".hypnotube.com" and "www.hypnotube.com" belong to hypnotube.com; "nothypnotube.com" doesn't.</summary>
        private static bool DomainMatches(string cookieDomain, string siteDomain) =>
            SiteRules.HostMatches(cookieDomain.Trim().TrimStart('.'), siteDomain);

        /// <summary>Cookie-Editor, EditThisCookie and "Get cookies.txt LOCALLY" JSON: [{"name","value","domain",...}] or {"cookies":[...]}.</summary>
        private static List<Item> FromJson(string text)
        {
            if (!(text.StartsWith('[') || text.StartsWith('{'))) return null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cookies", out var list)) root = list;
                if (root.ValueKind != JsonValueKind.Array) return null;

                string Text(JsonElement e, string name) =>
                    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var items = root.EnumerateArray()
                    .Select(e => (Name: Text(e, "name"), Value: Text(e, "value"), Domain: Text(e, "domain") ?? Text(e, "host")))
                    .Where(c => !string.IsNullOrEmpty(c.Name) && c.Value != null)
                    .Select(c => new Item(c.Name, c.Value, c.Domain))
                    .ToList();
                return items.Count > 0 ? items : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// "Copy as cURL": Firefox writes -H 'Cookie: …' (POSIX) or -H ^"Cookie: …^" (Windows, ^-escaped);
        /// Chrome/Edge write -b '…' or -H 'cookie: …' (bash) or -b ^"…^" (cmd).
        /// </summary>
        private static List<Item> FromCurl(string text, out string host)
        {
            host = null;
            if (!Regex.IsMatch(text, @"^\s*curl(\.exe)?\s", RegexOptions.IgnoreCase)) return null;

            // Windows cmd quoting: "^" escapes the next character and ends continued lines
            if (text.Contains("^\""))
            {
                text = Regex.Replace(text, @"\^\s*\r?\n", " ");
                text = Regex.Replace(text, @"\^(.)", "$1");
                text = text.Replace("\\\"", "\"").Replace("\"\"", "\"");
            }
            // Bash line continuations
            text = Regex.Replace(text, @"\\\s*\r?\n", " ");

            var url = Regex.Match(text, @"https?://[^\s'""]+", RegexOptions.IgnoreCase);
            if (url.Success && Uri.TryCreate(url.Value, UriKind.Absolute, out var uri)) host = uri.Host;

            var values = new List<string>();
            foreach (Match m in Regex.Matches(text, @"(?:-H|--header)\s+(['""])\s*cookie\s*:\s*(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                values.Add(m.Groups[2].Value);
            foreach (Match m in Regex.Matches(text, @"(?:-b|--cookie)\s+(['""])(.*?)\1", RegexOptions.Singleline))
                values.Add(m.Groups[2].Value);

            var items = values.SelectMany(v => Pairs(v.Replace("'\\''", "'"))).ToList();
            return items.Count > 0 ? items : new List<Item>(); // a cURL with no cookies is still a cURL: say so
        }

        /// <summary>Chrome/Edge "Copy as PowerShell": New-Object System.Net.Cookie("name", "value", "/", "domain").</summary>
        private static List<Item> FromPowerShell(string text)
        {
            var matches = Regex.Matches(text, @"System\.Net\.Cookie\(\s*""((?:[^""`]|`.)*)""\s*,\s*""((?:[^""`]|`.)*)""\s*,\s*""[^""]*""\s*,\s*""([^""]*)""\s*\)");
            if (matches.Count == 0)
                return Regex.IsMatch(text, @"Invoke-WebRequest|WebRequestSession", RegexOptions.IgnoreCase) ? new List<Item>() : null;
            static string Unescape(string s) => Regex.Replace(s, "`(.)", "$1");
            return matches.Select(m => new Item(Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value), m.Groups[3].Value)).ToList();
        }

        /// <summary>Netscape cookies.txt: domain, subdomains, path, secure, expiry, name, value (tabs); "#HttpOnly_" marks HttpOnly cookies.</summary>
        private static List<Item> FromCookiesTxt(string text)
        {
            var items = new List<Item>();
            var sawTabLine = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase)) line = line.Substring("#HttpOnly_".Length);
                else if (line.StartsWith('#') || line.Trim().Length == 0) continue;

                var fields = line.Split('\t');
                if (fields.Length < 7) continue;
                sawTabLine = true;
                if (fields[5].Length > 0) items.Add(new Item(fields[5], fields[6], fields[0]));
            }
            return sawTabLine ? items : null;
        }

        /// <summary>A block of request headers (Firefox "Copy Request Headers") or a single "Cookie: …" line.</summary>
        private static List<Item> FromHeaders(string text)
        {
            var cookieLines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => Regex.IsMatch(l, @"^cookie\s*:", RegexOptions.IgnoreCase))
                .Select(l => l.Substring(l.IndexOf(':') + 1))
                .ToList();
            if (cookieLines.Count == 0) return null;
            var items = cookieLines.SelectMany(Pairs).ToList();
            return items.Count > 0 ? items : null;
        }

        /// <summary>Plain "name=value; name=value" (also "a=1;b=2" without spaces), on one line.</summary>
        private static List<Item> FromPairs(string text)
        {
            if (text.Contains('\n') || !text.Contains('=')) return null;
            var items = Pairs(text).ToList();
            return items.Count > 0 ? items : null;
        }

        private static IEnumerable<Item> Pairs(string header) =>
            header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => (Part: part, Eq: part.IndexOf('=')))
                .Where(p => p.Eq > 0 && !p.Part[..p.Eq].Contains(' '))
                .Select(p => new Item(p.Part[..p.Eq].Trim(), p.Part[(p.Eq + 1)..].Trim(), null));
    }
}
