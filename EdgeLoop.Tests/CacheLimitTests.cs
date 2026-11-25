using System;
using System.IO;
using System.Linq;
using EdgeLoop.Classes;
using AwesomeAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// The download cache's size limit, cleanup and Clear button. The folder is the user's choice and
    /// may hold their own videos, so the main thing to prove is that only EdgeLoop's files go.
    /// </summary>
    public class CacheLimitTests : TestBase
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"edgeloop-cachelimit-{Guid.NewGuid():N}");
        private readonly VideoDownloadService _service = new VideoDownloadService();

        public CacheLimitTests()
        {
            Directory.CreateDirectory(_dir);
            MockSettings.Setup(s => s.EnableLocalCaching).Returns(true);
            MockSettings.Setup(s => s.LocalCacheDirectory).Returns(_dir); // Never the real cache folder
        }

        public override void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
            base.Dispose();
        }

        /// <summary>A download of EdgeLoop's, last used some hours ago.</summary>
        private string Download(string name, int kilobytes, double hoursAgo)
        {
            var path = Path.Combine(_dir, "edgeloop-" + Hash(name) + ".mp4");
            return Write(path, kilobytes, hoursAgo);
        }

        private string Write(string path, int kilobytes, double hoursAgo)
        {
            File.WriteAllBytes(path, new byte[kilobytes * 1024]);
            var when = DateTime.Now.AddHours(-hoursAgo);
            File.SetCreationTime(path, when);
            File.SetLastWriteTime(path, when);
            File.SetLastAccessTime(path, when);
            return path;
        }

        private static string Hash(string name) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..32].ToLowerInvariant();

        [Fact]
        public void OverTheLimit_TheLeastRecentlyUsedGoFirst()
        {
            var oldest = Download("a", 100, hoursAgo: 72);
            var middle = Download("b", 100, hoursAgo: 48);
            var newest = Download("c", 100, hoursAgo: 24);

            _service.TrimToLimit(250 * 1024).Should().Be(100 * 1024);

            File.Exists(oldest).Should().BeFalse();
            File.Exists(middle).Should().BeTrue();
            File.Exists(newest).Should().BeTrue();
        }

        [Fact]
        public void UnderTheLimit_OrWithNoLimit_NothingIsDeleted()
        {
            var file = Download("a", 100, hoursAgo: 72);
            _service.TrimToLimit(1024 * 1024).Should().Be(0);
            _service.TrimToLimit(0).Should().Be(0, "0 means no limit");
            File.Exists(file).Should().BeTrue();
        }

        [Fact]
        public void ACopyUsedInTheLastFewMinutes_IsKept_EvenOverTheLimit()
        {
            var playing = Download("playing", 300, hoursAgo: 0.05);
            _service.TrimToLimit(100 * 1024).Should().Be(0);
            File.Exists(playing).Should().BeTrue("it may be playing");
        }

        [Fact]
        public void AFileThatIsInUse_IsSkipped()
        {
            var locked = Download("locked", 200, hoursAgo: 72);
            var other = Download("other", 200, hoursAgo: 48);

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) // As the player does
            {
                _service.TrimToLimit(100 * 1024).Should().Be(200 * 1024);
                File.Exists(locked).Should().BeTrue();
                File.Exists(other).Should().BeFalse();
            }
        }

        [Fact]
        public void TheUsersOwnVideos_AreNeverTouched()
        {
            var holiday = Write(Path.Combine(_dir, "holiday.mp4"), 500, hoursAgo: 24 * 400);
            // Some sites name their downloads with a bare MD5, like EdgeLoop's copies used to be named
            var md5Named = Write(Path.Combine(_dir, "d41d8cd98f00b204e9800998ecf8427e.mp4"), 500, hoursAgo: 24 * 400);
            var other = Write(Path.Combine(_dir, "edgeloop-notes.mp4"), 500, hoursAgo: 24 * 400);
            var own = Download("own", 500, hoursAgo: 24 * 20);

            _service.CleanupOldFiles(10);
            _service.TrimToLimit(1);
            var (freed, kept) = _service.ClearAll();

            File.Exists(own).Should().BeFalse("it's EdgeLoop's, unused for 20 days");
            foreach (var mine in new[] { holiday, md5Named, other })
                File.Exists(mine).Should().BeTrue($"{Path.GetFileName(mine)} isn't one of EdgeLoop's downloads");
            (freed, kept).Should().Be((0L, 0), "nothing else was left to clear");
        }

        [Fact]
        public void CleanupOldFiles_DeletesUnusedDownloads_AndKeepsRecentOnes()
        {
            var stale = Download("stale", 10, hoursAgo: 24 * 11);
            var recent = Download("recent", 10, hoursAgo: 24 * 9);
            var leftover = Write(Path.Combine(_dir, "edgeloop-" + Hash("x") + ".mp4.downloading"), 10, hoursAgo: 5);
            var active = Write(Path.Combine(_dir, "edgeloop-" + Hash("y") + ".mp4.downloading"), 10, hoursAgo: 0.1);

            _service.CleanupOldFiles(10);

            File.Exists(stale).Should().BeFalse();
            File.Exists(recent).Should().BeTrue();
            File.Exists(leftover).Should().BeFalse("a download that stopped hours ago");
            File.Exists(active).Should().BeTrue("a download still running");
        }

        [Fact]
        public void PlayingACopy_CountsAsUsingIt()
        {
            const string page = "https://rule34video.com/video/1/x/";
            var copy = _service.GetCachePath(page);
            Write(copy, 10, hoursAgo: 24 * 9);

            _service.GetCachedFilePath(page).Should().Be(copy);

            File.GetLastAccessTime(copy).Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1), "the 10-day cleanup counts from the last use, not the download");
        }

        [Fact]
        public void ClearAll_ReportsWhatItFreed_AndWhatIsInUse()
        {
            Download("a", 100, hoursAgo: 1);
            var locked = Download("b", 50, hoursAgo: 1);

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                _service.ClearAll().Should().Be((100L * 1024, 1));
            }
            File.Exists(locked).Should().BeTrue();
        }

        [Fact]
        public void ACopyUnderTheOldName_IsStillFound_AndANewDownloadGetsTheNewName()
        {
            const string page = "https://rule34video.com/video/2/y/";
            var newName = Path.GetFileName(_service.GetCachePath(page));
            newName.Should().StartWith("edgeloop-").And.EndWith(".mp4");

            var old = Path.Combine(_dir, newName.Substring("edgeloop-".Length));
            Write(old, 10, hoursAgo: 1);
            _service.GetCachePath(page).Should().Be(old, "an existing copy keeps its place");
        }

        [Fact]
        public void OldNames_CountAsEdgeLoopsOwn_OnlyInItsOwnFolder()
        {
            var own = AppPaths.ProfileVideoCacheDirectory; // Names only: nothing in it is read
            VideoDownloadService.IsOwnCacheFile(Path.Combine(own, "d41d8cd98f00b204e9800998ecf8427e.mp4")).Should().BeTrue();
            VideoDownloadService.IsOwnCacheFile(Path.Combine(_dir, "d41d8cd98f00b204e9800998ecf8427e.mp4")).Should().BeFalse("a custom folder may hold the user's own files with such names");
            VideoDownloadService.IsOwnCacheFile(Path.Combine(_dir, "edgeloop-d41d8cd98f00b204e9800998ecf8427e.mp4")).Should().BeTrue();
            VideoDownloadService.IsOwnCacheFile(Path.Combine(own, "holiday.mp4")).Should().BeFalse();
            VideoDownloadService.IsOwnCacheFile(null).Should().BeFalse();
        }

        // ---- EdgeLoop's own folder ----

        [Fact]
        public void WithNoFolderPicked_DownloadsGoToEdgeLoopsOwnFolder()
        {
            foreach (var none in new[] { null, "", " " })
            {
                MockSettings.Setup(s => s.LocalCacheDirectory).Returns(none!);
                Path.GetDirectoryName(_service.GetCachePath("https://example.com/v")).Should().Be(Path.Combine(AppContext.BaseDirectory, "Data", "VideoCache"));
            }

            _service.UsesOwnFolder.Should().BeTrue("so the old downloads move there");

            MockSettings.Setup(s => s.LocalCacheDirectory).Returns(_dir);
            Path.GetDirectoryName(_service.GetCachePath("https://example.com/v")).Should().Be(_dir, "a picked folder is used");
            _service.UsesOwnFolder.Should().BeFalse("so the old downloads stay where they are");
        }

        /// <summary>The move, between two temp folders: the old one counts everything named by a hash as EdgeLoop's, as the real one does.</summary>
        private string[] Move(string from, bool sameDrive = true)
        {
            VideoDownloadService.MoveDownloads(from, _dir, sameDrive, path => Path.GetFileName(path) is var name
                && System.Text.RegularExpressions.Regex.IsMatch(name, "^(edgeloop-)?[0-9a-f]{32}\\."));
            return Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).OrderBy(n => n).ToArray();
        }

        private string OldFolder()
        {
            var old = Path.Combine(_dir, "old");
            Directory.CreateDirectory(old);
            return old;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)] // Copied, then deleted
        public void OldDownloads_AreMovedIn_UnderTheNewName_AndTheEmptyFolderGoes(bool sameDrive)
        {
            var old = OldFolder();
            Write(Path.Combine(old, Hash("a") + ".mp4"), 30, hoursAgo: 24 * 3);
            Write(Path.Combine(old, "edgeloop-" + Hash("b") + ".mp4"), 20, hoursAgo: 2);

            Move(old, sameDrive).Should().BeEquivalentTo("edgeloop-" + Hash("a") + ".mp4", "edgeloop-" + Hash("b") + ".mp4");

            new FileInfo(Path.Combine(_dir, "edgeloop-" + Hash("a") + ".mp4")).Length.Should().Be(30 * 1024);
            File.GetLastAccessTime(Path.Combine(_dir, "edgeloop-" + Hash("a") + ".mp4")).Should().BeCloseTo(DateTime.Now.AddDays(-3), TimeSpan.FromMinutes(1), "moving isn't using: the 10 days keep counting");
            Directory.Exists(old).Should().BeFalse();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Moving_LeavesOtherFilesAndRunningDownloads_AndDropsUnusedOnes(bool sameDrive)
        {
            var old = OldFolder();
            var mine = Write(Path.Combine(old, "holiday.mp4"), 10, hoursAgo: 24 * 400);
            var running = Write(Path.Combine(old, Hash("r") + ".mp4.downloading"), 10, hoursAgo: 0.1);
            var locked = Write(Path.Combine(old, Hash("l") + ".mp4"), 10, hoursAgo: 5);
            var unused = Write(Path.Combine(old, Hash("u") + ".mp4"), 10, hoursAgo: 24 * 11);

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) // As the player does
            {
                var arrived = Move(old, sameDrive);
                if (sameDrive) arrived.Should().BeEmpty();
                else arrived.Should().Equal("edgeloop-" + Hash("l") + ".mp4"); // Copied; the original goes next time
            }

            foreach (var kept in new[] { mine, running, locked })
                File.Exists(kept).Should().BeTrue($"{Path.GetFileName(kept)} stays");
            File.Exists(unused).Should().BeFalse("unused for 11 days, which the cleanup deletes anyway");

            Move(old, sameDrive).Should().Equal(new[] { "edgeloop-" + Hash("l") + ".mp4" }, "what was in use is moved at the next start");
        }

        [Fact]
        public void AVideoDownloadedAgainSince_KeepsTheNewCopy()
        {
            var old = OldFolder();
            Write(Path.Combine(old, Hash("a") + ".mp4"), 30, hoursAgo: 5);
            var current = Download("a", 50, hoursAgo: 1);

            Move(old);

            new FileInfo(current).Length.Should().Be(50 * 1024);
            Directory.Exists(old).Should().BeFalse();
        }

        [Fact]
        public void MovingAFolderOntoItself_ChangesNothing()
        {
            var file = Download("a", 10, hoursAgo: 24 * 20);
            VideoDownloadService.MoveDownloads(_dir, _dir + Path.DirectorySeparatorChar, true, _ => true);
            File.Exists(file).Should().BeTrue();
        }
    }
}
