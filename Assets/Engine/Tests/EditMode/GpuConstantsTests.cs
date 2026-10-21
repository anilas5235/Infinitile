using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Engine.Scripts.Render;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using NUnit.Framework;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The kernels and voxel shaders take their compile-time constants from VoxelGpuConstants.hlsl; the C# side
    ///     sizes buffers and dispatches with <see cref="VoxelRenderConstants" />. A value changed on one side only
    ///     builds and runs, but drops points or reads the wrong voxels.
    /// </summary>
    public class GpuConstantsTests{
        private const string IncludePath = "Assets/Engine/Shaders/Urp/VoxelGpuConstants.hlsl";

        private static readonly Dictionary<string, int> Expected = new(){
            ["POINTS_PER_PAGE"] = PointsPerPage,
            ["CHUNK_INDEX_SHIFT_X"] = ChunkIndexShiftX,
            ["CHUNK_INDEX_SHIFT_Z"] = ChunkIndexShiftZ,
            ["COMP_PAGE_SHIFT"] = CompPageShift,
            ["COMP_PAGE_MASK"] = CompPageMask,
            ["MESH_LAYER_SOLID"] = MeshLayerSolid,
            ["PREP_CHUNKS"] = PrepChunks,
            ["BOX_SIZE"] = PrepBoxSize,
            ["PREP_BOX_THREADS"] = PrepBoxThreads,
            ["BUILD_POINTS_THREADS"] = BuildPointsThreads,
            ["COPY_INTERVALS_THREADS"] = CopyIntervalsThreads,
            ["COPY_POINTS_THREADS"] = CopyPointsThreads,
            ["CULL_THREADS"] = CullThreads
        };

        private static Dictionary<string, int> ParseDefines(){
            Dictionary<string, int> defines = new();
            Regex define = new(@"^\s*#define\s+(\w+)\s+(\d+)u?\b");
            foreach (string line in File.ReadAllLines(IncludePath)){
                Match m = define.Match(line);
                if (m.Success) defines.Add(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            }

            return defines;
        }

        [Test]
        public void HlslDefines_MatchTheCSharpConstants(){
            Dictionary<string, int> defines = ParseDefines();
            CollectionAssert.AreEquivalent(Expected.Keys, defines.Keys,
                "every value define in the include needs its C# counterpart here, and the other way round");
            foreach ((string name, int value) in Expected) Assert.AreEqual(value, defines[name], name);
        }

        [Test]
        public void ChunkIndexShifts_MatchTheChunkLayout(){
            Assert.AreEqual(ChunkHeight, 1 << ChunkIndexShiftZ);
            Assert.AreEqual(ChunkDepth * ChunkHeight, 1 << ChunkIndexShiftX);

            int3 pos = new(ChunkWidth - 1, 77, 5);
            int shifted = (pos.x << ChunkIndexShiftX) + (pos.z << ChunkIndexShiftZ) + pos.y;
            Assert.AreEqual(ChunkSize.Flatten(pos), shifted, "the kernels index like Chunk.GetIndex");
        }

        [Test]
        public void DispatchSizes_CoverTheirWork(){
            Assert.AreEqual(IntervalPages.PageSize - 1, CompPageMask);
            // BuildPoints runs one thread per partition voxel and dispatches whole groups
            Assert.AreEqual(0, PartitionWidth % BuildPointsThreads);
            Assert.AreEqual(0, PartitionHeight % BuildPointsThreads);
            Assert.AreEqual(0, PartitionDepth % BuildPointsThreads);
            // CopyPoints runs one group per page, one thread per point
            Assert.AreEqual(PointsPerPage, CopyPointsThreads);
        }
    }
}
