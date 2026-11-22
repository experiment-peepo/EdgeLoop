using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Vortice.DXGI;

namespace FlyleafLib.MediaFramework.MediaRenderer.Effects;

/// <summary>
/// Fork: one of Magpie's shader effect files (MagpieFX version 4), read and turned into compute shader
/// source, one per pass. Ported from Magpie's EffectCompiler (https://github.com/Blinue/Magpie, GPL-3.0).
/// Every pass becomes a compute shader, including "PS style" ones, as in Magpie.
/// </summary>
/// <remarks>
/// Not supported: textures loaded from image files (<c>//!SOURCE</c>). FP16 is never used: <c>MF</c> is float.
/// INPUT and OUTPUT are 16-bit float textures here (Magpie: 8-bit), matching the renderer's.
/// </remarks>
internal sealed partial class MagpieEffect
{
    public const int Version = 4;
    const string Meta = "//!";

    public string Name { get; }
    public bool UseMulAdd { get; private set; }
    public bool UseDynamic { get; private set; }
    public IReadOnlyList<MagpieParameter> Parameters => parameters;
    /// <summary>[0] is INPUT and [1] is OUTPUT, then the effect's own textures.</summary>
    public IReadOnlyList<MagpieTexture> Textures => textures;
    public IReadOnlyList<MagpieSampler> Samplers => samplers;
    public IReadOnlyList<MagpiePass> Passes => passes;

    readonly List<MagpieParameter> parameters = [];
    readonly List<MagpieTexture> textures = [];
    readonly List<MagpieSampler> samplers = [];
    readonly List<MagpiePass> passes = [];
    readonly List<string> commonBlocks = [];

    MagpieEffect(string name) => Name = name;

    /// <summary>Reads an effect file. Throws <see cref="FormatException"/> (or <see cref="NotSupportedException"/>) with what's wrong.</summary>
    public static MagpieEffect Parse(string name, string source)
    {
        var effect = new MagpieEffect(name);
        effect.ParseSource(source.Replace("\r\n", "\n"));
        return effect;
    }

    void ParseSource(string source)
    {
        source = RemoveComments(source);

        var lines = source.Split('\n');
        int first = 0;
        while (first < lines.Length && string.IsNullOrWhiteSpace(lines[first]))
            first++;
        if (first == lines.Length || !Regex.IsMatch(lines[first], @"^\s*//!\s*MAGPIE\s+EFFECT\s*$"))
            Fail("doesn't start with //!MAGPIE EFFECT");

        // Blocks: the header, then one per PARAMETER, TEXTURE, SAMPLER, COMMON and PASS directive
        var header = new List<string>();
        var blocks = new List<(string Kind, List<string> Lines)>();
        for (int i = first + 1; i < lines.Length; i++)
        {
            string kind = BlockKind(lines[i]);
            if (kind != null)
                blocks.Add((kind, [lines[i]]));
            else if (blocks.Count == 0)
                header.Add(lines[i]);
            else
                blocks[^1].Lines.Add(lines[i]);
        }

        ParseHeader(header);

        textures.Add(new MagpieTexture("INPUT", MagpieFormat.Default, "INPUT_WIDTH", "INPUT_HEIGHT"));
        textures.Add(new MagpieTexture("OUTPUT", MagpieFormat.Default, null, null));

        var passBlocks = new List<List<string>>();
        foreach (var (kind, blockLines) in blocks)
        {
            switch (kind)
            {
                case "PARAMETER":
                    ParseParameter(blockLines);
                    break;
                case "TEXTURE":
                    ParseTexture(blockLines);
                    break;
                case "SAMPLER":
                    ParseSampler(blockLines);
                    break;
                case "COMMON":
                    commonBlocks.Add(string.Join('\n', blockLines.Skip(1)));
                    break;
                case "PASS":
                    passBlocks.Add(blockLines);
                    break;
            }
        }

        if (passBlocks.Count == 0)
            Fail("has no passes");
        ParsePasses(passBlocks);
    }

