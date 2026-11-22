using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace FlyleafLib.MediaFramework.MediaRenderer.Effects;

/// <summary>
/// Fork: lets the upscaling effects run with HDR output (Effects/EdgeLoop/HdrPicture.hlsl). <see cref="ToPQ"/> turns
/// the renderer's linear scRGB picture into a PQ one, which the effects work on as they would an SDR picture, and
/// <see cref="FromPQ"/> draws their result onto the screen as scRGB again.
/// </summary>
internal sealed class HdrPicture : IDisposable
{
    const string Name = "HdrPicture";
    static readonly object compileLock = new();
    static Task<Blob[]> compiled;

    /// <summary>The vertex shader and the two pixel shaders, compiled once per process on a worker thread. Faults if they don't compile.</summary>
    public static Task<Blob[]> Compile()
    {
        lock (compileLock)
            return compiled ??= Task.Run(() =>
            {
                byte[] source = Encoding.UTF8.GetBytes(MagpieEffects.ReadSource(Name));
                return new[] { Compile(source, "VS", "vs_5_0"), Compile(source, "ToPQ", "ps_5_0"), Compile(source, "FromPQ", "ps_5_0") };
            });
    }

    static Blob Compile(byte[] source, string entryPoint, string profile) =>
        ShaderCompiler.CompileShader(source, $"{Name}.hlsl", entryPoint, profile, out string errors)
        ?? throw new InvalidOperationException($"{Name} {entryPoint} doesn't compile: {errors}");

    /// <summary>The PQ picture (16-bit float), after <see cref="ToPQ"/>.</summary>
    public GpuTexture Output { get; private set; }

    readonly ID3D11Device device;
    readonly ID3D11VertexShader vs;
    readonly ID3D11PixelShader toPQ, fromPQ;

    public HdrPicture(ID3D11Device device, Blob[] blobs)
    {
        this.device = device;
        try
        {
            vs = device.CreateVertexShader(blobs[0]);
            toPQ = device.CreatePixelShader(blobs[1]);
            fromPQ = device.CreatePixelShader(blobs[2]);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// Draws the scRGB <paramref name="picture"/> into <see cref="Output"/> as PQ, at the same size, then unbinds it as a
    /// render target so the effects can read it. Sets the vertex and pixel shaders and resource slot 0; the caller
    /// puts its own back.
    /// </summary>
    public void ToPQ(ID3D11DeviceContext context, ID3D11ShaderResourceView picture, uint width, uint height)
    {
        if (Output == null || Output.Width != width || Output.Height != height)
        {
            Output?.Dispose();
            Output = null;
            Output = new GpuTexture(device, width, height, Format.R16G16B16A16_Float, BindFlags.ShaderResource | BindFlags.RenderTarget);
        }

        context.OMSetRenderTargets(Output.RTV);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        Draw(context, toPQ, picture);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null);
    }

    /// <summary>Draws the PQ <paramref name="picture"/> into the render target and viewport already set, as scRGB.</summary>
    public void FromPQ(ID3D11DeviceContext context, ID3D11ShaderResourceView picture) => Draw(context, fromPQ, picture);

    void Draw(ID3D11DeviceContext context, ID3D11PixelShader shader, ID3D11ShaderResourceView picture)
    {
        context.VSSetShader(vs);
        context.PSSetShader(shader);
        context.PSSetShaderResource(0, picture);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null);
    }

    public void Dispose()
    {
        Output?.Dispose();
        fromPQ?.Dispose();
        toPQ?.Dispose();
        vs?.Dispose();
    }
}
