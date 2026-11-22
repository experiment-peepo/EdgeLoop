using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace FlyleafLib.MediaFramework.MediaRenderer;

/// <summary>
/// Fork: shader upscaling and debanding on the Flyleaf processor. The frame is drawn at its own size (with sharper
/// colour from <see cref="ChromaFromLuma"/> for 4:2:0 hardware frames), run through the chain
/// <see cref="UpscalePlanner"/> picks (deband, FSRCNNX, FSR 1, SSimDownscaler, sharpening), then drawn into the
/// viewport with dithering. With HDR output the chain works on a PQ copy of the picture (<see cref="HdrPicture"/>),
/// turned back into scRGB as it's drawn. Only for unrotated video; anything else draws the usual way. When the
/// chain takes more than half of each frame's time, it steps down: to FSR 1, then to no upscaling.
/// </summary>
public partial class Renderer
{
    /// <summary>Fork: true while frames go through the upscaling (or deband) shaders.</summary>
    public bool ShaderUpscaling { get; private set; }

    /// <summary>Fork: why shader upscaling isn't running although it's switched on, e.g. "the video isn't enlarged". Null otherwise.</summary>
    public string ShaderUpscalingIdleReason { get; private set; }

    /// <summary>Fork: what this renderer stepped upscaling down to because it took too long ("Fast (FSR 1)" or "Off"). Null if it hasn't.</summary>
    public string ShaderUpscalingLowered { get; private set; }

    /// <summary>The largest picture the chain draws into.</summary>
    const uint MaxUpscaleSize = 8192;

    GpuTexture      upInput;    // The frame at its own size
    readonly List<(UpscaleStep Step, MagpieEffectRunner Runner)> upChain = [];
    string          upChainKey;
    bool            upChainUpscales;
    bool            upFailed;
    int             upLevel;    // 0 as configured, 1 stepped down to FSR 1, 2 stepped down to no upscaling
    (bool On, ShaderUpscaler Upscaler) upLevelFor;
    int             upSlowStrikes;
    ChromaFromLuma  upChroma;
    bool            upChromaFailed;
    HdrPicture      upHdr;      // With HDR output: the PQ picture the effects work on
    bool            upHdrFailed;

    /// <summary>Why no effect can run on this frame (upscaling or deband), or null.</summary>
    string FLEffectsBlockedReason()
    {
        if (upFailed)
            return "it failed to start";
        if (hdrSwapchain && upHdrFailed)
            return "HDR output";
        if (rotation != 0)
            return "rotated video";
        if (Pano360._enabled)
            return "360° video";
        if (splitFrameAlphaPosition != SplitFrameAlphaPosition.None)
            return "video with transparency";
        if (FeatureLevel < FeatureLevel.Level_11_0)
            return "the graphics card is too old";
        if (VisibleWidth < 16 || VisibleHeight < 16)
            return "the video is too small";
        return null;
    }

    /// <summary>Why this frame isn't upscaled (upscaling is on), or null.</summary>
    string FLUpscaleSkipReason(uint targetWidth, uint targetHeight)
    {
        if (upLevel >= 2)
            return "it was switched off on this screen, as the graphics card couldn't keep up";

        int maxHeight = ucfg._ShaderUpscalingMaxHeight;
        if (maxHeight > 0 && VisibleHeight > maxHeight)
            return $"the video is taller than {maxHeight}p";

        if (targetWidth <= VisibleWidth && targetHeight <= VisibleHeight)
            return $"the video isn't enlarged ({VisibleWidth}x{VisibleHeight} shown at {targetWidth}x{targetHeight})";

        if (targetWidth > MaxUpscaleSize || targetHeight > MaxUpscaleSize)
            return "the picture is zoomed in too far";

        return null;
    }

