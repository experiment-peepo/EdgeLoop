using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    public enum SignInOutcome
    {
        SignedIn,
        /// <summary>The site said the username or password is wrong.</summary>
        Rejected,
        /// <summary>The site wants a captcha solved, which only a person in a browser can do.</summary>
        CaptchaRequired,
        /// <summary>Couldn't reach the site, or it answered in a way EdgeLoop doesn't recognise.</summary>
        Failed,
    }

    /// <summary>What a sign-in came back with: the outcome, a sentence for Settings, and the session cookies on success.</summary>
    public sealed record SiteSignInResult(SignInOutcome Outcome, string Message, string Cookies = null)
    {
        public bool Success => Outcome == SignInOutcome.SignedIn;
    }

    /// <summary>
    /// Signs in to Hypnotube and Rule34Video with the account in Settings, keeps the session cookies
    /// (encrypted), checks now and then that they still work, and signs in again when they don't.
    /// Neither site has an API, so this posts to each site's own login form as a browser does:
    /// <list type="bullet">
    /// <item>Hypnotube: POST /login with ahd_username and ahd_password. Signed-in pages link to "/logout".</item>
    /// <item>Rule34Video (KVS): GET /login/ for the form's hidden fields (remember_me_csrf_token, ...), then
    /// POST them with username, pass, remember_me=1, mode=async and format=json; the reply is JSON.
    /// Signed in when js_user_status gives an id above 0.</item>
    /// </list>
    /// When a site asks for a captcha it stops and says so: pasted cookies in Settings get round that.
    /// </summary>
    public sealed class SiteSignIn
    {
        /// <summary>How long a checked session is trusted before it's checked again.</summary>
        public static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(30);

        /// <summary>After a failed sign-in, how long to wait before trying again, so the site doesn't see a burst of logins.</summary>
        public static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(10);

        private const string HypnotubeHome = "https://hypnotube.com/";
        private const string HypnotubeLogin = "https://hypnotube.com/login";
        private const string Rule34VideoHome = "https://rule34video.com/";
        private const string Rule34VideoLogin = "https://rule34video.com/login/";
        private const string Rule34VideoUserStatus = "https://rule34video.com/?mode=async&format=json&action=js_user_status";

        private static readonly Lazy<SiteSignIn> _shared = new(() => new SiteSignIn());
        public static SiteSignIn Shared => _shared.Value;

        private readonly Func<CookieContainer, HttpMessageHandler> _handlerFactory;
        private readonly Func<UserSettings> _settings;
        private readonly Func<DateTime> _now;
        private readonly ConcurrentDictionary<Site, SemaphoreSlim> _locks = new();
        private readonly ConcurrentDictionary<Site, State> _state = new();

        private sealed class State
        {
            public string Account;
            public DateTime CheckedAt;
            public DateTime FailedAt;
            public string LastError;
        }

        /// <param name="handlerFactory">Makes the handler for one cookie jar; tests pass a fake site.</param>
        /// <param name="settings">Where the accounts and sessions live; defaults to the app's settings.</param>
        /// <param name="now">The clock (UTC); tests pass a fake.</param>
        public SiteSignIn(Func<CookieContainer, HttpMessageHandler> handlerFactory = null, Func<UserSettings> settings = null, Func<DateTime> now = null)
        {
            _handlerFactory = handlerFactory ?? (jar => new HttpClientHandler
            {
                CookieContainer = jar,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            });
            _settings = settings ?? (() => App.Settings);
            _now = now ?? (() => DateTime.UtcNow);
        }

        public static bool Supports(Site site) => site is Site.Hypnotube or Site.Rule34Video;

        /// <summary>Whether Settings has a username and password for the site.</summary>
        public bool HasAccount(Site site) => Account(site, out _, out _) != null;

        /// <summary>Why the last automatic sign-in failed, or null.</summary>
        public string LastError(Site site) => _state.TryGetValue(site, out var state) ? state.LastError : null;

        /// <summary>Signs in with this account and returns the session cookies on success. Saves nothing.</summary>
        public async Task<SiteSignInResult> SignInAsync(Site site, string username, string password, CancellationToken cancellationToken = default)
        {
            if (!Supports(site)) return new(SignInOutcome.Failed, $"EdgeLoop can't sign in to {site}.");
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return new(SignInOutcome.Failed, "Enter your username and password first.");

            try
            {
                var jar = new CookieContainer();
                using var http = CreateClient(jar);
                return site == Site.Hypnotube
                    ? await SignInToHypnotubeAsync(http, jar, username.Trim(), password, cancellationToken)
                    : await SignInToRule34VideoAsync(http, jar, username.Trim(), password, cancellationToken);
            }
            catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
            {
                return new(SignInOutcome.Failed, $"Couldn't reach {Name(site)}: {ex.Message}");
            }
        }

        /// <summary>Whether these cookies are a signed-in session on the site.</summary>
        public async Task<bool> IsSignedInAsync(Site site, string cookies, CancellationToken cancellationToken = default)
        {
            if (!Supports(site) || string.IsNullOrWhiteSpace(cookies)) return false;

            var jar = new CookieContainer();
            AddCookies(jar, new Uri(site == Site.Hypnotube ? HypnotubeHome : Rule34VideoHome), cookies);
            using var http = CreateClient(jar);

            if (site == Site.Hypnotube)
            {
                return ShowsSignedIn(await http.GetStringAsync(HypnotubeHome, cancellationToken));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Rule34VideoUserStatus);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var response = await http.SendAsync(request, cancellationToken);
            return IsMember(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        /// <summary>
        /// Before fetching the site's pages: when Settings has an account, makes sure its session is
        /// signed in, signing in again if it ran out. Checks at most every <see cref="RecheckAfter"/>, and
        /// after a failed sign-in waits <see cref="PauseAfterFailure"/>. Does nothing without an account.
        /// </summary>
        public async Task EnsureSignedInAsync(Site site, CancellationToken cancellationToken = default)
        {
            if (!Supports(site)) return;
            var account = Account(site, out var username, out var password);
            if (account == null) return;

            var gate = _locks.GetOrAdd(site, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var state = _state.GetOrAdd(site, _ => new State());
                if (state.Account != account)
                {
                    // A different account (or password) in Settings: start over
                    state.Account = account;
                    state.CheckedAt = state.FailedAt = default;
                    state.LastError = null;
                }

                var now = _now();
                if (state.CheckedAt != default && now - state.CheckedAt < RecheckAfter) return;

                var session = Session(site);
                if (session != null)
                {
                    try
                    {
                        if (await IsSignedInAsync(site, session, cancellationToken))
                        {
                            state.CheckedAt = now;
                            return;
                        }
                    }
                    catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
                    {
                        // Can't tell right now; the page fetch that follows will hit the same problem anyway
                        Logger.Warning($"[SignIn] Couldn't check the {Name(site)} session: {ex.Message}");
                        return;
                    }
                }

                if (state.FailedAt != default && now - state.FailedAt < PauseAfterFailure) return;

                Logger.Info($"[SignIn] Signing in to {Name(site)}");
                var result = await SignInAsync(site, username, password, cancellationToken);
                if (result.Success)
                {
                    SaveSession(site, result.Cookies);
                    state.CheckedAt = now;
                    state.FailedAt = default;
                    state.LastError = null;
                    Logger.Info($"[SignIn] Signed in to {Name(site)}");
                }
                else
                {
                    // A dead session would hide any pasted cookies, which are the fallback
                    if (session != null) SaveSession(site, null);
                    state.FailedAt = now;
                    state.LastError = result.Message;
                    Logger.Warning($"[SignIn] {Name(site)}: {result.Message}");
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>A page came back signed out though the account's session should be signed in: check it again next time.</summary>
        public void NoteSignedOutPage(Site site)
        {
            if (_state.TryGetValue(site, out var state) && state.CheckedAt != default)
            {
                state.CheckedAt = default;
                Logger.Info($"[SignIn] A {Name(site)} page came back signed out; the session will be checked again");
            }
        }

        /// <summary>Whether a Hypnotube page is a signed-in one (it links to the logout page).</summary>
        public static bool ShowsSignedIn(string hypnotubeHtml) =>
            hypnotubeHtml != null && hypnotubeHtml.Contains("/logout\"", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether Rule34Video's js_user_status reply ({"id":123}) is a member's.</summary>
        internal static bool IsMember(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json ?? "");
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("id", out var id) &&
                       (id.ValueKind == JsonValueKind.Number ? id.GetInt64() : long.TryParse(id.GetString(), out var n) ? n : 0) > 0;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return false;
            }
        }

        /// <summary>The hidden fields of Rule34Video's login form (remember_me_csrf_token, action, ...), or null if there's no such form.</summary>
        internal static Dictionary<string, string> LoginFormFields(string html)
        {
            // Read the form's own markup: HTML parsers can treat <form> as empty and put its inputs beside it
            if (string.IsNullOrEmpty(html)) return null;
            var start = Regex.Match(html, @"<form\b[^>]*\baction\s*=\s*[""'][^""']*/login/[""'][^>]*>", RegexOptions.IgnoreCase);
            if (!start.Success) return null;
            var end = html.IndexOf("</form>", start.Index, StringComparison.OrdinalIgnoreCase);
            var form = end > start.Index ? html[start.Index..end] : html[start.Index..];

            static string Attribute(string tag, string name)
            {
                var m = Regex.Match(tag, $@"\b{name}\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
                return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match input in Regex.Matches(form, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
            {
                if (!string.Equals(Attribute(input.Value, "type"), "hidden", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Attribute(input.Value, "name");
                if (!string.IsNullOrEmpty(name)) fields[name] = WebUtility.HtmlDecode(Attribute(input.Value, "value") ?? "");
            }
            return fields;
        }

        private async Task<SiteSignInResult> SignInToHypnotubeAsync(HttpClient http, CookieContainer jar, string username, string password, CancellationToken cancellationToken)
        {
            // The login page sets the session cookie the form is posted with
            var page = await http.GetStringAsync(HypnotubeLogin, cancellationToken);
            if (SiteRestrictions.Detect(Site.Hypnotube, page) == SiteRestriction.LocationBlocked)
                return new(SignInOutcome.Failed, SiteRestrictions.Describe(SiteRestriction.LocationBlocked));

            using var request = new HttpRequestMessage(HttpMethod.Post, HypnotubeLogin)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["ahd_username"] = username,
                    ["ahd_password"] = password,
                    ["Submit"] = "",
                }),
            };
            request.Headers.Referrer = new Uri(HypnotubeLogin);
            request.Headers.TryAddWithoutValidation("Origin", "https://hypnotube.com");

            using var response = await http.SendAsync(request, cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            if (ShowsSignedIn(html))
                return new(SignInOutcome.SignedIn, "Hypnotube accepted the username and password.", CookiesFor(jar, HypnotubeHome));
            if (html.Contains("captcha.php", StringComparison.OrdinalIgnoreCase) || html.Contains("captchaaa", StringComparison.OrdinalIgnoreCase))
                return new(SignInOutcome.CaptchaRequired, "Hypnotube asked for a captcha, which EdgeLoop can't answer. Paste your cookies below instead.");
            if (SiteRestrictions.Detect(Site.Hypnotube, html) == SiteRestriction.LocationBlocked)
                return new(SignInOutcome.Failed, SiteRestrictions.Describe(SiteRestriction.LocationBlocked));
            return new(SignInOutcome.Rejected, "Hypnotube didn't accept that username and password (it wants the username, not the email).");
        }

        private async Task<SiteSignInResult> SignInToRule34VideoAsync(HttpClient http, CookieContainer jar, string username, string password, CancellationToken cancellationToken)
        {
            // The login page carries a one-time remember_me_csrf_token, tied to the session cookie it sets
            var fields = LoginFormFields(await http.GetStringAsync(Rule34VideoLogin, cancellationToken));
            if (fields == null)
                return new(SignInOutcome.Failed, "Rule34Video's sign-in page has changed, so EdgeLoop couldn't sign in. Paste your cookies below instead.");

            fields["username"] = username;
            fields["pass"] = password;
            fields["remember_me"] = "1";
            fields["mode"] = "async";
            fields["format"] = "json";
            fields.TryAdd("action", "login");

            using var request = new HttpRequestMessage(HttpMethod.Post, Rule34VideoLogin) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Referrer = new Uri(Rule34VideoLogin);
            request.Headers.TryAddWithoutValidation("Origin", "https://rule34video.com");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");

            using var response = await http.SendAsync(request, cancellationToken);
            var reply = await response.Content.ReadAsStringAsync(cancellationToken);

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(reply);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return new(SignInOutcome.Failed, $"Rule34Video answered the sign-in unexpectedly ({(int)response.StatusCode}). Paste your cookies below instead.");
            }

            var status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return new(SignInOutcome.SignedIn, "Rule34Video accepted the email/username and password.", CookiesFor(jar, Rule34VideoHome));

            // {"status":"failure","errors":[{"field":"pass","code":"invalid","message":"..."}]}; a "code" field is the captcha
            var errors = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList()
                : new List<JsonElement>();
            string Text(JsonElement x, string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            if (errors.Any(x => Text(x, "field") == "code" || (Text(x, "message") ?? "").Contains("captcha", StringComparison.OrdinalIgnoreCase)))
                return new(SignInOutcome.CaptchaRequired, "Rule34Video asked for a captcha, which EdgeLoop can't answer. Paste your cookies below instead.");

            var message = errors.Select(x => Text(x, "message")).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            return new(SignInOutcome.Rejected, message != null
                ? $"Rule34Video didn't accept that: {WebUtility.HtmlDecode(message).Trim()}"
                : "Rule34Video didn't accept that email/username and password.");
        }

        private HttpClient CreateClient(CookieContainer jar)
        {
            var http = new HttpClient(_handlerFactory(jar)) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.BrowserUserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return http;
        }

        private string Account(Site site, out string username, out string password)
        {
            var settings = _settings();
            username = site switch
            {
                Site.Hypnotube => settings?.HypnotubeUsername,
                Site.Rule34Video => settings?.Rule34VideoUsername,
                _ => null,
            };
            password = site switch
            {
                Site.Hypnotube => settings?.HypnotubePassword,
                Site.Rule34Video => settings?.Rule34VideoPassword,
                _ => null,
            };
            username = username?.Trim();
            return string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ? null : username + "\u001F" + password;
        }

        private string Session(Site site)
        {
            var settings = _settings();
            var cookies = site == Site.Hypnotube ? settings?.HypnotubeSession : settings?.Rule34VideoSession;
            return string.IsNullOrWhiteSpace(cookies) ? null : cookies;
        }

        private void SaveSession(Site site, string cookies)
        {
            var settings = _settings();
            if (settings == null) return;
            if (site == Site.Hypnotube) settings.HypnotubeSession = cookies;
            else settings.Rule34VideoSession = cookies;
            settings.Save();
        }

        /// <summary>The jar's unexpired cookies for a site, as a Cookie header value.</summary>
        internal static string CookiesFor(CookieContainer jar, string url)
        {
            var cookies = jar.GetCookies(new Uri(url)).Cast<Cookie>().Where(c => !c.Expired).Select(c => $"{c.Name}={c.Value}").ToList();
            return cookies.Count == 0 ? null : string.Join("; ", cookies);
        }

        private static void AddCookies(CookieContainer jar, Uri site, string header)
        {
            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                try
                {
                    jar.Add(new Cookie(part[..eq].Trim(), part[(eq + 1)..].Trim(), "/", site.Host));
                }
                catch (CookieException)
                {
                    // A value .NET can't hold (e.g. a comma); the rest still go
                }
            }
        }

        private static bool IsNetworkError(Exception ex, CancellationToken cancellationToken) =>
            ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

        private static string Name(Site site) => site == Site.Rule34Video ? "Rule34Video" : site.ToString();
    }
}
