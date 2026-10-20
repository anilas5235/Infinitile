using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;
using VoxelSo = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Scripts.WorldGen.Flora{
    /// <summary>
    ///     Builds the flora part of <see cref="WorldGenConfig" /> from the active biomes (INT-03, §10): the flattened
    ///     rules with their cumulative intervals, the species table, the expanded structures and the voxel replace
    ///     classes. Managed, once per settings change.
    /// </summary>
    internal static class FloraConfigBuilder{
        /// <summary>
        ///     Species in id order (id = index + 1, VEG-06): every distinct voxel or structure named by a valid rule of
        ///     an active biome, in biome and rule order.
        /// </summary>
        internal static List<Object> CollectSpecies(IReadOnlyList<Biome> biomes){
            List<Object> species = new();
            foreach (Biome biome in biomes){
                if (biome.FloraRules == null) continue;
                foreach (FloraRule rule in biome.FloraRules){
                    if (rule == null || !rule.IsValid) continue;
                    Object key = Species(rule);
                    if (!species.Contains(key)) species.Add(key);
                }
            }

            return species;
        }

        private static Object Species(FloraRule rule){
            return rule.SpeciesStructure ? rule.SpeciesStructure : rule.SpeciesVoxel;
        }

        /// <summary>
        ///     Fills the flora fields of <paramref name="cfg" /> and the rule ranges of <c>cfg.Biomes</c> (which must
        ///     already hold the biome structs). <paramref name="voxelId" /> null leaves every voxel id at 0 (preview):
        ///     the decision (species, source) is still complete, only stamping needs ids.
        /// </summary>
        internal static void Build(IReadOnlyList<Biome> biomes, Func<VoxelSo, ushort> voxelId, int treeSpacingRadius,
            Allocator allocator, ref WorldGenConfig cfg, Object owner){
            List<Object> species = CollectSpecies(biomes);
            List<FloraRuleDef> rules = new();
            List<FloraStructureDef> structures = new();
            List<FloraVoxel> voxels = new();
            List<float2> multiBlockIntervals = new();
            Dictionary<VoxelStructure, int> structureIndex = new();
            Dictionary<ushort, byte> classes = new();
            int reach = 0;
            bool anyMultiBlock = false;

            for (int b = 0; b < biomes.Count; b++){
                Biome biome = biomes[b];
                Biome.BiomeDef def = cfg.Biomes[b];
                def.floraRuleStart = rules.Count;
                float cumulative = 0f;
                if (biome.FloraRules != null){
                    for (int r = 0; r < biome.FloraRules.Count; r++){
                        FloraRule rule = biome.FloraRules[r];
                        if (rule == null || !rule.IsValid){
                            Debug.LogWarning($"{biome.name}: flora rule {r} needs exactly one of SpeciesVoxel and SpeciesStructure; it is skipped.", owner);
                            continue;
                        }

                        // VEG-02: cumulative per-column probabilities; both bounds are stored, so every reader compares
                        // against the same floats.
                        float start = cumulative;
                        cumulative += math.max(rule.Probability, 0f);
                        FloraRuleDef d = new(){
                            Species = (ushort)(species.IndexOf(Species(rule)) + 1),
                            CumulativeStart = start,
                            CumulativeEnd = cumulative,
                            RequiresSurfaceFromSameBiome = rule.RequiresSurfaceFromSameBiome ? (byte)1 : (byte)0,
                            MultiBlock = rule.IsMultiBlock ? (byte)1 : (byte)0,
                            MinHeightAboveWater = rule.MinHeightAboveWater,
                            Structure = -1,
                            MinStackHeight = math.max(1, math.min(rule.MinStackHeight, rule.MaxStackHeight)),
                            MaxStackHeight = math.max(1, math.max(rule.MinStackHeight, rule.MaxStackHeight))
                        };

                        if (rule.SpeciesStructure){
                            if (!structureIndex.TryGetValue(rule.SpeciesStructure, out d.Structure)){
                                d.Structure = structures.Count;
                                structureIndex.Add(rule.SpeciesStructure, d.Structure);
                                structures.Add(AddStructure(rule.SpeciesStructure, voxelId, voxels, classes));
                            }

                            reach = math.max(reach, structures[d.Structure].Reach);
                        }
                        else{
                            d.Voxel = voxelId?.Invoke(rule.SpeciesVoxel) ?? 0;
                            d.VoxelClass = FloraVoxelClass.Of(rule.SpeciesVoxel.meshLayer, rule.SpeciesVoxel.collision);
                            classes[d.Voxel] = d.VoxelClass;
                        }

                        if (d.MultiBlock != 0){
                            anyMultiBlock = true;
                            if (d.CumulativeEnd > d.CumulativeStart) multiBlockIntervals.Add(new float2(d.CumulativeStart, d.CumulativeEnd));
                        }

                        rules.Add(d);
                    }
                }

                if (cumulative > 1f)
                    Debug.LogWarning($"{biome.name}: flora probabilities sum to {cumulative}; rules past 1 are partly or never rolled.", owner);
                def.floraRuleCount = rules.Count - def.floraRuleStart;
                cfg.Biomes[b] = def;
            }

            cfg.TreeSpacingRadius = treeSpacingRadius;
            cfg.FloraReach = reach;
            cfg.FloraPadding = anyMultiBlock ? reach + treeSpacingRadius : 0;
            cfg.FloraRules = ToNative(rules, allocator);
            cfg.FloraStructures = ToNative(structures, allocator);
            cfg.FloraVoxels = ToNative(voxels, allocator);
            cfg.FloraMultiBlockIntervals = ToNative(multiBlockIntervals, allocator);

            // Ids absent from the table (terrain, water) are solid; air (0) is never a flora voxel.
            classes.Remove(0);
            int maxId = 0;
            foreach (ushort id in classes.Keys) maxId = math.max(maxId, id);
            cfg.VoxelClasses = new NativeArray<byte>(maxId + 1, allocator);
            for (int i = 1; i <= maxId; i++) cfg.VoxelClasses[i] = FloraVoxelClass.Solid;
            foreach (KeyValuePair<ushort, byte> entry in classes) cfg.VoxelClasses[entry.Key] = entry.Value;
        }

        private static NativeArray<T> ToNative<T>(List<T> list, Allocator allocator) where T : unmanaged{
            NativeArray<T> array = new(list.Count, allocator);
            for (int i = 0; i < list.Count; i++) array[i] = list[i];
            return array;
        }

        private static FloraStructureDef AddStructure(VoxelStructure structure, Func<VoxelSo, ushort> voxelId,
            List<FloraVoxel> voxels, Dictionary<ushort, byte> classes){
            FloraStructureDef def = new(){ Start = voxels.Count };
            if (structure.parts != null){
                foreach (StructurePart part in structure.parts){
                    if (part == null || !part.voxel || part.placements == null) continue;
                    ushort id = voxelId?.Invoke(part.voxel) ?? 0;
                    byte cls = FloraVoxelClass.Of(part.voxel.meshLayer, part.voxel.collision);
                    classes[id] = cls;
                    foreach (VoxelPlacement placement in part.placements)
                    foreach (int3 offset in Expand(placement)){
                        voxels.Add(new FloraVoxel{ Offset = offset, Voxel = id, Class = cls });
                        def.Reach = math.max(def.Reach, math.max(math.abs(offset.x), math.abs(offset.z)));
                    }
                }
            }

            def.Count = voxels.Count - def.Start;
            return def;
        }

        /// <summary>
        ///     The voxel offsets of one placement, relative to the first voxel above the anchor's top block (see
        ///     <see cref="VoxelStructure" />): Single = origin; Line = origin to end inclusive; Box = size centred on
        ///     origin (from origin − size/2), filled or a shell; Circle = horizontal disc of radius r at origin.y (a
        ///     one-block ring when not filled); Cylinder = that circle on <c>height</c> layers upwards.
        /// </summary>
        internal static IEnumerable<int3> Expand(VoxelPlacement p){
            int3 origin = new(p.origin.x, p.origin.y, p.origin.z);
            switch (p.shape){
                case PlacementShape.Single:
                    yield return origin;
                    break;
                case PlacementShape.Line:{
                    int3 delta = new int3(p.end.x, p.end.y, p.end.z) - origin;
                    int steps = math.cmax(math.abs(delta));
                    for (int i = 0; i <= steps; i++)
                        yield return steps == 0 ? origin : origin + RoundDiv(delta * i, steps);
                    break;
                }
                case PlacementShape.Box:{
                    int3 size = new(p.size.x, p.size.y, p.size.z);
                    if (math.any(size <= 0)) yield break;
                    int3 min = origin - size / 2;
                    for (int x = 0; x < size.x; x++)
                    for (int y = 0; y < size.y; y++)
                    for (int z = 0; z < size.z; z++){
                        bool shell = x == 0 || y == 0 || z == 0 || x == size.x - 1 || y == size.y - 1 || z == size.z - 1;
                        if (p.filled || shell) yield return min + new int3(x, y, z);
                    }

                    break;
                }
                case PlacementShape.Circle:
                case PlacementShape.Cylinder:{
                    int layers = p.shape == PlacementShape.Circle ? 1 : p.height;
                    int r = p.radius;
                    if (r < 0) yield break;
                    for (int y = 0; y < layers; y++)
                    for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++){
                        int d2 = dx * dx + dz * dz;
                        if (d2 > r * r) continue;
                        if (!p.filled && r > 0 && d2 <= (r - 1) * (r - 1)) continue;
                        yield return origin + new int3(dx, y, dz);
                    }

                    break;
                }
            }
        }

        /// <summary>Component-wise round(a / b) for b &gt; 0, halves away from zero, in integers.</summary>
        private static int3 RoundDiv(int3 a, int b){
            return new int3(RoundDiv(a.x, b), RoundDiv(a.y, b), RoundDiv(a.z, b));
        }

        private static int RoundDiv(int a, int b){
            return a >= 0 ? (2 * a + b) / (2 * b) : -((-2 * a + b) / (2 * b));
        }
    }
}
