using FlyleafLib.MediaFramework.MediaRenderer;
using FluentAssertions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// The FSRCNNX shader upscaler on a real GPU, or Windows' software one where there's none (CI):
    /// a known picture goes in and the doubled picture is checked.
    /// </summary>
    public class FsrcnnxTests
    {
        private const uint Width = 64, Height = 48;

        [Fact]
        public async Task AllSixPasses_Compile()
        {
            (await ShaderCompiler.FSRCNNXBlobs()).Should().HaveCount(6).And.NotContainNulls();
        }

        [Fact]
        public async Task ASmoothColourRamp_StaysTheSameRamp_AtTwiceTheSize()
        {
            static float[] Ramp(float x, float y) =>
                [0.1f + 0.6f * x / Width, 0.2f + 0.5f * y / Height, 0.8f - 0.4f * (x + y) / (Width + Height)];

            var output = Upscale(Ramp, await ShaderCompiler.FSRCNNXBlobs());

            double totalError = 0, maxError = 0;
            int count = 0;
            for (int oy = 8; oy < Height * 2 - 8; oy++)
            {
                for (int ox = 8; ox < Width * 2 - 8; ox++)
                {
                    // An output pixel's centre, in input pixels
                    var expected = Ramp((ox + 0.5f) / 2 - 0.5f, (oy + 0.5f) / 2 - 0.5f);
                    for (int c = 0; c < 3; c++)
                    {
                        double error = Math.Abs(output[oy, ox][c] - expected[c]);
                        totalError += error;
                        maxError = Math.Max(maxError, error);
                        count++;
                    }
                }
            }

            (totalError / count).Should().BeLessThan(0.01, "a smooth ramp has no detail to invent");
            maxError.Should().BeLessThan(0.05);
        }

        [Fact]
        public async Task Quadrants_LandInTheRightPlaces()
        {
            // Dark top-left, brighter to the right, much brighter at the bottom: catches flipped or swapped axes
            static float[] Quadrants(float x, float y)
            {
                float v = 0.1f + (x >= Width / 2 ? 0.2f : 0) + (y >= Height / 2 ? 0.6f : 0);
                return [v, v, v];
            }

            var output = Upscale(Quadrants, await ShaderCompiler.FSRCNNXBlobs());

            output[20, 20][1].Should().BeApproximately(0.1f, 0.03f);
            output[20, 108][1].Should().BeApproximately(0.3f, 0.03f);
            output[76, 20][1].Should().BeApproximately(0.7f, 0.03f);
            output[76, 108][1].Should().BeApproximately(0.9f, 0.03f);
            output[95, 127][3].Should().Be(1f, "the far corner is covered too");
        }

        /// <summary>Doubles a picture given as colour at pixel (x, y). Returns [row, column][r, g, b, a].</summary>
        private static float[,][] Upscale(Func<float, float, float[]> picture, Vortice.Direct3D.Blob[] shaders)
        {
            var (device, context) = CreateDevice();
            using (device)
            using (context)
            using (var point = device.CreateSamplerState(Sampler(Filter.MinMagMipPoint)))
            using (var linear = device.CreateSamplerState(Sampler(Filter.MinMagMipLinear)))
            using (var upscaler = new FsrcnnxUpscaler(device, shaders))
            {
                upscaler.Resize(context, Width, Height);

                var input = new Half[Width * Height * 4];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        var colour = picture(x, y);
                        int i = (int)((y * Width + x) * 4);
                        input[i] = (Half)colour[0];
                        input[i + 1] = (Half)colour[1];
                        input[i + 2] = (Half)colour[2];
                        input[i + 3] = (Half)1f;
                    }
                }
                context.UpdateSubresource(input, upscaler.InputTexture, 0, Width * 8);

                upscaler.Run(context, point, linear);

                return ReadBack(device, context, upscaler.OutputTexture, Width * 2, Height * 2);
            }
        }

        private static float[,][] ReadBack(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, uint width, uint height)
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

        private static (ID3D11Device, ID3D11DeviceContext) CreateDevice()
        {
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, levels, out ID3D11Device? device, out ID3D11DeviceContext? context).Success)
                return (device!, context!);

            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, levels, out device, out context).CheckError();
            return (device!, context!);
        }

        private static SamplerDescription Sampler(Filter filter) => new()
        {
            Filter = filter,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue
        };
    }
}
