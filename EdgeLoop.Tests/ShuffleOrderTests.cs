using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;
using Moq;

namespace EdgeLoop.Tests
{
    public class ShuffleOrderTests : TestBase
    {
        [Fact]
        public void EachRound_PlaysEveryVideoOnce_AndDoesNotRepeatAcrossTheBoundary()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var order = new ShuffleOrder(new Random(seed));
                var played = Enumerable.Range(0, 15).Select(_ => order.Next(5, -1)).ToList();

                played.Take(5).Should().BeEquivalentTo(Enumerable.Range(0, 5), $"seed {seed}: round one plays each video once");
                played.Skip(5).Take(5).Should().BeEquivalentTo(Enumerable.Range(0, 5), $"seed {seed}: round two plays each video once");
                played[5].Should().NotBe(played[4], $"seed {seed}: a round doesn't start with the video that just ended");
                played[10].Should().NotBe(played[9], $"seed {seed}");
            }
        }

        [Fact]
        public void FirstRound_DoesNotStartWithTheVideoOnScreen()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var order = new ShuffleOrder(new Random(seed));
                order.Next(4, current: 2).Should().NotBe(2, $"seed {seed}: shuffle just turned on while video 2 plays");
            }
        }

        [Fact]
        public void Back_RetracesWhatWasPlayed_AcrossRounds_ThenForwardReplaysIt()
        {
            var order = new ShuffleOrder(new Random(7));
            var played = Enumerable.Range(0, 6).Select(_ => order.Next(4, -1)).ToList(); // A round and a half

            // Back through the second round and into the first
            var back = Enumerable.Range(0, 5).Select(_ => order.Back(4)).ToList();
            back.Should().Equal(played[4], played[3], played[2], played[1], played[0]);

            order.Back(4).Should().Be(-1, "nothing played before the first video");

            // Forward again replays the same videos in the same order, including the round stepped back from
            var forward = Enumerable.Range(0, 5).Select(_ => order.Next(4, -1)).ToList();
            forward.Should().Equal(played[1], played[2], played[3], played[4], played[5]);
        }

        [Fact]
        public void Upcoming_IsWhatNextThenPlays_IntoTheNextRound()
        {
            var order = new ShuffleOrder(new Random(3));
            order.Next(5, -1);
            order.Next(5, -1);
            order.Next(5, -1); // Two left in this round

            var upcoming = order.Upcoming(5, 3);
            upcoming.Should().HaveCount(3);

            var played = Enumerable.Range(0, 3).Select(_ => order.Next(5, -1)).ToList();
            played.Should().Equal(upcoming, "pre-buffering must fetch the videos that really play next");
        }

        [Fact]
        public void StartAt_PlaysThatVideoFirst_AndMoveTo_CarriesOnFromAJump()
        {
            var order = new ShuffleOrder(new Random(11));
            order.StartAt(6, 4);
            order.Position.Should().Be(0);
            order.Upcoming(6, 5).Should().NotContain(4).And.HaveCount(5);

            var rest = order.Upcoming(6, 5);
            order.MoveTo(6, rest[2]); // Picked by hand
            order.Upcoming(6, 2).Should().Equal(rest[3], rest[4]);
            order.Next(6, rest[2]).Should().Be(rest[3], "the round carries on after the picked video");
        }

        [Fact]
        public void PlaylistChange_StartsAFreshOrder()
        {
            var order = new ShuffleOrder(new Random(5));
            order.Next(3, -1);
            order.Next(3, -1);

            order.Back(4).Should().Be(-1, "the order was for a 3-video playlist");
            var next = Enumerable.Range(0, 4).Select(_ => order.Next(4, -1)).ToList();
            next.Should().BeEquivalentTo(Enumerable.Range(0, 4));
        }

        // ---- Through the player ----

        [Fact]
        public async Task WithShuffle_PreBufferFetchesTheVideoThatPlaysNext_AndPreviousGoesBack()
        {
            MockSettings.Setup(s => s.VideoShuffle).Returns(true);
            var downloads = new Mock<IVideoDownloadService>();
            var requested = new List<string>();
            downloads.Setup(d => d.DownloadVideoAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<string, Dictionary<string, string>, CancellationToken, string>((url, _, _, _) => { lock (requested) requested.Add(url); })
                .ReturnsAsync((string?)null);
            ServiceContainer.Register(downloads.Object);

            var items = Enumerable.Range(0, 8).Select(i => new VideoItem($"https://example.com/video{i}.mp4")).ToArray();
            var vm = new HypnoViewModel(MockExtractor.Object, downloads.Object);
            vm.SetQueue(items);
            (await WaitUntil(() => vm.CurrentSource != null)).Should().BeTrue("the first video reaches the player");

            int first = vm.CurrentIndex;
            vm.OnMediaOpened(this, new FlyleafLib.MediaPlayer.OpenCompletedArgs()); // Pre-buffers what's next
            (await WaitUntil(() => { lock (requested) return requested.Count > 0; })).Should().BeTrue("the next video is pre-buffered");

            vm.PlayNext(force: true);
            int second = vm.CurrentIndex;
            second.Should().NotBe(first);
            lock (requested) requested[0].Should().Be(items[second].FilePath, "the pre-buffered video is the one shuffle plays next");

            vm.PlayPrevious(force: true);
            vm.CurrentIndex.Should().Be(first, "Previous goes back to the video that played before, not its playlist neighbour");

            vm.Dispose();
        }

        private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }
            return condition();
        }
    }
}
