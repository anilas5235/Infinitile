using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The per-column flora decision on the pure sampler and the region API, the structure expansion and the
    ///     config tables the chunk job stamps from.
    /// </summary>
    public class FloraPlacementTests{
        private const int WaterLevel = 124;

        private FloraFixture _fixture;
        private WorldGenConfig _cfg;

        [SetUp]
        public void SetUp(){
            _fixture = new FloraFixture();
        }

        [TearDown]
        public void TearDown(){
            _cfg.Dispose();
            _cfg = default;
            _fixture.Dispose();
        }

        private void Use(GenArchitecture architecture, BlendMethod method, params Biome[] biomes){
            WorldGenSettings s = _fixture.Settings(architecture, method, biomes);
            Assert.AreEqual(WaterLevel, s.WaterLevel);
            _cfg = _fixture.ToConfig(s);
        }

        private RegionSample Region(int2 origin, int2 size){
            return RegionSample.Sample(_cfg, origin, size);
        }

        private static readonly int2 Origin = new(-300, 120);
        private static readonly int2 Size = new(128, 128);

        // --- Flora biome and species ---------------------------------------------------------------------------------

        [Test]
        public void FloraSource_IsTheDitheredPickWithTheFloraSalt(){
            // The flora biome is its own roll on w_mat, so at transitions it may differ from the
            // surface source; it never differs from Pick(x, z, FloraBiome).
            Use(GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend);
            RegionSample r = Region(new int2(-1200, -400), new int2(256, 256));
            int planted = 0, differs = 0;
            for (int i = 0; i < r.FloraSpecies.Length; i++){
                if (r.FloraSpecies[i] == 0){
                    Assert.AreEqual(-1, r.FloraSource[i]);
                    continue;
                }

                planted++;
                int x = r.Origin.x + i % r.Size.x, z = r.Origin.y + i / r.Size.x;
                Assert.AreEqual(SurfacePick.Pick(x, z, _cfg.Seed, HashSalts.FloraBiome, r.MaterialWeights[i], _cfg.BiomeCount),
                    r.FloraSource[i], $"({x},{z})");
                if (r.FloraSource[i] != r.SurfaceSource[i]) differs++;
            }

            Assert.Greater(planted, 1000);
            Assert.Greater(differs, 0, "some plant comes from another biome than the ground it stands on");
        }

        [Test]
        public void FloraSource_WithoutMaterialBlend_IsTheHardBiome(){
            Use(GenArchitecture.BiomeFirst, BlendMethod.None);
            RegionSample r = Region(Origin, Size);
            for (int i = 0; i < r.FloraSpecies.Length; i++)
                if (r.FloraSpecies[i] != 0)
                    Assert.AreEqual(r.HardBiome[i], r.FloraSource[i]);
        }

        [Test]
        public void SpeciesFrequencies_MatchTheRuleProbabilities(){
            // Hash01(SALT_FLORA_SPECIES) against cumulative probabilities; past the last rule there is no plant.
            Biome flat = _fixture.MakeBiome("Flat", .5f, 150f, 2f, _fixture.GrassSo,
                FloraFixture.Rule(_fixture.TuftSo, .3f), FloraFixture.Rule(_fixture.FlowerSo, .1f));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, flat);
            RegionSample r = Region(Origin, Size);
            float n = r.FloraSpecies.Length;
            float tuft = r.FloraSpecies.Count(s => s == 1) / n;
            float flower = r.FloraSpecies.Count(s => s == 2) / n;
            Assert.AreEqual(.3f, tuft, .02f, "tuft");
            Assert.AreEqual(.1f, flower, .015f, "flower");
            Assert.AreEqual(0, r.FloraSpecies.Count(s => s > 2));
            for (int i = 0; i < r.FloraSpecies.Length; i++){
                int x = r.Origin.x + i % r.Size.x, z = r.Origin.y + i / r.Size.x;
                float u = Hash.Hash01(x, z, _cfg.Seed, HashSalts.FloraSpecies);
                ushort expected = u < _cfg.FloraRules[0].CumulativeEnd ? (ushort)1 : u < _cfg.FloraRules[1].CumulativeEnd ? (ushort)2 : (ushort)0;
                Assert.AreEqual(expected, r.FloraSpecies[i], $"({x},{z}) u={u}");
            }
        }

        // --- Ground conditions ---------------------------------------------------------------------------------------

        [Test]
        public void RequiresSurfaceFromSameBiome_SuppressesPlantsOnForeignGround(){
            Biome[] biomes = _fixture.DefaultBiomes();
            foreach (FloraRule rule in biomes.SelectMany(b => b.FloraRules)) rule.RequiresSurfaceFromSameBiome = true;
            Use(GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, biomes);
            RegionSample r = Region(new int2(-1200, -400), new int2(256, 256));
            int planted = 0;
            for (int i = 0; i < r.FloraSpecies.Length; i++){
                if (r.FloraSpecies[i] == 0) continue;
                planted++;
                Assert.AreEqual(r.SurfaceSource[i], r.FloraSource[i]);
            }

            Assert.Greater(planted, 1000);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(0)]
        [TestCase(-3)]
        public void MinHeightAboveWater_PlantsExactlyWhereTheTopFaceIsHighEnough(int minHeight){
            // heightAboveWater = h_int + 1 − WaterLevel; a negative minimum counts as 0 (never below water).
            // Probability 1: every column rolls the rule, so the ground condition alone decides.
            FloraRule rule = FloraFixture.Rule(_fixture.TuftSo, 1f);
            rule.MinHeightAboveWater = minHeight;
            Biome shore = _fixture.MakeBiome("Shore", .5f, 126f, 8f, _fixture.GrassSo, rule);
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, shore);
            RegionSample r = Region(Origin, Size);
            int planted = 0, bare = 0;
            for (int i = 0; i < r.FloraSpecies.Length; i++){
                int above = r.ColumnHeight[i] + 1 - WaterLevel;
                bool expected = above >= math.max(minHeight, 0);
                Assert.AreEqual(expected, r.FloraSpecies[i] != 0, $"column {i}: h = {r.ColumnHeight[i]}");
                if (expected) planted++;
                else bare++;
            }

            Assert.Greater(planted, 0);
            Assert.Greater(bare, 0, "the shore straddles the threshold");
        }

        // --- Columns without flora -----------------------------------------------------------------------------------

        [Test]
        public void NoFlora_BelowWater(){
            Biome sea = _fixture.MakeBiome("Sea", .5f, 100f, 6f, _fixture.SandSo, FloraFixture.Rule(_fixture.TuftSo, 1f),
                FloraFixture.Rule(_fixture.Tree, .5f));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, sea);
            RegionSample r = Region(Origin, Size);
            Assert.IsTrue(r.ColumnHeight.All(h => h < WaterLevel - 1));
            Assert.IsTrue(r.FloraSpecies.All(s => s == 0));
        }

        [Test]
        public void NoFlora_OnColumnsClampedAtHmax(){
            Biome peak = _fixture.MakeBiome("Peak", .5f, 140f, 6f, _fixture.GrassSo, FloraFixture.Rule(_fixture.TuftSo, 1f));
            WorldGenSettings s = _fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None, peak);
            s.HeightMax = 130f;
            _cfg = _fixture.ToConfig(s);
            RegionSample r = Region(Origin, Size);
            Assert.IsTrue(r.Clamped.All(c => c == 1), "every column is clamped");
            Assert.IsTrue(r.FloraSpecies.All(sp => sp == 0));
        }

        [Test]
        public void NoFlora_OnNonSolidTopBlocks(){
            Biome glass = _fixture.MakeBiome("Glass", .5f, 150f, 6f, _fixture.GlassSo, FloraFixture.Rule(_fixture.TuftSo, 1f));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, glass);
            Assert.AreEqual(0, _cfg.Biomes[0].floraGround);
            Assert.IsTrue(Region(Origin, Size).FloraSpecies.All(sp => sp == 0));
        }

        // --- Tree spacing --------------------------------------------------------------------------------------------

        [TestCase(3)]
        [TestCase(5)]
        [TestCase(0)]
        public void Anchors_AreTheLocalPriorityMaximaWithinTreeSpacing(int radius){
            // An anchor is a multi-block candidate that no candidate within r_tree outranks; so no two anchors
            // lie within r_tree, and every other candidate has an outranking candidate within r_tree.
            Biome wood = _fixture.MakeBiome("Wood", .5f, 150f, 8f, _fixture.GrassSo, FloraFixture.Rule(_fixture.Tree, .25f),
                FloraFixture.Rule(_fixture.CactusSo, .1f, 2, 2), FloraFixture.Rule(_fixture.TuftSo, .3f));
            WorldGenSettings s = _fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None, wood);
            s.TreeSpacingRadius = radius;
            _cfg = _fixture.ToConfig(s);
            Assert.AreEqual(radius, _cfg.TreeSpacingRadius);

            int pad = radius;
            RegionSample r = Region(Origin - pad, Size + 2 * pad);
            bool Candidate(int x, int z){
                WorldSampler.SampleColumn(x, z, _cfg, out ColumnSample c);
                return FloraPlacement.IsMultiBlock(FloraPlacement.RollRule(x, z, _cfg, default, c, out int _), _cfg);
            }

            bool[,] candidates = new bool[r.Size.x, r.Size.y];
            for (int i = 0; i < r.Size.x; i++)
            for (int j = 0; j < r.Size.y; j++)
                candidates[i, j] = Candidate(r.Origin.x + i, r.Origin.y + j);

            int anchors = 0, suppressed = 0;
            for (int i = pad; i < r.Size.x - pad; i++)
            for (int j = pad; j < r.Size.y - pad; j++){
                int x = r.Origin.x + i, z = r.Origin.y + j;
                ushort species = r.FloraSpecies[r.Index(x, z)];
                bool multi = species == 1 || species == 2; // Tree and the 2-high cactus come first
                if (!candidates[i, j]){
                    Assert.IsFalse(multi, $"({x},{z}) is no candidate");
                    continue;
                }

                bool outranked = false;
                uint own = FloraPlacement.Priority(x, z, _cfg);
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++){
                    if (dx == 0 && dz == 0 || dx * dx + dz * dz > radius * radius) continue;
                    if (!candidates[i + dx, j + dz]) continue;
                    int2 other = new(x + dx, z + dz);
                    if (FloraPlacement.Outranks(other, FloraPlacement.Priority(other.x, other.y, _cfg), new int2(x, z), own))
                        outranked = true;
                }

                Assert.AreEqual(!outranked, multi, $"({x},{z}) anchor");
                if (multi) anchors++;
                else suppressed++;
            }

            Assert.Greater(anchors, 50);
            if (radius > 0) Assert.Greater(suppressed, 0, "spacing suppresses candidates");
            else Assert.AreEqual(0, suppressed, "r_tree 0: every candidate is an anchor");

            // No two anchors within r_tree.
            List<int2> list = new();
            for (int i = 0; i < r.FloraSpecies.Length; i++)
                if (r.FloraSpecies[i] is 1 or 2)
                    list.Add(new int2(r.Origin.x + i % r.Size.x, r.Origin.y + i / r.Size.x));
            HashSet<int2> set = new(list);
            foreach (int2 a in list)
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                    if (FloraPlacement.InSpacingDisc(dx, dz, radius) && set.Contains(a + new int2(dx, dz)))
                        Assert.Fail($"anchors {a} and {a + new int2(dx, dz)} lie within r_tree");
        }

        [Test]
        public void Outranks_IsAStrictTotalOrder(){
            int2 a = new(3, 4), b = new(3, 5), c = new(2, 9);
            Assert.IsTrue(FloraPlacement.Outranks(a, 10, b, 5));
            Assert.IsFalse(FloraPlacement.Outranks(b, 5, a, 10));
            // Equal priorities: the lower (x, z) wins, exactly one of the pair.
            Assert.IsTrue(FloraPlacement.Outranks(a, 7, b, 7));
            Assert.IsFalse(FloraPlacement.Outranks(b, 7, a, 7));
            Assert.IsTrue(FloraPlacement.Outranks(c, 7, a, 7));
            Assert.IsFalse(FloraPlacement.Outranks(a, 7, a, 7));
        }

        [Test]
        public void StackHeight_CoversTheRuleRange(){
            Biome desert = _fixture.MakeBiome("Desert", .5f, 150f, 4f, _fixture.SandSo, FloraFixture.Rule(_fixture.CactusSo, .5f, 1, 3));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, desert);
            FloraRuleDef d = _cfg.FloraRules[0];
            Assert.AreEqual(1, d.MinStackHeight);
            Assert.AreEqual(3, d.MaxStackHeight);
            Assert.AreEqual(1, d.MultiBlock);
            int[] seen = new int[4];
            for (int x = 0; x < 300; x++){
                int h = FloraPlacement.StackHeight(x, -x * 7, _cfg, d);
                Assert.That(h, Is.InRange(1, 3));
                seen[h]++;
            }

            Assert.IsTrue(seen.Skip(1).All(c => c > 60), string.Join(",", seen));
        }

        [Test]
        public void SingleVoxelRule_WithoutStack_IsNotMultiBlock(){
            Assert.IsFalse(FloraFixture.Rule(_fixture.TuftSo, .1f).IsMultiBlock);
            Assert.IsTrue(FloraFixture.Rule(_fixture.CactusSo, .1f, 1, 3).IsMultiBlock);
            Assert.IsTrue(FloraFixture.Rule(_fixture.Tree, .1f).IsMultiBlock);
        }

        // --- Structures and config tables --------------------------------------------------------------------------

        private static HashSet<int3> Expand(VoxelPlacement p){
            List<int3> list = FloraConfigBuilder.Expand(p).ToList();
            HashSet<int3> set = new(list);
            Assert.AreEqual(list.Count, set.Count, "no duplicate offsets");
            return set;
        }

        [Test]
        public void Expand_Single_IsTheOrigin(){
            CollectionAssert.AreEquivalent(new[]{ new int3(1, 2, 3) },
                Expand(new VoxelPlacement{ shape = PlacementShape.Single, origin = new Vector3Int(1, 2, 3) }));
        }

        [Test]
        public void Expand_Line_IncludesBothEnds(){
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 5).Select(y => new int3(0, y, 0)),
                Expand(FloraFixture.Line(0, 0, 0, 0, 4, 0)));
            HashSet<int3> diagonal = Expand(FloraFixture.Line(0, 0, 0, 4, 2, 0));
            Assert.AreEqual(5, diagonal.Count);
            Assert.IsTrue(diagonal.Contains(new int3(0, 0, 0)) && diagonal.Contains(new int3(4, 2, 0)));
            Assert.IsTrue(diagonal.Contains(new int3(2, 1, 0)));
        }

        [Test]
        public void Expand_Box_IsCentredOnTheOrigin(){
            VoxelPlacement box = new(){ shape = PlacementShape.Box, origin = new Vector3Int(0, 5, 0), size = new Vector3Int(3, 3, 3), filled = true };
            HashSet<int3> filled = Expand(box);
            Assert.AreEqual(27, filled.Count);
            Assert.IsTrue(filled.All(p => math.all(p >= new int3(-1, 4, -1)) && math.all(p <= new int3(1, 6, 1))));
            box.filled = false;
            HashSet<int3> shell = Expand(box);
            Assert.AreEqual(26, shell.Count);
            Assert.IsFalse(shell.Contains(new int3(0, 5, 0)));
            box.size = new Vector3Int(5, 3, 5);
            box.filled = true;
            HashSet<int3> wide = Expand(box);
            Assert.AreEqual(75, wide.Count);
            Assert.IsTrue(wide.Contains(new int3(-2, 4, -2)) && wide.Contains(new int3(2, 6, 2)));
        }

        [Test]
        public void Expand_CircleAndCylinder(){
            VoxelPlacement disc = new(){ shape = PlacementShape.Circle, origin = new Vector3Int(0, 3, 0), radius = 2, filled = true };
            HashSet<int3> d = Expand(disc);
            Assert.AreEqual(13, d.Count); // dx² + dz² ≤ 4
            Assert.IsTrue(d.All(p => p.y == 3));
            disc.filled = false;
            HashSet<int3> ring = Expand(disc);
            Assert.AreEqual(8, ring.Count); // 1 < dx² + dz² ≤ 4
            Assert.IsFalse(ring.Contains(new int3(0, 3, 0)));
            VoxelPlacement cylinder = new(){ shape = PlacementShape.Cylinder, origin = new Vector3Int(0, 1, 0), radius = 1, height = 3, filled = true };
            HashSet<int3> c = Expand(cylinder);
            Assert.AreEqual(15, c.Count);
            CollectionAssert.AreEquivalent(new[]{ 1, 2, 3 }, c.Select(p => p.y).Distinct());
        }

        [Test]
        public void Config_FloraTables(){
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, _fixture.StructureBiomes());
            // Species ids are 1-based in first-use order over the biomes: Tuft, Flower, Tree, Cactus, Pine.
            Assert.AreEqual(6, _cfg.FloraRules.Length, "6 rules, 5 distinct species");
            CollectionAssert.AreEqual(new ushort[]{ 1, 2, 3, 4, 1, 5 }, _cfg.FloraRules.Select(r => r.Species).ToArray());
            Assert.AreEqual(0, _cfg.Biomes[0].floraRuleStart);
            Assert.AreEqual(3, _cfg.Biomes[0].floraRuleCount);
            Assert.AreEqual(3, _cfg.Biomes[1].floraRuleStart);
            Assert.AreEqual(3, _cfg.Biomes[1].floraRuleCount);
            Assert.AreEqual(.3f, _cfg.FloraRules[0].CumulativeEnd, 1e-6f);
            Assert.AreEqual(.35f, _cfg.FloraRules[1].CumulativeEnd, 1e-6f);
            Assert.AreEqual(.41f, _cfg.FloraRules[2].CumulativeEnd, 1e-6f);

            Assert.AreEqual(2, _cfg.FloraStructures.Length);
            Assert.AreEqual(2, _cfg.FloraReach, "5×5 box and r 2 circles");
            Assert.AreEqual(3, _cfg.TreeSpacingRadius);
            Assert.AreEqual(5, _cfg.FloraPadding, "reach + r_tree");
            Assert.AreEqual(3, _cfg.FloraMultiBlockIntervals.Length, "tree, cactus stack, pine");

            Assert.AreEqual(FloraFixture.Tuft, _cfg.FloraRules[0].Voxel);
            Assert.AreEqual(-1, _cfg.FloraRules[0].Structure);
            Assert.AreEqual(0, _cfg.FloraRules[2].Structure);
            Assert.AreEqual(1, _cfg.FloraRules[5].Structure);
            Assert.AreEqual(FloraFixture.Cactus, _cfg.FloraRules[3].Voxel);
            Assert.AreEqual(FloraVoxelClass.Solid, _cfg.FloraRules[3].VoxelClass);

            foreach (ushort id in new[]{ TestFixtures.Air, TestFixtures.Grass, TestFixtures.Water, FloraFixture.Tuft, FloraFixture.Flower,
                         FloraFixture.Log, FloraFixture.Leaves, FloraFixture.Needles, FloraFixture.Cactus, (ushort)200 })
                Assert.AreEqual(_fixture.ClassOf(id), _cfg.VoxelClassOf(id), $"class of {id}");

            // The tree: 5 log + 75 leaf + 1 top voxels, the log wins over the leaves at y = 4 when stamped.
            FloraStructureDef tree = _cfg.FloraStructures[0];
            Assert.AreEqual(81, tree.Count);
            Assert.AreEqual(2, tree.Reach);
        }

        [Test]
        public void Config_TreeTables(){
            // The default biomes: the Grove species (5 variants) in the meadow, the Pine structure in the dunes.
            Use(GenArchitecture.BiomeFirst, BlendMethod.None);
            Assert.AreEqual(1, _cfg.FloraStructures.Length, "pine");
            Assert.AreEqual(1, _cfg.TreeSpecies.Length);
            Assert.AreEqual(5, _cfg.TreeVariants.Length);
            Assert.AreEqual(0, _cfg.TreeSpecies[0].VariantStart);
            Assert.AreEqual(5, _cfg.TreeSpecies[0].VariantCount);
            Assert.AreEqual(0, _cfg.FloraRules[2].Tree);
            Assert.AreEqual(-1, _cfg.FloraRules[2].Structure);
            Assert.AreEqual(1, _cfg.FloraRules[2].MultiBlock);
            Assert.AreEqual(-1, _cfg.FloraRules[5].Tree);
            Assert.AreEqual(0, _cfg.FloraRules[5].Structure);
            CollectionAssert.AreEqual(new[]{ .2f, .4f, .6f, .8f, 1f }, Enumerable.Range(0, 5).Select(i => _cfg.TreeVariants[i].CumulativeEnd).ToArray());

            // Reach: Normal 0 + r 3 + bump; Wide lean 8 · 0.2 = 1 and width 2, + r 4 + bump; Spire r 3 + bump;
            // Tiered r 4 + bump; Snag lean 5 · 0.25 = 1, + branch 3 + cluster 2 + bump.
            CollectionAssert.AreEqual(new[]{ 4, 7, 4, 5, 7 }, Enumerable.Range(0, 5).Select(i => _cfg.TreeVariants[i].Reach).ToArray());
            CollectionAssert.AreEqual(new[]{ 3, 5, 2, 4, 3 }, Enumerable.Range(0, 5).Select(i => _cfg.TreeVariants[i].SpacingRadius).ToArray());
            Assert.AreEqual(7, _cfg.FloraReach, "largest variant reach");
            Assert.AreEqual(5, _cfg.FloraSpacingMax, "largest variant radius");
            Assert.AreEqual(12, _cfg.FloraPadding, "reach + largest spacing radius");

            TreeVariantDef wide = _cfg.TreeVariants[1];
            Assert.AreEqual(FloraFixture.Log, wide.TrunkVoxel);
            Assert.AreEqual(FloraVoxelClass.Solid, wide.TrunkClass);
            Assert.AreEqual(FloraFixture.Needles, wide.AltLeafVoxel);
            Assert.AreEqual(FloraVoxelClass.Leaves, wide.AltLeafClass);
            Assert.AreEqual(200, wide.LeanPerMille);
            Assert.AreEqual(new int2(4, 5), wide.CrownHeight);
            Assert.AreEqual(TestFixtures.Air, _cfg.TreeVariants[0].AltLeafVoxel, "no alternative leaf");
            Assert.AreEqual(FloraVoxelClass.Air, _cfg.TreeVariants[0].AltLeafClass);
        }

        [Test]
        public void Config_TreeVariantsAreSanitised(){
            // Swapped and negative ranges, too many branches and zero weights are clamped.
            TreeVariant odd = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, Weight = 0f, TrunkHeight = new IntRange(7, 3),
                TrunkWidth = 5, MaxLean = 3f, CrownRadius = new IntRange(-2, 1), BranchCount = new IntRange(2, 40),
                BranchLength = new IntRange(0, 0), BranchBandMin = .9f, BranchBandMax = .1f, HoleChance = 2f, SpacingRadius = -1
            };
            TreeVariant zero = new(){ TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, Weight = 0f };
            Biome b = _fixture.MakeBiome("B", .5f, 150f, 2f, _fixture.GrassSo, FloraFixture.Rule(_fixture.MakeTree("Odd", odd, zero), .1f));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, b);
            TreeVariantDef d = _cfg.TreeVariants[0];
            Assert.AreEqual(.5f, d.CumulativeEnd, "all weights 0: equal");
            Assert.AreEqual(1f, _cfg.TreeVariants[1].CumulativeEnd);
            Assert.AreEqual(new int2(3, 7), d.TrunkHeight);
            Assert.AreEqual(2, d.TrunkWidth);
            Assert.AreEqual(250, d.LeanPerMille);
            Assert.AreEqual(new int2(0, 1), d.CrownRadius);
            Assert.AreEqual(new int2(2, TreeShape.MaxBranches), d.BranchCount);
            Assert.AreEqual(new int2(1, 1), d.BranchLength);
            Assert.AreEqual(new int2(100, 900), d.BranchBand);
            Assert.AreEqual(1f, d.HoleChance);
            Assert.AreEqual(0, d.SpacingRadius);
        }

        [Test]
        public void Config_TreeVariantsWithWeightZeroAtTheEnd_AreNeverPicked(){
            TreeVariant a = new(){ TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, Weight = 1f };
            TreeVariant b = new(){ TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, Weight = 2f };
            TreeVariant zero = new(){ TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, Weight = 0f };
            Biome biome = _fixture.MakeBiome("B", .5f, 150f, 2f, _fixture.GrassSo, FloraFixture.Rule(_fixture.MakeTree("Tail", a, b, zero, zero), .1f));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, biome);
            Assert.AreEqual(1f / 3f, _cfg.TreeVariants[0].CumulativeEnd, 1e-6f);
            Assert.AreEqual(1f, _cfg.TreeVariants[1].CumulativeEnd, "the last weighted variant closes the interval");
            for (int x = 0; x < 256; x++)
            for (int z = 0; z < 256; z++)
                Assert.Less(TreeShape.PickVariant(x, z, _cfg, 0), 2);
        }

        [Test]
        public void Config_TreeWithoutVariants_IsSkipped(){
            Biome b = _fixture.MakeBiome("B", .5f, 150f, 2f, _fixture.GrassSo, FloraFixture.Rule(_fixture.MakeTree("Empty"), .1f),
                FloraFixture.Rule(_fixture.TuftSo, .2f));
            LogAssert.Expect(LogType.Warning, new Regex("exactly one of"));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, b);
            Assert.AreEqual(1, _cfg.FloraRules.Length);
            Assert.AreEqual(0, _cfg.TreeSpecies.Length);
            Assert.AreEqual(0, _cfg.FloraPadding);
        }

        [Test]
        public void Config_WithoutMultiBlockFlora_HasNoPadding(){
            Biome flat = _fixture.MakeBiome("Flat", .5f, 150f, 2f, _fixture.GrassSo, FloraFixture.Rule(_fixture.TuftSo, .3f));
            Use(GenArchitecture.BiomeFirst, BlendMethod.None, flat);
            Assert.IsTrue(_cfg.HasFlora);
            Assert.AreEqual(0, _cfg.FloraPadding);
            Assert.AreEqual(0, _cfg.FloraMultiBlockIntervals.Length);
        }

        [Test]
        public void Config_InvalidRulesAreSkippedAndOverfullSumsReported(){
            Biome odd = _fixture.MakeBiome("Odd", .5f, 150f, 2f, _fixture.GrassSo,
                new FloraRule{ Probability = .5f }, FloraFixture.Rule(_fixture.TuftSo, .8f), FloraFixture.Rule(_fixture.FlowerSo, .4f));
            WorldGenSettings s = _fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None, odd);
            LogAssert.Expect(LogType.Warning, new Regex("Odd: flora rule 0 needs exactly one"));
            LogAssert.Expect(LogType.Warning, new Regex("Odd: flora probabilities sum to 1.2"));
            _cfg = _fixture.ToConfig(s);
            Assert.AreEqual(2, _cfg.FloraRules.Length);
            Assert.AreEqual(.8f, _cfg.FloraRules[1].CumulativeStart, 1e-6f);
        }

        [Test]
        public void Config_NegativeTreeSpacing_Throws(){
            WorldGenSettings s = _fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None);
            s.TreeSpacingRadius = -1;
            Assert.Throws<System.InvalidOperationException>(() => _cfg = _fixture.ToConfig(s));
        }

        [Test]
        public void FloraSpeciesNames_StartWithNone(){
            WorldGenSettings s = _fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None);
            CollectionAssert.AreEqual(new[]{ "none", "Tuft", "Flower", "Grove", "Cactus", "Pine" }, s.FloraSpeciesNames());
        }

        [Test]
        public void SampleColumn_EqualsTheRegionApi(){
            // The region API reports the sampler's flora (species and source) per column.
            Use(GenArchitecture.TerrainFirst, BlendMethod.DistanceBlend);
            RegionSample r = Region(Origin, new int2(64, 64));
            for (int i = 0; i < r.FloraSpecies.Length; i += 3){
                int x = r.Origin.x + i % r.Size.x, z = r.Origin.y + i / r.Size.x;
                ColumnSample s = WorldSampler.SampleColumn(x, z, _cfg);
                Assert.AreEqual(s.FloraSpecies, r.FloraSpecies[i], $"({x},{z})");
                Assert.AreEqual(s.FloraSource, r.FloraSource[i], $"({x},{z})");
            }
        }
    }
}