    static string BlockKind(string line)
    {
        var match = Regex.Match(line, @"^\s*//!\s*(PARAMETER|TEXTURE|SAMPLER|COMMON|PASS)\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>A block's leading directives (name, value) and the code after them.</summary>
    static (List<(string Name, string Value)> Directives, string Code) SplitBlock(List<string> blockLines, int skip)
    {
        var directives = new List<(string, string)>();
        int i = skip;
        for (; i < blockLines.Count; i++)
        {
            var match = Regex.Match(blockLines[i], @"^\s*//!\s*(\w+)\s*(.*?)\s*$");
            if (!match.Success)
            {
                if (string.IsNullOrWhiteSpace(blockLines[i]))
                    continue;
                break;
            }
            directives.Add((match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value));
        }
        return (directives, string.Join('\n', blockLines.Skip(i)));
    }

    void ParseHeader(List<string> header)
    {
        var (directives, _) = SplitBlock(header, 0);
        bool version = false;
        foreach (var (name, value) in directives)
        {
            switch (name)
            {
                case "VERSION":
                    if (!int.TryParse(value, out int v) || v != Version)
                        Fail($"is MagpieFX version {value}, only {Version} is supported");
                    version = true;
                    break;
                case "USE":
                    foreach (var flag in value.Split(',').Select(f => f.Trim().ToUpperInvariant()))
                    {
                        if (flag == "MULADD")
                            UseMulAdd = true;
                        else if (flag == "_DYNAMIC")
                            UseDynamic = true;
                    }
                    break;
                case "CAPABILITY": // FP16 is optional; it's never used here
                case "SORT_NAME":
                    break;
            }
        }
        if (!version)
            Fail("has no //!VERSION");
    }

    void ParseParameter(List<string> blockLines)
    {
        var (directives, code) = SplitBlock(blockLines, 1);
        string Get(string key, bool required = true)
        {
            var found = directives.FirstOrDefault(d => d.Name == key);
            if (found.Name == null && required)
                Fail($"parameter has no //!{key}");
            return found.Value;
        }

        var declaration = Regex.Match(code, @"^\s*(float|int)\s+([A-Za-z_]\w*)\s*;\s*$");
        if (!declaration.Success)
            Fail($"parameter isn't followed by a float or int declaration: {code.Trim()}");

        bool isInt = declaration.Groups[1].Value == "int";
        float Number(string key) => float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)
            ? n : throw Error($"parameter {declaration.Groups[2].Value} has an invalid //!{key}");

        var parameter = new MagpieParameter(declaration.Groups[2].Value, Get("LABEL", false) ?? declaration.Groups[2].Value,
            isInt, Number("DEFAULT"), Number("MIN"), Number("MAX"), Number("STEP"));
        if (parameter.Default < parameter.Min || parameter.Default > parameter.Max)
            Fail($"parameter {parameter.Name}'s default is outside its range");
        parameters.Add(parameter);
    }

    void ParseTexture(List<string> blockLines)
    {
        var (directives, code) = SplitBlock(blockLines, 1);
        var declaration = Regex.Match(code, @"^\s*Texture2D\s+([A-Za-z_]\w*)\s*;\s*$");
        if (!declaration.Success)
            Fail($"texture isn't followed by a Texture2D declaration: {code.Trim()}");

        string name = declaration.Groups[1].Value;
        string width = null, height = null;
        var format = MagpieFormat.Default;
        foreach (var (key, value) in directives)
        {
            switch (key)
            {
                case "WIDTH":
                    width = value.Replace(" ", "");
                    break;
                case "HEIGHT":
                    height = value.Replace(" ", "");
                    break;
                case "FORMAT":
                    format = MagpieFormat.Get(value) ?? throw Error($"texture {name} has an unknown format {value}");
                    break;
                case "SOURCE":
                    throw new NotSupportedException($"Effect {Name}: texture {name} is loaded from a file, which isn't supported");
            }
        }
        if ((width == null) != (height == null))
            Fail($"texture {name} needs both //!WIDTH and //!HEIGHT");

        if (name == "INPUT")
        {
            if (directives.Count > 0)
                Fail("INPUT can't have options");
        }
        else if (name == "OUTPUT")
        {
            if (directives.Any(d => d.Name == "FORMAT"))
                Fail("OUTPUT can't have a format");
            textures[1] = textures[1] with { WidthExpression = width, HeightExpression = height };
        }
        else
        {
            if (width == null)
                Fail($"texture {name} has no size");
            textures.Add(new MagpieTexture(name, format, width, height));
        }
    }

    void ParseSampler(List<string> blockLines)
    {
        var (directives, code) = SplitBlock(blockLines, 1);
        var declaration = Regex.Match(code, @"^\s*SamplerState\s+([A-Za-z_]\w*)\s*;\s*$");
        if (!declaration.Success)
            Fail($"sampler isn't followed by a SamplerState declaration: {code.Trim()}");

        string filter = directives.FirstOrDefault(d => d.Name == "FILTER").Value?.ToUpperInvariant();
        string address = directives.FirstOrDefault(d => d.Name == "ADDRESS").Value?.ToUpperInvariant() ?? "CLAMP";
        if (filter is not ("LINEAR" or "POINT"))
            Fail($"sampler {declaration.Groups[1].Value} needs //!FILTER LINEAR or POINT");
        if (address is not ("CLAMP" or "WRAP"))
            Fail($"sampler {declaration.Groups[1].Value} has an unknown //!ADDRESS");

        samplers.Add(new MagpieSampler(declaration.Groups[1].Value, filter == "LINEAR", address == "WRAP"));
    }

    void ParsePasses(List<List<string>> passBlocks)
    {
        var numbered = passBlocks
            .Select(b => (Number: int.Parse(Regex.Match(b[0], @"PASS\s+(\d+)", RegexOptions.IgnoreCase).Groups[1].Value, CultureInfo.InvariantCulture), Block: b))
            .OrderBy(p => p.Number)
            .ToList();
        for (int i = 0; i < numbered.Count; i++)
            if (numbered[i].Number != i + 1)
                Fail("passes must be numbered 1, 2, 3 and so on");

        for (int i = 0; i < numbered.Count; i++)
        {
            var (directives, code) = SplitBlock(numbered[i].Block, 1);
            bool last = i == numbered.Count - 1;
            string Get(string key) => directives.FirstOrDefault(d => d.Name == key).Value;

            var available = textures.Select((t, index) => (t.Name, index)).ToDictionary(t => t.Name, t => t.index);
            List<int> Names(string key, bool outputs)
            {
                string value = Get(key) ?? throw Error($"pass {i + 1} has no //!{key}");
                var result = new List<int>();
                foreach (var texture in value.Split(',').Select(n => n.Trim()))
                {
                    if (!available.Remove(texture, out int index))
                        throw Error($"pass {i + 1} uses an unknown or repeated texture {texture}");
                    if (outputs ? index == 0 || (index == 1) != last : index == 1)
                        throw Error($"pass {i + 1} can't {(outputs ? "write" : "read")} {texture}");
                    result.Add(index);
                }
                return result;
            }

            var inputs = Names("IN", false);
            var outputs = Names("OUT", true);
            if (outputs.Count > 8 || (last && outputs.Count != 1))
                Fail($"pass {i + 1} has too many outputs");

            bool psStyle = Get("STYLE") is string style && style.Trim().Equals("PS", StringComparison.OrdinalIgnoreCase);
            uint blockWidth = 16, blockHeight = 16, threadsX = 64, threadsY = 1, threadsZ = 1;
            if (!psStyle)
            {
                var block = (Get("BLOCK_SIZE") ?? throw Error($"pass {i + 1} has no //!BLOCK_SIZE")).Split(',').Select(UInt).ToArray();
                var threads = (Get("NUM_THREADS") ?? throw Error($"pass {i + 1} has no //!NUM_THREADS")).Split(',').Select(UInt).ToArray();
                blockWidth = block[0];
                blockHeight = block.Length > 1 ? block[1] : block[0];
                threadsX = threads[0];
                threadsY = threads.Length > 1 ? threads[1] : 1;
                threadsZ = threads.Length > 2 ? threads[2] : 1;
            }

            passes.Add(new MagpiePass(i + 1, Get("DESC")?.Trim() ?? $"Pass {i + 1}", inputs, outputs, psStyle,
                blockWidth, blockHeight, threadsX, threadsY, threadsZ, code));
        }

        uint UInt(string text) => uint.TryParse(text.Trim(), out uint n) && n > 0 ? n : throw Error($"has an invalid number {text}");
    }

    /// <summary>Removes // and /* */ comments, keeping //! directives and line breaks (as Magpie does).</summary>
    static string RemoveComments(string source)
    {
        var result = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/' && (i + 2 >= source.Length || source[i + 2] != '!'))
                {
                    while (i < source.Length && source[i] != '\n')
                        i++;
                    if (i < source.Length)
                        result.Append('\n');
                    continue;
                }
                if (source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                        throw new FormatException("A /* comment isn't closed");
                    i = end + 1;
                    continue;
                }
            }
            result.Append(source[i]);
        }
        return result.ToString();
    }

    /// <summary>
    /// The compute shader source for one pass (1-based), as Magpie generates it: constants, textures and
    /// samplers, its helper functions, the effect's common code, the pass's code and an entry point.
    /// </summary>
    public string GeneratePassSource(int passNumber)
    {
        var pass = passes[passNumber - 1];
        var sb = new StringBuilder(4096 + pass.Code.Length);

        sb.Append($"#define MP_BLOCK_WIDTH {pass.BlockWidth}\n#define MP_BLOCK_HEIGHT {pass.BlockHeight}\n");
        sb.Append($"#define MP_NUM_THREADS_X {pass.ThreadsX}\n#define MP_NUM_THREADS_Y {pass.ThreadsY}\n#define MP_NUM_THREADS_Z {pass.ThreadsZ}\n");
        if (pass.PsStyle)
            sb.Append("#define MP_PS_STYLE\n");
        sb.Append("#define MF float\n");
        for (int i = 1; i <= 4; i++)
        {
            sb.Append($"#define MF{i} float{i}\n");
            for (int j = 1; j <= 4; j++)
                sb.Append($"#define MF{i}x{j} float{i}x{j}\n");
        }
        sb.Append('\n');

        // Constants (see ConstantLayout for the order MagpieEffectRunner fills them in)
        sb.Append("cbuffer __CB1 : register(b0) {\n\tuint2 __inputSize;\n\tuint2 __outputSize;\n\tfloat2 __inputPt;\n\tfloat2 __outputPt;\n\tfloat2 __scale;\n");
        for (int i = 0; i < passes.Count - 1; i++)
            if (passes[i].PsStyle)
                sb.Append($"\tuint2 __pass{i + 1}OutputSize;\n\tfloat2 __pass{i + 1}OutputPt;\n");
        foreach (var parameter in parameters)
            sb.Append($"\t{(parameter.IsInt ? "int" : "float")} {parameter.Name};\n");
        sb.Append("};\n\n");
        if (UseDynamic)
            sb.Append("cbuffer __CB2 : register(b1) { uint __frameCount; };\n\n");

        for (int i = 0; i < pass.Inputs.Count; i++)
        {
            var texture = textures[pass.Inputs[i]];
            sb.Append($"Texture2D<{texture.Format.SrvType}> {texture.Name} : register(t{i});\n");
        }
        for (int i = 0; i < pass.Outputs.Count; i++)
        {
            var texture = textures[pass.Outputs[i]];
            sb.Append($"RWTexture2D<{texture.Format.UavType}> {texture.Name} : register(u{i});\n");
        }
        for (int i = 0; i < samplers.Count; i++)
            sb.Append($"SamplerState {samplers[i].Name} : register(s{i});\n");
        sb.Append('\n');

        sb.Append("""
            uint __Bfe(uint src, uint off, uint bits) { uint mask = (1u << bits) - 1; return (src >> off) & mask; }
            uint __BfiM(uint src, uint ins, uint bits) { uint mask = (1u << bits) - 1; return (ins & mask) | (src & (~mask)); }
            uint2 Rmp8x8(uint a) { return uint2(__Bfe(a, 1u, 3u), __BfiM(__Bfe(a, 3u, 3u), a, 1u)); }
            uint2 GetInputSize() { return __inputSize; }
            float2 GetInputPt() { return __inputPt; }
            uint2 GetOutputSize() { return __outputSize; }
            float2 GetOutputPt() { return __outputPt; }
            float2 GetScale() { return __scale; }

            """);
        if (UseDynamic)
            sb.Append("uint GetFrameCount() { return __frameCount; }\n");
        if (UseMulAdd)
            sb.Append(MulAddFunctions());
        sb.Append('\n');

        foreach (var common in commonBlocks)
            sb.Append(common).Append('\n');
        sb.Append(pass.Code).Append("\n\n");

        sb.Append(pass.PsStyle ? PsStyleEntry(pass) : $$"""
            [numthreads({{pass.ThreadsX}}, {{pass.ThreadsY}}, {{pass.ThreadsZ}})]
            void __M(uint3 tid : SV_GroupThreadID, uint3 gid : SV_GroupID) {
            	Pass{{pass.Number}}(gid.xy * uint2({{pass.BlockWidth}}, {{pass.BlockHeight}}), tid);
            }

            """);
        return sb.ToString();
    }

    /// <summary>A "PS style" pass: each of 64 threads computes four pixels of a 16x16 block by calling PassN(pos).</summary>
    string PsStyleEntry(MagpiePass pass)
    {
        bool last = pass.Number == passes.Count;
        string size = last ? "__outputSize" : $"__pass{pass.Number}OutputSize";
        string pt = last ? "__outputPt" : $"__pass{pass.Number}OutputPt";

        string call;
        var declarations = new StringBuilder();
        if (pass.Outputs.Count == 1)
            call = $"{textures[pass.Outputs[0]].Name}[gxy] = Pass{pass.Number}(pos);";
        else
        {
            for (int i = 0; i < pass.Outputs.Count; i++)
                declarations.Append($"\t{textures[pass.Outputs[i]].Format.SrvType} c{i};\n");
            call = $"Pass{pass.Number}(pos, {string.Join(", ", Enumerable.Range(0, pass.Outputs.Count).Select(i => $"c{i}"))}); "
                 + string.Concat(pass.Outputs.Select((o, i) => $"{textures[o].Name}[gxy] = c{i}; "));
        }

        return $$"""
            [numthreads(64, 1, 1)]
            void __M(uint3 tid : SV_GroupThreadID, uint3 gid : SV_GroupID) {
            	uint2 gxy = (gid.xy << 4u) + Rmp8x8(tid.x);
            	if (gxy.x >= {{size}}.x || gxy.y >= {{size}}.y) {
            		return;
            	}
            	float2 pos = (gxy + 0.5f) * {{pt}};
            	float2 step = 8 * {{pt}};
            {{declarations}}
            	{ {{call}} }

            	gxy.x += 8u;
            	pos.x += step.x;
            	if (gxy.x < {{size}}.x && gxy.y < {{size}}.y) { {{call}} }

            	gxy.y += 8u;
            	pos.y += step.y;
            	if (gxy.x < {{size}}.x && gxy.y < {{size}}.y) { {{call}} }

            	gxy.x -= 8u;
            	pos.x -= step.x;
            	if (gxy.x < {{size}}.x && gxy.y < {{size}}.y) { {{call}} }
            }

            """;
    }

    /// <summary>Magpie's MulAdd: a vector times a matrix plus a vector, written with mad (faster than mul).</summary>
    static string MulAddFunctions()
    {
        var sb = new StringBuilder();
        string[] lanes = ["x", "y", "z", "w"];
        for (int rows = 2; rows <= 4; rows++)
            for (int cols = 2; cols <= 4; cols++)
            {
                sb.Append($"MF{cols} MulAdd(MF{rows} x, MF{rows}x{cols} y, MF{cols} a) {{\n\tMF{cols} result = a;\n");
                for (int r = 0; r < rows; r++)
                    sb.Append($"\tresult = mad(x.{lanes[r]}, y.{string.Join("", Enumerable.Range(0, cols).Select(c => $"_m{r}{c}"))}, result);\n");
                sb.Append("\treturn result;\n}\n");
            }
        return sb.ToString();
    }

    void Fail(string what) => throw Error(what);
    FormatException Error(string what) => new($"Effect {Name} {what}");
}

