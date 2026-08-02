using Engine.Scripts.VoxelConfig.Data;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class BiomeRegistry : Registry<BiomeDefinition>
    {
        public BiomeRegistry(int initCapacity) : base(initCapacity)
        {
        }
    }
}