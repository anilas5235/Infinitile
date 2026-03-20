using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Test
{
    public class VoxelWorldRender : MonoBehaviour
    {
        public Material solidMaterial;
        public Material transparentMaterial;
        public Material foliageMaterial;
        
        public ComputeShader pointBuilder;
        private int _pointBuilderKernelID;
        private int _copyKernelID;
        
        private RenderBufferManager _solidBufferManager;
        private RenderBufferManager _transparentBufferManager;
        private RenderBufferManager _foliageBufferManager;

        private Dictionary<int2, GraphicsBuffer> _voxelDataBuffers = new();

        private void Awake()
        {
            _solidBufferManager = new RenderBufferManager(solidMaterial);
            _transparentBufferManager = new RenderBufferManager(transparentMaterial);
            _foliageBufferManager = new RenderBufferManager(foliageMaterial);
            
            _pointBuilderKernelID = pointBuilder.FindKernel("RebuildPoints");
            _copyKernelID = pointBuilder.FindKernel("CopyPoints");
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
            _solidBufferManager.Dispose();
            _transparentBufferManager.Dispose();
            _foliageBufferManager.Dispose();

            foreach (GraphicsBuffer buffer in _voxelDataBuffers.Values)
            {
                buffer.Dispose();
            }

            _voxelDataBuffers.Clear();
        }

        private void Draw(ScriptableRenderContext context, Camera cam)
        {
            _solidBufferManager.Draw(cam);
            _transparentBufferManager.Draw(cam);
            _foliageBufferManager.Draw(cam);
        }
        
        public void CopyPointData(GraphicsBuffer source, int count)
        {
            
        }
        
        private void CopyJob(int solidCount, int transparentCount, int foliageCount)
        {
            
        }

        public void UpdatePartitions(List<int3> partitions)
        {
            foreach (int3 partitionPos in partitions)
            {
            }
        }

        private void CopyJob()
        {
        }
    }
}