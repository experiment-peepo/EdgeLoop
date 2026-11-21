using System;
using System.Collections.Generic;
using System.Linq;

namespace EdgeLoop.Classes
{
    /// <summary>Registers named global hotkeys. Implemented by <see cref="HotkeyService"/>.</summary>
    public interface IHotkeyRegistrar
    {
        bool Register(string name, uint modifiers, string keyName, Action callback);
        void Unregister(string name);
        bool IsRegistered(string name);
    }

    /// <summary>What a player needs to expose for hotkey skip targeting.</summary>
    public interface ISkipTarget
    {
        string MonitorName { get; }
        string SyncGroupId { get; }
        bool IsSyncMaster { get; }
    }

    /// <summary>The actions EdgeLoop's global hotkeys trigger.</summary>
    public sealed class PlaybackHotkeyActions
    {
        public Action Panic { get; init; }
        public Action WipeAndExit { get; init; }
        public Action OpaquePanic { get; init; }

        /// <summary>Skip on the targeted screen(s): true = next video, false = previous.</summary>
        public Action<bool> Skip { get; init; }

        /// <summary>Skip on one screen (by device name), for that screen's own keys.</summary>
        public Action<string, bool> SkipScreen { get; init; }
    }

    /// <summary>
    /// Owns EdgeLoop's global hotkeys. Panic and Wipe &amp; Exit are registered for the whole app
    /// lifetime. Skip (including each screen's own skip keys) and the triple-Escape exit are only
    /// registered while videos are playing: global hotkeys take their keys away from every other
    /// app, and the defaults are the plain arrow keys and Escape.
    /// </summary>
    public sealed class PlaybackHotkeyManager : IDisposable
    {
        public const string PanicName = "Panic";
        public const string ClearName = "Clear";
        public const string SkipForwardName = "SkipForward";
        public const string SkipBackwardName = "SkipBackward";
        public const string OpaquePanicName = "OpaquePanic";

        /// <summary>Prefix of a screen's own skip hotkeys: "ScreenSkip:{device}:F" and ":B".</summary>
        public const string ScreenSkipPrefix = "ScreenSkip:";

        private static readonly string[] PlaybackHotkeys = { SkipForwardName, SkipBackwardName, OpaquePanicName };

        private readonly IHotkeyRegistrar _hotkeys;
        private readonly Func<UserSettings> _settings;
        private readonly PlaybackHotkeyActions _actions;
        private readonly Func<IEnumerable<string>> _connectedScreens;
        private readonly HashSet<string> _screenSkipNames = new();

        /// <param name="connectedScreens">Device names of the screens plugged in now; screens' own skip keys are only
        /// registered for these. Null registers them for every screen in the settings.</param>
        public PlaybackHotkeyManager(IHotkeyRegistrar hotkeys, Func<UserSettings> settings, PlaybackHotkeyActions actions,
            Func<IEnumerable<string>> connectedScreens = null)
        {
            _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _connectedScreens = connectedScreens;
        }

        public bool IsPlaying { get; private set; }

        /// <summary>
        /// (Re)registers every hotkey that should be active right now, using the current settings.
        /// Returns the display names of hotkeys that could not be registered.
        /// </summary>
        public IReadOnlyList<string> RegisterAll()
        {
            var failures = new List<string>();
            var settings = _settings();
            if (settings == null)
                return failures;

            Register(PanicName, settings.PanicHotkeyModifiers, settings.PanicHotkeyKey ?? "End", _actions.Panic, failures);
            Register(ClearName, settings.ClearHotkeyModifiers, settings.ClearHotkeyKey ?? "Delete", _actions.WipeAndExit, failures);
            failures.AddRange(ApplyPlaybackHotkeys(settings));
            return failures;
        }

        /// <summary>
        /// Call when playback starts or stops. Registers or unregisters the playback-only hotkeys.
        /// Returns the display names of hotkeys that could not be registered.
        /// </summary>
        public IReadOnlyList<string> SetPlaying(bool isPlaying)
        {
            if (IsPlaying == isPlaying)
                return Array.Empty<string>();

            IsPlaying = isPlaying;
            var settings = _settings();
            return settings == null ? Array.Empty<string>() : ApplyPlaybackHotkeys(settings);
        }

        private List<string> ApplyPlaybackHotkeys(UserSettings settings)
        {
            var failures = new List<string>();

            if (!IsPlaying)
            {
                foreach (var name in PlaybackHotkeys)
                    _hotkeys.Unregister(name);
                UnregisterScreenSkips();
                return failures;
            }

            Register(SkipForwardName, settings.SkipForwardHotkeyModifiers, settings.SkipForwardHotkeyKey ?? "Right", () => _actions.Skip?.Invoke(true), failures);
            Register(SkipBackwardName, settings.SkipBackwardHotkeyModifiers, settings.SkipBackwardHotkeyKey ?? "Left", () => _actions.Skip?.Invoke(false), failures);

            // The triple-Escape exit only does anything in Opaque Mode, so don't hold Escape otherwise
            if (settings.AlwaysOpaque)
                Register(OpaquePanicName, settings.OpaquePanicHotkeyModifiers, settings.OpaquePanicHotkeyKey ?? "Escape", _actions.OpaquePanic, failures);
            else
                _hotkeys.Unregister(OpaquePanicName);

            RegisterScreenSkips(settings, failures);
            return failures;
        }

