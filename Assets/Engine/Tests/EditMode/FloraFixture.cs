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

        internal FloraFixture(){
            GrassSo = MakeVoxel("Grass", TestFixtures.Grass, MeshLayer.Solid, true);
            DirtSo = MakeVoxel("Dirt", TestFixtures.Dirt, MeshLayer.Solid, true);
            StoneSo = MakeVoxel("Stone", TestFixtures.Stone, MeshLayer.Solid, true);
            SandSo = MakeVoxel("Sand", Sand, MeshLayer.Solid, true);
            TuftSo = MakeVoxel("Tuft", Tuft, MeshLayer.Foliage, false);
            FlowerSo = MakeVoxel("Flower", Flower, MeshLayer.Foliage, false);
            LogSo = MakeVoxel("Log", Log, MeshLayer.Solid, true);
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
        ///     The two flora test biomes: a meadow (grass, flowers, trees) and dunes (cactus stacks 1–3, sparse grass,
        ///     pines), both above the water level 124 in Biome-first. Probabilities are high so every chunk has flora.
        /// </summary>
        internal Biome[] DefaultBiomes(){
            return new[]{
                MakeBiome("Meadow", .25f, 150f, 10f, GrassSo, Rule(TuftSo, .3f), Rule(FlowerSo, .05f), Rule(Tree, .06f)),
                MakeBiome("Dunes", .75f, 138f, 6f, SandSo, Rule(CactusSo, .03f, 1, 3), Rule(TuftSo, .05f), Rule(Pine, .03f))
            };
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
            return s.ToConfigWithVoxelIds(TestFixtures.Seed, v => v ? Ids[v] : (ushort)0, allocator);
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

        private readonly ushort[] _voxels;

        internal ReferenceWorld(FloraFixture fixture, IReadOnlyList<Biome> biomes, in GeneratorConfig config,
            int2 originChunk, int2 chunks){
            WorldGenConfig cfg = config.WorldGen;
            Origin = originChunk * new int2(ChunkWidth, ChunkDepth);
            Size = chunks * new int2(ChunkWidth, ChunkDepth);
            int reach = cfg.FloraReach;
            Region = RegionSample.Sample(cfg, Origin - reach, Size + 2 * reach);
            _voxels = new ushort[Size.x * Size.y * ChunkHeight];

            List<Object> species = FloraConfigBuilder.CollectSpecies(biomes);
            int waterLevel = config.WorldGen.WaterLevel;

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
                if (b && SurfaceCover.IsSnowCovered(wx, wz, t, h, def, cfg)) top = def.snowTopBlock;
                ushort under = b ? fixture.Ids[b.UnderBlock] : (ushort)0;
                ushort stone = b ? fixture.Ids[b.StoneBlock] : (ushort)0;
                int shift = SurfaceCover.StrataShift(wx, wz, cfg);
                bool frozen = SurfaceCover.WaterFreezes(wx, wz, t, cfg);
                for (int y = 0; y < ChunkHeight; y++){
                    ushort v = y < h - 4 ? stone : y < h ? under : y == h ? top : y < waterLevel ? TestFixtures.Water : TestFixtures.Air;
                    if (v == stone && def.strataBlock != 0 && SurfaceCover.InStrataBand(y, shift, cfg.StrataPeriod, cfg.StrataThickness))
                        v = def.strataBlock;
                    if (frozen && y == waterLevel - 1 && v == TestFixtures.Water) v = TestFixtures.Ice;
                    _voxels[Flat(x, y, z)] = v;
                }
            }

            // Single-block plants.
            for (int x = 0; x < Size.x; x++)
            for (int z = 0; z < Size.y; z++){
                int wx = Origin.x + x, wz = Origin.y + z;
                FloraRule rule = RuleAt(biomes, species, wx, wz, out int i);
                if (rule == null || rule.IsMultiBlock) continue;
                Put(fixture, new int3(wx, ColumnTop(i) + 1, wz), fixture.Ids[rule.SpeciesVoxel]);
            }

            // Anchors in world order: x, then z.
            for (int wx = Region.Origin.x; wx < Region.Origin.x + Region.Size.x; wx++)
            for (int wz = Region.Origin.y; wz < Region.Origin.y + Region.Size.y; wz++){
                FloraRule rule = RuleAt(biomes, species, wx, wz, out int i);
                if (rule == null || !rule.IsMultiBlock) continue;
                int3 basePos = new(wx, ColumnTop(i) + 1, wz);
                List<int3> footprint = new();
                if (rule.SpeciesStructure){
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
            }
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
            return biomes[Region.FloraSource[i]].FloraRules.First(r => r.SpeciesVoxel == so || r.SpeciesStructure == so);
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
