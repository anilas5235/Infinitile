using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Builds the Burst-side <see cref="WorldGenConfig" /> from a <see cref="WorldGenSettings" /> asset: checks
    ///     the settings, flattens climate, octaves, biomes, splines, blend kernels and flora, and frees everything
    ///     allocated so far when a step throws.
    /// </summary>
    internal static class WorldGenConfigBuilder{
        /// <summary>
        ///     <see cref="WorldGenSettings.ToConfig" /> with block ids from <paramref name="voxelId" /> (the
        ///     registry, or a test's own id table); null leaves every block and flora voxel id at 0. Lying logs come
        ///     from <paramref name="variantOf" /> (base id, orientation → variant id); null keeps them upright. The
        ///     caller owns the result and must dispose it.
        /// </summary>
        internal static WorldGenConfig Build(WorldGenSettings settings, int seed,
            Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId, Func<ushort, byte, ushort> variantOf, Allocator allocator){
            string name = settings.name;
            int octaveCount = settings.OctaveCount;
            if (octaveCount == 0) throw new InvalidOperationException($"{name}: OctaveWavelengths is empty.");
            if (octaveCount > DetailOctaves.MaxOctaves)
                throw new InvalidOperationException($"{name}: more than {DetailOctaves.MaxOctaves} detail octaves.");
            foreach ((string field, NoiseProfileSettings profile) in settings.ClimateProfiles())
                if (profile == null) throw new InvalidOperationException($"{name}: {field} is not set.");
            foreach ((string field, float value) in settings.InvalidScales())
                throw new InvalidOperationException($"{name}: {field} must be positive and finite, got {value}.");
            // h = (H - Hmin)/(Hmax - Hmin) is a classification dimension: an empty range gives NaN.
            if (!(settings.HeightMin < settings.HeightMax) || float.IsInfinity(settings.HeightMin) ||
                float.IsInfinity(settings.HeightMax))
                throw new InvalidOperationException(
                    $"{name}: HeightMin ({settings.HeightMin}) must be finite and below HeightMax ({settings.HeightMax}).");
            if (settings.TreeSpacingRadius < 0)
                throw new InvalidOperationException(
                    $"{name}: TreeSpacingRadius must not be negative, got {settings.TreeSpacingRadius}.");

            CaveSettings caves = settings.Caves ?? throw new InvalidOperationException($"{name}: Caves is not set.");
            foreach ((string field, string problem) in caves.InvalidRanges())
                throw new InvalidOperationException($"{name}: Caves.{field} {problem}.");

            List<Biome> active = settings.ActiveBiomes();

            // The height stage does not exist in Terrain-first; a configured method is reported and ignored.
            BlendMethod heightMethod = settings.HeightMethod;
            if (settings.Architecture == GenArchitecture.TerrainFirst && heightMethod != BlendMethod.None){
                Debug.LogWarning($"{name}: HeightMethod {heightMethod} is ignored in Terrain-first; the height is already continuous.", settings);
                heightMethod = BlendMethod.None;
            }

            if (settings.Architecture == GenArchitecture.TerrainFirst && !settings.Splines)
                throw new InvalidOperationException($"{name}: Terrain-first needs a TerrainSplineSet.");
            foreach ((string field, float value) in settings.InvalidBlendStrengths(heightMethod))
                throw new InvalidOperationException($"{name}: {field} is invalid for the selected blend method, got {value}.");

            WorldGenConfig cfg = new(){
                Seed = (uint)seed,
                Architecture = settings.Architecture,
                MaterialMethod = settings.MaterialMethod,
                MaterialBlendWidth = settings.MaterialBlendWidth,
                MaterialSigma = settings.MaterialSigma,
                HeightMethod = heightMethod,
                HeightBlendWidth = settings.HeightBlendWidth,
                HeightSigma = settings.HeightSigma,
                WarpAmplitude = settings.WarpAmplitude,
                WarpWavelength = settings.WarpWavelength,
                WarpOctaves = settings.WarpOctaves,
                LambdaTemperature = settings.LambdaTemperature,
                LambdaHumidity = settings.LambdaHumidity,
                LambdaContinental = settings.LambdaContinental,
                LambdaErosion = settings.LambdaErosion,
                LambdaPV = settings.LambdaPV,
                LambdaHeight = settings.LambdaHeight,
                HeightMin = settings.HeightMin,
                HeightMax = settings.HeightMax,
                PeakWavelength = settings.PeakWavelength,
                WaterLevel = settings.WaterLevel,
                WaterVoxel = Id(settings.WaterVoxel, voxelId),
                IceVoxel = Id(settings.IceVoxel, voxelId),
                EnableClimateOverrides = settings.EnableClimateOverrides,
                FreezeTemperature = settings.FreezeTemperature,
                SnowLapse = settings.SnowLapse,
                FreezeNoiseAmplitude = settings.FreezeNoiseAmplitude,
                FreezeNoiseWavelength = settings.FreezeNoiseWavelength,
                StrataPeriod = settings.StrataPeriod,
                StrataThickness = settings.StrataThickness,
                StrataWarpAmplitude = settings.StrataWarpAmplitude,
                StrataWarpWavelength = settings.StrataWarpWavelength,
                BeachWavelength = settings.BeachWavelength,
                BeachThreshold = math.saturate(settings.BeachThreshold),
                BeachRamp = math.max(settings.BeachRamp, .01f),
                BeachWaterDistance = math.max(settings.BeachWaterDistance, 0),
                FloraDensityWavelength = new float4(settings.TreesDensityWavelength, settings.UndergrowthDensityWavelength,
                    settings.FlowersDensityWavelength, settings.GroundDetailsDensityWavelength),
                FloraDensityContrast = new float4(settings.TreesDensityContrast, settings.UndergrowthDensityContrast,
                    settings.FlowersDensityContrast, settings.GroundDetailsDensityContrast)
            };

            // Biome.ToStruct and TerrainSplineSet.Flatten throw on bad data; free what was allocated so far before
            // rethrowing. Every allocation is stored in cfg as soon as it exists, so Dispose finds it.
            try{
                BuildClimate(settings, seed, ref cfg.Climate);
                cfg.OctaveWavelengths = new NativeArray<float>(settings.OctaveWavelengths, allocator);
                cfg.GlobalOctaveWeights = new NativeArray<float>(octaveCount, allocator);
                cfg.Biomes = new NativeArray<Biome.BiomeDef>(active.Count, allocator);
                cfg.OctaveCoefficients = new NativeArray<float>(active.Count * octaveCount, allocator);

                if (settings.Splines){
                    settings.Splines.Flatten(allocator, out cfg.SplineNodes, out cfg.SplinePoints,
                        out cfg.OffsetSpline, out cfg.AmpSpline, out cfg.JaggednessSpline);
                }
                else{
                    cfg.SplineNodes = new NativeArray<SplineNode>(0, allocator);
                    cfg.SplinePoints = new NativeArray<SplinePoint>(0, allocator);
                }

                BuildKernels(settings, heightMethod, allocator, ref cfg);

                float[] globalWeights = settings.GlobalOctaveWeights;
                for (int i = 0; i < octaveCount; i++)
                    cfg.GlobalOctaveWeights[i] = globalWeights != null && i < globalWeights.Length
                        ? globalWeights[i]
                        : 0f;

                for (int b = 0; b < active.Count; b++){
                    Biome biome = active[b];
                    cfg.Biomes[b] = voxelId != null ? biome.ToStruct(voxelId) : biome.ToStruct();
                    if (cfg.Biomes[b].beachHeight != 0) cfg.HasBeaches = true;
                    for (int i = 0; i < octaveCount; i++){
                        // a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i}; missing weights count as 0.
                        float w = biome.OctaveWeights != null && i < biome.OctaveWeights.Length ? biome.OctaveWeights[i] : 0f;
                        cfg.OctaveCoefficients[b * octaveCount + i] = biome.HeightAmplitude * w;
                    }
                }

                // Flora rules, species, structures.
                FloraConfigBuilder.Build(active, voxelId, variantOf, settings.TreeSpacingRadius, settings.FloraDensityNoise, allocator, ref cfg, settings);

                BuildCaves(caves, voxelId, allocator, ref cfg.Caves);
            }
            catch{
                cfg.Dispose();
                throw;
            }

            return cfg;
        }

        /// <summary>Voxel id of <paramref name="voxel" />; 0 without a voxel or an id table.</summary>
        private static ushort Id(VoxelConfig.Data.Voxel.Voxel voxel, Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId){
            return voxel && voxelId != null ? voxelId(voxel) : (ushort)0;
        }

        /// <summary>
        ///     The cave passes' values, with angles in radians and the ore rules flattened; a rule without its ore or
        ///     host voxel is left out.
        /// </summary>
        private static void BuildCaves(CaveSettings s, Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId, Allocator allocator,
            ref CaveConfig caves){
            caves = new CaveConfig{
                Enabled = s.Enabled,
                Cover = math.max(s.Cover, 0),
                WaterCover = math.max(s.WaterCover, 1),
                EntranceWaterDistance = math.max(s.EntranceWaterDistance, 0),
                CavernWavelength = new float2(s.CavernWavelength, s.CavernVerticalWavelength),
                CavernThreshold = s.CavernThreshold,
                CavernEdgeRise = math.max(s.CavernEdgeRise, 0f),
                CavernBandMin = math.max(s.CavernBandMin, BottomLayer.Height),
                CavernBandMax = s.CavernBandMax,
                CavernMaskWavelength = s.CavernMaskWavelength,
                CavernMaskThreshold = s.CavernMaskThreshold,
                PillarWavelength = new float2(s.PillarWavelength, s.PillarVerticalWavelength),
                PillarThreshold = s.PillarThreshold,
                WormCellSize = s.WormCellSize,
                WormsPerCell = math.clamp(s.WormsPerCell, 0f, 2f),
                WormStartMinY = s.WormStartMinY,
                WormLength = new int2(s.WormLength.Min, s.WormLength.Max),
                WormMinRadius = s.WormMinRadius,
                WormMaxRadius = s.WormMaxRadius,
                WormRadiusPeriod = s.WormRadiusPeriod,
                WormReach = s.WormReach,
                WormMaxPitch = math.radians(math.clamp(s.WormMaxPitch, 0f, 89f)),
                WormYawJitter = math.radians(math.max(s.WormYawJitter, 0f)),
                WormPitchJitter = math.radians(math.max(s.WormPitchJitter, 0f)),
                WormTurnDamping = math.saturate(s.WormTurnDamping),
                WormBranchChance = math.saturate(s.WormBranchChance),
                WormMinY = math.max(s.WormMinY, BottomLayer.Height),
                WormTargets = math.clamp(s.WormTargets, 0, Worms.MaxTargets),
                WormSteer = math.saturate(s.WormSteer),
                WormBridges = s.WormBridges,
                EntranceWavelength = s.EntranceWavelength,
                EntranceThreshold = s.EntranceThreshold,
                EntranceStartDepth = math.max(s.EntranceStartDepth, 0),
                LavaVoxel = Id(s.LavaVoxel, voxelId),
                LavaLevel = s.LavaLevel,
                LavaMaskWavelength = s.LavaMaskWavelength,
                LavaMaskThreshold = s.LavaMaskThreshold,
                LavaPocketWavelength = new float2(s.LavaPocketWavelength, s.LavaPocketVerticalWavelength),
                LavaPocketThreshold = s.LavaPocketThreshold,
                LavaPocketEdgeRise = math.max(s.LavaPocketEdgeRise, 0f),
                LavaPocketBandMin = math.max(s.LavaPocketBandMin, BottomLayer.Height),
                LavaPocketBandMax = s.LavaPocketBandMax,
                BedrockVoxel = Id(s.BedrockVoxel, voxelId),
                BedrockChance1 = math.saturate(s.BedrockChance1),
                BedrockChance2 = math.saturate(s.BedrockChance2)
            };

            List<OreRuleDef> ores = new();
            if (s.Ores != null)
                foreach (OreRule rule in s.Ores){
                    if (rule == null || !rule.Ore || !rule.Host) continue;
                    ores.Add(new OreRuleDef{
                        Ore = Id(rule.Ore, voxelId),
                        Host = Id(rule.Host, voxelId),
                        MinY = rule.MinY,
                        MaxY = rule.MaxY,
                        VeinsPerCell = math.max(rule.VeinsPerCell, 0f),
                        VeinSize = new int2(rule.VeinSize.Min, rule.VeinSize.Max)
                    });
                }

            caves.Ores = new NativeArray<OreRuleDef>(ores.ToArray(), allocator);
        }

        /// <summary>
        ///     Climate instances T, F, C, E, W from the asset. Each instance gets its own gradient salt. Fills
        ///     <paramref name="climate" /> one profile at a time, so a throw leaves only disposable state.
        /// </summary>
        private static void BuildClimate(WorldGenSettings settings, int seed, ref ClimateParams climate){
            climate.ContinentalHumidityInfluence = settings.ContinentalHumidityInfluence;
            climate.ContinentalTemperatureInfluence = settings.ContinentalTemperatureInfluence;
            climate.WGain = settings.WGain;
            climate.FrequencyScale = settings.ClimateFrequencyScale;
            climate.Temperature = new NoiseProfile(settings.temperatureProfile.ToStruct(seed, HashSalts.TemperatureNoise));
            climate.Humidity = new NoiseProfile(settings.humidityProfile.ToStruct(seed, HashSalts.HumidityNoise));
            climate.Continental = new WarpedNoiseLayer(settings.continentalLayer.ToStruct(seed,
                HashSalts.ContinentalNoise, HashSalts.ContinentalWarpX, HashSalts.ContinentalWarpY));
            climate.Erosion = new NoiseProfile(settings.erosionProfile.ToStruct(seed, HashSalts.ErosionNoise));
            climate.Weirdness = new NoiseProfile(settings.weirdnessProfile.ToStruct(seed, HashSalts.WeirdnessNoise));
        }

        /// <summary>
        ///     Kernel taps once per settings change; empty arrays for stages without a spatial kernel. Also decides
        ///     whether both stages can share one set of weights.
        /// </summary>
        private static void BuildKernels(WorldGenSettings settings, BlendMethod heightMethod, Allocator allocator,
            ref WorldGenConfig cfg){
            bool biomeFirst = settings.Architecture == GenArchitecture.BiomeFirst;
            BlendMethod materialMethod = settings.MaterialMethod;
            if (materialMethod == BlendMethod.DistanceBlend)
                cfg.MaterialKernel = KernelSet.Build(settings.MaterialBlendWidth, allocator, out cfg.MaterialTaps);
            else cfg.MaterialTaps = new NativeArray<KernelTap>(0, allocator);
            if (biomeFirst && heightMethod is BlendMethod.DistanceBlend or BlendMethod.DistortedBoundary)
                cfg.HeightKernel = KernelSet.Build(settings.HeightBlendWidth, allocator, out cfg.HeightTaps);
            else cfg.HeightTaps = new NativeArray<KernelTap>(0, allocator);
            cfg.StagesShareWeights = biomeFirst && materialMethod == heightMethod && materialMethod switch{
                BlendMethod.None => true,
                BlendMethod.DistanceBlend => settings.MaterialBlendWidth == settings.HeightBlendWidth,
                BlendMethod.ParameterSpaceWeighting => settings.MaterialSigma == settings.HeightSigma,
                _ => false // DistortedBoundary: warp only (material) vs. warp + distance blend (height)
            };
        }
    }
}
