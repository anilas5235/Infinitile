using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;
using BlendWeights = Engine.Scripts.WorldGen.Blending.BlendWeights;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Flora test inputs: in-memory voxels, two tree structures and two flora biomes, resolved through the
    ///     test's own id table (no registry). Ids 0…5 match <see cref="TestFixtures" />; the flora voxels follow.
    ///     The fixture owns every ScriptableObject it creates.
    /// </summary>
    internal sealed class FloraFixture : IDisposable{
        internal const ushort Sand = 6;
        internal const ushort Tuft = 7;
        internal const ushort Flower = 8;
        internal const ushort Log = 9;
        internal const ushort Leaves = 10;
        internal const ushort Needles = 11;
        internal const ushort Cactus = 12;
        internal const ushort Glass = 13;
        internal const ushort Snow = 14;
        internal const ushort Granite = 15;

        /// <summary>The log lying along x and along z (<see cref="VariantOf" />).</summary>
        internal const ushort LogX = 16;

        internal const ushort LogZ = 17;

        private readonly List<Object> _created = new();

        /// <summary>Test-side id of every voxel ScriptableObject.</summary>
        internal readonly Dictionary<Voxel, ushort> Ids = new();

        /// <summary>Expected flora replace class per id; ids not listed are solid.</summary>
        internal readonly Dictionary<ushort, byte> Classes = new(){
            [TestFixtures.Air] = FloraVoxelClass.Air,
            [Tuft] = FloraVoxelClass.Foliage,
            [Flower] = FloraVoxelClass.Foliage,
            [Leaves] = FloraVoxelClass.Leaves,
            [Needles] = FloraVoxelClass.Leaves
        };

        internal readonly Voxel GrassSo, DirtSo, StoneSo, SandSo, TuftSo, FlowerSo, LogSo, LeavesSo, NeedlesSo, CactusSo, GlassSo,
            SnowSo, GraniteSo;

        /// <summary>Log line 0…4, a filled 5×3×5 leaf box centred at y 5 (y 4…6), one leaf on top (reach 2).</summary>
        internal readonly VoxelStructure Tree;

        /// <summary>Log line 0…5, a needle cylinder r 2 on y 3…4, a needle ring r 2 at y 5, a disc r 1 at y 6 (reach 2).</summary>
        internal readonly VoxelStructure Pine;

        /// <summary>A tree species with every variant of <see cref="ShapeVariants" /> at weight 1.</summary>
        internal readonly TreeSpecies Grove;

        internal FloraFixture(){
            GrassSo = MakeVoxel("Grass", TestFixtures.Grass, MeshLayer.Solid, true);
            DirtSo = MakeVoxel("Dirt", TestFixtures.Dirt, MeshLayer.Solid, true);
            StoneSo = MakeVoxel("Stone", TestFixtures.Stone, MeshLayer.Solid, true);
            SandSo = MakeVoxel("Sand", Sand, MeshLayer.Solid, true);
            TuftSo = MakeVoxel("Tuft", Tuft, MeshLayer.Foliage, false);
            FlowerSo = MakeVoxel("Flower", Flower, MeshLayer.Foliage, false);
            LogSo = MakeVoxel("Log", Log, MeshLayer.Solid, true);
            LogSo.facing = VoxelFacing.Axis;
            LeavesSo = MakeVoxel("Leaves", Leaves, MeshLayer.Transparent, true);
            NeedlesSo = MakeVoxel("Needles", Needles, MeshLayer.Transparent, true);
            CactusSo = MakeVoxel("Cactus", Cactus, MeshLayer.Solid, true);
            GlassSo = MakeVoxel("Glass", Glass, MeshLayer.Transparent, false);
            SnowSo = MakeVoxel("Snow", Snow, MeshLayer.Solid, true);
            GraniteSo = MakeVoxel("Granite", Granite, MeshLayer.Solid, true);

            Tree = Create<VoxelStructure>("Tree");
            Tree.parts = new[]{
                Part(LogSo, Line(0, 0, 0, 0, 4, 0)),
                Part(LeavesSo, new VoxelPlacement{
                    shape = PlacementShape.Box, origin = new Vector3Int(0, 5, 0), size = new Vector3Int(5, 3, 5), filled = true
                }, new VoxelPlacement{ shape = PlacementShape.Single, origin = new Vector3Int(0, 7, 0) })
            };

            Pine = Create<VoxelStructure>("Pine");
            Pine.parts = new[]{
                Part(LogSo, Line(0, 0, 0, 0, 5, 0)),
                Part(NeedlesSo, new VoxelPlacement{
                        shape = PlacementShape.Cylinder, origin = new Vector3Int(0, 3, 0), radius = 2, height = 2, filled = true
                    }, new VoxelPlacement{ shape = PlacementShape.Circle, origin = new Vector3Int(0, 5, 0), radius = 2 },
                    new VoxelPlacement{ shape = PlacementShape.Circle, origin = new Vector3Int(0, 6, 0), radius = 1, filled = true })
            };

            Grove = MakeTree("Grove", ShapeVariants());
        }

        internal T Create<T>(string name) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            _created.Add(so);
            return so;
        }

        private Voxel MakeVoxel(string name, ushort id, MeshLayer layer, bool collision){
            Voxel v = Create<Voxel>(name);
            v.meshLayer = layer;
            v.collision = collision;
            Ids.Add(v, id);
            return v;
        }

        private static StructurePart Part(Voxel voxel, params VoxelPlacement[] placements){
            return new StructurePart{ voxel = voxel, placements = placements };
        }

        internal static VoxelPlacement Line(int x0, int y0, int z0, int x1, int y1, int z1){
            return new VoxelPlacement{
                shape = PlacementShape.Line, origin = new Vector3Int(x0, y0, z0), end = new Vector3Int(x1, y1, z1)
            };
        }

        internal static FloraRule Rule(Voxel voxel, float p, int minStack = 1, int maxStack = 1){
            return new FloraRule{ SpeciesVoxel = voxel, Probability = p, MinStackHeight = minStack, MaxStackHeight = maxStack };
        }

        internal static FloraRule Rule(VoxelStructure structure, float p){
            return new FloraRule{ SpeciesStructure = structure, Probability = p };
        }

        internal static FloraRule Rule(TreeSpecies tree, float p){
            return new FloraRule{ SpeciesTree = tree, Probability = p };
        }

        internal TreeSpecies MakeTree(string name, params TreeVariant[] variants){
            TreeSpecies t = Create<TreeSpecies>(name);
            t.Variants = new List<TreeVariant>(variants);
            return t;
        }

        /// <summary>Today's tree as a variant: log trunk 4–6, round leaf crown r 2–3, 0–1 diagonal branches, spacing 3.</summary>
        internal TreeVariant Normal(){
            return new TreeVariant{
                Name = "Normal", TrunkVoxel = LogSo, LeafVoxel = LeavesSo, BranchCount = new IntRange(0, 1)
            };
        }

        /// <summary>
        ///     One variant per crown shape and branch style, with wide, leaning and alt-leaf trunks and crowns and
        ///     spacing radii 2…5: Normal (round), Wide (ellipsoid, 2×2 leaning trunk), Spire (cone), Tiered (arm
        ///     branches with clusters) and Snag (no crown, diagonal branches with clusters).
        /// </summary>
        internal TreeVariant[] ShapeVariants(){
            return new[]{
                Normal(),
                new TreeVariant{
                    Name = "Wide", TrunkVoxel = LogSo, LeafVoxel = LeavesSo, AltLeafVoxel = NeedlesSo, AltLeafChance = .3f,
                    TrunkHeight = new IntRange(6, 8), TrunkWidth = 2, MaxLean = .2f, CrownShape = CrownShape.Ellipsoid,
                    CrownRadius = new IntRange(3, 4), CrownHeight = new IntRange(4, 5), SpacingRadius = 5
                },
                new TreeVariant{
                    Name = "Spire", TrunkVoxel = LogSo, LeafVoxel = NeedlesSo, TrunkHeight = new IntRange(8, 10),
                    CrownShape = CrownShape.Cone, CrownRadius = new IntRange(3, 3), CrownTopRadius = new IntRange(0, 0),
                    CrownMinBase = 3, SpacingRadius = 2
                },
                new TreeVariant{
                    Name = "Tiered", TrunkVoxel = LogSo, LeafVoxel = NeedlesSo, TrunkHeight = new IntRange(7, 9),
                    CrownShape = CrownShape.Tiered, CrownRadius = new IntRange(3, 4), CrownTopRadius = new IntRange(1, 1),
                    CrownMinBase = 2, BranchCount = new IntRange(2, 3), BranchStyle = BranchStyle.Arm,
                    BranchLength = new IntRange(1, 2), BranchBandMin = .2f, BranchBandMax = .6f, BranchLeafRadius = new IntRange(1, 1),
                    SpacingRadius = 4
                },
                new TreeVariant{
                    Name = "Snag", TrunkVoxel = LogSo, LeafVoxel = LeavesSo, TrunkHeight = new IntRange(3, 5), MaxLean = .25f,
                    CrownShape = CrownShape.None, BranchCount = new IntRange(2, 4), BranchLength = new IntRange(2, 3),
                    BranchLeafRadius = new IntRange(1, 2), SpacingRadius = 3
                }
            };
        }

        /// <summary>Folder of the shipped tree species.</summary>
        internal const string ShippedTreesPath = "Assets/Engine/Resources/VoxelDataPackages/Standard/Trees";

        /// <summary>Every variant of every shipped tree species, as "Species/index" (index into its variants).</summary>
        internal static IEnumerable<string> ShippedVariantNames(){
            foreach (string guid in AssetDatabase.FindAssets("t:TreeSpecies", new[]{ ShippedTreesPath }))
            foreach (TreeSpecies species in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<TreeSpecies>())
                for (int i = 0; i < species.Variants.Count; i++)
                    yield return $"{species.name}/{i}";
        }

        /// <summary>
        ///     A copy of the shipped variant "Species/index" on this fixture's voxels: cactus trunks take the cactus,
        ///     other trunks the log; spruce leaves take the needles, stone "leaves" (boulders) the granite, other
        ///     leaves the leaves.
        /// </summary>
        internal TreeVariant ShippedVariant(string name){
            string[] parts = name.Split('/');
            TreeSpecies species = AssetDatabase.LoadAssetAtPath<TreeSpecies>($"{ShippedTreesPath}/{parts[0]}.asset");
            TreeVariant source = species.Variants[int.Parse(parts[1])];
            TreeVariant copy = JsonUtility.FromJson<TreeVariant>(JsonUtility.ToJson(source));
            copy.TrunkVoxel = !source.TrunkVoxel ? null : source.TrunkVoxel.name == "Cactus" ? CactusSo : LogSo;
            copy.LeafVoxel = !source.LeafVoxel ? null
                : source.LeafVoxel.name == "LeavesSpruce" ? NeedlesSo
                : source.LeafVoxel.name.StartsWith("Stone") ? GraniteSo
                : LeavesSo;
            copy.AltLeafVoxel = !source.AltLeafVoxel ? null : NeedlesSo;
            return copy;
        }

        /// <summary>The shipped species <paramref name="name" /> with every variant on this fixture's voxels.</summary>
        internal TreeSpecies ShippedSpecies(string name){
            int count = AssetDatabase.LoadAssetAtPath<TreeSpecies>($"{ShippedTreesPath}/{name}.asset").Variants.Count;
            return MakeTree(name, Enumerable.Range(0, count).Select(i => ShippedVariant($"{name}/{i}")).ToArray());
        }

        /// <summary>
        ///     A biome usable by both architectures: Biome-first reads the height parameters, Terrain-first the climate
        ///     targets (h = .5). The flora biomes differ only in temperature, so both occur within a few chunks.
        /// </summary>
        internal Biome MakeBiome(string name, float temperature, float offset, float amplitude, Voxel top,
            params FloraRule[] rules){
            Biome b = Create<Biome>(name);
            b.TargetTemperature = temperature;
            b.TargetHumidity = .5f;
            b.TargetContinental = .5f;
            b.TargetErosion = .5f;
            b.TargetPV = 0f;
            b.TargetHeight = .5f;
            b.HeightOffset = offset;
            b.HeightAmplitude = amplitude;
            b.OctaveWeights = new[]{ 1f, .6f, .3f, .12f, .05f, .02f };
            b.Jaggedness = 0f;
            b.TopBlock = top;
            b.UnderBlock = DirtSo;
            b.StoneBlock = StoneSo;
            b.FloraRules = new List<FloraRule>(rules);
            return b;
        }

        /// <summary>
        ///     The two flora test biomes: a meadow (grass, flowers, <see cref="Grove" /> trees) and dunes (cactus stacks
        ///     1–3, sparse grass, pine structures), both above the water level 124 in Biome-first. Probabilities are
        ///     high so every chunk has flora.
        /// </summary>
        internal Biome[] DefaultBiomes(){
            return new[]{
                MakeBiome("Meadow", .25f, 150f, 10f, GrassSo, Rule(TuftSo, .3f), Rule(FlowerSo, .05f), Rule(Grove, .06f)),
                MakeBiome("Dunes", .75f, 138f, 6f, SandSo, Rule(CactusSo, .03f, 1, 3), Rule(TuftSo, .05f), Rule(Pine, .03f))
            };
        }

        /// <summary>
        ///     The default biomes with the shipped species set on this fixture's voxels: oak, birch, bush, spruce,
        ///     boulder and fallen log in the meadow, cactus and dead tree in the dunes.
        /// </summary>
        internal Biome[] ShippedSpeciesBiomes(){
            return new[]{
                MakeBiome("Meadow", .25f, 150f, 10f, GrassSo, Rule(TuftSo, .3f), Rule(FlowerSo, .05f), Rule(ShippedSpecies("Oak"), .02f),
                    Rule(ShippedSpecies("Birch"), .02f), Rule(ShippedSpecies("Bush"), .02f), Rule(ShippedSpecies("Spruce"), .02f),
                    Rule(ShippedSpecies("Boulder"), .02f), Rule(ShippedSpecies("FallenLog"), .02f)),
                MakeBiome("Dunes", .75f, 138f, 6f, SandSo, Rule(TuftSo, .05f), Rule(ShippedSpecies("Cactus"), .04f),
                    Rule(ShippedSpecies("DeadTree"), .03f))
            };
        }

        /// <summary>The default biomes with the <see cref="Tree" /> structure in the meadow instead of <see cref="Grove" />.</summary>
        internal Biome[] StructureBiomes(){
            Biome[] biomes = DefaultBiomes();
            biomes[0].FloraRules[2] = Rule(Tree, .06f);
            return biomes;
        }

        /// <summary>Settings with <paramref name="method" /> on the material stage (and, in Biome-first, the height stage).</summary>
        internal WorldGenSettings Settings(GenArchitecture architecture, BlendMethod method, params Biome[] biomes){
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Architecture = architecture;
            s.MaterialMethod = method;
            s.HeightMethod = architecture == GenArchitecture.BiomeFirst ? method : BlendMethod.None;
            if (architecture == GenArchitecture.TerrainFirst){
                s.Splines = Create<TerrainSplineSet>("Splines");
                s.Splines.ResetToPlaceholders();
            }

            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.AddRange(biomes.Length > 0 ? biomes : DefaultBiomes());
            s.Biomes = set;
            return s;
        }

        internal WorldGenConfig ToConfig(WorldGenSettings s, Allocator allocator = Allocator.Persistent){
            return s.ToConfigWithVoxelIds(TestFixtures.Seed, v => v ? Ids[v] : (ushort)0, allocator, VariantOf);
        }

        /// <summary>The test's variant table: the log's axis variants <see cref="LogX" /> and <see cref="LogZ" />.</summary>
        internal static ushort VariantOf(ushort id, byte orientation){
            if (id != Log) return id;
            return orientation switch{
                VoxelOrientation.AxisX => LogX,
                VoxelOrientation.AxisZ => LogZ,
                _ => id
            };
        }

        /// <summary>The base voxel of a test id: <see cref="Log" /> for its variants.</summary>
        internal static ushort BaseOf(ushort id){
            return id is LogX or LogZ ? Log : id;
        }

        /// <summary>A chunk-job config: the settings' world-gen config plus the water/ice ids FillTerrain needs.</summary>
        internal GeneratorConfig GeneratorConfig(WorldGenSettings s){
            NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> voxels = new(4, Allocator.Persistent);
            voxels.Add("std:air", new Voxel.VoxelDef{ Id = TestFixtures.Air, MeshLayer = MeshLayer.Air });
            voxels.Add("std:Water", new Voxel.VoxelDef{ Id = TestFixtures.Water, MeshLayer = MeshLayer.Transparent });
            voxels.Add("std:Ice", new Voxel.VoxelDef{ Id = TestFixtures.Ice, MeshLayer = MeshLayer.Solid, Collision = true });
            return new GeneratorConfig{
                Voxels = voxels,
                WorldGen = ToConfig(s)
            };
        }

        internal byte ClassOf(ushort id){
            return Classes.TryGetValue(id, out byte c) ? c : FloraVoxelClass.Solid;
        }

        public void Dispose(){
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }
    }

    /// <summary>Managed copy of a <see cref="WorldGenApi.SampleRegion" /> result at stride 1.</summary>
    internal sealed class RegionSample{
        internal int2 Origin;
        internal int2 Size;
        internal WorldFields[] Fields;
        internal float[] Height;
        internal int[] ColumnHeight;
        internal int[] HardBiome;
        internal BlendWeights[] MaterialWeights;
        internal int[] SurfaceSource;
        internal ushort[] FloraSpecies;
        internal int[] FloraSource;
        internal byte[] Clamped;

        internal int Index(int x, int z){
            return (z - Origin.y) * Size.x + x - Origin.x;
        }

        internal bool Contains(int x, int z){
            return x >= Origin.x && z >= Origin.y && x < Origin.x + Size.x && z < Origin.y + Size.y;
        }

        internal static RegionSample Sample(in WorldGenConfig cfg, int2 origin, int2 size){
            int n = size.x * size.y;
            RegionBuffers b = new(){
                Fields = new NativeArray<WorldFields>(n, Allocator.TempJob),
                Height = new NativeArray<float>(n, Allocator.TempJob),
                ColumnHeight = new NativeArray<int>(n, Allocator.TempJob),
                HardBiome = new NativeArray<int>(n, Allocator.TempJob),
                MaterialWeights = new NativeArray<BlendWeights>(n, Allocator.TempJob),
                SurfaceSource = new NativeArray<int>(n, Allocator.TempJob),
                FloraSpecies = new NativeArray<ushort>(n, Allocator.TempJob),
                FloraSource = new NativeArray<int>(n, Allocator.TempJob),
                Clamped = new NativeArray<byte>(n, Allocator.TempJob)
            };
            try{
                WorldGenRequest request = new(){ Config = cfg, Origin = origin, Size = size, Stride = 1 };
                WorldGenApi.SampleRegion(request, ref b).Complete();
                return new RegionSample{
                    Origin = origin,
                    Size = size,
                    Fields = b.Fields.ToArray(),
                    Height = b.Height.ToArray(),
                    ColumnHeight = b.ColumnHeight.ToArray(),
                    HardBiome = b.HardBiome.ToArray(),
                    MaterialWeights = b.MaterialWeights.ToArray(),
                    SurfaceSource = b.SurfaceSource.ToArray(),
                    FloraSpecies = b.FloraSpecies.ToArray(),
                    FloraSource = b.FloraSource.ToArray(),
                    Clamped = b.Clamped.ToArray()
                };
            }
            finally{
                b.Fields.Dispose();
                b.Height.Dispose();
                b.ColumnHeight.Dispose();
                b.HardBiome.Dispose();
                b.MaterialWeights.Dispose();
                b.SurfaceSource.Dispose();
                b.FloraSpecies.Dispose();
                b.FloraSource.Dispose();
                b.Clamped.Dispose();
            }
        }
    }

    /// <summary>
    ///     Test-side whole-world stamp of a chunk-aligned area, built only from the pure region API
    ///     (<see cref="WorldGenApi.SampleRegion" />) and the fixture's own block and class tables: the FillTerrain
    ///     layering, then every single-block plant on its column, then every anchor whose structure may reach the
    ///     area, in world (x, then z) order; a voxel replaces only one of a lower class. The chunk job must produce
    ///     exactly this, chunk by chunk.
    /// </summary>
    internal sealed class ReferenceWorld{
        internal readonly int2 Origin;
        internal readonly int2 Size;
        internal readonly RegionSample Region;

        /// <summary>World positions a plant or structure voxel was written to (whether it survived later writes or not).</summary>
        internal readonly HashSet<int3> Stamped = new();

        /// <summary>Anchors (world column) of multi-block species whose footprint touches the area.</summary>
        internal readonly List<(int2 column, List<int3> footprint)> Anchors = new();

        /// <summary>Tree variant (index into the config's variants) of every tree anchor in <see cref="Anchors" />.</summary>
        internal readonly Dictionary<int2, int> TreeVariants = new();

        /// <summary>Tree anchors in <see cref="Anchors" /> whose wide or leaning trunk fell back to a narrow, upright one.</summary>
        internal readonly HashSet<int2> Narrowed = new();

        private readonly ushort[] _voxels;

        internal ReferenceWorld(FloraFixture fixture, IReadOnlyList<Biome> biomes, in GeneratorConfig config,
            int2 originChunk, int2 chunks){
            WorldGenConfig cfg = config.WorldGen;
            Origin = originChunk * new int2(ChunkWidth, ChunkDepth);
            Size = chunks * new int2(ChunkWidth, ChunkDepth);
            int reach = cfg.FloraReach;
            // Anchors up to reach outside the area may stamp into it; their trunk footprint lies within reach of them.
            Region = RegionSample.Sample(cfg, Origin - 2 * reach, Size + 4 * reach);
            _voxels = new ushort[Size.x * Size.y * ChunkHeight];

            List<Object> species = FloraConfigBuilder.CollectSpecies(biomes);
            int waterLevel = config.WorldGen.WaterLevel;
            // Remembers the beach water probes of the area (the same result as the pure probes, only faster).
            BlendCache probes = BlendCache.Build(cfg, Origin, Origin + Size - 1, 1, Allocator.Temp);

            for (int x = 0; x < Size.x; x++)
            for (int z = 0; z < Size.y; z++){
                int i = Region.Index(Origin.x + x, Origin.y + z);
                int h = math.clamp(Region.ColumnHeight[i], 0, MaxTerrainHeight);
                int source = Region.SurfaceSource[i];
                int wx = Origin.x + x, wz = Origin.y + z;
                float t = Region.Fields[i].T;
                Biome b = source >= 0 ? biomes[source] : null;
                Biome.BiomeDef def = source >= 0 ? cfg.Biomes[source] : default;
                // Under water (the voxel above is water) the underwater block replaces the top block; cold dry
                // columns take the snow block.
                Voxel wet = b && b.UnderwaterTopBlock ? b.UnderwaterTopBlock : b ? b.UnderBlock : null;
                ushort top = b ? fixture.Ids[h + 1 < waterLevel ? wet : b.TopBlock] : (ushort)0;
                bool beach = b && b.BeachBlock && SurfaceCover.IsBeach(wx, wz, h, def, cfg, probes);
                if (beach) top = fixture.Ids[b.BeachBlock];
                if (b && SurfaceCover.IsSnowCovered(wx, wz, t, h, def, cfg)) top = def.snowTopBlock;
                ushort under = b ? fixture.Ids[b.UnderBlock] : (ushort)0;
                ushort beachUnder = beach ? fixture.Ids[b.BeachBlock] : under;
                ushort stone = b ? fixture.Ids[b.StoneBlock] : (ushort)0;
                int shift = SurfaceCover.StrataShift(wx, wz, cfg);
                // Ice follows the hard biome, as in the chunk job.
                int hard = Region.HardBiome[i];
                Biome.BiomeDef waterDef = source >= 0 && hard >= 0 ? cfg.Biomes[hard] : default;
                bool frozen = SurfaceCover.WaterFreezes(wx, wz, t, waterDef, cfg);
                for (int y = 0; y < ChunkHeight; y++){
                    ushort v = y < h - 4 ? stone : y < h - 2 ? under : y < h ? beachUnder : y == h ? top : y < waterLevel ? TestFixtures.Water : TestFixtures.Air;
                    if (v == stone && def.strataBlock != 0 && SurfaceCover.InStrataBand(y, shift, cfg.StrataPeriod, cfg.StrataThickness))
                        v = def.strataBlock;
                    if (frozen && y == waterLevel - 1 && v == TestFixtures.Water) v = TestFixtures.Ice;
                    _voxels[Flat(x, y, z)] = v;
                }
            }

            probes.Dispose();

            // Single-block plants.
            for (int x = 0; x < Size.x; x++)
            for (int z = 0; z < Size.y; z++){
                int wx = Origin.x + x, wz = Origin.y + z;
                FloraRule rule = RuleAt(biomes, species, wx, wz, out int i);
                if (rule == null || rule.IsMultiBlock) continue;
                Put(fixture, new int3(wx, ColumnTop(i) + 1, wz), fixture.Ids[rule.SpeciesVoxel]);
            }

            // Anchors in world order: x, then z.
            NativeList<FloraVoxel> tree = new(256, Allocator.Temp);
            for (int wx = Origin.x - reach; wx < Origin.x + Size.x + reach; wx++)
            for (int wz = Origin.y - reach; wz < Origin.y + Size.y + reach; wz++){
                FloraRule rule = RuleAt(biomes, species, wx, wz, out int i);
                if (rule == null || !rule.IsMultiBlock) continue;
                int3 basePos = new(wx, ColumnTop(i) + 1, wz);
                List<int3> footprint = new();
                if (rule.SpeciesTree){
                    // Variant and shape from the shared tree code; the flat-footprint fallback from the region heights.
                    int speciesId = Region.FloraSpecies[i];
                    int treeIndex = Enumerable.Range(0, cfg.FloraRules.Length).Select(k => cfg.FloraRules[k]).First(d => d.Species == speciesId).Tree;
                    int variant = TreeShape.PickVariant(wx, wz, cfg, treeIndex);
                    TreeVariantDef v = cfg.TreeVariants[variant];
                    TreeRoll roll = TreeShape.Roll(wx, wz, cfg.Seed, variant, v);
                    bool flat = roll.Lying ? LyingRun(wx, wz, roll) >= roll.TrunkHeight : FlatFootprint(wx, wz, TreeShape.Footprint(roll));
                    if (roll.NeedsFlatFootprint && !flat){
                        roll.Narrow();
                        Narrowed.Add(new int2(wx, wz));
                        if (roll.Lying && LyingRun(wx, wz, roll) < roll.TrunkHeight)
                            throw new InvalidOperationException($"The region reports a lying trunk at ({wx}, {wz}) that fits nowhere.");
                    }

                    tree.Clear();
                    TreeShape.Build(basePos, roll, v, cfg.Seed, ref tree);
                    foreach (FloraVoxel fv in tree){
                        footprint.Add(basePos + fv.Offset);
                        Put(fixture, basePos + fv.Offset, fv.Voxel);
                    }

                    TreeVariants[new int2(wx, wz)] = variant;
                }
                else if (rule.SpeciesStructure){
                    foreach (StructurePart part in rule.SpeciesStructure.parts)
                    foreach (VoxelPlacement placement in part.placements)
                    foreach (int3 offset in FloraConfigBuilder.Expand(placement)){
                        footprint.Add(basePos + offset);
                        Put(fixture, basePos + offset, fixture.Ids[part.voxel]);
                    }
                }
                else{
                    int span = rule.MaxStackHeight - rule.MinStackHeight + 1;
                    int height = rule.MinStackHeight + (int)(Hash.Hash32(wx, wz, cfg.Seed, HashSalts.FloraStackHeight) % (uint)span);
                    for (int k = 0; k < height; k++){
                        footprint.Add(basePos + new int3(0, k, 0));
                        Put(fixture, basePos + new int3(0, k, 0), fixture.Ids[rule.SpeciesVoxel]);
                    }
                }

                if (footprint.Any(InArea)) Anchors.Add((new int2(wx, wz), footprint));
                else{
                    TreeVariants.Remove(new int2(wx, wz));
                    Narrowed.Remove(new int2(wx, wz));
                }
            }

            tree.Dispose();
        }

        /// <summary>True if the clamped terrain heights under the trunk footprint (relative box) differ by at most 1.</summary>
        private bool FlatFootprint(int x, int z, int4 box){
            int min = int.MaxValue, max = int.MinValue;
            for (int fx = x + box.x; fx <= x + box.z; fx++)
            for (int fz = z + box.y; fz <= z + box.w; fz++){
                int h = ColumnTop(Region.Index(fx, fz));
                min = math.min(min, h);
                max = math.max(max, h);
            }

            return max - min <= 1;
        }

        /// <summary>Columns from the anchor along a lying trunk that share the anchor's clamped terrain height, up to its length.</summary>
        private int LyingRun(int x, int z, in TreeRoll r){
            int h = ColumnTop(Region.Index(x, z));
            int run = 1;
            while (run < r.TrunkHeight && ColumnTop(Region.Index(x + r.LeanDirection.x * run, z + r.LeanDirection.y * run)) == h) run++;
            return run;
        }

        private int ColumnTop(int regionIndex){
            return math.clamp(Region.ColumnHeight[regionIndex], 0, MaxTerrainHeight);
        }

        /// <summary>The flora rule of the plant the region reports at (x, z), from its source biome; null if none.</summary>
        private FloraRule RuleAt(IReadOnlyList<Biome> biomes, List<Object> species, int x, int z, out int i){
            i = Region.Index(x, z);
            ushort id = Region.FloraSpecies[i];
            if (id == 0) return null;
            Object so = species[id - 1];
            return biomes[Region.FloraSource[i]].FloraRules.First(r => r.SpeciesVoxel == so || r.SpeciesStructure == so || r.SpeciesTree == so);
        }

        internal bool InArea(int3 p){
            return p.x >= Origin.x && p.z >= Origin.y && p.x < Origin.x + Size.x && p.z < Origin.y + Size.y &&
                   p.y >= 0 && p.y < ChunkHeight;
        }

        private int Flat(int x, int y, int z){
            return (x * Size.y + z) * ChunkHeight + y;
        }

        private void Put(FloraFixture fixture, int3 world, ushort voxel){
            if (!InArea(world)) return;
            Stamped.Add(world);
            int idx = Flat(world.x - Origin.x, world.y, world.z - Origin.y);
            if (fixture.ClassOf(_voxels[idx]) < fixture.ClassOf(voxel)) _voxels[idx] = voxel;
        }

        internal ushort this[int3 world] => _voxels[Flat(world.x - Origin.x, world.y, world.z - Origin.y)];

        internal int SurfaceAt(int x, int z){
            return ColumnTop(Region.Index(x, z));
        }
    }
}
