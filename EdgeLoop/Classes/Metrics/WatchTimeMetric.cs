using System;
using System.Linq;
using EdgeLoop.Classes;

namespace EdgeLoop.Classes.Metrics {
    public static class WatchTimeMetric {
        public static double Calculate(string filePath, PlayHistoryService historyService) {
            var skipRate = historyService.GetSkipRate(filePath);
            var watchPercent = historyService.GetAverageWatchPercent(filePath);
            
            // If no history, return neutral
            if (historyService.GetTotalPlayCount(filePath) == 0) return 0.5;

            // Score increases with watch percentage and decreases with skip rate
            // 0.0 = hated (always skipped), 1.0 = loved (always watched to end)
            double score = watchPercent * (1.0 - skipRate);
            
            return Math.Clamp(score, 0.05, 1.0);
        }
    }
}

