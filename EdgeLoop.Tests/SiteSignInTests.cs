using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;
using Moq;

namespace EdgeLoop.Tests
{
    /// <summary>Signing in to Hypnotube and Rule34Video with the account in Settings, against fake sites.</summary>
    public class SiteSignInTests : TestBase
    {
        private DateTime _now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        private readonly List<(string Line, string Cookies, string Body, string RequestedWith)> _requests = new();
        private Func<HttpRequestMessage, string, CookieContainer, HttpResponseMessage> _site = (_, _, _) => Html("");

        public SiteSignInTests()
        {
            MockSettings.SetupProperty(s => s.HypnotubeSessionEncrypted);
            MockSettings.SetupProperty(s => s.Rule34VideoSessionEncrypted);
        }

        private SiteSignIn SignIn() => new(jar => new FakeSite(jar, this), () => MockSettings.Object, () => _now);

        private void Account(Site site, string username, string password)
        {
            if (site == Site.Hypnotube)
            {
                MockSettings.Setup(s => s.HypnotubeUsername).Returns(username);
                MockSettings.Setup(s => s.HypnotubePasswordEncrypted).Returns(password); // plain text reads back as-is
            }
            else
            {
                MockSettings.Setup(s => s.Rule34VideoUsername).Returns(username);
                MockSettings.Setup(s => s.Rule34VideoPasswordEncrypted).Returns(password);
            }
        }

        private static string Page(string name) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Sites", name));

        private static HttpResponseMessage Html(string html) =>
            new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static void SetCookie(CookieContainer jar, string site, string name, string value) =>
            jar.Add(new Cookie(name, value, "/", new Uri(site).Host));

        // --- Hypnotube -------------------------------------------------------------------------

        /// <summary>A Hypnotube that accepts one password, as its real form does: a session cookie from the page, then the post.</summary>
        private void FakeHypnotube(string password, string rejectPage = "<form id=\"login-form\"></form>")
        {
            _site = (request, body, jar) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/login")
                {
                    SetCookie(jar, "https://hypnotube.com/", "PHPSESSID", "visitor");
                    return Html("<form id=\"login-form\" method=\"post\" action=\"\"></form>");
                }
                if (request.Method == HttpMethod.Post && path == "/login")
                {
                    if (body != null && body.Contains($"ahd_password={password}"))
                    {
                        SetCookie(jar, "https://hypnotube.com/", "PHPSESSID", "member");
                        return Html("<a href=\"https://hypnotube.com/logout\">Logout</a>");
                    }
                    return Html(rejectPage);
                }
                if (request.Method == HttpMethod.Get && path == "/")
                {
                    var cookies = jar.GetCookieHeader(request.RequestUri);
                    return Html(cookies.Contains("PHPSESSID=member") ? "<a href=\"/logout\">Logout</a>" : "<a href=\"/login\">Login</a>");
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };
        }

        [Fact]
        public async Task Hypnotube_PostsItsForm_WithTheSessionFromTheLoginPage()
        {
            FakeHypnotube("secret");

            var result = await SignIn().SignInAsync(Site.Hypnotube, " me ", "secret");

            result.Outcome.Should().Be(SignInOutcome.SignedIn);
            result.Cookies.Should().Be("PHPSESSID=member");
            var post = _requests.Single(r => r.Line == "POST /login");
            post.Body.Should().Be("ahd_username=me&ahd_password=secret&Submit=");
            post.Cookies.Should().Contain("PHPSESSID=visitor", "the form is posted in the session the login page started");
        }

        [Fact]
        public async Task Hypnotube_WrongPassword_IsRejected()
        {
            FakeHypnotube("secret");

            var result = await SignIn().SignInAsync(Site.Hypnotube, "me", "nope");

            result.Outcome.Should().Be(SignInOutcome.Rejected);
            result.Message.Should().Contain("username, not the email");
        }

