using Engine.Scripts.Data;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Parallel job that procedurally generates chunk data (terrain, vegetation)
    ///     for multiple chunks in parallel.
    ///     The job calls <c>WorldSampler</c>, so it compiles synchronously with strict float mode; a Mono
    ///     fallback during async compilation or a relaxed float mode could otherwise change the generated world.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal partial struct ChunkJob : IJobParallelFor{
        [ReadOnly] public NativeList<int2> Jobs; // Chunk world positions
        [WriteOnly] public NativeParallelHashMap<int2, ChunkVoxelData>.ParallelWriter Results; // Result mapping
        [ReadOnly] public GeneratorConfig Config;

        /// <summary>
        ///     Executes chunk generation for the given job index and writes the result into the hash map.
        /// </summary>
        /// <param name="index">Index of the chunk position in the <see cref="Jobs" /> list.</param>
        public void Execute(int index){
            int2 position = Jobs[index];
            GenerateChunkData(position, out ChunkVoxelData voxelData);
            Results.TryAdd(position, voxelData);
        }

        /// <summary>
        ///     Generates all voxel data for a given chunk (terrain, vegetation).
        /// </summary>
        private void GenerateChunkData(int2 chunkPos, out ChunkVoxelData voxelData){
            int surfaceArea = ChunkSize.x * ChunkSize.z;

            int3 chunkWorldPos = ChunkSize.MemberMultiply(chunkPos.x, 0, chunkPos.y);

            NativeArray<ushort> chunkVoxels = new(VoxelsPerChunk, Allocator.Temp);
            NativeArray<ChunkColumn> chunkColumns = new(surfaceArea, Allocator.Temp);

            FloraGrid floraGrid = new(Config.WorldGen.FloraPadding, Allocator.Temp);

            PrepareChunkMaps(ref Config, ref chunkWorldPos, chunkColumns, ref floraGrid);
            DecideFlora(ref floraGrid, chunkColumns, chunkWorldPos, Config.WorldGen);
            ApplySurfaceCover(chunkColumns, chunkWorldPos, Config.WorldGen, floraGrid.Cache);

            FillTerrain(chunkVoxels, chunkColumns, ref Config);

            PlaceVegetation(chunkVoxels, chunkColumns, floraGrid, chunkWorldPos, ref Config);

            WriteToChunkData(chunkVoxels, out voxelData);

            chunkVoxels.Dispose();
            chunkColumns.Dispose();
            floraGrid.Dispose();
        }

        /// <summary>
        ///     Spare nodes on top of the generated runs, so the first edits of a chunk do not grow its list.
        /// </summary>
        private const int EditSlack = 16;

        /// <summary>
        ///     Initial node capacity of the Temp run list; it grows for chunks with more runs.
        /// </summary>
        private const int TempRunCapacity = 4096;

        /// <summary>
        ///     Writes uncompressed voxel data into the chunk using run-length encoding (RLE) compaction.
        ///     The runs go into a Temp list first and are then copied into a list of their exact size: a fixed
        ///     reservation would hold hundreds of KiB per loaded chunk, far more than the runs of a generated chunk.
        /// </summary>
        private void WriteToChunkData(NativeArray<ushort> vox, out ChunkVoxelData data){
            UnsafeIntervalList<ushort> runs = new(TempRunCapacity, Allocator.Temp);
            ushort last = 0;
            int run = 0;
            bool hasLast = false;
            for (int x = 0; x < ChunkSize.x; x++)
            for (int z = 0; z < ChunkSize.z; z++)
            for (int y = 0; y < ChunkSize.y; y++){
                ushort voxelId = vox[ChunkSize.Flatten(x, y, z)];
                if (hasLast && voxelId == last)
                    run++;
                else{
                    if (hasLast) runs.AddInterval(last, run);
                    last = voxelId;
                    run = 1;
                    hasLast = true;
                }
            }

            if (hasLast && run > 0) runs.AddInterval(last, run);

            data = new ChunkVoxelData(runs.CompressedLength + EditSlack);
            int start = 0;
            for (int n = 0; n < runs.CompressedLength; n++){
                UnsafeIntervalList<ushort>.Node node = runs.NodeAt(n);
                data.AddVoxels(node.Value, node.Count - start);
                start = node.Count;
            }

            runs.Dispose();
        }
    }
}
