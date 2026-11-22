using System.Linq;

namespace FlyleafLib.MediaFramework.MediaRenderer.Effects;

/// <summary>Fork: how shader upscaling enlarges video.</summary>
public enum ShaderUpscaler
{
    /// <summary>FSR 1 (EASU) straight to the window's size: one light pass, for slower graphics cards.</summary>
    Fast,
    /// <summary>FSRCNNX doubles the picture, then it's fitted to the window.</summary>
    Sharp,
    /// <summary>FSRCNNX trained on line art (anime, cartoons, drawings), then fitted to the window.</summary>
    LineArt
}

/// <summary>One effect in the chain: what it runs on and the size it produces.</summary>
internal readonly record struct UpscaleStep(string Effect, uint InputWidth, uint InputHeight, uint OutputWidth, uint OutputHeight);

/// <summary>
/// Fork: decides the chain of effects for a video of one size shown at another:
/// <list type="number">
/// <item>deband (optional), at the video's own size;</item>
/// <item>FSRCNNX doubling (Sharp, Line art): once when the picture needs real enlarging, again only when that
/// won't overshoot (480p on a 4K screen is doubled twice, 720p on any screen once);</item>
/// <item>a fit to the window: FSR 1 (EASU) when it still needs enlarging, SSimDownscaler when it overshot;</item>
/// <item>sharpening (optional) at the window's size.</item>
/// </list>
/// </summary>
internal static class UpscalePlanner
{
    /// <summary>The least enlargement worth doubling for; below it FSR 1 enlarges directly (cheaper, nearly the same).</summary>
    public const double DoubleFrom = 1.2;
    public const int MaxDoublings = 2;

    /// <param name="upscale">Enlarge to the target (false: only deband, at the video's size).</param>
    public static List<UpscaleStep> Plan(uint width, uint height, uint targetWidth, uint targetHeight,
        bool upscale, ShaderUpscaler upscaler, bool deband, bool sharpen)
    {
        var steps = new List<UpscaleStep>();
        if (width == 0 || height == 0)
            return steps;

        if (deband)
            steps.Add(new(MagpieEffects.Deband, width, height, width, height));

        if (!upscale || targetWidth == 0 || targetHeight == 0 || (targetWidth <= width && targetHeight <= height))
            return steps;

        if (upscaler != ShaderUpscaler.Fast)
        {
            string doubler = upscaler == ShaderUpscaler.LineArt ? MagpieEffects.FSRCNNXLineArt : MagpieEffects.FSRCNNX;
            for (int i = 0; i < MaxDoublings; i++)
            {
                double needed = Math.Max(targetWidth / (double)width, targetHeight / (double)height);
                if (needed < (i == 0 ? DoubleFrom : 2))
                    break;

                steps.Add(new(doubler, width, height, width * 2, height * 2));
                width *= 2;
                height *= 2;
            }
        }

        if (width != targetWidth || height != targetHeight)
        {
            bool shrink = width >= targetWidth && height >= targetHeight;
            steps.Add(new(shrink ? MagpieEffects.SSimDownscaler : MagpieEffects.Easu, width, height, targetWidth, targetHeight));
        }

        if (sharpen)
            steps.Add(new(MagpieEffects.Rcas, targetWidth, targetHeight, targetWidth, targetHeight));

        return steps;
    }

    /// <summary>The chain in words, e.g. "FSRCNNX 2560x1440 → shrink 1920x1080 → sharpen".</summary>
    public static string Describe(IReadOnlyList<UpscaleStep> steps) => string.Join(" → ", steps.Select(step => step.Effect switch
    {
        MagpieEffects.Deband => "deband",
        MagpieEffects.FSRCNNX => $"FSRCNNX {step.OutputWidth}x{step.OutputHeight}",
        MagpieEffects.FSRCNNXLineArt => $"FSRCNNX line art {step.OutputWidth}x{step.OutputHeight}",
        MagpieEffects.Easu => $"FSR 1 {step.OutputWidth}x{step.OutputHeight}",
        MagpieEffects.SSimDownscaler => $"shrink {step.OutputWidth}x{step.OutputHeight}",
        MagpieEffects.Rcas => "sharpen",
        _ => step.Effect
    }));
}

/// <summary>Fork: when shader upscaling costs too much of each frame's time, and the renderer should step down a level.</summary>
internal static class UpscaleBudget
{
    /// <summary>The most of a frame's time the upscaling may take.</summary>
    public const double Share = 0.5;

    /// <summary>Over-budget measurements in a row (each averages 60 frames, about a second) before stepping down.</summary>
    public const int Strikes = 3;

    public static bool IsOver(double ms, double fps) => ms > 1000 / (fps > 1 ? fps : 60) * Share;
}
