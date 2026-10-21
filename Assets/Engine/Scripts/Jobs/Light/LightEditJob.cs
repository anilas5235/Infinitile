using Engine.Scripts.Data;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Collections;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Light{
    /// <summary>
    ///     LGT-07: incremental light update after one voxel edit (the voxel data already holds the new id), run
    ///     synchronously with <c>Run()</c>. Per channel the usual two-queue method: a removal BFS clears the light
    ///     that may have come through the edited voxel, then the light is re-propagated from the voxels bordering
    ///     the cleared area, from the edited voxel's neighbours and from the cleared voxels' own sources (emission,
    ///     the sky above the world). Light changes at most 14 voxels from the edited column, so the box edit ± 16
    ///     (inside the 3 × 3 chunks around the edit) holds every voxel that can change plus its sources. The
    ///     result equals a full recomputation (LGT-11).
    /// </summary>
    [BurstCompile]
    internal struct LightEditJob : IJob{
        /// <summary>Box radius around the edited column.</summary>
        internal const int Radius = MaxLightLevel + 1;

        /// <summary>Box size per axis: 2 radii + the edited column + 2 sentinel columns.</summary>
        internal const int BoxSize = 2 * Radius + 3;

        /// <summary>Number of neighbour chunks the job reads and writes (3 × 3 around the edit).</summary>
        internal const int ChunkCount = 9;

        [ReadOnly] public VoxelLightTable Table;

        /// <summary>Voxel data of the 3 × 3 chunks, index <see cref="ChunkIndex" />.</summary>
        [ReadOnly] public NativeArray<ChunkVoxelData> Voxels;

        /// <summary>Light of the 3 × 3 chunks; a changed chunk's list is replaced (the old one is disposed).</summary>
        public NativeArray<ChunkLightData> Lights;

        public int2 CenterChunk;

        /// <summary>World position of the edited voxel.</summary>
        public int3 Position;

        public ushort OldVoxel;

        /// <summary>Per chunk (<see cref="ChunkIndex" />) and partition y: 1 if light in or next to it changed.</summary>
        [WriteOnly] public NativeArray<byte> ChangedPartitions;

        internal static int ChunkIndex(int2 offset){
            return (offset.x + 1) * 3 + offset.y + 1;
        }

        public void Execute(){
            for (int i = 0; i < ChangedPartitions.Length; i++) ChangedPartitions[i] = 0;

            int3 local = new(Position.x - CenterChunk.x * ChunkWidth, Position.y, Position.z - CenterChunk.y * ChunkDepth);
            ushort newVoxel = Voxels[ChunkIndex(int2.zero)].GetVoxel(local);
            if (Attenuation(OldVoxel) == Attenuation(newVoxel) && Emission(OldVoxel) == Emission(newVoxel)) return;

            LightRegion region = new(BoxSize, BoxSize, Position.xz - (Radius + 1), Allocator.Temp);
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int k = ChunkIndex(new int2(dx, dz));
                region.FillVoxels(Voxels[k].Data, (CenterChunk + new int2(dx, dz)) * ChunkSizeXZ, Table);
            }

            NativeHashMap<int, ushort> emission = new(region.Emitters.Length, Allocator.Temp);
            for (int i = 0; i < region.Emitters.Length; i++)
                emission.TryAdd(region.Emitters[i], region.Light[region.Emitters[i]]);

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                region.FillLight(Lights[ChunkIndex(new int2(dx, dz))].Data, (CenterChunk + new int2(dx, dz)) * ChunkSizeXZ);

            NativeArray<ushort> original = new(region.Light, Allocator.Temp);
            NativeQueue<int2> removal = new(Allocator.Temp);
            NativeQueue<int> add = new(Allocator.Temp);
            NativeList<int> removed = new(256, Allocator.Temp);
            int p = region.ColumnBase(Radius + 1, Radius + 1) + Position.y;

            for (int channel = 0; channel < VoxelLight.ChannelCount; channel++){
                Relight(ref region, channel, p, emission, ref removal, ref add, ref removed);
                removed.Clear();
            }

            WriteBack(ref region, original);

            removed.Dispose();
            add.Dispose();
            removal.Dispose();
            original.Dispose();
            emission.Dispose();
            region.Dispose();
        }

        private int Attenuation(ushort voxel){
            return voxel < Table.Count ? Table.Attenuation[voxel] : LightRegion.Opaque;
        }

        private ushort Emission(ushort voxel){
            return voxel < Table.Count ? Table.Emission[voxel] : (ushort)0;
        }

        private static void Relight(ref LightRegion region, int channel, int p, in NativeHashMap<int, ushort> emission,
            ref NativeQueue<int2> removal, ref NativeQueue<int> add, ref NativeList<int> removed){
            int shift = 4 * channel;
            int sky = channel == VoxelLight.Sky ? 1 : 0;

            removal.Enqueue(new int2(p, Level(region, p, shift)));
            SetLevel(ref region, p, shift, 0);
            removed.Add(p);

            // Removal BFS: a neighbour darker than the removed light (or sky 15 straight below sky 15) may
            // depend on it and is cleared too; a brighter or equal one is lit from elsewhere and re-propagates.
            while (removal.TryDequeue(out int2 entry)){
                int index = entry.x;
                int y = index & (ChunkHeight - 1);
                Remove(ref region, index + region.StrideX, entry.y, shift, 0, ref removal, ref add, ref removed);
                Remove(ref region, index - region.StrideX, entry.y, shift, 0, ref removal, ref add, ref removed);
                Remove(ref region, index + ChunkHeight, entry.y, shift, 0, ref removal, ref add, ref removed);
                Remove(ref region, index - ChunkHeight, entry.y, shift, 0, ref removal, ref add, ref removed);
                if (y < ChunkHeight - 1) Remove(ref region, index + 1, entry.y, shift, 0, ref removal, ref add, ref removed);
                if (y > 0) Remove(ref region, index - 1, entry.y, shift, sky, ref removal, ref add, ref removed);
            }

            // The edited voxel's neighbours light it (and through it) if it became clearer.
            int py = p & (ChunkHeight - 1);
            add.Enqueue(p + region.StrideX);
            add.Enqueue(p - region.StrideX);
            add.Enqueue(p + ChunkHeight);
            add.Enqueue(p - ChunkHeight);
            if (py < ChunkHeight - 1) add.Enqueue(p + 1);
            if (py > 0) add.Enqueue(p - 1);

            // Own sources of the cleared voxels.
            for (int i = 0; i < removed.Length; i++){
                int index = removed[i];
                int source = 0;
                if (emission.TryGetValue(index, out ushort e)) source = (e >> shift) & 0xF;
                if (sky != 0 && (index & (ChunkHeight - 1)) == ChunkHeight - 1){
                    int att = region.Att[index];
                    if (att < LightRegion.Opaque) source = att == 0 ? MaxLightLevel : MaxLightLevel - 1 - att;
                }

                if (source <= Level(region, index, shift)) continue;
                SetLevel(ref region, index, shift, source);
                add.Enqueue(index);
            }

            while (add.TryDequeue(out int index)){
                int level = Level(region, index, shift);
                if (level == 0) continue;
                int y = index & (ChunkHeight - 1);
                Spread(ref region, index + region.StrideX, level, shift, 0, ref add);
                Spread(ref region, index - region.StrideX, level, shift, 0, ref add);
                Spread(ref region, index + ChunkHeight, level, shift, 0, ref add);
                Spread(ref region, index - ChunkHeight, level, shift, 0, ref add);
                if (y < ChunkHeight - 1) Spread(ref region, index + 1, level, shift, 0, ref add);
                if (y > 0) Spread(ref region, index - 1, level, shift, sky, ref add);
            }
        }

        private static void Remove(ref LightRegion region, int index, int removedLevel, int shift, int skyDownward,
            ref NativeQueue<int2> removal, ref NativeQueue<int> add, ref NativeList<int> removed){
            int level = Level(region, index, shift);
            if (level == 0) return;

            if (level < removedLevel || (skyDownward != 0 && removedLevel == MaxLightLevel && level == MaxLightLevel)){
                SetLevel(ref region, index, shift, 0);
                removal.Enqueue(new int2(index, level));
                removed.Add(index);
                return;
            }

            add.Enqueue(index);
        }

        private static void Spread(ref LightRegion region, int index, int level, int shift, int skyDownward,
            ref NativeQueue<int> add){
            int att = region.Att[index];
            if (att >= LightRegion.Opaque) return;
            int candidate = skyDownward != 0 && att == 0 && level == MaxLightLevel ? MaxLightLevel : level - 1 - att;
            if (candidate <= Level(region, index, shift)) return;
            SetLevel(ref region, index, shift, candidate);
            add.Enqueue(index);
        }

        private static int Level(in LightRegion region, int index, int shift){
            return (region.Light[index] >> shift) & 0xF;
        }

        private static void SetLevel(ref LightRegion region, int index, int shift, int level){
            region.Light[index] = (ushort)((region.Light[index] & ~(0xF << shift)) | (level << shift));
        }

        /// <summary>Flags changed partitions (with their ±1 neighbourhood) and rebuilds the changed chunks' lists.</summary>
        private void WriteBack(ref LightRegion region, in NativeArray<ushort> original){
            NativeArray<byte> columnChanged = new(region.SizeX * region.SizeZ, Allocator.Temp);
            bool any = false;

            for (int x = 1; x < region.SizeX - 1; x++)
            for (int z = 1; z < region.SizeZ - 1; z++){
                int column = x * region.SizeZ + z;
                int baseIndex = column * ChunkHeight;
                for (int y = 0; y < ChunkHeight; y++){
                    if (region.Light[baseIndex + y] == original[baseIndex + y]) continue;
                    columnChanged[column] = 1;
                    any = true;
                    FlagPartitions(region.Origin + new int2(x, z), y);
                }
            }

            if (any)
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++){
                    int k = ChunkIndex(new int2(dx, dz));
                    int2 chunkOrigin = (CenterChunk + new int2(dx, dz)) * ChunkSizeXZ;
                    ChunkLightData old = Lights[k];
                    if (!RebuildChunk(ref region, old.Data, chunkOrigin, columnChanged, out ChunkLightData rebuilt))
                        continue;
                    old.Dispose();
                    Lights[k] = rebuilt;
                }

            columnChanged.Dispose();
        }

        private void FlagPartitions(int2 worldXZ, int y){
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int2 chunk = new(
                    VoxelUtils.FloorDivide(worldXZ.x + dx, ChunkWidth),
                    VoxelUtils.FloorDivide(worldXZ.y + dz, ChunkDepth)
                );
                int2 offset = chunk - CenterChunk;
                if (math.any(math.abs(offset) > 1)) continue;
                int k = ChunkIndex(offset);
                for (int dy = -1; dy <= 1; dy++){
                    int ny = y + dy;
                    if (ny is < 0 or >= ChunkHeight) continue;
                    ChangedPartitions[k * PartitionsPerChunk + ny / PartitionHeight] = 1;
                }
            }
        }

        /// <summary>
        ///     New light list for one chunk: changed box columns come from the box, all others are copied from the
        ///     old list run by run. Returns false if none of the chunk's columns changed.
        /// </summary>
        private static bool RebuildChunk(ref LightRegion region, in UnsafeIntervalList<ushort> old, int2 chunkOrigin,
            in NativeArray<byte> columnChanged, out ChunkLightData rebuilt){
            region.GetColumnRange(chunkOrigin, out int2 min, out int2 max, out int2 offset);
            rebuilt = default;
            bool changed = false;
            for (int cx = min.x; cx <= max.x && !changed; cx++)
            for (int cz = min.y; cz <= max.y; cz++){
                if (columnChanged[(cx + offset.x) * region.SizeZ + cz + offset.y] == 0) continue;
                changed = true;
                break;
            }

            if (!changed) return false;

            rebuilt = ChunkLightData.CreateEmpty(math.max(old.CompressedLength, 64));
            int nodeIndex = 0;
            int position = 0; // decompressed index of the next value to copy from the old list

            for (int cx = 0; cx < ChunkWidth; cx++)
            for (int cz = 0; cz < ChunkDepth; cz++){
                int columnStart = (cx * ChunkDepth + cz) * ChunkHeight;
                bool inBox = cx >= min.x && cx <= max.x && cz >= min.y && cz <= max.y;
                if (!inBox || columnChanged[(cx + offset.x) * region.SizeZ + cz + offset.y] == 0) continue;

                // Copy the old runs up to this column, then the column from the box.
                CopyRuns(old, ref nodeIndex, ref position, columnStart, ref rebuilt);
                int baseIndex = region.ColumnBase(cx + offset.x, cz + offset.y);
                for (int y = 0; y < ChunkHeight; y++) rebuilt.AddLights(region.Light[baseIndex + y], 1);
                position = columnStart + ChunkHeight;
            }

            CopyRuns(old, ref nodeIndex, ref position, VoxelsPerChunk, ref rebuilt);
            return true;
        }

        private static void CopyRuns(in UnsafeIntervalList<ushort> nodes, ref int nodeIndex,
            ref int position, int end, ref ChunkLightData output){
            while (nodeIndex < nodes.CompressedLength && nodes.NodeAt(nodeIndex).Count <= position) nodeIndex++;
            while (position < end){
                UnsafeIntervalList<ushort>.Node node = nodes.NodeAt(nodeIndex);
                int runEnd = math.min(node.Count, end);
                output.AddLights(node.Value, runEnd - position);
                position = runEnd;
                if (runEnd == node.Count) nodeIndex++;
            }
        }
    }
}
