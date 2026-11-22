using System.IO;
using EdgeLoop.Classes;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// The HDR switcher against a stand-in for Windows' display calls: it only ever switches monitors that support
    /// HDR and have it off, always switches them back, and puts them right after a crash.
    /// </summary>
    public class HdrSwitcherTests : IDisposable
    {
        private const string Screen1 = @"\\.\DISPLAY1", Screen2 = @"\\.\DISPLAY2";
        private const string Monitor1 = @"\\?\DISPLAY#AUS1#1", Monitor2 = @"\\?\DISPLAY#ABC2#1";

        private readonly string _record = Path.Combine(Path.GetTempPath(), $"hdr_restore_{Guid.NewGuid():N}.json");

        private sealed class FakeDisplays : IHdrDisplays
        {
            public readonly Dictionary<string, (string Path, bool Supported)> Screens = new()
            {
                [Screen1] = (Monitor1, true),
                [Screen2] = (Monitor2, false)
            };
            public readonly Dictionary<string, bool> Enabled = new() { [Monitor1] = false, [Monitor2] = false };
            public readonly List<(string Path, bool On)> Switches = new();
            public bool Refuse;

            public HdrMonitor Find(string screen) =>
                Screens.TryGetValue(screen, out var s) ? new HdrMonitor(s.Path, s.Supported, Enabled[s.Path]) : null!;

            public bool Switch(string path, bool on)
            {
                Switches.Add((path, on));
                if (Refuse || !Enabled.ContainsKey(path)) return false;
                Enabled[path] = on;
                return true;
            }
        }

        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public HdrSwitcherTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

        public void Dispose()
        {
            if (File.Exists(_record)) File.Delete(_record);
        }

        [Fact]
        public void Windows_ReportsEachScreensHdrState_WithoutChangingIt()
        {
            // Read-only: the real calls, on whatever screens this PC has (none on some build machines)
            var displays = new WindowsHdrDisplays();
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var monitor = displays.Find(screen.DeviceName);
                _output.WriteLine($"{screen.DeviceName}: {monitor}");
                if (monitor != null)
                    monitor.Path.Should().StartWith(@"\\?\", "a monitor device path");
            }
            displays.Find(@"\\.\DISPLAY99").Should().BeNull();
        }

        // ===== Which screens get their own video window =====

        [Fact]
        public void WithHdrOn_AndAnHdrMonitorAmongTheScreens_EveryScreenGetsItsOwnVideoWindow()
        {
            var switcher = new HdrSwitcher(new FakeDisplays(), _record);

            switcher.UsesOwnVideoWindows(true, new[] { Screen1, Screen2 }).Should().BeTrue("WPF stops showing video on screen 2 once screen 1 is in HDR");
            switcher.UsesOwnVideoWindows(true, new[] { Screen2, Screen1 }).Should().BeTrue("whichever screen comes first");
            switcher.UsesOwnVideoWindows(true, new[] { Screen1 }).Should().BeTrue();
        }

        [Fact]
        public void WithoutAnHdrMonitor_OrWithHdrOff_PlaybackIsAsUsual()
        {
            var displays = new FakeDisplays();
            var switcher = new HdrSwitcher(displays, _record);

            switcher.UsesOwnVideoWindows(true, new[] { Screen2 }).Should().BeFalse("screen 2's monitor has no HDR");
            switcher.UsesOwnVideoWindows(true, new[] { @"\\.\DISPLAY9", null! }).Should().BeFalse("nor do screens Windows doesn't know");
            switcher.UsesOwnVideoWindows(false, new[] { Screen1, Screen2 }).Should().BeFalse("HDR is off in Settings");
            displays.Switches.Should().BeEmpty("deciding switches nothing");
        }

        [Fact]
        public void HdrTheUserHasOn_CountsToo()
        {
            var displays = new FakeDisplays();
            displays.Enabled[Monitor1] = true;

            new HdrSwitcher(displays, _record).UsesOwnVideoWindows(true, new[] { Screen1, Screen2 }).Should().BeTrue();
        }

        // ===== Switching =====

        [Fact]
        public void AnHdrMonitor_IsSwitchedOn_AndBackOff()
        {
            var displays = new FakeDisplays();
            var switcher = new HdrSwitcher(displays, _record);

            switcher.SwitchOn(Screen1).Should().BeTrue();
            displays.Enabled[Monitor1].Should().BeTrue();
            File.Exists(_record).Should().BeTrue("it's written down while HDR is on");

            switcher.Restore(Screen1);
            displays.Enabled[Monitor1].Should().BeFalse();
            File.Exists(_record).Should().BeFalse("nothing is left to put back");
            switcher.SwitchedOn.Should().BeEmpty();
        }

        [Fact]
        public void HdrTheUserSwitchedOn_IsNeverTouched()
        {
            var displays = new FakeDisplays();
            displays.Enabled[Monitor1] = true;
            var switcher = new HdrSwitcher(displays, _record);

            switcher.SwitchOn(Screen1).Should().BeTrue("HDR is on");
            switcher.Restore(Screen1);
            switcher.RestoreAll();

            displays.Switches.Should().BeEmpty();
            displays.Enabled[Monitor1].Should().BeTrue("it stays the user's");
        }

        [Fact]
        public void AMonitorWithoutHdr_IsLeftAlone()
        {
            var displays = new FakeDisplays();
            var switcher = new HdrSwitcher(displays, _record);

            switcher.SwitchOn(Screen2).Should().BeFalse();
            switcher.SwitchOn(@"\\.\DISPLAY9").Should().BeFalse("an unknown screen too");

            displays.Switches.Should().BeEmpty();
            File.Exists(_record).Should().BeFalse();
        }

        [Fact]
        public void WhenWindowsRefuses_NothingIsLeftBehind()
        {
            var displays = new FakeDisplays { Refuse = true };
            var switcher = new HdrSwitcher(displays, _record);

            switcher.SwitchOn(Screen1).Should().BeFalse();

            switcher.SwitchedOn.Should().BeEmpty();
            File.Exists(_record).Should().BeFalse();
        }

        [Fact]
        public void ClosingEdgeLoop_SwitchesEverythingBack()
        {
            var displays = new FakeDisplays();
            var switcher = new HdrSwitcher(displays, _record);
            switcher.SwitchOn(Screen1);

            switcher.RestoreAll();

            displays.Enabled[Monitor1].Should().BeFalse();
            File.Exists(_record).Should().BeFalse();
        }

        [Fact]
        public void AfterACrash_TheNextStartSwitchesItBack()
        {
            var displays = new FakeDisplays();
            new HdrSwitcher(displays, _record).SwitchOn(Screen1); // ...and EdgeLoop crashes without restoring

            var nextRun = new HdrSwitcher(displays, _record);
            nextRun.RecoverFromRecord().Should().Be(1);

            displays.Enabled[Monitor1].Should().BeFalse();
            File.Exists(_record).Should().BeFalse();
            nextRun.RecoverFromRecord().Should().Be(0, "it's done once");
        }

        [Fact]
        public void AMonitorUnpluggedSinceTheCrash_IsTriedAgainNextTime()
        {
            var displays = new FakeDisplays();
            new HdrSwitcher(displays, _record).SwitchOn(Screen1);
            displays.Enabled.Remove(Monitor1); // Unplugged

            new HdrSwitcher(displays, _record).RecoverFromRecord().Should().Be(0);
            File.Exists(_record).Should().BeTrue("it's still owed a switch back");

            displays.Enabled[Monitor1] = true; // Plugged back in, still in HDR
            new HdrSwitcher(displays, _record).RecoverFromRecord().Should().Be(1);
            displays.Enabled[Monitor1].Should().BeFalse();
        }
    }
}
