using System;
using System.Collections.Generic;
using System.Linq;
using EdgeLoop.Classes.Metrics;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Classes {
    public class ShuffleScoringEngine {
        private readonly PlayHistoryService _historyService;
        private readonly Random _random = new Random();
        
        // Track recently picked videos IN MEMORY (survives between picks in a session)
        // This is critical for fresh sessions with no history file
        private readonly Queue<string> _recentPicksQueue = new Queue<string>();
        
        // Dynamic max: scaled per playlist in SelectBestVideo
        private int _currentMaxRecentPicks = 10;

        public ShuffleScoringEngine(PlayHistoryService historyService) {
            _historyService = historyService;
        }

        public int SelectBestVideo(VideoItem[] files, string playlistHash, UserSettings settings, int currentIndex = -1) {
            if (files == null || files.Length == 0) return 0;
            if (files.Length == 1) return 0;

            // --- PRE-COMPUTE SHARED DATA (avoids O(N²) per-file lookups) ---
            
            // Scale recent-picks queue proportionally to playlist size
            _currentMaxRecentPicks = Math.Clamp(files.Length / 3, 5, 30);
            while (_recentPicksQueue.Count > _currentMaxRecentPicks) {
                _recentPicksQueue.Dequeue();
            }

            var recentRecords = _historyService.GetRecentRecords(10);

            // Pre-fetch all history records once for TimeOfDay metric
            var allRecords = _historyService.GetRecentRecords(5000);
            var recordsByPath = allRecords.ToLookup(r => r.FilePath);

            // Pre-compute play counts for PlayCount metric (avoid O(N²))
            var playCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) {
                if (file == null) continue;
                if (!playCounts.ContainsKey(file.FilePath)) {
                    playCounts[file.FilePath] = _historyService.GetTotalPlayCount(file.FilePath);
                }
            }
            double playCountAverage = playCounts.Count > 0 ? playCounts.Values.Average() : 0;

            // Pre-build in-memory recent picks data
            var recentPicks = _recentPicksQueue.ToHashSet();
            var recentPicksList = _recentPicksQueue.ToList(); // For position-based penalty

            var candidates = new List<(int index, double score)>();

            for (int i = 0; i < files.Length; i++) {
                var file = files[i];
                if (file == null) continue;
                
                // CRITICAL: Skip the currently playing video
                if (i == currentIndex) continue;

                // 1. Calculate Individual Scores using pre-fetched data where possible
                double m1 = RecencyMetric.Calculate(file.FilePath, _historyService);
                double m2 = WatchTimeMetric.Calculate(file.FilePath, _historyService);
                double m3 = PlayCountMetric.Calculate(
                    playCounts.GetValueOrDefault(file.FilePath, 0), 
                    playCountAverage
                );
                
                double durationMs = 0;
                var lastPlay = _historyService.GetLastPlay(file.FilePath);
                if (lastPlay != null) {
                    durationMs = lastPlay.VideoDurationMs;
                }
                
                double m4 = LengthVarietyMetric.Calculate(durationMs, recentRecords);
                double m5 = CreatorVarietyMetric.Calculate(file.FilePath, recentRecords);
                double m6 = TimeOfDayMetric.Calculate(file.FilePath, recordsByPath);

                // 2. Combine with Weights
                double score = 
                    (m1 * settings.ShuffleRecencyWeight) + 
                    (m2 * settings.ShufflePreferenceWeight * 0.5) +
                    (m3 * settings.ShufflePreferenceWeight * 0.3) +
                    (m6 * settings.ShufflePreferenceWeight * 0.2) +
                    (m4 * settings.ShuffleLengthWeight) + 
                    (m5 * settings.ShuffleVarietyWeight);

                // 3. "Never Played" discovery boost — surface new additions more aggressively
                bool neverPlayed = _historyService.GetTotalPlayCount(file.FilePath) == 0;
                if (neverPlayed && !recentPicks.Contains(file.FilePath)) {
                    score *= 1.4; // 40% boost for undiscovered videos
                }

                // 4. Skip-rate penalty — demote videos that are consistently skipped
                double skipRate = _historyService.GetSkipRate(file.FilePath);
                if (skipRate > 0.6 && _historyService.GetTotalPlayCount(file.FilePath) >= 3) {
                    // Only apply if we have enough data (3+ plays) and skip rate is high
                    double skipPenalty = 1.0 - ((skipRate - 0.6) * 0.75); // 60% skip = 1.0x, 100% skip = 0.7x
                    score *= Math.Max(0.5, skipPenalty);
                }

                // 5. CRITICAL IN-MEMORY PENALTY: Apply harsh penalty for recently picked videos
                // This works even in fresh sessions with no history file
                if (recentPicks.Contains(file.FilePath)) {
                    int positionInQueue = recentPicksList.LastIndexOf(file.FilePath);
                    int recency = recentPicksList.Count - positionInQueue; // 1 = most recent, N = oldest
                    
                    // Most recent = 0.01 (99% penalty), oldest = 0.5 (50% penalty)
                    double penalty = 0.01 + (recency - 1) * 0.05;
                    penalty = Math.Min(penalty, 0.5);
                    score *= penalty;
                    
                    if (settings.EnableShuffleDebugLog) {
                        Logger.Debug($"[ShuffleScoring] {file.Title}: InMemory penalty applied (pos={recency}, penalty={penalty:F2})");
                    }
                }

                // Ensure score is positive for weighted selection
                score = Math.Max(score, 0.001);

                candidates.Add((i, score));

                if (settings.EnableShuffleDebugLog) {
                    string extras = neverPlayed ? " [NEW]" : "";
                    if (skipRate > 0.6) extras += $" [SKIP:{skipRate:P0}]";
                    Logger.Debug($"[ShuffleScoring] {file.Title}: Score={score:F3} (R={m1:F2}, W={m2:F2}, P={m3:F2}, L={m4:F2}, V={m5:F2}, T={m6:F2}){extras}");
                }
            }

            // Fallback if no candidates
            if (candidates.Count == 0) {
                return (currentIndex + 1) % files.Length;
            }

            // --- WEIGHTED RANDOM SELECTION (softmax-style) ---
            // Instead of always picking the top scorer, use scores as probabilities.
            // Higher scores are proportionally more likely, but not guaranteed.
            // This eliminates the determinism problem where ±10% noise was the only variety.
            int selectedIndex = WeightedRandomSelect(candidates);

            string selectedPath = files[selectedIndex]?.FilePath;
            
            // Remember this pick in our in-memory queue
            if (selectedPath != null) {
                _recentPicksQueue.Enqueue(selectedPath);
                while (_recentPicksQueue.Count > _currentMaxRecentPicks) {
                    _recentPicksQueue.Dequeue();
                }
            }

            if (settings.EnableShuffleDebugLog) {
                double totalScore = candidates.Sum(c => c.score);
                double selectedScore = candidates.First(c => c.index == selectedIndex).score;
                double probability = totalScore > 0 ? selectedScore / totalScore * 100 : 0;
                Logger.Info($"[ShuffleScoring] Selected #{selectedIndex} ({files[selectedIndex]?.FileName}) " +
                           $"from {candidates.Count} candidates (score={selectedScore:F3}, prob={probability:F1}%)");
            } else {
                Logger.Info($"[ShuffleScoring] Selected #{selectedIndex} ({files[selectedIndex]?.FileName}) from {candidates.Count} candidates");
            }
            
            return selectedIndex;
        }

        /// <summary>
        /// Selects a candidate using weighted random selection (roulette wheel).
        /// Higher-scored candidates are proportionally more likely to be selected,
        /// but every candidate has a chance — producing genuine variety.
        /// </summary>
        private int WeightedRandomSelect(List<(int index, double score)> candidates) {
            double totalScore = candidates.Sum(c => c.score);
            
            if (totalScore <= 0) {
                // Fallback to uniform random if all scores are zero
                return candidates[_random.Next(candidates.Count)].index;
            }

            double roll = _random.NextDouble() * totalScore;
            double cumulative = 0;

            for (int i = 0; i < candidates.Count; i++) {
                cumulative += candidates[i].score;
                if (roll <= cumulative) {
                    return candidates[i].index;
                }
            }

            // Fallback (shouldn't happen due to floating point, but be safe)
            return candidates[candidates.Count - 1].index;
        }
        
        /// <summary>
        /// Clears in-memory recent picks. Call when playlist changes.
        /// </summary>
        public void ClearRecentPicks() {
            _recentPicksQueue.Clear();
        }
    }
}

