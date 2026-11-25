using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using EdgeLoop.Classes.Censoring;
using AwesomeAssertions;

namespace EdgeLoop.Tests.Stability
{
    /// <summary>
    /// Covers the detection geometry and the ONNX plumbing.
    ///
    /// The pure-maths tests always run. The tests that need a real model skip
    /// themselves when none is present, so a checkout without model files still
    /// gives a green build - set EDGELOOP_TEST_MODEL to point at an .onnx to
    /// exercise them.
    /// </summary>
    public class CensorDetectionTests
    {
        private static string FindModel()
        {
            var fromEnv = Environment.GetEnvironmentVariable("EDGELOOP_TEST_MODEL");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

            return CensorModelLocator.FindDefault();
        }

        // ---------------------------------------------------------------- geometry --

        [Fact]
        public void Grow_ExpandsBox_AndClampsToFrame()
        {
            var box = new Detection(3, 0.9f, 0.10f, 0.10f, 0.20f, 0.20f);

            var grown = box.Grow(0.5f); // +50% of size on each side

            grown.X.Should().BeApproximately(0.00f, 1e-5f);
            grown.Y.Should().BeApproximately(0.00f, 1e-5f);
            grown.Width.Should().BeApproximately(0.40f, 1e-5f);
            grown.Height.Should().BeApproximately(0.40f, 1e-5f);
        }

        [Fact]
        public void Grow_NeverEscapesTheFrame()
        {
            var box = new Detection(0, 0.5f, 0.8f, 0.8f, 0.2f, 0.2f);

            var grown = box.Grow(2.0f);

            grown.X.Should().BeGreaterThanOrEqualTo(0f);
            grown.Y.Should().BeGreaterThanOrEqualTo(0f);
            grown.Right.Should().BeLessThanOrEqualTo(1f);
            grown.Bottom.Should().BeLessThanOrEqualTo(1f);
        }

        [Fact]
        public void IoU_IsOneForIdenticalBoxes_AndZeroWhenDisjoint()
        {
            var a = new Detection(1, 1f, 0.1f, 0.1f, 0.3f, 0.3f);
            var same = new Detection(1, 1f, 0.1f, 0.1f, 0.3f, 0.3f);
            var far = new Detection(1, 1f, 0.7f, 0.7f, 0.2f, 0.2f);

            a.IoU(same).Should().BeApproximately(1f, 1e-5f);
            a.IoU(far).Should().Be(0f);
        }

        [Fact]
        public void IoU_HalfOverlap_IsOneThird()
        {
            // Two unit-ish boxes overlapping by exactly half their area:
            // intersection = 0.5, union = 1.5 -> 1/3
            var a = new Detection(0, 1f, 0.0f, 0.0f, 0.4f, 0.4f);
            var b = new Detection(0, 1f, 0.2f, 0.0f, 0.4f, 0.4f);

            // inter = 0.2 * 0.4 = 0.08 ; union = 0.16 + 0.16 - 0.08 = 0.24
            a.IoU(b).Should().BeApproximately(0.08f / 0.24f, 1e-5f);
        }

        // ------------------------------------------------------------------ classes --

        [Fact]
        public void ClassList_MatchesTheModelsEmbeddedOrder()
        {
            // These indices are what the output tensor rows mean. If they drift,
            // every mask and every censor decision silently targets the wrong thing.
            CensorClasses.Count.Should().Be(16);
            CensorClasses.Names.Should().HaveCount(16);

            CensorClasses.NameOf(0).Should().Be("FEMALE_FACE");
            CensorClasses.NameOf(3).Should().Be("FEMALE_GENITALIA_EXPOSED");
            CensorClasses.NameOf(7).Should().Be("FEMALE_BREAST_EXPOSED");
            CensorClasses.NameOf(11).Should().Be("MALE_GENITALIA_EXPOSED");
            CensorClasses.NameOf(15).Should().Be("EYE");
        }

        [Fact]
        public void DefaultMask_SelectsOnlyExplicitClasses()
        {
            int mask = CensorClasses.DefaultMask;

            CensorClasses.IsSet(mask, 3).Should().BeTrue();   // genitalia exposed
            CensorClasses.IsSet(mask, 5).Should().BeTrue();   // buttocks exposed
            CensorClasses.IsSet(mask, 7).Should().BeTrue();   // breast exposed
            CensorClasses.IsSet(mask, 11).Should().BeTrue();
            CensorClasses.IsSet(mask, 12).Should().BeTrue();

            CensorClasses.IsSet(mask, 0).Should().BeFalse();  // faces are not censored by default
            CensorClasses.IsSet(mask, 13).Should().BeFalse(); // feet covered
            CensorClasses.IsSet(mask, 15).Should().BeFalse(); // eyes
        }

