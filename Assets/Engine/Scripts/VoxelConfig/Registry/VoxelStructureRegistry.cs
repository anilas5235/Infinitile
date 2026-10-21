using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry{
    public class VoxelStructureRegistry : TopLevelRegistry<VoxelStructure>{
        public NativeArray<VoxelStructure.VoxelStructureDef> StructureArray{ get; private set; }

        public void Initialize(){
            InternalInitialize();
        }

        public void FinalizeRegistry(VoxelRegistry voxelRegistry){
            InternalFinalize();

            if (NameRegistry.Count == 0){
                StructureArray = new NativeArray<VoxelStructure.VoxelStructureDef>(0, Allocator.Domain);
                return;
            }

            VoxelStructure.VoxelStructureDef[] tempArray = new VoxelStructure.VoxelStructureDef[NameRegistry.Count];
            bool[] converted = new bool[tempArray.Length];
            foreach (KeyValuePair<ushort, VoxelStructure> kvp in SoRegistry.GetAllEntries()){
                VoxelEngineLogger.Info<VoxelStructureRegistry>($"copy structure {kvp.Value.name} to Structure array");
                VoxelStructure.VoxelStructureDef def;
                try{
                    def = kvp.Value.ToStruct(voxelRegistry);
                }
                catch (Exception e){
                    // A skipped structure would leave a default def (uncreated placement map) at its id, which makes
                    // Dispose throw. A broken structure is a configuration error that fails the registry instead.
                    for (int i = 0; i < tempArray.Length; i++)
                        if (converted[i]) tempArray[i].Dispose();
                    throw new InvalidOperationException(
                        $"Failed to convert structure {kvp.Value.name} to struct: {e.Message}", e);
                }

                tempArray[kvp.Key] = def; // place by id, not by enumeration order
                converted[kvp.Key] = true;
            }

            StructureArray = new NativeArray<VoxelStructure.VoxelStructureDef>(tempArray, Allocator.Domain);
        }

        public override void Dispose(){
            if (!StructureArray.IsCreated) return; // FinalizeRegistry may have thrown before creating the array
            foreach (VoxelStructure.VoxelStructureDef def in StructureArray) def.Dispose();
            StructureArray.Dispose();
        }
    }
}
