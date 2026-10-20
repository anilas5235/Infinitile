using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation{
    /// <summary>
    ///     A multi-block voxel structure such as a tree (VEG-05). Placement offsets are relative to the first voxel
    ///     above the anchor column's top block: (0, 0, 0) sits on the ground. Shapes (expanded by
    ///     <c>FloraConfigBuilder.Expand</c>): Single = origin; Line = origin to end inclusive; Box = <c>size</c> centred
    ///     on origin (from origin − size/2), filled or a shell; Circle = horizontal disc of <c>radius</c> at origin.y
    ///     (a one-block ring when not filled); Cylinder = that circle on <c>height</c> layers upwards.
    /// </summary>
    [CreateAssetMenu(fileName = "MultiVoxelBlueprint", menuName = "Infinitile/Generation/MultiVoxelBlueprint")]
    public class VoxelStructure : ScriptableObject{
        public StructurePart[] parts;

        public List<FixedString32Bytes> GetRequiredVoxels(){
            HashSet<FixedString32Bytes> uniqueNames = new();
            foreach (StructurePart part in parts) uniqueNames.Add(part.voxel.GetFullName());
            return uniqueNames.ToList();
        }

        public VoxelStructureDef ToStruct(VoxelRegistry voxelRegistry){
            VoxelStructureDef def = new(){
                PlacementData =
                    new NativeHashMap<ushort, NativeArray<VoxelPlacement>>(parts.Length, Allocator.Domain)
            };

            try{
                foreach (StructurePart part in parts){
                    ushort id = voxelRegistry.GetIdOrThrow(part.voxel.GetFullName());
                    NativeArray<VoxelPlacement> placements = new(part.placements.Length, Allocator.Domain);
                    placements.CopyFrom(part.placements);
                    if (!def.PlacementData.TryAdd(id, placements)) placements.Dispose();
                }
            }
            catch{
                def.Dispose(); // D-04: release the partly built maps before the registry rethrows
                throw;
            }

            return def;
        }

        [BurstCompile]
        public struct VoxelStructureDef : IDisposable{
            [NativeDisableParallelForRestriction]
            public NativeHashMap<ushort, NativeArray<VoxelPlacement>> PlacementData;

            public void Dispose(){
                foreach (KVPair<ushort, NativeArray<VoxelPlacement>> item in PlacementData) item.Value.Dispose();
                PlacementData.Dispose();
            }
        }
    }

    [Serializable]
    public class StructurePart{
        public Voxel.Voxel voxel;
        public VoxelPlacement[] placements;
    }

    [Serializable]
    public struct VoxelPlacement{
        public PlacementShape shape;
        public Vector3Int origin;

        public Vector3Int end;
        public Vector3Int size;

        public int height;
        public int radius;

        public bool filled;
    }

    public enum PlacementShape{
        Single,
        Line,
        Circle,
        Box,
        Cylinder
    }
}
