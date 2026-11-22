using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;
using Moq;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// Cached copies of web videos: named after the video's page (signed media links change on
    /// every lookup), found again without a lookup, and recovered when the cleanup deletes them.
    /// </summary>
    public class VideoCacheTests : TestBase
    {
        private const string PageUrl = "https://rule34video.com/video/4187379/some-title/";
        private const string SignedUrl = "https://rule34video.com/get_file/1/abc/4187000/4187379/4187379_1080p.mp4/?rnd=1234";

        private readonly Mock<IVideoDownloadService> _downloads = new Mock<IVideoDownloadService>();
        private readonly List<string> _tempFiles = new List<string>();

        public VideoCacheTests()
        {
            ServiceContainer.Register(_downloads.Object);
        }

        public override void Dispose()
        {
            foreach (var file in _tempFiles)
            {
                try { File.Delete(file); } catch { }
            }
            base.Dispose();
        }

        [Fact]
        public void CacheKey_IsThePage_WhenTheVideoHasOne()
        {
            HypnoViewModel.CacheKeyFor(new VideoItem(SignedUrl) { OriginalPageUrl = PageUrl }, SignedUrl).Should().Be(PageUrl);
            HypnoViewModel.CacheKeyFor(new VideoItem(PageUrl), SignedUrl).Should().Be(PageUrl, "the item still points at its page");
            HypnoViewModel.CacheKeyFor(new VideoItem("https://example.com/clip.mp4"), "https://example.com/clip.mp4")
                .Should().Be("https://example.com/clip.mp4", "a direct link with no page is its own key");
        }

        [Fact]
        public void TwoLookupsOfTheSameVideo_ShareOneCachedCopy()
        {
            var service = new VideoDownloadService();
            var key = HypnoViewModel.CacheKeyFor(new VideoItem(SignedUrl) { OriginalPageUrl = PageUrl }, SignedUrl);
            var laterKey = HypnoViewModel.CacheKeyFor(new VideoItem(SignedUrl.Replace("rnd=1234", "rnd=9876")) { OriginalPageUrl = PageUrl }, SignedUrl);

            MockSettings.Setup(s => s.EnableLocalCaching).Returns(true);
            MockSettings.Setup(s => s.LocalCacheDirectory).Returns(Path.Combine(Path.GetTempPath(), "EdgeLoopTests", "VideoCache"));
            service.GetCachePath(key).Should().Be(service.GetCachePath(laterKey), "a new signature on the link is still the same video");
        }

        [Fact]
        public void DeletedCachedCopy_GoesBackToThePage()
        {
            var gone = Path.Combine(Path.GetTempPath(), $"edgeloop-gone-{Guid.NewGuid():N}.mp4");
            var item = new VideoItem(gone) { OriginalPageUrl = PageUrl };
            item.IsCached.Should().BeTrue();

            HypnoViewModel.RevertDeletedCacheFile(item).Should().BeTrue();
            item.FilePath.Should().Be(PageUrl);
            item.IsMissing.Should().BeFalse("it's a web video again, not a missing file");
        }

        [Fact]
        public void ExistingCachedCopy_AndLocalFiles_AreLeftAlone()
        {
            var present = TempVideo();
            var cached = new VideoItem(present) { OriginalPageUrl = PageUrl };
            HypnoViewModel.RevertDeletedCacheFile(cached).Should().BeFalse();
            cached.FilePath.Should().Be(present);

            var localOnly = new VideoItem(Path.Combine(Path.GetTempPath(), "not-there.mp4"));
            HypnoViewModel.RevertDeletedCacheFile(localOnly).Should().BeFalse("a local file has no page to go back to");

            var web = new VideoItem(SignedUrl) { OriginalPageUrl = PageUrl };
            HypnoViewModel.RevertDeletedCacheFile(web).Should().BeFalse();
        }

        [Theory]
        [InlineData(PageUrl)]
        [InlineData(SignedUrl)] // Pre-buffering already swapped in a signed link, which has since changed
        public async Task VideoWithACachedCopy_PlaysTheCopy_WithoutLookingThePageUp(string filePath)
        {
            var copy = TempVideo();
            _downloads.Setup(d => d.GetCachedFilePath(PageUrl)).Returns(copy);

            var item = new VideoItem(filePath) { OriginalPageUrl = PageUrl };
            var vm = new HypnoViewModel(MockExtractor.Object, _downloads.Object);
            vm.SetQueue(new[] { item });

            (await WaitUntil(() => vm.CurrentSource != null)).Should().BeTrue("the video reaches the player");
            item.FilePath.Should().Be(copy);
            MockExtractor.Verify(x => x.ExtractVideoUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()), Times.Never);

            vm.Dispose();
        }

        [Fact]
        public async Task PreBuffer_StoresTheDownloadUnderThePage_AndKeepsThePage()
        {
            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(PageUrl, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .ReturnsAsync(SignedUrl);
            string? usedKey = null;
            _downloads.Setup(d => d.DownloadVideoAsync(SignedUrl, It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<string, Dictionary<string, string>, CancellationToken, string>((_, _, _, key) => usedKey = key)
                .ReturnsAsync((string?)null);

            var first = new VideoItem("https://example.com/first.mp4");
            var next = new VideoItem(PageUrl); // Added without OriginalPageUrl
            var vm = new HypnoViewModel(MockExtractor.Object, _downloads.Object);
            vm.SetQueue(new[] { first, next });
            (await WaitUntil(() => vm.CurrentItem == first && vm.CurrentSource != null)).Should().BeTrue();

            vm.OnMediaOpened(this, new FlyleafLib.MediaPlayer.OpenCompletedArgs());
            (await WaitUntil(() => usedKey != null)).Should().BeTrue("the next video is downloaded");

            usedKey.Should().Be(PageUrl);
            next.FilePath.Should().Be(SignedUrl);
            next.OriginalPageUrl.Should().Be(PageUrl, "the page is kept, so the link can be looked up again when it expires");

            vm.Dispose();
        }

        [Theory]
        // PMVHaven's media is on a storage host of its own, so only the page says which site it is.
        // .invalid hosts never resolve, so nothing goes out on the network.
        [InlineData("https://pmvhaven-storage.invalid/videos/abc/1080p.m3u8", "https://pmvhaven.com/video/some-title_abc123", "0")]
        [InlineData("https://stream.invalid/master.m3u8", null, "1")]
        public async Task PersistentConnections_FollowTheVideosSite(string mediaUrl, string? pageUrl, string expected)
        {
            var item = new VideoItem(mediaUrl) { OriginalPageUrl = pageUrl };
            var vm = new HypnoViewModel(MockExtractor.Object, _downloads.Object);
            vm.SetQueue(new[] { item });

            (await WaitUntil(() => vm.CurrentSource != null)).Should().BeTrue("the video reaches the player");
            vm.Config.Demuxer.FormatOpt["http_persistent"].Should().Be(expected);

            vm.Dispose();
        }

        [Fact]
        public void OnlyPmvHaven_TurnsPersistentConnectionsOff()
        {
            SiteRules.Get(Site.PmvHaven).PersistentMediaConnections.Should().BeFalse();
            foreach (var site in new[] { Site.Unknown, Site.Iwara, Site.Hypnotube, Site.Rule34Video, Site.YouTube })
                SiteRules.Get(site).PersistentMediaConnections.Should().BeTrue($"{site} keeps the default");
        }

        private string TempVideo()
        {
            var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample_h264_aac.mp4");
            var copy = Path.Combine(Path.GetTempPath(), $"edgeloop-cache-{Guid.NewGuid():N}.mp4");
            File.Copy(source, copy);
            _tempFiles.Add(copy);
            return copy;
        }

        private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }
            return condition();
        }
    }
}
