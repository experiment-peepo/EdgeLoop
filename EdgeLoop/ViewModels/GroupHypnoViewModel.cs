using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EdgeLoop.Classes;

namespace EdgeLoop.ViewModels {
    /// <summary>
    /// A composite view model that controls multiple HypnoViewModel instances.
    /// Used for unified control of "All Monitors" playback.
    /// </summary>
    public class GroupHypnoViewModel : HypnoViewModel {
        private readonly List<HypnoViewModel> _children;
        public IReadOnlyList<HypnoViewModel> Children => _children.AsReadOnly();

        public GroupHypnoViewModel(IEnumerable<HypnoViewModel> children) {
            _children = children.ToList();
            this.MonitorName = "All Monitors";
            
            // Sync properties from first child and monitor for changes
            if (_children.Any()) {
                var first = _children.First();
                this.MediaState = first.MediaState;
                this.Volume = first.Volume;
                this.Opacity = first.Opacity;
                this.SpeedRatio = first.SpeedRatio;
                this.CurrentItem = first.CurrentItem;

                foreach (var child in _children) {
                    child.PropertyChanged += (s, e) => {
                        if (e.PropertyName == nameof(MediaState)) {
                            SyncStateWithChildren();
                        } else if (e.PropertyName == nameof(CurrentItem)) {
                            // Sync CurrentItem from the master or first child
                            Application.Current?.Dispatcher.InvokeAsync(() => {
                                var master = _children.FirstOrDefault(c => c.IsSyncMaster) ?? _children.FirstOrDefault();
                                if (master != null) this.CurrentItem = master.CurrentItem;
                            });
                        }
                    };
                }
            }
        }

        private void SyncStateWithChildren() {
            if (!_children.Any()) return;

            Application.Current?.Dispatcher.InvokeAsync(() => {
                // Re-check inside dispatcher since collection could have changed
                if (!_children.Any()) return;
                
                // If any child is playing, the group status should be 'Play'
                // This ensures the Pause icon shows up if any monitor is active
                if (_children.Any(c => c.MediaState == System.Windows.Controls.MediaState.Play)) {
                    this.MediaState = System.Windows.Controls.MediaState.Play;
                } else {
                    // Otherwise, reflect the first child's state (Master)
                    var firstChild = _children.FirstOrDefault();
                    if (firstChild != null) {
                        this.MediaState = firstChild.MediaState;
                    }
                }
            });
        }

        public override void Play() {
            if (ServiceContainer.TryGet<VideoPlayerService>(out var vps)) {
                vps.ResumeSyncClock();
            }
            foreach (var child in _children) child.Play();
            this.MediaState = System.Windows.Controls.MediaState.Play;
            base.Play();
        }

        public override void Pause() {
            if (ServiceContainer.TryGet<VideoPlayerService>(out var vps)) {
                vps.PauseSyncClock();
            }
            foreach (var child in _children) child.Pause();
            this.MediaState = System.Windows.Controls.MediaState.Pause;
            base.Pause();
        }

        public override void TogglePlayPause() {
            if (!_children.Any()) return;
            
            var isAnyPlaying = _children.Any(c => c.MediaState == System.Windows.Controls.MediaState.Play);
            if (isAnyPlaying) {
                Pause();
            } else {
                Play();
            }
        }

        public override void PlayNext(bool force = false) {
            // Optimistically set state to Play so icon reflects intentionality
            this.MediaState = System.Windows.Controls.MediaState.Play;
            
            // In group mode, we ONLY trigger the master child.
            // All followers will sync automatically via the broadcast mechanism.
            // Triggering skip on followers redundantly can interrupt the broadcast-initiated load.
            var master = _children.FirstOrDefault(c => c.IsSyncMaster) ?? _children.FirstOrDefault();
            if (master != null) {
                master.PlayNext(force);
            }
        }

        public override void ForcePlay() {
            if (ServiceContainer.TryGet<VideoPlayerService>(out var vps)) {
                vps.ResumeSyncClock();
            }
            foreach (var child in _children) child.ForcePlay();
        }

        public override double Volume {
            get => base.Volume;
            set {
                base.Volume = value;
                foreach (var child in _children) {
                    // Only update volume for the master or unsynced children
                    // Followers stay at 0 to prevent audio overlap
                    if (string.IsNullOrEmpty(child.SyncGroupId) || child.IsSyncMaster) {
                        child.Volume = value;
                    } else {
                        child.Volume = 0;
                    }
                }
            }
        }

        public override double Opacity {
            get => base.Opacity;
            set {
                base.Opacity = value;
                foreach (var child in _children) child.Opacity = value;
            }
        }

        public override double SpeedRatio {
            get => base.SpeedRatio;
            set {
                base.SpeedRatio = value;
                foreach (var child in _children) child.SpeedRatio = value;

                // Sync speed with SharedClock to maintain synchronization
                var masterClock = _children.FirstOrDefault(c => c.ExternalClock != null)?.ExternalClock as Classes.SharedClock;
                if (masterClock != null) {
                    masterClock.Speed = value;
                }
            }
        }

        public override void SyncPosition(TimeSpan position) {
            // Update the shared clock if any child uses it
            var masterClock = _children.FirstOrDefault(c => c.ExternalClock != null)?.ExternalClock as Classes.SharedClock;
            if (masterClock != null) {
                masterClock.Seek(position.Ticks);
            }

            foreach (var child in _children) {
                child.SyncPosition(position);
            }
            base.SyncPosition(position);
        }

        public override void RemoveItems(IEnumerable<VideoItem> itemsToRemove) {
            foreach (var child in _children) {
                child.RemoveItems(itemsToRemove);
            }
            base.RemoveItems(itemsToRemove); // Updates Group's own shadow queue
        }
    }
}

