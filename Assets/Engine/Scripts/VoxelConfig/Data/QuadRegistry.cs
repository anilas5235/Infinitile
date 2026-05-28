using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    /// Registers quad definitions and builds the runtime quad array used by voxel rendering.
    /// </summary>
    public class QuadRegistry
    {
        private readonly Dictionary<QuadDefinition, ushort> _quadToId = new();


        /// <summary>
        /// Gets the registered quad data array after preparation.
        /// </summary>
        public QuadDefinition.QuadData[] QuadArray { get; private set; }


        /// <summary>
        /// Registers a quad definition and returns its assigned ID.
        /// </summary>
        /// <param name="quad">The quad definition to register.</param>
        /// <returns>The assigned quad ID, or 0 if registration failed.</returns>
        public ushort Register(QuadDefinition quad)
        {
            ushort quadId = 0;
            if (!quad)
            {
                VoxelEngineLogger.Error<VoxelRegistry>("Attempted to register a null quad definition.");
                return quadId;
            }


            if (_quadToId.TryGetValue(quad, out quadId)) return quadId;

            quadId = (ushort)_quadToId.Count;
            _quadToId[quad] = quadId;

            return quadId;
        }

        /// <summary>
        /// Builds the runtime quad array from all registered quad definitions.
        /// </summary>
        internal void PrepareArray()
        {
            if (_quadToId.Count == 0)
            {
                QuadArray = Array.Empty<QuadDefinition.QuadData>();
                return;
            }

            QuadArray = new QuadDefinition.QuadData[_quadToId.Count];
            // Copy each texture into the texture array
            int index = 0;
            foreach (KeyValuePair<QuadDefinition, ushort> kvp in _quadToId)
            {
                VoxelEngineLogger.Info<TexRegistry>($"copy quad {kvp.Key.name} to Quad array");
                QuadArray[index] = kvp.Key.ToStruct();
                index++;
            }
        }
    }
}