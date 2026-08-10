using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation
{
    [CreateAssetMenu(fileName = "MultiVoxelBlueprint", menuName = "Infinitile/Generation/MultiVoxelBlueprint")]
    public class VoxelStructure : ScriptableObject
    {
        public StructurePart[] parts;

        public List<FixedString32Bytes> GetRequiredVoxels()
        {
            HashSet<FixedString32Bytes> uniqueNames = new();
            foreach (StructurePart part in parts) uniqueNames.Add(part.voxel.GetFullName());
            return uniqueNames.ToList();
        }

        public VoxelStructureDef ToStruct(VoxelRegistry voxelRegistry)
        {
            VoxelStructureDef def = new()
            {
                PlacementData =
                    new NativeHashMap<ushort, NativeArray<VoxelPlacement>>(parts.Length, Allocator.Domain)
            };

            foreach (StructurePart part in parts)
            {
                NativeArray<VoxelPlacement> placements = new(part.placements.Length, Allocator.Domain);
                placements.CopyFrom(part.placements);
                def.PlacementData.TryAdd(voxelRegistry.GetIdOrThrow(part.voxel.GetFullName()), placements);
            }

            return def;
        }

        [BurstCompile]
        public struct VoxelStructureDef : IDisposable
        {
            [NativeDisableParallelForRestriction]
            public NativeHashMap<ushort, NativeArray<VoxelPlacement>> PlacementData;

            public void Dispose()
            {
                foreach (KVPair<ushort, NativeArray<VoxelPlacement>> item in PlacementData) item.Value.Dispose();
                PlacementData.Dispose();
            }
        }
    }

    [Serializable]
    public class StructurePart
    {
        public Voxel.Voxel voxel;
        public VoxelPlacement[] placements;
    }

    [Serializable]
    public struct VoxelPlacement
    {
        public PlacementShape shape;
        public Vector3Int origin;

        public Vector3Int end;
        public Vector3Int size;

        public int height;
        public int radius;

        public bool filled;
    }

    public enum PlacementShape
    {
        Single,
        Line,
        Circle,
        Box,
        Cylinder,
    }
}