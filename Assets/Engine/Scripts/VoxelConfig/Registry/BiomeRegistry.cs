using System;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class BiomeRegistry : IDisposable
    {
        private readonly Registry<FixedString32Bytes> _nameRegistry = new(16);
        private readonly Registry<Biome> _biomeRegistry = new(16);

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


        public void Dispose()
        {
        }
    }
}