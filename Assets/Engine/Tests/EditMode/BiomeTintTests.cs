using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Render;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The biome tint (DEC-119 water, DEC-122 grass and plants): the palette, the tint map's layers and slots,
    ///     the per-texture tint words of the registry, the face rule, and the grey tiles and biome colours it needs.
    /// </summary>
    public class BiomeTintTests{
        private const string Standard = "Assets/Engine/Resources/VoxelDataPackages/Standard";

        private readonly List<BiomeTintMap> _maps = new();
        private readonly List<IDisposable> _disposables = new();
        private RegistryTestFixtures _fixtures;
        private ChunkVoxelData _data;

        // sRGB colours per layer (water, grass, plants) and biome; the water layer lacks biome 2
        private static readonly Color32[][] Colors ={
            new Color32[]{ new(200, 40, 40, 255), new(40, 200, 40, 255) },
            new Color32[]{ new(120, 194, 74, 255), new(184, 176, 78, 255), new(63, 186, 44, 255) },
            new Color32[]{ new(106, 174, 58, 255), new(168, 156, 60, 255), new(46, 165, 36, 255) }
        };

        [SetUp]
        public void SetUp(){
            _fixtures = new RegistryTestFixtures();
        }

        [TearDown]
        public void TearDown(){
            foreach (BiomeTintMap map in _maps) map.Dispose();
            _maps.Clear();
            foreach (IDisposable disposable in _disposables) disposable.Dispose();
            _disposables.Clear();
            _fixtures.DestroyAll();
            if (_data.Biomes.IsCreated) _data.Biomes.Dispose();
            _data = default;
        }

        private static BiomeTintPalette Palette(int version){
            IReadOnlyList<Color>[] colors = new IReadOnlyList<Color>[BiomeTintPalette.LayerCount];
            for (int layer = 0; layer < colors.Length; layer++)
                colors[layer] = Colors[layer].Select(c => (Color)c).ToArray();
            return new BiomeTintPalette(colors, version);
        }

        private BiomeTintMap Map(int drawDistance){
            BiomeTintMap map = new(drawDistance, Palette(0));
            _maps.Add(map);
            return map;
        }

        /// <summary>Writes the column biomes through the chunk job's own writer.</summary>
        private void WriteBiomes(Func<int, int, int> biomeAt){
            NativeArray<ChunkColumn> columns = new(ChunkWidth * ChunkDepth, Allocator.Temp);
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
                columns[z + x * ChunkDepth] = new ChunkColumn{ Biome = (ushort)biomeAt(x, z) };
            ChunkJob.WriteBiomes(columns, ref _data);
            columns.Dispose();
        }

        /// <summary>Biome 0 on the west half of a chunk, 1 on the east half: texel columns 0–3 and 4–7.</summary>
        private void WriteHalves(){
            WriteBiomes((x, _) => x < ChunkWidth / 2 ? 0 : 1);
        }

        private static void AssertColor(Color32 expected, Color32 actual, int delta, string what){
            Assert.AreEqual(expected.r, actual.r, delta, what + " red");
            Assert.AreEqual(expected.g, actual.g, delta, what + " green");
            Assert.AreEqual(expected.b, actual.b, delta, what + " blue");
        }

        private static Color32 Expected(BiomeTintPalette palette, int layer, int biome){
            float3 c = palette.Linear(layer, biome);
            return new Color(c.x, c.y, c.z, 1f).gamma;
        }

        #region Palette

        [Test]
        public void Palette_MissingBiomesAndLayersTakeTheDefaults(){
            BiomeTintPalette palette = Palette(7);
            Assert.AreEqual(7, palette.Version);
            Assert.AreEqual(3, palette.BiomeCount, "the longest layer sets the count");
            AssertColor(Colors[0][1], Expected(palette, 0, 1), 1, "known biome");
            AssertColor(Biome.DefaultWaterColor, Expected(palette, 0, 2), 1, "water lacks biome 2");
            AssertColor(Biome.DefaultGrassColor, Expected(palette, 1, -1), 1, "no biome");
            AssertColor(Biome.DefaultFoliageColor, Expected(palette, 2, 3), 1, "beyond the palette");

            BiomeTintPalette empty = BiomeTintPalette.FromBiomes(null, 1);
            Assert.AreEqual(0, empty.BiomeCount);
            AssertColor(Biome.DefaultGrassColor, Expected(empty, BiomeTintPalette.GrassLayer, 0), 1, "empty");
            Assert.AreEqual(Biome.DefaultFoliageColor, empty.DefaultColor(BiomeTintPalette.FoliageLayer));
        }

        [Test]
        public void Palette_FromBiomes_ReadsTheLayersInConfigOrder(){
            Biome savanna = _fixtures.CreateBiome("Savanna", null, null, null);
            savanna.WaterColor = new Color32(30, 90, 160, 255);
            savanna.GrassColor = new Color32(184, 176, 78, 255);
            savanna.FoliageColor = new Color32(168, 156, 60, 255);

            BiomeTintPalette palette = BiomeTintPalette.FromBiomes(new[]{ null, savanna }, 2);

            Assert.AreEqual(2, palette.BiomeCount);
            AssertColor(Biome.DefaultGrassColor, Expected(palette, BiomeTintPalette.GrassLayer, 0), 1, "null biome");
            AssertColor(savanna.WaterColor, Expected(palette, BiomeTintPalette.WaterLayer, 1), 1, "water");
            AssertColor(savanna.GrassColor, Expected(palette, BiomeTintPalette.GrassLayer, 1), 1, "grass");
            AssertColor(savanna.FoliageColor, Expected(palette, BiomeTintPalette.FoliageLayer, 1), 1, "plants");
        }

        #endregion

        #region Tint map

        [TestCase(1, 6)]
        [TestCase(12, 28)]
        [TestCase(32, 68)]
        public void TintMap_HoldsTheDrawnSquareWithSlack(int drawDistance, int chunks){
            Assert.AreEqual(chunks, BiomeTintMap.SizeChunksAt(drawDistance));
            Assert.Greater(chunks, 2 * drawDistance + 1, "two drawn chunks never share a slot");
        }

        [TestCase(0, 0)]
        [TestCase(27, 27)]
        [TestCase(28, 0)]
        [TestCase(-1, 27)]
        [TestCase(-28, 0)]
        [TestCase(-29, 27)]
        public void TintMap_SlotWrapsNegativeChunks(int chunk, int slot){
            Assert.AreEqual(slot, BiomeTintMap.Slot(chunk, 28));
        }

        [Test]
        public void TintMap_FillPixels_PutsEachLayerInItsOwnBlock(){
            WriteHalves();
            BiomeTintPalette palette = Palette(1);
            const int t = BiomeTintMap.ChunkTexels;
            Color32[] pixels = new Color32[t * t * BiomeTintPalette.LayerCount];

            BiomeTintMap.FillPixels(_data, palette, pixels);

            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++)
            for (int tx = 0; tx < t; tx++)
            for (int tz = 0; tz < t; tz++){
                Color32 expected = Expected(palette, layer, tx < t / 2 ? 0 : 1);
                AssertColor(expected, pixels[tx + (tz + layer * t) * t], 1, $"layer {layer}, texel ({tx}, {tz})");
            }
        }

        [Test]
        public void TintMap_FillPixels_AveragesItsColumnsInLinearLight(){
            WriteBiomes((x, z) => (x + 2 * z) % 3);
            BiomeTintPalette palette = Palette(1);
            const int t = BiomeTintMap.ChunkTexels, n = BiomeTintMap.BlendColumns;
            Color32[] pixels = new Color32[t * t * BiomeTintPalette.LayerCount];

            BiomeTintMap.FillPixels(_data, palette, pixels);

            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++)
            for (int tx = 0; tx < t; tx++)
            for (int tz = 0; tz < t; tz++){
                Color sum = Color.clear;
                for (int dx = 0; dx < n; dx++)
                for (int dz = 0; dz < n; dz++){
                    int biome = (tx * n + dx + 2 * (tz * n + dz)) % 3;
                    sum += ((Color)Expected(palette, layer, biome)).linear;
                }

                Color mean = (sum / (n * n)).gamma;
                AssertColor(mean, pixels[tx + (tz + layer * t) * t], 1, $"layer {layer}, texel ({tx}, {tz})");
            }

            // a texel of biomes 0 and 1 only is not the sRGB mean: linear light keeps the mix bright
            Color32 red = Colors[0][0], green = Colors[0][1];
            Color linearMix = ((((Color)red).linear + ((Color)green).linear) * 0.5f).gamma;
            Assert.Greater(linearMix.g, ((Color)red).g * 0.5f + ((Color)green).g * 0.5f);
        }

        [Test]
        public void TintMap_FillPixels_WithoutBiomesIsAllDefaults(){
            BiomeTintPalette palette = Palette(1);
            const int t = BiomeTintMap.ChunkTexels;
            Color32[] pixels = new Color32[t * t * BiomeTintPalette.LayerCount];
            ChunkVoxelData none = default;

            BiomeTintMap.FillPixels(none, palette, pixels);

            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++)
                AssertColor(palette.DefaultColor(layer), pixels[3 + (5 + layer * t) * t], 1, $"layer {layer}");
        }

        [Test]
        public void TintMap_Ensure_WritesOnceAndAgainAfterForgetOrNewColours(){
            WriteHalves();
            BiomeTintMap map = Map(1); // 6 chunks
            BiomeTintPalette palette = Palette(1);
            int2 chunk = new(-2, 5);

            Assert.IsTrue(map.Ensure(chunk, _data, palette), "first write");
            Assert.IsTrue(map.Owns(chunk));
            Assert.IsFalse(map.Ensure(chunk, _data, palette), "held already");

            int2 sameSlot = chunk + new int2(6, -6);
            Assert.AreEqual(map.SlotIndex(chunk), map.SlotIndex(sameSlot));
            map.Forget(sameSlot);
            Assert.IsTrue(map.Owns(chunk), "forgetting another chunk on the slot keeps the owner");
            Assert.IsTrue(map.Ensure(sameSlot, _data, palette), "the other chunk takes the slot");
            Assert.IsFalse(map.Owns(chunk));

            map.Forget(sameSlot);
            Assert.IsFalse(map.Owns(sameSlot));
            Assert.IsTrue(map.Ensure(sameSlot, _data, palette), "written again after eviction");
            Assert.IsTrue(map.Ensure(sameSlot, _data, Palette(2)), "and again for new biome colours");
        }

        [Test]
        public void TintMap_WritesEveryLayerIntoTheChunksSlot(){
            WriteHalves();
            BiomeTintMap map = Map(1); // 6 chunks, 48 texels
            BiomeTintPalette palette = Palette(1);
            int2 chunk = new(-1, 2); // slot (5, 2)
            map.Ensure(chunk, _data, palette);

            const int t = BiomeTintMap.ChunkTexels;
            int size = map.SizeTexels;
            Assert.AreEqual(6 * t, size);
            Assert.AreEqual(BiomeTintPalette.LayerCount, map.Texture.volumeDepth);
            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++){
                Color32[] texels = ReadSlice(map.Texture, layer);
                for (int tx = 0; tx < t; tx++)
                for (int tz = 0; tz < t; tz++){
                    Color32 expected = Expected(palette, layer, tx < t / 2 ? 0 : 1);
                    AssertColor(expected, texels[5 * t + tx + (2 * t + tz) * size], 1,
                        $"layer {layer}, texel ({tx}, {tz})");
                }

                AssertColor(palette.DefaultColor(layer), texels[0], 2, $"layer {layer}: the rest keeps the default");
            }
        }

        [Test]
        public void TintMap_WritesChunksWithoutAllocating(){
            WriteHalves();
            BiomeTintMap map = Map(1);
            BiomeTintPalette palette = Palette(1);
            map.Ensure(new int2(0, 0), _data, palette); // warm-up
            int x = 1;
            Assert.That(() => { map.Ensure(new int2(x++, 0), _data, palette); }, Is.Not.AllocatingGCMemory(),
                "the renderer writes chunks during flight (AllocationTests)");
        }

        private static Color32[] ReadSlice(RenderTexture texture, int slice){
            AsyncGPUReadbackRequest request =
                AsyncGPUReadback.Request(texture, 0, 0, texture.width, 0, texture.height, slice, 1);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "read back the tint map");
            return request.GetData<Color32>().ToArray();
        }

        #endregion

        #region Registry and face rule

        private static void Mode(Voxel voxel, Voxel.VoxelTexMode mode){
            voxel.TextureMode = mode;
        }

        private VoxelRegistry Registry(string prefix, params Voxel[] voxels){
            VoxelRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize(_fixtures.CreateTexture("ErrSolid", new Color32(255, 0, 255, 255)),
                _fixtures.CreateTexture("ErrTransparent", new Color32(255, 0, 255, 128)),
                _fixtures.CreateTexture("ErrFoliage", new Color32(255, 0, 255, 64)));
            foreach (Voxel voxel in voxels) registry.Register(prefix, voxel);
            registry.FinalizeRegistry();
            return registry;
        }

        private VoxelShape Cube(){
            return _fixtures.CreateShape("Cube",
                (_fixtures.CreateQuad("Up", Vector3.up), QuadDrawCondition.Up),
                (_fixtures.CreateQuad("Down", Vector3.zero), QuadDrawCondition.Down),
                (_fixtures.CreateQuad("Front", Vector3.forward), QuadDrawCondition.Forward));
        }

        [Test]
        public void FaceRule_TopFullSidesThroughTheMaskBottomNever(){
            VoxelDataPackage package = _fixtures.CreatePackage("tint");
            Texture2D tex = _fixtures.CreateTexture("Tex", new Color32(178, 178, 178, 255));
            Texture2D mask = _fixtures.CreateTexture("Mask", new Color32(178, 178, 178, 255));
            Voxel grass = _fixtures.CreateVoxel("Grass", package, Cube(), tex);
            Mode(grass, Voxel.VoxelTexMode.TopBottomSides);
            grass.biomeTint = VoxelBiomeTint.Grass;
            grass.biomeTintMask = mask;

            Assert.AreEqual((true, false), grass.BiomeTintOf(QuadDrawCondition.Up), "top: fully");
            Assert.AreEqual((false, false), grass.BiomeTintOf(QuadDrawCondition.Down), "bottom: never");
            Assert.AreEqual((true, true), grass.BiomeTintOf(QuadDrawCondition.Forward), "side: through the mask");
            Assert.AreEqual(Color.white, grass.DisplaySideTint, "an icon draws the side untinted under the overlay");

            grass.biomeTintMask = null;
            Assert.AreEqual((true, false), grass.BiomeTintOf(QuadDrawCondition.Left), "no mask: the side fully");
            Assert.AreEqual(grass.DisplayTint, grass.DisplaySideTint);

            Mode(grass, Voxel.VoxelTexMode.AllSame);
            Assert.AreEqual((true, false), grass.BiomeTintOf(QuadDrawCondition.Down), "one texture: every face");

            grass.biomeTint = VoxelBiomeTint.None;
            Assert.AreEqual((false, false), grass.BiomeTintOf(QuadDrawCondition.Up));
            Assert.AreEqual(Color.white, grass.DisplayTint, "an untinted block draws its icon as is");
        }

        [Test]
        public void DisplayTint_IsTheDefaultBiomeColourOverTheGreyMean(){
            Voxel voxel = ScriptableObject.CreateInstance<Voxel>();
            try{
                voxel.biomeTint = VoxelBiomeTint.Foliage;
                // the grey tiles' mean (linear 0.70) times the icon tint, in linear light as the UI draws it
                Color tinted = voxel.DisplayTint.linear * Voxel.BiomeTintGreyMean;
                Color expected = Biome.DefaultFoliageColor.linear;
                Assert.AreEqual(expected.r, tinted.r, 0.02f);
                Assert.AreEqual(expected.g, tinted.g, 0.02f);
                Assert.AreEqual(expected.b, tinted.b, 0.02f);
            }
            finally{
                Object.DestroyImmediate(voxel);
            }
        }

        [Test]
        public void Registry_GivesEveryTextureItsTintWordAndMaskSlice(){
            VoxelDataPackage package = _fixtures.CreatePackage("tint");
            Texture2D top = _fixtures.CreateTexture("Top", new Color32(178, 178, 178, 255));
            Texture2D side = _fixtures.CreateTexture("Side", new Color32(120, 90, 60, 255));
            Texture2D bottom = _fixtures.CreateTexture("Bottom", new Color32(120, 90, 60, 255));
            Texture2D stoneTex = _fixtures.CreateTexture("Stone", new Color32(128, 128, 128, 255));
            Texture2D plantTex = _fixtures.CreateTexture("Plant", new Color32(178, 178, 178, 200));
            Texture2D mask = _fixtures.CreateTexture("Mask", new Color32(178, 178, 178, 255));

            Voxel grass = _fixtures.CreateVoxel("Grass", package, Cube(), top);
            Mode(grass, Voxel.VoxelTexMode.TopBottomSides);
            grass.top = top;
            grass.side = side;
            grass.bottom = bottom;
            grass.biomeTint = VoxelBiomeTint.Grass;
            grass.biomeTintMask = mask;
            Voxel stone = _fixtures.CreateVoxel("Stone", package, Cube(), stoneTex);
            Voxel plant = _fixtures.CreateVoxel("Plant", package,
                _fixtures.CreateShape("Cross", (_fixtures.CreateQuad("X", Vector3.zero), QuadDrawCondition.Always)),
                plantTex, MeshLayer.Foliage, false);
            plant.biomeTint = VoxelBiomeTint.Foliage;

            VoxelRegistry registry = Registry("tint", grass, stone, plant);

            uint Word(Texture2D tex, MeshLayer layer){
                Assert.IsTrue(registry.TryGetTextureIndex(tex, layer, out ushort index), tex.name);
                return registry.TexTintWords(layer)[index];
            }

            Assert.IsTrue(registry.TryGetTintMaskSlice(mask, out ushort slice));
            Assert.AreEqual(1u, Word(top, MeshLayer.Solid), "grass top: layer grass, no mask");
            Assert.AreEqual(1u | 4u | ((uint)slice << 16), Word(side, MeshLayer.Solid), "grass side: masked");
            Assert.AreEqual(0u, Word(bottom, MeshLayer.Solid), "grass bottom: untinted");
            Assert.AreEqual(0u, Word(stoneTex, MeshLayer.Solid));
            Assert.AreEqual(2u, Word(plantTex, MeshLayer.Foliage), "plant: layer foliage");
            Assert.IsNotNull(registry.TintMaskArray);
            Assert.AreEqual(1, registry.TintMaskArray.depth);
        }

        [Test]
        public void Registry_WithoutMasksBindsOneClearSlice(){
            VoxelDataPackage package = _fixtures.CreatePackage("tint");
            Voxel stone = _fixtures.CreateVoxel("Stone", package, Cube(),
                _fixtures.CreateTexture("Stone", new Color32(128, 128, 128, 255)));

            VoxelRegistry registry = Registry("tint", stone);

            Assert.IsNotNull(registry.TintMaskArray, "the global is never unbound");
            Assert.AreEqual(1, registry.TintMaskArray.depth);
        }

        [Test]
        public void Registry_TextureWithTwoTints_KeepsTheFirstAndLogs(){
            VoxelDataPackage package = _fixtures.CreatePackage("tint");
            Texture2D shared = _fixtures.CreateTexture("Shared", new Color32(178, 178, 178, 255));
            VoxelShape oneFace = _fixtures.CreateShape("OneFace",
                (_fixtures.CreateQuad("Q", Vector3.zero), QuadDrawCondition.Always)); // one error, not one per face
            Voxel grass = _fixtures.CreateVoxel("Grass", package, oneFace, shared);
            grass.biomeTint = VoxelBiomeTint.Grass;
            Voxel moss = _fixtures.CreateVoxel("Moss", package, oneFace, shared);

            LogAssert.Expect(LogType.Error, new Regex("Shared.*two biome tints"));
            VoxelRegistry registry = Registry("tint", grass, moss);

            Assert.IsTrue(registry.TryGetTextureIndex(shared, MeshLayer.Solid, out ushort index));
            Assert.AreEqual(1u, registry.TexTintWords(MeshLayer.Solid)[index]);
        }

        [Test]
        public void Registry_TransparentVoxelAskingForATint_StaysUntintedAndWarns(){
            VoxelDataPackage package = _fixtures.CreatePackage("tint");
            Texture2D tex = _fixtures.CreateTexture("Leaves", new Color32(178, 178, 178, 128));
            Voxel leaves = _fixtures.CreateVoxel("Leaves", package, Cube(), tex, MeshLayer.Transparent);
            leaves.biomeTint = VoxelBiomeTint.Foliage;

            LogAssert.Expect(LogType.Warning, new Regex("Leaves.*transparent layer"));
            VoxelRegistry registry = Registry("tint", leaves);

            Assert.IsTrue(registry.TryGetTextureIndex(tex, MeshLayer.Transparent, out ushort index));
            Assert.AreEqual(0u, registry.TexTintWords(MeshLayer.Transparent)[index]);
        }

        #endregion

        #region Shipped assets

        private static IEnumerable<T> LoadAll<T>(string folder) where T : Object{
            return AssetDatabase.FindAssets("t:" + typeof(T).Name, new[]{ folder })
                .Select(guid => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid)));
        }

        private static Voxel LoadVoxel(string path){
            Voxel voxel = AssetDatabase.LoadAssetAtPath<Voxel>($"{Standard}/{path}.asset");
            Assert.IsNotNull(voxel, path);
            return voxel;
        }

        /// <summary>The PNG behind an imported tile, decoded as it is on disk (the import is compressed).</summary>
        private static Color32[] SourcePixels(Texture2D tile){
            string path = AssetDatabase.GetAssetPath(tile);
            Assert.That(path, Does.EndWith(".png"), tile.name);
            Texture2D decoded = new(2, 2, TextureFormat.RGBA32, false, false);
            try{
                Assert.IsTrue(decoded.LoadImage(File.ReadAllBytes(path)), path);
                return decoded.GetPixels32();
            }
            finally{
                Object.DestroyImmediate(decoded);
            }
        }

        private static readonly string[] Tinted ={
            "Solid/Grass", "Solid/StoneGrassy", "Foliage/GrassF", "Foliage/GrassFDry", "Foliage/GrassFSwamp",
            "Foliage/GrassFTall", "Foliage/Fern", "Foliage/Reeds"
        };

        [Test]
        public void Assets_TintTheGreenGrassAndPlantsOnly(){
            Assert.AreEqual(VoxelBiomeTint.Grass, LoadVoxel("Solid/Grass").biomeTint);
            Assert.AreEqual(VoxelBiomeTint.Grass, LoadVoxel("Solid/StoneGrassy").biomeTint);
            Assert.IsNotNull(LoadVoxel("Solid/Grass").biomeTintMask, "the grass side keeps its dirt untinted");
            Assert.IsNotNull(LoadVoxel("Solid/StoneGrassy").biomeTintMask);
            foreach (string path in Tinted.Where(p => p.StartsWith("Foliage/")))
                Assert.AreEqual(VoxelBiomeTint.Foliage, LoadVoxel(path).biomeTint, path);

            HashSet<string> tinted = new(Tinted.Select(p => p.Substring(p.IndexOf('/') + 1)));
            foreach (Voxel voxel in LoadAll<Voxel>(Standard).Where(v => !tinted.Contains(v.name)))
                Assert.AreEqual(VoxelBiomeTint.None, voxel.biomeTint, voxel.name + ": flowers, leaves, dead plants stay");
        }

        [Test]
        public void Assets_FoldedGrassVariantsAreGone(){
            string[] folded ={
                "GrassDry", "GrassJungle", "GrassSwamp", "GrassTaiga", "StoneGrassyTaiga", "GrassFTallDry",
                "GrassFTallJungle"
            };
            HashSet<string> names = new(LoadAll<Voxel>(Standard).Select(v => v.name));
            foreach (string name in folded) Assert.IsFalse(names.Contains(name), name + " folds into one tinted block");

            VoxelDataPackage package = AssetDatabase.LoadAssetAtPath<VoxelDataPackage>($"{Standard}/Standard.asset");
            Assert.IsNotNull(package);
            Assert.IsTrue(package.voxel.All(v => v), "no missing voxel in the package list");
            Voxel grass = LoadVoxel("Solid/Grass");
            foreach (string biome in new[]{ "Savanna", "Jungle", "Swamp", "Taiga", "Plains", "Forest" }){
                Biome asset = AssetDatabase.LoadAssetAtPath<Biome>($"{Standard}/Bioms/{biome}.asset");
                Assert.AreEqual(grass, asset.TopBlock, biome + " grows the one tinted grass");
            }
        }

        [Test]
        public void Assets_TintTilesAreGreyWithTheSharedMean(){
            HashSet<Texture2D> checkedTiles = new();
            foreach (string path in Tinted){
                Voxel voxel = LoadVoxel(path);
                foreach (QuadDrawCondition condition in new[]{ QuadDrawCondition.Up, QuadDrawCondition.Forward }){
                    (bool tinted, bool masked) = voxel.BiomeTintOf(condition);
                    if (!tinted) continue;
                    // a masked side: the tinted part is the overlay's opaque pixels
                    Texture2D tile = masked ? voxel.biomeTintMask : voxel.GetDisplayTexture(condition);
                    if (!checkedTiles.Add(tile)) continue;

                    double sum = 0;
                    int count = 0;
                    foreach (Color32 p in SourcePixels(tile)){
                        if (p.a == 0) continue; // the generator greys every pixel it can see
                        Assert.AreEqual(p.r, p.g, 2, tile.name + ": grey");
                        Assert.AreEqual(p.r, p.b, 2, tile.name + ": grey");
                        sum += Mathf.GammaToLinearSpace(p.g / 255f);
                        count++;
                    }

                    Assert.Greater(count, 0, tile.name);
                    Assert.AreEqual(Voxel.BiomeTintGreyMean, sum / count, 0.02, tile.name + ": linear mean");
                }
            }

            Assert.GreaterOrEqual(checkedTiles.Count, 8);
        }

        [Test]
        public void Assets_OverlayAlphaIsOnOrOff(){
            foreach (string path in new[]{ "Solid/Grass", "Solid/StoneGrassy" }){
                Texture2D overlay = LoadVoxel(path).biomeTintMask;
                Color32[] pixels = SourcePixels(overlay);
                Assert.That(pixels.All(p => p.a is 0 or 255), overlay.name + ": the shader tints or does not");
                Assert.That(pixels.Any(p => p.a == 0) && pixels.Any(p => p.a == 255), overlay.name);
            }
        }

        [Test]
        public void Assets_EveryBiomeHasGrassAndPlantColours(){
            Biome[] biomes = LoadAll<Biome>($"{Standard}/Bioms").ToArray();
            Assert.Greater(biomes.Length, 5);
            foreach (Biome biome in biomes){
                Assert.AreEqual(1f, biome.GrassColor.a, biome.name);
                Assert.AreEqual(1f, biome.FoliageColor.a, biome.name);
                Assert.LessOrEqual(biome.FoliageColor.grayscale, biome.GrassColor.grayscale,
                    biome.name + ": plants a shade darker than the grass");
            }

            Biome jungle = biomes.Single(b => b.name == "Jungle"), desert = biomes.Single(b => b.name == "Desert");
            Assert.Greater(jungle.GrassColor.g - jungle.GrassColor.r, desert.GrassColor.g - desert.GrassColor.r,
                "jungle grass greener than desert grass");
        }

        #endregion
    }
}
