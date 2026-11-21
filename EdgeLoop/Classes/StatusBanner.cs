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
    /// stay until they are dismissed or replaced.
    /// </summary>
    public sealed class StatusBanner : ObservableObject
    {
        public static readonly TimeSpan AutoClearDelay = TimeSpan.FromSeconds(6);

        private string _message;
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

        public void Show(string message, StatusMessageType type = StatusMessageType.Info)
        {
            Message = message;
            Type = type;

            int version = Interlocked.Increment(ref _version);
            if (!string.IsNullOrEmpty(message) && (type == StatusMessageType.Info || type == StatusMessageType.Success))
                _ = ClearLaterAsync(version);
        }

        public void Dismiss() => Show(null);

        private async Task ClearLaterAsync(int version)
        {
            try
            {
                do
                {
                    await Task.Delay(AutoClearDelay, Time);
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
