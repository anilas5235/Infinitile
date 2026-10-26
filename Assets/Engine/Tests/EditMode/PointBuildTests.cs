using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Engine.Scripts.Render;
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
        [TestCase(3, 12299, 1155, 380, 16766896623082871830UL)] // surface, water and every voxel type
        [TestCase(7, 5737, 0, 0, 16801187266041425973UL)] // floating blocks under the world top
        public void BuildPoints_MatchesRecordedPoints(int partitionY, int solid, int transparent, int foliage,
            ulong hash){
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Engine/Shaders/Urp/ComputeShaders/PointBuilder.compute");
            Assert.IsNotNull(shader, "PointBuilder.compute not found");
            Assert.AreEqual(Marshal.SizeOf<Vertex>(), Marshal.SizeOf<RawPoint>());

            VoxelRegistry registry = _importer.VoxelRegistry;
            // Base voxels only, so turned variants added to the package leave the recorded world unchanged
            NativeArray<Voxel.VoxelDef> defs = registry.GetVoxelGenData().VoxelRenderDefs.GetSubArray(0, registry.BaseCount);
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
