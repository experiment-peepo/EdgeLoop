using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    /// <summary>What a sign-in check came back with, worded for the Settings window.</summary>
    public sealed record IwaraSignInResult(bool Success, string Message);

    /// <summary>
    /// Iwara's API (api.iwara.tv), for signing in and reading playlists.
    ///
    /// Iwara signs in with an email and password. That gives a user token (about three weeks), which
    /// is swapped for an access token (about an hour) sent as "Authorization: Bearer". Cookies play
    /// no part. Signed out, the API only returns videos rated "ecchi".
    ///
    /// The API sits behind Cloudflare, which refuses TLS connections that don't name their protocol
    /// (ALPN). .NET's HttpClient names none for HTTP/1.1, and Cloudflare refuses its HTTP/2 too, so
    /// this client makes the TLS connection itself, naming "http/1.1", and the handler sends HTTP/1.1
    /// over it. Requests are addressed "http://" only so the handler doesn't add TLS a second time.
    /// </summary>
    public sealed class IwaraApi
    {
        public const string ApiHost = "api.iwara.tv";
        private static readonly TimeSpan FailedSignInPause = TimeSpan.FromMinutes(5);

        private static readonly Lazy<IwaraApi> _shared = new(() => new IwaraApi());
        public static IwaraApi Shared => _shared.Value;

        private readonly HttpClient _http;
        private readonly Func<UserSettings> _settings;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string _account;
        private string _userToken;
        private string _accessToken;
        private DateTime _signInFailedAt;

        /// <param name="handler">Sends the requests; tests pass a fake. Defaults to the TLS connection described above.</param>
        /// <param name="settings">Where the account comes from; defaults to the app's settings.</param>
        public IwaraApi(HttpMessageHandler handler = null, Func<UserSettings> settings = null)
        {
            _http = new HttpClient(handler ?? CreateHandler())
            {
                BaseAddress = new Uri("http://" + ApiHost + "/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.BrowserUserAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.iwara.tv/");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.iwara.tv");
            _settings = settings ?? (() => App.Settings);
        }

        /// <summary>Why signing in with the account in Settings failed last time, or null.</summary>
        public string LastSignInError { get; private set; }

        /// <summary>Whether Settings has an Iwara email and password.</summary>
        public bool HasAccount => Account(out _, out _) != null;

        /// <summary>Signs in with an email and password to see whether Iwara accepts them. Keeps nothing.</summary>
        public async Task<IwaraSignInResult> CheckSignInAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return new(false, "Enter your Iwara email and password first.");
            try
            {
                var (token, error) = await LoginAsync(email.Trim(), password, cancellationToken);
                return token != null ? new(true, "Iwara accepted the email and password.") : new(false, error);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return new(false, $"Couldn't reach Iwara: {ex.Message}");
            }
        }

        /// <summary>
        /// The "Authorization" value for API calls, or null when Settings has no Iwara account or it
        /// couldn't sign in (then <see cref="LastSignInError"/> says why). Signs in again when the
        /// user token runs out, and waits a few minutes after a failed sign-in before trying again.
        /// </summary>
        public async Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            var account = Account(out var email, out var password);
            if (account == null) return null;

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (account != _account)
                {
                    _account = account;
                    _userToken = _accessToken = null;
                    _signInFailedAt = default;
                    LastSignInError = null;
                }

                if (_accessToken != null && !ExpiresSoon(_accessToken)) return "Bearer " + _accessToken;
                if (_userToken == null && DateTime.UtcNow - _signInFailedAt < FailedSignInPause) return null;

                // A user token can be revoked early (password changed): then sign in once more
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (_userToken == null || ExpiresSoon(_userToken))
                    {
                        var (token, error) = await LoginAsync(email, password, cancellationToken);
                        if (token == null)
                        {
                            _signInFailedAt = DateTime.UtcNow;
                            LastSignInError = error;
                            Logger.Warning($"[Iwara] Sign-in failed: {error}");
                            return null;
                        }
                        _userToken = token;
                        LastSignInError = null;
                    }

                    _accessToken = await GetAccessTokenAsync(_userToken, cancellationToken);
                    if (_accessToken != null) return "Bearer " + _accessToken;
                    _userToken = null;
                }

                _signInFailedAt = DateTime.UtcNow;
                LastSignInError = "Iwara accepted the sign-in but wouldn't give access to videos.";
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _signInFailedAt = DateTime.UtcNow;
                LastSignInError = $"Couldn't reach Iwara to sign in: {ex.Message}";
                Logger.Warning($"[Iwara] {LastSignInError}");
                return null;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// Reads an API path ("playlist/{id}?page=0") as JSON, sending the authorization if given.
        /// Throws <see cref="HttpRequestException"/> (with the status code) when Iwara refuses.
        /// </summary>
        public async Task<JsonDocument> GetJsonAsync(string pathAndQuery, string authorization, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
            if (authorization != null) request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Iwara API refused {pathAndQuery} ({(int)response.StatusCode})", null, response.StatusCode);
            }
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        private string Account(out string email, out string password)
        {
            var settings = _settings();
            email = settings?.IwaraEmail?.Trim();
            password = settings?.IwaraPassword;
            return string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) ? null : email + "\u001F" + password;
        }

        private async Task<(string Token, string Error)> LoginAsync(string email, string password, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = email, ["password"] = password });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("user/login", content, cancellationToken);
            var reply = await response.Content.ReadAsStringAsync(cancellationToken);

            var token = JsonText(reply, "token");
            if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(token)) return (token, null);

            var message = JsonText(reply, "message");
            if (message != null && message.Contains("invalidLogin", StringComparison.OrdinalIgnoreCase))
                return (null, "Iwara didn't accept that email and password.");
            return (null, $"Iwara refused the sign-in ({(int)response.StatusCode}{(message != null ? ": " + message : "")}).");
        }

        private async Task<string> GetAccessTokenAsync(string userToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "user/token")
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var token = JsonText(await response.Content.ReadAsStringAsync(cancellationToken), "accessToken");
            return string.IsNullOrEmpty(token) ? null : token;
        }

        /// <summary>When a token (a JWT) runs out, from its "exp" claim, or null if it doesn't say.</summary>
        internal static DateTime? TokenExpiry(string token)
        {
            var parts = token?.Split('.');
            if (parts == null || parts.Length < 2) return null;
            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
                return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    : null;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                return null;
            }
        }

        // A token without an expiry is treated as spent, as yt-dlp does
        private static bool ExpiresSoon(string token) =>
            TokenExpiry(token) is not DateTime expiry || expiry <= DateTime.UtcNow.AddMinutes(2);

        private static string JsonText(string json, string property)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null; // Cloudflare's HTML error page, for instance
            }
        }

        private static SocketsHttpHandler CreateHandler() => new()
        {
            // Requests are addressed http:// (see above): a proxy would receive them unencrypted
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectWithTlsAsync,
        };

        /// <summary>A TLS connection to the API host that names "http/1.1", so Cloudflare accepts it.</summary>
        private static async ValueTask<Stream> ConnectWithTlsAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var host = context.DnsEndPoint.Host;
            if (!host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"The Iwara client only connects to {ApiHost}, not {host}.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(host, 443, cancellationToken);
                var tls = new SslStream(new NetworkStream(socket, ownsSocket: true));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                }, cancellationToken);
                return tls;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
