using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Tests.Stability {
    public class NicheSiteBugsTests : TestBase {
        
        [Fact]
        public async Task Rule34Video_TokenInjection_ShouldMaintainExistingParams() {
            // Arrange
            var mockFetcher = new Mock<IHtmlFetcher>();
            var html = @"
                <html>
                <script>
                    var video_url = 'https://cdn.r34.com/v/12345.mp4?existing=param';
                    var rnd = '98765';
                    var license_code = 'secret_code';
                </script>
                </html>";
            
            mockFetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(html);
            
            var extractor = new VideoUrlExtractor(mockFetcher.Object);
            
            // Act
            var result = await extractor.ExtractVideoUrlAsync("https://rule34video.com/video/12345/test-slug");
            
            // Assert
            result.Should().Contain("existing=param");
            result.Should().Contain("rnd=98765");
            result.Should().Contain("license_code=secret_code");
        }

        [Fact]
        public async Task PMVHaven_NestedLDJSON_ShouldExtractCorrectUrl() {
            // Arrange
            var mockFetcher = new Mock<IHtmlFetcher>();
            var html = @"
                <html>
                <script type=""application/ld+json"">
                {
                    ""@context"": ""https://schema.org"",
                    ""@type"": ""VideoObject"",
                    ""nested"": {
                        ""contentUrl"": ""https://video.pmvhaven.com/streams/master.m3u8""
                    }
                }
                </script>
                </html>";
            
            mockFetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(html);
            
            var extractor = new VideoUrlExtractor(mockFetcher.Object);
            
            // Act
            var result = await extractor.ExtractVideoUrlAsync("https://pmvhaven.com/videos/some-video");
            
            // Assert
            result.Should().Be("https://video.pmvhaven.com/streams/master.m3u8");
        }

        [Fact]
        public async Task Hypnotube_PlayerConfigFallback_ShouldWorkWhenTagsFail() {
            // Arrange
            var mockFetcher = new Mock<IHtmlFetcher>();
            // No <video> or <source> tags, only JS config
            var html = @"
                <html>
                <body>
                    <div id=""player""></div>
                    <script>
                        new Player({
                            file: 'https://media.hypnotube.com/v/fallback_video.mp4',
                            title: 'Fallback'
                        });
                    </script>
                </body>
                </html>";
            
            mockFetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(html);
            
            var extractor = new VideoUrlExtractor(mockFetcher.Object);
            
            // Act
            var result = await extractor.ExtractVideoUrlAsync("https://hypnotube.com/video/12345.html");
            
            // Assert
            result.Should().Be("https://media.hypnotube.com/v/fallback_video.mp4");
        }

        [Fact]
        public async Task ConcurrentExtraction_ShouldCoalesceCalls() {
            // Arrange
            PersistentUrlCache.Instance.Clear();
            var mockFetcher = new Mock<IHtmlFetcher>();
            int fetchCount = 0;
            
            mockFetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string url, CancellationToken ct) => {
                    Interlocked.Increment(ref fetchCount);
                    await Task.Delay(100); // Simulate network latency
                    return "<html><video src='video.mp4'></video></html>";
                });
            
            var extractor = new VideoUrlExtractor(mockFetcher.Object);
            var url = "https://pmvhaven.com/videos/concurrent-test";
            
            // Act
            // Fire multiple extractions for the same URL simultaneously
            var tasks = Enumerable.Range(0, 5)
                .Select(_ => extractor.ExtractVideoUrlAsync(url))
                .ToList();
            
            await Task.WhenAll(tasks);
            
            // Assert
            fetchCount.Should().Be(1, "Multiple concurrent requests for the same URL should only trigger ONE network fetch");
            tasks.All(t => t.IsCompletedSuccessfully).Should().BeTrue();
            
            foreach (var task in tasks) {
                var result = await task;
                result.Should().NotBeNull();
                result.Should().Contain("video.mp4");
            }
        }

        [Fact]
        public async Task OpeningStall_RemoteUrl_ShouldUse60sTimeout() {
            // Arrange
            var mockExtractor = new Mock<IVideoUrlExtractor>();
            
            // Fast extraction 
            mockExtractor.Setup(x => x.ExtractVideoUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("https://pmvhaven.com/slow-stream.m3u8");

            var vm = new HypnoViewModel(mockExtractor.Object);
            var item = new VideoItem("https://pmvhaven.com/video/slow");
            item.Validate(); 

            bool timeoutTriggered = false;
            vm.MediaFailed += (s, e) => {
                if (e.Exception is TimeoutException) timeoutTriggered = true;
            };

            // Act
            vm.SetQueue(new[] { item });
            
            // Wait for extraction to complete and Player.Open to be called
            await Task.Delay(1000);
            
            // Periodically force status to Opening to fighting Flyleaf's internal failure thread
            using (var cts = new CancellationTokenSource()) {
                var task = Task.Run(async () => {
                    var isLoadingField = typeof(HypnoViewModel).GetField("_isLoading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var playerStatusField = typeof(FlyleafLib.MediaPlayer.Player).GetField("status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    while (!cts.IsCancellationRequested) {
                        vm.Player.Status = FlyleafLib.MediaPlayer.Status.Opening;
                        playerStatusField?.SetValue(vm.Player, FlyleafLib.MediaPlayer.Status.Opening);
                        isLoadingField?.SetValue(vm, true);
                        await Task.Delay(50);
                    }
                });

                // Assert
                // Wait 45 seconds (more than old 30s)
                await Task.Delay(45000);
                timeoutTriggered.Should().BeFalse("Timeout should NOT trigger at 45s because we increased it to 60s for URLs");

                // Wait another 20 seconds (total 65s+)
                await Task.Delay(20000);
                
                cts.Cancel();
                await task;
            }
            
            // Assert
            timeoutTriggered.Should().BeTrue("Timeout SHOULD trigger after 60s for stalled URL extraction");
        }
        
        [Fact]
        public async Task Rule34Video_DirectDownloadPriority_ShouldSelectHighestQuality() {
             // Arrange
            var mockFetcher = new Mock<IHtmlFetcher>();
            var html = @"
                <html>
                <body>
                    <a href='/get_file/1/abc/480p.mp4/'>Download 480p</a>
                    <a href='/get_file/1/abc/1080p.mp4/'>Download 1080p</a>
                    <a href='/get_file/1/abc/720p.mp4/'>Download 720p</a>
                </body>
                </html>";
            
            mockFetcher.Setup(f => f.FetchHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(html);
            
            var extractor = new VideoUrlExtractor(mockFetcher.Object);
            
            // Act
            var result = await extractor.ExtractVideoUrlAsync("https://rule34video.com/video/123/test");
            
            // Assert
            result.Should().Contain("1080p.mp4");
        }
    }
}

