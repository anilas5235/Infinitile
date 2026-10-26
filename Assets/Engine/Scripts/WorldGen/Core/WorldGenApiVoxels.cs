using System;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>Direction a vertical slice runs in.</summary>
    public enum SliceAxis{
        /// <summary>Along x at a fixed z: an XY slice.</summary>
        X,

        /// <summary>Along z at a fixed x: a ZY slice.</summary>
        Z
    }

    public static partial class WorldGenApi{
        /// <summary>
        ///     Schedules the final voxel ids of a vertical slice, the same ids the chunk job writes: terrain, ores,
        ///     caves, lava, bedrock and, with <paramref name="flora" />, the vegetation. Both run the same passes on
        ///     chunk-sized blocks of the world. Along <paramref name="axis" /> the slice has the request's samples
        ///     (<c>Size.x</c> from <c>Origin.x</c> for <see cref="SliceAxis.X" />, <c>Size.y</c> from <c>Origin.y</c>
        ///     for <see cref="SliceAxis.Z" />, every <c>Stride</c> columns); the other horizontal coordinate is
        ///     <paramref name="coordinate" />. Element [(y − yMin) × width + i] is sample i at height y.
        /// </summary>
        /// <exception cref="ArgumentException">
        ///     The size is not positive, the y range lies outside the chunk height, or the output is too short.
        /// </exception>
        public static JobHandle SampleSlice(in WorldGenRequest req, SliceAxis axis, int coordinate, int yMin, int yMax,
            ref NativeArray<ushort> outVoxels, bool flora = true, JobHandle dependsOn = default){
            if (!req.Config.IsCreated)
                throw new ArgumentException("WorldGenRequest.Config is not created (build it with WorldGenSettings.ToConfig).", nameof(req));
            int width = axis == SliceAxis.X ? req.Size.x : req.Size.y;
            if (width <= 0) throw new ArgumentException($"Slice width must be positive, got {width}.", nameof(req));
            if (yMin < 0 || yMax >= ChunkHeight || yMin > yMax)
                throw new ArgumentException($"The y range must lie within 0…{ChunkHeight - 1}, got {yMin}…{yMax}.", nameof(yMin));
            int count = width * (yMax - yMin + 1);
            if (!outVoxels.IsCreated || outVoxels.Length < count)
                throw new ArgumentException(
                    $"outVoxels holds {(outVoxels.IsCreated ? outVoxels.Length : 0)} elements, the slice needs {count}.", nameof(outVoxels));

            int stride = math.max(1, req.Stride);
            // Samples whose columns fit into one block share its generation.
            int perBlock = (ChunkWidth - 1) / stride + 1;
            SliceJob job = new(){
                Config = req.Config,
                Axis = axis,
                Coordinate = coordinate,
                Start = axis == SliceAxis.X ? req.Origin.x : req.Origin.y,
                Stride = stride,
                Width = width,
                PerBlock = perBlock,
                YMin = yMin,
                YMax = yMax,
                Flora = flora,
                Voxels = outVoxels
            };
            return job.Schedule((width + perBlock - 1) / perBlock, 1, dependsOn);
        }

        /// <summary>The cave volume job of <see cref="SampleRegion" />: one chunk-sized block per index.</summary>
        private static JobHandle ScheduleCaveVolume(in WorldGenRequest request, NativeArray<int> volume, JobHandle dependsOn){
            CaveVolumeJob job = new(){
                Config = request.Config,
                Origin = request.Origin,
                Size = request.Size,
                BlocksX = (request.Size.x + ChunkWidth - 1) / ChunkWidth,
                Volume = volume
            };
            int blocks = job.BlocksX * ((request.Size.y + ChunkDepth - 1) / ChunkDepth);
            return job.Schedule(blocks, 1, dependsOn);
        }

        /// <summary>
        ///     Generates one chunk-sized block per index and copies the slice samples inside it. Strict float mode, like
        ///     the chunk job, so both produce the same voxels.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct SliceJob : IJobParallelFor{
            [ReadOnly] public WorldGenConfig Config;
            public SliceAxis Axis;
            public int Coordinate;
            public int Start;
            public int Stride;
            public int Width;
            public int PerBlock;
            public int YMin;
            public int YMax;
            public bool Flora;

            // Blocks write disjoint sample columns.
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<ushort> Voxels;

            public void Execute(int block){
                int first = block * PerBlock;
                int last = math.min(first + PerBlock, Width) - 1;
                int along = Start + first * Stride;
                int3 origin = Axis == SliceAxis.X ? new int3(along, 0, Coordinate) : new int3(Coordinate, 0, along);

                NativeArray<ushort> vox = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                NativeArray<ChunkColumn> columns = new(ChunkWidth * ChunkDepth, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                GeneratorConfig config = new(){ WorldGen = Config };
                ChunkJob.GenerateVoxels(ref config, origin, vox, columns, Flora);

                for (int i = first; i <= last; i++){
                    int offset = (i - first) * Stride;
                    int x = Axis == SliceAxis.X ? offset : 0;
                    int z = Axis == SliceAxis.X ? 0 : offset;
                    for (int y = YMin; y <= YMax; y++)
                        Voxels[(y - YMin) * Width + i] = vox[ChunkSize.Flatten(x, y, z)];
                }

                vox.Dispose();
                columns.Dispose();
            }
        }

        /// <summary>
        ///     Generates one chunk-sized block of the region per index, without flora, and counts the carved voxels of
        ///     every column inside the region.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct CaveVolumeJob : IJobParallelFor{
            [ReadOnly] public WorldGenConfig Config;
            public int2 Origin;
            public int2 Size;
            public int BlocksX;

            // Blocks write disjoint columns.
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Volume;

            public void Execute(int block){
                int2 first = new int2(block % BlocksX, block / BlocksX) * new int2(ChunkWidth, ChunkDepth);
                int2 last = math.min(first + new int2(ChunkWidth, ChunkDepth), Size) - 1;

                NativeArray<ushort> vox = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                NativeArray<ChunkColumn> columns = new(ChunkWidth * ChunkDepth, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                GeneratorConfig config = new(){ WorldGen = Config };
                ChunkJob.GenerateVoxels(ref config, new int3(Origin.x + first.x, 0, Origin.y + first.y), vox, columns, false);

                ushort lava = Config.Caves.LavaVoxel;
                for (int j = first.y; j <= last.y; j++)
                for (int i = first.x; i <= last.x; i++){
                    int x = i - first.x, z = j - first.y;
                    int top = math.min(columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)].Height, ChunkHeight - 1);
                    int carved = 0;
                    for (int y = 0; y <= top; y++){
                        ushort v = vox[ChunkSize.Flatten(x, y, z)];
                        if (v == 0 || (lava != 0 && v == lava)) carved++;
                    }

                    Volume[j * Size.x + i] = carved;
                }

                vox.Dispose();
                columns.Dispose();
            }
        }
    }
}
