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
    ///     Builds the flora part of <see cref="WorldGenConfig" /> from the active biomes: the flattened
    ///     rules with their cumulative intervals, the species table, the expanded structures, the tree variants and
    ///     the voxel replace classes. Managed, once per settings change.
    /// </summary>
    internal static class FloraConfigBuilder{
        /// <summary>
        ///     Species in id order (id = index + 1): every distinct voxel, structure or tree species named by a valid
        ///     rule of an active biome, in biome and rule order.
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
            if (rule.SpeciesTree) return rule.SpeciesTree;
            return rule.SpeciesStructure ? rule.SpeciesStructure : rule.SpeciesVoxel;
        }

        /// <summary>
        ///     Fills the flora fields of <paramref name="cfg" /> and the rule ranges of <c>cfg.Biomes</c> (which must
        ///     already hold the biome structs). <paramref name="voxelId" /> null leaves every voxel id at 0 (preview):
        ///     the decision (species, source) is still complete, only stamping needs ids. <paramref name="densityNoise" />
        ///     false sets every rule's density influence to 0, so every rule is spread evenly.
        /// </summary>
        internal static void Build(IReadOnlyList<Biome> biomes, Func<VoxelSo, ushort> voxelId, int treeSpacingRadius,
            bool densityNoise, Allocator allocator, ref WorldGenConfig cfg, Object owner){
            List<Object> species = CollectSpecies(biomes);
            List<FloraRuleDef> rules = new();
            List<FloraStructureDef> structures = new();
            List<FloraVoxel> voxels = new();
            List<float2> multiBlockIntervals = new();
            List<TreeSpeciesDef> trees = new();
            List<TreeVariantDef> variants = new();
            Dictionary<VoxelStructure, int> structureIndex = new();
            Dictionary<TreeSpecies, int> treeIndex = new();
            Dictionary<ushort, byte> classes = new();
            int reach = 0;
            int spacing = 0;
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
                            Debug.LogWarning($"{biome.name}: flora rule {r} needs exactly one of SpeciesVoxel, SpeciesStructure and SpeciesTree (a tree with at least one variant); it is skipped.", owner);
                            continue;
                        }

                        // Cumulative per-column probabilities; both bounds are stored, so every reader compares
                        // against the same floats.
                        float start = cumulative;
                        cumulative += math.max(rule.Probability, 0f);
                        FloraRuleDef d = new(){
                            Species = (ushort)(species.IndexOf(Species(rule)) + 1),
                            CumulativeStart = start,
                            CumulativeEnd = cumulative,
                            Probability = math.max(rule.Probability, 0f),
                            DensityNoise = (byte)math.clamp((int)rule.DensityNoise, 0, 3),
                            Influence = densityNoise ? math.saturate(rule.DensityInfluence) : 0f,
                            RequiresSurfaceFromSameBiome = rule.RequiresSurfaceFromSameBiome ? (byte)1 : (byte)0,
                            MultiBlock = rule.IsMultiBlock ? (byte)1 : (byte)0,
                            MinHeightAboveWater = rule.MinHeightAboveWater,
                            Structure = -1,
                            Tree = -1,
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
                            spacing = math.max(spacing, treeSpacingRadius);
                        }
                        else if (rule.SpeciesTree){
                            if (!treeIndex.TryGetValue(rule.SpeciesTree, out d.Tree)){
                                d.Tree = trees.Count;
                                treeIndex.Add(rule.SpeciesTree, d.Tree);
                                trees.Add(AddTree(rule.SpeciesTree, voxelId, variants, classes, owner));
                            }

                            TreeSpeciesDef tree = trees[d.Tree];
                            for (int v = tree.VariantStart; v < tree.VariantStart + tree.VariantCount; v++){
                                reach = math.max(reach, variants[v].Reach);
                                spacing = math.max(spacing, variants[v].SpacingRadius);
                            }
                        }
                        else{
                            d.Voxel = voxelId?.Invoke(rule.SpeciesVoxel) ?? 0;
                            d.VoxelClass = FloraVoxelClass.Of(rule.SpeciesVoxel.meshLayer, rule.SpeciesVoxel.collision);
                            classes[d.Voxel] = d.VoxelClass;
                            if (rule.IsMultiBlock) spacing = math.max(spacing, treeSpacingRadius);
                        }

                        if (d.MultiBlock != 0) anyMultiBlock = true;
                        if (d.Influence > 0f) def.floraDensity = 1;
                        rules.Add(d);
                    }
                }

                if (cumulative > 1f)
                    Debug.LogWarning($"{biome.name}: flora probabilities sum to {cumulative}; rules past 1 are partly or never rolled.", owner);
                def.floraRuleCount = rules.Count - def.floraRuleStart;
                AddMultiBlockIntervals(rules, def, multiBlockIntervals);
                cfg.Biomes[b] = def;
            }

            cfg.TreeSpacingRadius = treeSpacingRadius;
            cfg.FloraReach = reach;
            cfg.FloraSpacingMax = anyMultiBlock ? spacing : 0;
            cfg.FloraPadding = anyMultiBlock ? reach + spacing : 0;
            cfg.FloraRules = ToNative(rules, allocator);
            cfg.FloraStructures = ToNative(structures, allocator);
            cfg.FloraVoxels = ToNative(voxels, allocator);
            cfg.TreeSpecies = ToNative(trees, allocator);
            cfg.TreeVariants = ToNative(variants, allocator);
            cfg.FloraMultiBlockIntervals = ToNative(multiBlockIntervals, allocator);

            // Ids absent from the table (terrain, water) are solid; air (0) is never a flora voxel.
            classes.Remove(0);
            int maxId = 0;
            foreach (ushort id in classes.Keys) maxId = math.max(maxId, id);
            cfg.VoxelClasses = new NativeArray<byte>(maxId + 1, allocator);
            for (int i = 1; i <= maxId; i++) cfg.VoxelClasses[i] = FloraVoxelClass.Solid;
            foreach (KeyValuePair<ushort, byte> entry in classes) cfg.VoxelClasses[entry.Key] = entry.Value;
        }

        /// <summary>
        ///     The pre-filter intervals of the multi-block rules of biome <paramref name="def" />. Without density noise
        ///     they are the rules' intervals. With it they must hold every interval the column probabilities can give:
        ///     the start is at least Σ p(1 − k) of the rules before, divided by the largest possible sum
        ///     max(1, Σ p(1 + k)); the end is at most min(1, Σ p(1 + k)) up to the rule. A small margin covers rounding.
        /// </summary>
        private static void AddMultiBlockIntervals(List<FloraRuleDef> rules, in Biome.BiomeDef def, List<float2> intervals){
            const float margin = 1e-5f;
            int start = def.floraRuleStart, end = def.floraRuleStart + def.floraRuleCount;
            if (def.floraDensity == 0){
                for (int r = start; r < end; r++)
                    if (rules[r].MultiBlock != 0 && rules[r].CumulativeEnd > rules[r].CumulativeStart)
                        intervals.Add(new float2(rules[r].CumulativeStart, rules[r].CumulativeEnd));
                return;
            }

            float highest = 0f;
            for (int r = start; r < end; r++) highest += rules[r].Probability * (1f + rules[r].Influence);
            float low = 0f, high = 0f;
            for (int r = start; r < end; r++){
                FloraRuleDef d = rules[r];
                float lower = low / math.max(1f, highest);
                low += d.Probability * (1f - d.Influence);
                high += d.Probability * (1f + d.Influence);
                if (d.MultiBlock != 0 && d.Probability > 0f)
                    intervals.Add(new float2(math.max(lower - margin, 0f), math.min(high, 1f) + margin));
            }
        }

        private static NativeArray<T> ToNative<T>(List<T> list, Allocator allocator) where T : unmanaged{
            NativeArray<T> array = new(list.Count, allocator);
            for (int i = 0; i < list.Count; i++) array[i] = list[i];
            return array;
        }

        /// <summary>
        ///     Appends the sanitised variants of <paramref name="species" /> with cumulative weights (all equal when
        ///     every weight is 0) and their reach. Missing voxels are id 0 of class air, so they are never stamped;
        ///     <paramref name="warn" /> false skips their warnings (the species gallery redraws on every edit).
        /// </summary>
        internal static TreeSpeciesDef AddTree(TreeSpecies species, Func<VoxelSo, ushort> voxelId, List<TreeVariantDef> variants,
            Dictionary<ushort, byte> classes, Object owner, bool warn = true){
            TreeSpeciesDef def = new(){ VariantStart = variants.Count, VariantCount = species.Variants.Count };
            float total = 0f;
            int lastWeighted = species.Variants.Count - 1; // from here on the bound is 1, so rounding never picks a weight-0 tail
            for (int i = 0; i < species.Variants.Count; i++){
                float w = species.Variants[i] == null ? 0f : math.max(species.Variants[i].Weight, 0f);
                total += w;
                if (w > 0f) lastWeighted = i;
            }

            float cumulative = 0f;
            for (int i = 0; i < species.Variants.Count; i++){
                TreeVariant v = species.Variants[i] ?? new TreeVariant{ Weight = 0f };
                cumulative += total > 0f ? math.max(v.Weight, 0f) / total : 1f / species.Variants.Count;
                TreeVariantDef d = new(){
                    CumulativeEnd = i >= lastWeighted ? 1f : cumulative,
                    TrunkHeight = Range(v.TrunkHeight),
                    TrunkWidth = math.clamp(v.TrunkWidth, 1, 2),
                    LeanPerMille = (int)math.round(math.clamp(v.MaxLean, 0f, .25f) * 1000f),
                    Crown = v.CrownShape,
                    CrownRadius = Range(v.CrownRadius),
                    CrownTopRadius = Range(v.CrownTopRadius),
                    CrownHeight = Range(v.CrownHeight),
                    CrownBase = v.CrownBase,
                    CrownMinBase = v.CrownMinBase,
                    BranchChance = math.saturate(v.BranchChance),
                    BranchCount = v.BranchChance > 0f ? math.min(Range(v.BranchCount), TreeShape.MaxBranches) : int2.zero,
                    Branches = v.BranchStyle,
                    BranchLength = math.max(Range(v.BranchLength), 1),
                    BranchBand = new int2(PerMille(math.min(v.BranchBandMin, v.BranchBandMax)),
                        PerMille(math.max(v.BranchBandMin, v.BranchBandMax))),
                    ClusterRadius = Range(v.BranchLeafRadius),
                    HoleChance = math.saturate(v.HoleChance),
                    BumpChance = math.saturate(v.BumpChance),
                    HangChance = math.saturate(v.HangChance),
                    AltLeafChance = math.saturate(v.AltLeafChance),
                    SpacingRadius = math.max(v.SpacingRadius, 0)
                };
                d.TrunkVoxel = Voxel(v.TrunkVoxel, voxelId, classes, out d.TrunkClass);
                d.LeafVoxel = Voxel(v.LeafVoxel, voxelId, classes, out d.LeafClass);
                d.AltLeafVoxel = Voxel(v.AltLeafVoxel, voxelId, classes, out d.AltLeafClass);
                if (warn && !v.TrunkVoxel && (d.TrunkHeight.y > 0 || d.BranchCount.y > 0))
                    Debug.LogWarning($"{species.name}: variant {i} has a trunk but no trunk voxel; the trunk is left out.", owner);
                if (warn && !v.LeafVoxel && (d.Crown != CrownShape.None || d.ClusterRadius.y > 0))
                    Debug.LogWarning($"{species.name}: variant {i} has a crown but no leaf voxel; the leaves are left out.", owner);
                d.Reach = Reach(d);
                variants.Add(d);
            }

            return def;
        }

        /// <summary>
        ///     Largest horizontal reach of a variant from the range maxima: lean and trunk width, plus the crown radius,
        ///     or the branch length (Arm: 1) and tip cluster; crown bumps add one block.
        /// </summary>
        internal static int Reach(in TreeVariantDef d){
            int lean = d.TrunkHeight.y * d.LeanPerMille / 1000;
            int trunk = lean + d.TrunkWidth - 1;
            int bump = d.BumpChance > 0f ? 1 : 0;
            int reach = trunk;
            if (d.Crown != CrownShape.None){
                int radius = d.Crown is CrownShape.Cone or CrownShape.Tiered ? math.max(d.CrownRadius.y, d.CrownTopRadius.y) : d.CrownRadius.y;
                reach = math.max(reach, trunk + radius + bump);
            }

            if (d.BranchCount.y > 0 && d.TrunkHeight.y > 0){
                int length = d.Branches == BranchStyle.Arm ? 1 : d.BranchLength.y;
                int cluster = d.ClusterRadius.y > 0 ? d.ClusterRadius.y + bump : 0;
                reach = math.max(reach, trunk + length + cluster);
            }

            return reach;
        }

        private static int2 Range(IntRange range){
            int min = math.max(0, math.min(range.Min, range.Max));
            return new int2(min, math.max(min, math.max(range.Min, range.Max)));
        }

        private static int PerMille(float fraction){
            return (int)math.round(math.saturate(fraction) * 1000f);
        }

        private static ushort Voxel(VoxelSo voxel, Func<VoxelSo, ushort> voxelId, Dictionary<ushort, byte> classes, out byte cls){
            if (!voxel){
                cls = FloraVoxelClass.Air;
                return 0;
            }

            ushort id = voxelId?.Invoke(voxel) ?? 0;
            cls = FloraVoxelClass.Of(voxel.meshLayer, voxel.collision);
            classes[id] = cls;
            return id;
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
