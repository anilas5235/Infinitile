using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using BlendWeights = Engine.Scripts.WorldGen.Blending.BlendWeights;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Phase 3 (§9, BLD-01/02, BLD-10…41, PERF-07): kernel taps, the four blend methods per stage, the lattice
    ///     cache against the pure sampler (TST-01), the method-independence of the Terrain-first height (TST-04),
    ///     Biome-first linearity (TST-05), method 4 in the hard limit (TST-06), argmax = hard class (TST-07), weight
    ///     normalisation (TST-09) and the dithered surface pick (TST-14).
    /// </summary>
    public class BlendingTests{
        private static readonly BlendMethod[] Methods ={
            BlendMethod.None, BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary, BlendMethod.ParameterSpaceWeighting
        };

        private readonly List<Object> _created = new();
        private readonly List<IDisposable> _native = new();

        [TearDown]
        public void TearDown(){
            foreach (IDisposable d in _native) d.Dispose();
            _native.Clear();
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Create<T>(string name) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            _created.Add(so);
            return so;
        }

        private Biome Biome(string name, float t, float f, float c, float e, float pv, float offset, float amp, float[] weights, float j){
            Biome b = Create<Biome>(name);
            b.TargetTemperature = t;
            b.TargetHumidity = f;
            b.TargetContinental = c;
            b.TargetErosion = e;
            b.TargetPV = pv;
            b.HeightOffset = offset;
            b.HeightAmplitude = amp;
            b.OctaveWeights = weights;
            b.Jaggedness = j;
            return b;
        }

        /// <summary>A settings asset with the six §19.2 placeholder biomes (and the §19.1 splines for Terrain-first).</summary>
        private WorldGenSettings Settings(GenArchitecture architecture, BlendMethod material, BlendMethod height){
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.Add(Biome("Plains", .55f, .45f, .45f, .70f, -0.2f, 136, 6, new[]{ 1f, .6f, .3f, .12f, .05f, .02f }, 0));
            set.Biomes.Add(Biome("Desert", .90f, .10f, .45f, .60f, 0f, 134, 8, new[]{ 1f, .8f, .2f, .05f, .02f, .01f }, 0));
            set.Biomes.Add(Biome("Mountain", .35f, .40f, .80f, .15f, 0.6f, 165, 22, new[]{ .7f, .7f, .6f, .45f, .3f, .15f }, 35));
            set.Biomes.Add(Biome("Snow", .15f, .50f, .70f, .35f, 0.3f, 158, 14, new[]{ .8f, .6f, .45f, .3f, .15f, .08f }, 15));
            set.Biomes.Add(Biome("Forest", .50f, .70f, .50f, .55f, 0f, 140, 9, new[]{ 1f, .6f, .35f, .15f, .06f, .03f }, 0));
            set.Biomes.Add(Biome("Ocean", .50f, .80f, .08f, .50f, 0f, 100, 6, new[]{ 1f, .5f, .2f, .05f, .02f, .01f }, 0));
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Biomes = set;
            s.Architecture = architecture;
            s.MaterialMethod = material;
            s.HeightMethod = height;
            if (architecture == GenArchitecture.TerrainFirst){
                s.Splines = Create<TerrainSplineSet>("Splines");
                s.Splines.ResetToPlaceholders();
            }

            return s;
        }

        private WorldGenConfig Config(int seed, WorldGenSettings settings){
            WorldGenConfig cfg = settings.ToConfig(seed);
            _native.Add(cfg);
            return cfg;
        }

        private WorldGenConfig Config(int seed, GenArchitecture architecture, BlendMethod material, BlendMethod height = BlendMethod.None){
            return Config(seed, Settings(architecture, material, height));
        }

        /// <summary>
        ///     Columns spread over ±<paramref name="extent" /> in short random walks (ten columns each, steps of up to 3
        ///     blocks), so boundaries are crossed densely as well as sampled far apart.
        /// </summary>
        private static int2[] Columns(uint seed, int count, int extent){
            Unity.Mathematics.Random rng = new(seed);
            int2[] columns = new int2[count];
            for (int n = 0; n < count; n++)
                columns[n] = n % 10 == 0 ? rng.NextInt2(-extent, extent) : columns[n - 1] + rng.NextInt2(-3, 4);
            return columns;
        }

        private static bool IsBlended(in BlendWeights w, int count){
            return w.Max(count) < 1f;
        }

        // --- Burst entry points (GEN-16) ------------------------------------------------------------------------------

        private delegate void SampleCachedFn(int x, int z, in WorldGenConfig cfg, in BlendCache cache, out ColumnSample sample);

        private delegate int ClassifyFn(in float2 q, in WorldGenConfig cfg);

        [Test]
        public void BlendEntryPoints_CompileWithBurst(){
            // GEN-16 guard for the Phase 3 direct-call entry points (see WorldSamplerTests.SampleColumn_CompilesWithBurst).
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst compilation is disabled in the Editor (Jobs > Burst)");
            FunctionPointer<SampleCachedFn> sample = BurstCompiler.CompileFunctionPointer<SampleCachedFn>(WorldSampler.SampleColumn);
            FunctionPointer<ClassifyFn> classify = BurstCompiler.CompileFunctionPointer<ClassifyFn>(WorldSampler.ClassifyAt);
            FunctionPointer<ClassifyFn> warped = BurstCompiler.CompileFunctionPointer<ClassifyFn>(WorldSampler.ClassifyWarpedAt);
            Assert.IsTrue(sample.IsCreated && classify.IsCreated && warped.IsCreated);

            WorldGenConfig cfg = Config(5, GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary);
            BlendCache cache = BlendCache.Build(cfg, new int2(0, 0), new int2(15, 15), 1, Allocator.Temp);
            try{
                sample.Invoke(7, 9, cfg, cache, out ColumnSample viaPointer);
                ColumnSample direct = WorldSampler.SampleColumn(7, 9, cfg);
                Assert.AreEqual(direct.Height, viaPointer.Height);
                Assert.AreEqual(direct.SurfaceSource, viaPointer.SurfaceSource);
                Assert.AreEqual(direct.MaterialWeights, viaPointer.MaterialWeights);
                float2 q = new(123.5f, -40.25f);
                Assert.AreEqual(WorldSampler.ClassifyAt(q, cfg), classify.Invoke(q, cfg));
                Assert.AreEqual(WorldSampler.ClassifyWarpedAt(q, cfg), warped.Invoke(q, cfg));
            }
            finally{
                cache.Dispose();
            }
        }

        // --- Kernel taps (BLD-10, BLD-12) -----------------------------------------------------------------------------

        [TestCase(2f, 1)]
        [TestCase(8f, 1)]
        [TestCase(15.9f, 1)]
        [TestCase(16f, 2)]
        [TestCase(32f, 2)]
        [TestCase(200f, 2)]
        public void KernelStride_IsClampOfFloorRadiusOverFour(float blendWidth, int stride){
            Assert.AreEqual(stride, KernelSet.StrideFor(blendWidth));
        }

        [TestCase(2f)]
        [TestCase(7f)]
        [TestCase(16f)]
        [TestCase(32f)]
        [TestCase(45f)]
        public void KernelTaps_CoverExactlyTheLatticePointsInsideRWithTheSpecKernel(float blendWidth){
            KernelSet set = KernelSet.Build(blendWidth, Allocator.Temp, out NativeArray<KernelTap> taps);
            try{
                float r = blendWidth * 0.5f;
                int s = set.Stride;
                Assert.AreEqual(r, set.Radius);
                int reach = 0;
                for (int phase = 0; phase < s * s; phase++){
                    int px = phase % s, pz = phase / s;
                    // Reference: every lattice point with |q − p| < R, in row-major order (z outer, x inner, BLD-12).
                    List<int2> expected = new();
                    int bound = (int)math.ceil(r) + 2;
                    for (int dz = -bound; dz <= bound; dz++)
                    for (int dx = -bound; dx <= bound; dx++)
                        if ((dx + px) % s == 0 && (dz + pz) % s == 0 && dx * dx + dz * dz < r * r)
                            expected.Add(new int2(dx, dz));

                    Assert.AreEqual(expected.Count, set.PhaseCount[phase], $"tap count of phase {phase}");
                    for (int t = 0; t < expected.Count; t++){
                        KernelTap tap = taps[set.PhaseStart[phase] + t];
                        Assert.AreEqual(expected[t], tap.Offset, $"tap order, phase {phase}");
                        float k = 1f - (tap.Offset.x * tap.Offset.x + tap.Offset.y * tap.Offset.y) / (r * r);
                        Assert.AreEqual(k * k, tap.K, 1e-6f, "k(r) = (1 − r²/R²)²");
                        Assert.Greater(tap.K, 0f);
                        reach = math.max(reach, math.cmax(math.abs(tap.Offset)));
                    }
                }

                Assert.AreEqual(reach, set.Reach);
            }
            finally{
                taps.Dispose();
            }
        }

        [Test]
        public void KernelPhase_UsesFloorModuloForNegativeColumns(){
            KernelSet set = KernelSet.Build(32f, Allocator.Temp, out NativeArray<KernelTap> taps);
            taps.Dispose();
            Assert.AreEqual(2, set.Stride);
            Assert.AreEqual(0, set.Phase(0, 0));
            Assert.AreEqual(1, set.Phase(-1, 0));
            Assert.AreEqual(2, set.Phase(0, -1));
            Assert.AreEqual(3, set.Phase(-3, 5));
            Assert.AreEqual(0, set.Phase(-4, -6));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void KernelTaps_RejectNonPositiveWidths(float blendWidth){
            Assert.Throws<ArgumentOutOfRangeException>(() => KernelSet.Build(blendWidth, Allocator.Temp, out _));
        }

        // --- Settings (BLD-01, PERF-07) -------------------------------------------------------------------------------

        [Test]
        public void ToConfig_RejectsInvalidStrengthsOnlyWhereTheyAreUsed(){
            WorldGenSettings s = Settings(GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, BlendMethod.None);
            s.MaterialBlendWidth = 0f;
            Assert.Throws<InvalidOperationException>(() => s.ToConfig(1).Dispose());

            s.MaterialMethod = BlendMethod.ParameterSpaceWeighting; // B_mat is unused now
            Config(1, s);
            s.MaterialSigma = -1f;
            Assert.Throws<InvalidOperationException>(() => s.ToConfig(1).Dispose());

            s.MaterialMethod = BlendMethod.None;
            s.HeightMethod = BlendMethod.DistortedBoundary;
            s.WarpAmplitude = -2f;
            Assert.Throws<InvalidOperationException>(() => s.ToConfig(1).Dispose());

            // The height stage does not exist in Terrain-first (TRN-21), so its strengths are not checked there.
            s.Architecture = GenArchitecture.TerrainFirst;
            s.Splines = Create<TerrainSplineSet>("Splines");
            s.Splines.ResetToPlaceholders();
            Config(1, s);
        }

        [Test]
        public void ToConfig_SharesStageWeightsOnlyForTheSameMethodAndStrength(){
            Assert.IsTrue(Config(1, GenArchitecture.BiomeFirst, BlendMethod.None).StagesShareWeights);
            Assert.IsTrue(Config(1, GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, BlendMethod.DistanceBlend).StagesShareWeights);
            Assert.IsTrue(Config(1, GenArchitecture.BiomeFirst, BlendMethod.ParameterSpaceWeighting, BlendMethod.ParameterSpaceWeighting)
                .StagesShareWeights);
            // Method 3 differs per stage by construction (warp only vs warp + kernel, BLD-22).
            Assert.IsFalse(Config(1, GenArchitecture.BiomeFirst, BlendMethod.DistortedBoundary, BlendMethod.DistortedBoundary)
                .StagesShareWeights);
            Assert.IsFalse(Config(1, GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend).StagesShareWeights);
            Assert.IsFalse(Config(1, GenArchitecture.TerrainFirst, BlendMethod.None).StagesShareWeights);

            WorldGenSettings s = Settings(GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, BlendMethod.DistanceBlend);
            s.HeightBlendWidth = 48f;
            Assert.IsFalse(Config(1, s).StagesShareWeights);
            s = Settings(GenArchitecture.BiomeFirst, BlendMethod.ParameterSpaceWeighting, BlendMethod.ParameterSpaceWeighting);
            s.HeightSigma = 0.08f;
            Assert.IsFalse(Config(1, s).StagesShareWeights);
        }

        // --- Methods (§9.2…9.5) ---------------------------------------------------------------------------------------

        [Test]
        public void WarpField_IsBoundedAndTheIdentityAtZeroAmplitude(){
            WorldGenConfig cfg = Config(9, GenArchitecture.BiomeFirst, BlendMethod.DistortedBoundary);
            Unity.Mathematics.Random rng = new(5);
            float2 extreme = 0f;
            for (int n = 0; n < 20000; n++){
                float2 q = rng.NextFloat2(-50000f, 50000f);
                float2 omega = WarpField.Sample(q, cfg);
                Assert.That(omega.x, Is.InRange(-1f, 1f));
                Assert.That(omega.y, Is.InRange(-1f, 1f));
                extreme = math.max(extreme, math.abs(omega));
                Assert.AreEqual(q + cfg.WarpAmplitude * omega, WarpField.Warp(q, cfg));
            }

            Assert.Greater(math.cmin(extreme), 0.5f, "both channels vary");
            cfg.WarpAmplitude = 0f;
            Assert.AreEqual(new float2(17.5f, -3f), WarpField.Warp(new float2(17.5f, -3f), cfg));
        }

        [Test]
        public void DistortedBoundary_WithZeroAmplitudeIsTheUnwarpedMethod(){
            // A = 0: the warp is the identity, so nn~ = nn; the material stage is method 1 and the height stage is
            // method 2 with the same B_hgt.
            WorldGenSettings s = Settings(GenArchitecture.BiomeFirst, BlendMethod.DistortedBoundary, BlendMethod.DistortedBoundary);
            s.WarpAmplitude = 0f;
            WorldGenConfig warped = Config(21, s);
            WorldGenConfig unwarped = Config(21, GenArchitecture.BiomeFirst, BlendMethod.None, BlendMethod.DistanceBlend);
            foreach (int2 p in Columns(2, 300, 30000)){
                ColumnSample a = WorldSampler.SampleColumn(p.x, p.y, warped);
                ColumnSample b = WorldSampler.SampleColumn(p.x, p.y, unwarped);
                Assert.AreEqual(b.MaterialWeights, a.MaterialWeights, $"material at {p}");
                Assert.AreEqual(b.HeightWeights, a.HeightWeights, $"height at {p}");
                Assert.AreEqual(b.Height, a.Height);
            }
        }

        [TestCase(GenArchitecture.TerrainFirst)]
        [TestCase(GenArchitecture.BiomeFirst)]
        public void DistortedBoundary_MaterialStageIsOneHotAtTheWarpedPosition(GenArchitecture architecture){
            // BLD-21/22: the material stage of method 3 is nn~(p), one-hot; the warp moves only the classification.
            WorldGenConfig cfg = Config(33, architecture, BlendMethod.DistortedBoundary);
            WorldGenConfig hard = Config(33, architecture, BlendMethod.None);
            int moved = 0;
            foreach (int2 p in Columns(4, 400, 30000)){
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                int expected = WorldSampler.ClassifyWarpedAt(p, cfg);
                Assert.AreEqual(BlendWeights.OneHot(expected), s.MaterialWeights, $"material at {p}");
                Assert.AreEqual(expected, s.SurfaceSource);
                ColumnSample h = WorldSampler.SampleColumn(p.x, p.y, hard);
                Assert.AreEqual(h.HardBiome, s.HardBiome, "the hard class itself is unwarped");
                Assert.AreEqual(h.Height, s.Height, "the material method does not touch the height");
                if (expected != s.HardBiome) moved++;
            }

            Assert.Greater(moved, 0, "the warp moves some boundary");
        }

        [Test]
        public void DistanceBlend_WeightsAreTheKernelOverTheHardClassesOfTheLattice(){
            // §9.3: w_b = Σ k(|q − p|)·[nn(q) = b] / Σ k over the world-aligned lattice points within R.
            WorldGenConfig cfg = Config(41, GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend);
            WorldGenConfig hard = Config(41, GenArchitecture.BiomeFirst, BlendMethod.None);
            KernelSet kernel = cfg.MaterialKernel;
            int blended = 0;
            foreach (int2 p in Columns(6, 200, 20000)){
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                double[] acc = new double[cfg.BiomeCount];
                double total = 0;
                int phase = kernel.Phase(p.x, p.y);
                for (int t = kernel.PhaseStart[phase]; t < kernel.PhaseStart[phase] + kernel.PhaseCount[phase]; t++){
                    KernelTap tap = cfg.MaterialTaps[t];
                    int2 q = p + tap.Offset;
                    Assert.IsTrue(math.all((q % kernel.Stride + kernel.Stride) % kernel.Stride == 0), "taps lie on the world lattice");
                    acc[WorldSampler.SampleColumn(q.x, q.y, hard).HardBiome] += tap.K;
                    total += tap.K;
                }

                for (int b = 0; b < cfg.BiomeCount; b++)
                    Assert.AreEqual(acc[b] / total, s.MaterialWeights[b], 1e-5, $"w_{b} at {p}");
                if (IsBlended(s.MaterialWeights, cfg.BiomeCount)) blended++;
            }

            Assert.Greater(blended, 0, "some sampled columns lie within R of a boundary");
        }

        [Test]
        public void ParameterSpace_IsTheShiftedGaussianOverTheParameterDistance(){
            // §9.5: w_b ∝ exp(−(d_b² − d_min²)/(2σ²)).
            WorldGenConfig cfg = Config(43, GenArchitecture.BiomeFirst, BlendMethod.ParameterSpaceWeighting);
            int blended = 0;
            foreach (int2 p in Columns(8, 300, 30000)){
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                double[] d = new double[cfg.BiomeCount];
                double dMin = double.MaxValue;
                for (int b = 0; b < cfg.BiomeCount; b++){
                    d[b] = BiomeClassifier.DistanceSquared(s.Fields, cfg.Biomes[b], cfg);
                    dMin = math.min(dMin, d[b]);
                }

                double sum = 0;
                double[] e = new double[cfg.BiomeCount];
                for (int b = 0; b < cfg.BiomeCount; b++){
                    e[b] = math.exp(-(d[b] - dMin) / (2.0 * cfg.MaterialSigma * cfg.MaterialSigma));
                    sum += e[b];
                }

                for (int b = 0; b < cfg.BiomeCount; b++) Assert.AreEqual(e[b] / sum, s.MaterialWeights[b], 1e-4, $"w_{b} at {p}");
                if (IsBlended(s.MaterialWeights, cfg.BiomeCount)) blended++;
            }

            Assert.Greater(blended, 0);
        }

        // --- TST-04 ---------------------------------------------------------------------------------------------------

        [Test]
        public void TerrainFirst_HeightIsBitwiseIdenticalForEveryMaterialMethod(){
            // TST-04 / TRN-20: the material method never feeds back into the Terrain-first height.
            WorldGenConfig[] configs = new WorldGenConfig[Methods.Length];
            for (int m = 0; m < Methods.Length; m++) configs[m] = Config(61, GenArchitecture.TerrainFirst, Methods[m]);
            foreach (int2 p in Columns(10, 500, 40000)){
                ColumnSample reference = WorldSampler.SampleColumn(p.x, p.y, configs[0]);
                for (int m = 1; m < Methods.Length; m++){
                    ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, configs[m]);
                    Assert.AreEqual(math.asuint(reference.Height), math.asuint(s.Height), $"{Methods[m]} at {p}");
                    Assert.AreEqual(reference.TerrainParams, s.TerrainParams);
                    Assert.AreEqual(reference.HardBiome, s.HardBiome);
                    Assert.AreEqual(BlendWeights.OneHot(s.HardBiome), s.HeightWeights, "no height stage in Terrain-first (TRN-21)");
                }
            }
        }

        // --- TST-05 ---------------------------------------------------------------------------------------------------

        [TestCase(BlendMethod.DistanceBlend)]
        [TestCase(BlendMethod.ParameterSpaceWeighting)]
        public void BiomeFirst_BlendedHeightIsTheWeightedSumOfTheBiomeHeights(BlendMethod method){
            // TST-05: H from the blended parameters vs Σ w_b H_b with the sampler's own height weights, 10⁴ columns.
            WorldGenConfig cfg = Config(71, GenArchitecture.BiomeFirst, BlendMethod.None, method);
            NativeArray<int2> columns = new(Columns(12, 10000, 40000), Allocator.TempJob);
            NativeArray<ColumnSample> samples = new(columns.Length, Allocator.TempJob);
            try{
                new PureColumnsJob{ Columns = columns, Config = cfg, Samples = samples }.Schedule(columns.Length, 64).Complete();
                int blended = 0;
                double maxError = 0;
                for (int n = 0; n < columns.Length; n++){
                    ColumnSample s = samples[n];
                    float2 p = columns[n];
                    double expected = 0;
                    for (int b = 0; b < cfg.BiomeCount; b++){
                        if (s.HeightWeights[b] == 0f) continue;
                        BiomeFirstHeight.Sample(p, BlendWeights.OneHot(b), cfg, out _, out float hb);
                        expected += s.HeightWeights[b] * (double)hb;
                    }

                    BiomeFirstHeight.Sample(p, s.HeightWeights, cfg, out _, out float unclamped);
                    maxError = math.max(maxError, math.abs(expected - unclamped));
                    if (IsBlended(s.HeightWeights, cfg.BiomeCount)) blended++;
                }

                Assert.Less(maxError, 1e-3, "max |H(Σ w θ) − Σ w H|");
                Assert.Greater(blended, 100, "enough blended columns to make the test meaningful");
            }
            finally{
                columns.Dispose();
                samples.Dispose();
            }
        }

        // --- TST-06 ---------------------------------------------------------------------------------------------------

        [TestCase(GenArchitecture.TerrainFirst)]
        [TestCase(GenArchitecture.BiomeFirst)]
        public void ParameterSpace_InTheHardLimitIsMethodOne(GenArchitecture architecture){
            // TST-06: σ = 1e−4 makes method 4 one-hot except at near-ties; same classification, height within 1e−3.
            WorldGenSettings s = Settings(architecture, BlendMethod.ParameterSpaceWeighting, BlendMethod.ParameterSpaceWeighting);
            s.MaterialSigma = 1e-4f;
            s.HeightSigma = 1e-4f;
            WorldGenConfig soft = Config(81, s);
            WorldGenConfig hard = Config(81, architecture, BlendMethod.None);
            foreach (int2 p in Columns(14, 3000, 40000)){
                ColumnSample a = WorldSampler.SampleColumn(p.x, p.y, soft);
                ColumnSample b = WorldSampler.SampleColumn(p.x, p.y, hard);
                Assert.AreEqual(b.HardBiome, a.HardBiome, $"class at {p}");
                Assert.AreEqual(b.DominantMaterialBiome, a.DominantMaterialBiome, $"dominant material at {p}");
                Assert.AreEqual(b.Height, a.Height, 1e-3f, $"height at {p}");
            }
        }

        // --- TST-07 ---------------------------------------------------------------------------------------------------

        [Test]
        public void ArgMaxOfTheMaterialWeightsIsTheHardClassForMethodsOneAndFour(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(BlendMethod.None, BlendMethod.ParameterSpaceWeighting)] BlendMethod method){
            // TST-07 / BLD-31: the hard class gets exp(0) = 1 before normalisation and every other biome at most 1;
            // ties (a maximum weight shared by two biomes) are excepted.
            WorldGenConfig cfg = Config(91, architecture, method);
            int ties = 0;
            foreach (int2 p in Columns(16, 3000, 40000)){
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                float max = s.MaterialWeights.Max(cfg.BiomeCount);
                int atMax = 0;
                for (int b = 0; b < cfg.BiomeCount; b++)
                    if (s.MaterialWeights[b] == max) atMax++;
                if (atMax > 1){
                    ties++;
                    continue;
                }

                Assert.AreEqual(s.HardBiome, s.MaterialWeights.ArgMax(cfg.BiomeCount), $"argmax at {p}");
                Assert.AreEqual(s.HardBiome, s.DominantMaterialBiome);
            }

            Assert.Less(ties, 3, "ties are rare");
        }

        // --- TST-09 ---------------------------------------------------------------------------------------------------

        /// <summary>Every stage combination: Terrain-first × 4 material methods, Biome-first × 4 × 4.</summary>
        private static IEnumerable<TestCaseData> StageCombinations(){
            foreach (BlendMethod material in Methods)
                yield return new TestCaseData(GenArchitecture.TerrainFirst, material, BlendMethod.None);
            foreach (BlendMethod material in Methods)
            foreach (BlendMethod height in Methods)
                yield return new TestCaseData(GenArchitecture.BiomeFirst, material, height);
        }

        [TestCaseSource(nameof(StageCombinations))]
        public void WeightsAreNormalisedForEveryMethodAndStage(GenArchitecture architecture, BlendMethod material, BlendMethod height){
            // TST-09 / BLD-02: Σ w = 1 ± 1e−5 and w ≥ 0 for both stages; inactive slots stay zero.
            WorldGenConfig cfg = Config(101, architecture, material, height);
            NativeArray<int2> columns = new(Columns(18, 2000, 40000), Allocator.TempJob);
            NativeArray<ColumnSample> samples = new(columns.Length, Allocator.TempJob);
            try{
                new PureColumnsJob{ Columns = columns, Config = cfg, Samples = samples }.Schedule(columns.Length, 32).Complete();
                for (int n = 0; n < columns.Length; n++){
                    ColumnSample s = samples[n];
                    AssertNormalised(s.MaterialWeights, cfg.BiomeCount, $"material at {columns[n]}");
                    AssertNormalised(s.HeightWeights, cfg.BiomeCount, $"height at {columns[n]}");
                    Assert.That(s.SurfaceSource, Is.InRange(0, cfg.BiomeCount - 1));
                    Assert.Greater(s.MaterialWeights[s.SurfaceSource], 0f, "the surface source has weight (BLD-40)");
                }
            }
            finally{
                columns.Dispose();
                samples.Dispose();
            }
        }

        private static void AssertNormalised(in BlendWeights w, int count, string where){
            float sum = 0f;
            for (int b = 0; b < BlendWeights.Capacity; b++){
                if (b >= count){
                    Assert.AreEqual(0f, w[b], where + ": inactive slot");
                    continue;
                }

                Assert.GreaterOrEqual(w[b], 0f, where);
                sum += w[b];
            }

            Assert.AreEqual(1f, sum, 1e-5f, where + ": Σ w");
        }

        // --- TST-01 ---------------------------------------------------------------------------------------------------

        [Test]
        public void CachedLatticeIsBitIdenticalToThePureSampler(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary)] BlendMethod method){
            // TST-01 / BLD-11 / GEN-02: 50 random chunks, sampled per chunk with the padded lattice cache (the
            // ChunkJob path, including the uniform-window early-out of PERF-07) and per column on the pure path;
            // weights, surface pick and height agree bit for bit. Biome-first runs the method on the height stage and
            // DistanceBlend on the material stage, so with method 3 both lattices are built.
            BlendMethod material = architecture == GenArchitecture.BiomeFirst ? BlendMethod.DistanceBlend : method;
            BlendMethod height = architecture == GenArchitecture.BiomeFirst ? method : BlendMethod.None;
            WorldGenConfig cfg = Config(111, architecture, material, height);

            const int chunkCount = 50, columnsPerChunk = ChunkWidth * ChunkDepth;
            Unity.Mathematics.Random rng = new(23);
            NativeArray<int2> chunks = new(chunkCount, Allocator.TempJob);
            NativeArray<int2> columns = new(chunkCount * columnsPerChunk, Allocator.TempJob);
            NativeArray<ColumnSample> cached = new(columns.Length, Allocator.TempJob);
            NativeArray<ColumnSample> pure = new(columns.Length, Allocator.TempJob);
            try{
                for (int c = 0; c < chunkCount; c++){
                    chunks[c] = rng.NextInt2(-1500, 1500);
                    for (int i = 0; i < columnsPerChunk; i++)
                        columns[c * columnsPerChunk + i] = chunks[c] * new int2(ChunkWidth, ChunkDepth) + new int2(i % ChunkWidth, i / ChunkWidth);
                }

                JobHandle a = new CachedChunksJob{ Chunks = chunks, Config = cfg, Samples = cached }.Schedule(chunkCount, 1);
                JobHandle b = new PureColumnsJob{ Columns = columns, Config = cfg, Samples = pure }.Schedule(columns.Length, 64);
                JobHandle.CombineDependencies(a, b).Complete();

                int interesting = 0;
                for (int n = 0; n < columns.Length; n++){
                    ColumnSample x = cached[n], y = pure[n];
                    string where = $"{columns[n]}";
                    for (int w = 0; w < BlendWeights.Capacity; w++){
                        Assert.AreEqual(math.asuint(y.MaterialWeights[w]), math.asuint(x.MaterialWeights[w]), $"w_mat[{w}] at {where}");
                        Assert.AreEqual(math.asuint(y.HeightWeights[w]), math.asuint(x.HeightWeights[w]), $"w_hgt[{w}] at {where}");
                    }

                    Assert.AreEqual(y.SurfaceSource, x.SurfaceSource, $"surface pick at {where}");
                    Assert.AreEqual(math.asuint(y.Height), math.asuint(x.Height), $"height at {where}");
                    Assert.AreEqual(y.HardBiome, x.HardBiome);
                    // Blended weights, or a one-hot warped class that differs from the hard class (method 3 material).
                    if (IsBlended(y.MaterialWeights, cfg.BiomeCount) || IsBlended(y.HeightWeights, cfg.BiomeCount)
                                                                     || y.SurfaceSource != y.HardBiome) interesting++;
                }

                Assert.Greater(interesting, 200, "the chunks cross boundaries, so the non-uniform path is compared too");
            }
            finally{
                chunks.Dispose();
                columns.Dispose();
                cached.Dispose();
                pure.Dispose();
            }
        }

        [Test]
        public void ColumnsOutsideTheCacheFallBackToThePurePath(){
            // R3: a cache that does not cover a column's window never changes the result.
            WorldGenConfig cfg = Config(121, GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary);
            BlendCache cache = BlendCache.Build(cfg, new int2(0, 0), new int2(7, 7), 1, Allocator.Temp);
            try{
                for (int x = -40; x < 48; x += 3)
                for (int z = -40; z < 48; z += 5){
                    WorldSampler.SampleColumn(x, z, cfg, cache, out ColumnSample viaCache);
                    ColumnSample direct = WorldSampler.SampleColumn(x, z, cfg);
                    Assert.AreEqual(direct.MaterialWeights, viaCache.MaterialWeights, $"({x},{z})");
                    Assert.AreEqual(direct.HeightWeights, viaCache.HeightWeights, $"({x},{z})");
                    Assert.AreEqual(math.asuint(direct.Height), math.asuint(viaCache.Height));
                }
            }
            finally{
                cache.Dispose();
            }
        }

        // --- TST-14, BLD-40/41 ----------------------------------------------------------------------------------------

        [Test]
        public void SurfacePick_NeverReturnsNoneForWeightsJustBelowOne(){
            // TST-14: Σ w = 1 − 1e−5; the fallback to the last non-zero weight covers rounding at u → 1.
            Unity.Mathematics.Random rng = new(31);
            for (int n = 0; n < 20000; n++){
                int count = rng.NextInt(1, BlendWeights.Capacity + 1);
                BlendWeights w = default;
                float sum = 0f;
                for (int b = 0; b < count; b++){
                    w[b] = rng.NextFloat() < 0.3f ? 0f : rng.NextFloat();
                    sum += w[b];
                }

                if (sum == 0f) w[count - 1] = sum = 1f;
                for (int b = 0; b < count; b++) w[b] = w[b] / sum * (1f - 1e-5f);
                int pick = SurfacePick.Pick(rng.NextInt(), rng.NextInt(), 7u, HashSalts.SurfacePick, w, count);
                Assert.That(pick, Is.InRange(0, count - 1));
                Assert.Greater(w[pick], 0f, "never a zero-weight biome");
            }
        }

        [Test]
        public void SurfacePick_OneHotReturnsItsBiomeAndAllZeroReturnsMinusOne(){
            // BLD-41: method 1 weights reproduce the hard class.
            for (int b = 0; b < BlendWeights.Capacity; b++)
            for (int n = 0; n < 50; n++)
                Assert.AreEqual(b, SurfacePick.Pick(n * 7919, -n, 11u, HashSalts.SurfacePick, BlendWeights.OneHot(b), BlendWeights.Capacity));
            Assert.AreEqual(-1, SurfacePick.Pick(0, 0, 11u, HashSalts.SurfacePick, default, BlendWeights.Capacity));
        }

        [Test]
        public void SurfacePick_FrequenciesFollowTheWeights(){
            // BLD-40: P(pick = b) = w_b, white noise per column.
            BlendWeights w = default;
            w[0] = 0.1f;
            w[1] = 0.6f;
            w[3] = 0.3f;
            int[] hits = new int[4];
            const int samples = 200000;
            for (int n = 0; n < samples; n++) hits[SurfacePick.Pick(n % 997, n / 997, 5u, HashSalts.SurfacePick, w, 4)]++;
            Assert.AreEqual(0.1, hits[0] / (double)samples, 0.01);
            Assert.AreEqual(0.6, hits[1] / (double)samples, 0.01);
            Assert.AreEqual(0, hits[2]);
            Assert.AreEqual(0.3, hits[3] / (double)samples, 0.01);
        }

        // --- Jobs -----------------------------------------------------------------------------------------------------

        /// <summary>The pure path: every column on its own, no cache (GEN-01).</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct PureColumnsJob : IJobParallelFor{
            [ReadOnly] public NativeArray<int2> Columns;
            [ReadOnly] public WorldGenConfig Config;
            [WriteOnly] public NativeArray<ColumnSample> Samples;

            public void Execute(int index){
                int2 c = Columns[index];
                WorldSampler.SampleColumn(c.x, c.y, Config, out ColumnSample s);
                Samples[index] = s;
            }
        }

        /// <summary>The chunk path of <c>ChunkJob.PrepareChunkMaps</c>: one padded cache per chunk, then every column.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct CachedChunksJob : IJobParallelFor{
            [ReadOnly] public NativeArray<int2> Chunks;
            [ReadOnly] public WorldGenConfig Config;

            [NativeDisableParallelForRestriction] [WriteOnly]
            public NativeArray<ColumnSample> Samples;

            public void Execute(int chunk){
                int2 min = Chunks[chunk] * new int2(ChunkWidth, ChunkDepth);
                BlendCache cache = BlendCache.Build(Config, min, min + new int2(ChunkWidth - 1, ChunkDepth - 1), 1, Allocator.Temp);
                int start = chunk * ChunkWidth * ChunkDepth;
                for (int i = 0; i < ChunkWidth * ChunkDepth; i++){
                    WorldSampler.SampleColumn(min.x + i % ChunkWidth, min.y + i / ChunkWidth, Config, cache, out ColumnSample s);
                    Samples[start + i] = s;
                }

                cache.Dispose();
            }
        }
    }
}
