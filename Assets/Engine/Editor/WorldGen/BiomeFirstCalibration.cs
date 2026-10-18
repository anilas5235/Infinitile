using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     BIO-20: "Suggest Biome-first height parameters from Terrain-first". Samples the Terrain-first pipeline of
    ///     the same settings over a region, groups the columns by their Terrain-first (6D) biome and suggests per
    ///     biome b: Offset_b = mean H, Jaggedness_b = mean J and HeightAmplitude_b from the standard deviation of the
    ///     detail residual r = H − Offset − J·P = Amp·Σ g_i N_i. Biome-first produces the detail term
    ///     HeightAmplitude_b · Σ w_{b,i} N_i with the biome's own octave weights w_b, so the amplitude that matches
    ///     the Terrain-first spread is std(r) / std(Σ w_{b,i} N_i) over the biome's columns. Clamped columns (GEN-12)
    ///     are skipped because their H no longer follows the formula.
    ///     <para>Pure computation: nothing is written to any asset. Applying a suggestion is the caller's click.</para>
    /// </summary>
    public static class BiomeFirstCalibration{
        /// <summary>Suggested Biome-first parameters for one biome next to its current values.</summary>
        public struct Suggestion{
            public int Biome;
            public string Name;

            /// <summary>Terrain-first columns classified as this biome (unclamped only).</summary>
            public int Count;

            public float CurrentOffset;
            public float CurrentAmplitude;
            public float CurrentJaggedness;
            public float Offset;
            public float Amplitude;
            public float Jaggedness;

            /// <summary>False when the biome's octave weights give no detail (all zero), so no amplitude follows.</summary>
            public bool AmplitudeAvailable;
        }

        /// <summary>
        ///     Computes suggestions for every active biome that occurs in the region. The settings are read as
        ///     Terrain-first regardless of their <see cref="WorldGenSettings.Architecture" /> (the architecture and
        ///     height method are forced in memory for the duration of the call and restored; nothing is marked
        ///     dirty). Needs a <see cref="WorldGenSettings.Splines" /> asset.
        /// </summary>
        /// <param name="settings">Settings to calibrate; the biomes' current values are reported next to the suggestions.</param>
        /// <param name="seed">World seed.</param>
        /// <param name="origin">Bottom-left block column of the region.</param>
        /// <param name="size">Samples per side.</param>
        /// <param name="stride">Block distance between samples (1 = every column).</param>
        /// <param name="suggestions">One entry per active biome, in biome order; biomes without columns have Count 0.</param>
        /// <param name="error">Why no suggestion could be made, or null.</param>
        public static bool TrySuggest(WorldGenSettings settings, int seed, int2 origin, int size, int stride,
            out Suggestion[] suggestions, out string error){
            suggestions = Array.Empty<Suggestion>();
            error = null;
            if (!settings){
                error = "No WorldGenSettings assigned.";
                return false;
            }

            if (!settings.Splines){
                error = $"{settings.name}: the Terrain-first reference needs a TerrainSplineSet (TRN-05).";
                return false;
            }

            if (size <= 0 || stride <= 0){
                error = "Region size and stride must be positive.";
                return false;
            }

            List<Biome> biomes = ActiveBiomes(settings);
            if (biomes.Count == 0){
                error = $"{settings.name}: no active biomes (GEN-21).";
                return false;
            }

            // Terrain-first view of the same settings. Forced in memory only (no SetDirty, no Undo): the asset keeps
            // its architecture and the TRN-21 warning for a configured height method is avoided.
            GenArchitecture architecture = settings.Architecture;
            BlendMethod heightMethod = settings.HeightMethod;
            WorldGenConfig cfg;
            try{
                settings.Architecture = GenArchitecture.TerrainFirst;
                settings.HeightMethod = BlendMethod.None;
                cfg = settings.ToConfig(seed, null, Allocator.TempJob);
            }
            catch (Exception e){
                error = e.Message;
                return false;
            }
            finally{
                settings.Architecture = architecture;
                settings.HeightMethod = heightMethod;
            }

            int biomeCount = cfg.BiomeCount;
            int octaveCount = cfg.OctaveCount;
            NativeArray<float> weights = new(biomeCount * octaveCount, Allocator.TempJob);
            NativeArray<double> stats = new(biomeCount * StatCount, Allocator.TempJob);
            try{
                for (int b = 0; b < biomeCount; b++){
                    float[] w = biomes[b].OctaveWeights;
                    for (int i = 0; i < octaveCount; i++)
                        weights[b * octaveCount + i] = w != null && i < w.Length ? w[i] : 0f;
                }

                new CalibrationJob{
                    Config = cfg, Weights = weights, Origin = origin, Size = size, Stride = stride, Stats = stats
                }.Run();

                suggestions = new Suggestion[biomeCount];
                for (int b = 0; b < biomeCount; b++){
                    Biome biome = biomes[b];
                    Suggestion s = new(){
                        Biome = b,
                        Name = biome.name,
                        CurrentOffset = biome.HeightOffset,
                        CurrentAmplitude = biome.HeightAmplitude,
                        CurrentJaggedness = biome.Jaggedness
                    };
                    double n = stats[b * StatCount + StatCount_N];
                    s.Count = (int)n;
                    if (n > 0){
                        double meanH = stats[b * StatCount + StatCount_SumH] / n;
                        double meanJ = stats[b * StatCount + StatCount_SumJ] / n;
                        double meanR = stats[b * StatCount + StatCount_SumR] / n;
                        double varR = math.max(0.0, stats[b * StatCount + StatCount_SumR2] / n - meanR * meanR);
                        double meanD = stats[b * StatCount + StatCount_SumD] / n;
                        double varD = math.max(0.0, stats[b * StatCount + StatCount_SumD2] / n - meanD * meanD);
                        s.Offset = (float)meanH;
                        s.Jaggedness = (float)meanJ;
                        s.AmplitudeAvailable = varD > 1e-12;
                        s.Amplitude = s.AmplitudeAvailable ? (float)math.sqrt(varR / varD) : biome.HeightAmplitude;
                    }

                    suggestions[b] = s;
                }

                return true;
            }
            finally{
                stats.Dispose();
                weights.Dispose();
                cfg.Dispose();
            }
        }

        /// <summary>Active biomes in config order (GEN-21): the first MaxBiomes non-null slots; a null slot stops the list.</summary>
        private static List<Biome> ActiveBiomes(WorldGenSettings settings){
            List<Biome> result = new();
            if (!settings.Biomes || settings.Biomes.Biomes == null) return result;
            int count = Math.Min(settings.Biomes.Count, BiomeSet.MaxBiomes);
            for (int i = 0; i < count; i++){
                Biome biome = settings.Biomes.Biomes[i];
                if (!biome) break; // ToConfig throws on empty slots; the caller sees that error
                result.Add(biome);
            }

            return result;
        }

        private const int StatCount = 7;
        private const int StatCount_N = 0;
        private const int StatCount_SumH = 1;
        private const int StatCount_SumJ = 2;
        private const int StatCount_SumR = 3;
        private const int StatCount_SumR2 = 4;
        private const int StatCount_SumD = 5;
        private const int StatCount_SumD2 = 6;

        /// <summary>
        ///     Per-biome sums over the region in double precision: n, ΣH, ΣJ, Σr, Σr², ΣD_b, ΣD_b² with
        ///     r = Amp(p) · Σ_i g_i N_i(p) (the Terrain-first detail residual) and D_b = Σ_i w_{b,i} N_i(p) (the
        ///     biome's own detail with its current octave weights). One sampler call per column (INT-10).
        /// </summary>
        [BurstCompile]
        private struct CalibrationJob : IJob{
            [ReadOnly] public WorldGenConfig Config;
            [ReadOnly] public NativeArray<float> Weights;
            public int2 Origin;
            public int Size;
            public int Stride;
            public NativeArray<double> Stats;

            public void Execute(){
                int octaveCount = Config.OctaveCount;
                for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++){
                    int wx = Origin.x + x * Stride, wz = Origin.y + y * Stride;
                    WorldSampler.SampleColumn(wx, wz, Config, out ColumnSample s);
                    if (s.Clamped || s.HardBiome < 0) continue;

                    float2 p = new(wx, wz);
                    FixedList64Bytes<float> n = DetailOctaves.SampleAll(p, Config.OctaveWavelengths, Config.Seed);
                    float detailGlobal = 0f, detailBiome = 0f;
                    for (int i = 0; i < octaveCount; i++){
                        detailGlobal += Config.GlobalOctaveWeights[i] * n[i];
                        detailBiome += Weights[s.HardBiome * octaveCount + i] * n[i];
                    }

                    double r = s.TerrainParams.y * detailGlobal;
                    int k = s.HardBiome * StatCount;
                    Stats[k + StatCount_N] += 1.0;
                    Stats[k + StatCount_SumH] += s.Height;
                    Stats[k + StatCount_SumJ] += s.TerrainParams.z;
                    Stats[k + StatCount_SumR] += r;
                    Stats[k + StatCount_SumR2] += r * r;
                    Stats[k + StatCount_SumD] += detailBiome;
                    Stats[k + StatCount_SumD2] += (double)detailBiome * detailBiome;
                }
            }
        }
    }
}
