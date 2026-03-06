using System;
using System.Collections.Generic;
using System.Linq;
using GOON.Classes;

namespace GOON.Classes.Metrics {
    public static class PlayCountMetric {
        public static double Calculate(string filePath, IEnumerable<string> playlistPaths, PlayHistoryService historyService) {
            int thisCount = historyService.GetTotalPlayCount(filePath);
            
            // Get all counts in playlist to calculate average
            var counts = playlistPaths.Select(p => historyService.GetTotalPlayCount(p)).ToList();
            if (counts.Count == 0) return 1.0;
            
            double average = counts.Average();
            
            // If this video is played less than average, give it a higher score
            // If played more, lower score.
            if (thisCount <= average) {
                // Return score between 0.5 and 1.0
                return 0.5 + (average - thisCount) / (Math.Max(1, average) * 2);
            } else {
                // Return score between 0.1 and 0.5
                return Math.Max(0.1, 0.5 - (thisCount - average) / (Math.Max(1, average) * 2));
            }
        }
    }
}
