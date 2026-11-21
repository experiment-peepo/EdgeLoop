using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    public class SettingsTests : TestBase
    {
        private const string Screen1 = @"\\.\DISPLAY1";
        private const string Screen2 = @"\\.\DISPLAY2";
        private const string Screen3 = @"\\.\DISPLAY3";

        private static ScreenViewer FakeScreen(string device) => new(null) { DeviceName = device };

        private SettingsViewModel CreateSettings(Func<HotkeyCombo, bool?>? availability = null, params string[] screens)
        {
            if (screens.Length == 0) screens = new[] { Screen1, Screen2 };
            return new SettingsViewModel(availability ?? (_ => true), screens.Select(FakeScreen));
        }

        // --- Unsaved changes ---

        [Fact]
        public void HasChanges_FollowsEdits_AndClearsWhenTheyAreUndone()
        {
            var settings = CreateSettings();
            settings.HasChanges.Should().BeFalse();
            settings.OkCommand.CanExecute(null).Should().BeFalse("there's nothing to save");

            double volume = settings.DefaultVolume;
            settings.DefaultVolume = 0.25;
            settings.HasChanges.Should().BeTrue();
            settings.OkCommand.CanExecute(null).Should().BeTrue();

            settings.DefaultVolume = volume;
            settings.HasChanges.Should().BeFalse("the change was undone");
        }

        [Fact]
        public void ChangingAHotkey_OrAScreensOwnKeys_CountsAsAChange()
        {
            var settings = CreateSettings();

            settings.PanicHotkey.Combo = new HotkeyCombo(HotkeyCombo.Ctrl, "F9");
            settings.HasChanges.Should().BeTrue();

            settings.PanicHotkey.ResetToDefault();
            settings.PanicHotkey.Combo = new HotkeyCombo(MockSettings.Object.PanicHotkeyModifiers, MockSettings.Object.PanicHotkeyKey);
            settings.HasChanges.Should().BeFalse();

            settings.ScreenSkipSettings[1].UseOwnKeys = true;
            settings.HasChanges.Should().BeTrue();
        }

        // --- Cookies ---

        [Fact]
        public void Cookies_AreKeptAsTyped_AndCleanedWhenSaved()
        {
            MockSettings.SetupProperty(s => s.HypnotubeCookiesEncrypted); // Virtual, so the mock must be told to keep what is set
            var settings = CreateSettings();

            settings.HypnotubeCookies = "  Cookie: PHPSESSID=abc; theme=dark  ";
            settings.HypnotubeCookies.Should().Be("  Cookie: PHPSESSID=abc; theme=dark  ", "typing isn't interrupted by trimming");

            settings.Save();
            MockSettings.Object.HypnotubeCookies.Should().Be("PHPSESSID=abc; theme=dark");
        }

        [Fact]
        public void BlankCookies_AreSavedAsNone()
        {
            SettingsViewModel.CleanCookieString("   ").Should().BeNull();
            SettingsViewModel.CleanCookieString("Cookie:   ").Should().BeNull();
        }

        // --- Hotkey problems ---

        [Fact]
        public void DuplicateKeys_AcrossGlobalAndScreenKeys_BlockSaving()
        {
            var settings = CreateSettings();
            var screen2 = settings.ScreenSkipSettings[1];
            screen2.UseOwnKeys = true;

            screen2.Forward.Combo = settings.PanicHotkey.Combo;

            settings.HasHotkeyConflicts.Should().BeTrue();
            settings.OkCommand.CanExecute(null).Should().BeFalse();
            screen2.Forward.IsConflict.Should().BeTrue();
            screen2.Forward.Problem.Should().Contain("Panic");
            settings.PanicHotkey.Problem.Should().Contain("Screen 2 forward");

            screen2.Forward.Combo = new HotkeyCombo(HotkeyCombo.Ctrl | HotkeyCombo.Alt, "Right");
            settings.HasHotkeyConflicts.Should().BeFalse();
            settings.PanicHotkey.Problem.Should().BeNull();
            settings.OkCommand.CanExecute(null).Should().BeTrue();
        }

        [Fact]
        public void ScreenKeysThatAreSwitchedOff_DontConflict()
        {
            var settings = CreateSettings();
            var screen2 = settings.ScreenSkipSettings[1];
            screen2.UseOwnKeys = true;
            screen2.Forward.Combo = settings.PanicHotkey.Combo;
            settings.HasHotkeyConflicts.Should().BeTrue();

            screen2.UseOwnKeys = false;

            settings.HasHotkeyConflicts.Should().BeFalse("the screen follows the mouse again, so its keys aren't used");
        }

        [Fact]
        public void KeysAnotherAppHolds_AreAWarning_NotABlock()
        {
            var taken = new HotkeyCombo(HotkeyCombo.Ctrl, "F12");
            var settings = CreateSettings(combo => !combo.SameKeysAs(taken));

            settings.SkipForwardHotkey.Combo = taken;

            settings.SkipForwardHotkey.Problem.Should().Contain("Another app");
            settings.SkipForwardHotkey.IsConflict.Should().BeFalse();
            settings.OkCommand.CanExecute(null).Should().BeTrue();
        }

        // --- Skip keys for each screen ---

        [Fact]
        public void ScreenKeys_RoundTrip_AndDisconnectedScreensKeepTheirs()
        {
            MockSettings.Object.PerScreenSkipHotkeys = new()
            {
                [Screen2] = new ScreenSkipKeys { ForwardModifiers = HotkeyCombo.Ctrl, ForwardKey = "Right", BackwardModifiers = HotkeyCombo.Ctrl, BackwardKey = "Left" },
                [Screen3] = new ScreenSkipKeys { ForwardKey = "F7", BackwardKey = "F6" }
            };

            var settings = CreateSettings(null, Screen1, Screen2);

            settings.ScreenSkipSettings.Should().HaveCount(2, "only connected screens get a row");
            settings.ScreenSkipSettings[0].UseOwnKeys.Should().BeFalse();
            settings.ScreenSkipSettings[1].UseOwnKeys.Should().BeTrue();
            settings.ScreenSkipSettings[1].Forward.Combo.ToString().Should().Be("Ctrl+Right arrow");
            settings.DisconnectedScreensNote.Should().Contain("Screen 3");

            settings.ScreenSkipSettings[0].UseOwnKeys = true;
            settings.ScreenSkipSettings[0].Forward.Combo = new HotkeyCombo(0, "F2");
            settings.Save();

            var saved = MockSettings.Object.PerScreenSkipHotkeys;
            saved.Keys.Should().BeEquivalentTo(new[] { Screen1, Screen2, Screen3 });
            saved[Screen1].ForwardKey.Should().Be("F2");
            saved[Screen1].BackwardKey.Should().BeNull("only one direction was set");
            saved[Screen3].ForwardKey.Should().Be("F7", "an unplugged screen's keys are kept");
        }

        [Fact]
        public void ScreensWithOwnKeysButNoneSet_FollowTheMouse()
        {
            var settings = CreateSettings();
            settings.ScreenSkipSettings[1].UseOwnKeys = true;

            settings.ScreenSkipSettings[1].ToSettings().Should().BeNull();
        }

        // --- Sections ---

        [Theory]
        [InlineData("Hotkeys", SettingsSection.Hotkeys)]
        [InlineData("IsPlaybackExpanded", SettingsSection.Playback)]
        [InlineData("IsGeneralExpanded", SettingsSection.Library)]
        [InlineData("IsCookiesExpanded", SettingsSection.Privacy)]
        [InlineData("IsDiagnosticsExpanded", SettingsSection.Advanced)]
        [InlineData(null, SettingsSection.Playback)]
        public void TheLastSection_IsRemembered_IncludingOldNames(string? saved, SettingsSection expected)
        {
            SettingsViewModel.ParseSection(saved!).Should().Be(expected);
        }

        [Fact]
        public void Censoring_IsNotListed_WhileHidden()
        {
            CreateSettings().Sections.Select(s => s.Section).Should().NotContain(SettingsSection.Censoring);
        }

        [Fact]
        public void ResettingHotkeys_RestoresDefaults_AndScreensFollowTheMouse()
        {
            var settings = CreateSettings();
            settings.SkipForwardHotkey.Combo = new HotkeyCombo(HotkeyCombo.Alt, "N");
            settings.ScreenSkipSettings[1].UseOwnKeys = true;

            settings.ResetSectionCommand.Execute("Hotkeys");

            settings.SkipForwardHotkey.Combo.ToString().Should().Be("Right arrow");
            settings.ScreenSkipSettings[1].UseOwnKeys.Should().BeFalse();
        }
    }

    public class HotkeyComboTests
    {
        [Theory]
        [InlineData(HotkeyCombo.Ctrl | HotkeyCombo.Shift, "End", "Ctrl+Shift+End")]
        [InlineData(0u, "Right", "Right arrow")]
        [InlineData(HotkeyCombo.Alt, "D5", "Alt+5")]
        [InlineData(0u, "Escape", "Esc")]
        [InlineData(0u, null, "Not set")]
        public void Combos_ReadNaturally(uint modifiers, string? key, string expected)
        {
            new HotkeyCombo(modifiers, key!).ToString().Should().Be(expected);
        }

        [Fact]
        public void TheSameKeyUnderTwoNames_IsTheSameHotkey()
        {
            new HotkeyCombo(0, "Return").SameKeysAs(new HotkeyCombo(0, "Enter")).Should().BeTrue();
            new HotkeyCombo(HotkeyCombo.Ctrl, "Enter").SameKeysAs(new HotkeyCombo(0, "Enter")).Should().BeFalse();
        }

        [Fact]
        public void Conflicts_ListTheOthers_AndIgnoreUnsetKeys()
        {
            var conflicts = HotkeyConflicts.Find(new[]
            {
                new NamedHotkey("a", "Panic", new HotkeyCombo(HotkeyCombo.Ctrl, "End")),
                new NamedHotkey("b", "Skip", new HotkeyCombo(HotkeyCombo.Ctrl, "End")),
                new NamedHotkey("c", "Unset 1", default),
                new NamedHotkey("d", "Unset 2", default),
            });

            conflicts.Keys.Should().BeEquivalentTo(new[] { "a", "b" });
            conflicts["a"].Should().Equal("Skip");
        }
    }
}
