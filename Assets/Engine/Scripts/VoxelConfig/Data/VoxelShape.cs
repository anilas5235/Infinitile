using System;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    /// Describes a voxel shape as a list of quads and their draw conditions.
    /// </summary>
    [CreateAssetMenu(menuName = "Voxel/Shape/Voxel Shape", fileName = "VoxelShape")]
    public class VoxelShape : ScriptableObject
    {
        /// <summary>
        /// The quads that make up this voxel shape.
        /// </summary>
        public VoxelQuad[] quads;
    }

    /// <summary>
    /// Associates a quad definition with the condition under which it should be drawn.
    /// </summary>
    [Serializable]
    public class VoxelQuad
    {
        /// <summary>
        /// The quad definition used for rendering.
        /// </summary>
        public QuadDefinition quadDef;

        /// <summary>
        /// The condition that determines when this quad is drawn.
        /// </summary>
        public QuadDrawCondition drawCondition;
    }

    /// <summary>
    /// Specifies the face condition required for drawing a quad.
    /// </summary>
    public enum QuadDrawCondition
    {
        /// <summary>Draw the quad always.</summary>
        Always,
        /// <summary>Draw the quad when the face points up.</summary>
        Up,
        /// <summary>Draw the quad when the face points down.</summary>
        Down,
        /// <summary>Draw the quad when the face points forward.</summary>
        Forward,
        /// <summary>Draw the quad when the face points backward.</summary>
        Backward,
        /// <summary>Draw the quad when the face points left.</summary>
        Left,
        /// <summary>Draw the quad when the face points right.</summary>
        Right
    }
}