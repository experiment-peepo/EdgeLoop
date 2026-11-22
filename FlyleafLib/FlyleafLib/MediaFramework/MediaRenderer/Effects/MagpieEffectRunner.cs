using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaRenderer.Effects;

/// <summary>An effect with its passes compiled (device-independent), ready for <see cref="MagpieEffectRunner"/>.</summary>
internal sealed record CompiledMagpieEffect(MagpieEffect Effect, Blob[] Passes);

/// <summary>
/// Fork: the Magpie effects bundled with FlyleafLib (Effects/Magpie/*.hlsl, from https://github.com/Blinue/Magpie,
/// GPL-3.0), read from the assembly and compiled once per process on a worker thread.
/// </summary>
internal static class MagpieEffects
{
    public const string FSRCNNX = "FSRCNNX";
    public const string FSRCNNXLineArt = "FSRCNNX_LineArt";
    public const string Easu = "FSR_EASU";
    public const string Rcas = "FSR_RCAS";
    public const string SSimDownscaler = "SSimDownscaler";

    public static readonly IReadOnlyList<string> Bundled = [FSRCNNX, FSRCNNXLineArt, Easu, Rcas, SSimDownscaler];

    static readonly Dictionary<string, Task<CompiledMagpieEffect>> compiled = [];

    public static string ReadSource(string name)
    {
        using var stream = typeof(MagpieEffects).Assembly.GetManifestResourceStream($"MagpieEffects/{name}.hlsl")
            ?? throw new FileNotFoundException($"Magpie effect {name} isn't bundled");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static MagpieEffect Load(string name) => MagpieEffect.Parse(name, ReadSource(name));

    /// <summary>The effect compiled, started on first use. Faults if it doesn't read or compile.</summary>
    public static Task<CompiledMagpieEffect> Compile(string name)
    {
        lock (compiled)
        {
            if (!compiled.TryGetValue(name, out var task))
                compiled[name] = task = Task.Run(() =>
                {
                    var watch = Stopwatch.StartNew();
                    var effect = Load(name);
                    var blobs = new Blob[effect.Passes.Count];
                    var errors = new string[blobs.Length];
                    Parallel.For(0, blobs.Length, i =>
                        blobs[i] = ShaderCompiler.CompileCS(Encoding.UTF8.GetBytes(effect.GeneratePassSource(i + 1)), $"{name}_Pass{i + 1}.hlsl", "__M", out errors[i]));
                    int failed = Array.FindIndex(blobs, b => b == null);
                    if (failed >= 0)
                        throw new InvalidOperationException($"{name} pass {failed + 1} doesn't compile: {errors[failed]}");
                    Engine.Log?.Info($"[Shader] Compiled {name} ({blobs.Length} passes) in {watch.ElapsedMilliseconds} ms");
                    return new CompiledMagpieEffect(effect, blobs);
                });
            return task;
        }
    }
}

/// <summary>
/// Fork: runs a compiled Magpie effect on one device. <see cref="Resize"/> for the input (and, when the effect
/// doesn't fix it, the output) size, then <see cref="Run"/> with the input picture; the result is in
/// <see cref="Output"/>. Ported from Magpie's EffectDrawer.
/// </summary>
internal sealed class MagpieEffectRunner : IDisposable
{
    public MagpieEffect Effect { get; }
    public uint InputWidth { get; private set; }
    public uint InputHeight { get; private set; }
    public uint OutputWidth { get; private set; }
    public uint OutputHeight { get; private set; }
    public GpuTexture Output => textures?[1];

    readonly ID3D11Device device;
    readonly ID3D11ComputeShader[] shaders;
    readonly ID3D11SamplerState[] samplers;
    readonly float[] parameterValues;
    ID3D11Buffer constants, frameBuffer;
    GpuTexture[] textures; // By effect texture; [0] (INPUT) is the caller's
    (uint X, uint Y)[] dispatches;
    uint frameCount;
    bool constantsDirty = true;

    static readonly ID3D11ShaderResourceView[] noSRVs = new ID3D11ShaderResourceView[8];

