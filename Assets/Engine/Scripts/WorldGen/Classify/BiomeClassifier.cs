using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;

namespace Engine.Scripts.WorldGen.Classify{
    /// <summary>
    ///     Parameter-space classification (§6.4): d_b² = Σ_k λ_k (v_k − t_{b,k})² over T, F, C, E, PV (both
    ///     architectures) and h (Terrain-first only). PV and its target enter as (PV + 1)/2 so every dimension lives in
    ///     [0,1]. Hard classification is argmin_b d_b; ties break to the lower biome index (GEN-23).
    /// </summary>
    [BurstCompile]
    public static class BiomeClassifier{
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

        /// <summary>6D squared distance including normalised height <paramref name="h" /> (Terrain-first, §7).</summary>
        public static float DistanceSquared(in WorldFields f, float h, in Biome.BiomeDef b, in WorldGenConfig cfg){
            float dH = h - b.targetElevation;
            return DistanceSquared(f, b, cfg) + cfg.LambdaHeight * dH * dH;
        }

        /// <summary>Hard 5D classification: index of the nearest active biome, or -1 when the set is empty.</summary>
        public static int Classify(in WorldFields f, in WorldGenConfig cfg){
            int count = cfg.BiomeCount;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int b = 0; b < count; b++){
                float d = DistanceSquared(f, cfg.Biomes[b], cfg);
                if (d < bestDistance){ // strict: equal distances keep the lower index (GEN-23)
                    bestDistance = d;
                    best = b;
                }
            }

            return best;
        }

        /// <summary>Hard 6D classification with normalised height (Terrain-first), same tie rule.</summary>
        public static int Classify(in WorldFields f, float h, in WorldGenConfig cfg){
            int count = cfg.BiomeCount;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int b = 0; b < count; b++){
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
