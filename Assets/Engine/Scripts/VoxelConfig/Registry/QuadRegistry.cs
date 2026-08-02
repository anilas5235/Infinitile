using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data;

namespace Engine.Scripts.VoxelConfig.Registry
{
    /// <summary>
    /// Registers quad definitions and builds the runtime quad array used by voxel rendering.
    /// </summary>
    public class QuadRegistry :  Registry<QuadDefinition>
    {
        /// <summary>
        /// Gets the registered quad data array after preparation.
        /// </summary>
        public QuadDefinition.QuadData[] QuadArray { get; private set; }

        /// <summary>
        /// Registers a quad definition and returns its assigned ID.
        /// </summary>
        /// <param name="quad">The quad definition to register.</param>
        /// <returns>The assigned quad ID</returns>
        public override ushort Register(QuadDefinition quad)
        {
            if (quad) return base.Register(quad);
            throw new ArgumentNullException(nameof(quad), "Cannot register a null quad definition.");
        }

        /// <summary>
        /// Builds the runtime quad array from all registered quad definitions.
        /// </summary>
        public override void PrepareArray()
        {
            if (Count == 0)
            {
                QuadArray = Array.Empty<QuadDefinition.QuadData>();
                return;
            }

            QuadArray = new QuadDefinition.QuadData[Count];
            // Copy each texture into the texture array
            int index = 0;
            foreach (KeyValuePair<QuadDefinition, ushort> kvp in ForwardMap)
            {
                VoxelEngineLogger.Info<TexRegistry>($"copy quad {kvp.Key.name} to Quad array");
                QuadArray[index] = kvp.Key.ToStruct();
                index++;
            }
        }
    }
}