using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Flora;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Canonical JSON tree of a <see cref="WorldGenSettings" /> asset: every parameter that influences generation,
    ///     with the <see cref="BiomeSet" /> and <see cref="TerrainSplineSet" /> contents inlined. Run manifests hash
    ///     the text, so keys and values must not change for unchanged settings.
    /// </summary>
    internal static class WorldGenSettingsJson{
        /// <summary>Serialized tree of <paramref name="settings" />; <paramref name="seed" /> adds the world seed.</summary>
        internal static string Serialize(WorldGenSettings settings, int? seed){
            return CanonicalJson.Serialize(ToTree(settings, seed));
        }

        private static Dictionary<string, object> ToTree(WorldGenSettings s, int? seed){
            Dictionary<string, object> tree = new(){
                ["Architecture"] = s.Architecture,
                ["MaterialMethod"] = s.MaterialMethod,
                ["MaterialBlendWidth"] = s.MaterialBlendWidth,
                ["MaterialSigma"] = s.MaterialSigma,
                ["HeightMethod"] = s.HeightMethod,
                ["HeightBlendWidth"] = s.HeightBlendWidth,
                ["HeightSigma"] = s.HeightSigma,
                ["WarpAmplitude"] = s.WarpAmplitude,
                ["WarpWavelength"] = s.WarpWavelength,
                ["WarpOctaves"] = s.WarpOctaves,
                ["LambdaTemperature"] = s.LambdaTemperature,
                ["LambdaHumidity"] = s.LambdaHumidity,
                ["LambdaContinental"] = s.LambdaContinental,
                ["LambdaErosion"] = s.LambdaErosion,
                ["LambdaPV"] = s.LambdaPV,
                ["LambdaHeight"] = s.LambdaHeight,
                ["HeightMin"] = s.HeightMin,
                ["HeightMax"] = s.HeightMax,
                ["WaterLevel"] = s.WaterLevel,
                ["WaterVoxel"] = VoxelName(s.WaterVoxel),
                ["IceVoxel"] = VoxelName(s.IceVoxel),
                ["OctaveWavelengths"] = s.OctaveWavelengths ?? Array.Empty<float>(),
                ["GlobalOctaveWeights"] = s.GlobalOctaveWeights ?? Array.Empty<float>(),
                ["PeakWavelength"] = s.PeakWavelength,
                ["WGain"] = s.WGain,
                ["ClimateFrequencyScale"] = s.ClimateFrequencyScale,
                ["TemperatureProfile"] = ProfileTree(s.temperatureProfile),
                ["HumidityProfile"] = ProfileTree(s.humidityProfile),
                ["ContinentalLayer"] = LayerTree(s.continentalLayer),
                ["ContinentalHumidityInfluence"] = s.ContinentalHumidityInfluence,
                ["ContinentalTemperatureInfluence"] = s.ContinentalTemperatureInfluence,
                ["ErosionProfile"] = ProfileTree(s.erosionProfile),
                ["WeirdnessProfile"] = ProfileTree(s.weirdnessProfile),
                ["EnableClimateOverrides"] = s.EnableClimateOverrides,
                ["FreezeTemperature"] = s.FreezeTemperature,
                ["SnowLapse"] = s.SnowLapse,
                ["FreezeNoiseAmplitude"] = s.FreezeNoiseAmplitude,
                ["FreezeNoiseWavelength"] = s.FreezeNoiseWavelength,
                ["StrataPeriod"] = s.StrataPeriod,
                ["StrataThickness"] = s.StrataThickness,
                ["StrataWarpAmplitude"] = s.StrataWarpAmplitude,
                ["StrataWarpWavelength"] = s.StrataWarpWavelength,
                ["BeachWavelength"] = s.BeachWavelength,
                ["BeachThreshold"] = s.BeachThreshold,
                ["BeachRamp"] = s.BeachRamp,
                ["BeachWaterDistance"] = s.BeachWaterDistance,
                ["TreeSpacingRadius"] = s.TreeSpacingRadius,
                ["FloraDensityNoise"] = s.FloraDensityNoise,
                ["TreesDensityWavelength"] = s.TreesDensityWavelength,
                ["TreesDensityContrast"] = s.TreesDensityContrast,
                ["UndergrowthDensityWavelength"] = s.UndergrowthDensityWavelength,
                ["UndergrowthDensityContrast"] = s.UndergrowthDensityContrast,
                ["FlowersDensityWavelength"] = s.FlowersDensityWavelength,
                ["FlowersDensityContrast"] = s.FlowersDensityContrast,
                ["GroundDetailsDensityWavelength"] = s.GroundDetailsDensityWavelength,
                ["GroundDetailsDensityContrast"] = s.GroundDetailsDensityContrast,
                ["Caves"] = CaveTree(s.Caves),
                ["Biomes"] = BiomeSetTree(s.Biomes),
                ["Splines"] = s.Splines ? s.Splines.ToTree() : null
            };
            if (seed.HasValue) tree["Seed"] = seed.Value;
            return tree;
        }

        private static Dictionary<string, object> ProfileTree(NoiseProfileSettings p){
            if (p == null) return null;
            return new Dictionary<string, object>{
                ["Scale"] = p.scale,
                ["Persistance"] = p.persistance,
                ["Lacunarity"] = p.lacunarity,
                ["Octaves"] = p.octaves
            };
        }

        private static Dictionary<string, object> LayerTree(WarpedNoiseLayerSettings l){
            if (l == null) return null;
            List<object> curve = new();
            if (l.redistributionCurve != null)
                foreach (Keyframe k in l.redistributionCurve.keys) curve.Add(new[]{ k.time, k.value });
            return new Dictionary<string, object>{
                ["BaseNoise"] = ProfileTree(l.baseNoise),
                ["WarpNoise"] = ProfileTree(l.warpNoise),
                ["WarpStrength"] = l.warpStrength,
                ["RedistributionCurve"] = curve
            };
        }

        private static Dictionary<string, object> CaveTree(CaveSettings c){
            if (c == null) return null;
            List<object> ores = new();
            if (c.Ores != null)
                foreach (OreRule rule in c.Ores)
                    ores.Add(rule == null
                        ? null
                        : new Dictionary<string, object>{
                            ["Ore"] = VoxelName(rule.Ore),
                            ["Host"] = VoxelName(rule.Host),
                            ["MinY"] = rule.MinY,
                            ["MaxY"] = rule.MaxY,
                            ["VeinsPerCell"] = rule.VeinsPerCell,
                            ["VeinSize"] = Range(rule.VeinSize)
                        });

            return new Dictionary<string, object>{
                ["Enabled"] = c.Enabled,
                ["Cover"] = c.Cover,
                ["CavernWavelength"] = c.CavernWavelength,
                ["CavernVerticalWavelength"] = c.CavernVerticalWavelength,
                ["CavernThreshold"] = c.CavernThreshold,
                ["CavernEdgeRise"] = c.CavernEdgeRise,
                ["CavernFloorRise"] = c.CavernFloorRise,
                ["CavernBandMin"] = c.CavernBandMin,
                ["CavernBandMax"] = c.CavernBandMax,
                ["CavernMaskWavelength"] = c.CavernMaskWavelength,
                ["CavernMaskThreshold"] = c.CavernMaskThreshold,
                ["PillarWavelength"] = c.PillarWavelength,
                ["PillarVerticalWavelength"] = c.PillarVerticalWavelength,
                ["PillarThreshold"] = c.PillarThreshold,
                ["CavernDetailWavelength"] = c.CavernDetailWavelength,
                ["CavernDetailAmplitude"] = c.CavernDetailAmplitude,
                ["CavernDetailRidged"] = c.CavernDetailRidged,
                ["CavernWarpWavelength"] = c.CavernWarpWavelength,
                ["CavernWarpAmplitude"] = c.CavernWarpAmplitude,
                ["WormCellSize"] = c.WormCellSize,
                ["WormsPerCell"] = c.WormsPerCell,
                ["WormStartMinY"] = c.WormStartMinY,
                ["WormLength"] = Range(c.WormLength),
                ["WormMinRadius"] = c.WormMinRadius,
                ["WormMaxRadius"] = c.WormMaxRadius,
                ["WormRadiusPeriod"] = c.WormRadiusPeriod,
                ["WormReach"] = c.WormReach,
                ["WormMaxPitch"] = c.WormMaxPitch,
                ["WormYawJitter"] = c.WormYawJitter,
                ["WormPitchJitter"] = c.WormPitchJitter,
                ["WormTurnDamping"] = c.WormTurnDamping,
                ["WormBranchChance"] = c.WormBranchChance,
                ["WormMinY"] = c.WormMinY,
                ["WormLinks"] = c.WormLinks,
                ["WormHallLinks"] = c.WormHallLinks,
                ["WormSteer"] = c.WormSteer,
                ["EntranceWavelength"] = c.EntranceWavelength,
                ["EntranceThreshold"] = c.EntranceThreshold,
                ["EntranceStartDepth"] = c.EntranceStartDepth,
                ["LavaVoxel"] = VoxelName(c.LavaVoxel),
                ["LavaLevel"] = c.LavaLevel,
                ["LavaMaskWavelength"] = c.LavaMaskWavelength,
                ["LavaMaskThreshold"] = c.LavaMaskThreshold,
                ["LavaPocketWavelength"] = c.LavaPocketWavelength,
                ["LavaPocketVerticalWavelength"] = c.LavaPocketVerticalWavelength,
                ["LavaPocketThreshold"] = c.LavaPocketThreshold,
                ["LavaPocketEdgeRise"] = c.LavaPocketEdgeRise,
                ["LavaPocketBandMin"] = c.LavaPocketBandMin,
                ["LavaPocketBandMax"] = c.LavaPocketBandMax,
                ["Ores"] = ores,
                ["BedrockVoxel"] = VoxelName(c.BedrockVoxel),
                ["BedrockChance1"] = c.BedrockChance1,
                ["BedrockChance2"] = c.BedrockChance2
            };
        }

        private static List<object> BiomeSetTree(BiomeSet biomes){
            List<object> list = new();
            if (biomes == null || biomes.Biomes == null) return list;
            foreach (Biome biome in biomes.Biomes) list.Add(biome ? BiomeTree(biome) : null);
            return list;
        }

        private static Dictionary<string, object> BiomeTree(Biome b){
            List<object> flora = new();
            if (b.FloraRules != null)
                foreach (FloraRule rule in b.FloraRules)
                    flora.Add(new Dictionary<string, object>{
                        ["SpeciesVoxel"] = VoxelName(rule.SpeciesVoxel),
                        ["SpeciesStructure"] = rule.SpeciesStructure ? rule.SpeciesStructure.name : null,
                        ["SpeciesTree"] = rule.SpeciesTree ? TreeTree(rule.SpeciesTree) : null,
                        ["Probability"] = rule.Probability,
                        ["RequiresSurfaceFromSameBiome"] = rule.RequiresSurfaceFromSameBiome,
                        ["RequiresBeachGround"] = rule.RequiresBeachGround,
                        ["MinHeightAboveWater"] = rule.MinHeightAboveWater,
                        ["DensityNoise"] = rule.DensityNoise.ToString(),
                        ["DensityInfluence"] = rule.DensityInfluence,
                        ["MinStackHeight"] = rule.MinStackHeight,
                        ["MaxStackHeight"] = rule.MaxStackHeight
                    });

            return new Dictionary<string, object>{
                ["Name"] = b.name,
                ["TopBlock"] = VoxelName(b.TopBlock),
                ["UnderwaterTopBlock"] = VoxelName(b.UnderwaterTopBlock),
                ["UnderBlock"] = VoxelName(b.UnderBlock),
                ["StoneBlock"] = VoxelName(b.StoneBlock),
                ["SnowTopBlock"] = VoxelName(b.SnowTopBlock),
                ["FreezesWater"] = b.FreezesWater,
                ["StrataBlock"] = VoxelName(b.StrataBlock),
                ["BeachBlock"] = VoxelName(b.BeachBlock),
                ["BeachHeight"] = b.BeachHeight,
                ["TargetTemperature"] = b.TargetTemperature,
                ["TargetHumidity"] = b.TargetHumidity,
                ["TargetContinental"] = b.TargetContinental,
                ["TargetErosion"] = b.TargetErosion,
                ["TargetPV"] = b.TargetPV,
                ["TargetHeight"] = b.TargetHeight,
                ["HeightOffset"] = b.HeightOffset,
                ["HeightAmplitude"] = b.HeightAmplitude,
                ["OctaveWeights"] = b.OctaveWeights ?? Array.Empty<float>(),
                ["Jaggedness"] = b.Jaggedness,
                ["FloraRules"] = flora
            };
        }

        /// <summary>A tree species with all its variants inlined: their values shape the world like the biome fields.</summary>
        private static Dictionary<string, object> TreeTree(TreeSpecies species){
            List<object> variants = new();
            if (species.Variants != null)
                foreach (TreeVariant v in species.Variants)
                    variants.Add(v == null
                        ? null
                        : new Dictionary<string, object>{
                            ["Name"] = v.Name,
                            ["Weight"] = v.Weight,
                            ["TrunkVoxel"] = VoxelName(v.TrunkVoxel),
                            ["TrunkHeight"] = Range(v.TrunkHeight),
                            ["TrunkWidth"] = v.TrunkWidth,
                            ["MaxLean"] = v.MaxLean,
                            ["Lying"] = v.Lying,
                            ["CrownShape"] = v.CrownShape.ToString(),
                            ["LeafVoxel"] = VoxelName(v.LeafVoxel),
                            ["AltLeafVoxel"] = VoxelName(v.AltLeafVoxel),
                            ["CrownRadius"] = Range(v.CrownRadius),
                            ["CrownTopRadius"] = Range(v.CrownTopRadius),
                            ["CrownHeight"] = Range(v.CrownHeight),
                            ["CrownBase"] = v.CrownBase,
                            ["CrownMinBase"] = v.CrownMinBase,
                            ["BranchChance"] = v.BranchChance,
                            ["BranchCount"] = Range(v.BranchCount),
                            ["BranchStyle"] = v.BranchStyle.ToString(),
                            ["BranchLength"] = Range(v.BranchLength),
                            ["BranchBandMin"] = v.BranchBandMin,
                            ["BranchBandMax"] = v.BranchBandMax,
                            ["BranchLeafRadius"] = Range(v.BranchLeafRadius),
                            ["HoleChance"] = v.HoleChance,
                            ["BumpChance"] = v.BumpChance,
                            ["HangChance"] = v.HangChance,
                            ["AltLeafChance"] = v.AltLeafChance,
                            ["Sink"] = Range(v.Sink),
                            ["SpacingRadius"] = v.SpacingRadius
                        });

            return new Dictionary<string, object>{
                ["Name"] = species.name,
                ["Variants"] = variants
            };
        }

        private static int[] Range(IntRange range){
            return new[]{ range.Min, range.Max };
        }

        private static string VoxelName(VoxelConfig.Data.Voxel.Voxel voxel){
            if (!voxel) return null;
            return voxel.package ? voxel.GetFullName().ToString() : voxel.name;
        }
    }
}
