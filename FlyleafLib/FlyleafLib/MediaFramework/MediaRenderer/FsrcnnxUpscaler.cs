using System.Linq;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaRenderer;

/// <summary>
/// Fork: doubles an image with FSRCNNX_x2_8-0-4-1 (six compute passes, Shaders/CSShader.FSRCNNX.cs).
/// Draw the image into <see cref="InputRTV"/>, <see cref="Run"/>, then sample <see cref="OutputSRV"/>.
/// </summary>
internal sealed unsafe class FsrcnnxUpscaler : IDisposable
{
    public const Format TextureFormat = Format.R16G16B16A16_Float;

    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public ID3D11Texture2D InputTexture => textures?["INPUT"].Texture;
    public ID3D11RenderTargetView InputRTV => textures?["INPUT"].RTV;
    public ID3D11Texture2D OutputTexture => textures?["OUTPUT"].Texture;
    public ID3D11ShaderResourceView OutputSRV => textures?["OUTPUT"].SRV;

    sealed class Surface : IDisposable
    {
        public readonly ID3D11Texture2D Texture;
        public readonly ID3D11ShaderResourceView SRV;
        public readonly ID3D11RenderTargetView RTV;
        public readonly ID3D11UnorderedAccessView UAV;

        /// <param name="renderTarget">Drawn into (the input) rather than written by a pass.</param>
        public Surface(ID3D11Device device, uint width, uint height, bool renderTarget)
        {
            try
            {
                Texture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = TextureFormat,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | (renderTarget ? BindFlags.RenderTarget : BindFlags.UnorderedAccess)
                });
                SRV = device.CreateShaderResourceView(Texture);
                if (renderTarget)
                    RTV = device.CreateRenderTargetView(Texture);
                else
                    UAV = device.CreateUnorderedAccessView(Texture);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            UAV?.Dispose();
            RTV?.Dispose();
            SRV?.Dispose();
            Texture?.Dispose();
        }
    }

    sealed class Pass
    {
        public ID3D11ComputeShader Shader;
        public ID3D11ShaderResourceView[] SRVs;
        public ID3D11UnorderedAccessView[] UAVs;
        public uint GroupsX, GroupsY;
    }

    struct Sizes
    {
        public uint InputWidth, InputHeight, OutputWidth, OutputHeight;
        public float InputPtX, InputPtY, OutputPtX, OutputPtY;
    }

    static readonly ID3D11ShaderResourceView[] noSRVs = new ID3D11ShaderResourceView[4];

    readonly ID3D11Device device;
    readonly ID3D11ComputeShader[] shaders;
    readonly ID3D11Buffer sizesBuffer;
    Dictionary<string, Surface> textures;
    Pass[] passes;

    /// <param name="blobs">From <see cref="ShaderCompiler.FSRCNNXBlobs"/>.</param>
    public FsrcnnxUpscaler(ID3D11Device device, Blob[] blobs)
    {
        this.device = device;
        try
        {
            shaders = blobs.Select(b => device.CreateComputeShader(b.AsBytes())).ToArray();
            sizesBuffer = device.CreateBuffer(new BufferDescription
            {
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.None,
                ByteWidth = (uint)sizeof(Sizes)
            });
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Sizes the textures for an input this big (the output is twice it). Nothing happens if it already is.</summary>
    public void Resize(ID3D11DeviceContext context, uint width, uint height)
    {
        if (textures != null && Width == width && Height == height)
            return;

        DisposeTextures();
        Width = width;
        Height = height;
        textures = new()
        {
            ["INPUT"] = new(device, width, height, renderTarget: true),
            ["OUTPUT"] = new(device, width * 2, height * 2, renderTarget: false)
        };
        foreach (var name in new[] { "featureMap1", "featureMap2", "tex1", "tex2", "tex3", "tex4" })
            textures[name] = new(device, width, height, renderTarget: false);

        var layout = ShaderCompiler.FSRCNNXPasses;
        passes = new Pass[layout.Length];
        for (int i = 0; i < layout.Length; i++)
        {
            var (_, inputs, outputs, blockWidth, blockHeight) = layout[i];
            // Each pass covers its first output in blocks
            bool doubled = outputs[0] == "OUTPUT";
            uint outWidth = doubled ? width * 2 : width, outHeight = doubled ? height * 2 : height;
            passes[i] = new Pass
            {
                Shader = shaders[i],
                SRVs = inputs.Select(n => textures[n].SRV).ToArray(),
                UAVs = outputs.Select(n => textures[n].UAV).ToArray(),
                GroupsX = (outWidth + blockWidth - 1) / blockWidth,
                GroupsY = (outHeight + blockHeight - 1) / blockHeight
            };
        }

        context.UpdateSubresource(new Sizes
        {
            InputWidth = width,
            InputHeight = height,
            OutputWidth = width * 2,
            OutputHeight = height * 2,
            InputPtX = 1f / width,
            InputPtY = 1f / height,
            OutputPtX = 0.5f / width,
            OutputPtY = 0.5f / height
        }, sizesBuffer);
    }

    /// <summary>Runs the passes: input to output. The input must not be bound as a render target. Leaves no compute state behind.</summary>
    public void Run(ID3D11DeviceContext context, ID3D11SamplerState point, ID3D11SamplerState linear)
    {
        context.CSSetConstantBuffer(0, sizesBuffer);
        context.CSSetSampler(0, point);
        context.CSSetSampler(1, linear);

        foreach (var pass in passes)
        {
            context.CSSetShader(pass.Shader);
            context.CSSetShaderResources(0, pass.SRVs);
            for (uint i = 0; i < pass.UAVs.Length; i++)
                context.CSSetUnorderedAccessView(i, pass.UAVs[i]);

            context.Dispatch(pass.GroupsX, pass.GroupsY, 1);

            // A texture can't be read and written at once, and the next pass reads these
            context.CSSetShaderResources(0, noSRVs);
            for (uint i = 0; i < pass.UAVs.Length; i++)
                context.CSSetUnorderedAccessView(i, null);
        }

        context.CSSetShader(null);
        context.CSSetConstantBuffer(0, null);
        context.CSSetSampler(0, null);
        context.CSSetSampler(1, null);
    }

    void DisposeTextures()
    {
        if (textures != null)
            foreach (var texture in textures.Values)
                texture.Dispose();
        textures = null;
        passes = null;
        Width = Height = 0;
    }

    public void Dispose()
    {
        DisposeTextures();
        if (shaders != null)
            foreach (var shader in shaders)
                shader?.Dispose();
        sizesBuffer?.Dispose();
    }
}
