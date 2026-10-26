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
    ///     Parametric trees: shapes per crown shape and branch style over 200 seeds, every shipped variant, the
    ///     variant pick, and the anchor spacing with mixed variant radii.
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
            FloraConfigBuilder.AddTree(_fixture.MakeTree("T", variant), v => _fixture.Ids[v], FloraFixture.VariantOf, variants, new Dictionary<ushort, byte>(), null);
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

        private static bool IsLog(ushort id){
            return FloraFixture.BaseOf(id) == FloraFixture.Log;
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

        [Category(TestTiers.Slow)]
        [Explicit(TestTiers.SlowReason)]
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

                HashSet<int3> logs = new(voxels.Where(kv => IsLog(kv.Value)).Select(kv => kv.Key));
                HashSet<int3> leafSet = new(voxels.Where(kv => !IsLog(kv.Value)).Select(kv => kv.Key));
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
                int maxLogs = (r.TrunkHeight + math.max(v.CrownBase, 0) + v.CrownMinBase + 2 * r.Lean) * width * width +
                              r.BranchCount * (style == BranchStyle.Arm ? 1 + v.BranchLength.y : 3 * v.BranchLength.y);
                Assert.That(logs.Count, Is.InRange(minLogs, maxLogs), $"{at}: log count");

                // Leaves against the same tree without crown noise: each voxel adds at most one extra.
                Dictionary<int3, ushort> plain = Stamp(anchorBase, r, quiet, seed);
                int plainLeaves = plain.Count(kv => !IsLog(kv.Value));
                Assert.LessOrEqual(leafSet.Count, 2 * plainLeaves, $"{at}: leaf count");
                CollectionAssert.AreEquivalent(logs, plain.Where(kv => IsLog(kv.Value)).Select(kv => kv.Key),
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
        public void Shapes_Sink_MovesTheWholeShapeDown(){
            // A boulder with crown noise: sunk by 2 it is the shape built 2 lower, its noise hashed there.
            TreeVariant variant = new(){
                LeafVoxel = _fixture.GraniteSo, TrunkHeight = new IntRange(0, 0), CrownShape = CrownShape.Round,
                CrownRadius = new IntRange(2, 2), CrownBase = 1, HoleChance = .3f, BumpChance = .3f, Sink = new IntRange(2, 2)
            };
            TreeVariantDef v = Def(variant);
            Assert.AreEqual(new int2(2, 2), v.Sink);
            for (int s = 0; s < 20; s++){
                int x = s * 31 - 40, z = s * 17 + 3;
                TreeRoll sunk = TreeShape.Roll(x, z, 7u, 0, v);
                Assert.AreEqual(2, sunk.Sink);
                TreeRoll level = sunk;
                level.Sink = 0;
                Dictionary<int3, ushort> expected = Stamp(new int3(x, 98, z), level, v, 7u)
                    .ToDictionary(kv => kv.Key - new int3(0, 2, 0), kv => kv.Value);
                CollectionAssert.AreEquivalent(expected, Stamp(new int3(x, 100, z), sunk, v, 7u), $"anchor ({x}, {z})");
            }
        }

        [Test]
        public void Shapes_LyingTrunk_IsALineAlongXOrZ_TwoLongWhenNarrowed(){
            // Length 3–6 along one of the 4 axis directions from the anchor at y 0; crown and branches are ignored.
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, TrunkHeight = new IntRange(3, 6), Lying = true,
                CrownShape = CrownShape.Round, CrownRadius = new IntRange(2, 2), BranchCount = new IntRange(2, 2), MaxLean = .25f
            };
            TreeVariantDef v = Def(variant);
            Assert.AreEqual(5, v.Reach);
            HashSet<int2> directions = new();
            HashSet<int> lengths = new();
            for (int s = 0; s < 200; s++){
                int x = s * 37 - 3000, z = s * 53 + 211;
                TreeRoll r = TreeShape.Roll(x, z, 3u, 0, v);
                Assert.IsTrue(r.Lying && r.NeedsFlatFootprint);
                Assert.AreEqual(1, math.abs(r.LeanDirection.x) + math.abs(r.LeanDirection.y), "axis direction");
                int3[] line = Enumerable.Range(0, r.TrunkHeight).Select(i => new int3(r.LeanDirection.x * i, 0, r.LeanDirection.y * i)).ToArray();
                CollectionAssert.AreEquivalent(line, Stamp(new int3(x, 100, z), r, v, 3u).Keys, $"anchor ({x}, {z})");
                int4 box = TreeShape.Footprint(r);
                Assert.AreEqual(r.TrunkHeight, (box.z - box.x + 1) * (box.w - box.y + 1), "footprint is the line");
                directions.Add(r.LeanDirection);
                lengths.Add(r.TrunkHeight);
                r.Narrow();
                Assert.AreEqual(2, Stamp(new int3(x, 100, z), r, v, 3u).Count, "narrowed to 2");
            }

            Assert.AreEqual(4, directions.Count, "all 4 directions");
            CollectionAssert.AreEquivalent(new[]{ 3, 4, 5, 6 }, lengths, "all lengths");
        }

        [Test]
        public void Logs_LyingTrunk_LiesAlongItsDirection(){
            // A fallen log is the trunk voxel lying along x or z, whichever its direction is; both occur.
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, TrunkHeight = new IntRange(3, 6), Lying = true, CrownShape = CrownShape.None
            };
            TreeVariantDef v = Def(variant);
            Assert.AreEqual(FloraFixture.LogX, v.TrunkVoxelX);
            Assert.AreEqual(FloraFixture.LogZ, v.TrunkVoxelZ);
            HashSet<ushort> seen = new();
            for (int s = 0; s < 200; s++){
                int x = s * 37 - 3000, z = s * 53 + 211;
                TreeRoll r = TreeShape.Roll(x, z, 3u, 0, v);
                ushort expected = r.LeanDirection.x != 0 ? FloraFixture.LogX : FloraFixture.LogZ;
                foreach ((int3 p, ushort id) in Stamp(new int3(x, 100, z), r, v, 3u))
                    Assert.AreEqual(expected, id, $"anchor ({x}, {z}) direction {r.LeanDirection} voxel {p}");
                seen.Add(expected);
            }

            Assert.AreEqual(2, seen.Count, "logs along x and along z");
        }

        [Test]
        public void Logs_TrunkWithoutAxisMode_StaysTheBase(){
            // A trunk voxel without the axis mode (a cactus) keeps its own id for lying trunks and branches.
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.CactusSo, TrunkHeight = new IntRange(3, 6), Lying = true, CrownShape = CrownShape.None
            };
            TreeVariantDef lying = Def(variant);
            Assert.AreEqual(FloraFixture.Cactus, lying.TrunkVoxelX);
            Assert.AreEqual(FloraFixture.Cactus, lying.TrunkVoxelZ);
            variant.Lying = false;
            variant.BranchCount = new IntRange(2, 3);
            variant.BranchStyle = BranchStyle.Arm;
            TreeVariantDef armed = Def(variant);
            for (int s = 0; s < 50; s++){
                TreeRoll r = TreeShape.Roll(s * 11, s * 5, 9u, 0, armed);
                foreach (ushort id in Stamp(new int3(s * 11, 100, s * 5), r, armed, 9u).Values)
                    Assert.AreEqual(FloraFixture.Cactus, id);
            }

            // Without a variant table every log stands.
            List<TreeVariantDef> variants = new();
            FloraConfigBuilder.AddTree(_fixture.MakeTree("T", _fixture.Normal()), v => _fixture.Ids[v], null, variants,
                new Dictionary<ushort, byte>(), null);
            Assert.AreEqual(FloraFixture.Log, variants[0].TrunkVoxelX);
            Assert.AreEqual(FloraFixture.Log, variants[0].TrunkVoxelZ);
        }

        [Test]
        public void Logs_TrunkStands_BranchVoxelsFollowTheStepThatReachedThem(
            [Values(BranchStyle.Diagonal, BranchStyle.Arm)] BranchStyle style, [Values(1, 2)] int width){
            // Upright, leaning and wide trunks stand. A branch voxel reached by a step along x lies along x, along z
            // lies along z, and one reached by an upward step stands: each lying log has a log next to it along its
            // axis, and each standing branch log has a log right below it.
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, TrunkHeight = new IntRange(4, 8), TrunkWidth = width,
                MaxLean = .25f, CrownShape = CrownShape.Round, CrownRadius = new IntRange(2, 3), BranchCount = new IntRange(1, 4),
                BranchStyle = style, BranchLength = new IntRange(1, 3), BranchLeafRadius = new IntRange(0, 1)
            };
            TreeVariantDef v = Def(variant);
            int lyingX = 0, lyingZ = 0, standingBranch = 0;
            for (int s = 0; s < Seeds; s++){
                uint seed = (uint)(1000 + s * 7919);
                int x = s * 37 - 3000, z = s * 53 + 211;
                TreeRoll r = TreeShape.Roll(x, z, seed, 0, v);
                Dictionary<int3, ushort> voxels = Stamp(new int3(x, 100, z), r, v, seed);
                string at = $"seed {seed} ({x},{z}) lean {r.Lean} branches {r.BranchCount}";

                // The trunk alone, from the same roll without branches.
                TreeRoll bare = r;
                bare.BranchCount = 0;
                HashSet<int3> trunk = new(Stamp(new int3(x, 100, z), bare, v, seed).Where(kv => IsLog(kv.Value)).Select(kv => kv.Key));
                foreach (int3 p in trunk) Assert.AreEqual(FloraFixture.Log, voxels[p], $"{at}: trunk voxel {p} does not stand");

                bool LogAt(int3 q) => voxels.TryGetValue(q, out ushort n) && IsLog(n);
                foreach ((int3 p, ushort id) in voxels){
                    if (!IsLog(id) || trunk.Contains(p)) continue;
                    if (id == FloraFixture.LogX){
                        lyingX++;
                        Assert.IsTrue(LogAt(p + new int3(1, 0, 0)) || LogAt(p - new int3(1, 0, 0)), $"{at}: x log {p} reached along x");
                    }
                    else if (id == FloraFixture.LogZ){
                        lyingZ++;
                        Assert.IsTrue(LogAt(p + new int3(0, 0, 1)) || LogAt(p - new int3(0, 0, 1)), $"{at}: z log {p} reached along z");
                    }
                    else{
                        standingBranch++;
                        Assert.IsTrue(LogAt(p - Up), $"{at}: standing branch log {p} reached from below");
                    }
                }
            }

            TestContext.WriteLine($"{style} w{width}: branch logs along x {lyingX}, along z {lyingZ}, standing {standingBranch}");
            Assert.Greater(lyingX, 0, "branch logs along x");
            Assert.Greater(lyingZ, 0, "branch logs along z");
            Assert.Greater(standingBranch, 0, "standing branch logs");
        }

        [TestCase(0, 4, 8, 4, TestName = "Shapes_CrownBase_0_StartsLevelWithTheTrunkTop")]
        [TestCase(-2, 2, 6, 4, TestName = "Shapes_CrownBase_Negative_HangsDown")]
        [TestCase(-10, 1, 5, 4, TestName = "Shapes_CrownBase_FarBelow_KeepsOneLayerAboveTheTrunk")]
        [TestCase(3, 7, 11, 6, TestName = "Shapes_CrownBase_Above_ExtendsTheTrunk")]
        public void Shapes_CrownBase_PlacesTheLowestLayer(int crownBase, int bottom, int top, int trunkTop){
            // Trunk 5 (y 0…4) and a round crown of radius 2 (5 layers), without noise or branches.
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, TrunkHeight = new IntRange(5, 5), CrownShape = CrownShape.Round,
                CrownRadius = new IntRange(2, 2), CrownBase = crownBase, HoleChance = 0f, BumpChance = 0f, HangChance = 0f
            };
            TreeVariantDef v = Def(variant);
            TreeRoll r = TreeShape.Roll(5, 7, 1u, 0, v);
            Dictionary<int3, ushort> voxels = Stamp(new int3(5, 80, 7), r, v, 1u);
            int[] leafY = voxels.Where(kv => !IsLog(kv.Value)).Select(kv => kv.Key.y).ToArray();
            Assert.AreEqual(bottom, leafY.Min(), "lowest leaf layer");
            Assert.AreEqual(top, leafY.Max(), "top leaf layer");
            Assert.AreEqual(trunkTop, voxels.Where(kv => IsLog(kv.Value)).Max(kv => kv.Key.y), "trunk top");
        }

        [Test]
        public void Shapes_BranchesEndInTheCrown([Values(BranchStyle.Diagonal, BranchStyle.Arm)] BranchStyle style){
            // One low branch per tree under a round crown that starts level with the trunk top.
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, TrunkHeight = new IntRange(4, 6), CrownShape = CrownShape.Round,
                CrownRadius = new IntRange(2, 3), BranchCount = new IntRange(1, 1), BranchStyle = style, BranchLength = new IntRange(1, 2),
                BranchBandMin = .2f, BranchBandMax = .5f, HoleChance = 0f, BumpChance = 0f, HangChance = 0f
            };
            TreeVariantDef v = Def(variant);
            for (int s = 0; s < 200; s++){
                int x = s * 37 - 3000, z = s * 53 + 211;
                TreeRoll r = TreeShape.Roll(x, z, 7u, 0, v);
                Dictionary<int3, ushort> voxels = Stamp(new int3(x, 100, z), r, v, 7u);
                int tip = voxels.Where(kv => IsLog(kv.Value) && (kv.Key.x != 0 || kv.Key.z != 0)).Max(kv => kv.Key.y);
                Assert.GreaterOrEqual(tip, r.TrunkHeight - 1, $"({x},{z}) trunk {r.TrunkHeight}: the branch ends below the crown");
            }
        }

        [Test]
        public void Roll_BranchChance_GivesBranchesToThatShareOfTrees(){
            TreeVariant variant = new(){
                TrunkVoxel = _fixture.LogSo, LeafVoxel = _fixture.LeavesSo, BranchChance = .3f, BranchCount = new IntRange(1, 1)
            };
            TreeVariantDef v = Def(variant);
            const int n = 20000;
            int branched = 0;
            for (int i = 0; i < n; i++) branched += TreeShape.Roll(i % 200 - 100, i / 200, 5u, 0, v).BranchCount;
            Assert.AreEqual(.3f, branched / (float)n, .015f);

            variant.BranchChance = 0f;
            Assert.AreEqual(int2.zero, Def(variant).BranchCount, "chance 0: no branches, no branch reach");
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

        private static IEnumerable<string> ShippedVariants(){
            return FloraFixture.ShippedVariantNames();
        }

        [TestCaseSource(nameof(ShippedVariants))]
        public void Shapes_ShippedVariant_StandsOnItsAnchor_WithinReach(string name){
            // Every variant of the shipped species over 200 anchors: a face-connected trunk on the anchor that reaches
            // its crown (none for trunk 0), leaves only with a crown or branch clusters, a cone or tiered crown not
            // below its minimum base, arms of a crownless trunk below its top, and no voxel beyond the reach or below
            // the hang layer.
            TreeVariant variant = _fixture.ShippedVariant(name);
            TreeVariantDef v = Def(variant);
            ushort trunk = variant.TrunkVoxel ? _fixture.Ids[variant.TrunkVoxel] : (ushort)0;
            bool leafy = v.Crown != CrownShape.None || v.ClusterRadius.y > 0;
            bool tapered = v.Crown is CrownShape.Cone or CrownShape.Tiered;
            int lowest = int.MaxValue, highest = 0;
            long count = 0;
            for (int s = 0; s < Seeds; s++){
                uint seed = (uint)(1000 + s * 7919);
                int x = s * 37 - 3000, z = s * 53 + 211;
                TreeRoll r = TreeShape.Roll(x, z, seed, 0, v);
                Dictionary<int3, ushort> voxels = Stamp(new int3(x, 100, z), r, v, seed);
                string at = $"{name} seed {seed} ({x},{z}) trunk {r.TrunkHeight} r {r.CrownRadius} branches {r.BranchCount}";

                HashSet<int3> logs = new(voxels.Where(kv => FloraFixture.BaseOf(kv.Value) == trunk).Select(kv => kv.Key));
                HashSet<int3> leafSet = new(voxels.Where(kv => FloraFixture.BaseOf(kv.Value) != trunk).Select(kv => kv.Key));
                if (r.TrunkHeight > 0){
                    Assert.IsTrue(FaceConnected(logs, int3.zero), $"{at}: logs are not face-connected from the anchor");
                    if (v.Crown != CrownShape.None)
                        Assert.IsTrue(logs.Any(l => leafSet.Contains(l + Up)), $"{at}: the trunk does not reach its crown");
                }
                else Assert.IsEmpty(logs, $"{at}: trunk 0 has no logs");

                if (leafy) Assert.IsNotEmpty(leafSet, $"{at}: no leaves");
                else Assert.IsEmpty(leafSet, $"{at}: leaves without a crown or clusters");
                if (tapered)
                    Assert.GreaterOrEqual(leafSet.Min(p => p.y), v.CrownMinBase - 1, $"{at}: the crown starts below its minimum base");
                if (v.Crown == CrownShape.None && v.Branches == BranchStyle.Arm && r.Lean == 0)
                    foreach (int3 l in logs.Where(l => l.x < 0 || l.x >= r.Width || l.z < 0 || l.z >= r.Width))
                        Assert.Less(l.y, r.TrunkHeight - 1, $"{at}: arm voxel {l} not below the trunk top");

                foreach (int3 p in voxels.Keys){
                    Assert.LessOrEqual(math.max(math.abs(p.x), math.abs(p.z)), v.Reach, $"{at}: voxel {p} beyond reach {v.Reach}");
                    Assert.GreaterOrEqual(p.y, -1, $"{at}: voxel {p} below the hang layer");
                }

                int top = voxels.Keys.Max(p => p.y);
                lowest = math.min(lowest, top);
                highest = math.max(highest, top);
                count += voxels.Count;
            }

            TestContext.WriteLine($"{name}: reach {v.Reach}, spacing {v.SpacingRadius}, top layer {lowest}…{highest}, " +
                                  $"{count / (float)Seeds:F1} voxels per tree");
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
                candidates[i, j] = FloraPlacement.IsMultiBlock(FloraPlacement.RollRule(x, z, _cfg, default, c, out int _), _cfg);
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