        [Fact]
        public async Task Hypnotube_Captcha_SaysSo()
        {
            FakeHypnotube("secret", rejectPage: "<img src=\"/captcha.php\"><input name=\"captchaaa\">");

            var result = await SignIn().SignInAsync(Site.Hypnotube, "me", "nope");

            result.Outcome.Should().Be(SignInOutcome.CaptchaRequired);
            result.Message.Should().Contain("Paste your cookies");
        }

        // --- Rule34Video ---------------------------------------------------------------------

        private const string Rule34VideoToken = "85d6b8b969c818b69853714dab4fb065ee75a843df5a179a50fa2968f4cae42b";

        /// <summary>A Rule34Video that answers as KVS does: the real login form, a JSON reply, kt_member once signed in.</summary>
        private void FakeRule34Video(string password, string failureJson = "{\"status\":\"failure\",\"errors\":[{\"field\":\"pass\",\"code\":\"invalid\",\"message\":\"Invalid username or password\"}]}")
        {
            _site = (request, body, jar) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/login/")
                {
                    SetCookie(jar, "https://rule34video.com/", "PHPSESSID", "visitor");
                    return Html(Page("rule34video_login.html"));
                }
                if (request.Method == HttpMethod.Post && path == "/login/")
                {
                    if (body != null && body.Contains($"pass={password}") && body.Contains($"remember_me_csrf_token={Rule34VideoToken}"))
                    {
                        SetCookie(jar, "https://rule34video.com/", "kt_member", "abc123");
                        return Json("{\"status\":\"success\",\"redirect\":\"/\"}");
                    }
                    return Json(failureJson);
                }
                if (request.Method == HttpMethod.Get && path == "/" && request.RequestUri.Query.Contains("action=js_user_status"))
                {
                    return Json(jar.GetCookieHeader(request.RequestUri).Contains("kt_member=abc123") ? "{\"id\":4242}" : "{\"id\":0}");
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };
        }

        [Fact]
        public async Task Rule34Video_PostsTheFormsHiddenFields_AndKeepsKtMember()
        {
            FakeRule34Video("secret");

            var result = await SignIn().SignInAsync(Site.Rule34Video, "me@example.com", "secret");

            result.Outcome.Should().Be(SignInOutcome.SignedIn);
            result.Cookies.Should().Contain("kt_member=abc123").And.Contain("PHPSESSID=visitor");
            var post = _requests.Single(r => r.Line == "POST /login/");
            post.Body.Should().Contain($"remember_me_csrf_token={Rule34VideoToken}")
                .And.Contain("action=login").And.Contain("username=me%40example.com").And.Contain("pass=secret")
                .And.Contain("remember_me=1").And.Contain("mode=async").And.Contain("format=json");
            post.RequestedWith.Should().Be("XMLHttpRequest");
        }

        [Fact]
        public async Task Rule34Video_WrongPassword_PassesOnTheSitesReason()
        {
            FakeRule34Video("secret");

            var result = await SignIn().SignInAsync(Site.Rule34Video, "me", "nope");

            result.Outcome.Should().Be(SignInOutcome.Rejected);
            result.Message.Should().Be("Rule34Video didn't accept that: Invalid username or password");
        }

        [Fact]
        public async Task Rule34Video_CaptchaField_SaysSo()
        {
            FakeRule34Video("secret", failureJson: "{\"status\":\"failure\",\"errors\":[{\"field\":\"code\",\"code\":\"required\",\"message\":\"Please confirm you are human\"}]}");

            (await SignIn().SignInAsync(Site.Rule34Video, "me", "nope")).Outcome.Should().Be(SignInOutcome.CaptchaRequired);
        }

        [Fact]
        public void Rule34Video_LoginForm_HiddenFieldsAreRead()
        {
            var fields = SiteSignIn.LoginFormFields(Page("rule34video_login.html"));

            fields.Should().Contain(new Dictionary<string, string>
            {
                ["remember_me_csrf_token"] = Rule34VideoToken,
                ["action"] = "login",
                ["email_link"] = "https://rule34video.com/email/",
                ["redirect_to"] = "/",
            });
            fields.Should().NotContainKey("username", "only hidden fields come from the page");
        }

