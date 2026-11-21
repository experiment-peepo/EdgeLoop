using FlyleafLib.MediaFramework.MediaFrame;
using Vortice.Direct3D11;
using Vortice.Mathematics;

using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace FlyleafLib.MediaFramework.MediaRenderer;

/// <summary>
/// Fork: shader upscaling on the Flyleaf processor. The frame is drawn at its own size, doubled by FSRCNNX
/// (<see cref="FsrcnnxUpscaler"/>), then drawn scaled into the viewport. It only runs when the video is
/// enlarged, in SDR and unrotated; anything else draws the usual way.
/// </summary>
public partial class Renderer
{
    /// <summary>Fork: true while frames are being doubled by the FSRCNNX shader.</summary>
    public bool ShaderUpscaling { get; private set; }

    /// <summary>Fork: why shader upscaling isn't running although it's switched on, e.g. "the video isn't enlarged". Null otherwise.</summary>
    public string ShaderUpscalingIdleReason { get; private set; }

    FsrcnnxUpscaler upscaler;
    bool            upFailed;

    /// <summary>Why this frame can't be upscaled by the shader (it's switched on), or null when it can.</summary>
    string FLUpscaleIdleReason()
    {
        if (upFailed)
            return "it failed to start";
        if (hdrSwapchain)
            return "HDR output";
        if (rotation != 0)
            return "rotated video";
        if (Pano360._enabled)
            return "360° video";
        if (splitFrameAlphaPosition != SplitFrameAlphaPosition.None)
            return "video with transparency";
        if (FeatureLevel < FeatureLevel.Level_11_0)
            return "the graphics card is too old";

        uint width = VisibleWidth, height = VisibleHeight;
        if (width < 16 || height < 16)
            return "the video is too small";

        int maxHeight = ucfg._ShaderUpscalingMaxHeight;
        if (maxHeight > 0 && height > maxHeight)
            return $"the video is taller than {maxHeight}p";

        // Only worth it when the video is enlarged
        if (Viewport.Width <= width && Viewport.Height <= height)
            return $"the video isn't enlarged ({width}x{height} shown at {Viewport.Width:F0}x{Viewport.Height:F0})";

        return null;
    }

    /// <summary>Draws the bound frame into <paramref name="target"/> through FSRCNNX. False if it didn't (draw it the usual way).</summary>
    bool FLUpscaleDraw(VideoFrame frame, ID3D11RenderTargetView target)
    {
        string idle = !ucfg._ShaderUpscaling ? null : FLUpscaleIdleReason();
        if (!ucfg._ShaderUpscaling || idle != null || !FLUpscaleEnsure(VisibleWidth, VisibleHeight))
        {
            SetShaderUpscaling(false);
            SetShaderUpscalingIdle(!ucfg._ShaderUpscaling ? null : idle ?? (upFailed ? "it failed to start" : "starting"));
            return false;
        }

        int query = UpscaleTimingBegin();
        try
        {
            // 1. The frame at its own size, with all of the pixel shader's colour work
            context.OMSetRenderTargets(upscaler.InputRTV);
            context.RSSetViewport(new Viewport(0, 0, upscaler.Width, upscaler.Height));
            context.Draw(6, 0);
            context.OMSetRenderTargets((ID3D11RenderTargetView)null); // so the passes can read it

            // 2. Doubled
            upscaler.Run(context, samplerPoint, samplerLinear);

            // 3. Scaled into the viewport
            context.OMSetRenderTargets(target);
            context.RSSetViewport(Viewport);
            context.VSSetShader(vsSimple);
            context.PSSetShader(psShader["rgba"]);
            context.PSSetShaderResource(0, upscaler.OutputSRV);
            context.Draw(6, 0);
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

        SetShaderUpscaling(true);
        SetShaderUpscalingIdle(null);
        SetUpscaledSize($"{upscaler.Width * 2}x{upscaler.Height * 2}");
        return true;
    }

    bool FLUpscaleEnsure(uint width, uint height)
    {
        try
        {
            if (upscaler == null)
            {
                var blobs = ShaderCompiler.FSRCNNXBlobs();
                if (!blobs.IsCompleted)
                    return false; // Still compiling: draw the usual way meanwhile

                if (!blobs.IsCompletedSuccessfully)
                {
                    upFailed = true;
                    Log.Error($"[Upscale] FSRCNNX unavailable ({blobs.Exception?.GetBaseException().Message})");
                    return false;
                }

                upscaler = new(device, blobs.Result);
            }

            if (upscaler.Width != width || upscaler.Height != height)
            {
                upscaler.Resize(context, width, height);
                Log.Info($"[Upscale] FSRCNNX {width}x{height} -> {width * 2}x{height * 2}");
            }
            return true;
        }
        catch (Exception e)
        {
            upFailed = true;
            Log.Error($"[Upscale] Disabled after error ({e.Message})");
            FLUpscaleDispose();
            return false;
        }
    }

    void SetShaderUpscaling(bool active)
    {
        if (ShaderUpscaling == active)
            return;

        ShaderUpscaling = active;
        RaiseUI(nameof(ShaderUpscaling));
        if (!active)
            SetUpscaledSize(null);
    }

    void SetShaderUpscalingIdle(string reason)
    {
        if (ShaderUpscalingIdleReason == reason)
            return;

        ShaderUpscalingIdleReason = reason;
        RaiseUI(nameof(ShaderUpscalingIdleReason));
        if (reason != null)
            Log.Info($"[Upscale] FSRCNNX not running: {reason}");
    }

    /// <summary>The Flyleaf processor stopped drawing (another processor took over, or the device went).</summary>
    void FLUpscaleStopped()
    {
        SetShaderUpscaling(false);
        SetShaderUpscalingIdle(null);
    }

    void FLUpscaleDispose()
    {   // With the device (FLDispose), or after an error
        upscaler?.Dispose();
        upscaler = null;
        UpscaleDispose();
        FLUpscaleStopped();
    }
}
