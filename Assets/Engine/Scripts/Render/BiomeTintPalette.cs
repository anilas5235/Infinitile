using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     The biome colours of the <see cref="BiomeTintMap" /> layers by biome index (the column biomes of
    ///     <see cref="Data.ChunkVoxelData.Biomes" />), kept in linear light for averaging: layer 0 the water colour
    ///     (<see cref="Biome.WaterColor" />), 1 the grass colour, 2 the plant colour, 3 the leaf litter colour (the values of
    ///     <see cref="VoxelConfig.Data.Voxel.VoxelBiomeTint" />). A missing biome takes the layer's default.
    ///     Replaced, not changed, when the world's biomes change; <see cref="Version" /> tells the map to rewrite.
    /// </summary>
    internal sealed class BiomeTintPalette{
        /// <summary>Layers of the tint map: water, grass, plants, leaf litter.</summary>
        public const int LayerCount = 4;

        public const int WaterLayer = 0;
        public const int GrassLayer = 1;
        public const int FoliageLayer = 2;
        public const int LitterLayer = 3;

        private static readonly Color[] Defaults ={
            Biome.DefaultWaterColor, Biome.DefaultGrassColor, Biome.DefaultFoliageColor, Biome.DefaultLitterColor
        };

        private readonly float3[][] _linear = new float3[LayerCount][];
        private readonly float3[] _defaultLinear = new float3[LayerCount];

        /// <summary>Counts the palettes; a new palette has a new version.</summary>
        public int Version{ get; }

        /// <summary>Biomes this palette knows.</summary>
        public int BiomeCount => _linear[0].Length;

        /// <param name="colors">Per layer, the sRGB colour of every biome; a null layer has only defaults.</param>
        /// <param name="version">Tells this palette from the previous one.</param>
        public BiomeTintPalette(IReadOnlyList<Color>[] colors, int version){
            Version = version;
            int count = 0;
            for (int layer = 0; layer < LayerCount; layer++)
                count = math.max(count, colors != null && colors.Length > layer && colors[layer] != null ? colors[layer].Count : 0);

            for (int layer = 0; layer < LayerCount; layer++){
                _defaultLinear[layer] = ToLinear(Defaults[layer]);
                IReadOnlyList<Color> list = colors != null && colors.Length > layer ? colors[layer] : null;
                _linear[layer] = new float3[count];
                for (int i = 0; i < count; i++)
                    _linear[layer][i] = list != null && i < list.Count ? ToLinear(list[i]) : _defaultLinear[layer];
            }
        }

        /// <summary>The palette of <paramref name="biomes" /> in config order; a null biome takes the defaults.</summary>
        public static BiomeTintPalette FromBiomes(IReadOnlyList<Biome> biomes, int version){
            int count = biomes?.Count ?? 0;
            Color[] water = new Color[count], grass = new Color[count], foliage = new Color[count], litter = new Color[count];
            for (int i = 0; i < count; i++){
                Biome biome = biomes[i];
                water[i] = biome ? biome.WaterColor : Biome.DefaultWaterColor;
                grass[i] = biome ? biome.GrassColor : Biome.DefaultGrassColor;
                foliage[i] = biome ? biome.FoliageColor : Biome.DefaultFoliageColor;
                litter[i] = biome ? biome.LitterColor : Biome.DefaultLitterColor;
            }

            return new BiomeTintPalette(new IReadOnlyList<Color>[]{ water, grass, foliage, litter }, version);
        }

        /// <summary>The default colour (sRGB) of <paramref name="layer" />, where no chunk has written yet.</summary>
        public Color DefaultColor(int layer){
            return Defaults[layer];
        }

        /// <summary>The linear colour of <paramref name="biome" /> in <paramref name="layer" />; the default when unknown.</summary>
        public float3 Linear(int layer, int biome){
            float3[] colors = _linear[layer];
            return biome >= 0 && biome < colors.Length ? colors[biome] : _defaultLinear[layer];
        }

        private static float3 ToLinear(Color color){
            Color linear = color.linear;
            return new float3(linear.r, linear.g, linear.b);
        }
    }
}
