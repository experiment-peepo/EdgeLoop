using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EdgeLoop.Classes
{
    /// <summary>One monitor's HDR state as Windows reports it. <paramref name="Path"/> is its device path, which stays the same across restarts.</summary>
    public sealed record HdrMonitor(string Path, bool Supported, bool Enabled);

    /// <summary>Reads and switches Windows HDR per monitor (the real one is <see cref="WindowsHdrDisplays"/>).</summary>
    public interface IHdrDisplays
    {
        /// <summary>The monitor showing this screen (\\.\DISPLAYn), or null if Windows doesn't say.</summary>
        HdrMonitor Find(string screenDevice);

        /// <summary>Switches HDR on or off for the monitor with this device path. False if Windows refused.</summary>
        bool Switch(string monitorPath, bool on);
    }

    /// <summary>
    /// Switches Windows HDR on for a monitor while EdgeLoop plays there, and back off afterwards. Only monitors that
    /// support HDR and have it off are touched, so HDR the user switched on stays theirs. Before switching, it writes
    /// down what it switched (hdr_restore.json), so if EdgeLoop crashes the next start puts it back.
    /// </summary>
    public sealed class HdrSwitcher
    {
        private static HdrSwitcher _shared;

        /// <summary>The app's switcher, with its record in EdgeLoop's data folder.</summary>
        [SupportedOSPlatform("windows")]
        public static HdrSwitcher Shared => _shared ??= new HdrSwitcher(new WindowsHdrDisplays(), Path.Combine(AppPaths.DataDirectory, "hdr_restore.json"));

        private readonly IHdrDisplays _displays;
        private readonly string _recordPath;
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _switched = new(StringComparer.OrdinalIgnoreCase); // Screen device -> monitor path

        public HdrSwitcher(IHdrDisplays displays, string recordPath)
        {
            _displays = displays;
            _recordPath = recordPath;
        }

        /// <summary>
        /// Whether playback on these screens (\\.\DISPLAYn) draws its video in windows of its own, through FlyleafLib's swap
        /// chain (<see cref="HdrVideoSurface"/>), instead of through WPF: with HDR on in Settings and at least one of them
        /// on a monitor that supports HDR. Then all of them do, since WPF stops showing video on the other screens while a
        /// monitor is in HDR. Monitors that support HDR are switched to it; the others play in SDR.
        /// </summary>
        public bool UsesOwnVideoWindows(bool useHdr, IEnumerable<string> screenDevices) =>
            useHdr && screenDevices.Any(screen => screen != null && _displays.Find(screen) is { Supported: true });

        /// <summary>Monitors this switched on and hasn't switched back yet.</summary>
        public IReadOnlyCollection<string> SwitchedOn
        {
            get { lock (_gate) return _switched.Values.ToList(); }
        }

        /// <summary>
        /// Makes sure HDR is on for this screen's monitor, if it can show HDR. True when it's on, whether this switched it
        /// or the user had it on already; false when the monitor can't, or Windows refused.
        /// </summary>
        public bool SwitchOn(string screenDevice)
        {
            var monitor = _displays.Find(screenDevice);
            if (monitor is not { Supported: true })
                return false;
            if (monitor.Enabled)
                return true; // The user's own setting: never touched

            lock (_gate)
            {
                if (_switched.ContainsKey(screenDevice))
                    return true;

                // Written down first, so a crash while switching is still put right
                _switched[screenDevice] = monitor.Path;
                SaveRecord();
            }

            if (_displays.Switch(monitor.Path, true))
            {
                Logger.Info($"[HDR] Switched HDR on for {screenDevice}");
                return true;
            }

            Logger.Warning($"[HDR] Windows didn't switch HDR on for {screenDevice}");
            lock (_gate)
            {
                _switched.Remove(screenDevice);
                SaveRecord();
            }
            return false;
        }

        /// <summary>Switches HDR back off for this screen's monitor, if this switched it on.</summary>
        public void Restore(string screenDevice)
        {
            string path;
            lock (_gate)
            {
                if (!_switched.TryGetValue(screenDevice, out path))
                    return;
            }

            bool restored = _displays.Switch(path, false);
            if (restored)
                Logger.Info($"[HDR] Switched HDR back off for {screenDevice}");
            else
                Logger.Warning($"[HDR] Couldn't switch HDR back off for {screenDevice}; trying again at the next start");

            lock (_gate)
            {
                if (restored)
                    _switched.Remove(screenDevice);
                SaveRecord();
            }
        }

        /// <summary>Switches back everything this switched on (EdgeLoop is closing).</summary>
        public void RestoreAll()
        {
            List<string> screens;
            lock (_gate)
                screens = _switched.Keys.ToList();
            foreach (var screen in screens)
                Restore(screen);
        }

        /// <summary>At startup: switches back what a previous run switched on but didn't put back (it crashed). Returns how many.</summary>
        public int RecoverFromRecord()
        {
            List<string> paths;
            try
            {
                if (!File.Exists(_recordPath))
                    return 0;
                paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_recordPath)) ?? new List<string>();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[HDR] Couldn't read {_recordPath}: {ex.Message}");
                paths = new List<string>();
            }

            int restored = 0;
            var left = new List<string>();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_displays.Switch(path, false))
                    restored++;
                else
                    left.Add(path); // Not connected now, perhaps: next time
            }

            if (restored > 0)
                Logger.Info($"[HDR] Switched HDR back off for {restored} monitor(s) left on by a previous run");

            lock (_gate)
                WriteRecord(left);
            return restored;
        }

        private void SaveRecord() => WriteRecord(_switched.Values.ToList());

        private void WriteRecord(List<string> paths)
        {
            try
            {
                if (paths.Count == 0)
                {
                    if (File.Exists(_recordPath))
                        File.Delete(_recordPath);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
                AtomicFile.WriteAllText(_recordPath, JsonSerializer.Serialize(paths));
            }
            catch (Exception ex)
            {
                Logger.Warning($"[HDR] Couldn't write {_recordPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Windows' per-monitor HDR state and switch (DisplayConfigGetDeviceInfo / DisplayConfigSetDeviceInfo). Uses the
    /// Windows 11 24H2 HDR calls, and the older "advanced colour" ones where those aren't there.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed unsafe class WindowsHdrDisplays : IHdrDisplays
    {
        private const uint GetSourceName = 1, GetTargetName = 2, GetAdvancedColorInfo = 9, SetAdvancedColorState = 10;
        private const uint GetAdvancedColorInfo2 = 15, SetHdrState = 16;
        private const uint OnlyActivePaths = 2;

        public HdrMonitor Find(string screenDevice)
        {
            foreach (var (gdi, path, adapter, id) in Monitors())
            {
                if (!string.Equals(gdi, screenDevice, StringComparison.OrdinalIgnoreCase))
                    continue;

                var info2 = new ColorInfo2 { Header = Header(GetAdvancedColorInfo2, sizeof(ColorInfo2), adapter, id) };
                if (DisplayConfigGetDeviceInfo(&info2.Header) == 0)
                    return new HdrMonitor(path, Supported: (info2.Value & 0x10) != 0, Enabled: (info2.Value & 0x20) != 0);

                var info = new ColorInfo { Header = Header(GetAdvancedColorInfo, sizeof(ColorInfo), adapter, id) };
                if (DisplayConfigGetDeviceInfo(&info.Header) == 0)
                    return new HdrMonitor(path, Supported: (info.Value & 1) != 0, Enabled: (info.Value & 2) != 0);
                return null;
            }
            return null;
        }

        public bool Switch(string monitorPath, bool on)
        {
            foreach (var (_, path, adapter, id) in Monitors())
            {
                if (!string.Equals(path, monitorPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var state = new SetState { Header = Header(SetHdrState, sizeof(SetState), adapter, id), Value = on ? 1u : 0u };
                if (DisplayConfigSetDeviceInfo(&state.Header) == 0)
                    return true;

                state.Header.Type = SetAdvancedColorState;
                return DisplayConfigSetDeviceInfo(&state.Header) == 0;
            }
            return false;
        }

        /// <summary>Each active monitor: its screen device (\\.\DISPLAYn), device path, adapter and target.</summary>
        private static List<(string Gdi, string Path, Luid Adapter, uint Id)> Monitors()
        {
            var result = new List<(string, string, Luid, uint)>();
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
                return result;

            var paths = new PathInfo[pathCount];
            var modes = new byte[modeCount * 64];
            fixed (byte* m = modes)
            {
                if (QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, m, nint.Zero) != 0)
                    return result;
            }

            for (int i = 0; i < pathCount; i++)
            {
                var source = new SourceName { Header = Header(GetSourceName, sizeof(SourceName), paths[i].SourceAdapter, paths[i].SourceId) };
                var target = new TargetName { Header = Header(GetTargetName, sizeof(TargetName), paths[i].TargetAdapter, paths[i].TargetId) };
                if (DisplayConfigGetDeviceInfo(&source.Header) != 0 || DisplayConfigGetDeviceInfo(&target.Header) != 0)
                    continue;

                result.Add((new string(source.Name).TrimEnd('\0'), new string(target.Path).TrimEnd('\0'), paths[i].TargetAdapter, paths[i].TargetId));
            }
            return result;
        }

        private static DeviceInfoHeader Header(uint type, int size, Luid adapter, uint id) =>
            new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, byte* modeArray, nint topology);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(DeviceInfoHeader* packet);
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(DeviceInfoHeader* packet);

        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo
        {
            public Luid SourceAdapter;
            public uint SourceId, SourceMode, SourceStatus;
            public Luid TargetAdapter;
            public uint TargetId, TargetMode, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering, Available, TargetStatus;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader { public uint Type, Size; public Luid AdapterId; public uint Id; }
        [StructLayout(LayoutKind.Sequential)] private struct SourceName { public DeviceInfoHeader Header; public fixed char Name[32]; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetName
        {
            public DeviceInfoHeader Header;
            public uint Flags, Technology;
            public ushort Manufacturer, Product;
            public uint Connector;
            public fixed char FriendlyName[64];
            public fixed char Path[128];
        }

        [StructLayout(LayoutKind.Sequential)] private struct ColorInfo { public DeviceInfoHeader Header; public uint Value, Encoding, BitsPerChannel; }
        [StructLayout(LayoutKind.Sequential)] private struct ColorInfo2 { public DeviceInfoHeader Header; public uint Value, Encoding, BitsPerChannel, ActiveMode; }
        [StructLayout(LayoutKind.Sequential)] private struct SetState { public DeviceInfoHeader Header; public uint Value; }
    }
}
