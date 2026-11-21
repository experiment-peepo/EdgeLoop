using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EdgeLoop.Classes;

namespace EdgeLoop.Controls
{
    /// <summary>
    /// Shows a hotkey ("Ctrl+Shift+End"). Click it (or press Enter/Space on it), then press the new
    /// combination: any key, with or without Ctrl/Alt/Shift/Win. Tab or clicking elsewhere cancels.
    /// Styled by HotkeyRecorder in Themes/Settings.xaml.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class HotkeyRecorder : Control
    {
        public static readonly DependencyProperty ModifiersProperty = DependencyProperty.Register(
            nameof(Modifiers), typeof(uint), typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(0u, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyRecorder)d).UpdateText()));

        /// <summary>Modifier flags as RegisterHotKey takes them (Alt=1, Ctrl=2, Shift=4, Win=8).</summary>
        public uint Modifiers
        {
            get => (uint)GetValue(ModifiersProperty);
            set => SetValue(ModifiersProperty, value);
        }

        public static readonly DependencyProperty KeyNameProperty = DependencyProperty.Register(
            nameof(KeyName), typeof(string), typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyRecorder)d).UpdateText()));

        /// <summary>The key, as a <see cref="Key"/> name.</summary>
        public string KeyName
        {
            get => (string)GetValue(KeyNameProperty);
            set => SetValue(KeyNameProperty, value);
        }

        private static readonly DependencyPropertyKey IsRecordingPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsRecording), typeof(bool), typeof(HotkeyRecorder), new PropertyMetadata(false));
        public static readonly DependencyProperty IsRecordingProperty = IsRecordingPropertyKey.DependencyProperty;

        public bool IsRecording => (bool)GetValue(IsRecordingProperty);

        private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(DisplayText), typeof(string), typeof(HotkeyRecorder), new PropertyMetadata("Not set"));
        public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

        /// <summary>What the box shows: the combination, or what's held so far while recording.</summary>
        public string DisplayText => (string)GetValue(DisplayTextProperty);

        public HotkeyRecorder()
        {
            Focusable = true;
            SetResourceReference(StyleProperty, "HotkeyRecorderStyle");
            UpdateText();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            if (!IsRecording)
                StartRecording();
            e.Handled = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (!IsRecording)
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    StartRecording();
                    e.Handled = true;
                }
                return;
            }

            // With Alt held, WPF reports the key as Key.System and the real key in SystemKey
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                _ => e.Key
            };

            if (key == Key.Tab)
            {
                StopRecording(); // Let Tab move focus as usual
                return;
            }

            e.Handled = true;
            uint modifiers = HotkeyCombo.FromModifierKeys(Keyboard.Modifiers);
            if (HotkeyCombo.IsModifierKey(key) || key == Key.None)
            {
                // Show what's held so far, e.g. "Ctrl+Shift+…"
                SetValue(DisplayTextPropertyKey, string.Join("+", HotkeyCombo.ModifierNames(modifiers).Append("…")));
                return;
            }

            StopRecording();
            Modifiers = modifiers;
            KeyName = key.ToString();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            StopRecording();
        }

        private void StartRecording()
        {
            SetValue(IsRecordingPropertyKey, true);
            SetValue(DisplayTextPropertyKey, "Press keys…");
            HotkeyCapture.SetRecording(true);
        }

        private void StopRecording()
        {
            if (!IsRecording) return;
            SetValue(IsRecordingPropertyKey, false);
            HotkeyCapture.SetRecording(false);
            UpdateText();
        }

        private void UpdateText()
        {
            if (!IsRecording)
                SetValue(DisplayTextPropertyKey, new HotkeyCombo(Modifiers, KeyName).ToString());
        }
    }
}
