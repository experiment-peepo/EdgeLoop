using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace EdgeLoop.Classes
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class HotkeyService : IHotkeyRegistrar, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Modifiers: Alt=1, Ctrl=2, Shift=4, Win=8
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        private IntPtr _windowHandle;
        private HwndSource _source;

        private class HotkeyRegistration
        {
            public int Id { get; set; }
            public uint Modifiers { get; set; }
            public uint VirtualKey { get; set; }
            public Action Callback { get; set; }
        }

        private readonly Dictionary<string, HotkeyRegistration> _registrations = new Dictionary<string, HotkeyRegistration>();
        private readonly Dictionary<int, string> _idToNameMap = new Dictionary<int, string>();
        private int _nextId = 9000;

        public bool IsInitialized => _windowHandle != IntPtr.Zero;

        public void Initialize(IntPtr windowHandle)
        {
            if (_windowHandle != IntPtr.Zero)
            {
                // Already initialized, detach first if different handle (or just ignore)
                Dispose();
            }

            _windowHandle = windowHandle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source?.AddHook(HwndHook);
        }

        public bool IsRegistered(string name) => _registrations.ContainsKey(name);

        public bool Register(string name, uint modifiers, string keyName, Action callback)
        {
            if (_windowHandle == IntPtr.Zero) return false;

            // Handle specific key names that might process oddly or custom requirements
            if (!Enum.TryParse<Key>(keyName, true, out Key key))
            {
                Logger.Warning($"Failed to parse key: {keyName}");
                return false;
            }
            uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

            _registrations.TryGetValue(name, out var previous);
            if (previous != null && previous.Modifiers == modifiers && previous.VirtualKey == virtualKey)
            {
                previous.Callback = callback; // Same combination already registered: just refresh the action
                return true;
            }

            Unregister(name);

            if (TryRegister(name, modifiers, virtualKey, callback))
            {
                Logger.Debug($"Registered hotkey '{name}': {modifiers}+{keyName}");
                return true;
            }

            Logger.Warning($"Failed to register hotkey '{name}': {modifiers}+{keyName} (ErrorCode: {Marshal.GetLastWin32Error()})");

            // Keep the action reachable: put the previous combination back
            if (previous != null && TryRegister(name, previous.Modifiers, previous.VirtualKey, previous.Callback))
                Logger.Warning($"Kept the previous key combination for hotkey '{name}'.");

            return false;
        }

        private bool TryRegister(string name, uint modifiers, uint virtualKey, Action callback)
        {
            int id = _nextId++;
            if (!RegisterHotKey(_windowHandle, id, modifiers, virtualKey))
                return false;

            _registrations[name] = new HotkeyRegistration { Id = id, Modifiers = modifiers, VirtualKey = virtualKey, Callback = callback };
            _idToNameMap[id] = name;
            return true;
        }

        public void Unregister(string name)
        {
            if (_registrations.TryGetValue(name, out var reg))
            {
                if (_windowHandle != IntPtr.Zero)
                {
                    UnregisterHotKey(_windowHandle, reg.Id);
                }
                _idToNameMap.Remove(reg.Id);
                _registrations.Remove(name);
            }
        }

        public void UnregisterAll()
        {
            foreach (var name in new List<string>(_registrations.Keys))
            {
                Unregister(name);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_idToNameMap.TryGetValue(id, out string name))
                {
                    if (_registrations.TryGetValue(name, out var reg))
                    {
                        // Logger.Info($"Hotkey pressed: {name}"); // Optional: log if needed, excessive logging might be bad for hotkeys
                        reg.Callback?.Invoke();
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();

            if (_source != null)
            {
                _source.RemoveHook(HwndHook);
                _source = null; // Dispose not explicitly needed for HwndSource created FromHwnd usually
            }
            _windowHandle = IntPtr.Zero;
        }
    }
}

