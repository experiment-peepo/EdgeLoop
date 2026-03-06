using System;
using System.Linq;
using GOON.Classes;

namespace GOON.Classes.Metrics {
    public static class TimeOfDayMetric {
        public enum TimeBucket { Morning, Afternoon, Evening, Night }

        public static TimeBucket GetBucket(int hour) {
            if (hour >= 6 && hour < 12) return TimeBucket.Morning;
            if (hour >= 12 && hour < 18) return TimeBucket.Afternoon;
            if (hour >= 18 && hour < 22) return TimeBucket.Evening;
            return TimeBucket.Night;
        }

        public static double Calculate(string filePath, PlayHistoryService historyService) {
            var allRecords = historyService.GetRecentRecords(5000); // Look at last 5000 plays
            var fileRecords = allRecords.Where(r => r.FilePath == filePath).ToList();
            
            if (fileRecords.Count == 0) return 0.5;

            var currentBucket = GetBucket(DateTime.Now.Hour);
            
            int bucketPlays = fileRecords.Count(r => GetBucket(r.PlayedAt.Hour) == currentBucket);
            
            // Percentage of this video's plays that happened in this time bucket
            double ratio = (double)bucketPlays / fileRecords.Count;
            
            // If user often watches this video now, boost it (up to 1.0)
            // If they never watch it now, keep neutral (0.5)
            return 0.5 + (ratio * 0.5);
        }
    }
}