internal sealed record MagpieParameter(string Name, string Label, bool IsInt, float Default, float Min, float Max, float Step);

/// <summary>A texture; its size expressions use INPUT_WIDTH, INPUT_HEIGHT, OUTPUT_WIDTH and OUTPUT_HEIGHT. OUTPUT's may be null (the caller's size).</summary>
internal sealed record MagpieTexture(string Name, MagpieFormat Format, string WidthExpression, string HeightExpression);

internal sealed record MagpieSampler(string Name, bool Linear, bool Wrap);

internal sealed record MagpiePass(int Number, string Description, IReadOnlyList<int> Inputs, IReadOnlyList<int> Outputs, bool PsStyle,
    uint BlockWidth, uint BlockHeight, uint ThreadsX, uint ThreadsY, uint ThreadsZ, string Code);

/// <summary>A texture format an effect can ask for, with the HLSL types its views take (Magpie's FORMAT_DESCS).</summary>
internal sealed record MagpieFormat(string Name, Format Dxgi, string SrvType, string UavType)
{
    static readonly MagpieFormat[] All =
    [
        new("R32G32B32A32_FLOAT", Format.R32G32B32A32_Float, "float4", "float4"),
        new("R16G16B16A16_FLOAT", Format.R16G16B16A16_Float, "MF4", "MF4"),
        new("R16G16B16A16_UNORM", Format.R16G16B16A16_UNorm, "MF4", "unorm MF4"),
        new("R16G16B16A16_SNORM", Format.R16G16B16A16_SNorm, "MF4", "snorm MF4"),
        new("R32G32_FLOAT", Format.R32G32_Float, "float2", "float2"),
        new("R10G10B10A2_UNORM", Format.R10G10B10A2_UNorm, "MF4", "unorm MF4"),
        new("R11G11B10_FLOAT", Format.R11G11B10_Float, "MF3", "MF3"),
        new("R8G8B8A8_UNORM", Format.R8G8B8A8_UNorm, "MF4", "unorm MF4"),
        new("R8G8B8A8_SNORM", Format.R8G8B8A8_SNorm, "MF4", "snorm MF4"),
        new("R16G16_FLOAT", Format.R16G16_Float, "MF2", "MF2"),
        new("R16G16_UNORM", Format.R16G16_UNorm, "MF2", "unorm MF2"),
        new("R16G16_SNORM", Format.R16G16_SNorm, "MF2", "snorm MF2"),
        new("R32_FLOAT", Format.R32_Float, "float", "float"),
        new("R8G8_UNORM", Format.R8G8_UNorm, "MF2", "unorm MF2"),
        new("R8G8_SNORM", Format.R8G8_SNorm, "MF2", "snorm MF2"),
        new("R16_FLOAT", Format.R16_Float, "MF", "MF"),
        new("R16_UNORM", Format.R16_UNorm, "MF", "unorm MF"),
        new("R16_SNORM", Format.R16_SNorm, "MF", "snorm MF"),
        new("R8_UNORM", Format.R8_UNorm, "MF", "unorm MF"),
        new("R8_SNORM", Format.R8_SNorm, "MF", "snorm MF"),
    ];

