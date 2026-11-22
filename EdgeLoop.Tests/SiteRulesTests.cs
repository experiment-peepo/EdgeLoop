using EdgeLoop.Classes;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    public class SiteRulesTests : TestBase
    {
        [Theory]
        [InlineData("https://www.iwara.tv/video/abc123/some-slug", Site.Iwara)]
        [InlineData("https://files.iwara.tv/file/x.mp4", Site.Iwara)]
        [InlineData("www.iwara.tv", Site.Iwara)]
        [InlineData("https://hypnotube.com/video/my-video-123.html", Site.Hypnotube)]
        [InlineData("https://rule34video.com/video/1/slug/", Site.Rule34Video)]
        [InlineData("https://pmvhaven.com/video/x", Site.PmvHaven)]
        [InlineData("https://m.youtube.com/watch?v=abc", Site.YouTube)]
        [InlineData("https://youtu.be/abc", Site.YouTube)]
        public void Identify_RecognisesSiteHosts(string url, Site expected)
        {
            SiteRules.Identify(url).Should().Be(expected);
        }

        [Theory]
        [InlineData("https://cdn.example.com/video.mp4?ref=iwara.tv")] // site name only in the query
        [InlineData("https://example.com/hypnotube.com/video")]        // site name only in the path
        [InlineData("https://notiwara.tv/")]                           // look-alike domain
        [InlineData(@"C:\Videos\iwara.tv.mp4")]                        // local file
        [InlineData("")]
        [InlineData(null)]
        public void Identify_IgnoresSiteNamesOutsideTheHost(string? url)
        {
            SiteRules.Identify(url).Should().Be(Site.Unknown);
        }

        [Theory]
        [InlineData("x.com", "x.com", true)]
        [InlineData("api.x.com", "x.com", true)]
        [InlineData("www.netflix.com", "x.com", false)]
        [InlineData("x.com.evil.net", "x.com", false)]
        public void HostMatches_OnlyMatchesDomainAndSubdomains(string host, string domain, bool expected)
        {
            SiteRules.HostMatches(host, domain).Should().Be(expected);
        }

        [Theory]
        [InlineData("https://hypnotube.com/video/x-1.html?playlist=5", "https://hypnotube.com/video/x-1.html")]
        [InlineData("https://www.iwara.tv/video/abc/slug?sort=date", "https://www.iwara.tv/video/abc/slug")]
        [InlineData("https://pmvhaven.com/video/a_6b02?from=home", "https://pmvhaven.com/video/a_6b02")]
        [InlineData("https://media.hypnotube.com/videos/a.mp4?token=t&expires=1", "https://media.hypnotube.com/videos/a.mp4?token=t&expires=1")] // a media file's signature stays
        [InlineData("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc")]
        public void NormalizePageUrl_StripsQueryOnlyWhereNeeded(string url, string expected)
        {
            SiteRules.NormalizePageUrl(url).Should().Be(expected);
        }

        [Fact]
        public void BuildMediaHeaders_Iwara_SendsRefererAndOrigin()
        {
            var profile = SiteRules.Get(Site.Iwara);

            SiteRules.BuildMediaHeaders(profile, profile.MediaReferer, includeCookies: true)
                .Should().Be("Referer: https://iwara.tv/\r\nOrigin: https://iwara.tv\r\n");
        }

        [Fact]
        public void BuildMediaHeaders_Hypnotube_KeepsCookieWithReferer()
        {
            MockSettings.Setup(s => s.HypnotubeCookiesEncrypted).Returns("session=abc");
            var profile = SiteRules.Get(Site.Hypnotube);

            var headers = SiteRules.BuildMediaHeaders(profile, "https://hypnotube.com/video/x.html", includeCookies: true);

            headers.Should().Be("Referer: https://hypnotube.com/video/x.html\r\nCookie: session=abc\r\n");
        }

        [Fact]
        public void BuildMediaHeaders_NeverSendsCookiesToThirdPartyHosts()
        {
            MockSettings.Setup(s => s.HypnotubeCookiesEncrypted).Returns("session=abc");
            var profile = SiteRules.Get(Site.Hypnotube);

            var headers = SiteRules.BuildMediaHeaders(profile, "https://hypnotube.com/video/x.html", includeCookies: false);

            headers.Should().NotContain("Cookie");
        }

        [Fact]
        public void BuildMediaHeaders_NothingToSend_ReturnsNull()
        {
            SiteRules.BuildMediaHeaders(SiteRules.Get(Site.Unknown), null, includeCookies: true).Should().BeNull();
        }

        [Fact]
        public void GetCookies_TrimsAndTreatsBlankAsNone()
        {
            MockSettings.Setup(s => s.HypnotubeCookiesEncrypted).Returns("  token=1  ");
            MockSettings.Setup(s => s.Rule34VideoCookiesEncrypted).Returns("   ");
            MockSettings.Setup(s => s.IwaraCookiesEncrypted).Returns("sid=1");

            SiteRules.Get(Site.Hypnotube).GetCookies().Should().Be("token=1");
            SiteRules.Get(Site.Rule34Video).GetCookies().Should().BeNull();
            SiteRules.Get(Site.PmvHaven).GetCookies().Should().BeNull();
            SiteRules.Get(Site.Iwara).GetCookies().Should().BeNull("Iwara signs in with an account, not cookies");
        }

        [Theory]
        [InlineData("www.youtube.com", true)]
        [InlineData("www.iwara.tv", false)] // streams a single MP4 file
        [InlineData("x.com", true)]
        [InlineData("www.netflix.com", false)] // used to match because it ends in "x.com"
        [InlineData("pmvhaven.com", false)]
        public void IsDownloadRequiredSite_UsesHostMatching(string host, bool expected)
        {
            YtDlpService.IsDownloadRequiredSite(host).Should().Be(expected);
        }
    }
}
