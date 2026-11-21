using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Enter and Esc for EdgeLoop's dialogs. Handled at window level (rather than with
    /// Button.IsDefault/IsCancel) because the dialogs close themselves in their button handlers,
    /// and IsCancel would then set DialogResult a second time on an already closed window.
    /// </summary>
    public static class DialogKeys
    {
        /// <param name="window">The dialog.</param>
        /// <param name="defaultButton">Invoked by Enter, or null for no Enter action (e.g. destructive confirmations).</param>
        /// <param name="cancelButton">Invoked by Esc, or null to have Esc close the window.</param>
        /// <param name="enterFromTextBoxes">Whether Enter inside a text box also invokes the default button.</param>
        public static void Attach(Window window, ButtonBase defaultButton, ButtonBase cancelButton, bool enterFromTextBoxes = true)
        {
            window.PreviewKeyDown += (s, e) =>
            {
                // An open drop-down uses Enter and Esc itself
                if (IsInOpenDropDown(Keyboard.FocusedElement as DependencyObject))
                    return;

                if (e.Key == Key.Escape)
                {
                    if (cancelButton != null)
                    {
                        e.Handled = Invoke(cancelButton);
                    }
                    else
                    {
                        window.Close();
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Enter && defaultButton != null)
                {
                    // A focused button (reached with Tab) presses itself
                    if (Keyboard.FocusedElement is ButtonBase)
                        return;
                    if (Keyboard.FocusedElement is TextBox textBox && (textBox.AcceptsReturn || !enterFromTextBoxes))
                        return;
                    e.Handled = Invoke(defaultButton);
                }
            };
        }

        private static bool IsInOpenDropDown(DependencyObject focused) => focused switch
        {
            ComboBox comboBox => comboBox.IsDropDownOpen,
            ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true },
            _ => false
        };

        private static bool Invoke(ButtonBase button)
        {
            if (!button.IsEnabled)
                return false;

            if (button.Command != null)
            {
                if (!button.Command.CanExecute(button.CommandParameter))
                    return false;
                button.Command.Execute(button.CommandParameter);
                return true;
            }

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            return true;
        }
    }
}
