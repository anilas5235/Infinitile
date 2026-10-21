using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry{
    public class BiomeRegistry : TopLevelRegistry<Biome>{
        public NativeArray<Biome.BiomeDef> BiomeArray{ get; private set; }

        public void Initialize(){
            InternalInitialize();
        }

        public void FinalizeRegistry(VoxelRegistry voxelRegistry){
            InternalFinalize();

            if (NameRegistry.Count == 0){
                BiomeArray = new NativeArray<Biome.BiomeDef>(0, Allocator.Domain);
                return;
            }

            Biome.BiomeDef[] tempArray = new Biome.BiomeDef[NameRegistry.Count];
            bool[] converted = new bool[tempArray.Length];
            foreach (KeyValuePair<ushort, Biome> kvp in SoRegistry.GetAllEntries()){
                VoxelEngineLogger.Info<BiomeRegistry>($"copy biome {kvp.Value.name} to Biome array");
                Biome.BiomeDef def;
                try{
                    def = kvp.Value.ToStruct(voxelRegistry);
                }
                catch (Exception e){
                    // Skipping a biome would leave a zero (air) entry at its id, so a broken biome is a
                    // configuration error that fails the registry instead.
                    // Entries are placed by id, so release the converted slots, not the first n.
                    for (int i = 0; i < tempArray.Length; i++)
                        if (converted[i]) tempArray[i].Dispose();
                    throw new InvalidOperationException(
                        $"Failed to convert biome {kvp.Value.name} to struct: {e.Message}", e);
                }

                tempArray[kvp.Key] = def; // place by id, not by enumeration order
                converted[kvp.Key] = true;
            }

            BiomeArray = new NativeArray<Biome.BiomeDef>(tempArray, Allocator.Domain);
        }

        public override void Dispose(){
            if (!BiomeArray.IsCreated) return; // FinalizeRegistry may have thrown before creating the array
            foreach (Biome.BiomeDef def in BiomeArray) def.Dispose();
            BiomeArray.Dispose();
        }
    }
}
