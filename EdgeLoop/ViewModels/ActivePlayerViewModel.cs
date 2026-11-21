using System;
using System.Windows.Input;
using EdgeLoop.Classes;

namespace EdgeLoop.ViewModels
{
    public class ActivePlayerViewModel : ObservableObject
    {
        private static void OnUiThread(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.InvokeAsync(action);
        }

        private string _screenName;
        private HypnoViewModel _playerVm;

        public string ScreenName
        {
            get => _screenName;
            set => SetProperty(ref _screenName, value);
        }

        public bool IsPlaying => _playerVm?.MediaState == System.Windows.Controls.MediaState.Play;
        public string CurrentVideoTitle => _playerVm?.CurrentItem?.FileName ?? "Loading...";

        public HypnoViewModel Player => _playerVm;

        public ICommand SkipCommand => _playerVm?.SkipCommand;
        public ICommand PreviousCommand => _playerVm?.PreviousCommand;
        public ICommand TogglePlayPauseCommand => _playerVm?.TogglePlayPauseCommand;

        private string _problem;
        /// <summary>The last video that failed on this screen and why, until the next one opens.</summary>
        public string Problem
        {
            get => _problem;
            private set => SetProperty(ref _problem, value);
        }

        public ActivePlayerViewModel(string screenName, HypnoViewModel playerVm)
        {
            _screenName = screenName;
            _playerVm = playerVm;
            if (_playerVm != null)
            {
                _playerVm.MediaFailed += (s, e) =>
                {
                    string reason = PlaybackFailureText.Reason(e.ErrorMessage);
                    string problem = $"{PlaybackFailureText.Headline(e.Item?.FileName)}. {reason ?? ""}".Trim();
                    OnUiThread(() => Problem = problem);
                };
                _playerVm.MediaOpened += (s, e) => OnUiThread(() => Problem = null);

                _playerVm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(HypnoViewModel.MediaState))
                    {
                        OnPropertyChanged(nameof(IsPlaying));
                    }
                    else if (e.PropertyName == nameof(HypnoViewModel.CurrentItem))
                    {
                        OnPropertyChanged(nameof(CurrentVideoTitle));
                    }
                };
            }
        }
    }
}

