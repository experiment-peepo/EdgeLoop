using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GOON.ViewModels;
using GOON.Windows;
using System.Collections.ObjectModel;

namespace GOON.Classes {
    /// <summary>
    /// Interface for video playback management across screens
    /// </summary>
    public interface IVideoPlayerService {
        // Events
        event EventHandler MediaOpened;
        event EventHandler<MediaErrorEventArgs> MediaErrorOccurred;

        // Core playback
        Task PlayPerMonitorAsync(IDictionary<ScreenViewer, IEnumerable<VideoItem>> assignments, bool showGroupControl = true, PlaybackState resumeState = null, CancellationToken cancellationToken = default);
        Task PlayOnScreensAsync(IEnumerable<VideoItem> files, IEnumerable<ScreenViewer> screens);
        
        // Controls
        void PauseAll();
        void ContinueAll();
        void StopAll();
        
        // Volume/Opacity
        void SetVolumeAll(double volume);
        void SetOpacityAll(double opacity);
        
        // Player management
        void UnregisterPlayer(HypnoWindow player);
        void RemoveItemsFromAllPlayers(IEnumerable<VideoItem> items);
        
        // Highlights
        void UpdateHighlights();
        
        // State
        bool IsPlaying { get; }
        bool HasActivePlayers { get; }
        IReadOnlyList<HypnoViewModel> ActiveViewModels { get; }
        ObservableCollection<ActivePlayerViewModel> ActivePlayers { get; }
    }
}
