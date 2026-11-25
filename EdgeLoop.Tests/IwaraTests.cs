using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using AwesomeAssertions;
using Moq;

namespace EdgeLoop.Tests
{
    /// <summary>Iwara: signing in, playlists through its API, and handing the account to yt-dlp.</summary>
    public class IwaraTests : TestBase
    {
        private const string PlaylistId = "d21b1962-bb83-470d-82a1-7bed0e1e159f";
        private const string PlaylistUrl = "https://www.iwara.tv/playlist/" + PlaylistId;

        private readonly FakeIwara _iwara = new();

        private void SignedInAs(string email, string password)
        {
            MockSettings.Setup(s => s.IwaraEmail).Returns(email);
            MockSettings.Setup(s => s.IwaraPasswordEncrypted).Returns(password); // plain text reads back as-is
        }

        private IwaraApi Api() => new(_iwara, () => MockSettings.Object);

        private PlaylistImporter Importer(IwaraApi api = null) =>
            new(MockExtractor.Object, Mock.Of<IHtmlFetcher>(), null, api ?? Api());

        // --- Signing in ---------------------------------------------------------------------------

        [Fact]
        public async Task NoAccount_MeansNoSignIn_AndNoRequests()
        {
            (await Api().GetAuthorizationAsync()).Should().BeNull();
            _iwara.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task SignsIn_ThenSwapsTheUserTokenForAnAccessToken_AndReusesIt()
        {
            SignedInAs("me@example.com", "secret");
            var api = Api();

            var first = await api.GetAuthorizationAsync();
            var second = await api.GetAuthorizationAsync();

            first.Should().Be("Bearer " + _iwara.AccessToken);
            second.Should().Be(first);
            _iwara.Requests.Select(r => r.Line).Should().Equal("POST /user/login", "POST /user/token");
            _iwara.Requests[0].Body.Should().Contain("\"email\":\"me@example.com\"").And.Contain("\"password\":\"secret\"");
            _iwara.Requests[1].Authorization.Should().Be("Bearer " + _iwara.UserToken);
        }

        [Fact]
        public async Task RejectedPassword_SaysSo_AndDoesntTryAgainStraightAway()
        {
            SignedInAs("me@example.com", "wrong");
            _iwara.AcceptPassword = "secret";
            var api = Api();

            (await api.GetAuthorizationAsync()).Should().BeNull();
            (await api.GetAuthorizationAsync()).Should().BeNull();

            api.LastSignInError.Should().Be("Iwara didn't accept that email and password.");
            _iwara.Requests.Count(r => r.Line == "POST /user/login").Should().Be(1);
        }

        [Fact]
        public async Task CheckSignIn_ReportsWhetherIwaraAcceptsTheAccount()
        {
            _iwara.AcceptPassword = "secret";
            var api = Api();

            (await api.CheckSignInAsync("me@example.com", "secret")).Success.Should().BeTrue();
            (await api.CheckSignInAsync("me@example.com", "nope")).Message.Should().Be("Iwara didn't accept that email and password.");
            (await api.CheckSignInAsync("", "")).Message.Should().Be("Enter your Iwara email and password first.");
        }

        [Fact]
        public void TokenExpiry_ComesFromTheTokensExpClaim()
        {
            var expiry = new DateTime(2026, 12, 13, 8, 0, 0, DateTimeKind.Utc);

            IwaraApi.TokenExpiry(FakeIwara.Jwt(expiry)).Should().Be(expiry);
            IwaraApi.TokenExpiry("not-a-token").Should().BeNull();
        }

        // --- Playlists ----------------------------------------------------------------------------

        [Fact]
        public async Task Playlist_SignedOut_ImportsWhatIwaraShows_AndSaysTheRestNeedAnAccount()
        {
            _iwara.Playlist(declared: 5, Video("82X7wphqJgvloP", "first-video", "First Video"), Video("ypbizTN2Wo2I9X", null, "No Slug"));

            var importer = Importer();
            var items = await importer.ImportPlaylistAsync(PlaylistUrl);

            items.Select(i => i.FilePath).Should().Equal(
                "https://www.iwara.tv/video/82X7wphqJgvloP/first-video",
                "https://www.iwara.tv/video/ypbizTN2Wo2I9X");
            items.Select(i => i.Title).Should().Equal("First Video", "No Slug");
            importer.ImportNotice.Should().StartWith("Iwara showed 2 of this playlist's 5 videos.").And.Contain("Settings");
            _iwara.Requests.Should().OnlyContain(r => r.Authorization == null);
            MockExtractor.Verify(e => e.ExtractVideoMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Playlist_SignedIn_ReadsEveryPage_WithTheAccount()
        {
            SignedInAs("me@example.com", "secret");
            var videos = Enumerable.Range(0, 60).Select(i => Video($"id{i:D3}abcdefgh", $"slug-{i}", $"Video {i}")).ToArray();
            _iwara.Playlist(declared: 60, videos);

            var importer = Importer();
            var items = await importer.ImportPlaylistAsync(PlaylistUrl);

            items.Should().HaveCount(60);
            importer.ImportNotice.Should().BeNull();
            var pages = _iwara.Requests.Where(r => r.Line.StartsWith("GET /playlist/")).ToList();
            pages.Select(r => r.Line).Should().Equal(
                $"GET /playlist/{PlaylistId}?page=0&limit=50",
                $"GET /playlist/{PlaylistId}?page=1&limit=50");
            pages.Should().OnlyContain(r => r.Authorization == "Bearer " + _iwara.AccessToken);
        }

        [Fact]
        public async Task Playlist_WhenTheApiRefuses_ImportsNothing_AndClaimsNothing()
        {
            _iwara.RefuseApi = true;

            var importer = Importer();
            var items = await importer.ImportPlaylistAsync(PlaylistUrl);

            items.Should().BeEmpty();
            importer.ImportNotice.Should().BeNull();
        }

        // --- File links --------------------------------------------------------------------------

        [Theory]
        [InlineData("https://hime.iwara.tv/view?hash=ff37&filename=91d19d93_Source.mp4&path=2025%2F05%2F08&expires=1795355309", true)]
        [InlineData("https://files.iwara.tv/download?filename=x_540.mp4&expires=1", true)]
        [InlineData("https://www.iwara.tv/video/FXNY1VuxsOLw4B/vam", false)]
        [InlineData("https://hypnotube.com/video/x-1.html", false)]
        [InlineData("https://example.com/watch?file=notes.txt", false)]
        public void IwaraFileLinks_AreVideos_NotPages(string url, bool isFile)
        {
            FileValidator.IsDirectMediaUrl(url).Should().Be(isFile);
            FileValidator.IsPageUrl(url).Should().Be(!isFile);
        }

        [Fact]
        public void IwaraFileLinks_KeepTheirSignature()
        {
            const string file = "https://hime.iwara.tv/view?hash=ff37&filename=91d19d93_Source.mp4&expires=1795355309";

            SiteRules.NormalizePageUrl(file).Should().Be(file, "the query is what lets Iwara serve the file");
        }

        [Fact]
        public async Task IwaraFileLink_IsPlayedAsIs_NotExtractedAgain()
        {
            const string file = "https://hime.iwara.tv/view?hash=ff37&filename=91d19d93_Source.mp4&expires=1795355309";
            var fetcher = new Mock<IHtmlFetcher>(MockBehavior.Strict); // any fetch would throw

            (await new VideoUrlExtractor(fetcher.Object).ExtractVideoUrlAsync(file)).Should().Be(file);
        }

        // --- yt-dlp -------------------------------------------------------------------------------

        [Fact]
        public void YtDlp_GetsTheIwaraAccount_AndNoBrowserCookies()
        {
            SignedInAs("me@example.com", "secret");
            using var ytDlp = new YtDlpService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-yt-dlp.exe"));

            var options = ytDlp.BuildOptionsForUrl("https://www.iwara.tv/video/82X7wphqJgvloP", out var tempFiles);
            var accountFile = tempFiles.AccountFile;

            // Never on yt-dlp's command line, where other programs and Windows' process audit log can read it
            options.Username.Should().BeNull();
            options.Password.Should().BeNull();
            options.ToString().Should().Contain("--config-locations").And.NotContain("secret").And.NotContain("me@example.com");
            options.CookiesFromBrowser.Should().BeNull("Iwara signs in with an account, not cookies");

            // A UTF-8 file (with its byte-order mark, so yt-dlp doesn't fall back to the Windows code page)
            var bytes = System.IO.File.ReadAllBytes(accountFile);
            bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
            System.IO.File.ReadAllText(accountFile).Should().Be("--username 'me@example.com'\n--password 'secret'\n");

            tempFiles.Dispose();
            System.IO.File.Exists(accountFile).Should().BeFalse("it's deleted when yt-dlp is done");
        }

        [Theory]
        [InlineData("plain", "'plain'")]
        [InlineData("it's", "'it'\"'\"'s'")]
        [InlineData("a b #c $d \\e", "'a b #c $d \\e'")]
        public void YtDlp_AccountValues_AreShellQuoted(string value, string quoted)
        {
            // yt-dlp reads config files with Python's shlex.split(comments=True): single quotes keep everything literal
            YtDlpService.ConfigQuote(value).Should().Be(quoted);
        }

        [Fact]
        public void YtDlp_Streaming_AsksForOneFileWithPictureAndSound()
        {
            using var ytDlp = new YtDlpService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-yt-dlp.exe"));

            // "bestvideo+bestaudio" answers with two files to merge and no link the player can open
            ytDlp.BuildOptionsForUrl("https://www.youtube.com/watch?v=jNQXAC9IVRw", out _).Format.Should().Be("best");
            ytDlp.BuildOptionsForUrl("https://www.iwara.tv/video/82X7wphqJgvloP", out _).Format.Should().StartWith("Source/");
        }

        [Fact]
        public void YtDlp_GetsNoAccountForOtherSites()
        {
            SignedInAs("me@example.com", "secret");
            using var ytDlp = new YtDlpService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-yt-dlp.exe"));

            ytDlp.BuildOptionsForUrl("https://hypnotube.com/video/x-1.html", out _).Username.Should().BeNull();
        }

        [Theory]
        [InlineData("ERROR: [iwara] abc: Video may need login to view. Use --username and --password, --netrc-cmd, or --netrc (iwara) to provide account credentials", SiteRestriction.SignInRequired)]
        [InlineData("ERROR: [iwara] abc: Private video. Login if you have permissions to watch", SiteRestriction.SignInRequired)]
        [InlineData("ERROR: [iwara] Invalid login credentials", SiteRestriction.SignInRejected)]
        [InlineData("ERROR: [iwara] abc: This video is unplayable", SiteRestriction.None)]
        public void YtDlpErrors_ThatNeedAnAccount_AreRecognised(string error, SiteRestriction expected)
        {
            SiteRestrictions.FromYtDlpError(Site.Iwara, error).Should().Be(expected);
            SiteRestrictions.FromYtDlpError(Site.Hypnotube, error).Should().Be(SiteRestriction.None);
        }

        [Fact]
        public void YouTube_WithoutAJavaScriptRuntime_IsNamed()
        {
            // As yt-dlp 2026.10.26 reports it when Node is older than 22 and Deno isn't installed
            const string error = "WARNING: [youtube] jNQXAC9IVRw: n challenge solving failed: Some formats may be missing. Ensure you have a supported JavaScript runtime and challenge solver script distribution installed.\n" +
                                 "ERROR: [youtube] jNQXAC9IVRw: No video formats found!";

            SiteRestrictions.FromYtDlpError(Site.YouTube, error).Should().Be(SiteRestriction.JavaScriptRuntimeMissing);
            SiteRestrictions.FromYtDlpError(Site.YouTube, "ERROR: [youtube] x: Video unavailable").Should().Be(SiteRestriction.None);
            PlaybackFailureText.Reason($"Failed to resolve video URL: {SiteRestrictions.Describe(SiteRestriction.JavaScriptRuntimeMissing)}")
                .Should().StartWith("YouTube needs Node.js 22 or newer");
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", "YouTube video jNQXAC9IVRw")]
        [InlineData("https://youtu.be/jNQXAC9IVRw", "YouTube video jNQXAC9IVRw")]
        [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw", "YouTube video jNQXAC9IVRw")]
        [InlineData("https://example.com/videos/some-clip", "some-clip")]
        public void LastResortTitles_NeverSayWatch(string url, string expected)
        {
            TitleSanitizer.FromUrlSlug(url).Should().NotBe("Watch");
            PlaylistImporter.FallbackTitle(url).Should().Be(expected);
        }

        [Fact]
        public void PlaybackFailure_SaysWhenIwaraNeedsAnAccount()
        {
            PlaybackFailureText.Reason($"Failed to resolve video URL: {SiteRestrictions.Describe(SiteRestriction.SignInRequired)}")
                .Should().StartWith("Iwara only shows it to signed-in accounts");
        }

        // --- Settings -----------------------------------------------------------------------------

        [Fact]
        public async Task Settings_CheckSignIn_ShowsWhatIwaraSaid()
        {
            _iwara.AcceptPassword = "secret";
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>(), Api())
            {
                IwaraEmail = "me@example.com",
                IwaraPassword = "nope",
            };

            await settings.CheckIwaraSignInAsync();
            settings.IwaraSignInSucceeded.Should().BeFalse();
            settings.IwaraSignInStatus.Should().Be("Iwara didn't accept that email and password.");

            settings.IwaraPassword = "secret";
            settings.IwaraSignInStatus.Should().BeNull("an edit clears the old result");
            await settings.CheckIwaraSignInAsync();
            settings.IwaraSignInSucceeded.Should().BeTrue();
        }

        [Fact]
        public void Settings_SavesTheAccount_AndDropsIwaraCookies()
        {
            MockSettings.SetupProperty(s => s.IwaraEmail);
            MockSettings.SetupProperty(s => s.IwaraPasswordEncrypted);
            MockSettings.SetupProperty(s => s.IwaraCookiesEncrypted, "sid=old");
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>(), Api());

            settings.IwaraEmail = "  me@example.com ";
            settings.IwaraPassword = "secret";
            settings.HasChanges.Should().BeTrue();
            settings.Save();

            MockSettings.Object.IwaraEmail.Should().Be("me@example.com");
            MockSettings.Object.IwaraPassword.Should().Be("secret");
            MockSettings.Object.IwaraCookies.Should().BeNull("they never signed anyone in");
        }

        // --- A fake api.iwara.tv ------------------------------------------------------------------

        private static Dictionary<string, object> Video(string id, string slug, string title) =>
            new() { ["id"] = id, ["slug"] = slug, ["title"] = title, ["rating"] = "ecchi" };

        private sealed class FakeIwara : HttpMessageHandler
        {
            public readonly List<(string Line, string Authorization, string Body)> Requests = new();
            public string AcceptPassword = "secret";
            public bool RefuseApi;
            public readonly string UserToken = Jwt(DateTime.UtcNow.AddDays(21));
            public readonly string AccessToken = Jwt(DateTime.UtcNow.AddHours(1));
            private int _declared;
            private Dictionary<string, object>[] _videos = Array.Empty<Dictionary<string, object>>();

            public void Playlist(int declared, params Dictionary<string, object>[] videos)
            {
                _declared = declared;
                _videos = videos;
            }

            public static string Jwt(DateTime expiry)
            {
                static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return $"{Part("{\"alg\":\"HS256\"}")}.{Part($"{{\"exp\":{new DateTimeOffset(expiry).ToUnixTimeSeconds()}}}")}.signature";
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var line = $"{request.Method} {request.RequestUri!.PathAndQuery}";
                Requests.Add((line, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, body));

                if (RefuseApi) return Reply(HttpStatusCode.Forbidden, "<html>Cloudflare</html>");

                if (line == "POST /user/login")
                {
                    var password = JsonDocument.Parse(body!).RootElement.GetProperty("password").GetString();
                    return password == AcceptPassword
                        ? Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { token = UserToken }))
                        : Reply(HttpStatusCode.BadRequest, "{\"message\":\"errors.invalidLogin\"}");
                }
                if (line == "POST /user/token")
                {
                    return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { accessToken = AccessToken }));
                }
                if (request.RequestUri.AbsolutePath == "/playlist/" + PlaylistId)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                    int page = int.Parse(query["page"]!), limit = int.Parse(query["limit"]!);
                    var results = _videos.Skip(page * limit).Take(limit).ToArray();
                    return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new
                    {
                        playlist = new { id = PlaylistId, title = "A playlist", numVideos = _declared },
                        results,
                        count = _videos.Length,
                        limit,
                        page,
                    }));
                }
                return Reply(HttpStatusCode.NotFound, "{\"message\":\"errors.notFound\"}");
            }

            private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
                new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
