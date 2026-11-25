using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using AwesomeAssertions;
using Moq;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// Extraction and import against copies of the sites' real pages (TestAssets/Sites, saved
    /// 2026-11-22 and trimmed). When a site changes its layout, save the new page and update these.
    /// </summary>
    public class SitePageTests : TestBase
    {
        private const string PmvHavenVideoUrl = "https://pmvhaven.com/video/1-9-9-1-part-1_6b0227bd2e9d39f509509c54";
        private const string PmvHavenStream = "https://pmvhavencloud.s3.eu-west-par.io.cloud.ovh.net/videos/1795302183673_1v62ck75ppw_1_9_9_1__PART1_.mp4/1080p.m3u8";
        private const string Rule34VideoVideoUrl = "https://rule34video.com/video/4740949/wild-obsession/";
        private const string Rule34VideoPlaylistUrl = "https://rule34video.com/playlists/191034/j-meister555-s-playlist/";
        private const string HypnotubeVideoUrl = "https://hypnotube.com/video/censored-training-for-betas-101-45454.html";
        private const string HypnotubeVipUrl = "https://hypnotube.com/video/cum-for-me-135977.html";
        private const string HypnotubeChannelUrl = "https://hypnotube.com/channels/37/audio/";

        // The URL cache is shared with the app (it lives in the app's data folder), so tests clean up after themselves
        private static readonly string[] CachedPageUrls =
        {
            PmvHavenVideoUrl, PmvHavenVideoUrl + "?from=home", "https://pmvhaven.com/video/previews-only_6b0000000000000000000001",
            "https://pmvhaven.com/video/relative-only_6b0000000000000000000002", Rule34VideoVideoUrl, HypnotubeVideoUrl, HypnotubeVipUrl,
        };

        private readonly Mock<IHtmlFetcher> _fetcher = new();
        private readonly List<string> _fetched = new();

        public SitePageTests()
        {
            foreach (var url in CachedPageUrls) PersistentUrlCache.Instance.Remove(url);

            _fetcher.Setup(f => f.ResolveRedirectUrlAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string url, string _, CancellationToken _) => url);
        }

        public override void Dispose()
        {
            foreach (var url in CachedPageUrls) PersistentUrlCache.Instance.Remove(url);
            base.Dispose();
        }

        private static string Page(string name) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Sites", name));

        /// <summary>Serves pages by URL; anything else gets null, like a failed fetch.</summary>
        private void Serve(Dictionary<string, string> pages)
        {
            _fetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string url, CancellationToken _) =>
                {
                    _fetched.Add(url);
                    return pages.TryGetValue(url, out var html) ? html : null;
                });
        }

        private void ServeForAnyUrl(string html)
        {
            _fetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string url, CancellationToken _) => { _fetched.Add(url); return html; });
        }

        private PlaylistImporter Importer() => new(MockExtractor.Object, _fetcher.Object);

        // --- PMVHaven ---------------------------------------------------------------------------

        [Fact]
        public void PmvHaven_FindsTheVideosOwnStream_HighestQuality()
        {
            VideoUrlExtractor.FindPmvHavenStream(Page("pmvhaven_video.html")).Should().Be(PmvHavenStream);
        }

        [Fact]
        public async Task PmvHaven_VideoPage_ResolvesToItsStream()
        {
            ServeForAnyUrl(Page("pmvhaven_video.html"));
            var extractor = new VideoUrlExtractor(_fetcher.Object);

            var url = await extractor.ExtractVideoUrlAsync(PmvHavenVideoUrl + "?from=home");

            url.Should().Be(PmvHavenStream);
        }

        [Fact]
        public async Task PmvHaven_PageWithOnlyPreviewClips_FindsNothing()
        {
            const string html = "<script>[\"https:\\u002F\\u002Fpmvhavencloud.s3.eu-west-par.io.cloud.ovh.net\\u002Fpreviews\\u002Fx_preview.mp4\"," +
                                "\"https:\\u002F\\u002Fpmvhavencloud.s3.eu-west-par.io.cloud.ovh.net\\u002Fy\\u002FvideoPreview\\u002Fcomus_y.mp4\"]</script>";
            ServeForAnyUrl(html);
            var extractor = new VideoUrlExtractor(_fetcher.Object);

            var url = await extractor.ExtractVideoUrlAsync("https://pmvhaven.com/video/previews-only_6b0000000000000000000001");

            url.Should().BeNull("a few-second preview of another video is worse than no video");
        }

        [Fact]
        public void PmvHaven_OlderUploads_PickTheHighestVariantToo()
        {
            const string html = "<script>[\"https:\\u002F\\u002Fpmvhavencloud.s3.eu-west-par.io.cloud.ovh.net\\u002Fabc\\u002Fabc_480p.m3u8\"," +
                                "\"https:\\u002F\\u002Fpmvhavencloud.s3.eu-west-par.io.cloud.ovh.net\\u002Fabc\\u002Fabc_720p.m3u8\"," +
                                "\"https:\\u002F\\u002Fpmvhavencloud.s3.eu-west-par.io.cloud.ovh.net\\u002Fabc\\u002Fmaster.m3u8\"]</script>";

            VideoUrlExtractor.FindPmvHavenStream(html)
                .Should().Be("https://pmvhavencloud.s3.eu-west-par.io.cloud.ovh.net/abc/abc_720p.m3u8");
        }

        [Fact]
        public async Task Hypnotube_PlaylistLinks_AreStoredWithoutThePlaylistParameter()
        {
            const string listUrl = "https://hypnotube.com/playlist/214116/goon/";
            Serve(new() { [listUrl] = "<a href=\"https://hypnotube.com/video/brave-new-world-bnwo-pmv-47826.html?p=214116\" title=\"Brave New World - BNWO PMV\">.</a>" });

            var items = await Importer().ImportPlaylistAsync(listUrl);

            items.Should().ContainSingle().Which.FilePath.Should().Be("https://hypnotube.com/video/brave-new-world-bnwo-pmv-47826.html");
            items[0].Title.Should().Be("Brave New World - BNWO PMV");
        }

        [Fact]
        public void PmvHaven_RelativeStreamLinks_UseTheCdnThePageNames()
        {
            const string html = "<script>[\"https:\\u002F\\u002Fpmvhavencloud.s3.eu-west-par.io.cloud.ovh.net\\u002Fthumbnails\\u002Fa\\u002Fthumb_lg.webp\"," +
                                "\"videos\\u002Fa.mp4\\u002Fmaster.m3u8\"]</script>";

            VideoUrlExtractor.FindPmvHavenStream(html)
                .Should().Be("https://pmvhavencloud.s3.eu-west-par.io.cloud.ovh.net/videos/a.mp4/master.m3u8");
        }

        [Fact]
        public async Task PmvHaven_Playlist_ImportsEveryVideoWithItsTitle_WithoutFetchingThem()
        {
            Serve(new() { ["https://pmvhaven.com/playlists/6b029e4dc41c701c14af591f"] = Page("pmvhaven_playlist.html") });

            var items = await Importer().ImportPlaylistAsync("https://pmvhaven.com/playlists/6b029e4dc41c701c14af591f");

            items.Select(i => i.FilePath).Should().Equal(
                "https://pmvhaven.com/videos/68fdaa1db99aaf24a4c0ca45",
                "https://pmvhaven.com/videos/6abdf390c67c74171ecbe223");
            items.Select(i => i.Title).Should().Equal("HMV Uchi no Otouto Maji de Dekain Dakedo Mi ni Konai", "Pmv random hentai");
            MockExtractor.Verify(e => e.ExtractVideoMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("https:\\u002F\\u002Fhost\\u002Fa.mp4", "https://host/a.mp4")]
        [InlineData("https:\\/\\/host\\/a.mp4", "https://host/a.mp4")]
        [InlineData("plain text", "plain text")]
        public void DecodeScriptEscapes_RestoresEscapedCharacters(string text, string expected)
        {
            VideoUrlExtractor.DecodeScriptEscapes(text).Should().Be(expected);
        }

        // --- Importing never downloads ----------------------------------------------------------

        [Fact]
        public async Task Import_WithoutListTitles_LooksUpTitles_ButKeepsThePageUrl()
        {
            // A page whose links have no titles; the lookup also returns a signed media link
            const string listUrl = "https://pmvhaven.com/playlists/no-titles";
            Serve(new() { [listUrl] = "<a href=\"https://pmvhaven.com/video/a-video_6b0000000000000000000003\">.</a>" });
            MockExtractor.Setup(e => e.ExtractVideoMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoMetadata("https://cdn.example.com/a.mp4?expires=1", "A Video", null));

            var items = await Importer().ImportPlaylistAsync(listUrl);

            items.Should().ContainSingle();
            items[0].Title.Should().Be("A Video");
            items[0].FilePath.Should().Be("https://pmvhaven.com/video/a-video_6b0000000000000000000003",
                "the page is resolved when it plays, so an expiring link is never stored");
            items[0].OriginalPageUrl.Should().Be(items[0].FilePath);
        }

        [Theory]
        [InlineData("www.youtube.com", true)]
        [InlineData("vimeo.com", true)]
        [InlineData("www.iwara.tv", false)]
        [InlineData("hypnotube.com", false)]
        [InlineData("rule34video.com", false)]
        public void DownloadFirst_OnlyForSitesWithSeparateVideoAndAudio(string host, bool expected)
        {
            YtDlpService.IsDownloadRequiredSite(host).Should().Be(expected);
        }

        // --- Rule34Video ------------------------------------------------------------------------

        [Fact]
        public async Task Rule34Video_VideoPage_PicksThe1080pFile_WithTheAppsOwnScraper()
        {
            ServeForAnyUrl(Page("rule34video_video.html"));
            var extractor = new VideoUrlExtractor(_fetcher.Object);

            var url = await extractor.ExtractVideoUrlAsync(Rule34VideoVideoUrl);

            url.Should().Contain("/get_file/").And.Contain("4740949_1080p.mp4");
        }

        [Fact]
        public async Task Rule34Video_ResolvedLinks_AreCachedForTwentyMinutesAtMost()
        {
            ServeForAnyUrl(Page("rule34video_video.html"));
            await new VideoUrlExtractor(_fetcher.Object).ExtractVideoUrlAsync(Rule34VideoVideoUrl);

            PersistentUrlCache.Instance.GetExpiry(Rule34VideoVideoUrl)
                .Should().NotBeNull().And.BeOnOrBefore(DateTime.UtcNow.AddMinutes(20).AddSeconds(5));
        }

        [Fact]
        public async Task Rule34Video_Playlist_FollowsEveryPage_WithTitles()
        {
            Serve(new()
            {
                [Rule34VideoPlaylistUrl] = Page("rule34video_playlist_p1.html"),
                [Rule34VideoPlaylistUrl + "?from=2"] = Page("rule34video_playlist_p2.html"),
                // Past the end the site repeats the last page; nothing new means stop
                [Rule34VideoPlaylistUrl + "?from=3"] = Page("rule34video_playlist_p2.html"),
            });

            var items = await Importer().ImportPlaylistAsync(Rule34VideoPlaylistUrl);

            items.Should().HaveCount(120);
            items[0].Title.Should().Be("The Hitchhiker『Dieselbrain』");
            items[0].FilePath.Should().Be("https://rule34video.com/video/4602079/the-hitchhiker-dieselbrain/");
            items.Select(i => i.FilePath).Should().OnlyHaveUniqueItems();
            _fetched.Should().Equal(Rule34VideoPlaylistUrl, Rule34VideoPlaylistUrl + "?from=2", Rule34VideoPlaylistUrl + "?from=3");
            MockExtractor.Verify(e => e.ExtractVideoMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public void Rule34Video_NextPage_ComesFromThePagersNextLink()
        {
            PlaylistImporter.NextRule34VideoPage(Page("rule34video_playlist_p1.html"), Rule34VideoPlaylistUrl)
                .Should().Be(Rule34VideoPlaylistUrl + "?from=2");
            PlaylistImporter.NextRule34VideoPage(Page("rule34video_playlist_p2.html"), Rule34VideoPlaylistUrl)
                .Should().Be(Rule34VideoPlaylistUrl + "?from=3");
            PlaylistImporter.NextRule34VideoPage("<div class=\"item pager prev\"><a data-parameters=\"from:1\"></a></div>", Rule34VideoPlaylistUrl)
                .Should().BeNull("the last page has no next link");
        }

        [Fact]
        public void Rule34Video_NextPage_MemberUploadsUseTheirOwnOffset()
        {
            const string html = "<div class=\"item pager next\"><a data-parameters=\"sort_by:post_date;from_videos:2\"></a></div>";

            PlaylistImporter.NextRule34VideoPage(html, "https://rule34video.com/members/123/videos/")
                .Should().Be("https://rule34video.com/members/123/videos/?from_videos=2");
        }

        // --- Hypnotube --------------------------------------------------------------------------

        [Theory]
        [InlineData("hypnotube_video.html", SiteRestriction.None)]
        [InlineData("hypnotube_vip.html", SiteRestriction.MembersOnly)]
        [InlineData("hypnotube_location_blocked.html", SiteRestriction.LocationBlocked)]
        public void Hypnotube_RecognisesItsRestrictionPages(string page, SiteRestriction expected)
        {
            SiteRestrictions.Detect(Site.Hypnotube, Page(page)).Should().Be(expected);
        }

        [Fact]
        public async Task Hypnotube_PublicVideo_ResolvesToItsFile()
        {
            ServeForAnyUrl(Page("hypnotube_video.html"));

            var url = await new VideoUrlExtractor(_fetcher.Object).ExtractVideoUrlAsync(HypnotubeVideoUrl);

            url.Should().StartWith("https://media.hypnotube.com/videos/").And.Contain("648aaf1e8092c.mp4");
            SiteRestrictions.For(HypnotubeVideoUrl).Should().Be(SiteRestriction.None);
        }

        [Fact]
        public async Task Hypnotube_SignedIn_PicksThe720pFile_NotWhatItsTokenSpellsOut()
        {
            // Signed in, Hypnotube offers 720p next to 480p. Modelled on the public page's markup (a real
            // signed-in page isn't saved here): the 480p file's random token contains "HD" and "4k", which
            // the old quality guess read as 2160p because it looked at the whole link before the label.
            const string html = "<video id=\"plyr_player\" controls>" +
                "<source src=\"https://media.hypnotube.com/videos/6/4/8/a/a/648aaf1e8092c_m.mp4?token=qHDx4kZ&expires=1795349461\" type='video/mp4' sizes='480' />" +
                "<source src=\"https://media.hypnotube.com/videos/6/4/8/a/a/648aaf1e8092c.mp4?token=aSDb2&expires=1795349461\" type='video/mp4' sizes='720' />" +
                "</video>";
            ServeForAnyUrl(html);

            var url = await new VideoUrlExtractor(_fetcher.Object).ExtractVideoUrlAsync(HypnotubeVideoUrl);

            url.Should().Contain("648aaf1e8092c.mp4?token=aSDb2", "that's the 720p file");
        }

        [Theory]
        [InlineData("https://cdn.example.com/v/clip_720p.mp4?token=x4kHD", 720)]
        [InlineData("https://media.hypnotube.com/videos/6/4/8/648aaf1e8092c.mp4?token=x4kHDsd", 0)]
        [InlineData("https://cdn.example.com/v/clip.mp4", 0)]
        public void QualityFromALink_ReadsThePathOnly(string url, int expected)
        {
            QualitySelector.DetectQualityFromUrl(url).Should().Be(expected);
        }

        [Fact]
        public async Task Hypnotube_VipVideo_FailsWithTheReason()
        {
            ServeForAnyUrl(Page("hypnotube_vip.html"));

            var url = await new VideoUrlExtractor(_fetcher.Object).ExtractVideoUrlAsync(HypnotubeVipUrl);

            url.Should().BeNull();
            SiteRestrictions.Describe(HypnotubeVipUrl).Should().Be("It's for Hypnotube VIP members only.");
        }

        [Fact]
        public async Task Hypnotube_LocationBlockedChannel_ImportsNothing_AndSaysWhy()
        {
            Serve(new() { [HypnotubeChannelUrl] = Page("hypnotube_location_blocked.html") });

            var items = await Importer().ImportPlaylistAsync(HypnotubeChannelUrl);

            items.Should().BeEmpty();
            SiteRestrictions.Describe(HypnotubeChannelUrl).Should().StartWith("Hypnotube blocks this page in the country your connection comes from");
        }

        [Fact]
        public void PlaybackFailure_ShowsTheSitesReason()
        {
            PlaybackFailureText.Reason("Failed to resolve video URL: It's for Hypnotube VIP members only.")
                .Should().Be("It's for Hypnotube VIP members only.");
        }

        // --- yt-dlp -----------------------------------------------------------------------------

        [Fact]
        public void YtDlp_BrowserCookies_AreSetSoALockedDatabaseRetryCanDropThem()
        {
            using var ytDlp = new YtDlpService(Path.Combine(Path.GetTempPath(), "no-such-yt-dlp.exe"));

            var options = ytDlp.BuildOptionsForUrl("https://www.youtube.com/watch?v=abc", out _);

            options.CookiesFromBrowser.Should().Be("Firefox");
        }

        [Fact]
        public void YtDlp_RecognisesALockedCookieDatabase()
        {
            YtDlpService.IsCookieLockError("WARNING: x\nERROR: Could not copy Chrome cookie database. See https://github.com/yt-dlp/yt-dlp/issues/7271 for more info")
                .Should().BeTrue();
        }
    }
}
