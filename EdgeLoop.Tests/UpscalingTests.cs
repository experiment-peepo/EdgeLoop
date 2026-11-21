using System.Text.Json;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;
using static EdgeLoop.Classes.UpscalingSupport;

namespace EdgeLoop.Tests
{
    public class UpscalingTests : TestBase
    {
        [Fact]
        public void TheUpscalingChoice_IsSavedAsOnOffAndAHeightLimit()
        {
            MockSettings.SetupProperty(s => s.EnableSuperResolution, false);
            MockSettings.SetupProperty(s => s.SuperResolutionMaxHeight, 0);
            var settings = new SettingsViewModel(_ => true, new[] { new ScreenViewer(null) { DeviceName = @"\.\DISPLAY1" } });
            settings.Upscaling.Should().Be(-1, "it starts off");
            settings.UpscalingDescription.Should().Contain("FSRCNNX");

            settings.Upscaling = 1080;
            settings.HasChanges.Should().BeTrue();
            settings.Save();

            MockSettings.Object.EnableSuperResolution.Should().BeTrue();
            MockSettings.Object.SuperResolutionMaxHeight.Should().Be(1080);

            settings.Upscaling = -1;
            settings.Save();
            MockSettings.Object.EnableSuperResolution.Should().BeFalse();
        }

        [Fact]
        public void Players_UpscaleWithTheShader_NeverTheDriver()
        {
            MockSettings.SetupProperty(s => s.EnableSuperResolution, true);
            MockSettings.SetupProperty(s => s.SuperResolutionMaxHeight, 720);

            using var player = new HypnoViewModel(MockExtractor.Object);

            player.Player.Config.Video.ShaderUpscaling.Should().BeTrue();
            player.Player.Config.Video.SuperResolution.Should().BeFalse("the driver's upscaler isn't used");
            player.Player.Config.Video.ShaderUpscalingMaxHeight.Should().Be(720);

            MockSettings.Object.EnableSuperResolution = false;
            player.RefreshSuperResolution();
            player.Player.Config.Video.ShaderUpscaling.Should().BeFalse();
        }

        [Fact]
        public void OldSettingsFiles_StillLoad_AndDriverUpscalingBecomesFsrcnnx()
        {
            // Saved by earlier test builds, with the driver upscaler chosen and options that no longer exist
            const string json = """{ "EnableSuperResolution": true, "SuperResolutionMaxHeight": 1080, "UseShaderUpscaling": false, "UpscaleTo4K": true }""";

            var settings = JsonSerializer.Deserialize<UserSettings>(json)!;
            var video = new FlyleafLib.Config().Video;
            Apply(video, settings);

            video.ShaderUpscaling.Should().BeTrue();
            video.SuperResolution.Should().BeFalse();
            video.ShaderUpscalingMaxHeight.Should().Be(1080);
        }

        [Fact]
        public void TheOverlay_SaysWhereFsrcnnxUpscalesTo_AndTheCost()
        {
            Status(running: true, "2560x1440", 0.9, null).Should().Be($"✦ FSRCNNX to 2560x1440 · {0.9:F2} ms per frame");
            Status(running: true, "2560x1440", 0, null).Should().Be("✦ FSRCNNX to 2560x1440", "nothing measured yet");
            Status(running: false, null, 0, "HDR output").Should().Be("FSRCNNX not running: HDR output");
            Status(running: false, null, 0, null).Should().BeNull("upscaling is off");
        }
    }
}
