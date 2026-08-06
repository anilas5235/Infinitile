using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation
{
    [CreateAssetMenu(fileName = "MultiVoxelBlueprint", menuName = "Infinitile/Generation/MultiVoxelBlueprint")]
    public class MultiVoxelBlueprint : ScriptableObject
    {
        public MultiVoxelPart[] parts;

        public List<string> GetRequiredVoxels()
        {
            HashSet<string> uniqueNames = new();
            foreach (MultiVoxelPart part in parts) uniqueNames.Add(part.voxelName);
            return uniqueNames.ToList();
        }
    }

    [Serializable]
    public class MultiVoxelPart
    {
        public string voxelName;
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