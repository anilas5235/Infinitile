using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Engine.Scripts.Render;
using Engine.Scripts.Render.Post;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     GPU round trip of the partition build (<see cref="PointBuilderHandler" />: decompress kernels and
    ///     BuildPoints) over a 3 × 3 chunk neighbourhood of the Standard package's voxels. The points of each layer are
    ///     sorted (the kernel appends them in thread order) and hashed against values recorded with the kernels that
    ///     decompressed the whole chunks.
    /// </summary>
    public class PointBuildTests{
        /// <summary>Base voxels (air included) of the package when the values below were recorded.</summary>
        private const int RecordedBaseCount = 63;

        private GameObject _host;
        private DataImporter _importer;

        [SetUp]
        public void SetUp(){
            _host = new GameObject("PointBuildTests");
            _host.SetActive(false); // Awake must not run: the test loads the voxels itself
            _importer = _host.AddComponent<DataImporter>();

            VoxelDataPackage[] packages = Resources.LoadAll<VoxelDataPackage>("VoxelDataPackages");
            Assert.IsNotEmpty(packages, "no VoxelDataPackage in Resources/VoxelDataPackages");
            Texture2D texError = Resources.Load<Texture2D>("Artwork/TexError");
            Texture2D texErrorT = Resources.Load<Texture2D>("Artwork/TexErrorT");
            _importer.VoxelRegistry.Initialize(texError, texErrorT, texErrorT);
            foreach (VoxelDataPackage package in packages)
            foreach (Voxel voxel in package.voxel.Where(v => v))
                _importer.VoxelRegistry.Register(package.packagePrefix, voxel);
            _importer.VoxelRegistry.FinalizeRegistry();
        }

        [TearDown]
        public void TearDown(){
            _importer.VoxelRegistry.Dispose();
            Object.DestroyImmediate(_host);
        }

        [TestCase(0, 2953, 0, 0, 1102910112564121940UL)] // cave pockets on the world bottom
        [TestCase(3, 12063, 1292, 438, 5431716569978463381UL)] // surface, water and every voxel type
        [TestCase(7, 5737, 0, 0, 16801187266041425973UL)] // floating blocks under the world top
        public void BuildPoints_MatchesRecordedPoints(int partitionY, int solid, int transparent, int foliage,
            ulong hash){
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Engine/Shaders/Urp/ComputeShaders/PointBuilder.compute");
            Assert.IsNotNull(shader, "PointBuilder.compute not found");
            Assert.AreEqual(Marshal.SizeOf<Vertex>(), Marshal.SizeOf<RawPoint>());

            VoxelRegistry registry = _importer.VoxelRegistry;
            // The base voxels the values were recorded with (air included), so voxels appended to the package later
            // and turned variants leave the recorded world unchanged
            Assert.GreaterOrEqual(registry.BaseCount, RecordedBaseCount);
            NativeArray<Voxel.VoxelDef> defs = registry.GetVoxelGenData().VoxelRenderDefs.GetSubArray(0, RecordedBaseCount);
            ushort[] solids = Ids(defs, d => d.MeshLayer == MeshLayer.Solid);
            ushort[] liquids = Ids(defs, d => d.MeshLayer == MeshLayer.Transparent);
            ushort[] all = Ids(defs, d => d.MeshLayer != MeshLayer.Air);
            Assert.GreaterOrEqual(solids.Length, 3);
            Assert.IsNotEmpty(liquids);

            ChunkIntervalStore store = new(64, 32, shader);
            PointBuilderHandler handler = new(shader, registry.VoxelRenderDefBuffer, registry.QuadTexPairBuffer,
                registry.QuadBuffer);
            Dictionary<int2, IntervalRange> voxelLists = new(), lightLists = new();
            try{
                // The main chunk is not at the origin, so world and chunk coordinates differ
                int2 mainChunk = new(3, -2);
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++){
                    int2 chunk = mainChunk + new int2(dx, dz);
                    IntervalRange voxels = default, light = default;
                    Upload(store, ref voxels, ChunkValues(chunk, (x, y, z) => Voxel(x, y, z, solids, liquids, all)));
                    Upload(store, ref light, ChunkValues(chunk, Light));
                    voxelLists[chunk] = voxels;
                    lightLists[chunk] = light;
                }

                PartitionBuildRequest request = new(new int3(mainChunk.x, partitionY, mainChunk.y));
                request.CollectLists(voxelLists, lightLists);
                Assert.IsTrue(request.IsValid);

                handler.BuildPoints(request, store);
                AsyncGPUReadback.WaitAllRequests();
                Assert.IsTrue(handler.TryCompleteBuild(out int[] counts));

                GraphicsBuffer[] outputs = { handler.SolidPointsOut, handler.TransparentPointsOut, handler.FoliagePointsOut };
                ulong actual = 14695981039346656037UL;
                for (int layer = 0; layer < 3; layer++){
                    Assert.Less(counts[layer], MaxPointsPerPartition, $"layer {layer} overflowed");
                    actual = Fnv(actual, counts[layer]);
                    foreach (uint word in SortedPoints(outputs[layer], counts[layer])) actual = Fnv(actual, (int)word);
                }

                // One string, so a failure shows every recorded value at once
                Assert.AreEqual($"{solid} {transparent} {foliage} {hash}",
                    $"{counts[0]} {counts[1]} {counts[2]} {actual}", "solid, transparent, foliage points and hash");
            }
            finally{
                handler.Dispose();
                store.Dispose();
            }
        }

        /// <summary>
        ///     The face rules of the see-through blocks: one face between two cut-out leaf blocks (of any species), none
        ///     towards an opaque block or the same see-through block, both towards a different see-through block (water
        ///     against leaves or glass). Leaf points carry the cut-out flag, the top layer of a liquid the liquid-top flag
        ///     (unless a full opaque block lies on it).
        /// </summary>
        [Test]
        public void BuildPoints_SeeThroughFaceRules(){
            NativeArray<Voxel.VoxelDef> defs = _importer.VoxelRegistry.GetVoxelGenData().VoxelRenderDefs;
            ushort[] leaves = Ids(defs, d => d.AlphaCutout);
            ushort[] liquids = Ids(defs, d => d.Liquid && d.MeshLayer == MeshLayer.Transparent);
            ushort[] glass = Ids(defs, d => d.MeshLayer == MeshLayer.Transparent && !d.AlphaCutout && !d.Liquid);
            ushort[] stone = Ids(defs, d => d.MeshLayer == MeshLayer.Solid && d.HidesNeighbourFaces &&
                                            !d.AlwaysRenderAllFaces);
            Assert.GreaterOrEqual(leaves.Length, 2, "two leaf species");
            Assert.IsNotEmpty(liquids);
            Assert.IsNotEmpty(glass);
            Assert.IsNotEmpty(stone);
            ushort leaf = leaves[0], otherLeaf = leaves[1], water = liquids[0], pane = glass[0], solid = stone[0];

            // One cube block draws six faces
            Assert.AreEqual(6, BuildBlocks((0, 0, 0, leaf)).Length);

            // Leaves: one face per shared side, along every axis and between species
            Assert.AreEqual(11, BuildBlocks((0, 0, 0, leaf), (1, 0, 0, leaf)).Length, "leaves along x");
            Assert.AreEqual(11, BuildBlocks((0, 0, 0, leaf), (0, 1, 0, leaf)).Length, "leaves along y");
            Assert.AreEqual(11, BuildBlocks((0, 0, 0, leaf), (0, 0, 1, leaf)).Length, "leaves along z");
            Assert.AreEqual(11, BuildBlocks((0, 0, 0, leaf), (1, 0, 0, otherLeaf)).Length, "two species");
            RawPoint[] crown = BuildBlocks((0, 0, 0, leaf), (1, 0, 0, leaf), (0, 0, 1, leaf), (1, 0, 1, leaf));
            Assert.AreEqual(4 * 6 - 4, crown.Length, "2 × 2 leaves");
            Assert.IsTrue(crown.All(p => p.Cutout), "leaf points are cut out");

            // An opaque block hides the leaf face; its own face stays (in the solid layer)
            Assert.AreEqual(5, BuildBlocks((0, 0, 0, leaf), (1, 0, 0, solid)).Length, "leaf against stone");

            // The same plain see-through block culls, a different one keeps both faces
            Assert.AreEqual(10, BuildBlocks((0, 0, 0, pane), (1, 0, 0, pane)).Length, "glass against glass");
            Assert.AreEqual(12, BuildBlocks((0, 0, 0, water), (1, 0, 0, pane)).Length, "water against glass");
            RawPoint[] pond = BuildBlocks((0, 0, 0, water), (1, 0, 0, leaf));
            Assert.AreEqual(12, pond.Length, "water against leaves");
            Assert.AreEqual(6, pond.Count(p => p.Cutout), "only the leaf points are cut out");

            // Liquid top: the top layer only, also under leaves, not under a full opaque block
            RawPoint[] column = BuildBlocks((0, 0, 0, water), (0, 1, 0, water));
            Assert.AreEqual(10, column.Length, "water on water");
            float top = column.Max(p => p.Y);
            Assert.IsTrue(column.All(p => p.LiquidTop == (p.Y == top)), "only the upper water block is the top");
            Assert.IsTrue(BuildBlocks((0, 0, 0, water), (0, 1, 0, leaf)).Where(p => !p.Cutout).All(p => p.LiquidTop),
                "water under leaves");
            RawPoint[] covered = BuildBlocks((0, 0, 0, water), (0, 1, 0, solid));
            Assert.AreEqual(5, covered.Length, "water under stone");
            Assert.IsFalse(covered.Any(p => p.LiquidTop), "water under stone");
        }

        /// <summary>
        ///     The camera's liquid-top test (<see cref="VoxelPostProcessHandler.IsLiquidTop" />, which lowers the
        ///     underwater fog's surface) agrees with the drawn surface (PointBuilder.compute, is_liquid_top): water under
        ///     a slab or another partial solid shape is a liquid top, water under a full solid block is not.
        /// </summary>
        [Test]
        public void LiquidTop_CameraMatchesDrawnSurface(){
            VoxelRegistry registry = _importer.VoxelRegistry;
            NativeArray<Voxel.VoxelDef> defs = registry.GetVoxelGenData().VoxelRenderDefs.GetSubArray(0, registry.BaseCount);
            ushort water = Ids(defs, d => d.Liquid && d.MeshLayer == MeshLayer.Transparent)[0];
            ushort[] partial = Ids(defs, d => d.MeshLayer == MeshLayer.Solid && !d.HidesNeighbourFaces);
            ushort[] full = Ids(defs, d => d.MeshLayer == MeshLayer.Solid && d.HidesNeighbourFaces);
            ushort[] seeThrough = Ids(defs, d => d.MeshLayer == MeshLayer.Transparent && !d.Liquid);
            Assert.IsNotEmpty(partial, "a partial solid shape (slab)");
            Assert.IsNotEmpty(full);
            Assert.IsNotEmpty(seeThrough);

            Assert.IsTrue(VoxelPostProcessHandler.IsLiquidTop(registry, water, 0), "water under air");
            Assert.IsFalse(VoxelPostProcessHandler.IsLiquidTop(registry, water, water), "water under water");
            Assert.IsFalse(VoxelPostProcessHandler.IsLiquidTop(registry, full[0], 0), "stone is no liquid");

            foreach (ushort above in new[]{ (ushort)0, partial[0], full[0], seeThrough[0] }){
                bool drawn = BuildBlocks((0, 0, 0, water), (0, 1, 0, above)).Any(p => p.LiquidTop);
                Assert.AreEqual(drawn, VoxelPostProcessHandler.IsLiquidTop(registry, water, above),
                    $"water under voxel {above} ({defs[above].MeshLayer})");
            }

            foreach (ushort above in partial)
                Assert.IsTrue(VoxelPostProcessHandler.IsLiquidTop(registry, water, above),
                    $"water under the partial solid voxel {above}");
            foreach (ushort above in full)
                Assert.IsFalse(VoxelPostProcessHandler.IsLiquidTop(registry, water, above),
                    $"water under the full solid voxel {above}");
        }

        /// <summary>
        ///     The GPU render definition of every id carries its base voxel (PointBuilder.compute compares base voxels,
        ///     so turned variants of one see-through block or liquid count as the same block).
        /// </summary>
        [Test]
        public void GpuVoxelDefs_CarryTheBaseVoxel(){
            VoxelRegistry registry = _importer.VoxelRegistry;
            GraphicsBuffer buffer = registry.VoxelRenderDefBuffer;
            Assert.AreEqual(100, buffer.stride, "GPUVoxelDef stride");
            const int words = 25, baseIdWord = 24;
            uint[] data = new uint[buffer.count * words];
            buffer.GetData(data);
            Assert.Greater(buffer.count, registry.BaseCount, "the package has turned variants");
            for (int id = 0; id < buffer.count; id++)
                Assert.AreEqual((uint)registry.BaseOf((ushort)id), data[id * words + baseIdWord], $"voxel {id}");
        }

        /// <summary>
        ///     The transparent points of partition 3 of an air world holding only <paramref name="blocks" />, given
        ///     relative to a position inside the main chunk.
        /// </summary>
        private RawPoint[] BuildBlocks(params (int x, int y, int z, ushort id)[] blocks){
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Engine/Shaders/Urp/ComputeShaders/PointBuilder.compute");
            Assert.IsNotNull(shader, "PointBuilder.compute not found");

            const int partitionY = 3;
            int3 origin = new(10, partitionY * PartitionHeight + 10, 10);
            ushort[] air = new ushort[VoxelsPerChunk];
            ushort[] light = Enumerable.Repeat((ushort)0xF000, VoxelsPerChunk).ToArray();
            ushort[] main = new ushort[VoxelsPerChunk];
            foreach ((int x, int y, int z, ushort id) in blocks)
                main[((origin.x + x) * ChunkDepth + origin.z + z) * ChunkHeight + origin.y + y] = id;

            ChunkIntervalStore store = new(64, 32, shader);
            PointBuilderHandler handler = new(shader, _importer.VoxelRegistry.VoxelRenderDefBuffer,
                _importer.VoxelRegistry.QuadTexPairBuffer, _importer.VoxelRegistry.QuadBuffer);
            Dictionary<int2, IntervalRange> voxelLists = new(), lightLists = new();
            try{
                int2 mainChunk = new(3, -2);
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++){
                    int2 chunk = mainChunk + new int2(dx, dz);
                    IntervalRange voxels = default, lightRange = default;
                    Upload(store, ref voxels, dx == 0 && dz == 0 ? main : air);
                    Upload(store, ref lightRange, light);
                    voxelLists[chunk] = voxels;
                    lightLists[chunk] = lightRange;
                }

                PartitionBuildRequest request = new(new int3(mainChunk.x, partitionY, mainChunk.y));
                request.CollectLists(voxelLists, lightLists);
                Assert.IsTrue(request.IsValid);

                handler.BuildPoints(request, store);
                AsyncGPUReadback.WaitAllRequests();
                Assert.IsTrue(handler.TryCompleteBuild(out int[] counts));

                RawPoint[] points = new RawPoint[counts[1]];
                if (counts[1] > 0) handler.TransparentPointsOut.GetData(points, 0, 0, counts[1]);
                return points;
            }
            finally{
                handler.Dispose();
                store.Dispose();
            }
        }

        #region world

        private static uint Hash(int x, int y, int z){
            uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)z * 0xCB1AB31Fu;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }

        /// <summary>
        ///     Surface near y = 100 with water up to 104 and scattered blocks of every voxel type above it, cave
        ///     pockets at the world bottom and floating blocks at its top (so the y = 0 and y = 255 borders matter).
        /// </summary>
        private static ushort Voxel(int x, int y, int z, ushort[] solids, ushort[] liquids, ushort[] all){
            int height = 96 + (int)(Hash(x, 0, z) % 12);
            uint h = Hash(x, y, z);
            if (y < 3) return h % 7 == 0 ? (ushort)0 : solids[0];
            if (y >= 250) return h % 5 == 0 ? solids[2] : (ushort)0;
            if (y < height - 3) return solids[0];
            if (y < height) return solids[1];
            if (y == height) return solids[2];
            if (y <= height + 20 && h % 23 == 0) return all[h / 23 % (uint)all.Length];
            if (y <= 104) return liquids[0];
            return 0;
        }

        /// <summary>Light in runs of 4 voxels along y: every channel varies, so the smooth light sees each one.</summary>
        private static ushort Light(int x, int y, int z){
            return (ushort)Hash(x, y >> 2, z);
        }

        private static ushort[] ChunkValues(int2 chunk, Func<int, int, int, ushort> value){
            ushort[] values = new ushort[VoxelsPerChunk];
            int i = 0;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 0; y < ChunkHeight; y++)
                values[i++] = value(chunk.x * ChunkWidth + x, y, chunk.y * ChunkDepth + z);
            return values;
        }

        private static ushort[] Ids(NativeArray<Voxel.VoxelDef> defs, Func<Voxel.VoxelDef, bool> filter){
            List<ushort> ids = new();
            for (int i = 1; i < defs.Length; i++)
                if (filter(defs[i]))
                    ids.Add((ushort)i);
            return ids.ToArray();
        }

        #endregion

        private static void Upload(ChunkIntervalStore store, ref IntervalRange range, ushort[] values){
            UnsafeIntervalList<ushort> list = new(64, Allocator.Temp);
            int start = 0;
            for (int v = 1; v <= values.Length; v++){
                if (v < values.Length && values[v] == values[start]) continue;
                list.AddInterval(values[start], v - start);
                start = v;
            }

            store.Upload(ref range, list);
            list.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawPoint : IComparable<RawPoint>{
            public uint A, B, C, D, E, F, G;

            /// <summary>The voxel's y in the partition (PointData.position.y).</summary>
            public float Y => math.asfloat(B);

            /// <summary>packed.z bit 14 (VoxelCommon.hlsl).</summary>
            public bool Cutout => (F >> 14 & 1u) != 0;

            /// <summary>packed.z bit 15 (VoxelCommon.hlsl).</summary>
            public bool LiquidTop => (F >> 15 & 1u) != 0;

            public int CompareTo(RawPoint other){
                int c = A.CompareTo(other.A);
                if (c == 0) c = B.CompareTo(other.B);
                if (c == 0) c = C.CompareTo(other.C);
                if (c == 0) c = D.CompareTo(other.D);
                if (c == 0) c = E.CompareTo(other.E);
                if (c == 0) c = F.CompareTo(other.F);
                if (c == 0) c = G.CompareTo(other.G);
                return c;
            }

            public IEnumerable<uint> Words(){
                yield return A;
                yield return B;
                yield return C;
                yield return D;
                yield return E;
                yield return F;
                yield return G;
            }
        }

        /// <summary>The first <paramref name="count" /> points of a layer, sorted word by word.</summary>
        private static IEnumerable<uint> SortedPoints(GraphicsBuffer buffer, int count){
            RawPoint[] points = new RawPoint[count];
            if (count > 0) buffer.GetData(points, 0, 0, count);
            Array.Sort(points);
            return points.SelectMany(p => p.Words());
        }

        private static ulong Fnv(ulong hash, int value){
            for (int b = 0; b < 4; b++){
                hash ^= (byte)(value >> (8 * b));
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }
}
