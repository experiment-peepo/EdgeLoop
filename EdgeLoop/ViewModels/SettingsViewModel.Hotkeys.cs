using System;
using System.Collections.ObjectModel;
using EdgeLoop.Classes;

namespace EdgeLoop.ViewModels
{
    /// <summary>One configurable hotkey in Settings.</summary>
    public class HotkeySetting : ObservableObject
    {
        public HotkeySetting(string id, string title, string description, HotkeyCombo combo, HotkeyCombo defaultCombo)
        {
            Id = id;
            Title = title;
            Description = description;
            _modifiers = combo.Modifiers;
            _key = combo.Key;
            Default = defaultCombo;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public HotkeyCombo Default { get; }

        private uint _modifiers;
        public uint Modifiers
        {
            get => _modifiers;
            set
            {
                if (SetProperty(ref _modifiers, value))
                    OnPropertyChanged(nameof(Combo));
            }
        }

        private string _key;
        public string Key
        {
            get => _key;
            set
            {
                if (SetProperty(ref _key, value))
                    OnPropertyChanged(nameof(Combo));
            }
        }

        public HotkeyCombo Combo
        {
            get => new(Modifiers, Key);
            set
            {
                Modifiers = value.Modifiers;
                Key = value.Key;
            }
        }

        private string _problem;
        /// <summary>Why these keys won't work as expected, or null.</summary>
        public string Problem
        {
            get => _problem;
            set
            {
                if (SetProperty(ref _problem, value))
                    OnPropertyChanged(nameof(HasProblem));
            }
        }

        public bool HasProblem => Problem != null;

        private bool _isConflict;
        /// <summary>The problem is a clash with another EdgeLoop hotkey, which blocks saving.</summary>
        public bool IsConflict
        {
            get => _isConflict;
            set => SetProperty(ref _isConflict, value);
        }

        public void ResetToDefault() => Combo = Default;
    }

    /// <summary>A screen's row under "Skip keys for each screen".</summary>
    public class ScreenSkipSetting : ObservableObject
    {
        public ScreenSkipSetting(ScreenViewer screen, ScreenSkipKeys saved)
        {
            DeviceName = screen.DeviceName;
            DisplayName = screen.DisplayName;
            Details = screen.Details;
            _useOwnKeys = saved != null;
            Forward = new HotkeySetting($"ScreenSkip:{DeviceName}:F", $"{DisplayName} forward", null,
                saved?.Forward ?? default, default);
            Backward = new HotkeySetting($"ScreenSkip:{DeviceName}:B", $"{DisplayName} back", null,
                saved?.Backward ?? default, default);
        }

        public string DeviceName { get; }
        public string DisplayName { get; }
        public string Details { get; }
        public HotkeySetting Forward { get; }
        public HotkeySetting Backward { get; }

        private bool _useOwnKeys;
        /// <summary>False: this screen follows the mouse with the main skip keys.</summary>
        public bool UseOwnKeys
        {
            get => _useOwnKeys;
            set => SetProperty(ref _useOwnKeys, value);
        }

        /// <summary>What gets saved: the keys, or null to follow the mouse.</summary>
        public ScreenSkipKeys ToSettings() =>
            UseOwnKeys && (!Forward.Combo.IsEmpty || !Backward.Combo.IsEmpty)
                ? new ScreenSkipKeys
                {
                    ForwardModifiers = Forward.Modifiers,
                    ForwardKey = Forward.Combo.IsEmpty ? null : Forward.Key,
                    BackwardModifiers = Backward.Modifiers,
                    BackwardKey = Backward.Combo.IsEmpty ? null : Backward.Key
                }
                : null;
    }

    /// <summary>A Settings sidebar entry.</summary>
    public sealed class SettingsSectionItem
    {
        public SettingsSection Section { get; init; }
        public string Title { get; init; }
        public string Description { get; init; }
    }

    public enum SettingsSection
    {
        Playback,
        Library,
        Privacy,
        Hotkeys,
        Advanced,
        Censoring
    }
}
