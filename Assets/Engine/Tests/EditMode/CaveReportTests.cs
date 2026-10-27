using System.Collections.Generic;
using System.Linq;
using System.Text;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using static Engine.Scripts.Utils.VoxelConstants;
using Voxel = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     What the caves of the shipped settings amount to over 4096² columns around the origin: the mask shares, the
    ///     entrances per km², the carved share of every 16-block depth band, the share of the halls, the voxels per ore
    ///     rule and the runs per column the interval store has to hold. It prints values and sets no limits; the
    ///     placeholders are tuned towards their targets from it.
    /// </summary>
    [Category(TestTiers.Report)]
    [Explicit(TestTiers.ReportReason)]
    public class CaveReportTests{
        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";
        private const int Size = 4096;
        private const int Columns = ChunkWidth * ChunkDepth;
        private const int Bands = ChunkHeight / 16;
        private const int MaxOres = 8;
        private const int MaxRuns = 255;

        // Per-chunk counters.
        private const int CarvedBand = 0;
        private const int RockBand = CarvedBand + Bands;
        private const int Halls = RockBand + Bands;
        private const int Lava = Halls + 1;
        private const int LavaMask = Lava + 1;
        private const int CavernMask = LavaMask + 1;
        private const int EntranceMask = CavernMask + 1;
        private const int Openings = EntranceMask + 1;
        private const int Ore = Openings + 1;
        private const int RunHistogram = Ore + MaxOres;
        private const int OreFreeRunHistogram = RunHistogram + MaxRuns + 1;
        private const int Stride = OreFreeRunHistogram + MaxRuns + 1;

        /// <summary>Generates one chunk with flora and counts it; the halls alone are carved again into a solid copy.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct ReportJob : IJobParallelFor{
            [ReadOnly] public GeneratorConfig Config;
            public int ChunksPerSide;
            [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

            public void Execute(int index){
                GeneratorConfig config = Config;
                ref WorldGenConfig cfg = ref config.WorldGen;
                ref CaveConfig c = ref cfg.Caves;
                int3 origin = new((index % ChunksPerSide - ChunksPerSide / 2) * ChunkWidth, 0,
                    (index / ChunksPerSide - ChunksPerSide / 2) * ChunkDepth);
                NativeArray<ushort> vox = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                NativeArray<ushort> halls = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                NativeArray<ChunkColumn> columns = new(Columns, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                ChunkJob.GenerateVoxels(ref config, origin, vox, columns, true);
                int o = index * Stride;

                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    int wx = origin.x + x, wz = origin.z + z;
                    int h = math.min(columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)].Height, ChunkHeight - 1);
                    if (LavaPockets.MaskOpen(CaveMasks.Lava(wx, wz, cfg), c)) Counts[o + LavaMask]++;
                    if (Caverns.MaskOpen(CaveMasks.Cavern(wx, wz, cfg), c)) Counts[o + CavernMask]++;
                    if (CaveColumns.InEntranceMask(wx, wz, h, cfg)) Counts[o + EntranceMask]++;
                    ushort top = vox[ChunkSize.Flatten(x, h, z)];
                    if (top == 0 || top == c.LavaVoxel) Counts[o + Openings]++;

                    int runs = 0, oreFreeRuns = 0;
                    ushort last = 0, lastOreFree = 0;
                    for (int y = 0; y < ChunkHeight; y++){
                        ushort v = vox[ChunkSize.Flatten(x, y, z)], oreFree = v;
                        for (int r = 0; r < c.Ores.Length; r++)
                            if (v == c.Ores[r].Ore)
                                oreFree = c.Ores[r].Host;
                        if (y == 0 || v != last) runs++;
                        if (y == 0 || oreFree != lastOreFree) oreFreeRuns++;
                        last = v;
                        lastOreFree = oreFree;
                        halls[ChunkSize.Flatten(x, y, z)] = (ushort)(y <= h ? 1 : 0);
                        if (y > h) continue;
                        Counts[o + RockBand + y / 16]++;
                        if (v == 0 || v == c.LavaVoxel) Counts[o + CarvedBand + y / 16]++;
                        if (v != 0 && v == c.LavaVoxel) Counts[o + Lava]++;
                        for (int r = 0; r < c.Ores.Length && r < MaxOres; r++)
                            if (v == c.Ores[r].Ore)
                                Counts[o + Ore + r]++;
                    }

                    Counts[o + RunHistogram + math.min(runs, MaxRuns)]++;
                    Counts[o + OreFreeRunHistogram + math.min(oreFreeRuns, MaxRuns)]++;
                }

                // The halls alone: carved into a copy that is solid up to the surface and empty above it.
                CaveColumnData caves = new(0, Allocator.Temp);
                ChunkJob.PrepareCaveColumns(ref caves, columns, cfg);
                ChunkJob.CarveCaverns(halls, caves, origin, cfg);
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    int h = math.min(columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)].Height, ChunkHeight - 1);
                    for (int y = 0; y <= h; y++)
                        if (halls[ChunkSize.Flatten(x, y, z)] == 0)
                            Counts[o + Halls]++;
                }

                caves.Dispose();
                columns.Dispose();
                halls.Dispose();
                vox.Dispose();
            }
        }

        [Test]
        public void ShippedCaves_Report(){
            WorldGenSettings shipped = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            Assert.IsNotNull(shipped, ShippedSettingsPath);
            Dictionary<Voxel, ushort> ids = new();
            ushort Id(Voxel v){
                if (!ids.TryGetValue(v, out ushort id)) ids.Add(v, id = (ushort)(ids.Count + 1));
                return id;
            }

            GeneratorConfig config = new(){
                Voxels = new NativeHashMap<FixedString32Bytes, Voxel.VoxelDef>(1, Allocator.Persistent),
                WorldGen = shipped.ToConfigWithVoxelIds(TestFixtures.Seed, Id)
            };
            const int chunksPerSide = Size / ChunkWidth;
            NativeArray<int> counts = new(chunksPerSide * chunksPerSide * Stride, Allocator.Persistent);
            try{
                WorldGenConfig cfg = config.WorldGen;
                Assert.IsTrue(cfg.Caves.Enabled, "the shipped settings have caves on");
                Assert.LessOrEqual(cfg.Caves.Ores.Length, MaxOres);
                new ReportJob{ Config = config, ChunksPerSide = chunksPerSide, Counts = counts }
                    .Schedule(chunksPerSide * chunksPerSide, 1).Complete();

                long[] sum = new long[Stride];
                for (int i = 0; i < counts.Length; i++) sum[i % Stride] += counts[i];
                long columns = (long)Size * Size;
                double km2 = columns / 1e6;

                // Worms that start inside an entrance, and the connectors between worms and to halls, over the same area.
                int entranceWorms = 0, worms = 0, alone = 0, links = 0, linksReached = 0, halls = 0, hallsReached = 0, noHall = 0;
                int spirals = 0, connectorSpirals = 0;
                Dictionary<int3, int> arrivals = new();
                int2 firstCell = Worms.Cell(new int2(-Size / 2), cfg), lastCell = Worms.Cell(new int2(Size / 2 - 1), cfg);
                WormAnchors anchors = WormAnchors.Create(firstCell - 1, lastCell + 1, cfg, default, Allocator.Temp);
                for (int cx = firstCell.x; cx <= lastCell.x; cx++)
                for (int cz = firstCell.y; cz <= lastCell.y; cz++)
                for (int k = 0; k < Worms.MaxPerCell; k++){
                    int2 cell = new(cx, cz);
                    if (!Worms.Begin(cell, k, cfg, default, anchors, out Worm worm)){
                        if (k < Worms.PrimariesPerCell || !Worms.Present(cell, k, cfg)) continue;
                        if (Worms.IsHallLink(k)) noHall++;
                        else alone++;
                        continue;
                    }

                    if (k < Worms.PrimariesPerCell){
                        worms++;
                        if (worm.ShaftAbove != int.MaxValue) entranceWorms++;
                        if (Spirals(worm, cfg.Caves)) spirals++;
                        continue;
                    }

                    bool reached = Reaches(worm, worm.Target, cfg.Caves);
                    if (Spirals(worm, cfg.Caves)) connectorSpirals++;
                    if (Worms.IsHallLink(k)){
                        halls++;
                        if (reached) hallsReached++;
                    }
                    else{
                        links++;
                        if (reached) linksReached++;
                        Arrive(arrivals, worm.Target);
                    }
                }

                int busiest = arrivals.Count == 0 ? 0 : arrivals.Values.Max();
                int crowded = arrivals.Values.Count(n => n >= 3);

                anchors.Dispose();

                StringBuilder report = new();
                report.AppendLine($"Caves over {Size}² columns around the origin, seed {TestFixtures.Seed}:");
                report.AppendLine($"lava mask {100.0 * sum[LavaMask] / columns:F1} % of the area (target about 40 %), " +
                                  $"cavern mask {100.0 * sum[CavernMask] / columns:F1} % (about 15 %), " +
                                  $"entrance columns {100.0 * sum[EntranceMask] / columns:F2} %");
                report.AppendLine($"entrances: {entranceWorms / km2:F1} entrance worms per km² ({entranceWorms} of {worms} worms), " +
                                  $"{sum[Openings] / km2:F0} columns open to the sky per km²");
                report.AppendLine($"links: {links} of {worms} worms link to another worm ({alone} have none in reach), {linksReached} reach it; " +
                                  $"{halls} link to a hall ({noHall} have none in reach), {hallsReached} reach it");
                report.AppendLine($"junctions: the link ends lie in {arrivals.Count} cells of {ArrivalCell} blocks, at most {busiest} in one cell, " +
                                  $"{100.0 * crowded / math.max(arrivals.Count, 1):F1} % of the cells take 3 or more; " +
                                  $"{spirals} worms spiral ({100.0 * spirals / math.max(worms, 1):F1} %), {connectorSpirals} connectors " +
                                  $"({100.0 * connectorSpirals / math.max(links + halls, 1):F1} %)");
                long carved = Enumerable.Range(0, Bands).Sum(b => sum[CarvedBand + b]);
                report.AppendLine($"carved {carved} voxels, {100.0 * sum[Halls] / carved:F1} % of them in halls; " +
                                  $"lava {sum[Lava]} voxels ({100.0 * sum[Lava] / carved:F1} % of the carved)");
                report.Append("carved share per 16-block band below the surface:");
                for (int b = 0; b < Bands; b++)
                    if (sum[RockBand + b] > 0)
                        report.Append($" y{b * 16}–{b * 16 + 15} {100.0 * sum[CarvedBand + b] / sum[RockBand + b]:F2} %;");
                report.AppendLine();
                report.Append("voxels per ore rule:");
                for (int r = 0; r < cfg.Caves.Ores.Length; r++){
                    string name = ids.FirstOrDefault(p => p.Value == cfg.Caves.Ores[r].Ore).Key?.name ?? $"rule {r}";
                    report.Append($" {name} {sum[Ore + r]};");
                }

                report.AppendLine();
                report.AppendLine("voxel runs per column, as the interval lists hold them (with flora): " +
                                  Runs(sum, RunHistogram, columns) + "; with every ore read as its host: " +
                                  Runs(sum, OreFreeRunHistogram, columns));
                TestContext.WriteLine(report.ToString());
            }
            finally{
                counts.Dispose();
                config.WorldGen.Dispose();
                config.Voxels.Dispose();
            }
        }

        /// <summary>Edge of the cells the link ends are counted in, so ends a few blocks apart count as one junction.</summary>
        private const int ArrivalCell = 8;

        private static void Arrive(Dictionary<int3, int> arrivals, float3 target){
            int3 cell = (int3)math.floor(target / ArrivalCell);
            arrivals[cell] = arrivals.GetValueOrDefault(cell) + 1;
        }

        /// <summary>
        ///     The path (without its branch) has a window of <see cref="SpiralSteps" /> steps that climbs or
        ///     drops <see cref="SpiralRise" /> blocks or more while its ends lie within <see cref="SpiralFootprint" />
        ///     blocks horizontally: a worm circling up or down.
        /// </summary>
        private static bool Spirals(Worm worm, in CaveConfig c){
            List<float3> path = new(){ worm.Position };
            while (Worms.Next(ref worm, c)) path.Add(worm.Position);
            for (int i = SpiralSteps; i < path.Count; i++){
                float3 d = path[i] - path[i - SpiralSteps];
                if (math.abs(d.y) >= SpiralRise && math.lengthsq(d.xz) <= SpiralFootprint * SpiralFootprint) return true;
            }

            return false;
        }

        private const int SpiralSteps = 24;
        private const float SpiralRise = 10f;
        private const float SpiralFootprint = 6f;

        /// <summary>Some step of the worm (without its branch) opens into the worm that starts at <paramref name="target" />.</summary>
        private static bool Reaches(Worm worm, float3 target, in CaveConfig c){
            do{
                float r = worm.Radius + c.WormMinRadius;
                if (math.lengthsq(worm.Position - target) <= r * r) return true;
            } while (Worms.Next(ref worm, c));

            return false;
        }

        private static string Runs(long[] sum, int histogram, long columns){
            long seen = 0, median = -1, max = 0;
            double mean = 0;
            for (int n = 0; n <= MaxRuns; n++){
                long k = sum[histogram + n];
                if (k == 0) continue;
                max = n;
                mean += (double)n * k / columns;
                seen += k;
                if (median < 0 && seen * 2 >= columns) median = n;
            }

            return $"median {median}, mean {mean:F2}, max {max}" + (max == MaxRuns ? "+" : "");
        }
    }
}
