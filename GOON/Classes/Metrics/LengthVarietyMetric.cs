using System;
using System.Collections.Generic;
using System.Linq;
using GOON.Classes;

namespace GOON.Classes.Metrics {
    public static class LengthVarietyMetric {
        public enum LengthCategory { Short, Medium, Long }

        public static LengthCategory GetCategory(double durationMs) {
            double seconds = durationMs / 1000.0;
            if (seconds < 60) return LengthCategory.Short;
            if (seconds < 300) return LengthCategory.Medium;
            return LengthCategory.Long;
        }

        public static double Calculate(double durationMs, List<VideoPlayRecord> recentRecords) {
            if (recentRecords == null || recentRecords.Count == 0) return 0.5;

            var thisCategory = GetCategory(durationMs);
            
            // Count occurrences of each category in recent history (last 5)
            var last5 = recentRecords.Skip(Math.Max(0, recentRecords.Count - 5)).ToList();
            int sameCategoryCount = last5.Count(r => GetCategory(r.VideoDurationMs) == thisCategory);

            // Penalize more if we've played many of the same category recently
            // 0 same = 1.0 score, all same = 0.2 score
            double score = 1.0 - (sameCategoryCount / 5.0) * 0.8;
            
            return Math.Clamp(score, 0.2, 1.0);
        }
    }
}
