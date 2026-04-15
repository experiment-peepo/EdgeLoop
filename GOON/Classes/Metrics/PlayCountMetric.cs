using System;
using System.Collections.Generic;
using System.Linq;
using GOON.Classes;

namespace GOON.Classes.Metrics {
    public static class PlayCountMetric {
        /// <summary>
        /// Optimized overload that accepts pre-computed play counts and average.
        /// Avoids the O(N²) cost of querying every playlist path's count for each file.
        /// </summary>
        public static double Calculate(int thisCount, double playlistAverage) {
            if (playlistAverage <= 0 && thisCount == 0) return 1.0; // All unplayed

            // If this video is played less than average, give it a higher score
            // If played more, lower score.
            if (thisCount <= playlistAverage) {
                // Return score between 0.5 and 1.0
                return 0.5 + (playlistAverage - thisCount) / (Math.Max(1, playlistAverage) * 2);
            } else {
                // Return score between 0.1 and 0.5
                return Math.Max(0.1, 0.5 - (thisCount - playlistAverage) / (Math.Max(1, playlistAverage) * 2));
            }
        }

        /// <summary>
        /// Legacy overload for backward compatibility. Prefer the pre-computed overload.
        /// </summary>
        public static double Calculate(string filePath, IEnumerable<string> playlistPaths, PlayHistoryService historyService) {
            int thisCount = historyService.GetTotalPlayCount(filePath);
            
            // Get all counts in playlist to calculate average
            var counts = playlistPaths.Select(p => historyService.GetTotalPlayCount(p)).ToList();
            if (counts.Count == 0) return 1.0;
            
            double average = counts.Average();
            return Calculate(thisCount, average);
        }
    }
}