        private void RegisterScreenSkips(UserSettings settings, List<string> failures)
        {
            UnregisterScreenSkips();
            if (settings.PerScreenSkipHotkeys == null) return;

            var connected = _connectedScreens?.Invoke()?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (device, keys) in settings.PerScreenSkipHotkeys)
            {
                if (keys == null || (connected != null && !connected.Contains(device)))
                    continue; // A screen that isn't plugged in keeps its keys for later, unregistered

                RegisterScreenSkip(device, forward: true, keys.Forward, failures);
                RegisterScreenSkip(device, forward: false, keys.Backward, failures);
            }
        }

        private void RegisterScreenSkip(string device, bool forward, HotkeyCombo combo, List<string> failures)
        {
            if (combo.IsEmpty) return;
            string name = ScreenSkipName(device, forward);
            _screenSkipNames.Add(name);
            Register(name, combo.Modifiers, combo.Key, () => _actions.SkipScreen?.Invoke(device, forward), failures);
        }

        private void UnregisterScreenSkips()
        {
            foreach (var name in _screenSkipNames)
                _hotkeys.Unregister(name);
            _screenSkipNames.Clear();
        }

        public static string ScreenSkipName(string device, bool forward) => $"{ScreenSkipPrefix}{device}:{(forward ? "F" : "B")}";

        /// <summary>
        /// Releases every hotkey until the next <see cref="RegisterAll"/>, so a key recorder in Settings
        /// receives the keys instead of triggering them (Wipe &amp; Exit would close the app).
        /// </summary>
        public void Suspend()
        {
            _hotkeys.Unregister(PanicName);
            _hotkeys.Unregister(ClearName);
            foreach (var name in PlaybackHotkeys)
                _hotkeys.Unregister(name);
            UnregisterScreenSkips();
        }

        private void Register(string name, uint modifiers, string key, Action callback, List<string> failures)
        {
            if (!_hotkeys.Register(name, modifiers, key, callback ?? (() => { })))
                failures.Add(DisplayName(name));
        }

        public static string DisplayName(string name) => name switch
        {
            PanicName => "Panic",
            ClearName => "Wipe & Exit",
            SkipForwardName => "Skip Forward",
            SkipBackwardName => "Skip Backward",
            OpaquePanicName => "Fullscreen Escape",
            _ when name.StartsWith(ScreenSkipPrefix, StringComparison.Ordinal) => ScreenSkipDisplayName(name),
            _ => name
        };

        private static string ScreenSkipDisplayName(string name)
        {
            var rest = name.Substring(ScreenSkipPrefix.Length);
            bool forward = rest.EndsWith(":F", StringComparison.Ordinal);
            var device = rest.Length > 2 ? rest.Substring(0, rest.Length - 2) : rest;
            var number = System.Text.RegularExpressions.Regex.Match(device, @"DISPLAY(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var screen = number.Success ? $"Screen {number.Groups[1].Value}" : device;
            return $"{(forward ? "Skip Forward" : "Skip Backward")} on {screen}";
        }

        public void Dispose() => Suspend();
    }

    /// <summary>Decides which player(s) a skip hotkey acts on.</summary>
    public static class SkipTargetResolver
    {
        /// <summary>
        /// One logical player (or sync group) playing: skip it. Several: skip the one on the screen
        /// under the mouse, else the one on the primary screen, else all of them. Followers in a
        /// sync group resolve to their group's master, which broadcasts the skip.
        /// </summary>
        public static IReadOnlyList<T> Resolve<T>(IReadOnlyList<T> players, string cursorScreen, string primaryScreen)
            where T : class, ISkipTarget
        {
            if (players == null || players.Count == 0)
                return Array.Empty<T>();

            var independent = players.Where(p => string.IsNullOrEmpty(p.SyncGroupId) || p.IsSyncMaster).ToList();
            if (independent.Count <= 1)
                return independent;

            var target = OwnerOn(players, cursorScreen) ?? OwnerOn(players, primaryScreen);
            return target != null ? new[] { target } : independent;
        }

        /// <summary>The player to skip for one screen's own keys: the one on that screen (or its group's master), or none.</summary>
        public static IReadOnlyList<T> ResolveScreen<T>(IReadOnlyList<T> players, string screen) where T : class, ISkipTarget
        {
            var owner = players == null ? null : OwnerOn(players, screen);
            return owner != null ? new[] { owner } : Array.Empty<T>();
        }

        private static T OwnerOn<T>(IReadOnlyList<T> players, string screen) where T : class, ISkipTarget
        {
            if (string.IsNullOrEmpty(screen))
                return null;

            var onScreen = players.FirstOrDefault(p => string.Equals(p.MonitorName, screen, StringComparison.OrdinalIgnoreCase));
            if (onScreen == null)
                return null;

            if (string.IsNullOrEmpty(onScreen.SyncGroupId))
                return onScreen;

            return players.FirstOrDefault(p => p.SyncGroupId == onScreen.SyncGroupId && p.IsSyncMaster);
        }
    }
}
