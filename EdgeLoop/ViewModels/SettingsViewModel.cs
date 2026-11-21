using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Input;
using EdgeLoop.Classes;
using EdgeLoop.Classes.Censoring;

namespace EdgeLoop.ViewModels
{
    /// <summary>
    /// The Settings window. Changes stay here until Save; <see cref="HasChanges"/> says whether there
    /// is anything to save, and Save is blocked while two hotkeys share the same keys.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class SettingsViewModel : ObservableObject
    {
        private readonly UserSettings _settings;
        private readonly Func<HotkeyCombo, bool?> _checkAvailability;
        private readonly Dictionary<string, bool?> _availabilityCache = new();
        private readonly Dictionary<string, ScreenSkipKeys> _disconnectedScreenKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _initialStartWithWindows;
        private string _savedState;
        private bool _loading = true;

        /// <param name="checkAvailability">Whether keys are free to register; defaults to asking Windows. Tests pass a fake.</param>
        /// <param name="screens">The connected screens; defaults to the real ones.</param>
        public SettingsViewModel(Func<HotkeyCombo, bool?> checkAvailability = null, IEnumerable<ScreenViewer> screens = null)
        {
            _settings = App.Settings;
            _checkAvailability = checkAvailability ?? (combo => App.Hotkeys?.IsAvailable(combo.Modifiers, combo.Key));
            var settings = _settings;
            var defaults = new UserSettings();

            // --- Sections ---
            Sections = new List<SettingsSectionItem>
            {
                new() { Section = SettingsSection.Playback, Title = "Playback", Description = "Screen, opacity and volume for new videos" },
                new() { Section = SettingsSection.Library, Title = "Library & Cache", Description = "Saved playlist, positions and downloads" },
                new() { Section = SettingsSection.Privacy, Title = "Privacy & Cookies", Description = "Signed-in access to video sites" },
                new() { Section = SettingsSection.Hotkeys, Title = "Hotkeys", Description = "Keyboard shortcuts that work in any app" },
                new() { Section = SettingsSection.Advanced, Title = "Advanced", Description = "Startup and troubleshooting" },
            };
            if (ShowCensorSettings)
                Sections.Add(new() { Section = SettingsSection.Censoring, Title = "Censoring", Description = "Cover content in the video itself" });
            _selectedSection = Sections.FirstOrDefault(s => s.Section == ParseSection(settings.LastExpandedSection)) ?? Sections[0];

            // --- Playback ---
            AvailableMonitors = new ObservableCollection<ScreenViewer>();
            var connectedScreens = (screens ?? LoadScreens()).ToList();
            AvailableMonitors.Add(ScreenViewer.CreateAllScreens());
            foreach (var screen in connectedScreens)
                AvailableMonitors.Add(screen);
            _selectedDefaultMonitor = AvailableMonitors.FirstOrDefault(m => m.DeviceName == settings.DefaultMonitorDeviceName);
            _defaultOpacity = settings.DefaultOpacity;
            _defaultVolume = settings.DefaultVolume;
            _alwaysOpaque = settings.AlwaysOpaque;
            _enableSuperResolution = settings.EnableSuperResolution;
            _showPlaybackNotices = settings.ShowPlaybackNotices;

            // --- Library ---
            _rememberLastPlaylist = settings.RememberLastPlaylist;
            _rememberFilePosition = settings.RememberFilePosition;
            _enableLocalCaching = settings.EnableLocalCaching;
            _localCacheDirectory = settings.LocalCacheDirectory;

            // --- Privacy ---
            _browserForCookies = settings.BrowserForCookies ?? defaults.BrowserForCookies;
            _hypnotubeCookies = settings.HypnotubeCookies;
            _iwaraCookies = settings.IwaraCookies;
            _rule34VideoCookies = settings.Rule34VideoCookies;
            CookieEncryptionAvailable = CookieProtector.IsEncryptionAvailable();

            // --- Hotkeys ---
            PanicHotkey = new HotkeySetting(PlaybackHotkeyManager.PanicName, "Panic", "Stops every video at once",
                new(settings.PanicHotkeyModifiers, settings.PanicHotkeyKey ?? "End"), new(defaults.PanicHotkeyModifiers, defaults.PanicHotkeyKey));
            ClearHotkey = new HotkeySetting(PlaybackHotkeyManager.ClearName, "Wipe & Exit", "Clears the playlist and closes EdgeLoop",
                new(settings.ClearHotkeyModifiers, settings.ClearHotkeyKey ?? "Delete"), new(defaults.ClearHotkeyModifiers, defaults.ClearHotkeyKey));
            SkipForwardHotkey = new HotkeySetting(PlaybackHotkeyManager.SkipForwardName, "Skip forward", "Next video on the screen under the mouse",
                new(settings.SkipForwardHotkeyModifiers, settings.SkipForwardHotkeyKey ?? "Right"), new(defaults.SkipForwardHotkeyModifiers, defaults.SkipForwardHotkeyKey));
            SkipBackwardHotkey = new HotkeySetting(PlaybackHotkeyManager.SkipBackwardName, "Skip back", "Previous video on the screen under the mouse",
                new(settings.SkipBackwardHotkeyModifiers, settings.SkipBackwardHotkeyKey ?? "Left"), new(defaults.SkipBackwardHotkeyModifiers, defaults.SkipBackwardHotkeyKey));
            EscapeHotkey = new HotkeySetting(PlaybackHotkeyManager.OpaquePanicName, "Emergency stop (press 3 times)", "Only in Opaque Mode: press three times quickly to stop everything",
                new(settings.OpaquePanicHotkeyModifiers, settings.OpaquePanicHotkeyKey ?? "Escape"), new(defaults.OpaquePanicHotkeyModifiers, defaults.OpaquePanicHotkeyKey));

            var savedScreenKeys = settings.PerScreenSkipHotkeys ?? new();
            foreach (var screen in connectedScreens)
            {
                savedScreenKeys.TryGetValue(screen.DeviceName ?? "", out var keys);
                var row = new ScreenSkipSetting(screen, keys);
                row.PropertyChanged += (s, e) => OnHotkeysChanged();
                row.Forward.PropertyChanged += Hotkey_PropertyChanged;
                row.Backward.PropertyChanged += Hotkey_PropertyChanged;
                ScreenSkipSettings.Add(row);
            }
            foreach (var (device, keys) in savedScreenKeys)
            {
                if (keys != null && !connectedScreens.Any(s => string.Equals(s.DeviceName, device, StringComparison.OrdinalIgnoreCase)))
                    _disconnectedScreenKeys[device] = keys;
            }
            foreach (var hotkey in GlobalHotkeys)
                hotkey.PropertyChanged += Hotkey_PropertyChanged;

            // --- Advanced ---
            _startWithWindows = _initialStartWithWindows = StartupManager.IsStartupEnabled();
            _enableDiagnosticMode = settings.EnableDiagnosticMode;

            // --- Censoring (hidden until the feature is ready) ---
            _enableLocalCensor = settings.EnableLocalCensor;
            _censorModelPath = settings.CensorModelPath;
            _censorConfidence = settings.CensorConfidence;
            _censorIntervalMs = settings.CensorIntervalMs;
            _censorHoldMs = settings.CensorHoldMs;
            _censorPadding = settings.CensorPadding;
            _censorShowLabels = settings.CensorShowLabels;
            LoadCensorClassOptions(settings.CensorClassMask);

            OkCommand = new RelayCommand(Ok, _ => HasChanges && !HasHotkeyConflicts);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
            ResetSectionCommand = new RelayCommand(ResetSection);
            ResetPositionsCommand = new RelayCommand(ResetPositions);
            CopyCookieScriptCommand = new RelayCommand(CopyCookieScript);
            PasteCookiesCommand = new RelayCommand(site => SetCookies(site as string, System.Windows.Clipboard.GetText()));
            ClearCookiesCommand = new RelayCommand(site => SetCookies(site as string, null));
            OpenDiagnosticsFolderCommand = new RelayCommand(OpenDiagnosticsFolder);
            BrowseCacheDirectoryCommand = new RelayCommand(BrowseCacheDirectory);
            IdentifyScreensCommand = new RelayCommand(_ => ScreenIdentifier.Show());

            _loading = false;
            ValidateHotkeys();
            _savedState = Snapshot();
        }

        private static IEnumerable<ScreenViewer> LoadScreens()
        {
            try
            {
                return WindowServices.GetAllScreenViewers();
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to load monitors for settings", ex);
                return Array.Empty<ScreenViewer>();
            }
        }

        // ===== Sections =====

        public List<SettingsSectionItem> Sections { get; }

        private SettingsSectionItem _selectedSection;
        public SettingsSectionItem SelectedSection
        {
            get => _selectedSection;
            set
            {
                if (value != null && SetProperty(ref _selectedSection, value))
                    _settings.LastExpandedSection = value.Section.ToString(); // Reopens here next time
            }
        }

        /// <summary>Reads the remembered section, including the names the old accordion used.</summary>
        internal static SettingsSection ParseSection(string saved) => saved switch
        {
            _ when Enum.TryParse<SettingsSection>(saved, out var section) => section,
            "IsGeneralExpanded" or "IsApplicationExpanded" => SettingsSection.Library,
            "IsPrivacyExpanded" or "IsHistoryExpanded" or "IsCookiesExpanded" => SettingsSection.Privacy,
            "IsHotkeysExpanded" => SettingsSection.Hotkeys,
            "IsDiagnosticsExpanded" => SettingsSection.Advanced,
            _ => SettingsSection.Playback
        };

        // ===== Unsaved changes =====

        private bool _hasChanges;
        /// <summary>Something differs from what's saved.</summary>
        public bool HasChanges
        {
            get => _hasChanges;
            private set
            {
                if (SetProperty(ref _hasChanges, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Set once Save has written the settings, so closing doesn't ask about unsaved changes.</summary>
        public bool IsSaved { get; private set; }

        private static readonly HashSet<string> NotSettings = new()
        {
            nameof(HasChanges), nameof(SelectedSection), nameof(HasHotkeyConflicts), nameof(ShowCookies),
            nameof(CookieScriptCopied), nameof(CensorModelStatus), nameof(DisconnectedScreensNote)
        };

        protected override void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (!_loading && !NotSettings.Contains(propertyName))
                UpdateHasChanges();
        }

        private void UpdateHasChanges()
        {
            if (_savedState != null)
                HasChanges = Snapshot() != _savedState;
        }

        /// <summary>Every saved value, as text, to compare against what was loaded.</summary>
        private string Snapshot()
        {
            var values = new List<object>
            {
                SelectedDefaultMonitor?.DeviceName, DefaultOpacity, DefaultVolume, AlwaysOpaque, EnableSuperResolution, ShowPlaybackNotices,
                RememberLastPlaylist, RememberFilePosition, EnableLocalCaching, LocalCacheDirectory,
                BrowserForCookies, CleanCookieString(HypnotubeCookies), CleanCookieString(IwaraCookies), CleanCookieString(Rule34VideoCookies),
                StartWithWindows, EnableDiagnosticMode,
                EnableLocalCensor, CensorModelPath, CensorConfidence, CensorIntervalMs, CensorHoldMs, CensorPadding, CensorShowLabels, BuildCensorClassMask()
            };
            values.AddRange(GlobalHotkeys.Select(h => (object)h.Combo.ToString()));
            values.AddRange(ScreenSkipSettings.Select(s => (object)$"{s.UseOwnKeys}:{s.Forward.Combo}:{s.Backward.Combo}"));
            return string.Join("\u001F", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
        }

        // ===== Playback =====

        public ObservableCollection<ScreenViewer> AvailableMonitors { get; }

        private ScreenViewer _selectedDefaultMonitor;
        public ScreenViewer SelectedDefaultMonitor
        {
            get => _selectedDefaultMonitor;
            set => SetProperty(ref _selectedDefaultMonitor, value);
        }

        private double _defaultOpacity;
        public double DefaultOpacity
        {
            get => _defaultOpacity;
            set => SetProperty(ref _defaultOpacity, value);
        }

        private double _defaultVolume;
        public double DefaultVolume
        {
            get => _defaultVolume;
            set => SetProperty(ref _defaultVolume, value);
        }

        private bool _alwaysOpaque;
        public bool AlwaysOpaque
        {
            get => _alwaysOpaque;
            set => SetProperty(ref _alwaysOpaque, value);
        }

        private bool _enableSuperResolution;
        public bool EnableSuperResolution
        {
            get => _enableSuperResolution;
            set => SetProperty(ref _enableSuperResolution, value);
        }

        private bool _showPlaybackNotices;
        /// <summary>A short notice on a screen when one of its videos can't be played.</summary>
        public bool ShowPlaybackNotices
        {
            get => _showPlaybackNotices;
            set => SetProperty(ref _showPlaybackNotices, value);
        }

        // ===== Library & Cache =====

        private bool _rememberLastPlaylist;
        public bool RememberLastPlaylist
        {
            get => _rememberLastPlaylist;
            set => SetProperty(ref _rememberLastPlaylist, value);
        }

        private bool _rememberFilePosition;
        public bool RememberFilePosition
        {
            get => _rememberFilePosition;
            set => SetProperty(ref _rememberFilePosition, value);
        }

        private bool _enableLocalCaching;
        public bool EnableLocalCaching
        {
            get => _enableLocalCaching;
            set => SetProperty(ref _enableLocalCaching, value);
        }

        private string _localCacheDirectory;
        public string LocalCacheDirectory
        {
            get => _localCacheDirectory;
            set => SetProperty(ref _localCacheDirectory, value);
        }

        // ===== Privacy & Cookies =====

        public ObservableCollection<string> AvailableBrowsers { get; } = new() { "Chrome", "Firefox", "Edge", "Opera", "Brave" };

        private string _browserForCookies;
        public string BrowserForCookies
        {
            get => _browserForCookies;
            set => SetProperty(ref _browserForCookies, value);
        }

        // Cookies are kept as typed and cleaned up when saved, so editing doesn't fight the cursor
        private string _hypnotubeCookies;
        public string HypnotubeCookies
        {
            get => _hypnotubeCookies;
            set => SetProperty(ref _hypnotubeCookies, value);
        }

        private string _iwaraCookies;
        public string IwaraCookies
        {
            get => _iwaraCookies;
            set => SetProperty(ref _iwaraCookies, value);
        }

        private string _rule34VideoCookies;
        public string Rule34VideoCookies
        {
            get => _rule34VideoCookies;
            set => SetProperty(ref _rule34VideoCookies, value);
        }

        private bool _showCookies;
        /// <summary>Cookie values are hidden (they sign you in) until shown.</summary>
        public bool ShowCookies
        {
            get => _showCookies;
            set => SetProperty(ref _showCookies, value);
        }

        /// <summary>False when Windows can't encrypt cookies here, so they'd be saved as plain text.</summary>
        public bool CookieEncryptionAvailable { get; }

        private bool _cookieScriptCopied;
        /// <summary>Briefly true after the cookie helper was copied, to confirm it.</summary>
        public bool CookieScriptCopied
        {
            get => _cookieScriptCopied;
            private set => SetProperty(ref _cookieScriptCopied, value);
        }

        private void SetCookies(string site, string value)
        {
            switch (site)
            {
                case "Hypnotube": HypnotubeCookies = value; break;
                case "Iwara": IwaraCookies = value; break;
                case "Rule34Video": Rule34VideoCookies = value; break;
            }
        }

        /// <summary>Trims the cookie text and drops a "Cookie:" header name copied from the browser's DevTools.</summary>
        internal static string CleanCookieString(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var cleaned = value.Trim();
            if (cleaned.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(7).Trim();
            return cleaned.Length == 0 ? null : cleaned;
        }

        private void CopyCookieScript(object obj)
        {
            try
            {
                // Run in the browser console on the signed-in site: copies document.cookie to the clipboard
                string script = "(function(){copy(document.cookie);alert('EdgeLoop Cookie Helper:\\nCookies copied to clipboard!\\n\\nNow return to EdgeLoop and click Paste.');})();";
                System.Windows.Clipboard.SetText(script);

                CookieScriptCopied = true;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    CookieScriptCopied = false;
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to copy cookie script to clipboard", ex);
            }
        }

        // ===== Hotkeys =====

        public HotkeySetting PanicHotkey { get; }
        public HotkeySetting ClearHotkey { get; }
        public HotkeySetting SkipForwardHotkey { get; }
        public HotkeySetting SkipBackwardHotkey { get; }
        public HotkeySetting EscapeHotkey { get; }

        private IEnumerable<HotkeySetting> GlobalHotkeys => new[] { PanicHotkey, ClearHotkey, SkipForwardHotkey, SkipBackwardHotkey, EscapeHotkey };

        /// <summary>One row per connected screen: follow the mouse, or its own skip keys.</summary>
        public ObservableCollection<ScreenSkipSetting> ScreenSkipSettings { get; } = new();

        /// <summary>Screens with their own keys that aren't connected now, or null.</summary>
        public string DisconnectedScreensNote
        {
            get
            {
                if (_disconnectedScreenKeys.Count == 0) return null;
                var names = _disconnectedScreenKeys.Keys.Select(ScreenName);
                return $"{string.Join(", ", names)} {(_disconnectedScreenKeys.Count == 1 ? "isn't" : "aren't")} connected now. " +
                       "Their own skip keys are kept and work again when connected.";
            }
        }

        private static string ScreenName(string device)
        {
            var number = System.Text.RegularExpressions.Regex.Match(device, @"DISPLAY(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return number.Success ? $"Screen {number.Groups[1].Value}" : device;
        }

        private bool _hasHotkeyConflicts;
        /// <summary>Two hotkeys share the same keys; Save is disabled until that's fixed.</summary>
        public bool HasHotkeyConflicts
        {
            get => _hasHotkeyConflicts;
            private set
            {
                if (SetProperty(ref _hasHotkeyConflicts, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        private void Hotkey_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HotkeySetting.Combo))
                OnHotkeysChanged();
        }

        private void OnHotkeysChanged()
        {
            if (_loading) return;
            ValidateHotkeys();
            UpdateHasChanges();
        }

        /// <summary>Every hotkey that would be registered, for duplicate checks.</summary>
        private IEnumerable<HotkeySetting> ActiveHotkeys() =>
            GlobalHotkeys.Concat(ScreenSkipSettings.Where(s => s.UseOwnKeys).SelectMany(s => new[] { s.Forward, s.Backward }));

        /// <summary>Marks hotkeys that share keys (blocks saving) or that another app holds (a warning).</summary>
        internal void ValidateHotkeys()
        {
            var active = ActiveHotkeys().ToList();
            var conflicts = HotkeyConflicts.Find(active.Select(h => new NamedHotkey(h.Id, h.Title, h.Combo)));

            foreach (var hotkey in GlobalHotkeys.Concat(ScreenSkipSettings.SelectMany(s => new[] { s.Forward, s.Backward })))
            {
                bool isActive = active.Contains(hotkey);
                if (isActive && conflicts.TryGetValue(hotkey.Id, out var others))
                {
                    hotkey.IsConflict = true;
                    hotkey.Problem = $"Same keys as {string.Join(" and ", others)}. Choose different keys to save.";
                }
                else
                {
                    hotkey.IsConflict = false;
                    hotkey.Problem = isActive && !hotkey.Combo.IsEmpty && IsAvailable(hotkey.Combo) == false
                        ? "Another app already uses these keys, so they may not work."
                        : null;
                }
            }

            HasHotkeyConflicts = conflicts.Count > 0;
        }

        private bool? IsAvailable(HotkeyCombo combo)
        {
            string key = combo.ToString();
            if (!_availabilityCache.TryGetValue(key, out var available))
            {
                try { available = _checkAvailability(combo); }
                catch (Exception ex) { Logger.Debug($"[Settings] Couldn't check hotkey {key}: {ex.Message}"); available = null; }
                _availabilityCache[key] = available;
            }
            return available;
        }

        // ===== Advanced =====

        private bool _startWithWindows;
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set => SetProperty(ref _startWithWindows, value);
        }

        private bool _enableDiagnosticMode;
        public bool EnableDiagnosticMode
        {
            get => _enableDiagnosticMode;
            set => SetProperty(ref _enableDiagnosticMode, value);
        }

        // ===== Censoring (hidden, see FeatureFlags) =====

        /// <summary>The Censoring section is hidden until the feature is ready (see <see cref="FeatureFlags"/>).</summary>
        public bool ShowCensorSettings => FeatureFlags.CensorFeatureVisible;

        private bool _enableLocalCensor;
        public bool EnableLocalCensor
        {
            get => _enableLocalCensor;
            set
            {
                if (SetProperty(ref _enableLocalCensor, value))
                    OnPropertyChanged(nameof(CensorModelStatus));
            }
        }

        private string _censorModelPath;
        public string CensorModelPath
        {
            get => _censorModelPath;
            set
            {
                if (SetProperty(ref _censorModelPath, value))
                    OnPropertyChanged(nameof(CensorModelStatus));
            }
        }

        private double _censorConfidence;
        public double CensorConfidence
        {
            get => _censorConfidence;
            set => SetProperty(ref _censorConfidence, value);
        }

        private int _censorIntervalMs;
        public int CensorIntervalMs
        {
            get => _censorIntervalMs;
            set => SetProperty(ref _censorIntervalMs, value);
        }

        private int _censorHoldMs;
        public int CensorHoldMs
        {
            get => _censorHoldMs;
            set => SetProperty(ref _censorHoldMs, value);
        }

        private double _censorPadding;
        public double CensorPadding
        {
            get => _censorPadding;
            set => SetProperty(ref _censorPadding, value);
        }

        private bool _censorShowLabels;
        public bool CensorShowLabels
        {
            get => _censorShowLabels;
            set => SetProperty(ref _censorShowLabels, value);
        }

        /// <summary>Tells the user which model will actually be used, or why none will.</summary>
        public string CensorModelStatus
        {
            get
            {
                string path = string.IsNullOrWhiteSpace(CensorModelPath)
                    ? CensorModelLocator.FindDefault()
                    : CensorModelPath;

                if (string.IsNullOrWhiteSpace(path))
                    return "No model found. Put a YOLO11 .onnx file in the Models folder next to EdgeLoop.exe (see Models/README.md).";

                return System.IO.File.Exists(path)
                    ? "Using " + System.IO.Path.GetFileName(path)
                    : "Model not found at: " + path;
            }
        }

        /// <summary>Per-class censor toggles, backed by the bitmask in settings.</summary>
        public ObservableCollection<CensorClassOption> CensorClassOptions { get; } = new();

        public int BuildCensorClassMask()
        {
            int mask = 0;
            foreach (var option in CensorClassOptions)
            {
                if (option.IsEnabled)
                    mask |= 1 << option.ClassId;
            }
            return mask;
        }

        private void LoadCensorClassOptions(int mask)
        {
            foreach (var option in CensorClassOptions)
                option.PropertyChanged -= CensorOption_PropertyChanged;
            CensorClassOptions.Clear();
            for (int i = 0; i < CensorClasses.Count; i++)
            {
                var option = new CensorClassOption
                {
                    ClassId = i,
                    Name = CensorClasses.Names[i].Replace('_', ' '),
                    IsEnabled = CensorClasses.IsSet(mask, i),
                };
                option.PropertyChanged += CensorOption_PropertyChanged;
                CensorClassOptions.Add(option);
            }
        }

        private void CensorOption_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!_loading) UpdateHasChanges();
        }

        // ===== Resetting a section =====

        /// <summary>Puts one section back to the default values. Like any change, it only applies on Save.</summary>
        private void ResetSection(object parameter)
        {
            if (!Enum.TryParse<SettingsSection>(parameter?.ToString(), out var section)) return;
            var defaults = new UserSettings();

            switch (section)
            {
                case SettingsSection.Playback:
                    SelectedDefaultMonitor = AvailableMonitors.FirstOrDefault(m => m.DeviceName == defaults.DefaultMonitorDeviceName);
                    DefaultOpacity = defaults.DefaultOpacity;
                    DefaultVolume = defaults.DefaultVolume;
                    AlwaysOpaque = defaults.AlwaysOpaque;
                    EnableSuperResolution = defaults.EnableSuperResolution;
                    ShowPlaybackNotices = defaults.ShowPlaybackNotices;
                    break;
                case SettingsSection.Library:
                    RememberLastPlaylist = defaults.RememberLastPlaylist;
                    RememberFilePosition = defaults.RememberFilePosition;
                    EnableLocalCaching = defaults.EnableLocalCaching;
                    LocalCacheDirectory = defaults.LocalCacheDirectory;
                    break;
                case SettingsSection.Privacy:
                    BrowserForCookies = defaults.BrowserForCookies; // Cookies stay: they're sign-ins, not preferences
                    break;
                case SettingsSection.Hotkeys:
                    _loading = true;
                    foreach (var hotkey in GlobalHotkeys)
                        hotkey.ResetToDefault();
                    foreach (var screen in ScreenSkipSettings)
                        screen.UseOwnKeys = false;
                    _loading = false;
                    OnHotkeysChanged();
                    break;
                case SettingsSection.Advanced:
                    StartWithWindows = defaults.StartWithWindows;
                    EnableDiagnosticMode = defaults.EnableDiagnosticMode;
                    break;
                case SettingsSection.Censoring:
                    EnableLocalCensor = defaults.EnableLocalCensor;
                    CensorModelPath = defaults.CensorModelPath;
                    CensorConfidence = defaults.CensorConfidence;
                    CensorIntervalMs = defaults.CensorIntervalMs;
                    CensorHoldMs = defaults.CensorHoldMs;
                    CensorPadding = defaults.CensorPadding;
                    CensorShowLabels = defaults.CensorShowLabels;
                    LoadCensorClassOptions(defaults.CensorClassMask);
                    UpdateHasChanges();
                    break;
            }
        }

        // ===== Actions that apply immediately =====

        private void ResetPositions(object obj)
        {
            if (DialogService.Confirm("Reset playback history", "Clear the saved position of every video? This happens right away and can't be undone.",
                    "Clear positions", "Cancel", destructive: true))
            {
                PlaybackPositionTracker.Instance.ClearAllPositions();
            }
        }

        private void OpenDiagnosticsFolder(object obj)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.DataDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to open diagnostics folder", ex);
            }
        }

        private void BrowseCacheDirectory(object obj)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Local Cache Directory",
                UseDescriptionForTitle = true,
                SelectedPath = string.IsNullOrEmpty(LocalCacheDirectory) || !System.IO.Directory.Exists(LocalCacheDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    : LocalCacheDirectory
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                LocalCacheDirectory = dialog.SelectedPath;
        }

        // ===== Commands =====

        public ICommand OkCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ResetSectionCommand { get; }
        public ICommand ResetPositionsCommand { get; }
        public ICommand CopyCookieScriptCommand { get; }
        public ICommand PasteCookiesCommand { get; }
        public ICommand ClearCookiesCommand { get; }
        public ICommand OpenDiagnosticsFolderCommand { get; }
        public ICommand BrowseCacheDirectoryCommand { get; }
        public ICommand IdentifyScreensCommand { get; }

        public event EventHandler RequestClose;

        // ===== Saving =====

        private void Ok(object obj)
        {
            if (HasHotkeyConflicts) return;
            Save();
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Writes every value to the settings and saves them.</summary>
        internal void Save()
        {
            var settings = _settings;

            settings.DefaultMonitorDeviceName = SelectedDefaultMonitor?.DeviceName;
            settings.DefaultOpacity = DefaultOpacity;
            settings.DefaultVolume = DefaultVolume;
            settings.AlwaysOpaque = AlwaysOpaque;
            settings.EnableSuperResolution = EnableSuperResolution;
            settings.ShowPlaybackNotices = ShowPlaybackNotices;

            settings.RememberLastPlaylist = RememberLastPlaylist;
            settings.RememberFilePosition = RememberFilePosition;
            settings.EnableLocalCaching = EnableLocalCaching;
            settings.LocalCacheDirectory = LocalCacheDirectory;

            settings.BrowserForCookies = BrowserForCookies;
            settings.HypnotubeCookies = CleanCookieString(HypnotubeCookies);
            settings.IwaraCookies = CleanCookieString(IwaraCookies);
            settings.Rule34VideoCookies = CleanCookieString(Rule34VideoCookies);

            settings.PanicHotkeyModifiers = PanicHotkey.Modifiers;
            settings.PanicHotkeyKey = PanicHotkey.Key;
            settings.ClearHotkeyModifiers = ClearHotkey.Modifiers;
            settings.ClearHotkeyKey = ClearHotkey.Key;
            settings.SkipForwardHotkeyModifiers = SkipForwardHotkey.Modifiers;
            settings.SkipForwardHotkeyKey = SkipForwardHotkey.Key;
            settings.SkipBackwardHotkeyModifiers = SkipBackwardHotkey.Modifiers;
            settings.SkipBackwardHotkeyKey = SkipBackwardHotkey.Key;
            settings.OpaquePanicHotkeyModifiers = EscapeHotkey.Modifiers;
            settings.OpaquePanicHotkeyKey = EscapeHotkey.Key;

            var screenKeys = new Dictionary<string, ScreenSkipKeys>(_disconnectedScreenKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var screen in ScreenSkipSettings)
            {
                if (screen.ToSettings() is { } keys && !string.IsNullOrEmpty(screen.DeviceName))
                    screenKeys[screen.DeviceName] = keys;
            }
            settings.PerScreenSkipHotkeys = screenKeys;

            // Only touch the Windows startup entry when the choice changed
            if (StartWithWindows != _initialStartWithWindows)
                StartupManager.SetStartup(StartWithWindows);
            settings.StartWithWindows = StartWithWindows;

            settings.EnableDiagnosticMode = EnableDiagnosticMode;
            Logger.DiagnosticMode = EnableDiagnosticMode;
            // Main log stays at Warning; diagnostic log captures everything separately
            settings.LogLevel = LogLevel.Warning;
            Logger.MinimumLevel = LogLevel.Warning;

            settings.EnableLocalCensor = EnableLocalCensor;
            settings.CensorModelPath = string.IsNullOrWhiteSpace(CensorModelPath) ? null : CensorModelPath.Trim();
            settings.CensorConfidence = Math.Clamp(CensorConfidence, 0.05, 0.95);
            settings.CensorIntervalMs = Math.Max(40, CensorIntervalMs);
            settings.CensorHoldMs = Math.Max(0, CensorHoldMs);
            settings.CensorPadding = Math.Clamp(CensorPadding, 0.0, 0.5);
            settings.CensorShowLabels = CensorShowLabels;
            settings.CensorClassMask = BuildCensorClassMask();

            settings.LastExpandedSection = SelectedSection?.Section.ToString();
            settings.Save();

            IsSaved = true;
            _savedState = Snapshot();
            HasChanges = false;

            // Apply to running players
            if (ServiceContainer.TryGet<VideoPlayerService>(out var players))
            {
                players.RefreshAllSuperResolution();
                players.RefreshAllCensor();
            }
        }
    }

    /// <summary>One row in the per-class censor list.</summary>
    public class CensorClassOption : ObservableObject
    {
        public int ClassId { get; set; }
        public string Name { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }
        private bool _isEnabled;
    }
}
