using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;
using Voxel = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Caverns, worms, lava and ores on a copy of the shipped settings with a local voxel id table: the same voxels
    ///     in every block that holds them, equal to the pure evaluation, worms that cross chunks or start far away, the
    ///     rules next to water, at the floor, at the surface, for lava and for ores, and flora on carved ground. Blocks run
    ///     through the chunk job's voxel pipeline in a Burst job.
    /// </summary>
    public class CaveTests{
        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";
        private const int Columns = ChunkWidth * ChunkDepth;

        private readonly Dictionary<Voxel, ushort> _ids = new();
        private WorldGenSettings _settings;
        private GeneratorConfig _config;

        [SetUp]
        public void SetUp(){
            WorldGenSettings shipped = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            Assert.IsNotNull(shipped, ShippedSettingsPath);
            Assert.IsTrue(shipped.Caves.Enabled, "the shipped settings have caves on");
            _settings = Object.Instantiate(shipped);
        }

        [TearDown]
        public void TearDown(){
            Release(ref _config);
            Object.DestroyImmediate(_settings);
        }

        private GeneratorConfig Build(){
            // Generation reads only the world-gen config; the job safety system still wants the voxel map constructed.
            return new GeneratorConfig{
                Voxels = new NativeHashMap<FixedString32Bytes, Voxel.VoxelDef>(1, Allocator.Persistent),
                WorldGen = _settings.ToConfigWithVoxelIds(TestFixtures.Seed, Id)
            };
        }

        private static void Release(ref GeneratorConfig config){
            if (!config.Voxels.IsCreated) return;
            config.WorldGen.Dispose();
            config.Voxels.Dispose();
            config = default;
        }

        private ushort Id(Voxel voxel){
            if (!_ids.TryGetValue(voxel, out ushort id)) _ids.Add(voxel, id = (ushort)(_ids.Count + 1));
            return id;
        }

        #region Generation

        /// <summary>One chunk-sized block per origin through <see cref="ChunkJob.GenerateVoxels" />, with the column heights.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct BlockJob : IJobParallelFor{
            [ReadOnly] public NativeArray<int3> Origins;
            [ReadOnly] public GeneratorConfig Config;
            public bool Flora;
            public bool WithAnchors;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> Voxels;
            [NativeDisableParallelForRestriction] public NativeArray<int> Heights;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> Species;
            [NativeDisableParallelForRestriction] public NativeArray<byte> Anchor;
            [NativeDisableParallelForRestriction] public NativeArray<int> Rule;

            public void Execute(int i){
                GeneratorConfig config = Config;
                int3 origin = Origins[i];
                NativeArray<ushort> vox = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                NativeArray<ChunkColumn> columns = new(Columns, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                ChunkJob.GenerateVoxels(ref config, origin, vox, columns, Flora);
                NativeArray<ushort>.Copy(vox, 0, Voxels, i * VoxelsPerChunk, VoxelsPerChunk);
                for (int c = 0; c < Columns; c++){
                    Heights[i * Columns + c] = columns[c].Height;
                    Species[i * Columns + c] = columns[c].FloraSpecies;
                }

                if (WithAnchors){
                    // The multi-block anchors of the block's own columns, from the same decisions the job takes.
                    FloraGrid grid = new(config.WorldGen.FloraPadding, Allocator.Temp);
                    ChunkJob.PrepareChunkMaps(ref config, ref origin, columns, ref grid);
                    ChunkJob.DecideFlora(ref grid, columns, origin, config.WorldGen);
                    for (int x = 0; x < ChunkWidth; x++)
                    for (int z = 0; z < ChunkDepth; z++){
                        int g = grid.Index(x, z), c = ChunkJob.GetColumnIdx(x, z, ChunkDepth);
                        Anchor[i * Columns + c] = grid.Anchor[g];
                        Rule[i * Columns + c] = grid.Anchor[g] != 0 ? grid.Rule[g] : -1;
                    }

                    grid.Dispose();
                }

                vox.Dispose();
                columns.Dispose();
            }
        }

        /// <summary>Managed copies of generated blocks.</summary>
        private sealed class Blocks{
            public int3[] Origins;
            public ushort[] Voxels;
            public int[] Heights;
            public ushort[] Species;
            public byte[] Anchor;
            public int[] Rule;

            public ushort Voxel(int block, int x, int y, int z){
                return Voxels[block * VoxelsPerChunk + ChunkSize.Flatten(x, y, z)];
            }

            public int Column(int block, int x, int z){
                return block * Columns + ChunkJob.GetColumnIdx(x, z, ChunkDepth);
            }
        }

        private static Blocks Generate(in GeneratorConfig config, IReadOnlyList<int3> origins, bool flora, bool anchors = false){
            int n = origins.Count;
            NativeArray<int3> o = new(origins.ToArray(), Allocator.TempJob);
            NativeArray<ushort> voxels = new(n * VoxelsPerChunk, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> heights = new(n * Columns, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<ushort> species = new(n * Columns, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<byte> anchor = new(anchors ? n * Columns : 1, Allocator.TempJob);
            NativeArray<int> rule = new(anchors ? n * Columns : 1, Allocator.TempJob);
            try{
                new BlockJob{
                    Origins = o, Config = config, Flora = flora, WithAnchors = anchors, Voxels = voxels, Heights = heights,
                    Species = species, Anchor = anchor, Rule = rule
                }.Schedule(n, 1).Complete();
                return new Blocks{
                    Origins = origins.ToArray(), Voxels = voxels.ToArray(), Heights = heights.ToArray(),
                    Species = species.ToArray(), Anchor = anchor.ToArray(), Rule = rule.ToArray()
                };
            }
            finally{
                o.Dispose();
                voxels.Dispose();
                heights.Dispose();
                species.Dispose();
                anchor.Dispose();
                rule.Dispose();
            }
        }

        /// <summary>A chunk-aligned area of generated chunks, addressed by world position.</summary>
        private sealed class Area{
            public int2 Origin;
            public int2 Chunks;
            public Blocks Blocks;
            public ushort Lava;

            public bool Contains(int x, int z){
                return x >= Origin.x && z >= Origin.y && x < Origin.x + Chunks.x * ChunkWidth && z < Origin.y + Chunks.y * ChunkDepth;
            }

            public int Block(int x, int z, out int lx, out int lz){
                int cx = (x - Origin.x) / ChunkWidth, cz = (z - Origin.y) / ChunkDepth;
                lx = x - Origin.x - cx * ChunkWidth;
                lz = z - Origin.y - cz * ChunkDepth;
                return cx * Chunks.y + cz;
            }

            public ushort this[int x, int y, int z]{
                get{
                    int b = Block(x, z, out int lx, out int lz);
                    return Blocks.Voxel(b, lx, y, lz);
                }
            }

            public int Height(int x, int z){
                int b = Block(x, z, out int lx, out int lz);
                return Blocks.Heights[Blocks.Column(b, lx, lz)];
            }

            public ushort Species(int x, int z){
                int b = Block(x, z, out int lx, out int lz);
                return Blocks.Species[Blocks.Column(b, lx, lz)];
            }

            /// <summary>Air or lava, which only the cave passes leave at or below the surface.</summary>
            public bool IsHole(ushort voxel){
                return voxel == 0 || (Lava != 0 && voxel == Lava);
            }

            /// <summary>Air or lava at or below the surface before carving.</summary>
            public bool Carved(int x, int y, int z){
                return y <= Height(x, z) && IsHole(this[x, y, z]);
            }

            /// <summary>What the cave passes left at a voxel at or below the surface, as <see cref="CaveCarving.Kind" />.</summary>
            public int Kind(int x, int y, int z){
                ushort v = this[x, y, z];
                return v == 0 ? CaveCarving.Air : Lava != 0 && v == Lava ? CaveCarving.Lava : CaveCarving.Solid;
            }

            public int CountCarved(){
                return Count(Carved);
            }

            public int CountLava(){
                return Count((x, y, z) => this[x, y, z] == Lava);
            }

            private int Count(Func<int, int, int, bool> test){
                int n = 0;
                for (int x = Origin.x; x < Origin.x + Chunks.x * ChunkWidth; x++)
                for (int z = Origin.y; z < Origin.y + Chunks.y * ChunkDepth; z++){
                    int h = Height(x, z);
                    for (int y = 0; y <= h; y++)
                        if (test(x, y, z))
                            n++;
                }

                return n;
            }
        }

        private static Area GenerateArea(in GeneratorConfig config, int2 originChunk, int2 chunks, bool flora, bool anchors = false,
            bool reversed = false){
            List<int3> origins = new();
            for (int cx = 0; cx < chunks.x; cx++)
            for (int cz = 0; cz < chunks.y; cz++)
                origins.Add(new int3((originChunk.x + cx) * ChunkWidth, 0, (originChunk.y + cz) * ChunkDepth));
            Area area = new(){
                Origin = originChunk * new int2(ChunkWidth, ChunkDepth), Chunks = chunks, Lava = config.WorldGen.Caves.LavaVoxel
            };
            if (!reversed){
                area.Blocks = Generate(config, origins, flora, anchors);
                return area;
            }

            // Generated in reverse order, then put back into area order.
            origins.Reverse();
            Blocks back = Generate(config, origins, flora, anchors);
            int n = origins.Count;
            Blocks blocks = new(){
                Origins = back.Origins.Reverse().ToArray(), Voxels = new ushort[back.Voxels.Length],
                Heights = new int[back.Heights.Length], Species = new ushort[back.Species.Length], Anchor = back.Anchor, Rule = back.Rule
            };
            for (int i = 0; i < n; i++){
                Array.Copy(back.Voxels, (n - 1 - i) * VoxelsPerChunk, blocks.Voxels, i * VoxelsPerChunk, VoxelsPerChunk);
                Array.Copy(back.Heights, (n - 1 - i) * Columns, blocks.Heights, i * Columns, Columns);
                Array.Copy(back.Species, (n - 1 - i) * Columns, blocks.Species, i * Columns, Columns);
            }

            area.Blocks = blocks;
            return area;
        }

        #endregion

        #region Worm walks

        /// <summary>Every step of the worm and, apart, every step of its branch (empty without one).</summary>
        private static (List<Worm> main, List<Worm> branch) Paths(Worm worm, in CaveConfig c){
            List<Worm> main = new(), steps = new();
            Worm branch = default;
            bool branched = false;
            do{
                main.Add(worm);
                if (worm.Step == worm.BranchStep) branched = Worms.Branch(worm, c, out branch);
            } while (Worms.Next(ref worm, c));

            if (!branched) return (main, steps);
            do{
                steps.Add(branch);
            } while (Worms.Next(ref branch, c));

            return (main, steps);
        }

        /// <summary>Some step's sphere lies within <c>WormMinRadius</c> of <paramref name="target" />: the worm opens into the worm that starts there.</summary>
        private static bool Reaches(List<Worm> steps, float3 target, in CaveConfig c){
            float min = c.WormMinRadius;
            return steps.Any(s => math.lengthsq(s.Position - target) <= (s.Radius + min) * (s.Radius + min));
        }

        /// <summary>Every step of the worm, then every step of its branch.</summary>
        private static List<Worm> Walk(Worm worm, in CaveConfig c){
            List<Worm> steps = new();
            Worm branch = default;
            bool branched = false;
            do{
                steps.Add(worm);
                if (worm.Step == worm.BranchStep) branched = Worms.Branch(worm, c, out branch);
            } while (Worms.Next(ref worm, c));

            if (!branched) return steps;
            do{
                steps.Add(branch);
            } while (Worms.Next(ref branch, c));

            return steps;
        }

        /// <summary>Worms that start in the cells <paramref name="firstCell" /> … <paramref name="lastCell" />, with their start column.</summary>
        private static IEnumerable<(int2 start, Worm worm)> WormsIn(int2 firstCell, int2 lastCell, WorldGenConfig cfg){
            for (int cx = firstCell.x; cx <= lastCell.x; cx++)
            for (int cz = firstCell.y; cz <= lastCell.y; cz++)
            for (int k = 0; k < Worms.MaxPerCell; k++){
                int2 cell = new(cx, cz);
                if (Worms.Begin(cell, k, cfg, default, out Worm worm)) yield return (Worms.StartColumn(cell, k, cfg), worm);
            }
        }

        private static int2 ChunkOf(int x, int z){
            return new int2((int)math.floor(x / (float)ChunkWidth), (int)math.floor(z / (float)ChunkDepth));
        }

        #endregion

        #region Determinism and seams

        [Test]
        public void Blocks_AtShiftedOriginsAndInAnyOrder_EqualTheChunks(){
            // A block at any origin holds the same voxels as the chunks it overlaps, flora, lava and ores included, and
            // the chunks do not depend on the order they are generated in.
            _config = Build();
            Area area = GenerateArea(_config, new int2(0, 4), new int2(3, 3), true);
            HashSet<ushort> ores = new(_config.WorldGen.Caves.Ores.Select(o => o.Ore));
            int carved = area.CountCarved(), lava = area.CountLava(), ore = area.Blocks.Voxels.Count(ores.Contains);
            TestContext.WriteLine($"{carved} carved voxels in 3 × 3 chunks, {lava} of them lava; {ore} ore voxels");
            Assert.Greater(carved, 1000, "the area holds caves");
            Assert.Greater(lava, 100, "the area holds lava");
            Assert.Greater(ore, 1000, "the area holds ores");

            Area reversed = GenerateArea(_config, new int2(0, 4), new int2(3, 3), true, reversed: true);
            Assert.IsTrue(area.Blocks.Voxels.SequenceEqual(reversed.Blocks.Voxels), "chunks generated in reverse order differ");

            int2[] shifts = { new(13, 21), new(45, 3), new(7, 50), new(31, 33), new(60, 64) };
            int3[] origins = shifts.Select(s => new int3(area.Origin.x + s.x, 0, area.Origin.y + s.y)).ToArray();
            Blocks shifted = Generate(_config, origins, true);
            int mismatches = 0;
            List<string> report = new();
            for (int b = 0; b < origins.Length; b++)
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 0; y < ChunkHeight; y++){
                int3 p = origins[b] + new int3(x, y, z);
                ushort expected = area[p.x, p.y, p.z], actual = shifted.Voxel(b, x, y, z);
                if (expected != actual && mismatches++ < 12) report.Add($"{p}: chunk {expected}, block {actual}");
            }

            Assert.AreEqual(0, mismatches, "shifted blocks differ from the chunks:\n" + string.Join("\n", report));
        }

        [Test]
        public void ChunkVoxels_EqualThePureCaveEvaluation(){
            // Every voxel of the columns with the most carving, the most lava and of random columns is carved, and holds
            // air or lava, exactly as the single-voxel evaluation (halls, pockets, every worm that can reach it) says.
            _config = Build();
            Area area = GenerateArea(_config, new int2(0, 4), new int2(3, 3), false);
            List<int2> columns = new();
            for (int x = area.Origin.x; x < area.Origin.x + 3 * ChunkWidth; x++)
            for (int z = area.Origin.y; z < area.Origin.y + 3 * ChunkDepth; z++)
                columns.Add(new int2(x, z));
            int CountIn(int2 c, int kind){
                int n = 0;
                for (int y = 0; y <= area.Height(c.x, c.y); y++)
                    if (area.Kind(c.x, y, c.y) == kind)
                        n++;
                return n;
            }

            System.Random random = new(5);
            List<int2> picked = columns.OrderByDescending(c => CountIn(c, CaveCarving.Air)).Take(64).ToList();
            picked.AddRange(columns.OrderByDescending(c => CountIn(c, CaveCarving.Lava)).Take(32));
            picked.AddRange(columns.OrderBy(_ => random.Next()).Take(48));

            int carved = 0, lava = 0, solid = 0, mismatches = 0;
            List<string> report = new();
            WorldGenConfig cfg = _config.WorldGen;
            foreach (int2 c in picked.Distinct())
                for (int y = 0; y <= area.Height(c.x, c.y); y++){
                    int expected = CaveCarving.Kind(c.x, y, c.y, cfg), actual = area.Kind(c.x, y, c.y);
                    if (actual == CaveCarving.Air) carved++;
                    else if (actual == CaveCarving.Lava) lava++;
                    else solid++;
                    if (expected != actual && mismatches++ < 12) report.Add($"({c.x},{y},{c.y}): pure {expected}, chunk {actual}");
                }

            string counts = $"{carved} air, {lava} lava and {solid} solid voxels checked in {picked.Distinct().Count()} columns";
            TestContext.WriteLine(counts);
            Assert.Greater(carved, 500, "the columns hold caves; " + counts);
            Assert.Greater(lava, 50, "the columns hold lava; " + counts);
            Assert.AreEqual(0, mismatches, "chunk voxels differ from the pure evaluation:\n" + string.Join("\n", report));
        }

        [Test]
        public void Worm_CrossingFourChunks_IsCarvedAlongItsPath(){
            // The centre voxel of every step lies inside its sphere (radius ≥ 1.5), so it is carved wherever the column
            // limits allow, in each chunk the worm passes through.
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            List<Worm> best = null;
            foreach ((int2 _, Worm worm) in WormsIn(new int2(-2, 0), new int2(3, 5), cfg)){
                List<Worm> steps = Walk(worm, c);
                int chunks = steps.Select(s => ChunkOf((int)math.floor(s.Position.x), (int)math.floor(s.Position.z))).Distinct().Count();
                if (chunks >= 4 && (best == null || steps.Count > best.Count)) best = steps;
            }

            Assert.IsNotNull(best, "some worm crosses four chunks");
            Dictionary<int2, CaveLimits> limits = new();
            List<(int3 voxel, int2 chunk)> checks = new();
            foreach (Worm s in best){
                int3 v = (int3)math.floor(s.Position);
                if (!limits.TryGetValue(v.xz, out CaveLimits l)) limits[v.xz] = l = CaveColumns.Limits(v.x, v.z, cfg, default);
                // One block of margin to the limits and the floor, against rounding in the managed walk.
                if (v.y > math.max(c.WormMinY, CaveColumns.Floor + 1) && v.y < l.Worm) checks.Add((v, ChunkOf(v.x, v.z)));
            }

            int2[] chunkList = checks.Select(k => k.chunk).Distinct().ToArray();
            TestContext.WriteLine($"worm of {best.Count} steps, {checks.Count} checked in {chunkList.Length} chunks");
            Assert.GreaterOrEqual(chunkList.Length, 4, "the worm carves in four chunks");
            Blocks blocks = Generate(_config, chunkList.Select(k => new int3(k.x * ChunkWidth, 0, k.y * ChunkDepth)).ToArray(), false);
            foreach ((int3 v, int2 chunk) in checks){
                int b = Array.IndexOf(chunkList, chunk);
                ushort voxel = blocks.Voxel(b, v.x - chunk.x * ChunkWidth, v.y, v.z - chunk.y * ChunkDepth);
                Assert.IsTrue(voxel == 0 || voxel == c.LavaVoxel, $"step centre {v} in chunk {chunk} is {voxel}");
            }
        }

        [Test]
        public void EntranceWorm_ShaftLeadsFromItsStartBelowTheCover(){
            // Outside the entrance mask nothing above surface − Cover is carved, so an entrance worm goes straight down
            // in its start column until its sphere lies below the cover; a sloped start left the opening a dead end.
            // With the mask wide open and every cell full, the start column of each dry entrance worm is air from the
            // start down to the shaft bottom, and the descent then reaches the top of the cavern band or its reach.
            _settings.Caves.EntranceThreshold = 0f;
            _settings.Caves.WormsPerCell = 2f;
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            List<(int2 start, Worm worm)> entrances = WormsIn(new int2(-2, -2), new int2(2, 2), cfg)
                .Where(w => w.worm.ShaftAbove != int.MaxValue &&
                            CaveColumns.Limits(w.start.x, w.start.y, cfg, default).Worm ==
                            CaveColumns.SurfaceY(w.start.x, w.start.y, cfg, default))
                .ToList();
            TestContext.WriteLine($"{entrances.Count} dry entrance worms");
            Assert.GreaterOrEqual(entrances.Count, 5, "the area has entrance worms");

            int2[] chunkList = entrances.Select(e => ChunkOf(e.start.x, e.start.y)).Distinct().ToArray();
            Blocks blocks = Generate(_config, chunkList.Select(k => new int3(k.x * ChunkWidth, 0, k.y * ChunkDepth)).ToArray(), false);
            int reachedBand = 0;
            foreach ((int2 start, Worm worm) in entrances){
                int2 chunk = ChunkOf(start.x, start.y);
                int b = Array.IndexOf(chunkList, chunk);
                int top = (int)math.floor(worm.Position.y);
                for (int y = top; y >= worm.ShaftAbove; y--){
                    ushort voxel = blocks.Voxel(b, start.x - chunk.x * ChunkWidth, y, start.y - chunk.y * ChunkDepth);
                    Assert.AreEqual(0, voxel, $"shaft of the worm at {start} is closed at y {y} (start {top}, bottom {worm.ShaftAbove})");
                }

                if (Walk(worm, c).Min(s => s.Position.y) <= worm.DescendAbove + 1) reachedBand++;
            }

            TestContext.WriteLine($"{reachedBand} of {entrances.Count} descend to y {c.CavernBandMax} or below the shaft");
            Assert.Greater(reachedBand, entrances.Count / 2, "most entrance worms descend to the cavern band");
        }

        [Test]
        public void Worm_StartingBeyondItsReach_CarvesIntoTheBlock(){
            // With every radius at its largest, a worm whose path ends near its reach carves a voxel more than
            // WormReach columns from its start; a block placed with that voxel on its edge nearest to the start still
            // enumerates the worm and carves it.
            _settings.Caves.WormMinRadius = _settings.Caves.WormMaxRadius;
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            int3 farthest = default;
            int2 from = default;
            int distance = 0;
            foreach ((int2 start, Worm worm) in WormsIn(new int2(-8, -8), new int2(7, 7), cfg))
                foreach (Worm s in Walk(worm, c)){
                    if (math.cmax(math.abs(s.Position.xz - worm.Origin)) < c.WormReach - 3) continue;
                    // Voxels well inside the sphere, against rounding in the managed walk.
                    float inner = s.Radius - .25f;
                    int3 lo = (int3)math.floor(s.Position - s.Radius), hi = (int3)math.floor(s.Position + s.Radius);
                    for (int x = lo.x; x <= hi.x; x++)
                    for (int z = lo.z; z <= hi.z; z++){
                        int d = math.cmax(math.abs(new int2(x, z) - start));
                        if (d <= distance) continue;
                        for (int y = lo.y; y <= hi.y; y++){
                            if (math.lengthsq(new float3(x, y, z) + .5f - s.Position) > inner * inner) continue;
                            if (y <= math.max(c.WormMinY, CaveColumns.Floor + 1) || y >= CaveColumns.Limits(x, z, cfg, default).Worm) continue;
                            farthest = new int3(x, y, z);
                            from = start;
                            distance = d;
                            break;
                        }
                    }
                }

            TestContext.WriteLine($"voxel {farthest} lies {distance} columns from the start {from}; padding {c.WormPadding}");
            Assert.Greater(distance, c.WormReach, "some worm carves beyond its reach");
            int2 delta = farthest.xz - from;
            int3 origin = new(delta.x > 0 ? farthest.x : farthest.x - (ChunkWidth - 1), 0,
                delta.y > 0 ? farthest.z : farthest.z - (ChunkDepth - 1));
            Blocks block = Generate(_config, new[]{ origin }, false);
            int3 local = farthest - origin;
            ushort voxel = block.Voxel(0, local.x, local.y, local.z);
            Assert.IsTrue(voxel == 0 || voxel == c.LavaVoxel, $"{farthest} in the block at {origin} is {voxel}");
        }

        [Test]
        public void WormPadding_CoversEveryWormThatReachesABlock(){
            // The worm pass keeps no buffer: it walks every worm whose start lies within WormPadding of the block and
            // carves as it goes. That is complete when no sphere reaches farther than WormPadding from its start.
            _settings.Caves.WormMinRadius = _settings.Caves.WormMaxRadius;
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            float reach = 0f;
            int worms = 0, longest = 0;
            foreach ((int2 _, Worm worm) in WormsIn(new int2(-8, -8), new int2(7, 7), cfg)){
                worms++;
                List<Worm> steps = Walk(worm, c);
                longest = math.max(longest, steps.Count);
                foreach (Worm s in steps) reach = math.max(reach, math.cmax(math.abs(s.Position.xz - worm.Origin)) + s.Radius);
            }

            int cells = (ChunkWidth + 2 * c.WormPadding) / c.WormCellSize + 2;
            int worstSteps = cells * cells * Worms.MaxPerCell * Worms.MaxSteps(c);
            TestContext.WriteLine($"{worms} worms, farthest sphere edge {reach:F2} columns from the start, padding {c.WormPadding}, " +
                                  $"longest walk {longest} steps, at most {worstSteps} steps per chunk");
            Assert.Greater(worms, 100, "the cells hold worms");
            Assert.LessOrEqual(reach, c.WormPadding, "a sphere reaches beyond the padding");
            Assert.LessOrEqual(longest, Worms.MaxSteps(c), "a walk is longer than the bound");
        }

        [Test]
        public void Worms_ReachTheirTargets(){
            // No tunnel lies alone: a worm steers towards the nearest other starts of its 3 × 3 cells and its length
            // grows to reach the first, so every worm with a target opens into it; its branch takes the next one and
            // the bridges of a cell reach the nearest start of the +x and +z neighbour cells. Every bridge and every
            // first target must arrive; the later ones are reported.
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            int primaries = 0, withTarget = 0, alone = 0, first = 0, second = 0, secondReached = 0, branch = 0, branchReached = 0;
            int bridges = 0, bridgesReached = 0;
            List<string> missed = new();
            for (int cx = -8; cx < 8; cx++)
            for (int cz = -8; cz < 8; cz++)
            for (int k = 0; k < Worms.MaxPerCell; k++){
                int2 cell = new(cx, cz);
                if (!Worms.Begin(cell, k, cfg, default, out Worm worm)) continue;
                (List<Worm> main, List<Worm> fork) = Paths(worm, c);
                if (k >= Worms.PrimariesPerCell){
                    bridges++;
                    Assert.AreEqual(1, worm.TargetsLeft, $"bridge {k} of {cell} has one target");
                    if (Reaches(main, worm.Target, c)) bridgesReached++;
                    else missed.Add($"bridge {k} of cell {cell} from {worm.Position} misses {worm.Target} in {main.Count} steps");
                    continue;
                }

                primaries++;
                if (worm.TargetsLeft == 0){
                    alone++;
                    continue;
                }

                withTarget++;
                if (Reaches(main, worm.Target, c)) first++;
                else missed.Add($"worm {k} of cell {cell} from {worm.Position} misses {worm.Target} in {main.Count} steps");
                if (worm.TargetsLeft > 1){
                    second++;
                    if (Reaches(main, worm.NextTarget, c)) secondReached++;
                }

                if (fork.Count > 0 && worm.BranchTargets > 0){
                    branch++;
                    if (Reaches(fork, worm.BranchTarget, c)) branchReached++;
                }
            }

            TestContext.WriteLine($"{primaries} worms: {withTarget} with a target ({alone} without one in reach), first target reached " +
                                  $"by {first}, second by {secondReached} of {second}, branch target by {branchReached} of {branch}; " +
                                  $"{bridges} bridges, {bridgesReached} reach their target");
            Assert.Greater(withTarget, 100, "the cells hold worms with targets");
            Assert.Greater(bridges, 50, "the cells hold bridges");
            Assert.AreEqual(0, missed.Count, "paths miss their first target:\n" + string.Join("\n", missed));
            Assert.Greater(secondReached, second / 3, "most second targets are reached");
        }

        #endregion

        #region Rules

        [Test]
        public void Rules_Over64ChunksWithCoasts([Values(false, true)] bool moreEntrances){
            // 8 × 8 chunks with ~27 % water and entrance columns: no carved voxel touches water or ice, nothing is
            // carved at y ≤ 2 and y 0 is bedrock, carving above surface − Cover happens only in entrance columns
            // (caverns and pockets never come that close, worms only there), and lava lies only at or below LavaLevel
            // inside the lava mask, where no carved voxel at that depth holds air. Worms rarely start in the entrance mask,
            // so a second run widens the mask and fills every cell with worms to open some.
            if (moreEntrances){
                _settings.Caves.EntranceThreshold = 0f;
                _settings.Caves.WormsPerCell = 2f;
            }

            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            ushort water = cfg.WaterVoxel, ice = cfg.IceVoxel, bedrock = c.BedrockVoxel, lavaVoxel = c.LavaVoxel;
            Assert.AreNotEqual(0, water, "water resolves through the id table");
            Assert.AreNotEqual(0, bedrock, "bedrock resolves through the id table");
            Assert.AreNotEqual(0, lavaVoxel, "lava resolves through the id table");
            Area area = GenerateArea(_config, new int2(-2, 2), new int2(8, 8), true);

            int carved = 0, wet = 0, openings = 0, lava = 0, lavaColumns = 0;
            List<string> report = new();
            int failures = 0;
            void Fail(string message){
                if (failures++ < 12) report.Add(message);
            }

            int3[] faces = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
            for (int x = area.Origin.x; x < area.Origin.x + 8 * ChunkWidth; x++)
            for (int z = area.Origin.y; z < area.Origin.y + 8 * ChunkDepth; z++){
                int h = area.Height(x, z);
                if (CaveColumns.Submerged(h, cfg)) wet++;
                if (area[x, 0, z] != bedrock) Fail($"({x},0,{z}) is {area[x, 0, z]}, not bedrock");
                int highest = int.MinValue;
                bool inMask = LavaPockets.MaskOpen(CaveMasks.Lava(x, z, cfg), c);
                if (inMask) lavaColumns++;
                for (int y = 1; y < ChunkHeight; y++){
                    ushort voxel = area[x, y, z];
                    if (voxel == lavaVoxel){
                        lava++;
                        if (y > c.LavaLevel || !inMask) Fail($"({x},{y},{z}) lava above LavaLevel or outside the mask");
                    }

                    if (y > h || !area.IsHole(voxel)) continue;
                    carved++;
                    highest = y;
                    if (voxel == 0 && inMask && y <= c.LavaLevel) Fail($"({x},{y},{z}) air at the lava level inside the mask");
                    if (y <= CaveColumns.Floor) Fail($"({x},{y},{z}) carved in the bottom layer");
                    foreach (int3 f in faces){
                        int3 n = new int3(x, y, z) + f;
                        if (!area.Contains(n.x, n.z) || n.y < 0 || n.y >= ChunkHeight) continue;
                        ushort v = area[n.x, n.y, n.z];
                        if (v == water || (ice != 0 && v == ice)) Fail($"({x},{y},{z}) carved next to water at {n}");
                    }
                }

                if (highest == int.MinValue) continue;
                if (highest == h) openings++;
                if (highest > h - c.Cover && !CaveColumns.IsEntrance(x, z, h, cfg, default))
                    Fail($"({x},{highest},{z}) carved above surface {h} − Cover outside the entrances");
            }

            int columns = 64 * Columns;
            string counts = $"{carved} carved voxels ({lava} lava), {wet * 100f / columns:F1} % submerged columns, " +
                            $"{lavaColumns * 100f / columns:F1} % in the lava mask, {openings} columns open to the sky";
            TestContext.WriteLine(counts);
            Assert.AreEqual(0, failures, counts + "\n" + string.Join("\n", report));
            Assert.Greater(wet, columns / 10, "the area has coasts; " + counts);
            Assert.Greater(carved, 10000, "the area holds caves; " + counts);
            Assert.Greater(lava, 1000, "the area holds lava; " + counts);
            if (moreEntrances) Assert.Greater(openings, 0, "some entrance opens to the sky; " + counts);
        }

        [Test]
        public void LavaPockets_HoldLavaUnderAnAirGap(){
            // Inside the lava mask, pockets carve the low band whether or not a cave reaches them: lava up to LavaLevel
            // with air above it, so some columns hold lava at LavaLevel under air.
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            Area area = GenerateArea(_config, new int2(0, 4), new int2(3, 3), false);
            int gaps = 0, pocketVoxels = 0;
            for (int x = area.Origin.x; x < area.Origin.x + 3 * ChunkWidth; x++)
            for (int z = area.Origin.y; z < area.Origin.y + 3 * ChunkDepth; z++){
                if (area[x, c.LavaLevel, z] == c.LavaVoxel && area[x, c.LavaLevel + 1, z] == 0) gaps++;
                int top = CaveColumns.Limits(x, z, cfg, default).Cavern;
                for (int y = c.LavaPocketBandMin; y <= c.LavaPocketBandMax; y++)
                    if (LavaPockets.Carves(x, y, z, top, cfg))
                        pocketVoxels++;
            }

            TestContext.WriteLine($"{pocketVoxels} pocket voxels, {gaps} columns with lava under air at y {c.LavaLevel}");
            Assert.Greater(pocketVoxels, 100, "the area holds pockets");
            Assert.Greater(gaps, 10, "pockets hold lava under an air gap");
        }

        [Test]
        public void Ores_Over64Chunks_ReplaceOnlyTheirHostInTheirRange(){
            // Against the same chunks without ore rules, every changed voxel is the ore of a rule over that rule's host,
            // within its y range, and the ore the pure vein walk puts there; so is every unchanged host voxel sampled.
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            OreRuleDef[] rules = cfg.Caves.Ores.ToArray();
            Assert.AreEqual(8, rules.Length, "the shipped ore rules resolve");
            Area area = GenerateArea(_config, new int2(-2, 2), new int2(8, 8), false);
            _settings.Caves.Ores.Clear();
            GeneratorConfig withoutOres = Build();
            Area plain;
            try{
                plain = GenerateArea(withoutOres, new int2(-2, 2), new int2(8, 8), false);
            }
            finally{
                Release(ref withoutOres);
            }

            int[] perRule = new int[rules.Length];
            int failures = 0, pureChecked = 0;
            List<string> report = new();
            void Fail(string message){
                if (failures++ < 12) report.Add(message);
            }

            HashSet<ushort> hosts = new(rules.Select(r => r.Host));
            System.Random random = new(11);
            List<int3> pure = new();
            for (int b = 0; b < area.Blocks.Origins.Length; b++)
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 0; y < ChunkHeight; y++){
                ushort with = area.Blocks.Voxel(b, x, y, z), without = plain.Blocks.Voxel(b, x, y, z);
                int3 p = area.Blocks.Origins[b] + new int3(x, y, z);
                if (with == without){
                    if (hosts.Contains(without) && random.Next(2000) == 0) pure.Add(p);
                    continue;
                }

                int r = Array.FindIndex(rules, o => o.Ore == with);
                if (r < 0 || rules[r].Host != without || y < rules[r].MinY || y > rules[r].MaxY){
                    Fail($"{p}: {with} over {without}, not an ore over its host in its range");
                    continue;
                }

                perRule[r]++;
                if (random.Next(20) == 0) pure.Add(p);
            }

            foreach (int3 p in pure){
                int b = area.Block(p.x, p.z, out int lx, out int lz);
                ushort expected = OreVeins.Apply(p, plain.Blocks.Voxel(b, lx, p.y, lz), cfg), actual = area[p.x, p.y, p.z];
                pureChecked++;
                if (expected != actual) Fail($"{p}: pure veins give {expected}, chunk {actual}");
            }

            string counts = "ore voxels per rule: " + string.Join(", ", perRule) + $"; {pureChecked} voxels checked against the pure walk";
            TestContext.WriteLine(counts);
            Assert.AreEqual(0, failures, counts + "\n" + string.Join("\n", report));
            for (int r = 0; r < 6; r++) Assert.Greater(perRule[r], 0, $"stone ore rule {r} places veins; " + counts);
            Assert.Greater(pureChecked, 1000, counts);
        }

        #endregion

        #region Flora

        /// <summary>
        ///     Settings that carve the ground under much of the flora: no cover, halls everywhere up to the surface,
        ///     entrances everywhere away from water.
        /// </summary>
        private void CarveTheSurface(){
            CaveSettings caves = _settings.Caves;
            caves.Cover = 0;
            caves.CavernMaskThreshold = -1f;
            caves.CavernBandMax = MaxTerrainHeight;
            caves.CavernThreshold = .2f;
            caves.EntranceThreshold = -1f;
        }

        /// <summary>A multi-block anchor of a generated area, with the voxels its structure writes.</summary>
        private struct Anchor{
            public int3 Ground;
            public bool Carved;
            public bool Tree;
            public List<FloraVoxel> Voxels;
            public int3 Base;
        }

        private static List<Anchor> Anchors(Area area, in WorldGenConfig cfg){
            List<Anchor> anchors = new();
            NativeList<FloraVoxel> tree = new(256, Allocator.Temp);
            Blocks blocks = area.Blocks;
            for (int b = 0; b < blocks.Origins.Length; b++)
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int col = blocks.Column(b, x, z);
                if (blocks.Anchor[col] == 0) continue;
                FloraRuleDef d = cfg.FloraRules[blocks.Rule[col]];
                int wx = blocks.Origins[b].x + x, wz = blocks.Origins[b].z + z;
                int h = blocks.Heights[col];
                Anchor a = new(){ Base = new int3(wx, h + 1, wz), Voxels = new List<FloraVoxel>(), Tree = d.Tree >= 0 };
                if (a.Tree){
                    int variant = TreeShape.PickVariant(wx, wz, cfg, d.Tree);
                    TreeRoll roll = TreeShape.Roll(wx, wz, cfg.Seed, variant, cfg.TreeVariants[variant]);
                    if (blocks.Anchor[col] == 2) roll.Narrow();
                    // The voxels under the trunk, as the vegetation pass tests them.
                    int4 box = roll.Lying ? TreeShape.Footprint(roll) : new int4(0, 0, roll.Width - 1, roll.Width - 1);
                    a.Ground = new int3(wx, h - roll.Sink, wz);
                    for (int fx = wx + box.x; fx <= wx + box.z && !a.Carved; fx++)
                    for (int fz = wz + box.y; fz <= wz + box.w && !a.Carved; fz++)
                        a.Carved = CaveCarving.IsCarved(fx, h - roll.Sink, fz, cfg);
                    tree.Clear();
                    TreeShape.Build(a.Base, roll, cfg.TreeVariants[variant], cfg.Seed, ref tree);
                    foreach (FloraVoxel v in tree) a.Voxels.Add(v);
                }
                else{
                    a.Ground = new int3(wx, h, wz);
                    a.Carved = CaveCarving.IsCarved(wx, h, wz, cfg);
                    if (d.Structure >= 0){
                        FloraStructureDef s = cfg.FloraStructures[d.Structure];
                        for (int i = s.Start; i < s.Start + s.Count; i++) a.Voxels.Add(cfg.FloraVoxels[i]);
                    }
                    else
                        for (int k = 0; k < FloraPlacement.StackHeight(wx, wz, cfg, d); k++)
                            a.Voxels.Add(new FloraVoxel{ Offset = new int3(0, k, 0), Voxel = d.Voxel, Class = d.VoxelClass });
                }

                anchors.Add(a);
            }

            tree.Dispose();
            return anchors;
        }

        /// <summary>
        ///     Voxels of <paramref name="a" /> that stand out: the solid ones (trunks, logs, cacti) above their column's
        ///     surface, inside the area. Leaves are left out, since a neighbour's crown may fill the same voxel.
        /// </summary>
        private static IEnumerable<(int3 p, ushort voxel)> Standing(Anchor a, Area area){
            foreach (FloraVoxel v in a.Voxels){
                int3 p = a.Base + v.Offset;
                if (v.Class != FloraVoxelClass.Solid || !area.Contains(p.x, p.z) || p.y >= ChunkHeight || p.y <= area.Height(p.x, p.z)) continue;
                yield return (p, v.Voxel);
            }
        }

        [Test]
        public void Flora_NeverStandsOnCarvedGround(){
            // With the ground carved under much of the flora: no single plant stands on a carved top block, no trunk,
            // log or stack stands on carved ground, and flora on solid ground is all there.
            CarveTheSurface();
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            Area area = GenerateArea(_config, new int2(5, 12), new int2(3, 3), true, true);

            int carvedPlants = 0, plants = 0, failures = 0;
            List<string> report = new();
            for (int x = area.Origin.x; x < area.Origin.x + 3 * ChunkWidth; x++)
            for (int z = area.Origin.y; z < area.Origin.y + 3 * ChunkDepth; z++){
                ushort species = area.Species(x, z);
                if (species == 0) continue;
                FloraRuleDef d = Enumerable.Range(0, cfg.FloraRules.Length).Select(k => cfg.FloraRules[k]).First(r => r.Species == species);
                if (d.MultiBlock != 0) continue;
                int h = area.Height(x, z);
                if (CaveCarving.IsCarved(x, h, z, cfg)){
                    carvedPlants++;
                    if (area[x, h + 1, z] == d.Voxel && failures++ < 12) report.Add($"plant at ({x},{h + 1},{z}) on carved ground");
                }
                else plants++;
            }

            List<Anchor> anchors = Anchors(area, cfg);
            int carvedAnchors = 0, standing = 0, present = 0;
            foreach (Anchor a in anchors){
                List<(int3 p, ushort voxel)> voxels = Standing(a, area).ToList();
                int found = voxels.Count(v => area[v.p.x, v.p.y, v.p.z] == v.voxel);
                if (a.Carved){
                    carvedAnchors++;
                    if (found > 0 && failures++ < 12) report.Add($"{(a.Tree ? "tree" : "anchor")} at {a.Base} on carved ground {a.Ground}: {found} voxels stand");
                }
                else{
                    standing += voxels.Count;
                    present += found;
                }
            }

            TestContext.WriteLine($"single plants: {carvedPlants} on carved ground, {plants} on solid ground; anchors: {carvedAnchors} " +
                                  $"of {anchors.Count} on carved ground, {present} of {standing} standing voxels of the others found");
            Assert.Greater(carvedPlants, 20, "plants on carved ground");
            Assert.Greater(plants, 20, "plants on solid ground");
            Assert.Greater(carvedAnchors, 5, "anchors on carved ground");
            Assert.Greater(anchors.Count - carvedAnchors, 5, "anchors on solid ground");
            Assert.AreEqual(0, failures, string.Join("\n", report));
            Assert.Greater(present, standing * 95 / 100, "flora on solid ground stands");
        }

        [Test]
        public void Flora_TreeOnCarvedGroundNextToABorder_IsInNeitherChunk(){
            // A tree anchored one block from a chunk border on carved ground: neither chunk holds its trunk, and both
            // hold fewer of its voxels than with caves off, where the tree stands in both.
            CarveTheSurface();
            _config = Build();
            WorldGenConfig cfg = _config.WorldGen;
            Area area = GenerateArea(_config, new int2(5, 12), new int2(3, 3), true, true);
            _settings.Caves.Enabled = false;
            GeneratorConfig off = Build();
            try{
                Area plain = GenerateArea(off, new int2(5, 12), new int2(3, 3), true);
                int found = 0;
                foreach (Anchor a in Anchors(area, cfg)){
                    if (!a.Carved || !a.Tree) continue;
                    int lx = a.Base.x - area.Origin.x, lz = a.Base.z - area.Origin.y;
                    int mx = lx % ChunkWidth, mz = lz % ChunkDepth;
                    if (!(mx == 1 && lx > ChunkWidth) && !(mx == ChunkWidth - 2 && lx < 2 * ChunkWidth) &&
                        !(mz == 1 && lz > ChunkDepth) && !(mz == ChunkDepth - 2 && lz < 2 * ChunkDepth)) continue;
                    // Its voxels by chunk, where the tree reaches into two.
                    Dictionary<int2, List<FloraVoxel>> byChunk = new();
                    foreach (FloraVoxel v in a.Voxels){
                        int3 p = a.Base + v.Offset;
                        if (!area.Contains(p.x, p.z) || p.y >= ChunkHeight) continue;
                        int2 chunk = ChunkOf(p.x, p.z);
                        if (!byChunk.TryGetValue(chunk, out List<FloraVoxel> list)) byChunk[chunk] = list = new List<FloraVoxel>();
                        list.Add(v);
                    }

                    if (byChunk.Count < 2) continue;
                    found++;
                    List<(int3 p, ushort voxel)> standing = Standing(a, area).ToList();
                    Assert.AreEqual(0, standing.Count(v => area[v.p.x, v.p.y, v.p.z] == v.voxel), $"tree at {a.Base}: trunk stands with caves on");
                    Assert.Greater(standing.Count(v => plain[v.p.x, v.p.y, v.p.z] == v.voxel), 0, $"tree at {a.Base}: trunk stands with caves off");
                    foreach ((int2 chunk, List<FloraVoxel> voxels) in byChunk){
                        int on = voxels.Count(v => Matches(area, a.Base + v.Offset, v.Voxel));
                        int without = voxels.Count(v => Matches(plain, a.Base + v.Offset, v.Voxel));
                        Assert.Less(on, without, $"tree at {a.Base}, chunk {chunk}: {on} voxels with caves, {without} without");
                    }
                }

                TestContext.WriteLine($"{found} trees on carved ground next to a border reach into two chunks");
                Assert.Greater(found, 0, "a tree on carved ground next to a border");
            }
            finally{
                Release(ref off);
            }
        }

        private static bool Matches(Area area, int3 p, ushort voxel){
            return area[p.x, p.y, p.z] == voxel;
        }

        #endregion
    }
}
