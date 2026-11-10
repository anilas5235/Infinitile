using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The snow, ice and strata rules of <see cref="SurfaceCover" />: snow caps the columns above a noisy snow line
    ///     and needs a dry column and a snow block, ice needs a biome whose water freezes, the freeze noise makes the ice
    ///     edge ragged, and the strata bands wrap below zero.
    /// </summary>
    public class SurfaceCoverTests{
        private const int WaterLevel = 124;

        private static readonly Biome.BiomeDef Snowy = new(){ snowTopBlock = FloraFixture.Snow, freezesWater = 1 };

        private static WorldGenConfig Config(float noiseAmplitude){
            // Only the scalar fields SurfaceCover reads; nothing to dispose.
            return new WorldGenConfig{
                Seed = TestFixtures.Seed,
                WaterLevel = WaterLevel,
                HeightMax = 256f,
                EnableClimateOverrides = true,
                FreezeTemperature = .2f,
                SnowLineHeight = 210f,
                SnowLineNoiseAmplitude = 6f,
                SnowLineNoiseWavelength = 48f,
                TreeLineHeight = 195f,
                TreeLineNoiseAmplitude = 8f,
                TreeLineNoiseWavelength = 64f,
                FloraDensityWavelength = new Unity.Mathematics.float4(96f, 48f, 24f, 64f),
                FloraDensityContrast = new Unity.Mathematics.float4(2f),
                FreezeNoiseAmplitude = noiseAmplitude,
                FreezeNoiseWavelength = 24f,
                StrataWarpAmplitude = 4f,
                StrataWarpWavelength = 64f
            };
        }

        [Test]
        public void EffectiveTemperature_IsTheTemperaturePlusTheFreezeNoise(){
            WorldGenConfig cfg = Config(0f);
            Assert.AreEqual(.5f, SurfaceCover.EffectiveTemperature(3, 7, .5f, cfg), 1e-6f, "no noise");
            cfg = Config(.04f);
            float noise = SurfaceCover.FreezeNoise(3, 7, cfg);
            Assert.AreEqual(.5f + .04f * noise, SurfaceCover.EffectiveTemperature(3, 7, .5f, cfg), 1e-6f);
        }

        [Test]
        public void IsSnowCovered_OnlyAboveTheSnowLine_NeedsSnowBlockAndOverrides(){
            WorldGenConfig cfg = Config(0f);
            Assert.IsTrue(SurfaceCover.IsSnowCovered(0, 0, 230, Snowy, cfg), "peak above the highest snow line");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, 190, Snowy, cfg), "slope below the lowest snow line");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, WaterLevel - 1, Snowy, cfg), "shore");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, 230, default, cfg), "no snow block");
            cfg.SnowLineHeight = WaterLevel - 20f;
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, WaterLevel - 2, Snowy, cfg), "under water");
            cfg = Config(0f);
            cfg.EnableClimateOverrides = false;
            Assert.IsFalse(SurfaceCover.IsSnowCovered(0, 0, 230, Snowy, cfg), "overrides off");
            Assert.IsFalse(SurfaceCover.WaterFreezes(0, 0, .1f, Snowy, cfg), "overrides off");
        }

        [Test]
        public void SnowLine_WobblesWithinTheAmplitude_AndDecidesTheSnowEdge(){
            // The line is no straight contour: it varies over the world, stays within ±amplitude, and a column is
            // snowy exactly where its top face reaches the line.
            WorldGenConfig cfg = Config(0f);
            float min = float.MaxValue, max = float.MinValue;
            for (int x = -600; x < 600; x += 7)
            for (int z = -600; z < 600; z += 11){
                float line = SurfaceCover.SnowLine(x, z, cfg);
                min = System.Math.Min(min, line);
                max = System.Math.Max(max, line);
                for (int h = 200; h <= 220; h++)
                    Assert.AreEqual(h + 1 >= line, SurfaceCover.IsSnowCovered(x, z, h, Snowy, cfg), $"({x},{z}) h {h} line {line}");
            }

            Assert.GreaterOrEqual(min, 204f);
            Assert.LessOrEqual(max, 216f);
            Assert.Greater(max - min, 6f, "the line rises and falls");

            cfg.SnowLineNoiseAmplitude = 0f;
            Assert.AreEqual(210f, SurfaceCover.SnowLine(5, 9, cfg));
            Assert.IsTrue(SurfaceCover.IsSnowCovered(5, 9, 209, Snowy, cfg), "top face at 210");
            Assert.IsFalse(SurfaceCover.IsSnowCovered(5, 9, 208, Snowy, cfg), "top face at 209");
        }

        [Test]
        public void IsForestFloor_FollowsTheTreeDensityNoise_OnDryColumnsOnly(){
            // The floor lies exactly where the tree density factor reaches the biome's threshold; a higher threshold
            // gives less of it, and columns under water or biomes without the block have none.
            WorldGenConfig cfg = Config(0f);
            Biome.BiomeDef forest = new(){ forestFloorBlock = FloraFixture.Granite, forestFloorDensity = 1.5f };
            Biome.BiomeDef sparse = new(){ forestFloorBlock = FloraFixture.Granite, forestFloorDensity = 1.9f };
            int floor = 0, open = 0, sparseFloor = 0;
            for (int x = -600; x < 600; x += 7)
            for (int z = -600; z < 600; z += 11){
                float density = Engine.Scripts.WorldGen.Flora.FloraPlacement.DensityFactor(x, z, 0, cfg);
                bool expected = density >= 1.5f;
                Assert.AreEqual(expected, SurfaceCover.IsForestFloor(x, z, 150, forest, cfg), $"({x},{z}) density {density}");
                Assert.AreEqual(expected, SurfaceCover.IsForestFloor(x, z, WaterLevel - 1, forest, cfg), "top face at the water surface");
                Assert.IsFalse(SurfaceCover.IsForestFloor(x, z, WaterLevel - 2, forest, cfg), "under water");
                Assert.IsFalse(SurfaceCover.IsForestFloor(x, z, 150, default, cfg), "no forest floor block");
                if (expected) floor++;
                else open++;
                if (SurfaceCover.IsForestFloor(x, z, 150, sparse, cfg)) sparseFloor++;
            }

            Assert.Greater(floor, 1000, "dense forest");
            Assert.Greater(open, floor, "most of the biome keeps its top block");
            Assert.Less(sparseFloor, floor * 3 / 4, "a higher threshold shrinks the patches");
            Assert.Greater(sparseFloor, 0);
        }

        [Test]
        public void TreeLine_WobblesWithinTheAmplitude_AndZeroTurnsItOff(){
            // Like the snow line: no straight contour, within ±amplitude, and a column is above the line exactly
            // where its top face reaches it. It has its own noise, not the snow line's.
            WorldGenConfig cfg = Config(0f);
            float min = float.MaxValue, max = float.MinValue;
            int apart = 0;
            for (int x = -600; x < 600; x += 7)
            for (int z = -600; z < 600; z += 11){
                float line = SurfaceCover.TreeLine(x, z, cfg);
                min = System.Math.Min(min, line);
                max = System.Math.Max(max, line);
                for (int h = 180; h <= 206; h++)
                    Assert.AreEqual(h + 1 >= line, SurfaceCover.AboveTreeLine(x, z, h, cfg), $"({x},{z}) h {h} line {line}");
                float snow = (SurfaceCover.SnowLine(x, z, cfg) - 210f) / 6f;
                if (System.Math.Abs((line - 195f) / 8f - snow) > .25f) apart++;
            }

            Assert.GreaterOrEqual(min, 187f);
            Assert.LessOrEqual(max, 203f);
            Assert.Greater(max - min, 8f, "the line rises and falls");
            Assert.Greater(apart, 1000, "the tree line does not follow the snow line noise");

            cfg.TreeLineNoiseAmplitude = 0f;
            Assert.AreEqual(195f, SurfaceCover.TreeLine(5, 9, cfg));
            Assert.IsTrue(SurfaceCover.AboveTreeLine(5, 9, 194, cfg), "top face at 195");
            Assert.IsFalse(SurfaceCover.AboveTreeLine(5, 9, 193, cfg), "top face at 194");
            cfg.TreeLineHeight = 0f;
            Assert.IsFalse(SurfaceCover.AboveTreeLine(5, 9, 250, cfg), "height 0: no tree line");
        }

        [Test]
        public void WaterFreezes_OnlyInBiomesWhoseWaterFreezes(){
            WorldGenConfig cfg = Config(0f);
            Assert.IsTrue(SurfaceCover.WaterFreezes(0, 0, .1f, Snowy, cfg), "cold biome");
            Assert.IsFalse(SurfaceCover.WaterFreezes(0, 0, .1f, default, cfg), "biome without freezesWater");
            Assert.IsFalse(SurfaceCover.WaterFreezes(.1f, 0f, default, cfg), "biome without freezesWater, noise given");
        }

        [Test]
        public void FreezeNoise_MakesTheIceEdgeRagged(){
            // At exactly the freeze temperature the noise alone decides, so the water freezes in some places only.
            WorldGenConfig cfg = Config(.04f);
            int frozen = 0, n = 0;
            for (int x = 0; x < 2000; x += 5, n++)
                if (SurfaceCover.WaterFreezes(x, 17, cfg.FreezeTemperature, Snowy, cfg))
                    frozen++;
            Assert.Greater(frozen, n / 10);
            Assert.Less(frozen, n - n / 10);

            cfg.FreezeNoiseAmplitude = 0f;
            Assert.IsTrue(SurfaceCover.WaterFreezes(5, 17, cfg.FreezeTemperature - 1e-4f, Snowy, cfg));
            Assert.IsFalse(SurfaceCover.WaterFreezes(5, 17, cfg.FreezeTemperature, Snowy, cfg));
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
        public void StrataBand_CountsThePeriodsUpwards_AlsoBelowZero(){
            // One number per period: constant across a band and the gap above it, one more per period upwards.
            for (int shift = -9; shift <= 9; shift += 3)
            for (int y = -30; y < 60; y++){
                int band = SurfaceCover.StrataBand(y, shift, 7);
                Assert.AreEqual((int)System.Math.Floor((y + shift) / 7.0), band, $"y {y} shift {shift}");
                // The band starts where the phase is 0.
                if (SurfaceCover.InStrataBand(y, shift, 7, 1))
                    Assert.AreEqual(band - 1, SurfaceCover.StrataBand(y - 1, shift, 7), $"y {y} shift {shift}: a new band starts");
            }

            Assert.AreEqual(0, SurfaceCover.StrataBand(40, 2, 0), "period 0");
        }

        [Test]
        public void StrataBlockOf_CyclesThroughThePalette(){
            Biome.BiomeDef one = new(){ strataBlock = 5 };
            Biome.BiomeDef three = new(){ strataBlock = 5, strataBlock1 = 6, strataBlock2 = 7, strataCount = 3 };
            ushort[] cycle = { 5, 6, 7 };
            for (int band = -7; band < 9; band++){
                Assert.AreEqual(5, one.StrataBlockOf(band), $"band {band}: no palette");
                Assert.AreEqual(cycle[((band % 3) + 3) % 3], three.StrataBlockOf(band), $"band {band}");
            }
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
