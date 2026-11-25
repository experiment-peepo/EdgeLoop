using System;
using System.IO;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Centralized management of application data paths.
    /// Supports "Portable Mode" by checking for a local 'Data' folder.
    /// </summary>
    public static class AppPaths
    {
        private static string _dataDirectory;
        private static string _videoCacheDirectory;

        public static string DataDirectory
        {
            get
            {
                if (_dataDirectory == null)
                {
                    // Try to use local 'Data' folder for Portable Mode
                    var localData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

                    try
                    {
                        // Create it if it doesn't exist
                        if (!Directory.Exists(localData))
                        {
                            Directory.CreateDirectory(localData);
                        }

                        // If we can write to it, use it
                        if (Directory.Exists(localData))
                        {
                            _dataDirectory = localData;
                        }
                    }
                    catch
                    {
                        // If we can't create/access local 'Data' (e.g. Program Files), fallback to AppData
                        _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdgeLoop");

                        if (!Directory.Exists(_dataDirectory))
                        {
                            Directory.CreateDirectory(_dataDirectory);
                        }
                    }
                }
                return _dataDirectory;
            }
        }

        public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public static string LogFile => Path.Combine(DataDirectory, "EdgeLoop.log");
        public static string DiagnosticsLogFile => Path.Combine(DataDirectory, "diagnostics.log");
        public static string SessionFile => Path.Combine(DataDirectory, "session.json");
        public static string CacheFile => Path.Combine(DataDirectory, "urlcache.json");
        public static string TelemetryFile => Path.Combine(DataDirectory, "telemetry.json");
        public static string PositionsFile => Path.Combine(DataDirectory, "playback_positions.json");

        /// <summary>
        /// Where downloaded videos go unless the user picks another folder: EdgeLoop's own folder, with the
        /// rest of its data. If nothing can be written there (e.g. Program Files), the local profile.
        /// </summary>
        public static string VideoCacheDirectory
        {
            get
            {
                if (_videoCacheDirectory == null)
                {
                    var own = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "VideoCache");
                    try
                    {
                        Directory.CreateDirectory(own);
                        _videoCacheDirectory = own;
                    }
                    catch
                    {
                        _videoCacheDirectory = ProfileVideoCacheDirectory;
                    }
                }
                return _videoCacheDirectory;
            }
        }

        /// <summary>Where downloaded videos were kept before they moved into EdgeLoop's own folder.</summary>
        public static string ProfileVideoCacheDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeLoop", "VideoCache");

        /// <summary>The folder for downloaded videos, given the one in settings.</summary>
        public static string ResolveVideoCacheDirectory(string chosen) =>
            string.IsNullOrWhiteSpace(chosen) ? VideoCacheDirectory : chosen;

        public static bool IsSameDirectory(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        public static string PlaylistsDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Playlists");
    }
}

