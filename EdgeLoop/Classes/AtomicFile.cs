using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Saves files so that a crash or power cut leaves either the old file or the new one,
    /// never a half-written or missing one. The text goes to "&lt;file&gt;.tmp", is flushed to
    /// disk, and then replaces the file in one step.
    /// </summary>
    /// <remarks>
    /// Deleting the old file before moving the new one in (as the saves here used to) leaves a
    /// moment with no file at all, and a crash then loses it.
    /// </remarks>
    public static class AtomicFile
    {
        // Same as File.WriteAllText: UTF-8 without a byte-order mark
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static void WriteAllText(string path, string contents)
        {
            var tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }

        public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            var tempPath = path + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
    }
}
