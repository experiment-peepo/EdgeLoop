/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace EdgeLoop.Classes.Censoring
{
    /// <summary>
    /// Owns one detector and pumps frames through it off the UI thread.
    ///
    /// Frames are submitted from the UI thread (that is where the D3D snapshot
    /// has to happen) and inference runs on a single background worker. If a
    /// frame arrives while the worker is busy the older pending frame is dropped
    /// rather than queued - falling behind is worse than skipping, and the
    /// tracker below covers the gap.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class CensorService : IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly List<Track> _tracks = new();

        private NsfwDetector _detector;
        private Thread _worker;
        private Bitmap _pending;
        private volatile bool _running;
        private bool _disposed;

        /// <summary>Fires on the worker thread. Marshal before touching UI.</summary>
        public event Action<IReadOnlyList<Detection>> ResultsReady;

        public float Confidence { get; set; } = 0.35f;
        public float IoUThreshold { get; set; } = 0.45f;
        public int ClassMask { get; set; } = CensorClasses.DefaultMask;
        public float Padding { get; set; } = 0.04f;

        /// <summary>
        /// How long a box survives after the detector stops reporting it.
        /// Detection is inherently jittery frame to frame; without this the
        /// censor blinks. Equivalent to Hotscreen's "stay detected during".
        /// </summary>
        public int HoldMs { get; set; } = 450;

        public bool IsReady => _detector != null;
        public string Status { get; private set; } = "Not started";
        public string ModelPath => _detector?.ModelPath;

        /// <summary>Model input edge length, so callers can size their frame grab to match.</summary>
        public int InputSize => _detector?.InputSize ?? 0;

        public bool Start(string modelPath, int threads = 0)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_detector != null) return true;

                try
                {
                    _detector = NsfwDetector.Load(modelPath, threads);
                }
                catch (Exception ex)
                {
                    Status = "Failed to load model: " + ex.Message;
                    Logger.Error($"[Censor] Could not load detection model '{modelPath}': {ex.Message}", ex);
                    return false;
                }

                _running = true;
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "EdgeLoop.Censor",
                    Priority = ThreadPriority.BelowNormal,
                };
                _worker.Start();

                Status = $"Running ({Path.GetFileName(modelPath)}, {_detector.InputSize}px)";
                Logger.Info($"[Censor] Detector started: {modelPath} @ {_detector.InputSize}px");
                return true;
            }
        }

        /// <summary>Submits a frame. Takes ownership of the bitmap.</summary>
        public void Submit(Bitmap frame)
        {
            if (frame == null) return;

            if (!_running)
            {
                frame.Dispose();
                return;
            }

            Bitmap dropped = null;

            lock (_gate)
            {
                dropped = _pending;
                _pending = frame;
            }

            dropped?.Dispose();

            try
            {
                if (_signal.CurrentCount == 0) _signal.Release();
            }
            catch (SemaphoreFullException) { /* worker already has a wake pending */ }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                try
                {
                    if (!_signal.Wait(250)) continue;
                }
                catch (ObjectDisposedException) { return; }

                if (!_running) return;

                Bitmap frame;
                lock (_gate)
                {
                    frame = _pending;
                    _pending = null;
                }

                if (frame == null) continue;

                try
                {
                    var raw = _detector.Detect(frame, Confidence, IoUThreshold, ClassMask);
                    var stable = Stabilize(raw);
                    ResultsReady?.Invoke(stable);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Censor] Detection pass failed: {ex.Message}");
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }

        /// <summary>
        /// Matches new detections against the previous set and holds boxes that
        /// briefly disappear, so the censor does not flicker. Positions are eased
        /// toward the new measurement rather than snapping, which also damps the
        /// frame-to-frame jitter of the raw model output.
        /// </summary>
        private IReadOnlyList<Detection> Stabilize(IReadOnlyList<Detection> incoming)
        {
            const float matchIoU = 0.25f;
            const float ease = 0.55f;

            long now = Environment.TickCount64;
            var matched = new bool[_tracks.Count];

            foreach (var det in incoming)
            {
                int best = -1;
                float bestIoU = matchIoU;

                for (int i = 0; i < _tracks.Count; i++)
                {
                    if (matched[i]) continue;
                    if (_tracks[i].Box.ClassId != det.ClassId) continue;

                    float iou = _tracks[i].Box.IoU(det);
                    if (iou > bestIoU)
                    {
                        bestIoU = iou;
                        best = i;
                    }
                }

                if (best >= 0)
                {
                    matched[best] = true;
                    _tracks[best] = new Track(Lerp(_tracks[best].Box, det, ease), now);
                }
                else
                {
                    _tracks.Add(new Track(det, now));
                }
            }

            for (int i = _tracks.Count - 1; i >= 0; i--)
            {
                if (now - _tracks[i].LastSeen > HoldMs)
                    _tracks.RemoveAt(i);
            }

            float pad = Padding;
            return _tracks.Select(t => pad > 0f ? t.Box.Grow(pad) : t.Box).ToList();
        }

        private static Detection Lerp(in Detection from, in Detection to, float t)
        {
            return new Detection(
                to.ClassId,
                to.Confidence,
                from.X + ((to.X - from.X) * t),
                from.Y + ((to.Y - from.Y) * t),
                from.Width + ((to.Width - from.Width) * t),
                from.Height + ((to.Height - from.Height) * t));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _running = false;
            }

            try { _signal.Release(); } catch { /* already at max or disposed */ }

            try { _worker?.Join(1500); } catch { /* best effort */ }

            lock (_gate)
            {
                _pending?.Dispose();
                _pending = null;
                _detector?.Dispose();
                _detector = null;
            }

            _signal.Dispose();
            Status = "Stopped";
        }

        private readonly struct Track
        {
            public Detection Box { get; }
            public long LastSeen { get; }

            public Track(Detection box, long lastSeen)
            {
                Box = box;
                LastSeen = lastSeen;
            }
        }
    }

    /// <summary>Finds a detection model without hard-coding anyone's install path.</summary>
    public static class CensorModelLocator
    {
        /// <summary>Preference order: smaller/faster first, fp32 because this runs on CPU.</summary>
        private static readonly string[] Preferred =
        {
            "hs-real-anime-y11n-320-fp32.onnx",
            "hs-real-y11n-320-fp32.onnx",
            "hs-real-anime-y11n-640-fp32.onnx",
            "hs-real-y11n-640-fp32.onnx",
        };

        public static IEnumerable<string> SearchDirectories()
        {
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
            yield return Path.Combine(AppPaths.DataDirectory, "Models");
        }

        /// <summary>Returns the best model found, or null.</summary>
        public static string FindDefault()
        {
            foreach (var dir in SearchDirectories())
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                foreach (var name in Preferred)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }

                // Any .onnx will do if none of the preferred names are present.
                var any = Directory.EnumerateFiles(dir, "*.onnx").FirstOrDefault();
                if (any != null) return any;
            }

            return null;
        }
    }
}