    public MagpieEffectRunner(ID3D11Device device, CompiledMagpieEffect compiled)
    {
        this.device = device;
        Effect = compiled.Effect;
        parameterValues = Effect.Parameters.Select(p => p.Default).ToArray();
        try
        {
            shaders = compiled.Passes.Select(b => device.CreateComputeShader(b.AsBytes())).ToArray();
            samplers = Effect.Samplers.Select(s => device.CreateSamplerState(new SamplerDescription
            {
                Filter = s.Linear ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
                AddressU = s.Wrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                AddressV = s.Wrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                AddressW = s.Wrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MaxLOD = float.MaxValue
            })).ToArray();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Sets a parameter (by name), within its range. Unknown names are ignored.</summary>
    public void SetParameter(string name, float value)
    {
        for (int i = 0; i < Effect.Parameters.Count; i++)
        {
            var parameter = Effect.Parameters[i];
            if (parameter.Name != name)
                continue;

            value = Math.Clamp(parameter.IsInt ? MathF.Round(value) : value, parameter.Min, parameter.Max);
            if (parameterValues[i] != value)
            {
                parameterValues[i] = value;
                constantsDirty = true;
            }
        }
    }

    /// <summary>
    /// Sizes the textures for an input this big. <paramref name="targetWidth"/> x <paramref name="targetHeight"/> is the
    /// output size unless the effect sets its own (FSRCNNX: twice the input). Nothing happens if nothing changed.
    /// </summary>
    public void Resize(uint inputWidth, uint inputHeight, uint targetWidth, uint targetHeight)
    {
        var output = Effect.Textures[1];
        var values = new Dictionary<string, double> { ["INPUT_WIDTH"] = inputWidth, ["INPUT_HEIGHT"] = inputHeight };
        uint outputWidth = output.WidthExpression == null ? targetWidth : Size(output.WidthExpression, values);
        uint outputHeight = output.HeightExpression == null ? targetHeight : Size(output.HeightExpression, values);
        if (outputWidth == 0 || outputHeight == 0)
            throw new ArgumentException($"{Effect.Name} has nothing to draw into");

        if (textures != null && InputWidth == inputWidth && InputHeight == inputHeight && OutputWidth == outputWidth && OutputHeight == outputHeight)
            return;

        DisposeTextures();
        values["OUTPUT_WIDTH"] = outputWidth;
        values["OUTPUT_HEIGHT"] = outputHeight;

        try
        {
            textures = new GpuTexture[Effect.Textures.Count];
            for (int i = 1; i < textures.Length; i++)
            {
                var texture = Effect.Textures[i];
                uint width = i == 1 ? outputWidth : Size(texture.WidthExpression, values);
                uint height = i == 1 ? outputHeight : Size(texture.HeightExpression, values);
                textures[i] = new GpuTexture(device, width, height, texture.Format.Dxgi, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
            }

            dispatches = Effect.Passes.Select(p =>
            {
                var target = textures[p.Outputs[0]];
                return ((target.Width + p.BlockWidth - 1) / p.BlockWidth, (target.Height + p.BlockHeight - 1) / p.BlockHeight);
            }).ToArray();
        }
        catch { DisposeTextures(); throw; }

        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        constantsDirty = true;
    }

    static uint Size(string expression, IReadOnlyDictionary<string, double> values) =>
        (uint)Math.Max(1, Math.Round(MagpieExpression.Evaluate(expression, values)));

    /// <summary>Runs every pass on <paramref name="input"/> (INPUT_WIDTH x INPUT_HEIGHT). Leaves no compute state behind.</summary>
    public void Run(ID3D11DeviceContext context, ID3D11ShaderResourceView input)
    {
        if (textures == null)
            throw new InvalidOperationException("Resize first");

        if (constantsDirty)
            UpdateConstants(context);

        context.CSSetConstantBuffer(0, constants);
        if (Effect.UseDynamic)
        {
            frameBuffer ??= device.CreateBuffer(new BufferDescription { Usage = ResourceUsage.Default, BindFlags = BindFlags.ConstantBuffer, ByteWidth = 16 });
            context.UpdateSubresource(new uint[] { frameCount++, 0, 0, 0 }, frameBuffer);
            context.CSSetConstantBuffer(1, frameBuffer);
        }
        for (uint i = 0; i < samplers.Length; i++)
            context.CSSetSampler(i, samplers[i]);

        for (int p = 0; p < Effect.Passes.Count; p++)
        {
            var pass = Effect.Passes[p];
            context.CSSetShader(shaders[p]);
            for (int i = 0; i < pass.Inputs.Count; i++)
                context.CSSetShaderResource((uint)i, pass.Inputs[i] == 0 ? input : textures[pass.Inputs[i]].SRV);
            for (int i = 0; i < pass.Outputs.Count; i++)
                context.CSSetUnorderedAccessView((uint)i, textures[pass.Outputs[i]].UAV);

            context.Dispatch(dispatches[p].X, dispatches[p].Y, 1);

            // A texture can't be read and written at once, and later passes read these
            context.CSSetShaderResources(0, noSRVs);
            for (int i = 0; i < pass.Outputs.Count; i++)
                context.CSSetUnorderedAccessView((uint)i, null);
        }

        context.CSSetShader(null);
        context.CSSetConstantBuffer(0, null);
        if (Effect.UseDynamic)
            context.CSSetConstantBuffer(1, null);
        for (uint i = 0; i < samplers.Length; i++)
            context.CSSetSampler(i, null);
    }

    /// <summary>
    /// The constants in __CB1's order (see <see cref="MagpieEffect.GeneratePassSource"/>): input and output sizes,
    /// their pixel steps and the scale, each PS-style pass's output size and step (but the last's), then parameters.
    /// Every field is 4 bytes and the pairs fall on 8-byte boundaries, so HLSL's packing matches.
    /// </summary>
    void UpdateConstants(ID3D11DeviceContext context)
    {
        var values = new List<uint>
        {
            InputWidth, InputHeight, OutputWidth, OutputHeight,
            Float(1f / InputWidth), Float(1f / InputHeight), Float(1f / OutputWidth), Float(1f / OutputHeight),
            Float(OutputWidth / (float)InputWidth), Float(OutputHeight / (float)InputHeight)
        };
        for (int i = 0; i < Effect.Passes.Count - 1; i++)
        {
            if (!Effect.Passes[i].PsStyle)
                continue;
            var target = textures[Effect.Passes[i].Outputs[0]];
            values.AddRange([target.Width, target.Height, Float(1f / target.Width), Float(1f / target.Height)]);
        }
        for (int i = 0; i < Effect.Parameters.Count; i++)
            values.Add(Effect.Parameters[i].IsInt ? unchecked((uint)(int)parameterValues[i]) : Float(parameterValues[i]));

        while (values.Count % 4 != 0)
            values.Add(0);

        var data = values.ToArray();
        if (constants == null || constants.Description.ByteWidth != (uint)(data.Length * 4))
        {
            constants?.Dispose();
            constants = device.CreateBuffer(new BufferDescription
            {
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer,
                ByteWidth = (uint)(data.Length * 4)
            });
        }
        context.UpdateSubresource(data, constants);
        constantsDirty = false;

        static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);
    }

    void DisposeTextures()
    {
        if (textures != null)
            foreach (var texture in textures)
                texture?.Dispose();
        textures = null;
        dispatches = null;
        InputWidth = InputHeight = OutputWidth = OutputHeight = 0;
    }

    public void Dispose()
    {
        DisposeTextures();
        if (shaders != null)
            foreach (var shader in shaders)
                shader?.Dispose();
        if (samplers != null)
            foreach (var sampler in samplers)
                sampler?.Dispose();
        constants?.Dispose();
        frameBuffer?.Dispose();
    }
}

/// <summary>Fork: a 2D texture with the views the upscaling needs (shader input, and render target or compute output).</summary>
internal sealed class GpuTexture : IDisposable
{
    public uint Width { get; }
    public uint Height { get; }
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView SRV { get; }
    public ID3D11RenderTargetView RTV { get; }
    public ID3D11UnorderedAccessView UAV { get; }

    public GpuTexture(ID3D11Device device, uint width, uint height, Format format, BindFlags bind)
    {
        Width = width;
        Height = height;
        try
        {
            Texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bind
            });
            if (bind.HasFlag(BindFlags.ShaderResource))
                SRV = device.CreateShaderResourceView(Texture);
            if (bind.HasFlag(BindFlags.RenderTarget))
                RTV = device.CreateRenderTargetView(Texture);
            if (bind.HasFlag(BindFlags.UnorderedAccess))
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
