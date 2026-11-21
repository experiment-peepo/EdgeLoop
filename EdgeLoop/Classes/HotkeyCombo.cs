using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// A global hotkey: modifier flags as RegisterHotKey takes them (Alt=1, Ctrl=2, Shift=4, Win=8)
    /// plus a WPF <see cref="System.Windows.Input.Key"/> name such as "End" or "Right".
    /// </summary>
    public readonly record struct HotkeyCombo(uint Modifiers, string Key)
    {
        public const uint Alt = 0x0001;
        public const uint Ctrl = 0x0002;
        public const uint Shift = 0x0004;
        public const uint Win = 0x0008;

        public bool IsEmpty => ParsedKey == null;

        private Key? ParsedKey => !string.IsNullOrWhiteSpace(Key) && Enum.TryParse<Key>(Key, true, out var key) && key != System.Windows.Input.Key.None
            ? key
            : null;

        /// <summary>Same keys, even when spelled differently ("Return" and "Enter" are the same key).</summary>
        public bool SameKeysAs(HotkeyCombo other) =>
            !IsEmpty && !other.IsEmpty && Modifiers == other.Modifiers && ParsedKey == other.ParsedKey;

        /// <summary>Modifier flags from WPF's <see cref="ModifierKeys"/>.</summary>
        public static uint FromModifierKeys(ModifierKeys modifiers) =>
            (modifiers.HasFlag(ModifierKeys.Alt) ? Alt : 0) |
            (modifiers.HasFlag(ModifierKeys.Control) ? Ctrl : 0) |
            (modifiers.HasFlag(ModifierKeys.Shift) ? Shift : 0) |
            (modifiers.HasFlag(ModifierKeys.Windows) ? Win : 0);

        /// <summary>"Ctrl+Shift+End", "Right arrow", or "Not set".</summary>
        public override string ToString()
        {
            if (IsEmpty) return "Not set";
            var parts = ModifierNames(Modifiers).ToList();
            parts.Add(FriendlyKey(ParsedKey.Value));
            return string.Join("+", parts);
        }

        internal static IEnumerable<string> ModifierNames(uint modifiers)
        {
            if ((modifiers & Ctrl) != 0) yield return "Ctrl";
            if ((modifiers & Alt) != 0) yield return "Alt";
            if ((modifiers & Shift) != 0) yield return "Shift";
            if ((modifiers & Win) != 0) yield return "Win";
        }

        internal static string FriendlyKey(Key key) => key switch
        {
            System.Windows.Input.Key.Left => "Left arrow",
            System.Windows.Input.Key.Right => "Right arrow",
            System.Windows.Input.Key.Up => "Up arrow",
            System.Windows.Input.Key.Down => "Down arrow",
            System.Windows.Input.Key.Escape => "Esc",
            System.Windows.Input.Key.Return => "Enter",
            System.Windows.Input.Key.Back => "Backspace",
            System.Windows.Input.Key.Prior => "Page Up",
            System.Windows.Input.Key.Next => "Page Down",
            System.Windows.Input.Key.Snapshot => "Print Screen",
            System.Windows.Input.Key.Capital => "Caps Lock",
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num " + (int)(key - System.Windows.Input.Key.NumPad0),
            System.Windows.Input.Key.Multiply => "Num *",
            System.Windows.Input.Key.Add => "Num +",
            System.Windows.Input.Key.Subtract => "Num -",
            System.Windows.Input.Key.Divide => "Num /",
            System.Windows.Input.Key.Decimal => "Num .",
            System.Windows.Input.Key.OemPlus => "=",
            System.Windows.Input.Key.OemMinus => "-",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod => ".",
            _ => key.ToString()
        };

        /// <summary>True for keys that only modify others (Ctrl, Alt, Shift, Win).</summary>
        public static bool IsModifierKey(Key key) => key is
            System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
            System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
            System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
            System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin;
    }

    /// <summary>A named hotkey for conflict checking.</summary>
    public readonly record struct NamedHotkey(string Id, string Title, HotkeyCombo Combo);

    public static class HotkeyConflicts
    {
        /// <summary>
        /// For every hotkey that shares its keys with others, the titles of those others.
        /// Hotkeys that aren't set are ignored.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Find(IEnumerable<NamedHotkey> hotkeys)
        {
            var list = hotkeys.Where(h => !h.Combo.IsEmpty).ToList();
            var result = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var hotkey in list)
            {
                var others = list.Where(o => o.Id != hotkey.Id && o.Combo.SameKeysAs(hotkey.Combo)).Select(o => o.Title).ToList();
                if (others.Count > 0)
                    result[hotkey.Id] = others;
            }
            return result;
        }
    }
}
