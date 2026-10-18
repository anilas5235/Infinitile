using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Height{
    /// <summary>
    ///     Terrain-first height (§7.1, TRN-03): H = Offset(C,E,PV) + Amp(C,E,PV) · Σ_i g_i N_i + J(C,E,PV) · P with
    ///     the global octave weights g and the shared detail octaves N_i and peak noise P (GEN-11). T and F never
    ///     enter. Amp replaces Minecraft's "factor" (TRN-04); J is an own extension.
    /// </summary>
    [BurstCompile]
    public static class TerrainFirstHeight{
        /// <summary>Offset, Amp and J at the column's (C, E, PV), as (x, y, z).</summary>
        public static float3 SampleParams(in WorldFields f, in WorldGenConfig cfg){
            return new float3(
                NestedSpline.Evaluate(cfg.OffsetSpline, f.C, f.E, f.PV, cfg.SplineNodes, cfg.SplinePoints),
                NestedSpline.Evaluate(cfg.AmpSpline, f.C, f.E, f.PV, cfg.SplineNodes, cfg.SplinePoints),
                NestedSpline.Evaluate(cfg.JaggednessSpline, f.C, f.E, f.PV, cfg.SplineNodes, cfg.SplinePoints));
        }

        /// <summary>
        ///     H at world position <paramref name="p" /> with fields <paramref name="f" />. <paramref name="height" />
        ///     is clamped to [Hmin, Hmax], <paramref name="unclamped" /> is H before the clamp (GEN-12).
        /// </summary>
        public static void Sample(in float2 p, in WorldFields f, in WorldGenConfig cfg,
            out float3 terrainParams, out float height, out float unclamped){
            terrainParams = SampleParams(f, cfg);

            int octaveCount = cfg.OctaveCount;
            float detail = 0f;
            for (int i = 0; i < octaveCount; i++){
                float g = cfg.GlobalOctaveWeights[i];
                if (g != 0f) detail += g * DetailOctaves.Sample(i, p, cfg.OctaveWavelengths[i], cfg.Seed);
            }

            float h = terrainParams.x + terrainParams.y * detail;
            if (terrainParams.z != 0f) h += terrainParams.z * PeakNoise.Sample(p, cfg.PeakWavelength, cfg.Seed);

            unclamped = h;
            height = math.clamp(h, cfg.HeightMin, cfg.HeightMax);
        }
    }
}
