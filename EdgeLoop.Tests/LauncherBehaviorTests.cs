using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdgeLoop.Tests
{
    public class LauncherBehaviorTests : TestBase
    {
        // --- Status banner ---

        private static async Task WaitUntil(Func<bool> condition)
        {
            // Auto-clear continuations are posted back to the test's synchronization context
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }

        [Fact]
        public async Task InfoMessages_ClearThemselvesAfterTheDelay()
        {
            var time = new FakeTimeProvider();
            var banner = new StatusBanner { Time = time };

            banner.Show("Added 3 videos", StatusMessageType.Success);
            time.Advance(StatusBanner.AutoClearDelay - TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            banner.Message.Should().Be("Added 3 videos");

            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntil(() => banner.Message == null);
            banner.Message.Should().BeNull();
        }

        [Theory]
        [InlineData(StatusMessageType.Warning)]
        [InlineData(StatusMessageType.Error)]
        public async Task WarningsAndErrors_StayUntilDismissed(StatusMessageType type)
        {
            var time = new FakeTimeProvider();
            var banner = new StatusBanner { Time = time };

            banner.Show("Something went wrong", type);
            time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(50);
            banner.Message.Should().Be("Something went wrong");

            banner.Dismiss();
            banner.Message.Should().BeNull();
        }

        [Fact]
        public async Task ANewerMessage_IsNotClearedByTheOlderTimer()
        {
            var time = new FakeTimeProvider();
            var banner = new StatusBanner { Time = time };

            banner.Show("First");
            time.Advance(TimeSpan.FromSeconds(4));
            banner.Show("Second");
            time.Advance(TimeSpan.FromSeconds(3)); // first message's timer fires
            await Task.Delay(50);
            banner.Message.Should().Be("Second");

            time.Advance(TimeSpan.FromSeconds(4));
            await WaitUntil(() => banner.Message == null);
            banner.Message.Should().BeNull();
        }

        [Fact]
        public async Task ProgressMessages_StayWhileBusy()
        {
            var time = new FakeTimeProvider();
            bool busy = true;
            var banner = new StatusBanner { Time = time, IsBusy = () => busy };

            banner.Show("Importing playlist... 3 of 40 videos");
            time.Advance(TimeSpan.FromSeconds(20));
            await Task.Delay(50);
            banner.Message.Should().NotBeNull();

            busy = false;
            time.Advance(StatusBanner.AutoClearDelay);
            await WaitUntil(() => banner.Message == null);
            banner.Message.Should().BeNull();
        }

        // --- Failed videos ---

        [Fact]
        public void FailedVideos_AreMarkedNotRemoved()
        {
            var failed = new VideoItem(@"C:\Videos\a.mp4");
            var duplicate = new VideoItem(@"C:\Videos\a.mp4");
            var other = new VideoItem(@"C:\Videos\b.mp4");
            var playlist = new List<VideoItem> { failed, other, duplicate };

            var marked = LauncherViewModel.MarkFailed(playlist, failed, "Network error");

            marked.Should().BeEquivalentTo(new[] { failed, duplicate });
            playlist.Should().HaveCount(3, "failed videos stay in the playlist and saved session");
            failed.PlaybackFailed.Should().BeTrue();
            failed.PlaybackError.Should().Be("Network error");
            duplicate.PlaybackFailed.Should().BeTrue();
            other.PlaybackFailed.Should().BeFalse();
        }

        [Fact]
        public void FailedVideos_MatchByPageUrl_AndGetAFallbackReason()
        {
            var failed = new VideoItem("https://cdn.example.com/1.mp4") { OriginalPageUrl = "https://hypnotube.com/video/x-1.html" };
            var samePage = new VideoItem("https://cdn.example.com/other.mp4") { OriginalPageUrl = "https://hypnotube.com/video/x-1.html" };

            LauncherViewModel.MarkFailed(new[] { failed, samePage }, failed, "  ");

            samePage.PlaybackError.Should().Be("Playback failed");
        }

        [Fact]
        public void ClearingTheError_UnmarksTheVideo()
        {
            var item = new VideoItem(@"C:\Videos\a.mp4") { PlaybackError = "Timeout" };

            item.PlaybackError = null;

            item.PlaybackFailed.Should().BeFalse();
        }

        // --- Censoring is hidden ---

        [Fact]
        public void Censoring_StaysOff_WhileTheFeatureIsHidden()
        {
            FeatureFlags.CensorFeatureVisible.Should().BeFalse();
            FeatureFlags.IsCensorEnabled(new UserSettings { EnableLocalCensor = true }).Should().BeFalse(
                "an older settings.json with censoring on must not run a feature users can't see or turn off");
        }
    }
}
