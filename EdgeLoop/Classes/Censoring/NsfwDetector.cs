/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EdgeLoop.Classes.Censoring
{
    /// <summary>
    /// Runs a YOLO11 detection model over decoded video frames.
    ///
    /// This exists because Hotscreen (and anything else driven by screen capture)
    /// detects on the *composited* desktop. EdgeLoop's player windows are
    /// translucent, so by the time those pixels exist the video has already been
    /// blended with whatever is behind it - at 20% opacity the detector is mostly
    /// looking at the desktop, not the video. Running detection here, on the
    /// decoded frame before any window opacity is applied, sidesteps that
    /// entirely and works at any opacity.
    ///
    /// Model contract (read from the .onnx metadata, not assumed):
    ///   input  "images"  float32 [1, 3, S, S], RGB, 0..1, letterboxed
    ///   output "output0" float32 [1, 4 + numClasses, numAnchors]
    ///   exported with nms=False, so NMS is our job. Class scores are already
    ///   sigmoid-applied and there is no separate objectness channel (v8/v11 head).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class NsfwDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly int _inputSize;
        private readonly object _runLock = new();
        private bool _disposed;

        public int InputSize => _inputSize;
        public string ModelPath { get; }

        private NsfwDetector(InferenceSession session, string inputName, string outputName, int inputSize, string modelPath)
        {
            _session = session;
            _inputName = inputName;
            _outputName = outputName;
            _inputSize = inputSize;
            ModelPath = modelPath;
        }

        /// <summary>
        /// Loads a model. Deliberately CPU-only: on this workload the GPU is the
        /// contended resource (video decode plus any screen-capture detector),
        /// while the CPU usually has headroom. A YOLO11n at 320 costs single-digit
        /// milliseconds per frame on a modern desktop CPU.
        /// </summary>
        public static NsfwDetector Load(string modelPath, int threads = 0)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("Model path is empty.", nameof(modelPath));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Detection model not found.", modelPath);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };

            if (threads > 0)
                options.IntraOpNumThreads = threads;

            var session = new InferenceSession(modelPath, options);

            var input = session.InputMetadata.First();
            var output = session.OutputMetadata.First();

            // [1, 3, H, W] - take W. Fall back to 640 if the model is dynamic.
            int size = 640;
            var dims = input.Value.Dimensions;
            if (dims != null && dims.Length == 4 && dims[3] > 0)
                size = dims[3];

            return new NsfwDetector(session, input.Key, output.Key, size, modelPath);
        }

        /// <summary>
        /// Detects on a frame. Thread-safe, but calls are serialised - one worker
        /// is the intended usage.
        /// </summary>
        /// <param name="frame">Source frame. Any size; letterboxed internally.</param>
        /// <param name="confidence">Minimum class score to keep.</param>
        /// <param name="iouThreshold">NMS overlap threshold.</param>
        /// <param name="classMask">Bit N set = keep class N. See <see cref="CensorClasses"/>.</param>
        public IReadOnlyList<Detection> Detect(Bitmap frame, float confidence, float iouThreshold, int classMask)
        {
            if (_disposed || frame == null) return Array.Empty<Detection>();

            lock (_runLock)
            {
                if (_disposed) return Array.Empty<Detection>();

                int srcW = frame.Width;
                int srcH = frame.Height;
                if (srcW <= 0 || srcH <= 0) return Array.Empty<Detection>();

                // Letterbox: uniform scale, centred, grey padding - matching how
                // Ultralytics preprocesses at train/val time.
                float scale = Math.Min((float)_inputSize / srcW, (float)_inputSize / srcH);
                int newW = Math.Max(1, (int)Math.Round(srcW * scale));
                int newH = Math.Max(1, (int)Math.Round(srcH * scale));
                int padX = (_inputSize - newW) / 2;
                int padY = (_inputSize - newH) / 2;

                var tensor = BuildTensor(frame, newW, newH, padX, padY);

                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = _session.Run(inputs);
                var output = results.FirstOrDefault(r => r.Name == _outputName)?.AsTensor<float>()
                             ?? results.First().AsTensor<float>();

                return Decode(output, srcW, srcH, scale, padX, padY, confidence, iouThreshold, classMask);
            }
        }

        private DenseTensor<float> BuildTensor(Bitmap frame, int newW, int newH, int padX, int padY)
        {
            int s = _inputSize;
            var tensor = new DenseTensor<float>(new[] { 1, 3, s, s });
            var buffer = tensor.Buffer.Span;

            int planeR = 0;
            int planeG = s * s;
            int planeB = 2 * s * s;

            // 114/255 is Ultralytics' letterbox fill.
            const float pad = 114f / 255f;
            buffer.Fill(pad);

            var rect = new Rectangle(0, 0, frame.Width, frame.Height);
            BitmapData data = null;

            try
            {
                data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                unsafe
                {
                    byte* basePtr = (byte*)data.Scan0;
                    int stride = data.Stride;

                    // Nearest-neighbour resize straight into the tensor. Cheaper
                    // than allocating an intermediate Bitmap, and the detector is
                    // not sensitive to the difference at these scales.
                    for (int y = 0; y < newH; y++)
                    {
                        int srcY = (int)((y + 0.5f) * frame.Height / newH);
                        if (srcY >= frame.Height) srcY = frame.Height - 1;

                        byte* row = basePtr + (srcY * stride);
                        int dstRow = (y + padY) * s;

                        for (int x = 0; x < newW; x++)
                        {
                            int srcX = (int)((x + 0.5f) * frame.Width / newW);
                            if (srcX >= frame.Width) srcX = frame.Width - 1;

                            byte* px = row + (srcX * 4); // BGRA
                            int dst = dstRow + x + padX;

                            buffer[planeR + dst] = px[2] / 255f;
                            buffer[planeG + dst] = px[1] / 255f;
                            buffer[planeB + dst] = px[0] / 255f;
                        }
                    }
                }
            }
            finally
            {
                if (data != null) frame.UnlockBits(data);
            }

            return tensor;
        }

        private static IReadOnlyList<Detection> Decode(
            Tensor<float> output, int srcW, int srcH,
            float scale, int padX, int padY,
            float confidence, float iouThreshold, int classMask)
        {
            // [1, 4 + numClasses, numAnchors]
            var dims = output.Dimensions;
            if (dims.Length != 3) return Array.Empty<Detection>();

            int rows = dims[1];
            int anchors = dims[2];
            int numClasses = rows - 4;
            if (numClasses <= 0 || anchors <= 0) return Array.Empty<Detection>();

            var buffer = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray().AsSpan();

            var candidates = new List<Detection>();

            for (int a = 0; a < anchors; a++)
            {
                int bestClass = -1;
                float bestScore = confidence;

                for (int c = 0; c < numClasses; c++)
                {
                    if (!CensorClasses.IsSet(classMask, c)) continue;

                    float score = buffer[((4 + c) * anchors) + a];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestClass = c;
                    }
                }

                if (bestClass < 0) continue;

                float cx = buffer[(0 * anchors) + a];
                float cy = buffer[(1 * anchors) + a];
                float w = buffer[(2 * anchors) + a];
                float h = buffer[(3 * anchors) + a];

                // Input pixels -> un-letterbox -> source pixels -> normalised.
                float x1 = ((cx - (w * 0.5f)) - padX) / scale;
                float y1 = ((cy - (h * 0.5f)) - padY) / scale;
                float x2 = ((cx + (w * 0.5f)) - padX) / scale;
                float y2 = ((cy + (h * 0.5f)) - padY) / scale;

                float nx1 = Math.Clamp(x1 / srcW, 0f, 1f);
                float ny1 = Math.Clamp(y1 / srcH, 0f, 1f);
                float nx2 = Math.Clamp(x2 / srcW, 0f, 1f);
                float ny2 = Math.Clamp(y2 / srcH, 0f, 1f);

                if (nx2 <= nx1 || ny2 <= ny1) continue;

                candidates.Add(new Detection(bestClass, bestScore, nx1, ny1, nx2 - nx1, ny2 - ny1));
            }

            return NonMaxSuppression(candidates, iouThreshold);
        }

        private static List<Detection> NonMaxSuppression(List<Detection> boxes, float iouThreshold)
        {
            var kept = new List<Detection>();
            if (boxes.Count == 0) return kept;

            // Highest confidence first, suppress overlaps within the same class.
            boxes.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

            var suppressed = new bool[boxes.Count];

            for (int i = 0; i < boxes.Count; i++)
            {
                if (suppressed[i]) continue;
                kept.Add(boxes[i]);

                for (int j = i + 1; j < boxes.Count; j++)
                {
                    if (suppressed[j]) continue;
                    if (boxes[j].ClassId != boxes[i].ClassId) continue;
                    if (boxes[i].IoU(boxes[j]) > iouThreshold)
                        suppressed[j] = true;
                }
            }

            return kept;
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_runLock)
            {
                if (_disposed) return;
                _disposed = true;
                _session?.Dispose();
            }
        }
    }
}