    /// <summary>Draws the bound frame into <paramref name="target"/> through the effects. False if it didn't (draw it the usual way).</summary>
    bool FLUpscaleDraw(VideoFrame frame, ID3D11RenderTargetView target)
    {
        bool upscaleOn = ucfg._ShaderUpscaling, debandOn = ucfg._ShaderDeband;
        if (!upscaleOn && !debandOn)
        {
            FLUpscaleStopped();
            return false;
        }

        // A changed setting gets a fresh chance before any stepping down
        if (upLevelFor != (upscaleOn, ucfg._ShaderUpscaler))
        {
            upLevelFor = (upscaleOn, ucfg._ShaderUpscaler);
            upLevel = 0;
            upSlowStrikes = 0;
            SetShaderUpscalingLowered(null);
        }

        uint targetWidth = (uint)Math.Max(0, Math.Round(Viewport.Width)), targetHeight = (uint)Math.Max(0, Math.Round(Viewport.Height));
        string blocked = FLEffectsBlockedReason();
        string skip = blocked ?? (upscaleOn ? FLUpscaleSkipReason(targetWidth, targetHeight) : null);
        bool upscale = upscaleOn && skip == null;
        var upscaler = upLevel >= 1 ? ShaderUpscaler.Fast : ucfg._ShaderUpscaler;

        var steps = UpscalePlanner.Plan(VisibleWidth, VisibleHeight, targetWidth, targetHeight,
            upscale, upscaler, debandOn && blocked == null, upscale && ucfg._ShaderSharpening > 0);
        bool hdr = hdrSwapchain;
        if (steps.Count == 0 || !FLUpscaleEnsure(steps) || (hdr && !FLHdrPictureEnsure()))
        {
            SetShaderUpscaling(false, null);
            SetShaderUpscalingIdle(upscaleOn ? skip ?? (upFailed ? "it failed to start" : "starting") : null);
            return false;
        }
        upChainUpscales = upscale;
        bool sharperColour = FLChromaEnsure(frame);

        int query = UpscaleTimingBegin();
        try
        {
            // 0. Colour at brightness resolution, which the pixel shader then samples in place of the half-size plane
            if (sharperColour)
            {
                upChroma.Run(context, frame.SRV[0], frame.SRV[1], ffTextureDesc.Width, ffTextureDesc.Height);
                context.PSSetShaderResource(1, upChroma.Output.SRV);
            }

            // 1. The frame at its own size, with all of the pixel shader's colour work
            context.OMSetRenderTargets(upInput.RTV);
            context.RSSetViewport(new Viewport(0, 0, upInput.Width, upInput.Height));
            context.Draw(6, 0);
            context.OMSetRenderTargets((ID3D11RenderTargetView)null); // so the effects can read it

            // 1b. With HDR output, a PQ copy for the effects (theirs is a 0 to 1 picture, not scRGB's nits)
            var picture = upInput.SRV;
            if (hdr)
            {
                upHdr.ToPQ(context, picture, upInput.Width, upInput.Height);
                picture = upHdr.Output.SRV;
            }

            // 2. Each effect on the last one's picture
            foreach (var (step, runner) in upChain)
            {
                if (step.Effect == MagpieEffects.Rcas)
                    runner.SetParameter("sharpness", ucfg._ShaderSharpening);
                runner.Run(context, picture);
                picture = runner.Output.SRV;
            }

            // 3. Into the viewport (one to one when upscaled): dithered for the 8-bit screen, or back into scRGB for HDR
            context.OMSetRenderTargets(target);
            context.RSSetViewport(Viewport);
            if (hdr)
                upHdr.FromPQ(context, picture);
            else
            {
                context.VSSetShader(vsSimple);
                context.PSSetShader(psShader["rgbaDither"]);
                context.PSSetShaderResource(0, picture);
                context.Draw(6, 0);
            }
        }
        finally
        {
            // What the frame's own drawing expects again (subtitles, snapshots, the next frame)
            UpscaleTimingEnd(query);
            context.RSSetViewport(Viewport);
            context.VSSetShader(vsMain);
            context.PSSetShader(psShader[psId]);
            context.PSSetShaderResources(0, frame.SRV);
        }

        SetShaderUpscaling(true, (sharperColour ? "sharper colour → " : "") + UpscalePlanner.Describe(steps) + (hdr ? " (HDR)" : ""));
        SetShaderUpscalingIdle(null);
        return true;
    }

    /// <summary>Whether this frame gets sharper colour: a 4:2:0 hardware frame (NV12, P010) with it switched on, once the shader is ready.</summary>
    bool FLChromaEnsure(VideoFrame frame)
    {
        if (!ucfg._ShaderChromaFromLuma || upChromaFailed || psCase != PSCase.HW || isDovi || frame.SRV is not { Length: 2 } ||
            ffTextureDesc.Format is not (Format.NV12 or Format.P010 or Format.P016))
            return false;

        if (upChroma != null)
            return true;

        var task = ChromaFromLuma.Compile();
        if (!task.IsCompleted)
            return false; // Still compiling: the usual colour meanwhile

        try
        {
            upChroma = new ChromaFromLuma(device, task.Result);
            return true;
        }
        catch (Exception e)
        {
            upChromaFailed = true;
            Log.Error($"[Upscale] Sharper colour unavailable ({e.GetBaseException().Message})");
            return false;
        }
    }

    /// <summary>With HDR output: whether the PQ conversion is ready. False while it's still compiling, or if it failed.</summary>
    bool FLHdrPictureEnsure()
    {
        if (upHdr != null)
            return true;
        if (upHdrFailed)
            return false;

        var task = HdrPicture.Compile();
        if (!task.IsCompleted)
            return false; // Still compiling: the usual drawing meanwhile

        try
        {
            upHdr = new HdrPicture(device, task.Result);
            return true;
        }
        catch (Exception e)
        {
            upHdrFailed = true;
            Log.Error($"[Upscale] Unavailable with HDR output ({e.GetBaseException().Message})");
            return false;
        }
    }

