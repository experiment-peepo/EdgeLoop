using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace FlyleafLib.MediaFramework.MediaRenderer.Effects;

/// <summary>
/// Fork: rebuilds full-resolution colour for 4:2:0 hardware frames (NV12, P010) from their brightness, with
/// EdgeLoop's chroma-from-luma shader (Effects/EdgeLoop/ChromaFromLuma.hlsl). <see cref="Output"/> takes the colour
/// plane's place in the pixel shader, which samples it as it would the half-size plane.
/// </summary>
internal sealed class ChromaFromLuma : IDisposable
{
    const string Name = "ChromaFromLuma";
    static readonly object compileLock = new();
    static Task<Blob> compiled;

    /// <summary>The shader, compiled once per process on a worker thread. Faults if it doesn't compile.</summary>
    public static Task<Blob> Compile()
    {
        lock (compileLock)
            return compiled ??= Task.Run(() =>
                ShaderCompiler.CompileCS(Encoding.UTF8.GetBytes(MagpieEffects.ReadSource(Name)), $"{Name}.hlsl", "main", out string errors)
                ?? throw new InvalidOperationException($"{Name} doesn't compile: {errors}"));
    }

    /// <summary>Colour at brightness resolution (R16G16 float), after <see cref="Run"/>.</summary>
    public GpuTexture Output { get; private set; }

    readonly ID3D11Device device;
    readonly ID3D11ComputeShader shader;
    readonly ID3D11SamplerState linearClamp;
    ID3D11Buffer sizes;

    public ChromaFromLuma(ID3D11Device device, Blob blob)
    {
        this.device = device;
        try
        {
            shader = device.CreateComputeShader(blob.AsBytes());
            linearClamp = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MaxLOD = float.MaxValue
            });
        }
        catch { Dispose(); throw; }
    }

    /// <param name="luma">The brightness plane (a one-slice Texture2DArray view, as the decoder's are).</param>
    /// <param name="chroma">The half-size colour plane, the same kind of view.</param>
    /// <param name="width">The brightness plane's width.</param>
    /// <param name="height">Its height.</param>
    public void Run(ID3D11DeviceContext context, ID3D11ShaderResourceView luma, ID3D11ShaderResourceView chroma, uint width, uint height)
    {
        uint chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        if (Output == null || Output.Width != width || Output.Height != height)
        {
            Output?.Dispose();
            Output = null;
            Output = new GpuTexture(device, width, height, Format.R16G16_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess);

            sizes ??= device.CreateBuffer(new BufferDescription { Usage = ResourceUsage.Default, BindFlags = BindFlags.ConstantBuffer, ByteWidth = 32 });
            context.UpdateSubresource(new uint[]
            {
                width, height, chromaWidth, chromaHeight,
                BitConverter.SingleToUInt32Bits(1f / width), BitConverter.SingleToUInt32Bits(1f / height),
                BitConverter.SingleToUInt32Bits(1f / chromaWidth), BitConverter.SingleToUInt32Bits(1f / chromaHeight)
            }, sizes);
        }

        context.CSSetShader(shader);
        context.CSSetConstantBuffer(0, sizes);
        context.CSSetSampler(0, linearClamp);
        context.CSSetShaderResource(0, luma);
        context.CSSetShaderResource(1, chroma);
        context.CSSetUnorderedAccessView(0, Output.UAV);
        context.Dispatch((chromaWidth + 7) / 8, (chromaHeight + 7) / 8, 1);

        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
        context.CSSetShaderResource(1, null);
        context.CSSetSampler(0, null);
        context.CSSetConstantBuffer(0, null);
        context.CSSetShader(null);
    }

    public void Dispose()
    {
        Output?.Dispose();
        sizes?.Dispose();
        linearClamp?.Dispose();
        shader?.Dispose();
    }
}
