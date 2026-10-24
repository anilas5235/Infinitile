using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Parametric trees: shapes per crown shape and branch style over 200 seeds, the variant pick, and the
    ///     anchor spacing with mixed variant radii.
    /// </summary>
    public class TreeShapeTests{
        private const int Seeds = 200;
        private static readonly int3 Up = new(0, 1, 0);

        private static readonly int3[] Faces ={
            new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)
        };

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

        /// <summary>The sanitised config value of one variant.</summary>
        private TreeVariantDef Def(TreeVariant variant){
            List<TreeVariantDef> variants = new();
            FloraConfigBuilder.AddTree(_fixture.MakeTree("T", variant), v => _fixture.Ids[v], variants, new Dictionary<ushort, byte>(), null);
            return variants[0];
        }

        /// <summary>The voxels of a tree by offset after stamping: a voxel replaces one of a lower class only.</summary>
        private Dictionary<int3, ushort> Stamp(int3 anchorBase, in TreeRoll roll, in TreeVariantDef v, uint seed){
            NativeList<FloraVoxel> list = new(256, Allocator.Temp);
            try{
                TreeShape.Build(anchorBase, roll, v, seed, ref list);
                Dictionary<int3, ushort> voxels = new();
                foreach (FloraVoxel fv in list){
                    Assert.AreEqual(_fixture.ClassOf(fv.Voxel), fv.Class, $"class of {fv.Voxel}");
                    if (!voxels.TryGetValue(fv.Offset, out ushort old) || _fixture.ClassOf(old) < fv.Class) voxels[fv.Offset] = fv.Voxel;
                }

                return voxels;
            }
            finally{
                list.Dispose();
            }
        }

        private static bool FaceConnected(HashSet<int3> set, int3 start){
            if (!set.Contains(start)) return false;
            HashSet<int3> seen = new(){ start };
            Queue<int3> open = new();
            open.Enqueue(start);
            while (open.Count > 0){
                int3 p = open.Dequeue();
                foreach (int3 f in Faces)
                    if (set.Contains(p + f) && seen.Add(p + f))
                        open.Enqueue(p + f);
            }

            return seen.Count == set.Count;
        }

        [Test]
        public void Shapes_TrunkReachesCrown_BranchesStartOnTrunk_WithinReach_CountsInBounds(
            [Values(CrownShape.Round, CrownShape.Ellipsoid, CrownShape.Cone, CrownShape.Tiered, CrownShape.None)]
            CrownShape shape,
            [Values(BranchStyle.Diagonal, BranchStyle.Arm)] BranchStyle style,
            [Values(1, 2)] int width){
            bool tapered = shape is CrownShape.Cone or CrownShape.Tiered;
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, AltLeafVoxel = _fixture.NeedlesSo, AltLeafChance = .3f,
                TrunkHeight = new IntRange(4, 8), TrunkWidth = width, MaxLean = .25f, CrownShape = shape,
                CrownRadius = new IntRange(2, 4), CrownTopRadius = new IntRange(0, 1),
                CrownHeight = shape == CrownShape.Round ? new IntRange(0, 0) : new IntRange(3, 6),
                CrownMinBase = tapered ? 2 : 0, BranchCount = new IntRange(1, 4), BranchStyle = style,
                BranchLength = new IntRange(1, 3), BranchLeafRadius = new IntRange(1, 2)
            };
            TreeVariantDef v = Def(variant);
            variant.HoleChance = variant.BumpChance = variant.HangChance = 0f;
            TreeVariantDef quiet = Def(variant);

            long leaves = 0, quietLeaves = 0;
            int differs = 0;
            for (int s = 0; s < Seeds; s++){
                uint seed = (uint)(1000 + s * 7919);
                int x = s * 37 - 3000, z = s * 53 + 211;
                int3 anchorBase = new(x, 100, z);
                TreeRoll r = TreeShape.Roll(x, z, seed, 0, v);
                Dictionary<int3, ushort> voxels = Stamp(anchorBase, r, v, seed);
                string at = $"seed {seed} ({x},{z}) trunk {r.TrunkHeight} lean {r.Lean} r {r.CrownRadius} branches {r.BranchCount}";

                HashSet<int3> logs = new(voxels.Where(kv => kv.Value == FloraFixture.Log).Select(kv => kv.Key));
                HashSet<int3> leafSet = new(voxels.Where(kv => kv.Value != FloraFixture.Log).Select(kv => kv.Key));
                for (int i = 0; i < width; i++)
                for (int j = 0; j < width; j++)
                    Assert.IsTrue(logs.Contains(new int3(i, 0, j)), $"{at}: the trunk starts at the anchor's top block + 1");
                // One face-connected log set: the trunk is continuous and every branch starts on it.
                Assert.IsTrue(FaceConnected(logs, int3.zero), $"{at}: logs are not face-connected");
                if (shape != CrownShape.None)
                    Assert.IsTrue(logs.Any(l => leafSet.Contains(l + Up)), $"{at}: the trunk does not reach its crown");

                foreach (int3 p in voxels.Keys){
                    Assert.LessOrEqual(math.max(math.abs(p.x), math.abs(p.z)), v.Reach, $"{at}: voxel {p} beyond reach {v.Reach}");
                    Assert.GreaterOrEqual(p.y, -1, $"{at}: voxel {p} below the hang layer");
                }

                // Logs: the rolled trunk at least; at most the trunk extended to the crown, its lean steps and the branches.
                int minLogs = r.TrunkHeight * width * width;
                int maxLogs = (r.TrunkHeight + v.CrownTop + v.CrownMinBase + 2 * r.Lean) * width * width +
                              r.BranchCount * (style == BranchStyle.Arm ? 1 + v.BranchLength.y : 3 * v.BranchLength.y);
                Assert.That(logs.Count, Is.InRange(minLogs, maxLogs), $"{at}: log count");

                // Leaves against the same tree without crown noise: each voxel adds at most one extra.
                Dictionary<int3, ushort> plain = Stamp(anchorBase, r, quiet, seed);
                int plainLeaves = plain.Count(kv => kv.Value != FloraFixture.Log);
                Assert.LessOrEqual(leafSet.Count, 2 * plainLeaves, $"{at}: leaf count");
                CollectionAssert.AreEquivalent(logs, plain.Where(kv => kv.Value == FloraFixture.Log).Select(kv => kv.Key),
                    $"{at}: crown noise moved a log");
                leaves += leafSet.Count;
                quietLeaves += plainLeaves;

                // The same parameters at another anchor: crown noise gives another voxel set.
                Dictionary<int3, ushort> moved = Stamp(anchorBase + new int3(1000, 0, -1000), r, v, seed);
                if (!new HashSet<int3>(moved.Keys).SetEquals(voxels.Keys)) differs++;
            }

            // Holes drop at most p_hole of the voxels, bumps and hangs add at most p_bump + p_hang.
            double ratio = (double)leaves / quietLeaves;
            TestContext.WriteLine($"{shape} {style} w{width}: leaves {leaves} / noiseless {quietLeaves} = {ratio:F3}, reach {v.Reach}");
            Assert.That(ratio, Is.InRange(1.0 - v.HoleChance, 1.0 + v.BumpChance + v.HangChance));
            Assert.GreaterOrEqual(differs, Seeds * 95 / 100, "crown noise differs between anchors");
        }

        [Test]
        public void Shapes_NoCrownNoBranches_IsABareTrunk(){
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, TrunkHeight = new IntRange(3, 3), CrownShape = CrownShape.None
            };
            TreeVariantDef v = Def(variant);
            Assert.AreEqual(0, v.Reach);
            TreeRoll r = TreeShape.Roll(5, 7, 1u, 0, v);
            Dictionary<int3, ushort> voxels = Stamp(new int3(5, 80, 7), r, v, 1u);
            CollectionAssert.AreEquivalent(new[]{ new int3(0, 0, 0), new int3(0, 1, 0), new int3(0, 2, 0) }, voxels.Keys);
        }

        [Test]
        public void Roll_IsAPureFunctionOfTheAnchor_WithinTheRanges(){
            TreeVariantDef v = Def(_fixture.ShapeVariants()[1]);
            for (int i = 0; i < 100; i++){
                TreeRoll a = TreeShape.Roll(i * 13, -i * 7, 99u, 0, v), b = TreeShape.Roll(i * 13, -i * 7, 99u, 0, v);
                Assert.AreEqual(a.TrunkHeight, b.TrunkHeight);
                Assert.AreEqual(a.Lean, b.Lean);
                Assert.AreEqual(a.LeanDirection, b.LeanDirection);
                Assert.AreEqual(a.CrownRadius, b.CrownRadius);
                Assert.That(a.TrunkHeight, Is.InRange(v.TrunkHeight.x, v.TrunkHeight.y));
                Assert.That(a.CrownRadius, Is.InRange(v.CrownRadius.x, v.CrownRadius.y));
                Assert.LessOrEqual(a.Lean, a.TrunkHeight * v.LeanPerMille / 1000);
                Assert.AreEqual(1, math.max(math.abs(a.LeanDirection.x), math.abs(a.LeanDirection.y)));
            }
        }

        // --- Variant pick and spacing -------------------------------------------------------------------------------

        [Test]
        public void PickVariant_FrequenciesMatchTheWeights(){
            TreeVariant[] variants = _fixture.ShapeVariants();
            float[] weights ={ 4f, 1f, 2f, 0f, 3f };
            for (int i = 0; i < variants.Length; i++) variants[i].Weight = weights[i];
            TreeSpecies weighted = _fixture.MakeTree("Weighted", variants);
            Biome b = _fixture.MakeBiome("B", .5f, 150f, 8f, _fixture.GrassSo, FloraFixture.Rule(weighted, .1f));
            _cfg = _fixture.ToConfig(_fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None, b));
            Assert.AreEqual(1, _cfg.TreeSpecies.Length);
            Assert.AreEqual(5, _cfg.TreeVariants.Length);
            Assert.AreEqual(1f, _cfg.TreeVariants[4].CumulativeEnd);

            int[] counts = new int[5];
            const int n = 200000;
            for (int i = 0; i < n; i++) counts[TreeShape.PickVariant(i % 500 - 250, i / 500, _cfg, 0)]++;
            for (int i = 0; i < 5; i++) Assert.AreEqual(weights[i] / 10f, counts[i] / (float)n, .006f, $"variant {i}");
        }

        [Test]
        public void Spacing_MixedVariantRadii_AnchorsNeverConflict_AndSuppressedCandidatesAreOutranked(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            // Candidate a suppresses b when d ≤ max(r_a, r_b) and a outranks b; so no two anchors conflict, and
            // every candidate that is not an anchor has a conflicting candidate that outranks it.
            Biome grove = _fixture.MakeBiome("Grove", .5f, 150f, 8f, _fixture.GrassSo, FloraFixture.Rule(_fixture.Grove, .3f),
                FloraFixture.Rule(_fixture.TuftSo, .3f));
            _cfg = _fixture.ToConfig(_fixture.Settings(architecture, BlendMethod.None, grove));
            int r = _cfg.FloraSpacingMax;
            Assert.AreEqual(5, r);
            int2 origin = new(-200, 340), size = new(144, 144);
            RegionSample region = RegionSample.Sample(_cfg, origin - r, size + 2 * r);

            int Radius(int x, int z) => _cfg.TreeVariants[TreeShape.PickVariant(x, z, _cfg, 0)].SpacingRadius;
            bool[,] candidates = new bool[region.Size.x, region.Size.y];
            for (int i = 0; i < region.Size.x; i++)
            for (int j = 0; j < region.Size.y; j++){
                int x = region.Origin.x + i, z = region.Origin.y + j;
                WorldSampler.SampleColumn(x, z, _cfg, out ColumnSample c);
                candidates[i, j] = FloraPlacement.IsMultiBlock(FloraPlacement.RollRule(x, z, _cfg, c, out int _), _cfg);
            }

            HashSet<int2> anchors = new();
            for (int i = 0; i < region.Size.x; i++)
            for (int j = 0; j < region.Size.y; j++)
                if (region.FloraSpecies[region.Index(region.Origin.x + i, region.Origin.y + j)] == 1)
                    anchors.Add(region.Origin + new int2(i, j));

            int checkedAnchors = 0, suppressed = 0;
            HashSet<int> radii = new();
            for (int i = r; i < region.Size.x - r; i++)
            for (int j = r; j < region.Size.y - r; j++){
                int x = region.Origin.x + i, z = region.Origin.y + j;
                bool anchor = anchors.Contains(new int2(x, z));
                if (!candidates[i, j]){
                    Assert.IsFalse(anchor, $"({x},{z}) is no candidate");
                    continue;
                }

                int own = Radius(x, z);
                uint priority = FloraPlacement.Priority(x, z, _cfg);
                bool outranked = false;
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++){
                    if (dx == 0 && dz == 0 || !candidates[i + dx, j + dz]) continue;
                    int2 other = new(x + dx, z + dz);
                    if (!FloraPlacement.Conflict(dx * dx + dz * dz, own, Radius(other.x, other.y))) continue;
                    Assert.IsFalse(anchor && anchors.Contains(other),
                        $"anchors ({x},{z}) r {own} and {other} r {Radius(other.x, other.y)} conflict");
                    if (FloraPlacement.Outranks(other, FloraPlacement.Priority(other.x, other.y, _cfg), new int2(x, z), priority))
                        outranked = true;
                }

                Assert.AreEqual(!outranked, anchor, $"({x},{z}) anchor");
                if (anchor){
                    checkedAnchors++;
                    radii.Add(own);
                }
                else suppressed++;
            }

            TestContext.WriteLine($"{architecture}: anchors {checkedAnchors}, suppressed {suppressed}, radii {string.Join(",", radii.OrderBy(q => q))}");
            Assert.Greater(checkedAnchors, 100);
            Assert.Greater(suppressed, 100);
            Assert.AreEqual(4, radii.Count, "radii 2…5 all occur");
        }
    }
}
