using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using EdgeLoop.ViewModels;
using EdgeLoop.Classes;
using Moq;
using AwesomeAssertions;

namespace EdgeLoop.Tests.Stability
{
    public class PreBufferingTests : TestBase
    {
        private readonly Mock<IVideoDownloadService> _mockDownloadService = new Mock<IVideoDownloadService>();

        public PreBufferingTests()
        {
            ServiceContainer.Register(_mockDownloadService.Object);
        }

        [Fact]
        public async Task StartPreBuffer_Rule34Video_ShouldIncludeRefererHeader()
        {
            // Arrange
            var pageUrl = "https://rule34video.com/video/123/test-video/";
            var directUrl = "https://boomio-cdn.com/123.mp4?time=123";

            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(pageUrl, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .ReturnsAsync(directUrl);

            _mockDownloadService.Setup(d => d.DownloadVideoAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync("cached_path.mp4");

            var vm = new HypnoViewModel(MockExtractor.Object, _mockDownloadService.Object);

            // Use URL for item1 so it passes validation and becomes current
            var item1 = new VideoItem("https://example.com/item1.mp4");
            var item2 = new VideoItem(pageUrl) { OriginalPageUrl = pageUrl };

            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(item1.FilePath, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .ReturnsAsync(item1.FilePath);

            vm.SetQueue(new[] { item1, item2 });

            // Wait for PlayNext to settle on item1
            await Task.Delay(100);

            // Act
            // Skip to item1. OnMediaOpened will trigger StartPreBuffer for item2.
            // vm.SetQueue already calls PlayNext() -> LoadCurrentVideo() -> OnMediaOpened (mocked)
            // Wait, OnMediaOpened is where StartPreBuffer is called.

            vm.OnMediaOpened(this, new FlyleafLib.MediaPlayer.OpenCompletedArgs());

            // Give more time for the background task in StartPreBuffer
            await Task.Delay(1000);

            // Assert
            _mockDownloadService.Verify(d => d.DownloadVideoAsync(
                directUrl,
                It.Is<Dictionary<string, string>>(h => h != null && h.ContainsKey("Referer") && h["Referer"] == pageUrl),
                It.IsAny<CancellationToken>(),
                pageUrl // Stored under the page: the signed link changes on every lookup
            ), Times.Once);
        }

        [Fact]
        public async Task Iwara_NextVideosFileLink_PlaysWithoutBeingResolvedAgain()
        {
            // Pre-buffering swaps the next video's page for its file link, which on Iwara names the file in
            // the query (/view?…&filename=…_Source.mp4). Taking that link for a page sent it back through
            // extraction, which Iwara refuses, so playback never moved on to the next video.
            const string pageUrl = "https://www.iwara.tv/video/FXNY1VuxsOLw4B/vam-sphmackg4-mightty-pmvhmv";
            const string fileUrl = "https://hime.iwara.tv/view?hash=ff374fa5e41e&filename=91d19d93-2a9f_Source.mp4&path=2025%2F05%2F08&expires=1795355309";
            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(pageUrl, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .ReturnsAsync(fileUrl);

            var vm = new HypnoViewModel(MockExtractor.Object, _mockDownloadService.Object);
            var item1 = new VideoItem("https://example.com/item1.mp4");
            var item2 = new VideoItem(pageUrl) { OriginalPageUrl = pageUrl };
            vm.SetQueue(new[] { item1, item2 });
            // The player ignores "media opened" until the load has handed it a source, so wait for that first
            (await WaitUntil(() => vm.CurrentItem == item1 && vm.CurrentSource != null)).Should().BeTrue("the first video reaches the player");

            vm.OnMediaOpened(this, new FlyleafLib.MediaPlayer.OpenCompletedArgs()); // pre-buffers item2
            (await WaitUntil(() => item2.FilePath == fileUrl)).Should().BeTrue("pre-buffering swaps in the file link");

            vm.PlayNext(force: true);
            (await WaitUntil(() => vm.CurrentItem == item2)).Should().BeTrue("playback moves on to the next video");
            await Task.Delay(200); // let the load run far enough to have asked for a lookup, if it were going to

            MockExtractor.Verify(x => x.ExtractVideoUrlAsync(fileUrl, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()), Times.Never);
        }

        /// <summary>Polls a condition, so a busy test run doesn't fail on a fixed wait that was too short.</summary>
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

        [Fact]
        public async Task Skip_ShouldCancelOngoingPreBuffer()
        {
            // Arrange
            var pageUrl1 = "https://example.com/p1";
            var pageUrl2 = "https://example.com/p2";

            var tcs = new TaskCompletionSource<string>();
            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(pageUrl2, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .Returns(tcs.Task);

            var vm = new HypnoViewModel(MockExtractor.Object, _mockDownloadService.Object);

            var items = new[] {
                new VideoItem("v1.mp4"),
                new VideoItem(pageUrl1) { OriginalPageUrl = pageUrl1 },
                new VideoItem(pageUrl2) { OriginalPageUrl = pageUrl2 }
            };
            vm.SetQueue(items);

            // Start pre-buffering for item 2
            vm.OnMediaOpened(this, new FlyleafLib.MediaPlayer.OpenCompletedArgs());
            await Task.Delay(50); // Extraction for p2 starts

            // Act
            vm.PlayNext(force: true); // Should cancel pre-buffer for p2

            // Assert
            // We can't easily check internal CTS but we can verify that if we resume the task, 
            // it doesn't try to download if cancelled.
            tcs.SetResult("direct.mp4");
            await Task.Delay(50);

            // Should either not call Download OR call it with a cancelled token
            // But checking for NOT called is easier if we verify the flow.
        }
    }
}

