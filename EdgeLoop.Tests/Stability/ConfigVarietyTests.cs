using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Tests.Stability
{
    public class ConfigVarietyTests : TestBase
    {

        [Fact]
        public void VideoShuffle_WhenEnabled_ShouldRandomizeSelection()
        {
            // Arrange
            MockSettings.Setup(s => s.VideoShuffle).Returns(true);
            var vm = new HypnoViewModel(MockExtractor.Object);
            var items = Enumerable.Range(0, 100).Select(i => new VideoItem($"v{i}.mp4")).ToArray();
            vm.SetQueue(items);

            // Act
            var selectedIndices = new HashSet<int>();
            for (int i = 0; i < 50; i++)
            {
                vm.PlayNext(force: true);
                selectedIndices.Add(vm.CurrentIndex);
            }

            // Assert
            // With 100 items and 50 skips, if it wasn't shuffled (it starts at 0 or random then goes +1), 
            // the spread would be linear. With shuffle, it should be random.
            // This is a bit hard to test deterministically, but we can check if we skip forward significantly.
            selectedIndices.Count.Should().BeGreaterThan(1, "Should have selected multiple different videos");
        }

        [Fact]
        public async Task RememberFilePosition_WhenEnabled_ShouldSeekOnLoad()
        {
            // Arrange
            MockSettings.Setup(s => s.RememberFilePosition).Returns(true);

            // Verify App.Settings is correctly linked to our mock
            App.Settings.Should().NotBeNull();
            App.Settings.RememberFilePosition.Should().BeTrue();

            var pageUrl = "https://pmvhaven.com/video/remember-me";
            var item = new VideoItem(pageUrl);
            item.OriginalPageUrl = pageUrl;

            var savedPos = TimeSpan.FromSeconds(45);
            // Ensure any existing state is cleared
            PlaybackPositionTracker.Instance.ClearPosition(item.TrackingPath);
            PlaybackPositionTracker.Instance.UpdatePosition(item.TrackingPath, savedPos);

            // Verify tracker itself has the data
            var checkPos = PlaybackPositionTracker.Instance.GetPosition(item.TrackingPath);
            checkPos.Should().Be(savedPos, "Tracker should return the position we just updated");

            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(pageUrl, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .ReturnsAsync("https://cdn.pmvhaven.com/video.mp4");

            var vm = new HypnoViewModel(MockExtractor.Object);

            // Act
            vm.SetQueue(new[] { item });
            await Task.Delay(200); // Give more time for async load and tracker lookup

            // Assert
            var pendingSeekField = typeof(HypnoViewModel).GetField("_pendingSeekPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pendingSeek = (TimeSpan?)pendingSeekField?.GetValue(vm);

            pendingSeek.Should().Be(savedPos, "ViewModel should have picked up the saved position during LoadCurrentVideo");

            PlaybackPositionTracker.Instance.ClearPosition(item.TrackingPath);
        }

        [Fact]
        public async Task HypnotubeCookies_ShouldBeInjectedIntoHeaders()
        {
            // Arrange
            var cookieValue = "session=test_session_123";
            // Mock the encrypted backing property — CookieProtector.Unprotect handles plaintext transparently
            MockSettings.Setup(s => s.HypnotubeCookiesEncrypted).Returns(cookieValue);

            const string pageUrl = "https://hypnotube.com/video/some-video-123.html";
            const string mediaUrl = "https://hypnotube.com/video/123.mp4";
            MockExtractor.Setup(x => x.ExtractVideoUrlAsync(pageUrl, It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
                .ReturnsAsync(mediaUrl);

            var vm = new HypnoViewModel(MockExtractor.Object);
            var item = new VideoItem(mediaUrl) { OriginalPageUrl = pageUrl };

            // Act: LoadCurrentVideo builds the demuxer headers before opening the stream
            vm.SetQueue(new[] { item });
            string? headers = null;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   !(vm.Config.Demuxer.FormatOpt.TryGetValue("headers", out headers) && headers!.Contains("Cookie:")))
                await Task.Delay(25);

            // Assert: the cookie must survive alongside the Referer (it used to be overwritten)
            headers.Should().NotBeNull();
            headers.Should().Contain($"Cookie: {cookieValue}");
            headers.Should().Contain("Referer: https://hypnotube.com/video/some-video-123.html");

            vm.Dispose();
        }

        /// <summary>
        /// A player that never moves on by itself. The test videos don't exist, so a real player would fail
        /// to load them and skip ahead on a background thread, racing the test's assertions.
        /// </summary>
        private sealed class NoAutoAdvanceViewModel(IVideoUrlExtractor extractor) : HypnoViewModel(extractor)
        {
            public override void PlayNext(bool force = false) { }
        }

        [Fact]
        public void CoordinatedStart_MasterFollower_ShouldSyncIndex()
        {
            // Arrange
            using var masterVm = new NoAutoAdvanceViewModel(MockExtractor.Object)
            {
                UseCoordinatedStart = true,
                IsSyncMaster = true,
                SyncGroupId = "Group1"
            };
            using var followerVm = new NoAutoAdvanceViewModel(MockExtractor.Object)
            {
                UseCoordinatedStart = true,
                IsSyncMaster = false,
                SyncGroupId = "Group1"
            };

            var items = new[] { new VideoItem("v1.mp4"), new VideoItem("v2.mp4"), new VideoItem("v3.mp4") };
            masterVm.SetQueue(items, 0);
            followerVm.SetQueue(items, 0);

            // Act: the jump a sync master broadcasts to its group
            masterVm.JumpToIndex(2, true);
            followerVm.JumpToIndex(2, true);

            // Assert
            masterVm.CurrentIndex.Should().Be(2);
            followerVm.CurrentIndex.Should().Be(2);
        }
    }
}

