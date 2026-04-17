using System;
using System.Windows.Input;
using EdgeLoop.Classes;

namespace EdgeLoop.ViewModels {
    public class ActivePlayerViewModel : ObservableObject {
        private string _screenName;
        private HypnoViewModel _playerVm;

        public string ScreenName {
            get => _screenName;
            set => SetProperty(ref _screenName, value);
        }

        public bool IsPlaying => _playerVm?.MediaState == System.Windows.Controls.MediaState.Play;
        public string CurrentVideoTitle => _playerVm?.CurrentItem?.FileName ?? "Loading...";

        public HypnoViewModel Player => _playerVm;

        public ICommand SkipCommand => _playerVm?.SkipCommand;
        public ICommand TogglePlayPauseCommand => _playerVm?.TogglePlayPauseCommand;

        public ActivePlayerViewModel(string screenName, HypnoViewModel playerVm) {
            _screenName = screenName;
            _playerVm = playerVm;
            if (_playerVm != null) {
                _playerVm.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(HypnoViewModel.MediaState)) {
                        OnPropertyChanged(nameof(IsPlaying));
                    } else if (e.PropertyName == nameof(HypnoViewModel.CurrentItem)) {
                        OnPropertyChanged(nameof(CurrentVideoTitle));
                    }
                };
            }
        }
    }
}

