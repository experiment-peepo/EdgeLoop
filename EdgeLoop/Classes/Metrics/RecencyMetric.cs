using System;
using System.Collections.Generic;
using System.Linq;
using EdgeLoop.Classes;

namespace EdgeLoop.Classes.Metrics {
    public static class RecencyMetric {
        public static double Calculate(string filePath, PlayHistoryService historyService) {
            var lastPlay = historyService.GetLastPlay(filePath);
            if (lastPlay == null) return 1.0; // Never played, maximum freshness

            var hoursSince = (DateTime.Now - lastPlay.PlayedAt).TotalHours;
            
            // Logarithmic decay for freshness
            if (hoursSince < 1) return 0.05; // Played very recently
            if (hoursSince < 24) return 0.1 + (Math.Log(hoursSince) / Math.Log(24)) * 0.3; // 24 hours: up to 0.4
            if (hoursSince < 168) return 0.4 + (Math.Log(hoursSince / 24) / Math.Log(7)) * 0.4; // 1 week: up to 0.8
            
            return Math.Min(1.0, 0.8 + (hoursSince / 720) * 0.2); // 1 month+: up to 1.0
        }
    }
}

