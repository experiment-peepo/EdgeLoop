using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using EdgeLoop.Windows;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// EdgeLoop's message boxes. Every dialog gets an owner (the window that asked, else the active
    /// window, else the launcher), so it centres on that window and can't fall behind it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DialogService
    {
        /// <summary>Asks a yes/no question. A destructive question gets a red confirm button that Enter doesn't press.</summary>
        public static bool Confirm(string title, string message, string confirmText = "Yes", string cancelText = "No",
            bool destructive = false, Window owner = null)
        {
            var dialog = new ConfirmDialog(title, message, confirmText, cancelText, destructive);
            return ShowModal(dialog, owner) == true;
        }

        /// <summary>
        /// Offers two options; the second is chosen automatically after
        /// <see cref="ChoiceDialogWindow.AutoSelectSeconds"/> unless the user stops the countdown.
        /// </summary>
        /// <returns>1 or 2 for the option chosen, 0 if cancelled.</returns>
        public static int Choose(string title, string message, string option1, string option2, Window owner = null)
        {
            var dialog = new ChoiceDialogWindow
            {
                DialogTitle = title,
                Message = message,
                Option1Text = option1,
                Option2Text = option2
            };
            return ShowModal(dialog, owner) == true ? dialog.SelectedOption : 0;
        }

        /// <summary>Asks for one line of text.</summary>
        /// <param name="hint">Example shown inside the empty box.</param>
        /// <param name="note">Small print under the box.</param>
        /// <returns>The trimmed text, or null if cancelled.</returns>
        public static string Prompt(string title, string label, string hint = null, string note = null,
            string initialValue = "", Window owner = null)
        {
            var dialog = new InputDialogWindow
            {
                DialogTitle = title,
                LabelText = label,
                Hint = hint,
                Note = note,
                InputText = initialValue ?? ""
            };
            return ShowModal(dialog, owner) == true ? dialog.InputText?.Trim() ?? "" : null;
        }

        private static bool? ShowModal(Window dialog, Window preferredOwner)
        {
            var app = Application.Current;
            var owner = PickOwner(preferredOwner, app?.Windows.OfType<Window>(), app?.MainWindow,
                isVisible: w => w.IsVisible,
                isActive: w => w.IsActive,
                canOwn: w => w != dialog && w is not HypnoWindow);

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return dialog.ShowDialog();
        }

        /// <summary>
        /// The owner for a new dialog: the preferred window if it can own one, else the active window,
        /// else the main window, else none. Playback overlays never own dialogs.
        /// </summary>
        internal static T PickOwner<T>(T preferred, IEnumerable<T> openWindows, T mainWindow,
            Func<T, bool> isVisible, Func<T, bool> isActive, Func<T, bool> canOwn) where T : class
        {
            bool Usable(T window) => window != null && canOwn(window) && isVisible(window);

            if (Usable(preferred))
                return preferred;

            var active = openWindows?.FirstOrDefault(w => Usable(w) && isActive(w));
            if (active != null)
                return active;

            return Usable(mainWindow) ? mainWindow : null;
        }
    }
}
