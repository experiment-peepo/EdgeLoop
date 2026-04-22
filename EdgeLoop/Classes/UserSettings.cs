using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeLoop.Classes {
    public enum ShuffleMode {
        Sequential,
        Simple,
        Smart,
        Random
    }


    public class UserSettings {
        public int SettingsVersion { get; set; } = 1;
        public virtual double Opacity { get; set; } = 0.2;
        public virtual double Volume { get; set; } = 0.5;

        public bool LauncherAlwaysOnTop { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
        public double DefaultOpacity { get; set; } = 0.9;
        public double DefaultVolume { get; set; } = 0.5;
        public string DefaultMonitorDeviceName { get; set; } = null;
        // --- Cookie storage: encrypted at rest via DPAPI ---
        // The JSON file stores the DPAPI-encrypted value.
        // Use the Get/Set helpers below for plaintext access at runtime.

        [JsonPropertyName("HypnotubeCookies")]
        public virtual string HypnotubeCookiesEncrypted { get; set; } = null;

        [JsonPropertyName("Cookies")]
        public virtual string CookiesEncrypted { get; set; } = null;

        public virtual string UserAgent { get; set; } = null;

        /// <summary>
        /// Gets the decrypted Hypnotube cookies for runtime use.
        /// Handles both legacy plaintext and DPAPI-encrypted values.
        /// </summary>
        [JsonIgnore]
        public string HypnotubeCookies {
            get => CookieProtector.Unprotect(HypnotubeCookiesEncrypted);
            set => HypnotubeCookiesEncrypted = CookieProtector.Protect(value);
        }

        /// <summary>
        /// Gets the decrypted global cookies for runtime use.
        /// </summary>
        [JsonIgnore]
        public string Cookies {
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

        public virtual bool AlwaysOpaque { get; set; } = false;
        public virtual bool EnableSuperResolution { get; set; } = false;
        
        // History Settings

        public virtual bool RememberLastPlaylist { get; set; } = true;
        public virtual bool RememberFilePosition { get; set; } = true;
        public System.Collections.Generic.List<string> PlayedHistory { get; set; } = new System.Collections.Generic.List<string>();
        public virtual bool VideoShuffle { get; set; } = false;
        public virtual ShuffleMode CurrentShuffleMode { get; set; } = ShuffleMode.Smart;
        public virtual double ShuffleRecencyWeight { get; set; } = 0.3;
        public virtual double ShufflePreferenceWeight { get; set; } = 0.3;
        public virtual double ShuffleVarietyWeight { get; set; } = 0.2;
        public virtual double ShuffleLengthWeight { get; set; } = 0.2;
        public virtual bool EnableShuffleDebugLog { get; set; } = false;
        
        // Logging Settings
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        // Playlist Import Settings
        public int MaxPlaylistPages { get; set; } = 100; // Maximum pages to fetch for paginated playlists

        // UI State
        public string LastExpandedSection { get; set; } = "IsPlaybackExpanded";
        public bool CompactMode { get; set; } = false;

        public double LauncherWindowWidth { get; set; } = 700;
        public double LauncherWindowHeight { get; set; } = 750;
        public double LauncherWindowTop { get; set; } = -1;
        public double LauncherWindowLeft { get; set; } = -1;


        public static string SettingsFilePath {
            get => AppPaths.SettingsFile;
            internal set => throw new InvalidOperationException("Path is now managed by AppPaths");
        }

        public static UserSettings Load() {
            try {
                // Migration Check 1: Old TrainMeX AppData to new EdgeLoop data directory
                var oldAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrainMeX");
                var newDataDir = AppPaths.DataDirectory;
                if (Directory.Exists(oldAppData) && !File.Exists(SettingsFilePath)) {
                    try {
                        Logger.Info("Migrating TrainMeX settings to EdgeLoop...");
                        foreach (var file in Directory.GetFiles(oldAppData)) {
                            var destFile = Path.Combine(newDataDir, Path.GetFileName(file));
                            if (!File.Exists(destFile)) {
                                File.Copy(file, destFile);
                            }
                        }
                    } catch (Exception ex) {
                        Logger.Warning("Failed to migrate settings from old AppData", ex);
                    }
                }

                // Migration Check 2: If local settings exist but AppData doesn't, migrate them
                var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                if (File.Exists(localPath) && !File.Exists(SettingsFilePath)) {
                    try {
                        Logger.Info("Migrating legacy local settings to AppData...");
                        File.Copy(localPath, SettingsFilePath);
                        // Optional: File.Delete(localPath); // Keep for safety for now
                    } catch (Exception ex) {
                        Logger.Warning("Failed to migrate local settings", ex);
                    }
                }

                if (File.Exists(SettingsFilePath)) {
                    string json = SafeFileReader.ReadAllTextSafe(SettingsFilePath);
                    if (string.IsNullOrEmpty(json)) return new UserSettings();
                    try {
                        var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();
                        
                        // Migration logic
                        if (settings.SettingsVersion < 1) {
                            settings.SettingsVersion = 1;
                            settings.Save();
                            Logger.Info("Settings migrated to version 1");
                        }

                        // Validate and clamp loaded values
                        settings.ValidateAndClampValues();
                        Logger.MinimumLevel = settings.LogLevel;
                        Logger.Info($"Loaded settings from {SettingsFilePath}");
                        return settings;
                    } catch (JsonException jex) {
                        Logger.Warning($"Settings file at {SettingsFilePath} is corrupted. Renaming to .bak and using defaults.", jex);
                        try {
                            var bakPath = SettingsFilePath + ".bak";
                            if (File.Exists(bakPath)) File.Delete(bakPath);
                            File.Move(SettingsFilePath, bakPath);
                        } catch (Exception moveEx) {
                            Logger.Error("Failed to backup corrupted settings file", moveEx);
                        }
                        return new UserSettings();
                    }
                }
            } catch (Exception ex) {
                Logger.Warning("Failed to load settings, using defaults", ex);
            }
            return new UserSettings();
        }

        /// <summary>
        /// Validates and clamps opacity and volume values to valid ranges (0.0-1.0)
        /// </summary>
        private void ValidateAndClampValues() {
            bool valuesCorrected = false;
            
            if (Opacity < 0.0 || Opacity > 1.0) {
                var oldValue = Opacity;
                Opacity = Math.Max(0.0, Math.Min(1.0, Opacity));
                Logger.Warning($"Opacity value {oldValue} was out of range (0.0-1.0), clamped to {Opacity}");
                valuesCorrected = true;
            }
            
            if (Volume < 0.0 || Volume > 1.0) {
                var oldValue = Volume;
                Volume = Math.Max(0.0, Math.Min(1.0, Volume));
                Logger.Warning($"Volume value {oldValue} was out of range (0.0-1.0), clamped to {Volume}");
                valuesCorrected = true;
            }
            
            if (DefaultOpacity < 0.0 || DefaultOpacity > 1.0) {
                var oldValue = DefaultOpacity;
                DefaultOpacity = Math.Max(0.0, Math.Min(1.0, DefaultOpacity));
                Logger.Warning($"DefaultOpacity value {oldValue} was out of range (0.0-1.0), clamped to {DefaultOpacity}");
                valuesCorrected = true;
            }
            
            if (DefaultVolume < 0.0 || DefaultVolume > 1.0) {
                var oldValue = DefaultVolume;
                DefaultVolume = Math.Max(0.0, Math.Min(1.0, DefaultVolume));
                Logger.Warning($"DefaultVolume value {oldValue} was out of range (0.0-1.0), clamped to {DefaultVolume}");
                valuesCorrected = true;
            }

            // Ensure weights are valid numbers
            if (double.IsNaN(ShuffleRecencyWeight) || double.IsInfinity(ShuffleRecencyWeight)) { ShuffleRecencyWeight = 0.3; valuesCorrected = true; }
            if (double.IsNaN(ShufflePreferenceWeight) || double.IsInfinity(ShufflePreferenceWeight)) { ShufflePreferenceWeight = 0.3; valuesCorrected = true; }
            if (double.IsNaN(ShuffleVarietyWeight) || double.IsInfinity(ShuffleVarietyWeight)) { ShuffleVarietyWeight = 0.2; valuesCorrected = true; }
            if (double.IsNaN(ShuffleLengthWeight) || double.IsInfinity(ShuffleLengthWeight)) { ShuffleLengthWeight = 0.2; valuesCorrected = true; }

            // Ensure window dimensions are valid and finite
            if (!double.IsFinite(LauncherWindowWidth) || LauncherWindowWidth <= 100) { LauncherWindowWidth = 700; valuesCorrected = true; }
            if (!double.IsFinite(LauncherWindowHeight) || LauncherWindowHeight <= 100) { LauncherWindowHeight = 750; valuesCorrected = true; }
            if (!double.IsFinite(LauncherWindowTop)) { LauncherWindowTop = -1; valuesCorrected = true; }
            if (!double.IsFinite(LauncherWindowLeft)) { LauncherWindowLeft = -1; valuesCorrected = true; }

            // Validate PlaybackState
            if (LastPlaybackState != null) {
                if (double.IsNaN(LastPlaybackState.SpeedRatio) || double.IsInfinity(LastPlaybackState.SpeedRatio)) {
                    LastPlaybackState.SpeedRatio = 1.0;
                    valuesCorrected = true;
                }
            }

            if (valuesCorrected) {
                // Save corrected values back to file
                try {
                    Save();
                } catch (Exception ex) {
                    Logger.Warning("Failed to save corrected settings values", ex);
                }
            }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals | 
                             System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };
        private readonly System.Threading.SemaphoreSlim _saveLock = new System.Threading.SemaphoreSlim(1, 1);

        public void Save() {
            try {
                _saveLock.Wait();
                try {
                    string json = JsonSerializer.Serialize(this, _jsonOptions);
                    string tempPath = SettingsFilePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, SettingsFilePath, true);
                } finally {
                    _saveLock.Release();
                }
            } catch (Exception ex) {
                Logger.Error("Failed to save settings (Sync)", ex);
            }
        }

        public async System.Threading.Tasks.Task SaveAsync() {
            try {
                await _saveLock.WaitAsync();
                try {
                    string json = JsonSerializer.Serialize(this, _jsonOptions);
                    string tempPath = SettingsFilePath + ".tmp";
                    await File.WriteAllTextAsync(tempPath, json);
                    File.Move(tempPath, SettingsFilePath, true);
                } finally {
                    _saveLock.Release();
                }
            } catch (Exception ex) {
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

        public async System.Threading.Tasks.Task SaveSessionAsync() {
            try {
                if (CurrentSessionPlaylist == null) return;
                await _sessionLock.WaitAsync();
                try {
                    string json = JsonSerializer.Serialize(CurrentSessionPlaylist, _jsonOptions);
                    string tempPath = SessionFilePath + ".tmp";
                    await File.WriteAllTextAsync(tempPath, json);
                    File.Move(tempPath, SessionFilePath, true);
                } finally {
                    _sessionLock.Release();
                }
            } catch (Exception ex) {
                Logger.Warning("Failed to save session playlist (Async)", ex);
            }
        }

        public void SaveSession() {
            try {
                if (CurrentSessionPlaylist == null) return;
                _sessionLock.Wait();
                try {
                    string json = JsonSerializer.Serialize(CurrentSessionPlaylist, _jsonOptions);
                    string tempPath = SessionFilePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, SessionFilePath, true);
                } finally {
                    _sessionLock.Release();
                }
            } catch (Exception ex) {
                Logger.Warning("Failed to save session playlist (Sync)", ex);
            }
        }

        public void LoadSession() {
            try {
                if (File.Exists(SessionFilePath)) {
                    string json = SafeFileReader.ReadAllTextSafe(SessionFilePath);
                    if (string.IsNullOrEmpty(json)) return;
                    CurrentSessionPlaylist = JsonSerializer.Deserialize<Playlist>(json) ?? new Playlist();
                    
                    // Migration logic
                    if (CurrentSessionPlaylist.Version < 1) {
                        CurrentSessionPlaylist.Version = 1;
                        SaveSession();
                        Logger.Info("Session playlist migrated to version 1");
                    }

                    Logger.Info("Loaded previous session playlist");
                }
            } catch (Exception ex) {
                Logger.Warning("Failed to load session playlist", ex);
                CurrentSessionPlaylist = new Playlist();
            }
        }
    }

    public class PlaybackState {
        public int CurrentIndex { get; set; } = 0;
        public long PositionTicks { get; set; } = 0;
        public double SpeedRatio { get; set; } = 1.0;
        public DateTime LastPlayed { get; set; } = DateTime.MinValue;
    }
}

