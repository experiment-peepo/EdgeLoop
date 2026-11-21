using EdgeLoop.Classes;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    public class PlaybackHotkeyTests
    {
        private sealed record Player(string MonitorName, string? SyncGroupId = null, bool IsSyncMaster = false) : ISkipTarget;

        private const string Primary = @"\\.\DISPLAY1";
        private const string Second = @"\\.\DISPLAY2";
        private const string Third = @"\\.\DISPLAY3";

        // --- SkipTargetResolver ---

        [Fact]
        public void Resolve_SinglePlayer_SkipsItWhereverTheMouseIs()
        {
            var only = new Player(Second);

            SkipTargetResolver.Resolve(new[] { only }, cursorScreen: Third, primaryScreen: Primary)
                .Should().Equal(only);
        }

        [Fact]
        public void Resolve_SeveralPlayers_SkipsTheScreenUnderTheMouse()
        {
            var a = new Player(Primary);
            var b = new Player(Second);

            SkipTargetResolver.Resolve(new[] { a, b }, cursorScreen: Second, primaryScreen: Primary)
                .Should().Equal(b);
        }

        [Fact]
        public void Resolve_MouseOnIdleScreen_SkipsThePrimaryScreen()
        {
            var a = new Player(Primary);
            var b = new Player(Second);

            SkipTargetResolver.Resolve(new[] { a, b }, cursorScreen: Third, primaryScreen: Primary)
                .Should().Equal(a);
        }

        [Fact]
        public void Resolve_MouseAndPrimaryIdle_SkipsAllPlayers()
        {
            var b = new Player(Second);
            var c = new Player(Third);

            SkipTargetResolver.Resolve(new[] { b, c }, cursorScreen: @"\\.\DISPLAY9", primaryScreen: Primary)
                .Should().BeEquivalentTo(new[] { b, c });
        }

        [Fact]
        public void Resolve_SyncGroupFollowerUnderMouse_SkipsTheGroupsMaster()
        {
            var master = new Player(Primary, "group", IsSyncMaster: true);
            var follower = new Player(Second, "group");
            var other = new Player(Third);

            SkipTargetResolver.Resolve(new[] { master, follower, other }, cursorScreen: Second, primaryScreen: Primary)
                .Should().Equal(master);
        }

        [Fact]
        public void Resolve_AllMonitorsGroup_SkipsOnlyTheMaster()
        {
            var master = new Player(Primary, "all", IsSyncMaster: true);
            var follower = new Player(Second, "all");

            SkipTargetResolver.Resolve(new[] { master, follower }, cursorScreen: Second, primaryScreen: Primary)
                .Should().Equal(master);
        }

        // --- PlaybackHotkeyManager ---

        private sealed class FakeHotkeys : IHotkeyRegistrar
        {
            public readonly Dictionary<string, (uint Modifiers, string Key, Action Callback)> Registered = new();
            public readonly HashSet<string> Fail = new();

            public bool Register(string name, uint modifiers, string keyName, Action callback)
            {
                if (Fail.Contains(name)) return false;
                Registered[name] = (modifiers, keyName, callback);
                return true;
            }

            public void Unregister(string name) => Registered.Remove(name);
            public bool IsRegistered(string name) => Registered.ContainsKey(name);
        }

        private static (PlaybackHotkeyManager Manager, FakeHotkeys Hotkeys, List<bool> Skips) CreateManager(UserSettings settings)
        {
            var hotkeys = new FakeHotkeys();
            var skips = new List<bool>();
            var manager = new PlaybackHotkeyManager(hotkeys, () => settings, new PlaybackHotkeyActions
            {
                Panic = () => { },
                WipeAndExit = () => { },
                OpaquePanic = () => { },
                Skip = skips.Add
            });
            return (manager, hotkeys, skips);
        }

        [Fact]
        public void NotPlaying_OnlyPanicAndWipeAreRegistered()
        {
            var (manager, hotkeys, _) = CreateManager(new UserSettings());

            manager.RegisterAll().Should().BeEmpty();

            hotkeys.Registered.Keys.Should().BeEquivalentTo(PlaybackHotkeyManager.PanicName, PlaybackHotkeyManager.ClearName);
        }

        [Fact]
        public void Playing_RegistersPlainArrowSkipKeys_AndStoppingReleasesThem()
        {
            var (manager, hotkeys, _) = CreateManager(new UserSettings());
            manager.RegisterAll();

            manager.SetPlaying(true).Should().BeEmpty();
            hotkeys.Registered[PlaybackHotkeyManager.SkipForwardName].Should().Be((0u, "Right", hotkeys.Registered[PlaybackHotkeyManager.SkipForwardName].Callback));
            hotkeys.Registered[PlaybackHotkeyManager.SkipBackwardName].Key.Should().Be("Left");
            hotkeys.Registered[PlaybackHotkeyManager.SkipBackwardName].Modifiers.Should().Be(0u);

            manager.SetPlaying(false);
            hotkeys.Registered.Keys.Should().BeEquivalentTo(PlaybackHotkeyManager.PanicName, PlaybackHotkeyManager.ClearName);
        }

        [Fact]
        public void TripleEscape_IsOnlyRegisteredDuringOpaqueModePlayback()
        {
            var settings = new UserSettings { AlwaysOpaque = false };
            var (manager, hotkeys, _) = CreateManager(settings);
            manager.RegisterAll();
            manager.SetPlaying(true);

            hotkeys.IsRegistered(PlaybackHotkeyManager.OpaquePanicName).Should().BeFalse();

            settings.AlwaysOpaque = true;
            manager.RegisterAll(); // settings reloaded
            hotkeys.IsRegistered(PlaybackHotkeyManager.OpaquePanicName).Should().BeTrue();

            manager.SetPlaying(false);
            hotkeys.IsRegistered(PlaybackHotkeyManager.OpaquePanicName).Should().BeFalse();
        }

        [Fact]
        public void SkipHotkeys_InvokeSkipInTheRightDirection()
        {
            var (manager, hotkeys, skips) = CreateManager(new UserSettings());
            manager.SetPlaying(true);

            hotkeys.Registered[PlaybackHotkeyManager.SkipForwardName].Callback();
            hotkeys.Registered[PlaybackHotkeyManager.SkipBackwardName].Callback();

            skips.Should().Equal(true, false);
        }

        [Fact]
        public void FailedRegistrations_AreReportedByDisplayName()
        {
            var (manager, hotkeys, _) = CreateManager(new UserSettings());
            hotkeys.Fail.Add(PlaybackHotkeyManager.SkipForwardName);

            manager.SetPlaying(true).Should().Equal("Skip Forward");
        }

        [Fact]
        public void Dispose_ReleasesEverything()
        {
            var (manager, hotkeys, _) = CreateManager(new UserSettings { AlwaysOpaque = true });
            manager.RegisterAll();
            manager.SetPlaying(true);

            manager.Dispose();

            hotkeys.Registered.Should().BeEmpty();
        }
    }
}
