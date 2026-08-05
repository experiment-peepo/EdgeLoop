/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System;

namespace EdgeLoop.Classes.Censoring
{
    /// <summary>
    /// A single detected region, in coordinates normalised to the source video
    /// frame (0..1). Normalised so the overlay can map them onto whatever size
    /// the window happens to be without knowing the decode resolution.
    /// </summary>
    public readonly struct Detection
    {
        public int ClassId { get; }
        public float Confidence { get; }
        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        public Detection(int classId, float confidence, float x, float y, float width, float height)
        {
            ClassId = classId;
            Confidence = confidence;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float Right => X + Width;
        public float Bottom => Y + Height;

        public string Label => CensorClasses.NameOf(ClassId);

        /// <summary>Grows the box by <paramref name="fraction"/> of its own size on every side, clamped to the frame.</summary>
        public Detection Grow(float fraction)
        {
            if (fraction <= 0f) return this;

            float dx = Width * fraction;
            float dy = Height * fraction;

            float x = Math.Max(0f, X - dx);
            float y = Math.Max(0f, Y - dy);
            float r = Math.Min(1f, Right + dx);
            float b = Math.Min(1f, Bottom + dy);

            return new Detection(ClassId, Confidence, x, y, r - x, b - y);
        }

        public float IoU(in Detection other)
        {
            float ix = Math.Max(X, other.X);
            float iy = Math.Max(Y, other.Y);
            float ir = Math.Min(Right, other.Right);
            float ib = Math.Min(Bottom, other.Bottom);

            float iw = ir - ix;
            float ih = ib - iy;
            if (iw <= 0f || ih <= 0f) return 0f;

            float inter = iw * ih;
            float union = (Width * Height) + (other.Width * other.Height) - inter;
            return union <= 0f ? 0f : inter / union;
        }
    }

    /// <summary>
    /// The 16 classes emitted by the hs-real-anime / hs-real YOLO11 models, in
    /// model order. Taken from the `names` metadata embedded in the .onnx file,
    /// not guessed - the index order is what the output tensor rows mean.
    /// </summary>
    public static class CensorClasses
    {
        public const int Count = 16;

        public static readonly string[] Names =
        {
            "FEMALE_FACE",               // 0
            "MALE_FACE",                 // 1
            "FEMALE_GENITALIA_COVERED",  // 2
            "FEMALE_GENITALIA_EXPOSED",  // 3
            "BUTTOCKS_COVERED",          // 4
            "BUTTOCKS_EXPOSED",          // 5
            "FEMALE_BREAST_COVERED",     // 6
            "FEMALE_BREAST_EXPOSED",     // 7
            "MALE_BREAST_EXPOSED",       // 8
            "ARMPITS_EXPOSED",           // 9
            "BELLY_EXPOSED",             // 10
            "MALE_GENITALIA_EXPOSED",    // 11
            "ANUS_EXPOSED",              // 12
            "FEET_COVERED",              // 13
            "FEET_EXPOSED",              // 14
            "EYE",                       // 15
        };

        /// <summary>Explicit classes only - the sensible default to censor.</summary>
        public static readonly int DefaultMask =
            (1 << 3) |   // FEMALE_GENITALIA_EXPOSED
            (1 << 5) |   // BUTTOCKS_EXPOSED
            (1 << 7) |   // FEMALE_BREAST_EXPOSED
            (1 << 11) |  // MALE_GENITALIA_EXPOSED
            (1 << 12);   // ANUS_EXPOSED

        public static string NameOf(int classId) =>
            classId >= 0 && classId < Names.Length ? Names[classId] : $"CLASS_{classId}";

        public static bool IsSet(int mask, int classId) =>
            classId >= 0 && classId < 32 && (mask & (1 << classId)) != 0;
    }
}
