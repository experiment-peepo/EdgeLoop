using System;
using System.Collections.Generic;
using System.Linq;
using FlyleafLib.MediaFramework.MediaRenderer.Effects;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// EdgeLoop's upscaling, done by shaders on any graphics card and on every screen at once: FSRCNNX (Sharp,
    /// Line art) or FSR 1 (Fast), fitted to the screen, with optional sharpening and debanding. The graphics
    /// drivers' upscalers (NVIDIA RTX, Intel) aren't used: they upscale one video at a time, need the driver's
    /// own setting, and can't be timed.
    /// </summary>
    public static class UpscalingSupport
    {
        /// <summary>The Settings description of upscaling.</summary>
        public const string Description = "Sharper video when it's smaller than the screen, on every screen at once, with sharper colour, light sharpening and smoother gradients. Fast suits slower graphics cards; Line art suits anime and drawings.";

        /// <summary>Upscaling in Settings: off, or how (stored in <see cref="UserSettings.UpscalingQuality"/>).</summary>
        public static readonly IReadOnlyList<(string Label, string Mode)> Modes = new[]
        {
            ("Off", Off),
            ("Fast (FSR 1)", nameof(ShaderUpscaler.Fast)),
            ("Sharp (FSRCNNX)", nameof(ShaderUpscaler.Sharp)),
            ("Line art (FSRCNNX)", nameof(ShaderUpscaler.LineArt))
        };

        public const string Off = "Off";

        /// <summary>
        /// What each mode does besides upscaling: FSR 1's sharpening at this strength (0 to 1), and debanding.
        /// Fast is FSR 1 as AMD pairs it (enlarging, then sharpening); Sharp is FSRCNNX with strong sharpening;
        /// Line art only lightly sharpens, as drawn lines are already crisp and would ring.
        /// </summary>
        public static (float Sharpening, bool Deband) Preset(ShaderUpscaler upscaler) => upscaler switch
        {
            ShaderUpscaler.Fast => (0.6f, true),
            ShaderUpscaler.LineArt => (0.35f, true),
            _ => (0.87f, true)
        };

        /// <summary>A player's upscaling from the user's settings: any video smaller than its screen, with the mode's preset.</summary>
        public static void Apply(FlyleafLib.Config.VideoConfig video, UserSettings settings)
        {
            bool on = settings?.EnableSuperResolution ?? false;
            var upscaler = ParseUpscaler(settings?.UpscalingQuality);
            var (sharpening, deband) = Preset(upscaler);

            video.SuperResolution = false; // Never the driver's upscaler
            video.ShaderUpscaling = on;
            video.ShaderUpscalingMaxHeight = 0;
            video.ShaderUpscaler = upscaler;
            video.ShaderSharpening = on ? sharpening : 0;
            video.ShaderDeband = on && deband;
            video.ShaderChromaFromLuma = on; // Every mode: it costs well under 0.1 ms a frame at 1080p
        }

        /// <summary>The saved quality as an upscaler; anything unknown is Sharp.</summary>
        public static ShaderUpscaler ParseUpscaler(string quality) =>
            Enum.TryParse<ShaderUpscaler>(quality, out var upscaler) && Enum.IsDefined(upscaler) ? upscaler : ShaderUpscaler.Sharp;

        /// <summary>
        /// The playback overlay's upscaling line (Diagnostic mode), e.g. "✦ FSRCNNX 2560x1440 → shrink 1920x1080 · 1.30 ms per frame".
        /// Null when nothing runs and nothing is waiting to.
        /// </summary>
        /// <param name="running">The effects are running; <paramref name="steps"/> says which.</param>
        /// <param name="ms">GPU time per frame, 0 until measured.</param>
        /// <param name="idleReason">Why upscaling is switched on but not running.</param>
        public static string Status(bool running, string steps, double ms, string idleReason)
        {
            if (running)
                return $"✦ {steps}{(ms > 0 ? $" · {ms:F2} ms per frame" : "")}";
            if (idleReason != null)
                return $"Upscaling not running: {idleReason}";
            return null;
        }

        /// <summary>The notice when a screen stepped upscaling down because the graphics card couldn't keep up.</summary>
        /// <param name="level">What it stepped down to: "Fast (FSR 1)" or "Off".</param>
        public static (string Headline, string Detail) LoweredNotice(string level) => level == Off
            ? ("Upscaling switched off on this screen", "The graphics card couldn't keep up, even with Fast (FSR 1). It's back when playback starts again.")
            : ($"Upscaling lowered to {level} on this screen", "The graphics card couldn't keep up. Your choice is back when playback starts again.");

        /// <summary>Whether <paramref name="mode"/> is one of <see cref="Modes"/>.</summary>
        public static bool IsMode(string mode) => Modes.Any(m => m.Mode == mode);
    }
}
