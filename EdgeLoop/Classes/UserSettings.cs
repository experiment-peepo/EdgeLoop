using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeLoop.Classes
{


    public class UserSettings
    {
        public int SettingsVersion { get; set; } = 1;
        public virtual double Opacity { get; set; } = 0.2;
        public virtual double Volume { get; set; } = 0.5;

        public bool StartWithWindows { get; set; } = false;
        public double DefaultOpacity { get; set; } = 0.9;
        public double DefaultVolume { get; set; } = 0.5;
        public string DefaultMonitorDeviceName { get; set; } = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        // --- Cookie storage: encrypted at rest via DPAPI ---
        // The JSON file stores the DPAPI-encrypted value.
        // Use the Get/Set helpers below for plaintext access at runtime.

        [JsonPropertyName("HypnotubeCookies")]
        public virtual string HypnotubeCookiesEncrypted { get; set; } = null;

        [JsonPropertyName("IwaraCookies")]
        public virtual string IwaraCookiesEncrypted { get; set; } = null;

        [JsonPropertyName("Rule34VideoCookies")]
        public virtual string Rule34VideoCookiesEncrypted { get; set; } = null;

        [JsonPropertyName("Cookies")]
        public virtual string CookiesEncrypted { get; set; } = null;

        /// <summary>The Iwara account's email. Iwara shows explicit videos only to signed-in accounts.</summary>
        public virtual string IwaraEmail { get; set; } = null;

        [JsonPropertyName("IwaraPassword")]
        public virtual string IwaraPasswordEncrypted { get; set; } = null;

        /// <summary>The Iwara account's password, encrypted at rest (never saved as plain text).</summary>
        [JsonIgnore]
        public string IwaraPassword
        {
            get => CookieProtector.Unprotect(IwaraPasswordEncrypted);
            set => IwaraPasswordEncrypted = CookieProtector.ProtectSecret(value);
        }

        // --- Hypnotube and Rule34Video accounts: EdgeLoop signs in with them and keeps the session ---

        /// <summary>The Hypnotube username (its login form takes a username, not an email).</summary>
        public virtual string HypnotubeUsername { get; set; } = null;

        [JsonPropertyName("HypnotubePassword")]
        public virtual string HypnotubePasswordEncrypted { get; set; } = null;

        [JsonIgnore]
        public string HypnotubePassword
        {
            get => CookieProtector.Unprotect(HypnotubePasswordEncrypted);
            set => HypnotubePasswordEncrypted = CookieProtector.ProtectSecret(value);
        }

        [JsonPropertyName("HypnotubeSession")]
        public virtual string HypnotubeSessionEncrypted { get; set; } = null;

        /// <summary>Cookies from EdgeLoop's own sign-in with the account above (not the pasted cookies).</summary>
        [JsonIgnore]
        public string HypnotubeSession
        {
            get => CookieProtector.Unprotect(HypnotubeSessionEncrypted);
            set => HypnotubeSessionEncrypted = CookieProtector.Protect(value);
        }

        /// <summary>The Rule34Video email or username.</summary>
        public virtual string Rule34VideoUsername { get; set; } = null;

        [JsonPropertyName("Rule34VideoPassword")]
        public virtual string Rule34VideoPasswordEncrypted { get; set; } = null;

        [JsonIgnore]
        public string Rule34VideoPassword
        {
            get => CookieProtector.Unprotect(Rule34VideoPasswordEncrypted);
            set => Rule34VideoPasswordEncrypted = CookieProtector.ProtectSecret(value);
        }

        [JsonPropertyName("Rule34VideoSession")]
        public virtual string Rule34VideoSessionEncrypted { get; set; } = null;

        /// <summary>Cookies from EdgeLoop's own sign-in with the account above (not the pasted cookies).</summary>
        [JsonIgnore]
        public string Rule34VideoSession
        {
            get => CookieProtector.Unprotect(Rule34VideoSessionEncrypted);
            set => Rule34VideoSessionEncrypted = CookieProtector.Protect(value);
        }

        public virtual string UserAgent { get; set; } = null;
        public virtual string BrowserForCookies { get; set; } = "Firefox";

        /// <summary>
        /// Gets the decrypted Hypnotube cookies for runtime use.
        /// Handles both legacy plaintext and DPAPI-encrypted values.
        /// </summary>
        [JsonIgnore]
        public string HypnotubeCookies
        {
            get => CookieProtector.Unprotect(HypnotubeCookiesEncrypted);
            set => HypnotubeCookiesEncrypted = CookieProtector.Protect(value);
        }

        /// <summary>
        /// Gets the decrypted Iwara cookies for runtime use.
        /// </summary>
        [JsonIgnore]
        public string IwaraCookies
        {
            get => CookieProtector.Unprotect(IwaraCookiesEncrypted);
            set => IwaraCookiesEncrypted = CookieProtector.Protect(value);
        }

        /// <summary>
        /// Gets the decrypted Rule34Video cookies for runtime use.
        /// </summary>
        [JsonIgnore]
        public string Rule34VideoCookies
        {
            get => CookieProtector.Unprotect(Rule34VideoCookiesEncrypted);
            set => Rule34VideoCookiesEncrypted = CookieProtector.Protect(value);
        }

        /// <summary>
        /// Gets the decrypted global cookies for runtime use.
        /// </summary>
        [JsonIgnore]
        public string Cookies
        {
            get => CookieProtector.Unprotect(CookiesEncrypted);
            set => CookiesEncrypted = CookieProtector.Protect(value);
        }


        // Panic hotkey configuration
        // Modifiers: Ctrl=2, Shift=4, Alt=1 (can be combined with bitwise OR)
        public uint PanicHotkeyModifiers { get; set; } = 0x0002 | 0x0004; // Ctrl+Shift (default)
        public string PanicHotkeyKey { get; set; } = "End"; // Default key

        public uint ClearHotkeyModifiers { get; set; } = 0x0002 | 0x0004; // Ctrl+Shift (default)
        public string ClearHotkeyKey { get; set; } = "Delete"; // Default key

        // Skip hotkeys
        public uint SkipForwardHotkeyModifiers { get; set; } = 0;
        public string SkipForwardHotkeyKey { get; set; } = "Right";

        public uint SkipBackwardHotkeyModifiers { get; set; } = 0;
        public string SkipBackwardHotkeyKey { get; set; } = "Left";

        public string OpaquePanicHotkeyKey { get; set; } = "Escape";
        public uint OpaquePanicHotkeyModifiers { get; set; } = 0; // No modifiers by default

        /// <summary>
        /// Skip keys for particular screens, by screen device name (e.g. \\.\DISPLAY2). These always
        /// skip that screen, wherever the mouse is. Screens not listed follow the mouse with the skip
        /// keys above.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, ScreenSkipKeys> PerScreenSkipHotkeys { get; set; } = new();

        public virtual bool AlwaysOpaque { get; set; } = false;

        /// <summary>Show a short notice on a screen when one of its videos can't be played.</summary>
        public virtual bool ShowPlaybackNotices { get; set; } = true;
        public virtual bool EnableSuperResolution { get; set; } = false;

        /// <summary>How upscaling enlarges video: "Fast" (FSR 1), "Sharp" (FSRCNNX) or "LineArt" (FSRCNNX for line art).</summary>
        public virtual string UpscalingQuality { get; set; } = "Sharp";

        /// <summary>
        /// HDR videos play in HDR on monitors that support it: Windows HDR is switched on for them while EdgeLoop plays
        /// there, and back off afterwards (see <see cref="HdrSwitcher"/>).
        /// </summary>
        public virtual bool UseHdr { get; set; } = false;

        // Local Censor Settings
        //
        // Screen-capture censoring tools detect on the composited desktop, which
        // means a translucent player window is blended with whatever is behind it
        // before they ever see it - at low opacity they mostly detect the desktop.
        // These settings drive detection on the decoded frame instead, before any
        // window opacity is applied, so it works at any opacity.

        /// <summary>Master switch for in-player detection and censoring.</summary>
        public virtual bool EnableLocalCensor { get; set; } = false;

        /// <summary>Path to a YOLO11 .onnx model. Empty means auto-discover under Models/.</summary>
        public virtual string CensorModelPath { get; set; } = null;

        /// <summary>Minimum class score to accept a detection.</summary>
        public virtual double CensorConfidence { get; set; } = 0.35;

        /// <summary>How often a frame is sampled for detection, in milliseconds.</summary>
        public virtual int CensorIntervalMs { get; set; } = 125;

        /// <summary>How long a box survives after the detector stops reporting it, in milliseconds.</summary>
        public virtual int CensorHoldMs { get; set; } = 450;

        /// <summary>Bit N set = censor class N. See <see cref="Censoring.CensorClasses"/>.</summary>
        public virtual int CensorClassMask { get; set; } = Censoring.CensorClasses.DefaultMask;

        /// <summary>Fraction of its own size each box grows by, so edges are not clipped.</summary>
        public virtual double CensorPadding { get; set; } = 0.04;

        /// <summary>Fill colour of the censor box, as #AARRGGBB or #RRGGBB.</summary>
        public virtual string CensorColor { get; set; } = "#FF000000";

        /// <summary>Corner rounding of the censor box, in device-independent pixels.</summary>
        public virtual double CensorCornerRadius { get; set; } = 0;

        /// <summary>Draws class name and confidence on each box. Diagnostics only.</summary>
        public virtual bool CensorShowLabels { get; set; } = false;

        // History Settings

        public virtual bool RememberLastPlaylist { get; set; } = true;
        public virtual bool RememberFilePosition { get; set; } = true;
        public System.Collections.Generic.List<string> PlayedHistory { get; set; } = new System.Collections.Generic.List<string>();
        public virtual bool VideoShuffle { get; set; } = false;

        // Logging Settings
        public LogLevel LogLevel { get; set; } = LogLevel.Warning;

        /// <summary>
        /// When enabled, a separate diagnostics.log is generated with full debug detail.
        /// The main EdgeLoop.log continues to log warnings/errors only.
        /// </summary>
        /// <remarks>
        /// The JSON property name is kept as "EnableVerboseLogging" for backward
        /// compatibility — existing settings.json files are migrated transparently.
        /// </remarks>
        [JsonPropertyName("EnableVerboseLogging")]
        public virtual bool EnableDiagnosticMode { get; set; } = false;

        // Playlist Import Settings
        public int MaxPlaylistPages { get; set; } = 100; // Maximum pages to fetch for paginated playlists

        public DateTime LastYtDlpUpdateCheck { get; set; } = DateTime.MinValue;

        // UI State
        public string LastExpandedSection { get; set; } = "IsPlaybackExpanded";


        public double LauncherWindowWidth { get; set; } = 700;
        public double LauncherWindowHeight { get; set; } = 750;
        public double LauncherWindowTop { get; set; } = -1;
        public double LauncherWindowLeft { get; set; } = -1;

        // Caching Settings
        public virtual bool EnableLocalCaching { get; set; } = true;
        public virtual string LocalCacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeLoop", "VideoCache");

        public static string SettingsFilePath
        {
            get => AppPaths.SettingsFile;
            internal set => throw new InvalidOperationException("Path is now managed by AppPaths");
        }

        public static UserSettings Load()
        {
            try
            {
                // Migration Check 1: Old TrainMeX AppData to new EdgeLoop data directory
                var oldAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrainMeX");
                var newDataDir = AppPaths.DataDirectory;
                if (Directory.Exists(oldAppData) && !File.Exists(SettingsFilePath))
                {
                    try
                    {
                        Logger.Debug("Migrating TrainMeX settings to EdgeLoop...");
                        foreach (var file in Directory.GetFiles(oldAppData))
                        {
                            var destFile = Path.Combine(newDataDir, Path.GetFileName(file));
                            if (!File.Exists(destFile))
                            {
                                File.Copy(file, destFile);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Failed to migrate settings from old AppData", ex);
                    }
                }

                // Migration Check 2: If local settings exist but AppData doesn't, migrate them
                var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                if (File.Exists(localPath) && !File.Exists(SettingsFilePath))
                {
                    try
                    {
                        Logger.Debug("Migrating legacy local settings to AppData...");
                        File.Copy(localPath, SettingsFilePath);
                        // Optional: File.Delete(localPath); // Keep for safety for now
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Failed to migrate local settings", ex);
                    }
                }

                if (File.Exists(SettingsFilePath))
                {
                    string json = SafeFileReader.ReadAllTextSafe(SettingsFilePath);
                    if (string.IsNullOrEmpty(json)) return new UserSettings();
                    try
                    {
                        var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();

                        // Migration logic
                        if (settings.SettingsVersion < 1)
                        {
                            settings.SettingsVersion = 1;
                            settings.Save();
                            Logger.Debug("Settings migrated to version 1");
                        }

                        // Validate and clamp loaded values
                        settings.ValidateAndClampValues();
                        Logger.MinimumLevel = settings.LogLevel;

                        // Migration: Upgrade default browser from 'chrome' to 'Firefox' if it was the default
                        if (settings.BrowserForCookies == "chrome")
                        {
                            settings.BrowserForCookies = "Firefox";
                        }

                        Logger.Debug($"Loaded settings from {SettingsFilePath}");
                        return settings;
                    }
                    catch (JsonException jex)
                    {
                        Logger.Warning($"Settings file at {SettingsFilePath} is corrupted. Renaming to .bak and using defaults.", jex);
                        try
                        {
                            var bakPath = SettingsFilePath + ".bak";
                            if (File.Exists(bakPath)) File.Delete(bakPath);
                            File.Move(SettingsFilePath, bakPath);
                        }
                        catch (Exception moveEx)
                        {
                            Logger.Error("Failed to backup corrupted settings file", moveEx);
                        }
                        return new UserSettings();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to load settings, using defaults", ex);
            }
            return new UserSettings();
        }

        /// <summary>
        /// Validates and clamps opacity and volume values to valid ranges (0.0-1.0)
        /// </summary>
        private void ValidateAndClampValues()
        {
            bool valuesCorrected = false;

            if (!UpscalingSupport.IsMode(UpscalingQuality) || UpscalingQuality == UpscalingSupport.Off)
            {
                UpscalingQuality = "Sharp";
                valuesCorrected = true;
            }

            if (Opacity < 0.0 || Opacity > 1.0)
            {
                var oldValue = Opacity;
                Opacity = Math.Max(0.0, Math.Min(1.0, Opacity));
                Logger.Warning($"Opacity value {oldValue} was out of range (0.0-1.0), clamped to {Opacity}");
                valuesCorrected = true;
            }

            if (Volume < 0.0 || Volume > 1.0)
            {
                var oldValue = Volume;
                Volume = Math.Max(0.0, Math.Min(1.0, Volume));
                Logger.Warning($"Volume value {oldValue} was out of range (0.0-1.0), clamped to {Volume}");
                valuesCorrected = true;
            }

            if (DefaultOpacity < 0.0 || DefaultOpacity > 1.0)
            {
                var oldValue = DefaultOpacity;
                DefaultOpacity = Math.Max(0.0, Math.Min(1.0, DefaultOpacity));
                Logger.Warning($"DefaultOpacity value {oldValue} was out of range (0.0-1.0), clamped to {DefaultOpacity}");
                valuesCorrected = true;
            }

            if (DefaultVolume < 0.0 || DefaultVolume > 1.0)
            {
                var oldValue = DefaultVolume;
                DefaultVolume = Math.Max(0.0, Math.Min(1.0, DefaultVolume));
                Logger.Warning($"DefaultVolume value {oldValue} was out of range (0.0-1.0), clamped to {DefaultVolume}");
                valuesCorrected = true;
            }


            // Ensure window dimensions are valid and finite
            if (!double.IsFinite(LauncherWindowWidth) || LauncherWindowWidth <= 100) { LauncherWindowWidth = 700; valuesCorrected = true; }
            if (!double.IsFinite(LauncherWindowHeight) || LauncherWindowHeight <= 100) { LauncherWindowHeight = 750; valuesCorrected = true; }
            if (!double.IsFinite(LauncherWindowTop)) { LauncherWindowTop = -1; valuesCorrected = true; }
            if (!double.IsFinite(LauncherWindowLeft)) { LauncherWindowLeft = -1; valuesCorrected = true; }

            // Validate PlaybackState
            if (LastPlaybackState != null)
            {
                if (double.IsNaN(LastPlaybackState.SpeedRatio) || double.IsInfinity(LastPlaybackState.SpeedRatio))
                {
                    LastPlaybackState.SpeedRatio = 1.0;
                    valuesCorrected = true;
                }
            }

            if (valuesCorrected)
            {
                // Save corrected values back to file
                try
                {
                    Save();
                }
                catch (Exception ex)
                {
                    Logger.Warning("Failed to save corrected settings values", ex);
                }
            }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals |
                             System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };
        private readonly System.Threading.SemaphoreSlim _saveLock = new System.Threading.SemaphoreSlim(1, 1);

        public void Save()
        {
            try
            {
                _saveLock.Wait();
                try
                {
                    string json = JsonSerializer.Serialize(this, _jsonOptions);
                    AtomicFile.WriteAllText(SettingsFilePath, json);
                }
                finally
                {
                    _saveLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save settings (Sync)", ex);
            }
        }

        public async System.Threading.Tasks.Task SaveAsync()
        {
            try
            {
                await _saveLock.WaitAsync();
                try
                {
                    string json = JsonSerializer.Serialize(this, _jsonOptions);
                    await AtomicFile.WriteAllTextAsync(SettingsFilePath, json);
                }
                finally
                {
                    _saveLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save settings (Async)", ex);
            }
        }

        // --- Session Persistence ---

        // --- Session Persistence ---

        // Heavy data (Playlist) - Saved to session.json only on change
        [System.Text.Json.Serialization.JsonIgnore]
        public Playlist CurrentSessionPlaylist { get; set; } = new Playlist();

        // Light data (Position/Index) - Saved to settings.json continuously
        public PlaybackState LastPlaybackState { get; set; } = new PlaybackState();

        public static string SessionFilePath => AppPaths.SessionFile;

        private readonly System.Threading.SemaphoreSlim _sessionLock = new System.Threading.SemaphoreSlim(1, 1);

        public async System.Threading.Tasks.Task SaveSessionAsync()
        {
            try
            {
                if (CurrentSessionPlaylist == null) return;
                await _sessionLock.WaitAsync();
                try
                {
                    string json = JsonSerializer.Serialize(CurrentSessionPlaylist, _jsonOptions);
                    await AtomicFile.WriteAllTextAsync(SessionFilePath, json);
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to save session playlist (Async)", ex);
            }
        }

        public void SaveSession()
        {
            try
            {
                if (CurrentSessionPlaylist == null) return;
                _sessionLock.Wait();
                try
                {
                    string json = JsonSerializer.Serialize(CurrentSessionPlaylist, _jsonOptions);
                    AtomicFile.WriteAllText(SessionFilePath, json);
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to save session playlist (Sync)", ex);
            }
        }

        public void LoadSession()
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    string json = SafeFileReader.ReadAllTextSafe(SessionFilePath);
                    if (string.IsNullOrEmpty(json)) return;
                    CurrentSessionPlaylist = JsonSerializer.Deserialize<Playlist>(json) ?? new Playlist();

                    // Migration logic
                    if (CurrentSessionPlaylist.Version < 1)
                    {
                        CurrentSessionPlaylist.Version = 1;
                        SaveSession();
                        Logger.Debug("Session playlist migrated to version 1");
                    }

                    Logger.Debug("Loaded previous session playlist");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to load session playlist", ex);
                CurrentSessionPlaylist = new Playlist();
            }
        }
    }

    public class PlaybackState
    {
        public int CurrentIndex { get; set; } = 0;
        public long PositionTicks { get; set; } = 0;
        public double SpeedRatio { get; set; } = 1.0;
        public DateTime LastPlayed { get; set; } = DateTime.MinValue;
    }

    /// <summary>One screen's own skip keys. An empty key means that direction has none.</summary>
    public class ScreenSkipKeys
    {
        public uint ForwardModifiers { get; set; }
        public string ForwardKey { get; set; }
        public uint BackwardModifiers { get; set; }
        public string BackwardKey { get; set; }

        [JsonIgnore] public HotkeyCombo Forward => new(ForwardModifiers, ForwardKey);
        [JsonIgnore] public HotkeyCombo Backward => new(BackwardModifiers, BackwardKey);
    }
}
