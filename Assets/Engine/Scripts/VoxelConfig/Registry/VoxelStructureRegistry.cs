using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class VoxelStructureRegistry : Registry<VoxelStructure>, IResourceRegistry<VoxelStructure>
    {
        public VoxelStructureRegistry(int initCapacity) : base(initCapacity)
        {
        }


        public VoxelStructure.VoxelStructureDef[] StructureArray { get; private set; }


        public override ushort Register(VoxelStructure structure)
        {
            if (structure) return base.Register(structure);
            throw new ArgumentNullException(nameof(structure), "Cannot register a null structure definition.");
        }

        public void PrepareArray()
        {
            if (Count == 0)
            {
                StructureArray = Array.Empty<VoxelStructure.VoxelStructureDef>();
                return;
            }

            StructureArray = new VoxelStructure.VoxelStructureDef[Count];
            int index = 0;
            foreach (KeyValuePair<VoxelStructure, ushort> kvp in ForwardMap)
            {
                VoxelEngineLogger.Info<VoxelStructureRegistry>($"copy structure {kvp.Key.name} to Structure array");
                StructureArray[index] = kvp.Key.ToStruct();
                index++;
            }
        }
    }
}