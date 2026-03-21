using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Utils.Collections;
using Runtime.Engine.Utils.Extensions;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelConstants;
using static Runtime.Engine.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Test
{
    public class ComputeTest : MonoBehaviour
    {
        private const int PointsPerPage = 128;
        private const int PagesPerBuffer = 512;
        private const int RenderBufferSize = PointsPerPage * PagesPerBuffer;

        

        struct PartitionMetadata
        {
            int3 partitionPos; // World partition coordinates
            float3 boundsMin; // AABB min for frustum culling
            float3 boundsMax; // AABB max
        };

        [SerializeField] private ComputeShader pointBuilder;
        [SerializeField] private Material solidMaterial;
        [SerializeField] private Material transparentMaterial;
        [SerializeField] private Material foliageMaterial;


        private GraphicsBuffer _voxelRenderDefBuffer;
        private GraphicsBuffer _voxelQuadTexPairBuffer;
        private GraphicsBuffer _voxelData;
        private GraphicsBuffer _metadata;

        private GraphicsBuffer _solidPointsOut;
        private GraphicsBuffer _transparentPointsOut;
        private GraphicsBuffer _foliagePointsOut;

        private GraphicsBuffer _solidArgBuffer;
        private GraphicsBuffer _transparentArgBuffer;
        private GraphicsBuffer _foliageArgBuffer;
        private MaterialPropertyBlock _materialPropertyBlock;

        private GraphicsBuffer _bigSolidVertexBuffer;
        private GraphicsBuffer _bigTransparentVertexBuffer;
        private GraphicsBuffer _bigFoliageVertexBuffer;
        private GraphicsBuffer _solidPageStatesBuffer;
        private GraphicsBuffer _transparentPageStatesBuffer;
        private GraphicsBuffer _foliagePageStatesBuffer;
        private GraphicsBuffer _solidPagesBuffer;
        private GraphicsBuffer _transparentPagesBuffer;
        private GraphicsBuffer _foliagePagesBuffer;
        private GraphicsBuffer _pageCountsBuffer;

        private GraphicsBuffer _readBackCountBuffer;

        private int _buildPointsKernel;
        private int _copyPointsKernel;

        private UnsafeIntervalList<ushort> _voxels;

        private bool _dataInitialized = false;

        private readonly MaterialPass[] _drawCalls = new MaterialPass[3];
        private readonly uint[] _defaultArgs = { 0u, 1u, 0u, 0u, 0u };

        private void Awake()
        {
            VoxelRegistry voxelRegistry = VoxelDataImporter.Instance.VoxelRegistry;
            _voxelRenderDefBuffer = voxelRegistry.VoxelRenderDefBuffer;
            _voxelQuadTexPairBuffer = voxelRegistry.QuadTexPairBuffer;

            _readBackCountBuffer = new GraphicsBuffer(Target.Raw, 3, sizeof(uint));

            _buildPointsKernel = pointBuilder.FindKernel("RebuildPoints");
            _copyPointsKernel = pointBuilder.FindKernel("CopyPoints");
            _voxels = new UnsafeIntervalList<ushort>(10, Allocator.Domain);
            _voxels.AddInterval(0, VoxelsPerPartition);
            for (int j = 0; j < 64; j++)
            {
                int index = PartitionSize.Flatten(new int3((j % 16) * 2, 0, 2 * (int)math.floor(j / 16.0f)));
                _voxels.Set(index, (ushort)j);
            }

            uint2[] intervalData = new uint2[_voxels.CompressedLength];
            int i = 0;
            foreach (UnsafeIntervalList<ushort>.Node n in _voxels.Internal)
            {
                intervalData[i++] = new uint2(n.Value, (uint)n.Count);
            }

            if (_voxels.Length != VoxelsPerPartition) throw new Exception("Voxel data length mismatch!");
            _voxelData = new GraphicsBuffer(Target.Structured, _voxels.CompressedLength, Marshal.SizeOf<uint2>());
            _voxelData.SetData(intervalData);
            Debug.Log("Voxel data uploaded to GPU.: " + string.Join(", ", intervalData));
            _metadata = new GraphicsBuffer(Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());

            _solidPointsOut = new GraphicsBuffer(Target.Append | Target.CopySource, MaxPointsPerPartition,
                Marshal.SizeOf<Vertex>());
            _transparentPointsOut = new GraphicsBuffer(Target.Append | Target.CopySource, MaxPointsPerPartition,
                Marshal.SizeOf<Vertex>());
            _foliagePointsOut = new GraphicsBuffer(Target.Append | Target.CopySource, MaxPointsPerPartition / 3,
                Marshal.SizeOf<Vertex>());

            _bigSolidVertexBuffer = new GraphicsBuffer(Target.Structured | Target.CopyDestination, RenderBufferSize,
                Marshal.SizeOf<Vertex>());
            _bigTransparentVertexBuffer = new GraphicsBuffer(Target.Structured | Target.CopyDestination,
                RenderBufferSize,
                Marshal.SizeOf<Vertex>());
            _bigFoliageVertexBuffer = new GraphicsBuffer(Target.Structured | Target.CopyDestination, RenderBufferSize,
                Marshal.SizeOf<Vertex>());

            _solidPageStatesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, sizeof(uint));
            _transparentPageStatesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, sizeof(uint));
            _foliagePageStatesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, sizeof(uint));
            uint[] emptyPageStates = new uint[PagesPerBuffer];
            _solidPageStatesBuffer.SetData(emptyPageStates);
            _transparentPageStatesBuffer.SetData(emptyPageStates);
            _foliagePageStatesBuffer.SetData(emptyPageStates);

            _solidPagesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<uint2>());
            _transparentPagesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<uint2>());
            _foliagePagesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<uint2>());
            _pageCountsBuffer = new GraphicsBuffer(Target.Structured, 3, sizeof(uint));

            _solidArgBuffer = new GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint));
            _solidArgBuffer.SetData(_defaultArgs);
            _transparentArgBuffer = new GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint));
            _transparentArgBuffer.SetData(_defaultArgs);
            _foliageArgBuffer = new GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint));
            _foliageArgBuffer.SetData(_defaultArgs);

            _materialPropertyBlock = new MaterialPropertyBlock();

            _drawCalls[0] = new MaterialPass(solidMaterial);
            _drawCalls[1] = new MaterialPass(transparentMaterial);
            _drawCalls[2] = new MaterialPass(foliageMaterial);
            _drawCalls[0].AddDrawCall(new DrawCall
            {
                VertexBuffer = _bigSolidVertexBuffer,
                ArgsBuffer = _solidArgBuffer,
                PageStatesBuffer = _solidPageStatesBuffer
            });
            _drawCalls[1].AddDrawCall(new DrawCall
            {
                VertexBuffer = _bigTransparentVertexBuffer,
                ArgsBuffer = _transparentArgBuffer,
                PageStatesBuffer = _transparentPageStatesBuffer,
            });
            _drawCalls[2].AddDrawCall(new DrawCall
            {
                VertexBuffer = _bigFoliageVertexBuffer,
                ArgsBuffer = _foliageArgBuffer,
                PageStatesBuffer = _foliagePageStatesBuffer,
            });
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += Draw;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= Draw;
        }

        private void OnDestroy()
        {
            _voxels.Dispose();
            _voxelData.Dispose();
            _metadata.Dispose();
            _solidPointsOut.Dispose();
            _transparentPointsOut.Dispose();
            _foliagePointsOut.Dispose();
            _solidArgBuffer.Dispose();
            _transparentArgBuffer.Dispose();
            _foliageArgBuffer.Dispose();
            _bigSolidVertexBuffer.Dispose();
            _bigTransparentVertexBuffer.Dispose();
            _bigFoliageVertexBuffer.Dispose();
            _solidPageStatesBuffer.Dispose();
            _transparentPageStatesBuffer.Dispose();
            _foliagePageStatesBuffer.Dispose();
            _solidPagesBuffer.Dispose();
            _transparentPagesBuffer.Dispose();
            _foliagePagesBuffer.Dispose();
            _pageCountsBuffer.Dispose();
            _materialPropertyBlock.Clear();
        }

        private void Start()
        {
            double start = Time.realtimeSinceStartupAsDouble;
            pointBuilder.SetBuffer(_buildPointsKernel, VoxelRenderDefNameID, _voxelRenderDefBuffer);
            pointBuilder.SetInt(VoxelRenderDefCountNameID, _voxelRenderDefBuffer.count);

            pointBuilder.SetBuffer(_buildPointsKernel, VoxelQuadTexPairNameID, _voxelQuadTexPairBuffer);
            pointBuilder.SetInt(VoxelQuadTexPairCountNameID, _voxelQuadTexPairBuffer.count);

            pointBuilder.SetBuffer(_buildPointsKernel, VoxelDataNameID, _voxelData);
            pointBuilder.SetInt(VoxelCompressedCountNameID, _voxelData.count);

            pointBuilder.SetBuffer(_buildPointsKernel, MetadataNameID, _metadata);
            pointBuilder.SetBuffer(_buildPointsKernel, SolidPointsOutNameID, _solidPointsOut);
            pointBuilder.SetBuffer(_buildPointsKernel, TransparentPointsOutNameID, _transparentPointsOut);
            pointBuilder.SetBuffer(_buildPointsKernel, FoliagePointsOutNameID, _foliagePointsOut);

            pointBuilder.SetInt(PartitionIndexNameID, 0);

            pointBuilder.Dispatch(_buildPointsKernel, 4, 4, 4);
            double buildFinished = Time.realtimeSinceStartupAsDouble;

            CopyCount(_solidPointsOut, _readBackCountBuffer, sizeof(uint) * 0);
            CopyCount(_transparentPointsOut, _readBackCountBuffer, sizeof(uint) * 1);
            CopyCount(_foliagePointsOut, _readBackCountBuffer, sizeof(uint) * 2);

            uint[] counts = new uint[_readBackCountBuffer.count];
            _readBackCountBuffer.GetData(counts);
            Debug.Log($"Points generated - Solid: {counts[0]}, Transparent: {counts[1]}, Foliage: {counts[2]}");
            double countReadFinished = Time.realtimeSinceStartupAsDouble;

            int solidCount = (int)counts[0];
            SetIndirectArgs(_solidArgBuffer, solidCount);
            _drawCalls[0].DrawCalls[0].VertexCount = solidCount;

            int transparentCount = (int)counts[1];
            SetIndirectArgs(_transparentArgBuffer, transparentCount);
            _drawCalls[1].DrawCalls[0].VertexCount = transparentCount;

            int foliageCount = (int)counts[2];
            SetIndirectArgs(_foliageArgBuffer, foliageCount);
            _drawCalls[2].DrawCalls[0].VertexCount = foliageCount;
            double indicesSetFinished = Time.realtimeSinceStartupAsDouble;

            CopyJob(solidCount, transparentCount, foliageCount);
            double copyFinished = Time.realtimeSinceStartupAsDouble;
            _dataInitialized = true;

            double done = Time.realtimeSinceStartupAsDouble;
            Debug.Log(
                $"Build time: {(buildFinished - start) * 1000:F3}ms, Readback: {(countReadFinished - buildFinished) * 1000:F3}ms," +
                $" IndirectArgs Set:{(indicesSetFinished - countReadFinished) * 1000:F3}ms ,Copy {(copyFinished - indicesSetFinished) * 1000:F3}ms, Total: {(done - start) * 1001:F3}ms");
        }

        private bool SetIndirectArgs(GraphicsBuffer argBuffer, int count)
        {
            uint[] argData = (uint[])_defaultArgs.Clone();
            if (count < 1) return false;
            argData[0] = (uint)(count * 6u);
            argBuffer.SetData(argData);
            Debug.Log($"Indirect args: {string.Join(", ", argData)}");
            Debug.Log($"Real point count: {count}");
            return true;
        }

        private void CopyJob(int solidCount, int transparentCount, int foliageCount)
        {
            int solidPagesCount = BuildPagesForLayer(solidCount, _solidPagesBuffer, _solidPageStatesBuffer);
            int transparentPagesCount = BuildPagesForLayer(transparentCount, _transparentPagesBuffer,
                _transparentPageStatesBuffer);
            int foliagePagesCount = BuildPagesForLayer(foliageCount, _foliagePagesBuffer, _foliagePageStatesBuffer);

            uint[] pageCounts = { (uint)solidPagesCount, (uint)transparentPagesCount, (uint)foliagePagesCount };
            _pageCountsBuffer.SetData(pageCounts);

            pointBuilder.SetBuffer(_copyPointsKernel, SolidPointsInNameID, _solidPointsOut);
            pointBuilder.SetBuffer(_copyPointsKernel, SolidPointsCopyOutNameID, _bigSolidVertexBuffer);
            pointBuilder.SetBuffer(_copyPointsKernel, SolidPagesNameID, _solidPagesBuffer);
            pointBuilder.SetBuffer(_copyPointsKernel, TransparentPointsInNameID, _transparentPointsOut);
            pointBuilder.SetBuffer(_copyPointsKernel, TransparentPointsCopyOutNameID, _bigTransparentVertexBuffer);
            pointBuilder.SetBuffer(_copyPointsKernel, TransparentPagesNameID, _transparentPagesBuffer);
            pointBuilder.SetBuffer(_copyPointsKernel, FoliagePointsInNameID, _foliagePointsOut);
            pointBuilder.SetBuffer(_copyPointsKernel, FoliagePointsCopyOutNameID, _bigFoliageVertexBuffer);
            pointBuilder.SetBuffer(_copyPointsKernel, FoliagePagesNameID, _foliagePagesBuffer);
            pointBuilder.SetBuffer(_copyPointsKernel, PageCountsNameID, _pageCountsBuffer);
            pointBuilder.SetInt(PointsPerPageNameID, PointsPerPage);

            int maxPageCount = math.max(solidPagesCount, math.max(transparentPagesCount, foliagePagesCount));
            if (maxPageCount <= 0) return;

            pointBuilder.Dispatch(_copyPointsKernel, Mathf.CeilToInt(maxPageCount / 8f), 1, 1);

            _solidPointsOut.SetCounterValue(0);
            _transparentPointsOut.SetCounterValue(0);
            _foliagePointsOut.SetCounterValue(0);
        }

        private static int BuildPagesForLayer(int pointCount, GraphicsBuffer pagesBuffer, GraphicsBuffer pageStatesBuffer)
        {
            if (pointCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pointCount));
            }

            if (pointCount > RenderBufferSize)
            {
                throw new InvalidOperationException(
                    $"Layer point count ({pointCount}) exceeds test render buffer capacity ({RenderBufferSize}).");
            }

            uint2[] pages = new uint2[PagesPerBuffer];
            uint[] pageStates = new uint[PagesPerBuffer];

            int pageIndex = 0;
            int remaining = pointCount;
            while (remaining > 0)
            {
                int inPageCount = math.min(PointsPerPage, remaining);
                pages[pageIndex] = new uint2((uint)pageIndex, (uint)inPageCount);
                pageStates[pageIndex] = (uint)inPageCount;

                pageIndex++;
                remaining -= inPageCount;
            }

            pagesBuffer.SetData(pages);
            pageStatesBuffer.SetData(pageStates);
            return pageIndex;
        }

        private void Draw(ScriptableRenderContext context, Camera cam)
        {
            if (!_dataInitialized) return;
            foreach (MaterialPass pass in _drawCalls)
            {
                pass.Draw(cam);
            }
        }

        private class MaterialPass
        {
            public readonly Material Material;
            private MaterialPropertyBlock PropertyBlock = new();
            public readonly List<DrawCall> DrawCalls = new();

            public MaterialPass(Material material)
            {
                Material = material;
            }

            public void AddDrawCall(DrawCall drawCall)
            {
                DrawCalls.Add(drawCall);
            }

            public void Draw(Camera cam)
            {
                foreach (DrawCall drawCall in DrawCalls)
                {
                    if (!drawCall.CanDraw) continue;
                    PropertyBlock.SetBuffer(PointDataNameID, drawCall.VertexBuffer);
                    PropertyBlock.SetBuffer(PageStatesNameID, drawCall.PageStatesBuffer);
                    PropertyBlock.SetInteger(PointsPerPageNameID, PointsPerPage);
                    Graphics.DrawProceduralIndirect(
                        Material,
                        new Bounds(Vector3.zero, Vector3.one * 100),
                        MeshTopology.Triangles,
                        drawCall.ArgsBuffer,
                        0,
                        cam,
                        PropertyBlock,
                        ShadowCastingMode.Off,
                        false
                    );
                    //Debug.Log($"Drawing {drawCall.VertexCount} vertices with material {Material.name}");
                }
            }
        }

        private class DrawCall
        {
            public bool CanDraw => VertexCount > 0;
            public int VertexCount;
            public GraphicsBuffer VertexBuffer;
            public GraphicsBuffer ArgsBuffer;
            public GraphicsBuffer PageStatesBuffer;
        }
    }
}