    /// <summary>INPUT, OUTPUT and textures without a format.</summary>
    public static MagpieFormat Default => All[1];

    public static MagpieFormat Get(string name) => All.FirstOrDefault(f => f.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Evaluates a texture size expression: numbers, + - * /, parentheses and the size names.</summary>
internal static class MagpieExpression
{
    public static double Evaluate(string expression, IReadOnlyDictionary<string, double> values)
    {
        int position = 0;
        double result = Sum();
        if (position != expression.Length)
            throw Error();
        return result;

        double Sum()
        {
            double value = Product();
            while (position < expression.Length && expression[position] is '+' or '-')
                value = expression[position++] == '+' ? value + Product() : value - Product();
            return value;
        }

        double Product()
        {
            double value = Unary();
            while (position < expression.Length && expression[position] is '*' or '/')
                value = expression[position++] == '*' ? value * Unary() : value / Unary();
            return value;
        }

        double Unary()
        {
            if (position < expression.Length && expression[position] == '-')
            {
                position++;
                return -Unary();
            }
            if (position < expression.Length && expression[position] == '(')
            {
                position++;
                double value = Sum();
                if (position >= expression.Length || expression[position++] != ')')
                    throw Error();
                return value;
            }

            int start = position;
            if (position < expression.Length && (char.IsLetter(expression[position]) || expression[position] == '_'))
            {
                while (position < expression.Length && (char.IsLetterOrDigit(expression[position]) || expression[position] == '_'))
                    position++;
                string name = expression[start..position];
                return values.TryGetValue(name, out double named) ? named : throw new FormatException($"Unknown size {name} in {expression}");
            }

            while (position < expression.Length && (char.IsDigit(expression[position]) || expression[position] == '.'))
                position++;
            return start < position && double.TryParse(expression[start..position], NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? number : throw Error();
        }

        FormatException Error() => new($"Invalid size expression: {expression}");
    }
}
