using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GOON.Classes;

namespace GOON.Classes.Metrics {
    public static class CreatorVarietyMetric {
        public static string GetCreator(string filePath) {
            try {
                if (string.IsNullOrEmpty(filePath)) return "Unknown";
                return Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "Unknown";
            } catch {
                return "Unknown";
            }
        }

        public static double Calculate(string filePath, List<VideoPlayRecord> recentRecords) {
            if (recentRecords == null || recentRecords.Count == 0) return 0.5;

            var thisCreator = GetCreator(filePath);
            
            // Check last 3 records
            var last3 = recentRecords.Skip(Math.Max(0, recentRecords.Count - 3)).ToList();
            
            int matchIndex = -1;
            for (int i = 0; i < last3.Count; i++) {
                if (GetCreator(last3[last3.Count - 1 - i].FilePath) == thisCreator) {
                    matchIndex = i; // 0 = last, 1 = second to last, etc.
                    break;
                }
            }

            if (matchIndex == -1) return 1.0; // Fresh creator
            if (matchIndex == 0) return 0.1;  // Same as previous
            if (matchIndex == 1) return 0.4;  // Same as 2nd previous
            return 0.7;                      // Same as 3rd previous
        }
    }
}
