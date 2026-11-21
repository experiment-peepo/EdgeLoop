using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EdgeLoop.ViewModels;
using Flyleaf.FFmpeg;
using static Flyleaf.FFmpeg.Raw;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Finds out how long local videos are, in the background, for the playlist cards. Reads the
    /// container header with the FFmpeg that the playback engine already loaded, and remembers the
    /// answer (in Data/durations.json) until the file's size or modification time changes.
    /// Streams aren't probed: they learn their length the first time they play.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class DurationProbe
    {
        private static readonly Lazy<DurationProbe> _shared =
            new(() => new DurationProbe(Path.Combine(AppPaths.DataDirectory, "durations.json")));

        public static DurationProbe Shared => _shared.Value;

        private sealed record CacheEntry(long Length, long LastWriteUtcTicks, double Seconds);

        private readonly string _cacheFile;
        private readonly Func<string, TimeSpan?> _probe;
        private readonly bool _usesFFmpeg;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<VideoItem> _queue = new();
        private readonly object _loadLock = new();
        private bool _loaded;
        private int _dirty;
        private int _running;

        /// <param name="cacheFile">Where results are kept between runs, or null for none.</param>
        /// <param name="probe">Reads a file's length; defaults to FFmpeg. Tests pass a fake.</param>
        public DurationProbe(string cacheFile, Func<string, TimeSpan?> probe = null)
        {
            _cacheFile = cacheFile;
            _usesFFmpeg = probe == null;
            _probe = probe ?? ProbeWithFFmpeg;
        }

        /// <summary>Queues local items whose length isn't known yet; each gets its <see cref="VideoItem.Duration"/> when found.</summary>
        public void Request(IEnumerable<VideoItem> items)
        {
            foreach (var item in items)
            {
                if (item != null && item.Duration == null && !item.IsUrl && !string.IsNullOrEmpty(item.FilePath))
                    _queue.Enqueue(item);
            }

            if (!_queue.IsEmpty && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                _ = Task.Run(DrainAsync);
        }

        private async Task DrainAsync()
        {
            try
            {
                // At startup the saved playlist can load before the playback engine has loaded FFmpeg
                for (int i = 0; i < 40 && _usesFFmpeg && !FlyleafLib.Engine.IsLoaded; i++)
                    await Task.Delay(250);

                while (true)
                {
                    while (_queue.TryDequeue(out var item))
                    {
                        if (item.Duration != null) continue;
                        TimeSpan? duration = null;
                        try
                        {
                            duration = GetDuration(item.FilePath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"[DurationProbe] Couldn't read the length of {item.FilePath}: {ex.Message}");
                        }

                        if (duration is { } found)
                            Publish(item, found);
                    }

                    Save();

                    // Stop, unless something was queued after the queue looked empty
                    Volatile.Write(ref _running, 0);
                    if (_queue.IsEmpty || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                        return;
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _running, 0);
                Logger.Warning($"[DurationProbe] Stopped: {ex.Message}");
            }
        }

        private static void Publish(VideoItem item, TimeSpan duration)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                item.Duration ??= duration;
            else
                dispatcher.InvokeAsync(() => item.Duration ??= duration);
        }

        /// <summary>The length of a local file: remembered if the file hasn't changed, otherwise probed. Null if unreadable.</summary>
        internal TimeSpan? GetDuration(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;

            EnsureLoaded();
            string key = file.FullName;
            long lastWrite = file.LastWriteTimeUtc.Ticks;

            if (_cache.TryGetValue(key, out var entry) && entry.Length == file.Length && entry.LastWriteUtcTicks == lastWrite)
                return TimeSpan.FromSeconds(entry.Seconds);

            var duration = _probe(file.FullName);
            if (duration is { } found && found > TimeSpan.Zero)
            {
                _cache[key] = new CacheEntry(file.Length, lastWrite, found.TotalSeconds);
                Interlocked.Exchange(ref _dirty, 1);
            }
            return duration;
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                try
                {
                    if (_cacheFile != null && File.Exists(_cacheFile))
                    {
                        var saved = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cacheFile));
                        foreach (var (key, value) in saved ?? new())
                            _cache[key] = value;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[DurationProbe] Ignoring unreadable cache {_cacheFile}: {ex.Message}");
                }
                _loaded = true;
            }
        }

        /// <summary>Writes new results to the cache file (if anything changed).</summary>
        internal void Save()
        {
            if (_cacheFile == null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                var snapshot = _cache.ToDictionary(p => p.Key, p => p.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile));
                var temp = _cacheFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                File.Move(temp, _cacheFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DurationProbe] Couldn't save {_cacheFile}: {ex.Message}");
            }
        }

        /// <summary>Opens the file's container with FFmpeg and reads its duration. Null if FFmpeg isn't loaded or can't tell.</summary>
        internal static unsafe TimeSpan? ProbeWithFFmpeg(string path)
        {
            if (!FlyleafLib.Engine.IsLoaded) return null;

            AVFormatContext* context = null;
            AVDictionary* options = null;
            try
            {
                if (avformat_open_input(ref context, path, null, ref options) < 0 || context == null)
                    return null;

                // Most containers state the length in their header; others need a look at the streams
                if (context->duration <= 0 && avformat_find_stream_info(context, null) < 0)
                    return null;

                // FFmpeg counts in microseconds; a TimeSpan tick is 100 ns
                return context->duration > 0 ? TimeSpan.FromTicks(context->duration * 10) : null;
            }
            finally
            {
                if (context != null)
                    avformat_close_input(&context);
                if (options != null)
                    av_dict_free(&options);
            }
        }
    }
}