        [Fact]
        public void IsSet_GuardsTheClassIndex_NotTheMask()
        {
            // -1 is every bit set, i.e. "censor everything" - a legitimate mask.
            CensorClasses.IsSet(-1, 0).Should().BeTrue();

            // The guard exists so a bogus class index cannot shift past the mask.
            CensorClasses.IsSet(-1, -1).Should().BeFalse();
            CensorClasses.IsSet(-1, 32).Should().BeFalse();
            CensorClasses.IsSet(int.MaxValue, 64).Should().BeFalse();
        }

        // -------------------------------------------------------------- onnx plumbing --

        [Fact]
        public void Detector_LoadsModel_AndReportsSquareInputSize()
        {
            var model = FindModel();
            if (model == null) return; // no model available in this checkout

            using var detector = NsfwDetector.Load(model);

            detector.InputSize.Should().BeOneOf(320, 640);
            detector.ModelPath.Should().Be(model);
        }

        [Fact]
        public void Detect_OnSyntheticFrame_ReturnsBoxesInsideTheFrame()
        {
            var model = FindModel();
            if (model == null) return;

            using var detector = NsfwDetector.Load(model);
            using var frame = MakeGradient(640, 360);

            // Wide open: every class, very low threshold. We are validating the
            // decode path, not the model's judgement - whatever comes back must
            // still be normalised and inside the frame.
            var results = detector.Detect(frame, 0.05f, 0.45f, 0xFFFF);

            results.Should().NotBeNull();

            foreach (var box in results)
            {
                box.X.Should().BeGreaterThanOrEqualTo(0f);
                box.Y.Should().BeGreaterThanOrEqualTo(0f);
                box.Right.Should().BeLessThanOrEqualTo(1.0001f);
                box.Bottom.Should().BeLessThanOrEqualTo(1.0001f);
                box.Width.Should().BeGreaterThan(0f);
                box.Height.Should().BeGreaterThan(0f);
                box.ClassId.Should().BeInRange(0, CensorClasses.Count - 1);
                box.Confidence.Should().BeInRange(0f, 1.0001f);
            }
        }

        [Fact]
        public void Detect_WithEmptyClassMask_ReturnsNothing()
        {
            var model = FindModel();
            if (model == null) return;

            using var detector = NsfwDetector.Load(model);
            using var frame = MakeGradient(640, 360);

            // Threshold at the floor, but no classes selected.
            var results = detector.Detect(frame, 0.01f, 0.45f, 0);

            results.Should().BeEmpty();
        }

        [Fact]
        public void Detect_HandlesNonStandardAspectRatios()
        {
            var model = FindModel();
            if (model == null) return;

            using var detector = NsfwDetector.Load(model);

            // Letterboxing is where box mapping usually goes wrong, so push it.
            foreach (var (w, h) in new[] { (320, 320), (1920, 200), (200, 1920), (17, 640) })
            {
                using var frame = MakeGradient(w, h);
                var results = detector.Detect(frame, 0.05f, 0.45f, 0xFFFF);

                results.Should().NotBeNull();
                results.All(b => b.Right <= 1.0001f && b.Bottom <= 1.0001f).Should().BeTrue(
                    $"boxes must stay normalised for a {w}x{h} frame");
            }
        }

        [Fact]
        public void Detector_IsSafeToDisposeTwice()
        {
            var model = FindModel();
            if (model == null) return;

            var detector = NsfwDetector.Load(model);
            detector.Dispose();
            detector.Dispose();

            // Calls after disposal are inert rather than throwing.
            using var frame = MakeGradient(64, 64);
            detector.Detect(frame, 0.5f, 0.45f, 0xFFFF).Should().BeEmpty();
        }

        [Fact]
        public void Load_ThrowsClearly_WhenModelMissing()
        {
            var missing = Path.Combine(Path.GetTempPath(), "edgeloop-no-such-model.onnx");

            Action act = () => NsfwDetector.Load(missing);

            act.Should().Throw<FileNotFoundException>();
        }

        // --------------------------------------------------------------------- helpers --

        private static Bitmap MakeGradient(int width, int height)
        {
            var bmp = new Bitmap(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int r = (int)(255.0 * x / Math.Max(1, width - 1));
                    int g = (int)(255.0 * y / Math.Max(1, height - 1));
                    bmp.SetPixel(x, y, Color.FromArgb(255, r, g, 128));
                }
            }
            return bmp;
        }
    }
}
