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
                ["TreeSpacingRadius"] = s.TreeSpacingRadius,
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
                        ["MinHeightAboveWater"] = rule.MinHeightAboveWater,
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
                ["StrataBlock"] = VoxelName(b.StrataBlock),
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
                            ["CrownShape"] = v.CrownShape.ToString(),
                            ["LeafVoxel"] = VoxelName(v.LeafVoxel),
                            ["AltLeafVoxel"] = VoxelName(v.AltLeafVoxel),
                            ["CrownRadius"] = Range(v.CrownRadius),
                            ["CrownTopRadius"] = Range(v.CrownTopRadius),
                            ["CrownHeight"] = Range(v.CrownHeight),
                            ["CrownTop"] = v.CrownTop,
                            ["CrownMinBase"] = v.CrownMinBase,
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
