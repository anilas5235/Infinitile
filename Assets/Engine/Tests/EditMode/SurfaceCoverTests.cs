using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The snow, ice and strata rules of <see cref="SurfaceCover" />: the effective temperature falls with the
    ///     height, snow needs a dry column and a snow block, a shore column at the water surface is snowy exactly where
    ///     the water freezes, the freeze noise makes the edge ragged, and the strata bands wrap below zero.
    /// </summary>
    public class SurfaceCoverTests{
        private const int WaterLevel = 124;

        private static readonly Biome.BiomeDef Snowy = new(){ snowTopBlock = FloraFixture.Snow };

        private static WorldGenConfig Config(float noiseAmplitude){
            // Only the scalar fields SurfaceCover reads; nothing to dispose.
            return new WorldGenConfig{
                Seed = TestFixtures.Seed,
                WaterLevel = WaterLevel,
                HeightMax = 256f,
                EnableClimateOverrides = true,
                FreezeTemperature = .2f,
                SnowLapse = .45f,
                FreezeNoiseAmplitude = noiseAmplitude,
                FreezeNoiseWavelength = 24f,
                StrataWarpAmplitude = 4f,
                StrataWarpWavelength = 64f
            };
        }

        [Test]
        public void EffectiveTemperature_FallsWithHeight(){
            WorldGenConfig cfg = Config(0f);
            Assert.AreEqual(.5f, SurfaceCover.EffectiveTemperature(3, 7, .5f, WaterLevel - 1, cfg), 1e-6f, "at the water surface");
            Assert.AreEqual(.5f, SurfaceCover.EffectiveTemperature(3, 7, .5f, 40, cfg), 1e-6f, "under water");
            Assert.AreEqual(.5f - cfg.SnowLapse, SurfaceCover.EffectiveTemperature(3, 7, .5f, 255, cfg), 1e-6f, "at HeightMax");
            float previous = float.MaxValue;
            for (int h = WaterLevel - 1; h < 256; h++){
                float t = SurfaceCover.EffectiveTemperature(3, 7, .5f, h, cfg);
                Assert.LessOrEqual(t, previous, $"h {h}");
                previous = t;
            }
        }

        [Test]
        public void IsSnowCovered_NeedsDryColumnSnowBlockAndOverrides(){
            WorldGenConfig cfg = Config(0f);
            Assert.IsTrue(SurfaceCover.IsSnowCovered(0, 0, .1f, WaterLevel - 1, Snowy, cfg), "cold shore");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, .1f, WaterLevel - 2, Snowy, cfg), "under water");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, .1f, WaterLevel + 10, default, cfg), "no snow block");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, .5f, WaterLevel + 10, Snowy, cfg), "warm lowland");
            Assert.IsTrue(SurfaceCover.IsSnowCovered(0, 0, .5f, 250, Snowy, cfg), "warm biome, high peak");
            cfg.EnableClimateOverrides = false;
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, .1f, WaterLevel - 1, Snowy, cfg), "overrides off");
            Assert.IsFalse(SurfaceCover.WaterFreezes(0, 0, .1f, cfg), "overrides off");
        }

        [Test]
        public void ShoreColumn_IsSnowyExactlyWhereTheWaterFreezes(){
            // A dry column at the water surface next to ice: same rule, so no grass strip between snow and ice.
            WorldGenConfig cfg = Config(.04f);
            int frozen = 0, open = 0;
            for (int x = -300; x < 300; x += 7)
            for (int z = -300; z < 300; z += 11)
            for (float t = .14f; t < .27f; t += .02f){
                bool ice = SurfaceCover.WaterFreezes(x, z, t, cfg);
                Assert.AreEqual(ice, SurfaceCover.IsSnowCovered(x, z, t, WaterLevel - 1, Snowy, cfg), $"({x},{z}) t {t}");
                if (ice) frozen++;
                else open++;
            }

            Assert.Greater(frozen, 0);
            Assert.Greater(open, 0);
        }

        [Test]
        public void FreezeNoise_MakesTheIceEdgeRagged(){
            // At exactly the freeze temperature the noise alone decides, so the water freezes in some places only.
            WorldGenConfig cfg = Config(.04f);
            int frozen = 0, n = 0;
            for (int x = 0; x < 2000; x += 5, n++)
                if (SurfaceCover.WaterFreezes(x, 17, cfg.FreezeTemperature, cfg))
                    frozen++;
            Assert.Greater(frozen, n / 10);
            Assert.Less(frozen, n - n / 10);

            cfg.FreezeNoiseAmplitude = 0f;
            Assert.IsTrue(SurfaceCover.WaterFreezes(5, 17, cfg.FreezeTemperature - 1e-4f, cfg));
            Assert.IsFalse(SurfaceCover.WaterFreezes(5, 17, cfg.FreezeTemperature, cfg));
        }

        [Test]
        public void InStrataBand_WrapsBelowZeroAndPeriodZeroTurnsItOff(){
            bool[] expected = { true, true, false, false, false, false, false };
            for (int y = -21; y < 21; y++){
                int phase = ((y % 7) + 7) % 7;
                Assert.AreEqual(expected[phase], SurfaceCover.InStrataBand(y, 0, 7, 2), $"y {y}");
                Assert.AreEqual(expected[(phase + 3) % 7], SurfaceCover.InStrataBand(y, 3, 7, 2), $"y {y} shift 3");
            }

            Assert.IsFalse(SurfaceCover.InStrataBand(0, 0, 0, 2), "period 0");
            Assert.IsFalse(SurfaceCover.InStrataBand(0, 0, 7, 0), "thickness 0");
        }

        [Test]
        public void StrataShift_StaysWithinTheAmplitudeAndVaries(){
            WorldGenConfig cfg = Config(0f);
            int min = int.MaxValue, max = int.MinValue;
            for (int x = -500; x < 500; x += 9)
            for (int z = -500; z < 500; z += 13){
                int shift = SurfaceCover.StrataShift(x, z, cfg);
                min = System.Math.Min(min, shift);
                max = System.Math.Max(max, shift);
            }

            Assert.GreaterOrEqual(min, -4);
            Assert.LessOrEqual(max, 4);
            Assert.Greater(max - min, 2, "the bands rise and fall");
        }
    }
}
