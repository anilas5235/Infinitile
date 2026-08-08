using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class BiomeRegistry : IDisposable
    {
        private readonly Registry<FixedString32Bytes> _nameRegistry = new(16);
        private readonly Registry<Biome> _biomeRegistry = new(16);
        
        public NativeArray<Biome.BiomeDef> BiomeArray { get; private set; }

        public void Register(FixedString32Bytes packagePrefix, Biome biome)
        {
            if (!biome) throw new ArgumentNullException(nameof(biome), "Cannot register a null biome definition.");
            FixedString32Bytes biomeName;

            try
            {
                biomeName = new FixedString32Bytes(packagePrefix + ":" + biome.name);
            }
            catch (ArgumentException e)
            {
                VoxelEngineLogger.Error<VoxelRegistry>(
                    $"Voxel name '{biome.name}' exceeds the maximum length of {FixedString32Bytes.UTF8MaxLengthInBytes} bytes. Registration skipped.");
                return;
            }

            _nameRegistry.Register(biomeName);
            _biomeRegistry.Register(biome);
        }

        public void PrepareArray(VoxelRegistry voxelRegistry)
        {
            if (_nameRegistry.Count == 0)
            {
                BiomeArray = new NativeArray<Biome.BiomeDef>(0, Allocator.Domain);
                return;
            }

            Biome.BiomeDef[] tempArray = new Biome.BiomeDef[_nameRegistry.Count];
            int index = 0;
            foreach (KeyValuePair<ushort, Biome> kvp in _biomeRegistry.GetAllEntries())
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


        public void Dispose()
        {
            foreach (Biome.BiomeDef def in BiomeArray) def.Dispose();
            BiomeArray.Dispose();
        }
    }
}