using Engine.Editor.WorldGen;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     INT-20/21: configuration B is layered on the asset without touching it, A's knobs round-trip through the
    ///     asset, and the difference views report what differs.
    /// </summary>
    public class PreviewCompareTests{
        private WorldGenSettings _settings;
        private BiomeSet _set;
        private Biome _plains;
        private Biome _desert;

        [SetUp]
        public void SetUp(){
            _settings = ScriptableObject.CreateInstance<WorldGenSettings>();
            _set = ScriptableObject.CreateInstance<BiomeSet>();
            _plains = ScriptableObject.CreateInstance<Biome>();
            _plains.name = "Plains";
            _desert = ScriptableObject.CreateInstance<Biome>();
            _desert.name = "Desert";
            _set.Biomes.Add(_plains);
            _set.Biomes.Add(_desert);
            _settings.Biomes = _set;
            _settings.Architecture = GenArchitecture.BiomeFirst;
            _settings.MaterialMethod = BlendMethod.None;
            _settings.HeightMethod = BlendMethod.None;
        }

        [TearDown]
        public void TearDown(){
            Object.DestroyImmediate(_settings);
            Object.DestroyImmediate(_set);
            Object.DestroyImmediate(_plains);
            Object.DestroyImmediate(_desert);
        }

        [Test]
        public void CaptureApply_RoundTripsEveryKnob(){
            PreviewConfiguration c = PreviewConfiguration.Capture(_settings);
            Assert.IsTrue(c.Matches(_settings));
            c.MaterialMethod = BlendMethod.DistanceBlend;
            c.MaterialBlendWidth = 48f;
            c.MaterialSigma = 0.07f;
            c.HeightMethod = BlendMethod.DistortedBoundary;
            c.HeightBlendWidth = 20f;
            c.HeightSigma = 0.09f;
            c.WarpAmplitude = 40f;
            c.WarpWavelength = 128f;
            c.WarpOctaves = 3;
            Assert.IsFalse(c.Matches(_settings));
            c.ApplyTo(_settings);
            Assert.IsTrue(c.Matches(_settings));
            Assert.AreEqual(48f, _settings.MaterialBlendWidth);
            Assert.AreEqual(3, _settings.WarpOctaves);
            Assert.IsTrue(PreviewConfiguration.Capture(_settings).Matches(_settings));
        }

        [Test]
        public void BuildConfig_UsesOverrides_AndLeavesTheAssetUntouched(){
            string before = _settings.ToCanonicalJson(7);
            PreviewConfiguration b = PreviewConfiguration.Capture(_settings);
            b.MaterialMethod = BlendMethod.ParameterSpaceWeighting;
            b.MaterialSigma = 0.1f;
            b.HeightMethod = BlendMethod.DistanceBlend;
            b.HeightBlendWidth = 16f;

            WorldGenConfig config = b.BuildConfig(_settings, 7);
            try{
                Assert.AreEqual(BlendMethod.ParameterSpaceWeighting, config.MaterialMethod);
                Assert.AreEqual(0.1f, config.MaterialSigma);
                Assert.AreEqual(BlendMethod.DistanceBlend, config.HeightMethod);
                Assert.AreEqual(16f, config.HeightBlendWidth);
                Assert.AreEqual(2, config.BiomeCount); // biomes come from the asset
            }
            finally{
                config.Dispose();
            }

            Assert.AreEqual(before, _settings.ToCanonicalJson(7));
            Assert.AreEqual(BlendMethod.None, _settings.MaterialMethod);
        }

        [Test]
        public void Describe_ShowsOnlyTheStrengthsInUse(){
            PreviewConfiguration c = PreviewConfiguration.Capture(_settings);
            c.MaterialMethod = BlendMethod.DistanceBlend;
            c.MaterialBlendWidth = 32f;
            StringAssert.Contains("Biome-first", c.Describe());
            StringAssert.Contains("B=32", c.Describe());
            StringAssert.DoesNotContain("σ", c.Describe());
            c.Architecture = GenArchitecture.TerrainFirst;
            StringAssert.DoesNotContain("height", c.Describe()); // TRN-21: no height stage
        }

        [Test]
        public void SignedDiff_ReportsMeanMaxAndShare(){
            using NativeArray<float> a = new(new[]{ 100f, 100f, 100f, 100f }, Allocator.TempJob);
            using NativeArray<float> b = new(new[]{ 100f, 100.5f, 104f, 92f }, Allocator.TempJob);
            using NativeArray<Color32> output = new(4, Allocator.TempJob);
            PreviewDiff.Stats stats = PreviewDiff.Signed(a, b, output);
            Assert.AreEqual(4, stats.Count);
            Assert.AreEqual(2, stats.Differing); // |Δ| ≥ 1 block
            Assert.AreEqual(8f, stats.MaxAbs, 1e-6f);
            Assert.AreEqual((0f + 0.5f + 4f + 8f) / 4f, stats.MeanAbs, 1e-6f);
            Assert.AreEqual((0f + 0.5f + 4f - 8f) / 4f, stats.Mean, 1e-6f);
            Assert.Greater(output[2].r, output[2].b); // B higher → red
            Assert.Greater(output[3].b, output[3].r); // B lower → blue
            Assert.AreEqual(output[0].r, output[0].b); // no difference → grey
        }

        [Test]
        public void Mismatch_MarksDifferingSamples_OverTheDimmedBase(){
            using NativeArray<int> a = new(new[]{ 0, 1, 2, 3 }, Allocator.TempJob);
            using NativeArray<int> b = new(new[]{ 0, 2, 2, 1 }, Allocator.TempJob);
            using NativeArray<Color32> baseView = new(new[]{
                new Color32(200, 100, 50, 255), new Color32(10, 10, 10, 255), new Color32(100, 100, 100, 255), new Color32(0, 0, 0, 255)
            }, Allocator.TempJob);
            using NativeArray<Color32> output = new(4, Allocator.TempJob);
            PreviewDiff.Stats stats = PreviewDiff.Mismatch(a, b, baseView, output);
            Assert.AreEqual(2, stats.Differing);
            Assert.AreEqual(0.5f, stats.Share);
            Assert.AreEqual(PreviewDiff.MismatchColor, output[1]);
            Assert.AreEqual(PreviewDiff.MismatchColor, output[3]);
            Assert.AreEqual(new Color32(60, 30, 15, 255), output[0]);

            using NativeArray<ushort> fa = new(new ushort[]{ 0, 5 }, Allocator.TempJob);
            using NativeArray<ushort> fb = new(new ushort[]{ 0, 5 }, Allocator.TempJob);
            using NativeArray<Color32> fBase = new(2, Allocator.TempJob);
            using NativeArray<Color32> fOut = new(2, Allocator.TempJob);
            Assert.AreEqual(0, PreviewDiff.Mismatch(fa, fb, fBase, fOut).Differing);
        }

        [Test]
        public void PixelDiff_CountsAnyChannel(){
            using NativeArray<Color32> a = new(new[]{ new Color32(10, 10, 10, 255), new Color32(10, 10, 10, 255) }, Allocator.TempJob);
            using NativeArray<Color32> b = new(new[]{ new Color32(10, 10, 10, 255), new Color32(10, 12, 10, 255) }, Allocator.TempJob);
            using NativeArray<Color32> output = new(2, Allocator.TempJob);
            PreviewDiff.Stats stats = PreviewDiff.Pixels(a, b, output);
            Assert.AreEqual(1, stats.Differing);
            Assert.AreEqual(8, output[1].g);
        }
    }
}
