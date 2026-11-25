using FlyleafLib.MediaFramework.MediaRenderer.Effects;
using AwesomeAssertions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// Magpie's effect files, read, turned into shaders and run on a real GPU (or Windows' software one where
    /// there's none, as in CI): known pictures go in and the results are checked.
    /// </summary>
    public class MagpieEffectTests
    {
        public static TheoryData<string> BundledEffects => new(MagpieEffects.Bundled);

        // ===== Reading the files =====

        [Fact]
        public void Fsrcnnx_IsSixComputePasses_ThatDoubleTheSize()
        {
            var effect = MagpieEffects.Load(MagpieEffects.FSRCNNX);

            effect.Passes.Should().HaveCount(6).And.OnlyContain(p => !p.PsStyle);
            effect.UseMulAdd.Should().BeTrue();
            effect.Passes[0].Should().Match<MagpiePass>(p => p.BlockWidth == 32 && p.BlockHeight == 24 && p.ThreadsX == 128);
            effect.Textures[1].WidthExpression.Should().Be("INPUT_WIDTH*2");
            effect.Textures.Should().Contain(t => t.Name == "featureMap1" && t.Format.Name == "R16G16B16A16_FLOAT");
        }

        [Fact]
        public void Settings_AreReadWithTheirRanges()
        {
            var rcas = MagpieEffects.Load(MagpieEffects.Rcas);

            rcas.Parameters.Should().ContainSingle().Which.Should().Be(new MagpieParameter("sharpness", "Sharpness", false, 0.87f, 0, 1, 0.01f));
            rcas.Textures[1].WidthExpression.Should().Be("INPUT_WIDTH", "sharpening keeps the size");
        }

        [Fact]
        public void PixelShaderStylePasses_AndOwnFormats_AreRead()
        {
            var ssim = MagpieEffects.Load(MagpieEffects.SSimDownscaler);

            ssim.Passes.Select(p => p.PsStyle).Should().Equal(true, true, true, false, false);
            ssim.Textures.Should().Contain(t => t.Name == "POSTKERNEL" && t.Format.Dxgi != MagpieFormat.Default.Dxgi, "its own format, not the default");
            MagpieEffects.Load(MagpieEffects.Easu).Textures[1].WidthExpression.Should().BeNull("EASU draws at whatever size it's given");
        }

        [Fact]
        public void TheGeneratedShaders_HaveMagpiesEntryPointsAndConstants()
        {
            var fsrcnnx = MagpieEffects.Load(MagpieEffects.FSRCNNX).GeneratePassSource(1);
            fsrcnnx.Should().Contain("[numthreads(128, 1, 1)]").And.Contain("Pass1(gid.xy * uint2(32, 24), tid);").And.Contain("MF4 MulAdd(MF4 x, MF4x4 y, MF4 a)");

            var ssim = MagpieEffects.Load(MagpieEffects.SSimDownscaler);
            var firstPass = ssim.GeneratePassSource(1);
            firstPass.Should().Contain("POSTKERNEL[gxy] = Pass1(pos);").And.Contain("__pass1OutputSize").And.Contain("float oversharp;");
        }

        [Theory]
        [InlineData("INPUT_WIDTH*2", 128)]
        [InlineData("(INPUT_WIDTH+1)/2", 32.5)]
        [InlineData("OUTPUT_HEIGHT-INPUT_HEIGHT*2", 0)]
        [InlineData("-2+INPUT_HEIGHT", 46)]
        public void SizeExpressions_Evaluate(string expression, double expected)
        {
            var values = new Dictionary<string, double> { ["INPUT_WIDTH"] = 64, ["INPUT_HEIGHT"] = 48, ["OUTPUT_WIDTH"] = 128, ["OUTPUT_HEIGHT"] = 96 };
            MagpieExpression.Evaluate(expression, values).Should().Be(expected);
        }

        [Fact]
        public void BrokenOrUnsupportedFiles_AreRefusedWithAReason()
        {
            const string pass = "//!PASS 1\n//!IN INPUT\n//!OUT OUTPUT\n//!BLOCK_SIZE 8\n//!NUM_THREADS 64\nvoid Pass1(uint2 b, uint3 t) {}\n";

            var notMagpie = () => MagpieEffect.Parse("Test", "float4 main() : SV_Target { return 0; }");
            notMagpie.Should().Throw<FormatException>().WithMessage("*MAGPIE EFFECT*");

            var noVersion = () => MagpieEffect.Parse("Test", "//!MAGPIE EFFECT\n\n" + pass);
            noVersion.Should().Throw<FormatException>().WithMessage("*VERSION*");

            var oldVersion = () => MagpieEffect.Parse("Test", "//!MAGPIE EFFECT\n//!VERSION 3\n\n" + pass);
            oldVersion.Should().Throw<FormatException>().WithMessage("*version 3*");

            var imageTexture = () => MagpieEffect.Parse("Test", "//!MAGPIE EFFECT\n//!VERSION 4\n\n//!TEXTURE\n//!SOURCE lut.dds\nTexture2D lut;\n\n" + pass);
            imageTexture.Should().Throw<NotSupportedException>().WithMessage("*lut*");

            var unknownSize = () => MagpieExpression.Evaluate("SCREEN_WIDTH", new Dictionary<string, double>());
            unknownSize.Should().Throw<FormatException>().WithMessage("*SCREEN_WIDTH*");
        }

        [Theory]
        [MemberData(nameof(BundledEffects))]
        public async Task EveryBundledEffect_Compiles(string name)
        {
            var compiled = await MagpieEffects.Compile(name);

            compiled.Passes.Should().HaveCount(compiled.Effect.Passes.Count).And.NotContainNulls();
        }

        // ===== Running them on the GPU =====

        private static float[] Ramp(float x, float y, uint width, uint height) =>
            [0.1f + 0.6f * x / width, 0.2f + 0.5f * y / height, 0.8f - 0.4f * (x + y) / (width + height)];

        [Theory]
        [InlineData(MagpieEffects.FSRCNNX, 64u, 48u, 128u, 96u)]          // Doubles
        [InlineData(MagpieEffects.FSRCNNXLineArt, 64u, 48u, 128u, 96u)]
        [InlineData(MagpieEffects.Easu, 64u, 48u, 96u, 72u)]              // Any enlarging
        [InlineData(MagpieEffects.SSimDownscaler, 128u, 96u, 64u, 48u)]   // Shrinks
        [InlineData(MagpieEffects.Rcas, 64u, 48u, 64u, 48u)]              // Sharpens at the same size
        public async Task ASmoothColourRamp_StaysTheSameRamp(string name, uint inWidth, uint inHeight, uint outWidth, uint outHeight)
        {
            var output = await Run(name, (x, y) => Ramp(x, y, inWidth, inHeight), inWidth, inHeight, outWidth, outHeight);

            output.GetLength(0).Should().Be((int)outHeight);
            output.GetLength(1).Should().Be((int)outWidth);

            double totalError = 0, maxError = 0;
            int count = 0, margin = (int)Math.Max(4, outWidth / 16);
            for (int oy = margin; oy < outHeight - margin; oy++)
            {
                for (int ox = margin; ox < outWidth - margin; ox++)
                {
                    // An output pixel's centre, in input pixels
                    var expected = Ramp((ox + 0.5f) * inWidth / outWidth - 0.5f, (oy + 0.5f) * inHeight / outHeight - 0.5f, inWidth, inHeight);
                    for (int c = 0; c < 3; c++)
                    {
                        float value = output[oy, ox][c];
                        float.IsFinite(value).Should().BeTrue();
                        double error = Math.Abs(value - expected[c]);
                        totalError += error;
                        maxError = Math.Max(maxError, error);
                        count++;
                    }
                }
            }

            (totalError / count).Should().BeLessThan(0.01, "a smooth ramp has no detail to invent or lose");
            maxError.Should().BeLessThan(0.05);
        }

        [Fact]
        public async Task FsrcnnxQuadrants_LandInTheRightPlaces()
        {
            // Dark top-left, brighter to the right, much brighter at the bottom: catches flipped or swapped axes
            static float[] Quadrants(float x, float y)
            {
                float v = 0.1f + (x >= 32 ? 0.2f : 0) + (y >= 24 ? 0.6f : 0);
                return [v, v, v];
            }

            var output = await Run(MagpieEffects.FSRCNNX, Quadrants, 64, 48, 128, 96);

            output[20, 20][1].Should().BeApproximately(0.1f, 0.03f);
            output[20, 108][1].Should().BeApproximately(0.3f, 0.03f);
            output[76, 20][1].Should().BeApproximately(0.7f, 0.03f);
            output[76, 108][1].Should().BeApproximately(0.9f, 0.03f);
            output[95, 127][3].Should().Be(1f, "the far corner is covered too");
        }

        [Fact]
        public async Task MoreSharpening_ChangesAnEdgeMore()
        {
            static float[] Edge(float x, float y) => x < 32 ? [0.3f, 0.3f, 0.3f] : [0.7f, 0.7f, 0.7f];

            var soft = await Run(MagpieEffects.Rcas, Edge, 64, 48, 64, 48, ("sharpness", 0f));
            var sharp = await Run(MagpieEffects.Rcas, Edge, 64, 48, 64, 48, ("sharpness", 1f));

            // Next to the edge: settings reach the shader through the constant buffer
            Math.Abs(soft[24, 31][0] - sharp[24, 31][0]).Should().BeGreaterThan(0.01f);
        }

        [Fact]
        public async Task Deband_SmoothsTheBandsInAGradient()
        {
            // A gentle gradient stored in 8 bits shows stair-step bands, one 8-bit step each
            const uint width = 128, height = 32;
            static float Ideal(float x) => 0.3f + 0.05f * x / width;
            static float Banded(float x) => MathF.Round(Ideal(x) * 255) / 255;

            var output = await Run(MagpieEffects.Deband, (x, y) => [Banded(x), Banded(x), Banded(x)], width, height, width, height, ("grain", 0f));

            double before = 0, after = 0;
            int count = 0;
            for (int y = 4; y < height - 4; y++)
            {
                for (int x = 20; x < width - 20; x++)
                {
                    before += Math.Abs(Banded(x) - Ideal(x));
                    after += Math.Abs(output[y, x][0] - Ideal(x));
                    count++;
                }
            }

            (after / count).Should().BeLessThan(before / count * 0.7, "the steps are smoothed back towards the real gradient");
        }

        [Fact]
        public void EightBitTextures_AreSixteenBit_SoHdrPicturesDontBand()
        {
            var effect = MagpieEffects.Load(MagpieEffects.SSimDownscaler);

            effect.Textures.Should().Contain(t => t.Name == "POSTKERNEL" && t.Format.Name == "R16G16B16A16_UNORM",
                "the file asks for R8G8B8A8_UNORM; 16-bit keeps its 0 to 1 range");
            effect.Textures.Should().NotContain(t => t.Format.Dxgi == Format.R8G8B8A8_UNorm);
        }

        [Fact]
        public async Task Deband_KeepsRealEdges()
        {
            static float[] Edge(float x, float y) => x < 32 ? [0.2f, 0.2f, 0.2f] : [0.8f, 0.8f, 0.8f];

            var output = await Run(MagpieEffects.Deband, Edge, 64, 32, 64, 32, ("grain", 0f));

            output[16, 28][0].Should().BeApproximately(0.2f, 0.002f, "an edge isn't a band");
            output[16, 36][0].Should().BeApproximately(0.8f, 0.002f);
        }

        /// <summary>Runs an effect on a picture given as colour at pixel (x, y). Returns [row, column][r, g, b, a].</summary>
        private static async Task<float[,][]> Run(string name, Func<float, float, float[]> picture, uint inWidth, uint inHeight,
            uint targetWidth, uint targetHeight, params (string Name, float Value)[] settings)
        {
            var compiled = await MagpieEffects.Compile(name);
            var (device, context) = CreateDevice();
            using (device)
            using (context)
            using (var input = new GpuTexture(device, inWidth, inHeight, Format.R16G16B16A16_Float, BindFlags.ShaderResource))
            using (var runner = new MagpieEffectRunner(device, compiled))
            {
                var pixels = new Half[inWidth * inHeight * 4];
                for (int y = 0; y < inHeight; y++)
                {
                    for (int x = 0; x < inWidth; x++)
                    {
                        var colour = picture(x, y);
                        int i = (int)((y * inWidth + x) * 4);
                        pixels[i] = (Half)colour[0];
                        pixels[i + 1] = (Half)colour[1];
                        pixels[i + 2] = (Half)colour[2];
                        pixels[i + 3] = (Half)1f;
                    }
                }
                context.UpdateSubresource(pixels, input.Texture, 0, inWidth * 8);

                foreach (var (setting, value) in settings)
                    runner.SetParameter(setting, value);
                runner.Resize(inWidth, inHeight, targetWidth, targetHeight);
                runner.Run(context, input.SRV);

                return ReadBack(device, context, runner.Output.Texture, runner.OutputWidth, runner.OutputHeight);
            }
        }

        internal static float[,][] ReadBack(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, uint width, uint height)
        {
            var description = texture.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;

            using var staging = device.CreateTexture2D(description);
            context.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, MapMode.Read);
            try
            {
                var pixels = new float[height, width][];
                for (int y = 0; y < height; y++)
                {
                    var row = new short[width * 4];
                    System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                        pixels[y, x] = Enumerable.Range(0, 4).Select(c => (float)BitConverter.Int16BitsToHalf(row[x * 4 + c])).ToArray();
                }
                return pixels;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        /// <summary>The real GPU, or Windows' software one (WARP) where there's none.</summary>
        internal static (ID3D11Device, ID3D11DeviceContext) CreateDevice()
        {
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, levels, out ID3D11Device? device, out ID3D11DeviceContext? context).Success)
                return (device!, context!);

            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, levels, out device, out context).CheckError();
            return (device!, context!);
        }
    }
}
