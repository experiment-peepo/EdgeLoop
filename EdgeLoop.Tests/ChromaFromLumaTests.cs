using FlyleafLib.MediaFramework.MediaRenderer.Effects;
using FluentAssertions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// The chroma-from-luma shader on the GPU: 4:2:0 planes like a hardware decoder's go in, colour at
    /// brightness resolution comes out.
    /// </summary>
    public class ChromaFromLumaTests
    {
        private const uint Width = 64, Height = 32;
        private static readonly float[] Left = [0.40f, 0.70f], Right = [0.60f, 0.30f];

        [Fact]
        public async Task AColourEdgeThatFollowsBrightness_ComesOutSharp()
        {
            // Darker red on the left, brighter blue on the right, with the edge between colour samples
            var output = await Run(x => x < 31 ? 0.3f : 0.7f, x => x < 31 ? Left : Right);

            output[16, 30][0].Should().BeApproximately(Left[0], 0.02f, "the pixel left of the edge keeps the left colour");
            output[16, 30][1].Should().BeApproximately(Left[1], 0.02f);
            output[16, 31][0].Should().BeApproximately(Right[0], 0.02f, "and the one right of it the right colour");
            output[16, 31][1].Should().BeApproximately(Right[1], 0.02f);
            output[16, 10][0].Should().BeApproximately(Left[0], 0.01f, "flat areas are untouched");
        }

        [Fact]
        public async Task AColourEdgeWithoutBrightness_KeepsTheUsualBlend()
        {
            // Brightness says nothing about where the colour changes, so nothing is invented
            var output = await Run(_ => 0.5f, x => x < 31 ? Left : Right);

            // Bilinear at pixel 30 is a quarter of colour sample 14 and three quarters of the mixed sample 15
            float blended = 0.25f * Left[0] + 0.75f * (Left[0] + Right[0]) / 2;
            output[16, 30][0].Should().BeApproximately(blended, 0.01f);
        }

        [Fact]
        public async Task TheShader_Compiles()
        {
            (await ChromaFromLuma.Compile()).Should().NotBeNull();
        }

        /// <summary>Makes 8-bit 4:2:0 planes from full-resolution brightness and colour, runs the shader, returns [row, column][u, v].</summary>
        private static async Task<float[,][]> Run(Func<int, float> luma, Func<int, float[]> colour)
        {
            var blob = await ChromaFromLuma.Compile();
            var (device, context) = MagpieEffectTests.CreateDevice();
            using (device)
            using (context)
            {
                var y = new byte[Width * Height];
                var uv = new byte[Width / 2 * (Height / 2) * 2];
                for (int row = 0; row < Height; row++)
                    for (int x = 0; x < Width; x++)
                        y[row * Width + x] = (byte)Math.Round(luma(x) * 255);
                for (int row = 0; row < Height / 2; row++)
                {
                    for (int x = 0; x < Width / 2; x++)
                    {
                        // Each colour sample is its 2x2 pixels' average, as the encoder made it
                        for (int c = 0; c < 2; c++)
                            uv[(row * (Width / 2) + x) * 2 + c] = (byte)Math.Round((colour(2 * x)[c] + colour(2 * x + 1)[c]) / 2 * 255);
                    }
                }

                using var yTexture = Texture(device, Width, Height, Format.R8_UNorm);
                using var uvTexture = Texture(device, Width / 2, Height / 2, Format.R8G8_UNorm);
                context.UpdateSubresource(y, yTexture, 0, Width);
                context.UpdateSubresource(uv, uvTexture, 0, Width);
                using var ySrv = ArrayView(device, yTexture, Format.R8_UNorm);
                using var uvSrv = ArrayView(device, uvTexture, Format.R8G8_UNorm);

                using var chroma = new ChromaFromLuma(device, blob);
                chroma.Run(context, ySrv, uvSrv, Width, Height);

                return ReadBack(device, context, chroma.Output.Texture);
            }
        }

        private static ID3D11Texture2D Texture(ID3D11Device device, uint width, uint height, Format format) => device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });

        // A one-slice array view, as the renderer makes for the decoder's texture array
        private static ID3D11ShaderResourceView ArrayView(ID3D11Device device, ID3D11Texture2D texture, Format format) =>
            device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Texture2DArray = new() { MipLevels = 1, ArraySize = 1, FirstArraySlice = 0 }
            });

        private static float[,][] ReadBack(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture)
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
                var pixels = new float[Height, Width][];
                for (int row = 0; row < Height; row++)
                {
                    var values = new short[Width * 2];
                    System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)(row * mapped.RowPitch), values, 0, values.Length);
                    for (int x = 0; x < Width; x++)
                        pixels[row, x] = [(float)BitConverter.Int16BitsToHalf(values[x * 2]), (float)BitConverter.Int16BitsToHalf(values[x * 2 + 1])];
                }
                return pixels;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }
}