    /// <summary>Builds the chain for these steps if it isn't already. False while an effect is still compiling, or if it failed.</summary>
    bool FLUpscaleEnsure(List<UpscaleStep> steps)
    {
        string key = string.Join("|", steps);
        if (key == upChainKey)
            return true;

        var compiled = new CompiledMagpieEffect[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            var task = MagpieEffects.Compile(steps[i].Effect);
            if (!task.IsCompleted)
                return false; // Still compiling: draw the usual way meanwhile

            if (!task.IsCompletedSuccessfully)
            {
                upFailed = true;
                Log.Error($"[Upscale] {steps[i].Effect} unavailable ({task.Exception?.GetBaseException().Message})");
                return false;
            }
            compiled[i] = task.Result;
        }

        FLUpscaleDisposeChain();
        try
        {
            var first = steps[0];
            if (upInput == null || upInput.Width != first.InputWidth || upInput.Height != first.InputHeight)
            {
                upInput?.Dispose();
                upInput = null;
                upInput = new GpuTexture(device, first.InputWidth, first.InputHeight, Format.R16G16B16A16_Float, BindFlags.ShaderResource | BindFlags.RenderTarget);
            }

            for (int i = 0; i < steps.Count; i++)
            {
                var runner = new MagpieEffectRunner(device, compiled[i]);
                upChain.Add((steps[i], runner));
                runner.Resize(steps[i].InputWidth, steps[i].InputHeight, steps[i].OutputWidth, steps[i].OutputHeight);
            }

            upChainKey = key;
            Log.Info($"[Upscale] {VisibleWidth}x{VisibleHeight}: {UpscalePlanner.Describe(steps)}");
            return true;
        }
        catch (Exception e)
        {
            upFailed = true;
            Log.Error($"[Upscale] Disabled after error ({e.Message})");
            FLUpscaleDisposeChain();
            return false;
        }
    }

    /// <summary>After each measurement: three too slow in a row step the upscaling down a level.</summary>
    void FLUpscaleCheckBudget(double ms)
    {
        if (!ShaderUpscaling || !upChainUpscales || upLevel >= 2)
            return;

        if (!UpscaleBudget.IsOver(ms, scfg?.FPS ?? 0))
        {
            upSlowStrikes = 0;
            return;
        }

        if (++upSlowStrikes < UpscaleBudget.Strikes)
            return;

        upSlowStrikes = 0;
        var current = upLevel >= 1 ? ShaderUpscaler.Fast : ucfg._ShaderUpscaler;
        upLevel = current == ShaderUpscaler.Fast ? 2 : 1;
        Log.Warn($"[Upscale] {ms:F1} ms a frame is too slow: stepping down to {(upLevel == 1 ? "FSR 1" : "no upscaling")}");
        SetShaderUpscalingLowered(upLevel == 1 ? "Fast (FSR 1)" : "Off");
    }

    void SetShaderUpscaling(bool active, string steps)
    {
        if (ShaderUpscaling != active)
        {
            ShaderUpscaling = active;
            RaiseUI(nameof(ShaderUpscaling));
        }
        SetUpscalingSteps(active ? steps : null);
    }

    void SetShaderUpscalingIdle(string reason)
    {
        if (ShaderUpscalingIdleReason == reason)
            return;

        ShaderUpscalingIdleReason = reason;
        RaiseUI(nameof(ShaderUpscalingIdleReason));
        if (reason != null)
            Log.Info($"[Upscale] Not running: {reason}");
    }

    void SetShaderUpscalingLowered(string level)
    {
        if (ShaderUpscalingLowered == level)
            return;

        ShaderUpscalingLowered = level;
        RaiseUI(nameof(ShaderUpscalingLowered));
    }

    /// <summary>The Flyleaf processor stopped drawing (another processor took over, or the device went).</summary>
    void FLUpscaleStopped()
    {
        SetShaderUpscaling(false, null);
        SetShaderUpscalingIdle(null);
    }

    void FLUpscaleDisposeChain()
    {
        foreach (var (_, runner) in upChain)
            runner.Dispose();
        upChain.Clear();
        upChainKey = null;
    }

    void FLUpscaleDispose()
    {   // With the device (FLDispose), or after an error
        upChroma?.Dispose();
        upChroma = null;
        upHdr?.Dispose();
        upHdr = null;
        FLUpscaleDisposeChain();
        upInput?.Dispose();
        upInput = null;
        UpscaleDispose();
        FLUpscaleStopped();
    }
}
