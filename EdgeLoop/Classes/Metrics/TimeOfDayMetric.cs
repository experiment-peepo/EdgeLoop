using System;
using System.Collections.Generic;
using System.Linq;
using EdgeLoop.Classes;

namespace EdgeLoop.Classes.Metrics {
    public static class TimeOfDayMetric {
        public enum TimeBucket { Morning, Afternoon, Evening, Night }

        public static TimeBucket GetBucket(int hour) {
            if (hour >= 6 && hour < 12) return TimeBucket.Morning;
            if (hour >= 12 && hour < 18) return TimeBucket.Afternoon;
            if (hour >= 18 && hour < 22) return TimeBucket.Evening;
            return TimeBucket.Night;
        }

        /// <summary>
        /// Optimized overload that accepts pre-fetched records grouped by file path.
        /// Avoids the expensive GetRecentRecords(5000) call for every single file.
        /// </summary>
        public static double Calculate(string filePath, ILookup<string, VideoPlayRecord> recordsByPath) {
            var fileRecords = recordsByPath[filePath];
            
            int totalCount = fileRecords.Count();
            if (totalCount == 0) return 0.5;

            var currentBucket = GetBucket(DateTime.Now.Hour);
            
            int bucketPlays = fileRecords.Count(r => GetBucket(r.PlayedAt.Hour) == currentBucket);
            
            // Percentage of this video's plays that happened in this time bucket
            double ratio = (double)bucketPlays / totalCount;
            
            // If user often watches this video now, boost it (up to 1.0)
            // If they never watch it now, keep neutral (0.5)
            return 0.5 + (ratio * 0.5);
        }

        /// <summary>
        /// Legacy overload for backward compatibility. Prefer the pre-fetched overload.
        /// </summary>
        public static double Calculate(string filePath, PlayHistoryService historyService) {
            var allRecords = historyService.GetRecentRecords(5000);
            var lookup = allRecords.ToLookup(r => r.FilePath);
            return Calculate(filePath, lookup);
        }
    }
}

