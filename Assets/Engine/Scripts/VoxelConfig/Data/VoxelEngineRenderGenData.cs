using System;
using Unity.Burst;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Burst-compatible container that exposes voxel render definitions to meshing jobs.
    /// </summary>
    [BurstCompile]
    public struct VoxelEngineRenderGenData : IDisposable
    {
        /// <summary>
        ///     Backing array of render definitions, indexed by voxel ID.
        /// </summary>
        [NativeDisableParallelForRestriction] internal NativeArray<VoxelRenderDef> VoxelRenderDefs;

        /// <summary>
        ///     Disposes the underlying native array holding voxel render definitions.
        /// </summary>
        public void Dispose()
        {
            VoxelRenderDefs.Dispose();
        }

        /// <summary>
        ///     Gets the mesh layer of the voxel with the given ID.
        /// </summary>
        /// <param name="voxelId">Voxel ID to query.</param>
        /// <returns>Mesh layer associated with the voxel.</returns>
        public readonly MeshLayer GetMeshLayer(ushort voxelId)
        {
            return VoxelRenderDefs[voxelId].MeshLayer;
        }

        /// <summary>
        ///     Gets the full render definition for the voxel with the given ID.
        /// </summary>
        /// <param name="voxelId">Voxel ID used to index into the render definition array.</param>
        /// <returns>Render definition for the voxel.</returns>
        public readonly VoxelRenderDef GetRenderDef(ushort voxelId)
        {
            return VoxelRenderDefs[voxelId];
        }
    }
}