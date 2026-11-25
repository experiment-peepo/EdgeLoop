using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using AwesomeAssertions;

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

        [Theory]
        [InlineData("theme=dark; age_verified=1", true)]                     // copied with document.cookie: no session
        [InlineData("Cookie: theme=dark; PHPSESSID=abc123", false)]          // copied from the Network tab
        [InlineData("PHPSESSID=abc123", false)]
        [InlineData("theme=dark; NOTPHPSESSID=x", true)]
        [InlineData("   ", false)]                                           // nothing pasted: nothing to warn about
        public void PastedCookies_WithoutTheSignInCookie_GetAWarning(string cookies, bool warns)
        {
            var settings = CreateSettings();

            settings.HypnotubeCookies = cookies;

            (settings.HypnotubeCookiesNote != null).Should().Be(warns);
            SettingsViewModel.SignInCookieNote(cookies).Should().Be(settings.HypnotubeCookiesNote);
        }

        [Fact]
        public void CookieWarning_IsNotASetting()
        {
            var settings = CreateSettings();

            settings.Rule34VideoCookies = "theme=dark";
            settings.Rule34VideoCookiesNote.Should().NotBeNull();
            settings.Rule34VideoCookies = null;

            settings.HasChanges.Should().BeFalse("the warning follows the cookies; it isn't saved itself");
        }

        [Fact]
        public void CookieBackup_StartsClosed_AndTogglingItIsNotAChange()
        {
            var settings = CreateSettings();
            settings.ShowCookieFallback.Should().BeFalse("accounts are the way to sign in; cookies are only a backup");

            settings.ToggleCookieFallbackCommand.Execute(null);
            settings.ShowCookieFallback.Should().BeTrue();
            settings.HasChanges.Should().BeFalse("opening a section isn't a setting");

            settings.ToggleCookieFallbackCommand.Execute(null);
            settings.ShowCookieFallback.Should().BeFalse();
        }

        [Fact]
        public void CookieBackup_StartsOpen_WhenCookiesAreAlreadySet()
        {
            MockSettings.Setup(s => s.Rule34VideoCookiesEncrypted).Returns("PHPSESSID=abc");

            CreateSettings().ShowCookieFallback.Should().BeTrue("they're in use, so they shouldn't be hidden");
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

        [Fact]
        public void HdrSetting_IsOffByDefault_AndIsSaved()
        {
            new UserSettings().UseHdr.Should().BeFalse("it switches Windows' own HDR setting");

            MockSettings.SetupProperty(s => s.UseHdr, false);
            var settings = CreateSettings();
            settings.UseHdr = true;
            settings.HasChanges.Should().BeTrue();

            settings.Save();
            MockSettings.Object.UseHdr.Should().BeTrue();
        }

        // --- Downloads ---

        [Fact]
        public void SizeLimit_ListsThePresets_AndAnyValueSavedByHand()
        {
            SettingsViewModel.BuildCacheSizeLimits(0).Select(l => l.Value).Should().Equal("5 GB", "10 GB", "20 GB", "50 GB", "100 GB", "No limit");
            SettingsViewModel.BuildCacheSizeLimits(30).Select(l => l.Value).Should().Equal("5 GB", "10 GB", "20 GB", "30 GB", "50 GB", "100 GB", "No limit");
            new UserSettings().MaxCacheSizeGb.Should().Be(20);
        }

        [Fact]
        public void ALowerSizeLimit_IsSaved_AndAppliedRightAway()
        {
            MockSettings.SetupProperty(s => s.MaxCacheSizeGb, 50);
            var downloads = new Moq.Mock<IVideoDownloadService>();
            var trimmed = new System.Threading.ManualResetEventSlim();
            downloads.Setup(d => d.TrimToLimit()).Callback(() => trimmed.Set()).Returns(0);
            ServiceContainer.Register(downloads.Object);

            var settings = CreateSettings();
            settings.MaxCacheSizeGb = 10;
            settings.HasChanges.Should().BeTrue();
            settings.Save();

            MockSettings.Object.MaxCacheSizeGb.Should().Be(10);
            trimmed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("a lower limit doesn't wait for the next download");
        }

        [Fact]
        public void RaisingOrRemovingTheLimit_DeletesNothing()
        {
            MockSettings.SetupProperty(s => s.MaxCacheSizeGb, 10);
            var downloads = new Moq.Mock<IVideoDownloadService>();
            ServiceContainer.Register(downloads.Object);

            foreach (var limit in new[] { 50, 0 })
            {
                var settings = CreateSettings();
                settings.MaxCacheSizeGb = limit;
                settings.Save();
            }

            System.Threading.Thread.Sleep(200);
            downloads.Verify(d => d.TrimToLimit(), Moq.Times.Never);
        }

        [Fact]
        public async Task SpaceUsed_IsShown()
        {
            var downloads = new Moq.Mock<IVideoDownloadService>();
            downloads.Setup(d => d.GetCacheSize()).Returns((long)(3.4 * 1024 * 1024 * 1024));
            ServiceContainer.Register(downloads.Object);

            var settings = CreateSettings();
            await settings.RefreshCacheSizeAsync();
            settings.CacheSizeText.Should().Be($"{3.4.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} GB used");
            settings.HasChanges.Should().BeFalse("showing the size isn't a setting");

            SettingsViewModel.FormatSize(0).Should().Be("0 MB");
            SettingsViewModel.FormatSize(143L * 1024 * 1024).Should().Be("143 MB");
        }

        [Fact]
        public void TheDownloadFolder_IsEdgeLoopsOwn_AndIsntSavedAsAPath()
        {
            new UserSettings().LocalCacheDirectory.Should().BeNull();
            MockSettings.SetupProperty(s => s.LocalCacheDirectory, null!);

            var settings = CreateSettings();
            settings.LocalCacheDirectory.Should().Be(AppPaths.VideoCacheDirectory).And.StartWith(AppContext.BaseDirectory);
            settings.Save();
            MockSettings.Object.LocalCacheDirectory.Should().BeNull("a saved path would point at the old place once EdgeLoop's folder is moved");

            settings.LocalCacheDirectory = @"E:\Videos";
            settings.HasChanges.Should().BeTrue();
            settings.Save();
            MockSettings.Object.LocalCacheDirectory.Should().Be(@"E:\Videos");

            settings.ResetSectionCommand.Execute("Library");
            settings.LocalCacheDirectory.Should().Be(AppPaths.VideoCacheDirectory);
            settings.Save();
            MockSettings.Object.LocalCacheDirectory.Should().BeNull();
        }

        [Fact]
        public void SettingsSavedBefore_MoveToEdgeLoopsFolder_UnlessAFolderWasPicked()
        {
            var old = new UserSettings { SettingsVersion = 1, LocalCacheDirectory = AppPaths.ProfileVideoCacheDirectory };
            old.Migrate().Should().BeTrue();
            old.LocalCacheDirectory.Should().BeNull("the old default was saved as a path, but nobody picked it");
            old.Migrate().Should().BeFalse();

            var picked = new UserSettings { SettingsVersion = 1, LocalCacheDirectory = @"E:\Videos" };
            picked.Migrate().Should().BeTrue();
            picked.LocalCacheDirectory.Should().Be(@"E:\Videos");

            var pickedLater = new UserSettings { LocalCacheDirectory = AppPaths.ProfileVideoCacheDirectory };
            pickedLater.Migrate().Should().BeFalse();
            pickedLater.LocalCacheDirectory.Should().Be(AppPaths.ProfileVideoCacheDirectory, "picking that folder now is a choice");
        }

        // --- The old cookie setting ---

        [Fact]
        public void OldSettingsFiles_WithTheRemovedCookieSetting_StillLoad_AndSavingDropsIt()
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"Cookies\":\"session=abc\",\"DefaultVolume\":0.25}")!;
            loaded.DefaultVolume.Should().Be(0.25);

            using var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(loaded));
            saved.RootElement.TryGetProperty("Cookies", out _).Should().BeFalse("one cookie for every site was sent to every host");
            saved.RootElement.TryGetProperty("HypnotubeCookies", out _).Should().BeTrue("each site keeps its own cookies");
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
