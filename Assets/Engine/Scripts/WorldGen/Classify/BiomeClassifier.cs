using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;

namespace Engine.Scripts.WorldGen.Classify{
    /// <summary>
    ///     Parameter-space classification: d_b² = Σ_k λ_k (v_k − t_{b,k})² over T, F, C, E, PV (both
    ///     architectures) and h (Terrain-first only). PV and its target enter as (PV + 1)/2 so every dimension lives in
    ///     [0,1]. Hard classification is argmin_b d_b; ties break to the lower biome index.
    /// </summary>
    [BurstCompile]
    public static class BiomeClassifier{
        /// <summary>
        ///     Distance of a biome a column may not take (an underwater biome on a dry column). Finite, so the
        ///     parameter-space blend weight exp(−(d − d_min)/2σ²) is exactly 0 without an infinity.
        /// </summary>
        public const float Excluded = 1e30f;

        /// <summary>5D squared distance of the column fields to biome <paramref name="b" />.</summary>
        public static float DistanceSquared(in WorldFields f, in Biome.BiomeDef b, in WorldGenConfig cfg){
            float dT = f.T - b.targetTemperature;
            float dF = f.F - b.targetHumidity;
            float dC = f.C - b.targetContinental;
            float dE = f.E - b.targetErosion;
            float dPV = (f.PV + 1f) * 0.5f - (b.targetPV + 1f) * 0.5f;
            return cfg.LambdaTemperature * dT * dT
                   + cfg.LambdaHumidity * dF * dF
                   + cfg.LambdaContinental * dC * dC
                   + cfg.LambdaErosion * dE * dE
                   + cfg.LambdaPV * dPV * dPV;
        }

        /// <summary>
        ///     6D squared distance including normalised height <paramref name="h" /> (Terrain-first). A biome whose
        ///     target height is a floor (<c>elevationIsFloor</c>) pays no height distance above its target.
        /// </summary>
        public static float DistanceSquared(in WorldFields f, float h, in Biome.BiomeDef b, in WorldGenConfig cfg){
            float dH = h - b.targetElevation;
            if (b.elevationIsFloor != 0 && dH > 0f) dH = 0f;
            return DistanceSquared(f, b, cfg) + cfg.LambdaHeight * dH * dH;
        }

        /// <summary>Hard 5D classification: index of the nearest active biome, or -1 when the set is empty.</summary>
        public static int Classify(in WorldFields f, in WorldGenConfig cfg){
            int count = cfg.BiomeCount;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int b = 0; b < count; b++){
                float d = DistanceSquared(f, cfg.Biomes[b], cfg);
                if (d < bestDistance){ // strict: equal distances keep the lower index
                    bestDistance = d;
                    best = b;
                }
            }

            return best;
        }

        /// <summary>
        ///     Every active biome's squared distance d_b² into <paramref name="distancesSq" /> (slot b; a 16-float
        ///     vector, not weights) and the hard class argmin_b d_b with the same tie rule as <see cref="Classify(in WorldFields,in WorldGenConfig)" />.
        ///     6D with <paramref name="h" /> when <paramref name="withHeight" /> (Terrain-first), else 5D. Computed once
        ///     per column and shared by the hard class and method 4.
        /// </summary>
        public static int Distances(in WorldFields f, float h, bool withHeight, in WorldGenConfig cfg, out BlendWeights distancesSq){
            return Distances(f, h, withHeight, false, cfg, out distancesSq);
        }

        /// <summary>
        ///     <see cref="Distances(in WorldFields,float,bool,in WorldGenConfig,out BlendWeights)" /> for a column whose top
        ///     block is <paramref name="dry" /> (not under water): a <see cref="Biome.BiomeDef.isUnderwaterBiome" /> biome then
        ///     gets <see cref="Excluded" /> and never wins.
        /// </summary>
        public static int Distances(in WorldFields f, float h, bool withHeight, bool dry, in WorldGenConfig cfg,
            out BlendWeights distancesSq){
            distancesSq = default;
            int count = cfg.BiomeCount;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int b = 0; b < count; b++){
                float d = dry && cfg.Biomes[b].isUnderwaterBiome != 0 ? Excluded
                    : withHeight ? DistanceSquared(f, h, cfg.Biomes[b], cfg) : DistanceSquared(f, cfg.Biomes[b], cfg);
                distancesSq[b] = d;
                if (d < bestDistance){
                    bestDistance = d;
                    best = b;
                }
            }

            return best;
        }

        /// <summary>Hard 6D classification with normalised height (Terrain-first), same tie rule.</summary>
        public static int Classify(in WorldFields f, float h, in WorldGenConfig cfg){
            return Classify(f, h, false, cfg);
        }

        /// <summary>
        ///     <see cref="Classify(in WorldFields,float,in WorldGenConfig)" /> for a column whose top block is
        ///     <paramref name="dry" />: <see cref="Biome.BiomeDef.isUnderwaterBiome" /> biomes are skipped then.
        /// </summary>
        public static int Classify(in WorldFields f, float h, bool dry, in WorldGenConfig cfg){
            int count = cfg.BiomeCount;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int b = 0; b < count; b++){
                if (dry && cfg.Biomes[b].isUnderwaterBiome != 0) continue;
                float d = DistanceSquared(f, h, cfg.Biomes[b], cfg);
                if (d < bestDistance){
                    bestDistance = d;
                    best = b;
                }
            }

            return best;
        }
    }
}
