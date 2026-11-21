using System.IO;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// End-to-end check that the bundled FFmpeg DLLs load through the FlyleafLib bindings and can
    /// demux and decode real media. The other tests never start the engine, so an FFmpeg/bindings
    /// mismatch (e.g. after upgrading FlyleafLib or the DLLs) would otherwise go unnoticed.
    /// </summary>
    [Collection(EngineCollection.Name)]
    public class FFmpegPlaybackTests : TestBase
    {
        private static readonly string SamplePath =
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample_h264_aac.mp4");

        private static void EnsureEngineStarted()
        {
            if (Engine.IsLoaded)
                return;

            Engine.Start(new EngineConfig
            {
                FFmpegPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg"),
                PluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins"),
                UIRefresh = false,
                LogLevel = FlyleafLib.LogLevel.Warn,
                LogOutput = ":debug",
            });
        }

        [Fact]
        public void Engine_LoadsBundledFFmpeg9()
        {
            EnsureEngineStarted();

            Engine.IsLoaded.Should().BeTrue();
            Engine.FFmpeg.Version.Should().StartWith("63.", "the bundled avformat-63.dll is FFmpeg 9");
        }

        [Fact]
        public async Task Player_OpensAndDecodesLocalVideo()
        {
            File.Exists(SamplePath).Should().BeTrue($"test clip should be copied to {SamplePath}");
            EnsureEngineStarted();

            var config = new Config();
            config.Playback.AutoPlay = false;
            using var player = new Player(config);

            // Act: open
            var opened = player.Open(SamplePath);

            // Assert: demux + codec open through the FFmpeg 9 bindings
            opened.Success.Should().BeTrue(opened.Error);
            player.Video.IsOpened.Should().BeTrue();
            player.Video.Codec.Should().Be("h264");
            var stream = player.decoder.VideoStream;
            stream.Width.Should().Be(320u);
            stream.Height.Should().Be(240u);
            player.Audio.Codec.Should().Be("aac");
            player.Duration.Should().BeInRange(TimeSpan.FromSeconds(1.8).Ticks, TimeSpan.FromSeconds(2.2).Ticks);

            // Act: play and let the decode/playback loop advance
            player.Play();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (player.CurTime < TimeSpan.FromMilliseconds(500).Ticks && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            // Assert: frames were decoded and the clock moved
            player.CurTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500).Ticks,
                "playback should advance through decoded frames");

            player.Stop();
        }

        [Fact]
        public async Task ShaderUpscaling_UsesTheFlyleafProcessor_EvenWhenD3D11IsPreferred()
        {
            EnsureEngineStarted();

            var config = new Config();
            config.Playback.AutoPlay = false;
            config.Video.ShaderUpscaling = true;
            using var player = new Player(config);
            player.Renderer.PreferredVideoProcessor = VideoProcessors.D3D11; // What EdgeLoop's players ask for

            var opened = player.Open(SamplePath);
            opened.Success.Should().BeTrue(opened.Error);

            // The processor is chosen once frames are decoding
            player.Play();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (player.Renderer.VideoProcessor == VideoProcessors.Auto && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            player.Stop();

            player.Renderer.VideoProcessor.Should().Be(VideoProcessors.Flyleaf, "FSRCNNX runs in the Flyleaf processor's shaders");
        }

        [Fact]
        public void DurationProbe_ReadsTheClipsLength()
        {
            EnsureEngineStarted();

            var duration = EdgeLoop.Classes.DurationProbe.ProbeWithFFmpeg(SamplePath);

            duration.Should().NotBeNull();
            duration!.Value.TotalSeconds.Should().BeApproximately(2, 0.2, "the bundled clip is 2 seconds long");
        }
    }
}
