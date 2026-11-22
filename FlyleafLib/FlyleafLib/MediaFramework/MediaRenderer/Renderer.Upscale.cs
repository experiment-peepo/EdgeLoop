namespace FlyleafLib.MediaFramework.MediaRenderer;

/// <summary>Fork: the shader upscaling's timing and reported state.</summary>
public partial class Renderer
{
    /// <summary>Fork: GPU time the upscaling shaders take per frame in milliseconds, averaged over 60 frames. Zero until measured or when not running.</summary>
    public double UpscalingMs { get; private set; }

    /// <summary>Fork: the chain of effects running, in words (e.g. "FSRCNNX 2560x1440 → shrink 1920x1080"). Null when none runs.</summary>
    public string UpscalingSteps { get; private set; }

    GpuTimer upTimer;

    int UpscaleTimingBegin()
    {
        try
        {
            upTimer ??= new GpuTimer(device);
            if (upTimer.Collect(context) is double ms)
            {
                SetUpscalingMs(Math.Round(ms, 2));
                FLUpscaleCheckBudget(ms);
            }
            return upTimer.Begin(context);
        }
        catch (Exception e)
        {
            Log.Warn($"[Upscale] GPU timing unavailable ({e.Message})");
            return -1;
        }
    }

    void UpscaleTimingEnd(int index) => upTimer?.End(context, index);

    void SetUpscalingMs(double ms)
    {
        if (UpscalingMs == ms)
            return;

        UpscalingMs = ms;
        RaiseUI(nameof(UpscalingMs));
    }

    /// <summary>Upscaling stopped or changed: forget the measured time.</summary>
    void ResetUpscalingMs()
    {
        upTimer?.Reset();
        SetUpscalingMs(0);
    }

    void SetUpscalingSteps(string steps)
    {
        if (UpscalingSteps == steps)
            return;

        UpscalingSteps = steps;
        RaiseUI(nameof(UpscalingSteps));
        ResetUpscalingMs(); // A different amount of work
    }

    /// <summary>The processor changed: the Flyleaf processor's upscaling no longer runs.</summary>
    void UpscaleProcessorChanged(VideoProcessors oldProcessor)
    {
        if (oldProcessor == VideoProcessors.Flyleaf)
            FLUpscaleStopped();
    }

    void UpscaleDispose()
    {
        upTimer?.Dispose();
        upTimer = null;
    }
}
