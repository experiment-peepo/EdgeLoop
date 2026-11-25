using System.Text.Json;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FlyleafLib.MediaFramework.MediaRenderer.Effects;
using AwesomeAssertions;
using static EdgeLoop.Classes.UpscalingSupport;

namespace EdgeLoop.Tests
{
    public class UpscalingTests : TestBase
    {
        private static SettingsViewModel CreateSettings() =>
            new(_ => true, new[] { new ScreenViewer(null) { DeviceName = @"\.\DISPLAY1" } });

        [Fact]
        public void TheUpscalingMode_IsSaved()
        {
            MockSettings.SetupProperty(s => s.EnableSuperResolution, false);
            MockSettings.SetupProperty(s => s.UpscalingQuality, "Sharp");
            var settings = CreateSettings();
            settings.UpscalingMode.Should().Be(Off, "it starts off");
            settings.UpscalingModes.Select(m => m.Key).Should().Equal(Off, "Fast", "Sharp", "LineArt");

            settings.UpscalingMode = "LineArt";
            settings.HasChanges.Should().BeTrue();
            settings.Save();

            MockSettings.Object.EnableSuperResolution.Should().BeTrue();
            MockSettings.Object.UpscalingQuality.Should().Be("LineArt");

            settings.UpscalingMode = Off;
            settings.Save();
            MockSettings.Object.EnableSuperResolution.Should().BeFalse();
            MockSettings.Object.UpscalingQuality.Should().Be("LineArt", "turning it back on remembers how");
        }

        [Theory]
        [InlineData("Fast", ShaderUpscaler.Fast, 0.6f)]
        [InlineData("Sharp", ShaderUpscaler.Sharp, 0.87f)]
        [InlineData("LineArt", ShaderUpscaler.LineArt, 0.35f)]
        public void EachMode_BringsItsSharpeningAndDebanding(string mode, ShaderUpscaler upscaler, float sharpening)
        {
            MockSettings.SetupProperty(s => s.EnableSuperResolution, true);
            MockSettings.SetupProperty(s => s.UpscalingQuality, mode);

            using var player = new HypnoViewModel(MockExtractor.Object);
            var video = player.Player.Config.Video;

            video.ShaderUpscaling.Should().BeTrue();
            video.SuperResolution.Should().BeFalse("the driver's upscaler isn't used");
            video.ShaderUpscalingMaxHeight.Should().Be(0, "every video smaller than its screen is upscaled");
            video.ShaderUpscaler.Should().Be(upscaler);
            video.ShaderSharpening.Should().Be(sharpening);
            video.ShaderDeband.Should().BeTrue();
            video.ShaderChromaFromLuma.Should().BeTrue("every mode gets sharper colour");

            MockSettings.Object.EnableSuperResolution = false;
            player.RefreshSuperResolution();
            video.ShaderUpscaling.Should().BeFalse();
            video.ShaderSharpening.Should().Be(0);
            video.ShaderDeband.Should().BeFalse("off is off");
            video.ShaderChromaFromLuma.Should().BeFalse();
        }

        [Fact]
        public void DiagnosticMode_ShowsTheUpscalingLine_AsSoonAsItsSwitchedOn()
        {
            MockSettings.SetupProperty(s => s.EnableSuperResolution, true);
            MockSettings.SetupProperty(s => s.EnableDiagnosticMode, false);
            using var player = new HypnoViewModel(MockExtractor.Object);
            player.Player.Stats.Should().BeFalse();

            MockSettings.Object.EnableDiagnosticMode = true;
            player.RefreshSuperResolution(); // What saving Settings does for every playing screen

            player.Player.Stats.Should().BeTrue("the corner details appear without restarting playback");
        }

        [Fact]
        public void OldSettingsFiles_StillLoad_AsSharpUpscaling()
        {
            // Saved by earlier test builds, with options that no longer exist
            const string json = """{ "EnableSuperResolution": true, "SuperResolutionMaxHeight": 1080, "UseShaderUpscaling": false, "UpscaleTo4K": true, "UpscalingSharpening": 1, "SmoothBanding": false }""";

            var settings = JsonSerializer.Deserialize<UserSettings>(json)!;
            var video = new FlyleafLib.Config().Video;
            Apply(video, settings);

            video.ShaderUpscaling.Should().BeTrue();
            video.ShaderUpscaler.Should().Be(ShaderUpscaler.Sharp);
            video.ShaderUpscalingMaxHeight.Should().Be(0, "the size limit is gone");
            video.ShaderSharpening.Should().Be(0.87f, "Sharp's own sharpening");
            video.ShaderDeband.Should().BeTrue();
        }

        [Theory]
        [InlineData("Fast", ShaderUpscaler.Fast)]
        [InlineData("LineArt", ShaderUpscaler.LineArt)]
        [InlineData("Sharp", ShaderUpscaler.Sharp)]
        [InlineData(null, ShaderUpscaler.Sharp)]
        [InlineData("Turbo", ShaderUpscaler.Sharp)]
        [InlineData("7", ShaderUpscaler.Sharp)]
        public void SavedQualities_AreReadSafely(string? quality, ShaderUpscaler expected)
        {
            ParseUpscaler(quality!).Should().Be(expected);
        }

        [Fact]
        public void TheOverlay_SaysWhatRuns_AndTheCost()
        {
            Status(running: true, "FSRCNNX 2560x1440 → shrink 1920x1080", 1.3, null)
                .Should().Be($"✦ FSRCNNX 2560x1440 → shrink 1920x1080 · {1.3:F2} ms per frame");
            Status(running: true, "deband", 0, null).Should().Be("✦ deband", "nothing measured yet");
            Status(running: false, null, 0, "HDR output").Should().Be("Upscaling not running: HDR output");
            Status(running: false, null, 0, null).Should().BeNull("upscaling is off");
        }

        [Fact]
        public void SteppingDown_IsExplained()
        {
            LoweredNotice("Fast (FSR 1)").Headline.Should().Be("Upscaling lowered to Fast (FSR 1) on this screen");
            LoweredNotice(Off).Headline.Should().Be("Upscaling switched off on this screen");
            LoweredNotice(Off).Detail.Should().Contain("back when playback starts again");
        }
    }
}
