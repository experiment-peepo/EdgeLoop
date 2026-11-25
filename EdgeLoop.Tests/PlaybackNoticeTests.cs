using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using AwesomeAssertions;

namespace EdgeLoop.Tests
{
    public class PlaybackNoticeTests : TestBase
    {
        /// <summary>A player that never moves on by itself, so failures don't race the assertions.</summary>
        private sealed class StillPlayer(IVideoUrlExtractor extractor) : HypnoViewModel(extractor)
        {
            public override void PlayNext(bool force = false) { }
        }

        // --- Wording ---

        [Theory]
        [InlineData("[avformat_open_input] Server returned 404 Not Found (-875574520)", "It isn't online anymore (404).")]
        [InlineData("HTTP error 403 Forbidden", "The site refused access (403).")]
        [InlineData("The operation has timed out.", "It took too long to load.")]
        [InlineData("Could not find file 'C:\\v\\a.mp4'", "The file is missing.")]
        [InlineData("Something odd", null)]
        public void FailureReasons_AreInPlainWords(string error, string? expected)
        {
            PlaybackFailureText.Reason(error).Should().Be(expected);
        }

        [Fact]
        public void Notices_SayWhatFailed_AndWhatHappensNext()
        {
            PlaybackFailureText.Headline("Sunset").Should().Be("Couldn't play \u201CSunset\u201D");
            PlaybackFailureText.Headline(null!).Should().Be("Couldn't play a video");
            PlaybackFailureText.Detail("404 Not Found").Should().Be("It isn't online anymore (404). Skipping to the next video.");
            PlaybackFailureText.Detail("weird").Should().Be("Skipping to the next video.");
            PlaybackFailureText.ScreenName(@"\\.\DISPLAY3").Should().Be("Screen 3");
        }

        // --- Loading doesn't black out the screen ---

        [Fact]
        public void TheOverlay_StartsAtTheVideosOpacity_NotTheDefault()
        {
            using var player = new StillPlayer(MockExtractor.Object);
            var items = new[] { new VideoItem("a.mp4") { Opacity = 0.3 }, new VideoItem("b.mp4") { Opacity = 0.6 } };

            player.SetQueue(items, 0);

            player.Opacity.Should().Be(0.3, "the screen shouldn't sit at 90% while the first video loads");
        }

        // --- Now Playing ---

        [Fact]
        public async Task NowPlaying_ShowsTheLastFailure()
        {
            using var player = new StillPlayer(MockExtractor.Object);
            var row = new ActivePlayerViewModel("Screen 1", player);

            player.OnMediaFailed(new Exception("Server returned 404 Not Found"));

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (row.Problem == null && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            row.Problem.Should().Be("Couldn't play a video. It isn't online anymore (404).");
        }

        // --- Setting ---

        [Fact]
        public void NoticesSetting_IsTrackedAndSaved()
        {
            MockSettings.SetupProperty(s => s.ShowPlaybackNotices, true);
            var settings = new SettingsViewModel(_ => true, new[] { new ScreenViewer(null) { DeviceName = @"\\.\DISPLAY1" } });

            settings.ShowPlaybackNotices = false;
            settings.HasChanges.Should().BeTrue();

            settings.Save();
            MockSettings.Object.ShowPlaybackNotices.Should().BeFalse();
        }
    }
}
