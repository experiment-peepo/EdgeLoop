using System;
using System.IO;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using AwesomeAssertions;

namespace EdgeLoop.Tests
{
    public class AtomicFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"edgeloop-atomic-{Guid.NewGuid():N}");

        public AtomicFileTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void ReplacesTheFile_AndLeavesNoTempFileBehind()
        {
            var path = Path.Combine(_dir, "session.json");
            File.WriteAllText(path, "old");

            AtomicFile.WriteAllText(path, "{\"new\":\"ü\"}");

            File.ReadAllText(path).Should().Be("{\"new\":\"ü\"}");
            File.Exists(path + ".tmp").Should().BeFalse();
            File.ReadAllBytes(path)[0].Should().Be((byte)'{', "no byte-order mark, like File.WriteAllText");
        }

        [Fact]
        public async Task Async_CreatesAndReplacesTheFile()
        {
            var path = Path.Combine(_dir, "history.json");

            await AtomicFile.WriteAllTextAsync(path, "first");
            await AtomicFile.WriteAllTextAsync(path, "second");

            File.ReadAllText(path).Should().Be("second");
            File.Exists(path + ".tmp").Should().BeFalse();
        }

        [Fact]
        public void LeftoverTempFile_FromACrash_DoesNotGetInTheWay()
        {
            var path = Path.Combine(_dir, "urlcache.json");
            File.WriteAllText(path, "good");
            File.WriteAllText(path + ".tmp", "half-writ"); // A save that crashed part-way

            AtomicFile.WriteAllText(path, "newer");

            File.ReadAllText(path).Should().Be("newer");
        }
    }
}
