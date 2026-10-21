using System;
using System.Runtime.CompilerServices;
using Engine.Scripts.Data;
using Engine.Scripts.Utils.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Light{
    /// <summary>
    ///     A full-height box of voxels for light propagation, decompressed from the chunks' interval
    ///     lists. Column (x, z) of the box starts at <c>x · StrideX + z · 256</c>, y is innermost (the chunk
    ///     Flatten order), so a step in y is ±1 and a y bound check is <c>index &amp; 255</c>. The outermost ring
    ///     of columns is a sentinel with attenuation 15: the BFS never enters it and needs no x/z bound checks.
    ///     Used by <see cref="LightJob" /> (chunk ± 14) and <see cref="LightEditJob" /> (edit ± 16).
    /// </summary>
    internal struct LightRegion : IDisposable{
        internal const int Opaque = MaxLightLevel;
        private const int Height = ChunkHeight;

        public readonly int SizeX;
        public readonly int SizeZ;
        public readonly int StrideX;

        /// <summary>World x/z of the box column (0, 0).</summary>
        public readonly int2 Origin;

        public NativeArray<byte> Att;
        public NativeArray<ushort> Light;

        /// <summary>Highest y with attenuation &gt; 0 per column (-1: the column is clear), filled with the voxels.</summary>
        public NativeArray<short> Top;

        public NativeList<int> Emitters;

        public LightRegion(int sizeX, int sizeZ, int2 origin, Allocator allocator){
            SizeX = sizeX;
            SizeZ = sizeZ;
            StrideX = sizeZ * Height;
            Origin = origin;
            int volume = sizeX * sizeZ * Height;
            Att = new NativeArray<byte>(volume, allocator, NativeArrayOptions.UninitializedMemory);
            Light = new NativeArray<ushort>(volume, allocator);
            Top = new NativeArray<short>(sizeX * sizeZ, allocator, NativeArrayOptions.UninitializedMemory);
            Emitters = new NativeList<int>(64, allocator);

            // Sentinel / missing chunks: opaque everywhere until a chunk fills its columns.
            unsafe{
                UnsafeUtility.MemSet(Att.GetUnsafePtr(), Opaque, volume);
            }

            for (int i = 0; i < Top.Length; i++) Top[i] = Height - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ColumnBase(int x, int z){
            return x * StrideX + z * Height;
        }

        /// <summary>Box column index (x · SizeZ + z) of a voxel index.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ColumnOf(int index){
            return index / Height;
        }

        /// <summary>
        ///     Fills the box columns covered by one chunk: attenuation, <see cref="Top" /> and emitters from its voxel
        ///     data. Columns of the chunk outside the box interior are skipped.
        /// </summary>
        public unsafe void FillVoxels(in UnsafeIntervalList<ushort> voxels, int2 chunkWorldOrigin,
            in VoxelLightTable table){
            GetColumnRange(chunkWorldOrigin, out int2 min, out int2 max, out int2 offset);
            byte* attPtr = (byte*)Att.GetUnsafePtr();
            int nextStart = -1;
            int lastNode = 0;
            int lastCount = 0;

            for (int cx = min.x; cx <= max.x; cx++)
            for (int cz = min.y; cz <= max.y; cz++){
                int chunkStart = (cx * ChunkDepth + cz) * Height;
                int baseIndex = ColumnBase(cx + offset.x, cz + offset.y);
                // A column that follows the previous one starts in its last node or the next one
                int nodeIndex = chunkStart == nextStart
                    ? lastCount > chunkStart ? lastNode : lastNode + 1
                    : voxels.NodeIndex(chunkStart);
                int top = -1;
                int y = 0;

                while (y < Height){
                    lastNode = nodeIndex;
                    UnsafeIntervalList<ushort>.Node node = voxels.NodeAt(nodeIndex++);
                    lastCount = node.Count;
                    int end = math.min(node.Count - chunkStart, Height);
                    ushort id = node.Value;
                    byte att = id < table.Count ? table.Attenuation[id] : (byte)Opaque;
                    ushort emission = id < table.Count ? table.Emission[id] : (ushort)0;

                    // Raw memset: the checked indexer keeps Burst from vectorising
                    UnsafeUtility.MemSet(attPtr + baseIndex + y, att, end - y);
                    if (att != 0) top = end - 1;
                    if (emission != 0)
                        for (int i = y; i < end; i++){
                            Light[baseIndex + i] = emission;
                            Emitters.Add(baseIndex + i);
                        }

                    y = end;
                }

                Top[baseIndex / Height] = (short)top;
                nextStart = chunkStart + Height;
            }
        }

        /// <summary>Copies the current light of the box columns covered by one chunk (edit jobs).</summary>
        public unsafe void FillLight(in UnsafeIntervalList<ushort> light, int2 chunkWorldOrigin){
            GetColumnRange(chunkWorldOrigin, out int2 min, out int2 max, out int2 offset);
            ushort* lightPtr = (ushort*)Light.GetUnsafePtr();

            for (int cx = min.x; cx <= max.x; cx++)
            for (int cz = min.y; cz <= max.y; cz++){
                int chunkStart = (cx * ChunkDepth + cz) * Height;
                int baseIndex = ColumnBase(cx + offset.x, cz + offset.y);
                int nodeIndex = light.NodeIndex(chunkStart);
                int y = 0;

                while (y < Height){
                    UnsafeIntervalList<ushort>.Node node = light.NodeAt(nodeIndex++);
                    int end = math.min(node.Count - chunkStart, Height);
                    ushort value = node.Value;
                    UnsafeUtility.MemCpyReplicate(lightPtr + baseIndex + y, &value, sizeof(ushort), end - y);
                    y = end;
                }
            }
        }

        /// <summary>
        ///     Chunk-local column range of a chunk that lies inside the box interior, and the offset from chunk-local
        ///     to box coordinates. The range is empty (min &gt; max) for a chunk outside the box.
        /// </summary>
        public void GetColumnRange(int2 chunkWorldOrigin, out int2 min, out int2 max, out int2 offset){
            offset = chunkWorldOrigin - Origin;
            min = math.max(0, 1 - offset);
            max = math.min(new int2(ChunkWidth - 1, ChunkDepth - 1), new int2(SizeX - 2, SizeZ - 2) - offset);
        }

        /// <summary>
        ///     Sky light of the box interior without BFS — every voxel above a column's
        ///     <see cref="Top" /> is 15 — then the frontier seeds: the first non-clear voxel under the sky part
        ///     (if light can enter it) and sky voxels beside a higher neighbour column that has non-opaque voxels at
        ///     that height. Columns without such voxels need no BFS.
        /// </summary>
        public unsafe void SeedSky(ref LightQueue queue){
            ushort* lightPtr = (ushort*)Light.GetUnsafePtr();
            for (int x = 1; x < SizeX - 1; x++)
            for (int z = 1; z < SizeZ - 1; z++){
                int column = x * SizeZ + z;
                int baseIndex = column * Height;
                int top = Top[column];
                ushort* sky = lightPtr + baseIndex; // unchecked so Burst vectorises the fill
                for (int y = top + 1; y < Height; y++) sky[y] |= VoxelLight.FullSky;

                if (top == Height - 1){
                    // The topmost voxel is not clear: it sees the sky above the world with one step of loss.
                    int index = baseIndex + top;
                    int att = Att[index];
                    if (att < Opaque){
                        Light[index] = VoxelLight.With(Light[index], VoxelLight.Sky, MaxLightLevel - 1 - att);
                        queue.Enqueue(index);
                    }

                    continue;
                }

                if (top >= 0 && Att[baseIndex + top] < Opaque) queue.Enqueue(baseIndex + top + 1);

                int maxNeighbourTop = math.max(
                    math.max(Top[column - SizeZ], Top[column + SizeZ]),
                    math.max(Top[column - 1], Top[column + 1])
                );

                for (int y = top + 1; y <= maxNeighbourTop; y++){
                    if (IsLightableNeighbour(column - SizeZ, y) || IsLightableNeighbour(column + SizeZ, y) ||
                        IsLightableNeighbour(column - 1, y) || IsLightableNeighbour(column + 1, y))
                        queue.Enqueue(baseIndex + y);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsLightableNeighbour(int column, int y){
            return Top[column] >= y && Att[column * Height + y] < Opaque;
        }

        /// <summary>Queues the emitters found by <see cref="FillVoxels" /> (their light holds the emission).</summary>
        public void SeedEmitters(ref LightQueue queue){
            for (int i = 0; i < Emitters.Length; i++) queue.Enqueue(Emitters[i]);
        }

        /// <summary>
        ///     BFS until the queue is empty. Each channel of a neighbour n becomes
        ///     max(L(n), L(v) − 1 − att(n)); opaque voxels are never entered; sky 15 goes down into a clear voxel
        ///     without loss. Light only rises, so a voxel queued more than once is still correct.
        ///     The four channels are combined at once in byte lanes (<see cref="Spread" />).
        /// </summary>
        public unsafe void Propagate(ref LightQueue queue){
            byte* att = (byte*)Att.GetUnsafeReadOnlyPtr();
            ushort* light = (ushort*)Light.GetUnsafePtr();
            int strideX = StrideX;
            while (queue.TryDequeue(out int index)){
                uint from = Spread(light[index]);
                // Candidate for a clear neighbour; most neighbours are clear, so it is computed once per voxel.
                uint clear = SubtractSaturated(from, 1);
                // Sky 15 steps down into a clear voxel without loss.
                uint clearDown = (from & SkyLane) == SkyLane ? clear | SkyLane : clear;
                if (clearDown == 0) continue; // every channel ≤ 1: nothing reaches a neighbour

                int y = index & (Height - 1);
                Relax(att, light, index + strideX, from, clear, ref queue);
                Relax(att, light, index - strideX, from, clear, ref queue);
                Relax(att, light, index + Height, from, clear, ref queue);
                Relax(att, light, index - Height, from, clear, ref queue);
                if (y < Height - 1) Relax(att, light, index + 1, from, clear, ref queue);
                if (y > 0) Relax(att, light, index - 1, from, clearDown, ref queue);
            }
        }

        /// <summary>Sky channel (bits 12–15) in the byte-lane layout of <see cref="Spread" />.</summary>
        private const uint SkyLane = 0x0Fu << (8 * VoxelLight.Sky);

        private const uint LaneGuard = 0x10101010u;
        private const uint LaneOnes = 0x01010101u;

        /// <summary>
        ///     One BFS step into <paramref name="index" />; unchecked pointers (the sentinel ring keeps it in bounds). <paramref name="clear" /> is the candidate for a clear voxel (attenuation 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Relax(byte* attPtr, ushort* lightPtr, int index, uint from, uint clear,
            ref LightQueue queue){
            int att = attPtr[index];
            if (att >= Opaque) return;
            uint candidate = att == 0 ? clear : SubtractSaturated(from, 1 + att);
            uint current = Spread(lightPtr[index]);
            uint higher = GreaterLanes(candidate, current);
            if (higher == 0) return;
            lightPtr[index] = Pack(current ^ ((candidate ^ current) & higher));
            queue.Enqueue(index);
        }

        /// <summary>
        ///     SWAR layout: the four 4-bit channels of a light value, one per byte lane (channel c in bits 8c…8c+3),
        ///     so bit 4 of each lane is free as a borrow guard.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Spread(ushort light){
            uint x = light;
            x = (x | (x << 8)) & 0x00FF00FFu;
            return (x | (x << 4)) & 0x0F0F0F0Fu;
        }

        /// <summary>Inverse of <see cref="Spread" />.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort Pack(uint lanes){
            uint x = (lanes | (lanes >> 4)) & 0x00FF00FFu;
            return (ushort)(x | (x >> 8));
        }

        /// <summary>Per lane max(lane − <paramref name="loss" />, 0) for loss in [0, 16].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint SubtractSaturated(uint lanes, int loss){
            // 16 + lane − loss ≥ 0 never borrows from the next lane; bit 4 tells whether lane ≥ loss.
            uint d = (lanes | LaneGuard) - (uint)loss * LaneOnes;
            uint keep = ((d >> 4) & LaneOnes) * 0x0Fu;
            return d & keep;
        }

        /// <summary>0x0F in every lane where <paramref name="a" /> is greater than <paramref name="b" />, else 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint GreaterLanes(uint a, uint b){
            // 16 + b − a has bit 4 clear exactly when a > b.
            uint d = (b | LaneGuard) - a;
            return ((~d >> 4) & LaneOnes) * 0x0Fu;
        }

        /// <summary>
        ///     Per channel max(current, from − 1 − att), with the lossless sky step downward. Scalar reference of
        ///     the byte-lane step in <see cref="Propagate" /> (kept for the tests).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort Combine(ushort current, ushort from, int att, int downward){
            int result = current;
            int loss = 1 + att;
            for (int c = 0; c < VoxelLight.ChannelCount; c++){
                int shift = 4 * c;
                int source = (from >> shift) & 0xF;
                int candidate = source - loss;
                if (c == VoxelLight.Sky && downward != 0 && att == 0 && source == MaxLightLevel)
                    candidate = MaxLightLevel;
                if (candidate > ((result >> shift) & 0xF)) result = (result & ~(0xF << shift)) | (candidate << shift);
            }

            return (ushort)result;
        }

        /// <summary>
        ///     Run-length encodes the box columns of one chunk (box x/z from <paramref name="boxMin" />, 32 × 32
        ///     columns) into <paramref name="output" /> in Flatten order.
        /// </summary>
        public unsafe void WriteChunk(int2 boxMin, ref ChunkLightData output){
            ushort* lightPtr = (ushort*)Light.GetUnsafeReadOnlyPtr();
            ushort run = lightPtr[ColumnBase(boxMin.x, boxMin.y)];
            int count = 0;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                ushort* column = lightPtr + ColumnBase(boxMin.x + x, boxMin.y + z);
                for (int y = 0; y < Height; y++){
                    ushort value = column[y];
                    if (value == run){
                        count++;
                        continue;
                    }

                    output.AddLights(run, count);
                    run = value;
                    count = 1;
                }
            }

            output.AddLights(run, count);
        }

        public void Dispose(){
            if (Att.IsCreated) Att.Dispose();
            if (Light.IsCreated) Light.Dispose();
            if (Top.IsCreated) Top.Dispose();
            if (Emitters.IsCreated) Emitters.Dispose();
        }
    }
}
