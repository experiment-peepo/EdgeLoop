using System;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Tells the app when a hotkey recorder (in Settings) is listening for keys. Global hotkeys get
    /// their keys before any window does, so they're released while recording and registered again
    /// afterwards; otherwise recording Wipe &amp; Exit's keys would run it.
    /// </summary>
    public static class HotkeyCapture
    {
        public static bool IsRecording { get; private set; }

        /// <summary>Raised with true when recording starts and false when it ends.</summary>
        public static event Action<bool> RecordingChanged;

        public static void SetRecording(bool isRecording)
        {
            if (IsRecording == isRecording) return;
            IsRecording = isRecording;
            RecordingChanged?.Invoke(isRecording);
        }
    }
}