        [Theory]
        [InlineData("{\"id\":0}", false)]
        [InlineData("{\"id\":4242}", true)]
        [InlineData("{\"id\":\"17\"}", true)]
        [InlineData("<html>DDoS-Guard</html>", false)]
        public void Rule34Video_UserStatus_MemberWhenTheIdIsAboveZero(string json, bool member)
        {
            SiteSignIn.IsMember(json).Should().Be(member);
        }

        // --- Keeping the session ----------------------------------------------------------------

        [Fact]
        public async Task NoAccount_NoRequests()
        {
            await SignIn().EnsureSignedInAsync(Site.Hypnotube);

            _requests.Should().BeEmpty();
        }

        [Fact]
        public async Task ExpiredSession_SignsInAgain_AndSavesTheNewSession()
        {
            Account(Site.Hypnotube, "me", "secret");
            MockSettings.Object.HypnotubeSession = "PHPSESSID=expired";
            FakeHypnotube("secret");

            await SignIn().EnsureSignedInAsync(Site.Hypnotube);

            MockSettings.Object.HypnotubeSession.Should().Be("PHPSESSID=member");
            _requests.Select(r => r.Line).Should().Equal("GET /", "GET /login", "POST /login");
        }

        [Fact]
        public async Task WorkingSession_IsOnlyCheckedEvery30Minutes()
        {
            Account(Site.Rule34Video, "me", "secret");
            MockSettings.Object.Rule34VideoSession = "kt_member=abc123";
            FakeRule34Video("secret");
            var signIn = SignIn();

            await signIn.EnsureSignedInAsync(Site.Rule34Video);
            _now += TimeSpan.FromMinutes(29);
            await signIn.EnsureSignedInAsync(Site.Rule34Video);
            _now += TimeSpan.FromMinutes(2);
            await signIn.EnsureSignedInAsync(Site.Rule34Video);

            _requests.Select(r => r.Line).Should().Equal(
                "GET /?mode=async&format=json&action=js_user_status",
                "GET /?mode=async&format=json&action=js_user_status");
        }

        [Fact]
        public async Task FailedSignIn_DropsTheDeadSession_AndWaitsBeforeTryingAgain()
        {
            Account(Site.Hypnotube, "me", "wrong");
            MockSettings.Object.HypnotubeSession = "PHPSESSID=expired";
            FakeHypnotube("secret");
            var signIn = SignIn();

            await signIn.EnsureSignedInAsync(Site.Hypnotube);
            MockSettings.Object.HypnotubeSession.Should().BeNull("a dead session would hide pasted cookies");
            signIn.LastError(Site.Hypnotube).Should().Contain("didn't accept");

            _now += TimeSpan.FromMinutes(5);
            await signIn.EnsureSignedInAsync(Site.Hypnotube);
            _now += TimeSpan.FromMinutes(6);
            await signIn.EnsureSignedInAsync(Site.Hypnotube);

            _requests.Count(r => r.Line == "POST /login").Should().Be(2, "one attempt, then none for 10 minutes, then another");
        }

        [Fact]
        public async Task ASignedOutPage_MakesTheNextFetchCheckAgain()
        {
            Account(Site.Hypnotube, "me", "secret");
            FakeHypnotube("secret");
            var signIn = SignIn();
            await signIn.EnsureSignedInAsync(Site.Hypnotube);
            var before = _requests.Count;

            signIn.NoteSignedOutPage(Site.Hypnotube);
            await signIn.EnsureSignedInAsync(Site.Hypnotube);

            _requests.Count.Should().BeGreaterThan(before);
        }

