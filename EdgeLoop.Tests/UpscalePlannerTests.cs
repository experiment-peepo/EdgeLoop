using FlyleafLib.MediaFramework.MediaRenderer.Effects;
using AwesomeAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>Which effects run for which video and screen sizes, and when a slow graphics card steps down.</summary>
    public class UpscalePlannerTests
    {
        private static string Plan(uint width, uint height, uint targetWidth, uint targetHeight,
            ShaderUpscaler upscaler = ShaderUpscaler.Sharp, bool upscale = true, bool deband = false, bool sharpen = false) =>
            UpscalePlanner.Describe(UpscalePlanner.Plan(width, height, targetWidth, targetHeight, upscale, upscaler, deband, sharpen));

        [Fact]
        public void Video720p_OnA1080pScreen_IsDoubledThenShrunk()
        {
            Plan(1280, 720, 1920, 1080).Should().Be("FSRCNNX 2560x1440 → shrink 1920x1080");
        }

        [Fact]
        public void Video720p_OnA1440pScreen_IsDoubledExactly()
        {
            Plan(1280, 720, 2560, 1440).Should().Be("FSRCNNX 2560x1440");
        }

        [Fact]
        public void Video720p_OnA4KScreen_IsDoubledOnce_ThenFsr1DoesTheRest()
        {
            Plan(1280, 720, 3840, 2160).Should().Be("FSRCNNX 2560x1440 → FSR 1 3840x2160", "a second doubling would overshoot to 2880p");
        }

        [Fact]
        public void Video480p_OnA4KScreen_IsDoubledTwice()
        {
            Plan(854, 480, 3840, 2160).Should().Be("FSRCNNX 1708x960 → FSRCNNX 3416x1920 → FSR 1 3840x2160");
        }

        [Fact]
        public void ASmallEnlargement_IsLeftToFsr1()
        {
            Plan(1800, 1000, 1944, 1080).Should().Be("FSR 1 1944x1080", "doubling for 8% more would waste the graphics card");
        }

        [Fact]
        public void VideoAlreadyBigEnough_IsNotUpscaled()
        {
            Plan(1920, 1080, 1920, 1080).Should().BeEmpty();
            Plan(1280, 720, 960, 540).Should().BeEmpty("shrinking isn't upscaling");
        }

        [Fact]
        public void Fast_IsFsr1Alone_AndLineArt_UsesItsOwnModel()
        {
            Plan(1280, 720, 1920, 1080, ShaderUpscaler.Fast).Should().Be("FSR 1 1920x1080");
            Plan(1280, 720, 2560, 1440, ShaderUpscaler.LineArt).Should().Be("FSRCNNX line art 2560x1440");
        }

        [Fact]
        public void Deband_ComesFirst_AndSharpening_Last()
        {
            Plan(1280, 720, 1920, 1080, deband: true, sharpen: true).Should().Be("deband → FSRCNNX 2560x1440 → shrink 1920x1080 → sharpen");
        }

        [Fact]
        public void WithoutUpscaling_DebandStillRuns_AtTheVideosSize()
        {
            var steps = UpscalePlanner.Plan(1920, 1080, 1920, 1080, upscale: false, ShaderUpscaler.Sharp, deband: true, sharpen: false);

            steps.Should().ContainSingle().Which.Should().Be(new UpscaleStep(MagpieEffects.Deband, 1920, 1080, 1920, 1080));
        }

        [Theory]
        [InlineData(9, 60, true)]    // More than half of a 60 fps frame (16.7 ms)
        [InlineData(8, 60, false)]
        [InlineData(16, 30, false)]  // A 30 fps frame has twice the time
        [InlineData(9, 0, true)]     // Frame rate unknown: assume 60
        public void TheBudget_IsHalfOfEachFrame(double ms, double fps, bool over)
        {
            UpscaleBudget.IsOver(ms, fps).Should().Be(over);
        }
    }
}
