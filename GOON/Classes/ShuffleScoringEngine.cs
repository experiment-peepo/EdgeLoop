using System;
using System.Collections.Generic;
using System.Linq;
using GOON.Classes.Metrics;
using GOON.ViewModels;

namespace GOON.Classes {
    public class ShuffleScoringEngine {
        private readonly PlayHistoryService _historyService;
        private readonly Random _random = new Random();
        
        // Track recently picked videos IN MEMORY (survives between picks in a session)
        // This is critical for fresh sessions with no history file
        private readonly Queue<string> _recentPicksQueue = new Queue<string>();
        private const int MaxRecentPicks = 10; // Track last 10 picks

        public ShuffleScoringEngine(PlayHistoryService historyService) {
            _historyService = historyService;
        }

        public int SelectBestVideo(VideoItem[] files, string playlistHash, UserSettings settings, int currentIndex = -1) {
            if (files == null || files.Length == 0) return 0;
            if (files.Length == 1) return 0;

            var candidates = new List<(int index, double score)>();
            var recentRecords = _historyService.GetRecentRecords(10);
            var playlistPaths = files.Select(f => f.FilePath).ToList();
            
            // Get paths we've recently picked (in-memory, survives across picks)
            var recentPicks = _recentPicksQueue.ToHashSet();
            var recentPicksList = _recentPicksQueue.ToList(); // For position-based penalty

            for (int i = 0; i < files.Length; i++) {
                var file = files[i];
                if (file == null) continue;
                
                // CRITICAL: Skip the currently playing video
                if (i == currentIndex) continue;

                // 1. Calculate Individual Scores from history
                double m1 = RecencyMetric.Calculate(file.FilePath, _historyService);
                double m2 = WatchTimeMetric.Calculate(file.FilePath, _historyService);
                double m3 = PlayCountMetric.Calculate(file.FilePath, playlistPaths, _historyService);
                
                double durationMs = 0;
                var lastPlay = _historyService.GetLastPlay(file.FilePath);
                if (lastPlay != null) {
                    durationMs = lastPlay.VideoDurationMs;
                }
                
                double m4 = LengthVarietyMetric.Calculate(durationMs, recentRecords);
                double m5 = CreatorVarietyMetric.Calculate(file.FilePath, recentRecords);
                double m6 = TimeOfDayMetric.Calculate(file.FilePath, _historyService);

                // 2. Combine with Weights
                double score = 
                    (m1 * settings.ShuffleRecencyWeight) + 
                    (m2 * settings.ShufflePreferenceWeight * 0.5) +
                    (m3 * settings.ShufflePreferenceWeight * 0.3) +
                    (m6 * settings.ShufflePreferenceWeight * 0.2) +
                    (m4 * settings.ShuffleLengthWeight) + 
                    (m5 * settings.ShuffleVarietyWeight);

                // 3. CRITICAL IN-MEMORY PENALTY: Apply harsh penalty for recently picked videos
                // This works even in fresh sessions with no history file
                if (recentPicks.Contains(file.FilePath)) {
                    int positionInQueue = recentPicksList.LastIndexOf(file.FilePath);
                    int recency = recentPicksList.Count - positionInQueue; // 1 = most recent, 10 = oldest
                    
                    // Most recent = 0.01 (99% penalty), oldest = 0.5 (50% penalty)
                    double penalty = 0.01 + (recency - 1) * 0.05;
                    penalty = Math.Min(penalty, 0.5);
                    score *= penalty;
                    
                    if (settings.EnableShuffleDebugLog) {
                        Logger.Debug($"[ShuffleScoring] {file.Title}: InMemory penalty applied (pos={recency}, penalty={penalty:F2})");
                    }
                }

                // 4. Add Noise (±10% instead of ±5% for more variety)
                score *= (0.90 + _random.NextDouble() * 0.20);

                candidates.Add((i, score));

                if (settings.EnableShuffleDebugLog) {
                    Logger.Debug($"[ShuffleScoring] {file.Title}: Score={score:F2} (R={m1:F2}, W={m2:F2}, P={m3:F2}, L={m4:F2}, V={m5:F2}, T={m6:F2})");
                }
            }

            // Fallback if no candidates
            if (candidates.Count == 0) {
                return (currentIndex + 1) % files.Length;
            }

            // Pick the best one
            int selectedIndex = candidates.OrderByDescending(c => c.score).First().index;
            string selectedPath = files[selectedIndex]?.FilePath;
            
            // Remember this pick in our in-memory queue
            if (selectedPath != null) {
                _recentPicksQueue.Enqueue(selectedPath);
                while (_recentPicksQueue.Count > MaxRecentPicks) {
                    _recentPicksQueue.Dequeue();
                }
            }
            
            Logger.Info($"[ShuffleScoring] Selected #{selectedIndex} ({files[selectedIndex]?.FileName}) from {candidates.Count} candidates");
            
            return selectedIndex;
        }
        
        /// <summary>
        /// Clears in-memory recent picks. Call when playlist changes.
        /// </summary>
        public void ClearRecentPicks() {
            _recentPicksQueue.Clear();
        }
    }
}
