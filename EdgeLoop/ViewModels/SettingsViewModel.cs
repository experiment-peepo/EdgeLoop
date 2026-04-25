using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Input;
using EdgeLoop.Classes;
using System.IO;

namespace EdgeLoop.ViewModels {
    /// <summary>
    /// ViewModel for the Settings window
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class SettingsViewModel : ObservableObject {
        private double _defaultOpacity;
        private double _defaultVolume;

        private bool _launcherAlwaysOnTop;
        private bool _startWithWindows;
        private bool _panicHotkeyCtrl;
        private bool _panicHotkeyShift;
        private bool _panicHotkeyAlt;
        private string _panicHotkeyKey;
        private bool _clearHotkeyCtrl;
        private bool _clearHotkeyShift;
        private bool _clearHotkeyAlt;
        private string _clearHotkeyKey;
        private string _skipForwardHotkeyKey;
        private string _skipBackwardHotkeyKey;
        private string _opaquePanicHotkeyKey;
        private ScreenViewer _selectedDefaultMonitor;
        private bool _alwaysOpaque;
        
        private bool _skipForwardHotkeyCtrl;
        private bool _skipForwardHotkeyShift;
        private bool _skipForwardHotkeyAlt;
        
        private bool _skipBackwardHotkeyCtrl;
        private bool _skipBackwardHotkeyShift;
        private bool _skipBackwardHotkeyAlt;

        private bool _rememberLastPlaylist;
        private bool _rememberFilePosition;
        private bool _isPlaybackExpanded;
        private bool _isGeneralExpanded;
        private bool _isHotkeysExpanded;
        private bool _isPrivacyExpanded;
        private bool _isDiagnosticsExpanded;
        private bool _enableLocalCaching;
        private string _localCacheDirectory;

        private bool _enableDiagnosticMode;
        private string _hypnotubeCookies;
        private string _browserForCookies;

        public ObservableCollection<string> AvailableBrowsers { get; } = new ObservableCollection<string> { 
            "Chrome", "Firefox", "Edge", "Opera", "Brave"
        };

        public string BrowserForCookies {
            get => _browserForCookies;
            set => SetProperty(ref _browserForCookies, value);
        }

        public bool IsPrivacyExpanded {
            get => _isPrivacyExpanded;
            set {
                if (SetProperty(ref _isPrivacyExpanded, value) && value) {
                    CollapseOthers(nameof(IsPrivacyExpanded));
                    App.Settings.LastExpandedSection = nameof(IsPrivacyExpanded);
                }
            }
        }

        public string HypnotubeCookies {
            get => _hypnotubeCookies;
            set => SetProperty(ref _hypnotubeCookies, CleanCookieString(value));
        }


        private string CleanCookieString(string value) {
            if (string.IsNullOrWhiteSpace(value)) return value;
            
            var cleaned = value.Trim();
            // Strip "Cookie: " prefix if user copied it from DevTools headers
            if (cleaned.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) {
                cleaned = cleaned.Substring(7).Trim();
            }
            
            return cleaned;
        }

        public bool IsPlaybackExpanded {
            get => _isPlaybackExpanded;
            set {
                if (SetProperty(ref _isPlaybackExpanded, value) && value) {
                    CollapseOthers(nameof(IsPlaybackExpanded));
                    App.Settings.LastExpandedSection = nameof(IsPlaybackExpanded);
                }
            }
        }

        public bool IsGeneralExpanded {
            get => _isGeneralExpanded;
            set {
                if (SetProperty(ref _isGeneralExpanded, value) && value) {
                    CollapseOthers(nameof(IsGeneralExpanded));
                    App.Settings.LastExpandedSection = nameof(IsGeneralExpanded);
                }
            }
        }

        public bool IsHotkeysExpanded {
            get => _isHotkeysExpanded;
            set {
                if (SetProperty(ref _isHotkeysExpanded, value) && value) {
                    CollapseOthers(nameof(IsHotkeysExpanded));
                    App.Settings.LastExpandedSection = nameof(IsHotkeysExpanded);
                }
            }
        }

        // Removed IsHistoryExpanded - now part of IsPrivacyExpanded

        public bool IsDiagnosticsExpanded {
            get => _isDiagnosticsExpanded;
            set {
                if (SetProperty(ref _isDiagnosticsExpanded, value) && value) {
                    CollapseOthers(nameof(IsDiagnosticsExpanded));
                    App.Settings.LastExpandedSection = nameof(IsDiagnosticsExpanded);
                }
            }
        }

        private void CollapseOthers(string current) {
            if (current != nameof(IsPlaybackExpanded)) IsPlaybackExpanded = false;
            if (current != nameof(IsGeneralExpanded)) IsGeneralExpanded = false;
            if (current != nameof(IsHotkeysExpanded)) IsHotkeysExpanded = false;
            if (current != nameof(IsPrivacyExpanded)) IsPrivacyExpanded = false;
            if (current != nameof(IsDiagnosticsExpanded)) IsDiagnosticsExpanded = false;
        }

        // Taboo Settings


        // Modifier flags
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_ALT = 0x0001;

        [SupportedOSPlatform("windows")]
        public SettingsViewModel() {
            // Load current settings
            var settings = App.Settings;
            _defaultOpacity = settings.DefaultOpacity;
            _defaultVolume = settings.DefaultVolume;

            _launcherAlwaysOnTop = settings.LauncherAlwaysOnTop;
            _startWithWindows = StartupManager.IsStartupEnabled();
            
            // Load panic hotkey settings
            _panicHotkeyCtrl = (settings.PanicHotkeyModifiers & MOD_CONTROL) != 0;
            _panicHotkeyShift = (settings.PanicHotkeyModifiers & MOD_SHIFT) != 0;
            _panicHotkeyAlt = (settings.PanicHotkeyModifiers & MOD_ALT) != 0;
            _panicHotkeyKey = settings.PanicHotkeyKey ?? "End";
            
            _clearHotkeyCtrl = (settings.ClearHotkeyModifiers & MOD_CONTROL) != 0;
            _clearHotkeyShift = (settings.ClearHotkeyModifiers & MOD_SHIFT) != 0;
            _clearHotkeyAlt = (settings.ClearHotkeyModifiers & MOD_ALT) != 0;
            _clearHotkeyKey = settings.ClearHotkeyKey ?? "Delete";

            _skipForwardHotkeyCtrl = (settings.SkipForwardHotkeyModifiers & MOD_CONTROL) != 0;
            _skipForwardHotkeyShift = (settings.SkipForwardHotkeyModifiers & MOD_SHIFT) != 0;
            _skipForwardHotkeyAlt = (settings.SkipForwardHotkeyModifiers & MOD_ALT) != 0;
            _skipForwardHotkeyKey = settings.SkipForwardHotkeyKey ?? "Right";

            _skipBackwardHotkeyCtrl = (settings.SkipBackwardHotkeyModifiers & MOD_CONTROL) != 0;
            _skipBackwardHotkeyShift = (settings.SkipBackwardHotkeyModifiers & MOD_SHIFT) != 0;
            _skipBackwardHotkeyAlt = (settings.SkipBackwardHotkeyModifiers & MOD_ALT) != 0;
            _skipBackwardHotkeyKey = settings.SkipBackwardHotkeyKey ?? "Left";

            _opaquePanicHotkeyKey = settings.OpaquePanicHotkeyKey ?? "Escape";
            _alwaysOpaque = settings.AlwaysOpaque;

            _rememberLastPlaylist = settings.RememberLastPlaylist;
            _rememberFilePosition = settings.RememberFilePosition;
            _enableSuperResolution = settings.EnableSuperResolution;
            _enableDiagnosticMode = settings.EnableDiagnosticMode;
            _hypnotubeCookies = settings.HypnotubeCookies;
            _browserForCookies = settings.BrowserForCookies ?? "Firefox";
            

            // Load and set the last expanded section
            var lastSection = settings.LastExpandedSection ?? nameof(IsPlaybackExpanded);
            
            // Map legacy section names
            if (lastSection == "IsApplicationExpanded") lastSection = nameof(IsGeneralExpanded);
            if (lastSection == "IsHistoryExpanded" || lastSection == "IsCookiesExpanded") lastSection = nameof(IsPrivacyExpanded);

            _isPlaybackExpanded = lastSection == nameof(IsPlaybackExpanded);
            _isGeneralExpanded = lastSection == nameof(IsGeneralExpanded);
            _isHotkeysExpanded = lastSection == nameof(IsHotkeysExpanded);
            _isPrivacyExpanded = lastSection == nameof(IsPrivacyExpanded);
            _isDiagnosticsExpanded = lastSection == nameof(IsDiagnosticsExpanded);

            // Ensure at least one is expanded
            if (!_isPlaybackExpanded && !_isGeneralExpanded && !_isHotkeysExpanded && !_isPrivacyExpanded && !_isDiagnosticsExpanded) {
                _isPlaybackExpanded = true;
            }



            // Load available monitors
            AvailableMonitors = new ObservableCollection<ScreenViewer>();
            RefreshAvailableMonitors();
            
            // Load default monitor from settings
            if (!string.IsNullOrEmpty(settings.DefaultMonitorDeviceName)) {
                _selectedDefaultMonitor = AvailableMonitors.FirstOrDefault(m => m.DeviceName == settings.DefaultMonitorDeviceName);
            }

            OkCommand = new RelayCommand(Ok);
            CancelCommand = new RelayCommand(Cancel);
            OpenKoFiCommand = new RelayCommand(OpenKoFi);
            ResetPositionsCommand = new RelayCommand(ResetPositions);
            CopyCookieScriptCommand = new RelayCommand(CopyCookieScript);
            PasteHypnoCookiesCommand = new RelayCommand(o => HypnotubeCookies = System.Windows.Clipboard.GetText());
            OpenDiagnosticsFolderCommand = new RelayCommand(OpenDiagnosticsFolder);
            BrowseCacheDirectoryCommand = new RelayCommand(ExecuteBrowseCacheDirectory);
        }

        private void CopyCookieScript(object obj) {
            try {
                // Netscape/Header format helper
                // Netscape/Header format helper - captures cookies and localStorage tokens
                string script = "(function(){copy(document.cookie);alert('EdgeLoop Cookie Helper:\\nCookies copied to clipboard!\\n\\nNow return to EdgeLoop and click Paste.');})();";
                System.Windows.Clipboard.SetText(script);
            } catch (Exception ex) {
                Logger.Error("Failed to copy cookie script to clipboard", ex);
            }
        }

        private void ResetPositions(object obj) {
            if (Windows.ConfirmationDialog.Show("Are you sure you want to clear all saved video positions? This cannot be undone.", "Reset Playback History")) {
                PlaybackPositionTracker.Instance.ClearAllPositions();
            }
        }

        private void OpenKoFi(object obj) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://ko-fi.com/vexfromdestiny",
                    UseShellExecute = true
                });
            } catch (System.Exception ex) {
                Logger.Error("Failed to open Ko-Fi link", ex);
            }
        }

        private void OpenDiagnosticsFolder(object obj) {
            try {
                var dataDir = AppPaths.DataDirectory;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = dataDir,
                    UseShellExecute = true
                });
            } catch (System.Exception ex) {
                Logger.Error("Failed to open diagnostics folder", ex);
            }
        }

        private void ExecuteBrowseCacheDirectory(object obj) {
            var dialog = new System.Windows.Forms.FolderBrowserDialog {
                Description = "Select Local Cache Directory",
                UseDescriptionForTitle = true,
                SelectedPath = string.IsNullOrEmpty(LocalCacheDirectory) || !System.IO.Directory.Exists(LocalCacheDirectory) 
                    ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) 
                    : LocalCacheDirectory
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                LocalCacheDirectory = dialog.SelectedPath;
            }
        }

        [SupportedOSPlatform("windows")]
        private void RefreshAvailableMonitors() {
            AvailableMonitors.Clear();
            try {
                // Add "All Screens" option first
                AvailableMonitors.Add(ScreenViewer.CreateAllScreens());
                
                var screens = WindowServices.GetAllScreenViewers();
                foreach (var screen in screens) {
                    AvailableMonitors.Add(screen);
                }
            } catch (System.Exception ex) {
                Logger.Warning("Failed to load monitors for settings", ex);
            }
        }

        public ObservableCollection<ScreenViewer> AvailableMonitors { get; }

        public ScreenViewer SelectedDefaultMonitor {
            get => _selectedDefaultMonitor;
            set => SetProperty(ref _selectedDefaultMonitor, value);
        }

        public double DefaultOpacity {
            get => _defaultOpacity;
            set => SetProperty(ref _defaultOpacity, value);
        }

        public double DefaultVolume {
            get => _defaultVolume;
            set => SetProperty(ref _defaultVolume, value);
        }



        public bool EnableSuperResolution {
            get => _enableSuperResolution;
            set => SetProperty(ref _enableSuperResolution, value);
        }
        private bool _enableSuperResolution;

        public bool EnableDiagnosticMode {
            get => _enableDiagnosticMode;
            set => SetProperty(ref _enableDiagnosticMode, value);
        }

        public bool LauncherAlwaysOnTop {
            get => _launcherAlwaysOnTop;
            set => SetProperty(ref _launcherAlwaysOnTop, value);
        }

        public bool StartWithWindows {
            get => _startWithWindows;
            set => SetProperty(ref _startWithWindows, value);
        }

        public bool PanicHotkeyCtrl {
            get => _panicHotkeyCtrl;
            set {
                SetProperty(ref _panicHotkeyCtrl, value);
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
            }
        }

        public bool PanicHotkeyShift {
            get => _panicHotkeyShift;
            set {
                SetProperty(ref _panicHotkeyShift, value);
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
            }
        }

        public bool PanicHotkeyAlt {
            get => _panicHotkeyAlt;
            set {
                SetProperty(ref _panicHotkeyAlt, value);
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
            }
        }

        public string PanicHotkeyKey {
            get => _panicHotkeyKey;
            set {
                SetProperty(ref _panicHotkeyKey, value);
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
            }
        }

        public bool ClearHotkeyCtrl {
            get => _clearHotkeyCtrl;
            set => SetProperty(ref _clearHotkeyCtrl, value);
        }

        public bool ClearHotkeyShift {
            get => _clearHotkeyShift;
            set => SetProperty(ref _clearHotkeyShift, value);
        }

        public bool ClearHotkeyAlt {
            get => _clearHotkeyAlt;
            set => SetProperty(ref _clearHotkeyAlt, value);
        }

        public string ClearHotkeyKey {
            get => _clearHotkeyKey;
            set => SetProperty(ref _clearHotkeyKey, value);
        }

        public string OpaquePanicHotkeyKey {
            get => _opaquePanicHotkeyKey;
            set => SetProperty(ref _opaquePanicHotkeyKey, value);
        }

        public bool SkipForwardHotkeyCtrl {
            get => _skipForwardHotkeyCtrl;
            set => SetProperty(ref _skipForwardHotkeyCtrl, value);
        }
        public bool SkipForwardHotkeyShift {
            get => _skipForwardHotkeyShift;
            set => SetProperty(ref _skipForwardHotkeyShift, value);
        }
        public bool SkipForwardHotkeyAlt {
            get => _skipForwardHotkeyAlt;
            set => SetProperty(ref _skipForwardHotkeyAlt, value);
        }
        public string SkipForwardHotkeyKey {
            get => _skipForwardHotkeyKey;
            set => SetProperty(ref _skipForwardHotkeyKey, value);
        }

        public bool SkipBackwardHotkeyCtrl {
            get => _skipBackwardHotkeyCtrl;
            set => SetProperty(ref _skipBackwardHotkeyCtrl, value);
        }
        public bool SkipBackwardHotkeyShift {
            get => _skipBackwardHotkeyShift;
            set => SetProperty(ref _skipBackwardHotkeyShift, value);
        }
        public bool SkipBackwardHotkeyAlt {
            get => _skipBackwardHotkeyAlt;
            set => SetProperty(ref _skipBackwardHotkeyAlt, value);
        }
        public string SkipBackwardHotkeyKey {
            get => _skipBackwardHotkeyKey;
            set => SetProperty(ref _skipBackwardHotkeyKey, value);
        }

        public string PanicHotkeyDisplay {
            get {
                var parts = new System.Collections.Generic.List<string>();
                if (PanicHotkeyCtrl) parts.Add("Ctrl");
                if (PanicHotkeyShift) parts.Add("Shift");
                if (PanicHotkeyAlt) parts.Add("Alt");
                
                parts.Add(PanicHotkeyKey ?? "End");
                return string.Join("+", parts);
            }
        }

        public bool AlwaysOpaque {
            get => _alwaysOpaque;
            set => SetProperty(ref _alwaysOpaque, value);
        }



        public bool RememberLastPlaylist {
            get => _rememberLastPlaylist;
            set => SetProperty(ref _rememberLastPlaylist, value);
        }

        public bool RememberFilePosition {
            get => _rememberFilePosition;
            set => SetProperty(ref _rememberFilePosition, value);
        }

        public bool EnableLocalCaching {
            get => _enableLocalCaching;
            set => SetProperty(ref _enableLocalCaching, value);
        }


        public string LocalCacheDirectory {
            get => _localCacheDirectory;
            set => SetProperty(ref _localCacheDirectory, value);
        }

            
        public ICommand OkCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenKoFiCommand { get; }
        public ICommand ResetPositionsCommand { get; }
        public ICommand CopyCookieScriptCommand { get; }
        public ICommand PasteHypnoCookiesCommand { get; }
        public ICommand OpenDiagnosticsFolderCommand { get; }
        public ICommand BrowseCacheDirectoryCommand { get; }

        public event System.EventHandler RequestClose;

        private void Ok(object obj) {
            // Save settings
            var settings = App.Settings;
            settings.DefaultOpacity = DefaultOpacity;
            settings.DefaultVolume = DefaultVolume;

            settings.LauncherAlwaysOnTop = LauncherAlwaysOnTop;
            
            // Apply startup setting to Registry
            StartupManager.SetStartup(StartWithWindows);
            settings.StartWithWindows = StartWithWindows;
            
            // Save default monitor
            settings.DefaultMonitorDeviceName = SelectedDefaultMonitor?.DeviceName;
            
            // Save panic hotkey settings
            uint modifiers = 0;
            if (PanicHotkeyCtrl) modifiers |= MOD_CONTROL;
            if (PanicHotkeyShift) modifiers |= MOD_SHIFT;
            if (PanicHotkeyAlt) modifiers |= MOD_ALT;
            settings.PanicHotkeyModifiers = modifiers;
            settings.PanicHotkeyKey = PanicHotkeyKey ?? "End";

            uint clearModifiers = 0;
            if (ClearHotkeyCtrl) clearModifiers |= MOD_CONTROL;
            if (ClearHotkeyShift) clearModifiers |= MOD_SHIFT;
            if (ClearHotkeyAlt) clearModifiers |= MOD_ALT;
            settings.ClearHotkeyModifiers = clearModifiers;
            settings.ClearHotkeyKey = ClearHotkeyKey ?? "Delete";

            uint skipForwardModifiers = 0;
            if (SkipForwardHotkeyCtrl) skipForwardModifiers |= MOD_CONTROL;
            if (SkipForwardHotkeyShift) skipForwardModifiers |= MOD_SHIFT;
            if (SkipForwardHotkeyAlt) skipForwardModifiers |= MOD_ALT;
            settings.SkipForwardHotkeyModifiers = skipForwardModifiers;
            settings.SkipForwardHotkeyKey = SkipForwardHotkeyKey ?? "Right";

            uint skipBackwardModifiers = 0;
            if (SkipBackwardHotkeyCtrl) skipBackwardModifiers |= MOD_CONTROL;
            if (SkipBackwardHotkeyShift) skipBackwardModifiers |= MOD_SHIFT;
            if (SkipBackwardHotkeyAlt) skipBackwardModifiers |= MOD_ALT;
            settings.SkipBackwardHotkeyModifiers = skipBackwardModifiers;
            settings.SkipBackwardHotkeyKey = SkipBackwardHotkeyKey ?? "Left";

            settings.OpaquePanicHotkeyKey = OpaquePanicHotkeyKey ?? "Escape";
            settings.AlwaysOpaque = AlwaysOpaque;

            settings.RememberLastPlaylist = RememberLastPlaylist;
            settings.RememberFilePosition = RememberFilePosition;
            settings.EnableSuperResolution = EnableSuperResolution;
            settings.EnableLocalCaching = EnableLocalCaching;
            settings.LocalCacheDirectory = LocalCacheDirectory;
            settings.HypnotubeCookies = HypnotubeCookies;
            settings.BrowserForCookies = BrowserForCookies;
            

            // Save currently expanded section
            if (IsPlaybackExpanded) settings.LastExpandedSection = nameof(IsPlaybackExpanded);
            else if (IsGeneralExpanded) settings.LastExpandedSection = nameof(IsGeneralExpanded);
            else if (IsHotkeysExpanded) settings.LastExpandedSection = nameof(IsHotkeysExpanded);
            else if (IsPrivacyExpanded) settings.LastExpandedSection = nameof(IsPrivacyExpanded);
            else if (IsDiagnosticsExpanded) settings.LastExpandedSection = nameof(IsDiagnosticsExpanded);
            
            settings.EnableDiagnosticMode = EnableDiagnosticMode;
            Logger.DiagnosticMode = EnableDiagnosticMode;
            // Main log stays at Warning; diagnostic log captures everything separately
            settings.LogLevel = LogLevel.Warning;
            Logger.MinimumLevel = LogLevel.Warning;
            
            settings.Save();

            // Refresh Super Resolution for all active players
            if (ServiceContainer.TryGet<VideoPlayerService>(out var vps)) {
                vps.RefreshAllSuperResolution();
            }

            RequestClose?.Invoke(this, System.EventArgs.Empty);
        }

        private void Cancel(object obj) {
            RequestClose?.Invoke(this, System.EventArgs.Empty);
        }

    }
}

