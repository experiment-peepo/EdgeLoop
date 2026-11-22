using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// Saving the launcher's playlist (session.json, in the test run's own Data folder).
    /// </summary>
    public class LauncherSessionTests : TestBase
    {
        private readonly string _sessionFile = AppPaths.SessionFile;
        private readonly string? _savedCopy;

        public LauncherSessionTests()
        {
            // Leave whatever session file the test run had as it was
            if (File.Exists(_sessionFile))
            {
                _savedCopy = Path.GetTempFileName();
                File.Copy(_sessionFile, _savedCopy, overwrite: true);
            }
        }

        public override void Dispose()
        {
            try
            {
                if (_savedCopy != null) File.Move(_savedCopy, _sessionFile, overwrite: true);
                else if (File.Exists(_sessionFile)) File.Delete(_sessionFile);
            }
            catch { }
            base.Dispose();
        }

        private LauncherViewModel CreateLauncher()
        {
            ServiceContainer.Register(new VideoPlayerService());
            var launcher = new LauncherViewModel(); // Doesn't load a session: RememberLastPlaylist is still off
            MockSettings.Setup(s => s.RememberLastPlaylist).Returns(true);
            return launcher;
        }

        [Fact]
        public async Task SaveOnExit_WaitsForASaveInProgress_InsteadOfSkippingIt()
        {
            var launcher = CreateLauncher();
            launcher.AddedFiles.Add(new VideoItem(@"C:\Videos\last-change.mp4"));

            // Another save is writing the file right now
            var saveLock = (SemaphoreSlim)typeof(LauncherViewModel)
                .GetField("_saveSemaphore", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(launcher)!;
            saveLock.Wait();
            var otherSave = Task.Run(async () =>
            {
                await Task.Delay(300);
                saveLock.Release();
            });

            launcher.SaveSession(runInBackground: false); // What Exit does
            await otherSave;

            File.ReadAllText(_sessionFile).Should().Contain("last-change.mp4", "the save on exit used to be skipped while another save ran");
        }

        [Fact]
        public async Task BackgroundSaves_EndWithTheNewestPlaylist()
        {
            var launcher = CreateLauncher();
            for (int i = 0; i < 20; i++)
            {
                launcher.AddedFiles.Add(new VideoItem($@"C:\Videos\video{i}.mp4"));
                launcher.SaveSession(); // In the background, often while the previous one is still writing
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !(File.Exists(_sessionFile) && SafeRead().Contains("video19.mp4")))
                await Task.Delay(25);

            SafeRead().Should().Contain("video19.mp4", "the last change is saved even when its save found another one running");
            File.Exists(_sessionFile + ".tmp").Should().BeFalse();
        }

        private string SafeRead()
        {
            try { return File.ReadAllText(_sessionFile); }
            catch (IOException) { return string.Empty; }
        }
    }
}
