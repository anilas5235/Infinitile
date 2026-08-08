using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class VoxelStructureRegistry : TopLevelRegistry<VoxelStructure>
    {
        public NativeArray<VoxelStructure.VoxelStructureDef> StructureArray { get; private set; }
        
        public void Initialize()
        {
            InternalInitialize();
        }

        public void FinalizeRegistry(VoxelRegistry voxelRegistry)
        {
            InternalFinalize();

            if (NameRegistry.Count == 0)
            {
                StructureArray = new NativeArray<VoxelStructure.VoxelStructureDef>(0, Allocator.Domain);
                return;
            }

            VoxelStructure.VoxelStructureDef[] tempArray = new VoxelStructure.VoxelStructureDef[NameRegistry.Count];
            int index = 0;
            foreach (KeyValuePair<ushort, VoxelStructure> kvp in SoRegistry.GetAllEntries())
            {
                VoxelEngineLogger.Info<VoxelStructureRegistry>($"copy structure {kvp.Value.name} to Structure array");
                VoxelStructure.VoxelStructureDef def;
                try
                {
                    def = kvp.Value.ToStruct(voxelRegistry);
                }
                catch (Exception e)
                {
                    VoxelEngineLogger.Error<VoxelStructureRegistry>(
                        $"Failed to convert structure {kvp.Value.name} to struct: {e.Message}. Skipping this structure.");
                    continue;
                }

                tempArray[index] = def;
                index++;
            }

            StructureArray = new NativeArray<VoxelStructure.VoxelStructureDef>(tempArray, Allocator.Domain);
        }

        public override void Dispose()
        {
            foreach (VoxelStructure.VoxelStructureDef def in StructureArray) def.Dispose();
            StructureArray.Dispose();
        }
    }
}