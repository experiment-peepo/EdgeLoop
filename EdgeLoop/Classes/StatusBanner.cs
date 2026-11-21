using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// The launcher's status message. Info and success messages clear themselves after a few
    /// seconds (but not while <see cref="IsBusy"/> reports work in progress); warnings and errors
    /// stay until they are dismissed or replaced. A message can carry one action, such as Undo.
    /// </summary>
    public sealed class StatusBanner : ObservableObject
    {
        public static readonly TimeSpan AutoClearDelay = TimeSpan.FromSeconds(6);

        /// <summary>Messages with an action (Undo) stay a little longer, so there's time to use it.</summary>
        public static readonly TimeSpan ActionClearDelay = TimeSpan.FromSeconds(10);

        private string _message;
        private string _actionText;
        private Action _action;
        private StatusMessageType _type = StatusMessageType.Info;
        private int _version;

        /// <summary>Clock for clearing messages. Tests swap in a fake clock.</summary>
        public TimeProvider Time { get; set; } = TimeProvider.System;

        /// <summary>While this returns true, progress messages are kept up instead of clearing.</summary>
        public Func<bool> IsBusy { get; set; }

        public string Message
        {
            get => _message;
            private set => SetProperty(ref _message, value);
        }

        public StatusMessageType Type
        {
            get => _type;
            private set => SetProperty(ref _type, value);
        }

        /// <summary>Label of the message's action button (e.g. "Undo"), or null for no button.</summary>
        public string ActionText
        {
            get => _actionText;
            private set => SetProperty(ref _actionText, value);
        }

        public void Show(string message, StatusMessageType type = StatusMessageType.Info) => Show(message, type, null, null);

        /// <summary>Shows a message with a button; <paramref name="action"/> runs at most once, when it's pressed.</summary>
        public void Show(string message, StatusMessageType type, string actionText, Action action)
        {
            bool hasAction = action != null && !string.IsNullOrEmpty(actionText) && !string.IsNullOrEmpty(message);
            _action = hasAction ? action : null;
            Message = message;
            Type = type;
            ActionText = hasAction ? actionText : null;

            int version = Interlocked.Increment(ref _version);
            if (!string.IsNullOrEmpty(message) && (type == StatusMessageType.Info || type == StatusMessageType.Success))
                _ = ClearLaterAsync(version, hasAction ? ActionClearDelay : AutoClearDelay);
        }

        /// <summary>Runs the current message's action (if any) and dismisses the message.</summary>
        public void RunAction()
        {
            var action = _action;
            Dismiss();
            action?.Invoke();
        }

        public void Dismiss() => Show(null);

        private async Task ClearLaterAsync(int version, TimeSpan delay)
        {
            try
            {
                do
                {
                    await Task.Delay(delay, Time);
                    if (version != Volatile.Read(ref _version))
                        return; // Replaced by a newer message
                } while (IsBusy?.Invoke() == true);

                void Clear()
                {
                    if (version == Volatile.Read(ref _version))
                        Dismiss();
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.Invoke(Clear);
                else
                    Clear();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[StatusBanner] Auto-clear skipped: {ex.Message}");
            }
        }
    }
}
