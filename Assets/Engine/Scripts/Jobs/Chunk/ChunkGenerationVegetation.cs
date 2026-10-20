using System;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Flora decisions of a chunk and its border ring (VEG-05): the chunk columns ± <c>FloraPadding</c>
    ///     (r_tree + max canopy radius). <see cref="ChunkJob.PrepareChunkMaps(int,ref GeneratorConfig,ref int3,NativeArray{ChunkColumn},ref FloraGrid)" />
    ///     fills it, <see cref="ChunkJob.PlaceVegetation" /> stamps from it. Element [gz + gx × Size.y] holds the
    ///     column (origin.x − Padding + gx, origin.z − Padding + gz): x-major like the chunk columns, so walking the
    ///     elements in order walks the world in (x, then z) order. INT-12: Temp, owned by the job.
    /// </summary>
    internal struct FloraGrid : IDisposable{
        public int Padding;
        public int2 Size;

        /// <summary>
        ///     Rule index into <c>WorldGenConfig.FloraRules</c> that the column passes, or -1. Ring columns only
        ///     record multi-block rules: their single-block plants lie outside the chunk.
        /// </summary>
        public NativeArray<int> Rule;

        /// <summary>Surface block Y of the column (clamped like <c>ChunkColumn.Height</c>); valid where <see cref="Rule" /> ≥ 0.</summary>
        public NativeArray<int> Height;

        /// <summary>1 for multi-block anchors whose structure may reach into the chunk (VEG-05).</summary>
        public NativeArray<byte> Anchor;

        public FloraGrid(int padding, Allocator allocator){
            Padding = padding;
            Size = new int2(ChunkWidth, ChunkDepth) + 2 * padding;
            int count = Size.x * Size.y;
            Rule = new NativeArray<int>(count, allocator, NativeArrayOptions.UninitializedMemory);
            Height = new NativeArray<int>(count, allocator);
            Anchor = new NativeArray<byte>(count, allocator);
        }

        /// <summary>Element index of chunk-local column (x, z), which may lie in the ring (−Padding ≤ x &lt; Width + Padding).</summary>
        public int Index(int x, int z){
            return z + Padding + (x + Padding) * Size.y;
        }

        public void Dispose(){
            if (Rule.IsCreated) Rule.Dispose();
            if (Height.IsCreated) Height.Dispose();
            if (Anchor.IsCreated) Anchor.Dispose();
        }
    }

    /// <summary>
    ///     Places the flora decided by <c>FloraPlacement</c> (§10, INT-11): single-block plants on their column, then
    ///     every multi-block anchor within reach of the chunk, clipped to the chunk (VEG-05). Replaces the old stub
    ///     with its per-chunk random stream and skipped edge columns (D-01, D-02).
    /// </summary>
    internal partial struct ChunkJob{
        /// <summary>
        ///     Stamps the flora of <paramref name="grid" /> into <paramref name="vox" />. A flora voxel replaces only a
        ///     voxel of a lower <see cref="FloraVoxelClass" />. Anchors are stamped in world (x, then z) order, so each
        ///     voxel gets the same value as in a whole-world stamp, whichever chunk holds it (TST-03).
        /// </summary>
        /// <param name="vox">Voxel buffer of the chunk after <see cref="FillTerrain" />.</param>
        /// <param name="chunkColumns">Per-column data from <c>PrepareChunkMaps</c>.</param>
        /// <param name="grid">Flora grid from the same <c>PrepareChunkMaps</c> call.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="config">Generator configuration with the flora tables.</param>
        public static void PlaceVegetation(NativeArray<ushort> vox, NativeArray<ChunkColumn> chunkColumns,
            in FloraGrid grid, int3 chunkWordPos, ref GeneratorConfig config){
            ref WorldGenConfig cfg = ref config.WorldGen;
            if (!cfg.HasFlora) return;

            // Single-block plants: only the column's own chunk holds them (D-02: every column, edges included).
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                ChunkColumn col = chunkColumns[GetColumnIdx(x, z, ChunkDepth)];
                if (col.FloraSpecies == 0) continue;
                FloraRuleDef d = cfg.FloraRules[grid.Rule[grid.Index(x, z)]];
                if (d.MultiBlock == 0) Put(vox, x, col.Height + 1, z, d.Voxel, d.VoxelClass, cfg);
            }

            // Multi-block anchors in chunk ± reach, world order.
            int reach = cfg.FloraReach;
            for (int x = -reach; x < ChunkWidth + reach; x++)
            for (int z = -reach; z < ChunkDepth + reach; z++){
                int g = grid.Index(x, z);
                if (grid.Anchor[g] == 0) continue;
                FloraRuleDef d = cfg.FloraRules[grid.Rule[g]];
                int baseY = grid.Height[g] + 1;
                if (d.Structure >= 0){
                    FloraStructureDef structure = cfg.FloraStructures[d.Structure];
                    for (int i = structure.Start; i < structure.Start + structure.Count; i++){
                        FloraVoxel v = cfg.FloraVoxels[i];
                        Put(vox, x + v.Offset.x, baseY + v.Offset.y, z + v.Offset.z, v.Voxel, v.Class, cfg);
                    }
                }
                else{
                    // A stacked voxel (cactus) stays on its column.
                    int height = FloraPlacement.StackHeight(chunkWordPos.x + x, chunkWordPos.z + z, cfg, d);
                    for (int k = 0; k < height; k++) Put(vox, x, baseY + k, z, d.Voxel, d.VoxelClass, cfg);
                }
            }
        }

        /// <summary>Writes <paramref name="voxel" /> at a chunk-local position if it lies in the chunk and outranks the voxel there.</summary>
        private static void Put(NativeArray<ushort> vox, int x, int y, int z, ushort voxel, byte cls, in WorldGenConfig cfg){
            if (!InChunk(x, y, z)) return;
            int idx = ChunkSize.Flatten(x, y, z);
            if (cfg.VoxelClassOf(vox[idx]) < cls) vox[idx] = voxel;
        }
    }
}
