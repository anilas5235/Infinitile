using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>
    ///     A merged layout as the chunk jobs read it: the compiled lookup over every box, the lookup over the land boxes
    ///     for the dry-column rule (§6.5), the water biomes as a mask and the blocks of each biome id. Built once per
    ///     config (<see cref="Create" />); the caller disposes it.
    /// </summary>
    public struct BiomeLayoutData : IDisposable{
        public BiomeLookup Main, Land;

        /// <summary>The water biomes (only Ocean in the shipped set).</summary>
        public BiomeMask Water;

        public int BiomeCount;

        /// <summary>Blocks and flags per biome id.</summary>
        [ReadOnly] public NativeArray<Biome.BiomeDef> Defs;

        public bool IsCreated => Main.IsCreated;

        /// <summary>
        ///     Whether the layout has boxes: the density generator's biome passes run only then. A config of another
        ///     generator carries an <see cref="Empty" /> layout, so its jobs see valid containers.
        /// </summary>
        public bool HasBoxes => Main.IsCreated && Main.BoxCount > 0;

        /// <summary>A layout without biomes or boxes: every container is created, <see cref="HasBoxes" /> is false.</summary>
        public static BiomeLayoutData Empty(Allocator allocator){
            return Create(new BiomeLayout(), null, allocator);
        }

        /// <summary>
        ///     Compiles <paramref name="layout" /> (no validation: that is <see cref="BiomeLayoutValidator" />'s). False
        ///     with the reason when a lookup exceeds its limit.
        /// </summary>
        public static bool TryCreate(BiomeLayout layout, Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId, Allocator allocator, out BiomeLayoutData data,
            out string error){
            data = default;
            List<LayoutBox> land = new();
            foreach (LayoutBox b in layout.Boxes)
                if (b.Biome < layout.Biomes.Count && !layout.Biomes[b.Biome].IsWater)
                    land.Add(b);
            if (!BiomeLookupCompiler.TryCompile(layout.Boxes, allocator, out BiomeLookup main, out _, out error)) return false;
            if (!BiomeLookupCompiler.TryCompile(land, allocator, out BiomeLookup landLookup, out _, out error)){
                main.Dispose();
                return false;
            }

            data.Main = main;
            data.Land = landLookup;
            data.BiomeCount = layout.Biomes.Count;
            data.Defs = new NativeArray<Biome.BiomeDef>(Math.Max(1, layout.Biomes.Count), allocator);
            for (int i = 0; i < layout.Biomes.Count; i++){
                LayoutBiome b = layout.Biomes[i];
                if (b.IsWater) data.Water.Add(i);
                if (b.Asset) data.Defs[i] = voxelId != null ? b.Asset.ToStruct(voxelId) : b.Asset.ToStruct();
            }

            return true;
        }

        /// <summary><see cref="TryCreate" /> that throws on a lookup limit.</summary>
        public static BiomeLayoutData Create(BiomeLayout layout, Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId, Allocator allocator){
            if (!TryCreate(layout, voxelId, allocator, out BiomeLayoutData data, out string error)) throw new InvalidOperationException(error);
            return data;
        }

        public void Dispose(){
            Main.Dispose();
            Land.Dispose();
            if (Defs.IsCreated) Defs.Dispose();
        }
    }
}
