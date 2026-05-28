using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Render definition for a voxel, including texture slots for all faces, mesh layer,
    ///     collision flag and additional rendering information.
    /// </summary>
    [BurstCompile]
    public struct VoxelRenderDef
    {
        /// <summary>Mesh layer (solid, transparent or air).</summary>
        public MeshLayer MeshLayer;

        /// <summary>Whether all faces should always be rendered, even when hidden by neighbors.</summary>
        public bool AlwaysRenderAllFaces;

        /// <summary>Distance at which depth fading starts for transparent voxels.</summary>
        public half DepthFadeDistance;

        /// <summary> Emissive glow level for the voxel (0-255, where 255 is full brightness).</summary>
        public byte Glow;

        /// <summary>Whether this voxel participates in physics collision.</summary>
        public bool Collision;

        public uint2 Always;
        public uint2 Right;
        public uint2 Left;
        public uint2 Up;
        public uint2 Down;
        public uint2 Front;
        public uint2 Back;

        public bool IsAir => MeshLayer == MeshLayer.Air;
        public bool IsTransparent => MeshLayer == MeshLayer.Transparent;
        public bool IsSolid => MeshLayer == MeshLayer.Solid;
        public bool IsFoliage => MeshLayer == MeshLayer.Foliage;
    }
}