        [Fact]
        public void TheSignedInSession_IsUsedBeforePastedCookies()
        {
            MockSettings.Setup(s => s.HypnotubeCookiesEncrypted).Returns("PHPSESSID=pasted");

            SiteRules.Get(Site.Hypnotube).GetCookies().Should().Be("PHPSESSID=pasted");
            MockSettings.Object.HypnotubeSession = "PHPSESSID=member";
            SiteRules.Get(Site.Hypnotube).GetCookies().Should().Be("PHPSESSID=member");
        }

        // --- Settings ------------------------------------------------------------------------------

        [Fact]
        public async Task Settings_CheckSignIn_ShowsWhatTheSiteSaid()
        {
            FakeHypnotube("secret");
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>(), siteSignIn: SignIn());

            settings.HypnotubeAccount.Username = "me";
            settings.HypnotubeAccount.Password = "nope";
            await settings.HypnotubeAccount.CheckAsync();
            settings.HypnotubeAccount.Succeeded.Should().BeFalse();

            settings.HypnotubeAccount.Password = "secret";
            settings.HypnotubeAccount.Status.Should().BeNull("an edit clears the old result");
            await settings.HypnotubeAccount.CheckAsync();
            settings.HypnotubeAccount.Succeeded.Should().BeTrue();
            settings.HypnotubeAccount.Status.Should().Be("Hypnotube accepted the username and password.");
        }

        [Fact]
        public void Settings_ChangingTheAccount_StartsAFreshSession()
        {
            MockSettings.SetupProperty(s => s.Rule34VideoUsername, "old@example.com");
            MockSettings.SetupProperty(s => s.Rule34VideoPasswordEncrypted, "secret");
            MockSettings.Object.Rule34VideoSession = "kt_member=old";
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>(), siteSignIn: SignIn());

            settings.Rule34VideoAccount.Username = "new@example.com";
            settings.HasChanges.Should().BeTrue();
            settings.Save();

            MockSettings.Object.Rule34VideoUsername.Should().Be("new@example.com");
            MockSettings.Object.Rule34VideoSession.Should().BeNull("it belonged to the old account");
        }

        [Fact]
        public async Task Settings_CookieBackup_OpensWhenTheLastSignInFailed()
        {
            Account(Site.Hypnotube, "me", "wrong");
            FakeHypnotube("secret");
            var signIn = SignIn();
            await signIn.EnsureSignedInAsync(Site.Hypnotube);

            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>(), siteSignIn: signIn);

            settings.ShowCookieFallback.Should().BeTrue("pasted cookies are the way round a failing sign-in");
            settings.HypnotubeAccount.Status.Should().StartWith("Last sign-in failed:");
        }

        [Fact]
        public void Settings_SavingWithoutChangingTheAccount_KeepsTheSession()
        {
            MockSettings.SetupProperty(s => s.Rule34VideoUsername, "me@example.com");
            MockSettings.SetupProperty(s => s.Rule34VideoPasswordEncrypted, "secret");
            MockSettings.Object.Rule34VideoSession = "kt_member=abc";
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>(), siteSignIn: SignIn());

            settings.DefaultVolume = 0.3;
            settings.Save();

            MockSettings.Object.Rule34VideoSession.Should().Be("kt_member=abc");
        }

        /// <summary>A site behind a real cookie jar, as HttpClientHandler would give: it sends the jar's cookies and the fake adds Set-Cookies to it.</summary>
        private sealed class FakeSite : HttpMessageHandler
        {
            private readonly CookieContainer _jar;
            private readonly SiteSignInTests _test;

            public FakeSite(CookieContainer jar, SiteSignInTests test)
            {
                _jar = jar;
                _test = test;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var requestedWith = request.Headers.TryGetValues("X-Requested-With", out var values) ? values.Single() : null;
                _test._requests.Add(($"{request.Method} {request.RequestUri!.PathAndQuery}", _jar.GetCookieHeader(request.RequestUri), body, requestedWith));
                return _test._site(request, body, _jar);
            }
        }
    }
}
