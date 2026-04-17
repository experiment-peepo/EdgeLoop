using System;

namespace EdgeLoop.Classes {
    /// <summary>
    /// Represents a single playback event for a video.
    /// Used for smart shuffle scoring and analytics.
    /// </summary>
    public class VideoPlayRecord {
        /// <summary>
        /// Unique identifier for the video (usually local file path or URL)
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Timestamp when the video started playing
        /// </summary>
        public DateTime PlayedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// How many milliseconds the user watched before skipping or finishing
        /// </summary>
        public long WatchDurationMs { get; set; }

        /// <summary>
        /// Total duration of the video in milliseconds
        /// </summary>
        public long VideoDurationMs { get; set; }

        /// <summary>
        /// True if the user manually skipped the video
        /// </summary>
        public bool WasSkipped { get; set; }

        /// <summary>
        /// True if the video played to completion (e.g., > 90%)
        /// </summary>
        public bool WasCompleted { get; set; }

        /// <summary>
        /// Identifier for the session in which this play occurred
        /// </summary>
        public Guid SessionId { get; set; }

        /// <summary>
        /// Hash identifier for the playlist this video belonged to
        /// </summary>
        public string PlaylistHash { get; set; }
        
        /// <summary>
        /// Helper to get watch percentage (0-1)
        /// </summary>
        public double WatchPercentage => VideoDurationMs > 0 ? (double)WatchDurationMs / VideoDurationMs : 0;
    }
}

