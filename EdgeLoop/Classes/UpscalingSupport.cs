using System.Collections.Generic;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// EdgeLoop's upscaling, done by its own shaders (FSRCNNX) on any graphics card, on every screen at once.
    /// The graphics drivers' upscalers (NVIDIA RTX, Intel) aren't used: they upscale one video at a time, need
    /// the driver's own setting, and can't be timed.
    /// </summary>
    public static class UpscalingSupport
    {
        /// <summary>The Settings description of upscaling.</summary>
        public const string Description = "Sharper video when it's smaller than the screen, using the FSRCNNX shader on your graphics card. Works on every screen at once.";

        /// <summary>The upscaling choices in Settings: the tallest video to upscale, 0 for any, -1 for off.</summary>
        public static readonly IReadOnlyList<(string Label, int MaxHeight)> Choices = new[]
        {
            ("Off", -1),
            ("Videos up to 720p", 720),
            ("Videos up to 1080p", 1080),
            ("All videos", 0)
        };

        /// <summary>A player's upscaling from the user's settings.</summary>
        public static void Apply(FlyleafLib.Config.VideoConfig video, UserSettings settings)
        {
            video.SuperResolution = false; // Never the driver's upscaler
            video.ShaderUpscaling = settings?.EnableSuperResolution ?? false;
            video.ShaderUpscalingMaxHeight = settings?.SuperResolutionMaxHeight ?? 0;
        }

        /// <summary>
        /// The playback overlay's upscaling line, e.g. "✦ FSRCNNX to 2560x1440 · 0.90 ms per frame".
        /// Null when nothing upscales and nothing is waiting to.
        /// </summary>
        /// <param name="running">FSRCNNX is upscaling.</param>
        /// <param name="upscaledSize">The size it upscales to before the picture is fitted to the screen.</param>
        /// <param name="ms">GPU time per frame, 0 until measured.</param>
        /// <param name="idleReason">Why it's switched on but not running.</param>
        public static string Status(bool running, string upscaledSize, double ms, string idleReason)
        {
            if (running)
                return $"✦ FSRCNNX{(upscaledSize != null ? " to " + upscaledSize : "")}{(ms > 0 ? $" · {ms:F2} ms per frame" : "")}";
            if (idleReason != null)
                return $"FSRCNNX not running: {idleReason}";
            return null;
        }
    }
}
