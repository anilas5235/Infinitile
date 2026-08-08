using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class BiomeRegistry : TopLevelRegistry<Biome>
    {
        public NativeArray<Biome.BiomeDef> BiomeArray { get; private set; }

        public void Initialize()
        {
            InternalInitialize();
        }

        public void FinalizeRegistry(VoxelRegistry voxelRegistry)
        {
            InternalFinalize();

            if (NameRegistry.Count == 0)
            {
                BiomeArray = new NativeArray<Biome.BiomeDef>(0, Allocator.Domain);
                return;
            }

            Biome.BiomeDef[] tempArray = new Biome.BiomeDef[NameRegistry.Count];
            int index = 0;
            foreach (KeyValuePair<ushort, Biome> kvp in SoRegistry.GetAllEntries())
            {
                VoxelEngineLogger.Info<BiomeRegistry>($"copy biome {kvp.Value.name} to Biome array");
                Biome.BiomeDef def;
                try
                {
                    def = kvp.Value.ToStruct(voxelRegistry);
                }
                catch (Exception e)
                {
                    VoxelEngineLogger.Error<BiomeRegistry>(
                        $"Failed to convert biome {kvp.Value.name} to struct: {e.Message}. Skipping this biome.");
                    continue;
                }

                tempArray[index] = def;
                index++;
            }

            BiomeArray = new NativeArray<Biome.BiomeDef>(tempArray, Allocator.Domain);
        }


        public override void Dispose()
        {
            foreach (Biome.BiomeDef def in BiomeArray) def.Dispose();
            BiomeArray.Dispose();
        }
    }
}