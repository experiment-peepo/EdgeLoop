using FlyleafLib.MediaFramework.MediaRenderer.Effects;
using FluentAssertions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// Upscaling with HDR output, on the GPU: the renderer's scRGB picture becomes PQ for the effects, and comes back
    /// unchanged when it's drawn onto the screen.
    /// </summary>
    public class HdrPictureTests
    {
        // scRGB: 1.0 = 80 nits, BT.709 primaries
        private static readonly float[][] Pixels =
        [
            [0f, 0f, 0f],
            [1f, 1f, 1f],             // 80 nits white
            [12.5f, 12.5f, 12.5f],    // 1000 nits white
            [-0.2f, 1.2f, -0.05f],    // A green outside BT.709
            [3f, 0.5f, 0.1f],
            [125f, 125f, 125f],       // 10000 nits, PQ's top
        ];

        [Fact]
        public async Task ThePicture_BecomesPQ_ForTheEffects()
        {
            var (pq, _) = await Run();

            pq[0, 0][0].Should().BeApproximately(0f, 0.001f);
            pq[0, 1][0].Should().BeApproximately(Pq(80), 0.001f, "white keeps its 80 nits");
            pq[0, 1][1].Should().BeApproximately(Pq(80), 0.001f);
            pq[0, 2][0].Should().BeApproximately(Pq(1000), 0.001f);
            pq[0, 5][0].Should().BeApproximately(1f, 0.001f);
            for (int x = 0; x < Pixels.Length; x++)
                pq[0, x].Take(3).Should().OnlyContain(v => v >= 0f && v <= 1f, "every colour fits the effects' 0 to 1, even ones outside BT.709");
        }

        [Fact]
        public async Task ThePQPicture_ComesBackUnchanged_OnTheScreen()
        {
            var (_, screen) = await Run();

            for (int x = 0; x < Pixels.Length; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float expected = Pixels[x][c];
                    screen[0, x][c].Should().BeApproximately(expected, 0.004f + Math.Abs(expected) * 0.01f, $"pixel {x}, channel {c}");
                }
                screen[0, x][3].Should().BeApproximately(1f, 0.001f);
            }
        }

        /// <summary>ToPQ on <see cref="Pixels"/>, then FromPQ on its result, at the same size.</summary>
        private static async Task<(float[,][] PQ, float[,][] Screen)> Run()
        {
            var blobs = await HdrPicture.Compile();
            var (device, context) = MagpieEffectTests.CreateDevice();
            uint width = (uint)Pixels.Length;
            using (device)
            using (context)
            using (var input = new GpuTexture(device, width, 1, Format.R16G16B16A16_Float, BindFlags.ShaderResource))
            using (var screen = new GpuTexture(device, width, 1, Format.R16G16B16A16_Float, BindFlags.ShaderResource | BindFlags.RenderTarget))
            using (var point = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue
            }))
            using (var hdr = new HdrPicture(device, blobs))
            {
                var data = Pixels.SelectMany(p => new[] { (Half)p[0], (Half)p[1], (Half)p[2], (Half)1f }).ToArray();
                context.UpdateSubresource(data, input.Texture, 0, width * 8);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.PSSetSampler(0, point);

                hdr.ToPQ(context, input.SRV, width, 1);

                context.OMSetRenderTargets(screen.RTV);
                context.RSSetViewport(new Viewport(0, 0, width, 1));
                hdr.FromPQ(context, hdr.Output.SRV);
                context.OMSetRenderTargets((ID3D11RenderTargetView)null!);

                return (MagpieEffectTests.ReadBack(device, context, hdr.Output.Texture, width, 1),
                        MagpieEffectTests.ReadBack(device, context, screen.Texture, width, 1));
            }
        }

        /// <summary>SMPTE ST 2084, for nits.</summary>
        private static float Pq(double nits)
        {
            const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
            double y = Math.Pow(nits / 10000, m1);
            return (float)Math.Pow((c1 + c2 * y) / (1 + c3 * y), m2);
        }
    }
}
