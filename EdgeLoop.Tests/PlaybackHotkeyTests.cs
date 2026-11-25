using EdgeLoop.Classes;
using AwesomeAssertions;

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
            var (manager, hotkeys, skips, _) = CreateManager(settings, null);
            return (manager, hotkeys, skips);
        }

        private static (PlaybackHotkeyManager Manager, FakeHotkeys Hotkeys, List<bool> Skips, List<(string Screen, bool Forward)> ScreenSkips)
            CreateManager(UserSettings settings, string[]? connectedScreens)
        {
            var hotkeys = new FakeHotkeys();
            var skips = new List<bool>();
            var screenSkips = new List<(string, bool)>();
            var manager = new PlaybackHotkeyManager(hotkeys, () => settings, new PlaybackHotkeyActions
            {
                Panic = () => { },
                WipeAndExit = () => { },
                OpaquePanic = () => { },
                Skip = skips.Add,
                SkipScreen = (screen, forward) => screenSkips.Add((screen, forward))
            },
            connectedScreens == null ? null : () => connectedScreens);
            return (manager, hotkeys, skips, screenSkips);
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

        // --- Each screen's own skip keys ---

        private static UserSettings WithScreenKeys() => new()
        {
            PerScreenSkipHotkeys = new()
            {
                [Second] = new ScreenSkipKeys { ForwardModifiers = HotkeyCombo.Ctrl, ForwardKey = "Right", BackwardModifiers = HotkeyCombo.Ctrl, BackwardKey = "Left" },
                [Third] = new ScreenSkipKeys { ForwardKey = "F7" }
            }
        };

        [Fact]
        public void ScreenKeys_AreRegisteredWhilePlaying_ForConnectedScreensOnly()
        {
            var (manager, hotkeys, _, screenSkips) = CreateManager(WithScreenKeys(), connectedScreens: new[] { Primary, Second });
            manager.RegisterAll();
            hotkeys.Registered.Keys.Should().NotContain(k => k.StartsWith(PlaybackHotkeyManager.ScreenSkipPrefix), "nothing is playing");

            manager.SetPlaying(true);
            var forward = PlaybackHotkeyManager.ScreenSkipName(Second, forward: true);
            hotkeys.Registered.Should().ContainKey(forward);
            hotkeys.Registered.Should().ContainKey(PlaybackHotkeyManager.ScreenSkipName(Second, forward: false));
            hotkeys.Registered.Should().NotContainKey(PlaybackHotkeyManager.ScreenSkipName(Third, forward: true), "screen 3 isn't connected");

            hotkeys.Registered[forward].Callback();
            screenSkips.Should().Equal((Second, true));

            manager.SetPlaying(false);
            hotkeys.Registered.Keys.Should().NotContain(k => k.StartsWith(PlaybackHotkeyManager.ScreenSkipPrefix));
        }

        [Fact]
        public void Suspend_ReleasesEveryHotkey_UntilTheyAreRegisteredAgain()
        {
            var (manager, hotkeys, _, _) = CreateManager(WithScreenKeys(), connectedScreens: new[] { Second });
            manager.RegisterAll();
            manager.SetPlaying(true);

            manager.Suspend();
            hotkeys.Registered.Should().BeEmpty("a key recorder in Settings needs the keys");

            manager.RegisterAll();
            hotkeys.Registered.Should().ContainKeys(PlaybackHotkeyManager.PanicName, PlaybackHotkeyManager.SkipForwardName,
                PlaybackHotkeyManager.ScreenSkipName(Second, forward: true));
        }

        [Fact]
        public void ScreenKeyFailures_NameTheScreen()
        {
            PlaybackHotkeyManager.DisplayName(PlaybackHotkeyManager.ScreenSkipName(Second, forward: false))
                .Should().Be("Skip Backward on Screen 2");
        }

        [Fact]
        public void ResolveScreen_SkipsOnlyThatScreen_ViaItsGroupsMaster()
        {
            var master = new Player(Primary, "group", IsSyncMaster: true);
            var follower = new Player(Second, "group");
            var alone = new Player(Third);
            var players = new[] { master, follower, alone };

            SkipTargetResolver.ResolveScreen(players, Third).Should().Equal(alone);
            SkipTargetResolver.ResolveScreen(players, Second).Should().Equal(master);
            SkipTargetResolver.ResolveScreen(players, @"\\.\DISPLAY9").Should().BeEmpty("nothing plays there");
        }
    }
}
