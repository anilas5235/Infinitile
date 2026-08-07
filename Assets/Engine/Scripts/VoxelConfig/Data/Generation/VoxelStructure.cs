using System;
using System.Collections.Generic;
using System.Linq;
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
            foreach (StructurePart part in parts) uniqueNames.Add(part.voxelName);
            return uniqueNames.ToList();
        }

        public VoxelStructureDef ToStruct()
        {
            VoxelStructureDef def = new()
            {
                PlacementData =
                    new NativeHashMap<FixedString32Bytes, NativeArray<VoxelPlacement>>(parts.Length, Allocator.Domain)
            };

            foreach (StructurePart part in parts)
            {
                NativeArray<VoxelPlacement> placements = new(part.placements.Length, Allocator.Domain);
                placements.CopyFrom(part.placements);
                def.PlacementData.TryAdd(part.voxelName, placements);
            }

            return def;
        }

        [BurstCompile]
        public struct VoxelStructureDef
        {
            [NativeDisableParallelForRestriction]
            public NativeHashMap<FixedString32Bytes, NativeArray<VoxelPlacement>> PlacementData;
        }
    }

    [Serializable]
    public class StructurePart
    {
        public FixedString32Bytes voxelName;
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