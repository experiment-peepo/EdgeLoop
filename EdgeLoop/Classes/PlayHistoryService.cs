using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Service for managing video play history and statistics.
    /// Provides data for smart shuffle scoring.
    /// </summary>
    public class PlayHistoryService
    {
        private readonly string _historyFilePath;
        private readonly List<VideoPlayRecord> _records = new List<VideoPlayRecord>();
        private readonly object _lock = new object();
        private const int MaxRecords = 50000;
        private const int RetentionDays = 30;

        public PlayHistoryService()
        {
            _historyFilePath = Path.Combine(AppPaths.DataDirectory, "play_history.json");
            LoadHistory();
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    var json = SafeFileReader.ReadAllTextSafe(_historyFilePath);
                    if (string.IsNullOrEmpty(json)) return;
                    var loaded = JsonSerializer.Deserialize<List<VideoPlayRecord>>(json);
                    if (loaded != null)
                    {
                        lock (_lock)
                        {
                            _records.Clear();
                            _records.AddRange(loaded);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load play history: {ex.Message}");
            }
        }

        private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);
        private DateTime _lastSaveTime = DateTime.MinValue;
        private readonly TimeSpan _saveDebounce = TimeSpan.FromSeconds(5);

        public async Task SaveHistoryAsync()
        {
            // Debounce saves to prevent thrashing during fast skips
            if (DateTime.Now - _lastSaveTime < _saveDebounce)
            {
                return;
            }

            await _saveSemaphore.WaitAsync();
            try
            {
                // Double check after acquiring lock
                if (DateTime.Now - _lastSaveTime < _saveDebounce) return;
                await DoSaveAsync();
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        public void SaveHistorySync()
        {
            _saveSemaphore.Wait();
            try
            {
                DoSaveSync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save play history (sync): {ex.Message}");
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        private async Task DoSaveAsync()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    json = JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true });
                }

                // Atomic write using temp file
                string tempPath = _historyFilePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json);

                if (File.Exists(_historyFilePath)) File.Delete(_historyFilePath);
                File.Move(tempPath, _historyFilePath);

                _lastSaveTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save play history (async): {ex.Message}");
            }
        }

        private void DoSaveSync()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    json = JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true });
                }

                // Atomic write using temp file
                string tempPath = _historyFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(_historyFilePath)) File.Delete(_historyFilePath);
                File.Move(tempPath, _historyFilePath);

                _lastSaveTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save play history (sync internal): {ex.Message}");
            }
        }

        public void AddRecord(VideoPlayRecord record)
        {
            lock (_lock)
            {
                _records.Add(record);

                // Prune if exceeds max records
                if (_records.Count > MaxRecords)
                {
                    _records.RemoveRange(0, _records.Count - MaxRecords);
                }
            }
            _ = SaveHistoryAsync();
        }

        public void PruneOldRecords()
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            lock (_lock)
            {
                _records.RemoveAll(r => r.PlayedAt < cutoff);
            }
            _ = SaveHistoryAsync();
        }

        public List<VideoPlayRecord> GetRecentRecords(int count)
        {
            lock (_lock)
            {
                return _records.Skip(Math.Max(0, _records.Count - count)).ToList();
            }
        }

        public VideoPlayRecord GetLastPlay(string filePath)
        {
            lock (_lock)
            {
                return _records.LastOrDefault(r => r.FilePath == filePath);
            }
        }

        public double GetSkipRate(string filePath)
        {
            lock (_lock)
            {
                var fileRecords = _records.Where(r => r.FilePath == filePath).ToList();
                if (fileRecords.Count == 0) return 0.5; // Unknown

                int skipped = fileRecords.Count(r => r.WasSkipped);
                return (double)skipped / fileRecords.Count;
            }
        }

        public double GetAverageWatchPercent(string filePath)
        {
            lock (_lock)
            {
                var fileRecords = _records.Where(r => r.FilePath == filePath).ToList();
                if (fileRecords.Count == 0) return 0.5;

                return fileRecords.Average(r => r.WatchPercentage);
            }
        }

        public int GetTotalPlayCount(string filePath)
        {
            lock (_lock)
            {
                return _records.Count(r => r.FilePath == filePath);
            }
        }

        public List<VideoPlayRecord> GetRecordsForPlaylist(string playlistHash)
        {
            lock (_lock)
            {
                return _records.Where(r => r.PlaylistHash == playlistHash).ToList();
            }
        }
    }
}

