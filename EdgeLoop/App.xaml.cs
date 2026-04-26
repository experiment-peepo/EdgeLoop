/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>. 
*/

using EdgeLoop.Classes;
using System;
using System.Windows;

namespace EdgeLoop
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class App : System.Windows.Application
    {
        public static VideoPlayerService VideoService => ServiceContainer.TryGet<VideoPlayerService>(out var service) ? service : null;
        public static UserSettings Settings => ServiceContainer.TryGet<UserSettings>(out var settings) ? settings : null;
        public static HotkeyService Hotkeys => ServiceContainer.TryGet<HotkeyService>(out var hotkeys) ? hotkeys : null;

        public static TelemetryService Telemetry => ServiceContainer.TryGet<TelemetryService>(out var telemetry) ? telemetry : null;
        public static IVideoUrlExtractor UrlExtractor => ServiceContainer.TryGet<IVideoUrlExtractor>(out var extractor) ? extractor : null;

        private System.Threading.CancellationTokenSource _cookieCleanupCts;
        private System.Threading.Tasks.Task _cookieCleanupTask;

        private static System.Threading.Mutex _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "EdgeLoop-Global-Instance-Mutex-v1";
            bool createdNew;

            _mutex = new System.Threading.Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // Single Instance Check: App is already running
                MessageBox.Show("EdgeLoop is already running.", "EdgeLoop", MessageBoxButton.OK, MessageBoxImage.Information);
                this.Shutdown();
                return;
            }

            // 1. Add global exception handlers FIRST so we can catch initialization crashes
            this.DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    Classes.Logger.Error("Unhandled exception in UI thread", args.Exception);
                    if (args.Exception.InnerException != null)
                    {
                        Classes.Logger.Error("Inner exception", args.Exception.InnerException);
                    }
                    Console.Error.WriteLine($"Unhandled Exception: {args.Exception}");

                    var userMessage = "An unexpected error occurred in the application.\n\n" +
                                     args.Exception.Message + "\n\n" +
                                     "The error details have been logged.";

                    MessageBox.Show(userMessage, "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    args.Handled = true;
                }
                catch
                {
                    args.Handled = true;
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    Logger.Error("Fatal unhandled exception", ex);
                    MessageBox.Show("A critical error occurred: " + ex?.Message, "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            };

            // 1.5. Rotate logs if too large (20MB main, 10MB diagnostics)
            Classes.Logger.CheckAndRotateLogFile(20 * 1024 * 1024);
            Classes.Logger.CheckAndRotateDiagnosticsLogFile(10 * 1024 * 1024);

            // 2. Resolve Log Level (Command Line > Env Var > Settings)
            var resolvedLogLevel = ResolveLogLevel(e.Args);
            Classes.Logger.MinimumLevel = resolvedLogLevel;

            // 3. Initialize Flyleaf Engine with robust paths and detailed logging
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string ffmpegPath = System.IO.Path.Combine(baseDir, "FFmpeg");

                // If FFmpeg folder doesn't exist or doesn't contain DLLs, check Dependencies
                bool hasDlls = System.IO.Directory.Exists(ffmpegPath) &&
                              System.IO.Directory.GetFiles(ffmpegPath, "avcodec*.dll").Length > 0;

                if (!hasDlls)
                {
                    string altPath = System.IO.Path.Combine(baseDir, "Dependencies");
                    if (System.IO.Directory.Exists(altPath) &&
                        System.IO.Directory.GetFiles(altPath, "avcodec*.dll").Length > 0)
                    {
                        ffmpegPath = altPath;
                    }
                }
                string pluginsPath = System.IO.Path.Combine(baseDir, "Plugins");

                // Redirect Flyleaf logs to diagnostic log only (Debug level)
                FlyleafLib.Logger.CustomOutput = (msg) =>
                {
                    Classes.Logger.Debug(msg);
                };

                FlyleafLib.Engine.Start(new FlyleafLib.EngineConfig()
                {
                    FFmpegPath = ffmpegPath,
                    PluginsPath = pluginsPath,
                    UIRefresh = true,
                    // Flyleaf always generates messages at Info level;
                    // our Logger.Debug() routes them to diagnostics.log only (not the main log)
                    LogLevel = resolvedLogLevel == Classes.LogLevel.Debug ? FlyleafLib.LogLevel.Debug : FlyleafLib.LogLevel.Info,
                    LogOutput = ":custom",
                    FFmpegLogLevel = resolvedLogLevel == Classes.LogLevel.Debug ? Flyleaf.FFmpeg.LogLevel.Info : Flyleaf.FFmpeg.LogLevel.Warn
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize Flyleaf Engine", ex);
                MessageBox.Show("Failed to initialize Video Engine:\n" + ex.Message, "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // We should probably exit if the engine fails to start
                this.Shutdown();
                return;
            }

            // Register Services
            var settings = UserSettings.Load();
            // Apply diagnostic mode from settings
            Classes.Logger.DiagnosticMode = settings.EnableDiagnosticMode;
            // Apply settings log level only if not overridden by CLI/Env
            if (resolvedLogLevel == Classes.LogLevel.Warning && settings.LogLevel != Classes.LogLevel.Warning)
            {
                Classes.Logger.MinimumLevel = settings.LogLevel;
            }
            ServiceContainer.Register(settings);

            // Ensure Playlists directory exists
            try
            {
                if (!System.IO.Directory.Exists(AppPaths.PlaylistsDirectory))
                {
                    System.IO.Directory.CreateDirectory(AppPaths.PlaylistsDirectory);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to create Playlists directory: {ex.Message}");
            }

            // 4. Initialize Core Services
            ServiceContainer.Register(settings);

            // Startup Repair: If "Start with Windows" is enabled, verify the registry path matches the current executable path
            // This handles cases where the user moves the EdgeLoop folder.
            if (settings.StartWithWindows)
            {
                try
                {
                    StartupManager.SetStartup(true);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Startup path repair failed: {ex.Message}");
                }
            }

            var videoService = new VideoPlayerService();
            ServiceContainer.Register<IVideoPlayerService>(videoService);
            ServiceContainer.Register(videoService);
            ServiceContainer.Register(new HotkeyService());
            var ytDlpService = new YtDlpService();
            ServiceContainer.Register(ytDlpService);

            ServiceContainer.Register<IVideoUrlExtractor>(new VideoUrlExtractor(null, ytDlpService));

            ServiceContainer.Register(new TelemetryService());
            var historyService = new PlayHistoryService();
            ServiceContainer.Register(historyService);

            // Prune old history records in background
            System.Threading.Tasks.Task.Run(() => historyService.PruneOldRecords());

            // Cleanup old cached videos (10+ days old) in background
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var downloadService = new VideoDownloadService();
                    downloadService.CleanupOldFiles(10);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to cleanup video cache: {ex.Message}");
                }
            });

            // Security: Clean up any orphaned temp cookie files from previous sessions
            _cookieCleanupCts = new System.Threading.CancellationTokenSource();
            _cookieCleanupTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var tempDir = System.IO.Path.GetTempPath();
                    foreach (var file in System.IO.Directory.GetFiles(tempDir, "edgeloop_cookies_*.txt"))
                    {
                        if (_cookieCleanupCts.Token.IsCancellationRequested) break;
                        try { System.IO.File.Delete(file); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Cookie temp cleanup: {ex.Message}");
                }
            }, _cookieCleanupCts.Token);

            // Security: Migrate legacy plaintext cookies to encrypted form
            if (settings.HypnotubeCookiesEncrypted != null && !CookieProtector.IsEncrypted(settings.HypnotubeCookiesEncrypted))
            {
                Logger.Debug("Migrating plaintext cookies to encrypted storage...");
                settings.HypnotubeCookies = settings.HypnotubeCookiesEncrypted; // Re-set triggers encryption
                settings.Save();
            }
            if (settings.CookiesEncrypted != null && !CookieProtector.IsEncrypted(settings.CookiesEncrypted))
            {
                settings.Cookies = settings.CookiesEncrypted;
                settings.Save();
            }

            // base.OnStartup starts the UI - call LAST
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Debug("Application exiting - starting shutdown sequence...");

            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch { /* Ignore errors on exit */ }

            // Start a watchdog timer to force exit if shutdown hangs (e.g., due to lingering FFmpeg or Flyleaf threads)
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(5000);
                Logger.Warning("Shutdown is taking too long (5s). Force exiting process to prevent background lingering.");
                Environment.Exit(0);
            });

            // Clean up tasks
            try
            {
                if (_cookieCleanupTask != null && !_cookieCleanupTask.IsCompleted)
                {
                    _cookieCleanupCts?.Cancel();
                    _cookieCleanupTask.Wait(TimeSpan.FromMilliseconds(500));
                }
            }
            catch { /* ignore wait errors on exit */ }

            try
            {
                // 1. Stop all video players and close windows (this also saves per-item positions)
                if (VideoService != null)
                {
                    Logger.Debug("Stopping all video players...");
                    VideoService.StopAll();
                }

                // 2. Explicitly save play history on exit (synchronous to avoid deadlocks)
                if (ServiceContainer.TryGet<PlayHistoryService>(out var historyService))
                {
                    Logger.Debug("Saving play history...");
                    historyService.SaveHistorySync();
                }

                // SESSION RESUME: Final save of positions
                Logger.Debug("Saving playback positions...");
                PlaybackPositionTracker.Instance.SaveSync();

                // Ensure hotkeys are unregistered
                if (ServiceContainer.TryGet<HotkeyService>(out var hotkeyService))
                {
                    hotkeyService.Dispose();
                }

                // Final save of settings (ensure window bounds etc are preserved)
                Settings?.Save();

                Logger.Debug("Shutdown sequence complete.");
            }
            catch (Exception ex)
            {
                Logger.Error("Error during shutdown sequence", ex);
            }
            finally
            {
                base.OnExit(e);

                // Final failsafe: force exit if we reached here but process is still alive
                Logger.Debug("Application exiting normally via Environment.Exit.");
                Environment.Exit(0);
            }
        }

        private Classes.LogLevel ResolveLogLevel(string[] args)
        {
            // 1. Check Command Line
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i].ToLowerInvariant();
                    if (arg == "--debug" || arg == "-d") return Classes.LogLevel.Debug;
                    if (arg == "--verbose" || arg == "-v") return Classes.LogLevel.Info;
                    if (arg == "--loglevel" && i + 1 < args.Length)
                    {
                        if (Enum.TryParse<Classes.LogLevel>(args[i + 1], true, out var level)) return level;
                    }
                }
            }

            // 2. Check Environment Variable
            var envLevel = Environment.GetEnvironmentVariable("EDGELOOP_LOG_LEVEL");
            if (!string.IsNullOrEmpty(envLevel) && Enum.TryParse<Classes.LogLevel>(envLevel, true, out var eLevel))
            {
                return eLevel;
            }

            // 3. Check for debug.enabled trigger file
            if (System.IO.File.Exists(System.IO.Path.Combine(AppPaths.DataDirectory, "debug.enabled")))
            {
                return Classes.LogLevel.Debug;
            }

            // Default to Warning — only errors and warnings in the main log
            return Classes.LogLevel.Warning;
        }
    }
}


