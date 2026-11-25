using System.IO;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdgeLoop.Tests
{
    public class LauncherPlaylistTests : TestBase
    {
        private LauncherViewModel CreateLauncher(params string[] files)
        {
            MockSettings.Object.RememberLastPlaylist = false; // no session file reads or writes
            ServiceContainer.Register(new VideoPlayerService());
            var launcher = new LauncherViewModel();
            foreach (var file in files)
                launcher.AddedFiles.Add(new VideoItem(file));
            return launcher;
        }

        private static string[] Names(LauncherViewModel launcher) =>
            launcher.AddedFiles.Select(f => Path.GetFileNameWithoutExtension(f.FilePath)).ToArray();

        // --- Reordering ---

        [Theory]
        [InlineData(0, 2, true, 2)]   // A after C:  B C A D
        [InlineData(0, 2, false, 1)]  // A before C: B A C D
        [InlineData(3, 1, false, 1)]  // D before B: A D B C
        [InlineData(3, 1, true, 2)]   // D after B:  A B D C
        [InlineData(1, 1, true, 1)]   // onto itself
        public void DropIndex_PutsTheCardWhereTheLineWasShown(int from, int target, bool after, int expected)
        {
            LauncherViewModel.DropIndex(from, target, after, count: 4).Should().Be(expected);
        }

        [Fact]
        public void AltArrows_MoveTheCardOnePlace_AndStopAtTheEnds()
        {
            using var launcher = CreateLauncher(@"C:\v\A.mp4", @"C:\v\B.mp4", @"C:\v\C.mp4");
            var b = launcher.AddedFiles[1];

            launcher.MoveVideoItemBy(b, -1).Should().BeTrue();
            Names(launcher).Should().Equal("B", "A", "C");

            launcher.MoveVideoItemBy(b, -1).Should().BeFalse("it's already first");
            launcher.MoveVideoItemBy(b, +1).Should().BeTrue();
            launcher.MoveVideoItemBy(b, +1).Should().BeTrue();
            Names(launcher).Should().Equal("A", "C", "B");
        }

        // --- Removing with Undo ---

        [Fact]
        public void RemovingACard_CanBeUndone()
        {
            using var launcher = CreateLauncher(@"C:\v\A.mp4", @"C:\v\B.mp4", @"C:\v\C.mp4");

            launcher.RemoveItemCommand.Execute(launcher.AddedFiles[1]);
            Names(launcher).Should().Equal("A", "C");
            launcher.StatusActionText.Should().Be("Undo");

            launcher.StatusActionCommand.Execute(null);
            Names(launcher).Should().Equal("A", "B", "C");
            launcher.StatusActionText.Should().BeNull("Undo only works once");
        }

        [Fact]
        public void RemoveFailed_RemovesOnlyFailedVideos_AndUndoPutsThemBack()
        {
            using var launcher = CreateLauncher(@"C:\v\A.mp4", @"C:\v\B.mp4", @"C:\v\C.mp4", @"C:\v\D.mp4");
            launcher.AddedFiles[0].PlaybackError = "Timeout";
            launcher.AddedFiles[2].PlaybackError = "404";
            launcher.FailedCount.Should().Be(2);
            launcher.RemoveFailedCommand.CanExecute(null).Should().BeTrue();

            launcher.RemoveFailedCommand.Execute(null);
            Names(launcher).Should().Equal("B", "D");
            launcher.FailedCount.Should().Be(0);
            launcher.RemoveFailedCommand.CanExecute(null).Should().BeFalse();

            launcher.StatusActionCommand.Execute(null);
            Names(launcher).Should().Equal("A", "B", "C", "D");
        }

        // --- Start button hint ---

        [Fact]
        public void StartHint_SaysWhyStartIsDisabled()
        {
            using var launcher = CreateLauncher();
            launcher.StartHint.Should().Be("Add videos first");

            launcher.AddedFiles.Add(new VideoItem(@"C:\v\A.mp4"));
            launcher.IsHypnotizeEnabled.Should().BeFalse();
            launcher.StartHint.Should().Be("Choose a screen for every video first");

            launcher.AddedFiles[0].AssignedScreen = launcher.AvailableScreens.First(s => !s.IsAllScreens);
            launcher.IsHypnotizeEnabled.Should().BeTrue();
            launcher.StartHint.Should().Be("Start playback on the assigned screens");
        }

        // --- Durations ---

        [Theory]
        [InlineData(0, null)]
        [InlineData(7.4, "0:07")]
        [InlineData(187, "3:07")]
        [InlineData(3765, "1:02:45")]
        public void Durations_ReadLikeAClock(double seconds, string? expected)
        {
            VideoItem.FormatDuration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
        }

        [Fact]
        public void PlaylistFiles_KeepKnownDurations()
        {
            new PlaylistItem { DurationSeconds = 95 }.Duration.Should().Be(TimeSpan.FromSeconds(95));
            new PlaylistItem().Duration.Should().BeNull("older playlists have no duration");
        }

        // --- Status banner actions ---

        [Fact]
        public void BannerAction_RunsOnce_AndANewerMessageDropsIt()
        {
            var banner = new StatusBanner { Time = new FakeTimeProvider() };
            int undone = 0;

            banner.Show("Removed 1 video", StatusMessageType.Info, "Undo", () => undone++);
            banner.ActionText.Should().Be("Undo");
            banner.RunAction();
            banner.RunAction();
            undone.Should().Be(1);
            banner.Message.Should().BeNull();

            banner.Show("Removed 1 video", StatusMessageType.Info, "Undo", () => undone++);
            banner.Show("Added 3 videos", StatusMessageType.Success);
            banner.ActionText.Should().BeNull();
            banner.RunAction();
            undone.Should().Be(1, "the Undo belonged to the replaced message");
        }
    }

    public class DurationProbeTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "EdgeLoopTests", Guid.NewGuid().ToString("N"));

        public DurationProbeTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        }

        private string CreateVideo(string name)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return path;
        }

        [Fact]
        public void KnownFiles_AreNotProbedAgain_UntilTheyChange()
        {
            var video = CreateVideo("a.mp4");
            int probes = 0;
            var probe = new DurationProbe(Path.Combine(_folder, "durations.json"), _ => { probes++; return TimeSpan.FromSeconds(42); });

            probe.GetDuration(video).Should().Be(TimeSpan.FromSeconds(42));
            probe.GetDuration(video).Should().Be(TimeSpan.FromSeconds(42));
            probes.Should().Be(1);

            File.SetLastWriteTimeUtc(video, DateTime.UtcNow.AddMinutes(1));
            probe.GetDuration(video);
            probes.Should().Be(2, "a changed file is probed again");
        }

        [Fact]
        public void Results_SurviveARestart()
        {
            var video = CreateVideo("b.mkv");
            var cacheFile = Path.Combine(_folder, "durations.json");
            var first = new DurationProbe(cacheFile, _ => TimeSpan.FromSeconds(90));
            first.GetDuration(video);
            first.Save();

            var second = new DurationProbe(cacheFile, _ => throw new InvalidOperationException("should come from the cache"));
            second.GetDuration(video).Should().Be(TimeSpan.FromSeconds(90));
        }

        [Fact]
        public void MissingOrUnreadableFiles_HaveNoDuration()
        {
            var probe = new DurationProbe(null, _ => null);

            probe.GetDuration(Path.Combine(_folder, "gone.mp4")).Should().BeNull();
            probe.GetDuration(CreateVideo("broken.mp4")).Should().BeNull();
        }
    }
}
