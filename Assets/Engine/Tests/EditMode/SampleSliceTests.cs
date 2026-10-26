using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using static Engine.Scripts.Utils.VoxelConstants;
using Voxel = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Slices and the cave volume of the region API against the chunk job, on the shipped settings with caves on
    ///     and a local voxel id table.
    /// </summary>
    public class SampleSliceTests{
        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";

        // Around the origin (mostly under water) and a stretch of land with flora around (216, 424).
        private static readonly int2[] Chunks = {
            new(-2, -1), new(-1, -1), new(0, -1), new(1, -1), new(0, -2), new(0, 0), new(6, 13), new(7, 13)
        };

        private readonly Dictionary<Voxel, ushort> _ids = new();
        private GeneratorConfig _config;
        private Dictionary<int2, ChunkVoxelData> _chunks;
        private NativeParallelHashMap<int2, ChunkVoxelData> _results;

        [OneTimeSetUp]
        public void OneTimeSetUp(){
            WorldGenSettings settings = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            Assert.IsNotNull(settings, ShippedSettingsPath);
            Assert.IsTrue(settings.Caves.Enabled, "the shipped settings have caves on");
            // Generation reads only the world-gen config; the job safety system still wants the voxel map constructed.
            _config = new GeneratorConfig{
                Voxels = new NativeHashMap<FixedString32Bytes, Voxel.VoxelDef>(1, Allocator.Persistent),
                WorldGen = settings.ToConfigWithVoxelIds(TestFixtures.Seed, Id)
            };
            Assert.AreNotEqual(0, _config.WorldGen.Caves.BedrockVoxel, "bedrock resolves through the id table");

            NativeList<int2> jobs = new(Chunks.Length, Allocator.TempJob);
            foreach (int2 chunk in Chunks) jobs.Add(chunk);
            _results = new NativeParallelHashMap<int2, ChunkVoxelData>(jobs.Length, Allocator.Persistent);
            try{
                new ChunkJob{ Jobs = jobs, Results = _results.AsParallelWriter(), Config = _config }.Run(jobs.Length);
            }
            finally{
                jobs.Dispose();
            }

            _chunks = new Dictionary<int2, ChunkVoxelData>();
            foreach (KeyValue<int2, ChunkVoxelData> kv in _results) _chunks.Add(kv.Key, kv.Value);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown(){
            foreach (KeyValue<int2, ChunkVoxelData> kv in _results) kv.Value.Dispose();
            _results.Dispose();
            _config.WorldGen.Dispose();
            _config.Voxels.Dispose();
        }

        private ushort Id(Voxel voxel){
            if (!_ids.TryGetValue(voxel, out ushort id)) _ids.Add(voxel, id = (ushort)(_ids.Count + 1));
            return id;
        }

        private ushort ChunkVoxel(int x, int y, int z){
            int2 chunk = new((int)math.floor(x / (float)ChunkWidth), (int)math.floor(z / (float)ChunkDepth));
            Assert.IsTrue(_chunks.ContainsKey(chunk), $"column ({x},{z}) lies outside the generated chunks");
            return _chunks[chunk].GetVoxel(new int3(x - chunk.x * ChunkWidth, y, z - chunk.y * ChunkDepth));
        }

        private ushort[] Slice(SliceAxis axis, int start, int width, int stride, int coordinate, int yMin, int yMax, bool flora){
            WorldGenRequest request = new(){
                Config = _config.WorldGen,
                Origin = axis == SliceAxis.X ? new int2(start, 0) : new int2(0, start),
                Size = axis == SliceAxis.X ? new int2(width, 1) : new int2(1, width),
                Stride = stride
            };
            NativeArray<ushort> voxels = new(width * (yMax - yMin + 1), Allocator.TempJob);
            try{
                WorldGenApi.SampleSlice(request, axis, coordinate, yMin, yMax, ref voxels, flora).Complete();
                return voxels.ToArray();
            }
            finally{
                voxels.Dispose();
            }
        }

        [TestCase(SliceAxis.X, -50, 96, 1, -7, 0, 255)]
        [TestCase(SliceAxis.Z, -40, 70, 1, 13, 60, 180)]
        [TestCase(SliceAxis.X, -50, 32, 3, -30, 0, 255)]
        [TestCase(SliceAxis.Z, -60, 12, 7, 2, 0, 140)]
        [TestCase(SliceAxis.X, 195, 55, 1, 430, 0, 255)]
        public void SampleSlice_EqualsChunkJob(SliceAxis axis, int start, int width, int stride, int coordinate, int yMin, int yMax){
            ushort[] slice = Slice(axis, start, width, stride, coordinate, yMin, yMax, true);
            int solid = 0;
            for (int i = 0; i < width; i++){
                int along = start + i * stride;
                int x = axis == SliceAxis.X ? along : coordinate;
                int z = axis == SliceAxis.X ? coordinate : along;
                for (int y = yMin; y <= yMax; y++){
                    ushort expected = ChunkVoxel(x, y, z);
                    if (expected != 0) solid++;
                    Assert.AreEqual(expected, slice[(y - yMin) * width + i], $"({x},{y},{z})");
                }
            }

            Assert.Greater(solid, 0, "the slice holds terrain");
        }

        [Test]
        public void SampleSlice_WithoutFlora_DiffersOnlyAboveTheSurface(){
            const int start = 192, width = 64, coordinate = 424;
            ushort[] slice = Slice(SliceAxis.X, start, width, 1, coordinate, 0, ChunkHeight - 1, false);
            int[] heights = ColumnHeights(new int2(start, coordinate), new int2(width, 1));
            int differing = 0;
            for (int i = 0; i < width; i++)
            for (int y = 0; y < ChunkHeight; y++){
                ushort chunk = ChunkVoxel(start + i, y, coordinate);
                ushort v = slice[y * width + i];
                if (v == chunk) continue;
                differing++;
                Assert.Greater(y, heights[i], $"({start + i},{y},{coordinate}) differs at or below the surface");
            }

            Assert.Greater(differing, 0, "the row holds flora, so leaving it out shows");
        }

        [Test]
        public void CaveVolume_MatchesCarvedVoxelsOfChunkJob(){
            int2 origin = new(-40, -30), size = new(64, 26);
            int[] heights = ColumnHeights(origin, size);
            NativeArray<int> volume = new(size.x * size.y, Allocator.TempJob);
            try{
                WorldGenRequest request = new(){ Config = _config.WorldGen, Origin = origin, Size = size, Stride = 1 };
                RegionBuffers buffers = new(){ CaveVolume = volume };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();

                ushort lava = _config.WorldGen.Caves.LavaVoxel;
                long total = 0;
                for (int j = 0; j < size.y; j++)
                for (int i = 0; i < size.x; i++){
                    int x = origin.x + i, z = origin.y + j;
                    int top = math.clamp(heights[j * size.x + i], 0, MaxTerrainHeight);
                    int carved = 0;
                    for (int y = 0; y <= top; y++){
                        ushort v = ChunkVoxel(x, y, z);
                        if (v == 0 || (lava != 0 && v == lava)) carved++;
                    }

                    Assert.AreEqual(carved, volume[j * size.x + i], $"column ({x},{z})");
                    total += carved;
                }

                TestContext.WriteLine($"{total} carved voxels over {size.x * size.y} columns");
            }
            finally{
                volume.Dispose();
            }
        }

        [Test]
        public void CaveVolume_RejectsStrideAboveOne(){
            NativeArray<int> volume = new(16, Allocator.TempJob);
            try{
                WorldGenRequest request = new(){ Config = _config.WorldGen, Origin = int2.zero, Size = new int2(4, 4), Stride = 2 };
                RegionBuffers buffers = new(){ CaveVolume = volume };
                Assert.Throws<ArgumentException>(() => WorldGenApi.SampleRegion(request, ref buffers));
            }
            finally{
                volume.Dispose();
            }
        }

        [Test]
        public void SampleSlice_RejectsBadRangeAndShortOutput(){
            WorldGenRequest request = new(){ Config = _config.WorldGen, Origin = int2.zero, Size = new int2(8, 1), Stride = 1 };
            NativeArray<ushort> voxels = new(8 * 10, Allocator.TempJob);
            try{
                Assert.Throws<ArgumentException>(() => WorldGenApi.SampleSlice(request, SliceAxis.X, 0, 10, 5, ref voxels));
                Assert.Throws<ArgumentException>(() => WorldGenApi.SampleSlice(request, SliceAxis.X, 0, 0, ChunkHeight, ref voxels));
                Assert.Throws<ArgumentException>(() => WorldGenApi.SampleSlice(request, SliceAxis.X, 0, 0, 10, ref voxels));
            }
            finally{
                voxels.Dispose();
            }
        }

        /// <summary>The region API's column heights, the surface before carving.</summary>
        private int[] ColumnHeights(int2 origin, int2 size){
            NativeArray<int> heights = new(size.x * size.y, Allocator.TempJob);
            try{
                WorldGenRequest request = new(){ Config = _config.WorldGen, Origin = origin, Size = size, Stride = 1 };
                RegionBuffers buffers = new(){ ColumnHeight = heights };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                return heights.ToArray();
            }
            finally{
                heights.Dispose();
            }
        }
    }
}
