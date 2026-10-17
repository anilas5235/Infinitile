using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Engine.Tests.EditMode{
    /// <summary>TST-E08 (Registry, TopLevelRegistry), TST-E09 (D-04) and TST-E19 (array index = ID, air, F-22).</summary>
    public class RegistryTests{
        private RegistryTestFixtures _fixtures;
        private readonly List<IDisposable> _disposables = new();

        [SetUp]
        public void SetUp(){
            _fixtures = new RegistryTestFixtures();
        }

        [TearDown]
        public void TearDown(){
            foreach (IDisposable disposable in _disposables) disposable.Dispose();
            _disposables.Clear();
            _fixtures.DestroyAll();
        }

        private VoxelRegistry StandardVoxels(out Voxel[] voxels){
            VoxelRegistry registry = _fixtures.CreateStandardVoxelRegistry(out _, out voxels);
            _disposables.Add(registry);
            return registry;
        }

        #region TST-E08 Registry<T>

        [Test]
        public void Registry_IdsAreUniqueAndFollowRegistrationOrder(){
            Registry<string> registry = new(4);
            Assert.AreEqual(0, registry.Register("a"));
            Assert.AreEqual(1, registry.Register("b"));
            Assert.AreEqual(2, registry.Register("c"));
            Assert.AreEqual(3, registry.Count);

            Assert.IsTrue(registry.TryGet(1, out string item));
            Assert.AreEqual("b", item);
            Assert.IsTrue(registry.TryGetId("c", out ushort id));
            Assert.AreEqual(2, id);
            Assert.IsFalse(registry.TryGet(3, out _));
            Assert.IsFalse(registry.TryGetId("zzz", out _));

            List<KeyValuePair<ushort, string>> entries = registry.GetAllEntries();
            CollectionAssert.AreEqual(new ushort[]{ 0, 1, 2 }, entries.Select(e => e.Key));
            CollectionAssert.AllItemsAreUnique(entries.Select(e => e.Key).ToList());
        }

        [Test]
        public void Registry_DuplicateRegistration_ReturnsExistingIdAndDoesNotGrow(){
            Registry<string> registry = new(4);
            ushort first = registry.Register("a");
            registry.Register("b");
            Assert.AreEqual(first, registry.Register("a"));
            Assert.AreEqual(2, registry.Count);
        }

        [Test]
        public void Registry_RegisterWithExplicitId_IsRetrievableAndRejectsKnownItems(){
            Registry<string> registry = new(4);
            Assert.IsTrue(registry.Register(7, "x"));
            Assert.IsTrue(registry.TryGet(7, out string item));
            Assert.AreEqual("x", item);
            Assert.IsTrue(registry.TryGetId("x", out ushort id));
            Assert.AreEqual(7, id);
            // Documented behaviour: an item that is already registered is ignored (with a warning) and keeps its ID.
            Assert.IsFalse(registry.Register(9, "x"));
            Assert.IsTrue(registry.TryGetId("x", out id));
            Assert.AreEqual(7, id);
        }

        #endregion

        #region TST-E08 TopLevelRegistry<T>

        [Test]
        public void TopLevelRegistry_RegisterBeforeInitialize_Throws(){
            BiomeRegistry registry = new();
            Biome biome = _fixtures.CreateBiome("Plains", null, null, null);
            Assert.Throws<InvalidOperationException>(() => registry.Register(RegistryTestFixtures.Prefix, biome));
        }

        [Test]
        public void TopLevelRegistry_RegisterAfterFinalize_Throws(){
            VoxelRegistry voxels = StandardVoxels(out Voxel[] v);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            registry.FinalizeRegistry(voxels);
            Biome biome = _fixtures.CreateBiome("Plains", v[0], v[1], v[2]);
            Assert.Throws<InvalidOperationException>(() => registry.Register(RegistryTestFixtures.Prefix, biome));
            Assert.Throws<InvalidOperationException>(() => registry.Initialize(), "double initialise");
            Assert.Throws<InvalidOperationException>(() => registry.FinalizeRegistry(voxels), "double finalise");
        }

        [Test]
        public void TopLevelRegistry_NullRegistration_LogsErrorAndIsIgnored(){
            VoxelRegistry voxels = StandardVoxels(out _);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            LogAssert.Expect(LogType.Error, new Regex("Cannot register a null Biome"));
            registry.Register(RegistryTestFixtures.Prefix, null);
            registry.FinalizeRegistry(voxels);
            Assert.AreEqual(0, registry.BiomeArray.Length);
        }

        [Test]
        public void TopLevelRegistry_NameTooLong_LogsErrorAndIsIgnored(){
            VoxelRegistry voxels = StandardVoxels(out Voxel[] v);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            Biome biome = _fixtures.CreateBiome(new string('x', FixedString32Bytes.UTF8MaxLengthInBytes), v[0], v[1], v[2]);
            LogAssert.Expect(LogType.Error, new Regex("exceeds the maximum length"));
            registry.Register(RegistryTestFixtures.Prefix, biome);
            registry.FinalizeRegistry(voxels);
            Assert.AreEqual(0, registry.BiomeArray.Length);
        }

        [Test]
        public void TopLevelRegistry_DuplicateName_IsSkipped(){
            VoxelRegistry voxels = StandardVoxels(out Voxel[] v);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            Biome plains = _fixtures.CreateBiome("Plains", v[0], v[1], v[2]);
            Biome plainsAgain = _fixtures.CreateBiome("Plains", v[2], v[1], v[0]);
            registry.Register(RegistryTestFixtures.Prefix, plains);
            registry.Register(RegistryTestFixtures.Prefix, plainsAgain);
            registry.FinalizeRegistry(voxels);

            Assert.AreEqual(1, registry.BiomeArray.Length);
            Assert.IsTrue(registry.TryGet(0, out Biome stored));
            Assert.AreSame(plains, stored, "the first registration wins");
        }

        [Test]
        public void TopLevelRegistry_Lookups(){
            VoxelRegistry voxels = StandardVoxels(out Voxel[] v);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            Biome plains = _fixtures.CreateBiome("Plains", v[0], v[1], v[2]);
            Biome hills = _fixtures.CreateBiome("Hills", v[2], v[1], v[2]);
            registry.Register(RegistryTestFixtures.Prefix, plains);
            registry.Register("other", hills);
            registry.FinalizeRegistry(voxels);

            Assert.IsTrue(registry.TryGetId("std:Plains", out ushort id));
            Assert.AreEqual(0, id);
            Assert.AreEqual(1, registry.GetIdOrThrow("other:Hills"));
            Assert.Throws<KeyNotFoundException>(() => registry.GetIdOrThrow("std:Hills"));
            Assert.IsFalse(registry.TryGetId("std:Hills", out _));

            Assert.IsTrue(registry.TryGetName(1, out FixedString32Bytes name));
            Assert.AreEqual("other:Hills", name.ToString());
            Assert.IsFalse(registry.TryGetName(2, out _));

            Assert.IsTrue(registry.TryGet(1, out Biome so));
            Assert.AreSame(hills, so);
            Assert.IsFalse(registry.TryGet(2, out _));

            Assert.AreEqual(2, registry.BiomeArray.Length);
            Assert.AreEqual(voxels.GetIdOrThrow("std:Grass"), registry.BiomeArray[0].topBlock);
            Assert.AreEqual(voxels.GetIdOrThrow("std:Stone"), registry.BiomeArray[1].topBlock);
        }

        #endregion

        #region TST-E09 BiomeRegistry (D-04)

        [Test]
        public void BiomeRegistry_FailingBiome_DoesNotShiftIndicesOrYieldAirBiome(){
            // D-04: a biome whose conversion fails must neither shift the biomes after it nor leave a
            // zero-initialised (air-filled) BiomeDef in the array.
            VoxelRegistry voxels = StandardVoxels(out Voxel[] v);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            Biome plains = _fixtures.CreateBiome("Plains", v[0], v[1], v[2]);
            Biome broken = _fixtures.CreateBiome("Broken", null, v[1], v[2]);
            Biome hills = _fixtures.CreateBiome("Hills", v[2], v[2], v[2]);
            registry.Register(RegistryTestFixtures.Prefix, plains);
            registry.Register(RegistryTestFixtures.Prefix, broken);
            registry.Register(RegistryTestFixtures.Prefix, hills);

            LogAssert.ignoreFailingMessages = true;
            try{
                registry.FinalizeRegistry(voxels);
            }
            catch (InvalidOperationException){
                Assert.Pass("the registry rejected the broken biome at finalisation");
            }
            finally{
                LogAssert.ignoreFailingMessages = false;
            }

            ushort air = voxels.GetIdOrThrow("std:air");
            for (int i = 0; i < registry.BiomeArray.Length; i++){
                Biome.BiomeDef def = registry.BiomeArray[i];
                Assert.AreNotEqual(air, def.topBlock, $"BiomeArray[{i}] has an air top block");
                Assert.AreNotEqual(air, def.underBlock, $"BiomeArray[{i}] has an air under block");
                Assert.AreNotEqual(air, def.stoneBlock, $"BiomeArray[{i}] has an air stone block");
            }

            Assert.IsTrue(registry.TryGetId("std:Hills", out ushort hillsId));
            Assert.Less(hillsId, registry.BiomeArray.Length);
            Assert.AreEqual(voxels.GetIdOrThrow("std:Stone"), registry.BiomeArray[hillsId].topBlock,
                "BiomeArray[id of Hills] must be the Hills definition");
            Assert.IsTrue(registry.TryGetId("std:Plains", out ushort plainsId));
            Assert.AreEqual(voxels.GetIdOrThrow("std:Grass"), registry.BiomeArray[plainsId].topBlock);
        }

        #endregion

        #region TST-E19 registries

        [Test]
        public void QuadRegistry_ArrayIndexEqualsRegisteredId(){
            QuadRegistry registry = new(4);
            QuadDefinition[] quads ={
                _fixtures.CreateQuad("Q0", new Vector3(0, 0, 0)),
                _fixtures.CreateQuad("Q1", new Vector3(1, 0, 0)),
                _fixtures.CreateQuad("Q2", new Vector3(2, 0, 0))
            };
            ushort[] ids = quads.Select(q => registry.Register(q)).ToArray();
            registry.PrepareArray();

            Assert.AreEqual(3, registry.QuadArray.Length);
            for (int i = 0; i < quads.Length; i++){
                Assert.AreEqual(quads[i].position00.x, registry.QuadArray[ids[i]].position00.x,
                    $"QuadArray[{ids[i]}] must be {quads[i].name}");
            }

            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
        }

        [Test]
        public void QuadRegistry_EmptyPrepareArray_GivesEmptyArray(){
            QuadRegistry registry = new(4);
            registry.PrepareArray();
            Assert.AreEqual(0, registry.QuadArray.Length);
        }

        [Test]
        public void TexRegistry_ArrayIndexEqualsRegisteredId(){
            TexRegistry registry = new(4);
            Color32[] colours ={ new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255) };
            ushort[] ids = new ushort[colours.Length];
            for (int i = 0; i < colours.Length; i++)
                ids[i] = registry.Register(_fixtures.CreateTexture("T" + i, colours[i]));
            registry.PrepareArray();

            Texture2DArray array = registry.TextureArray;
            Assert.IsNotNull(array);
            Assert.AreEqual(colours.Length, array.depth);
            Assert.AreEqual(FilterMode.Point, array.filterMode);
            Assert.AreEqual(TextureWrapMode.Repeat, array.wrapMode);
            try{
                for (int i = 0; i < colours.Length; i++){
                    AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(array, 0, 0, 1, 0, 1, ids[i], 1,
                        TextureFormat.RGBA32);
                    request.WaitForCompletion();
                    Assert.IsFalse(request.hasError, "GPU read-back failed");
                    Color32 pixel = request.GetData<Color32>()[0];
                    Assert.AreEqual(colours[i], pixel, $"layer {ids[i]} must hold texture T{i}");
                }
            }
            finally{
                UnityEngine.Object.DestroyImmediate(array);
            }
        }

        [Test]
        public void TexRegistry_RejectsNullWrongSizeAndMixedFormats(){
            TexRegistry registry = new(4);
            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
            Texture2D small = new(16, 16, TextureFormat.RGBA32, false);
            try{
                Assert.Throws<ArgumentException>(() => registry.Register(small));
            }
            finally{
                UnityEngine.Object.DestroyImmediate(small);
            }

            registry.Register(_fixtures.CreateTexture("rgba", new Color32(1, 2, 3, 4)));
            Texture2D otherFormat = new(VoxelRegistry.TextureSize, VoxelRegistry.TextureSize, TextureFormat.RGB24, false);
            try{
                Assert.Throws<ArgumentException>(() => registry.Register(otherFormat));
            }
            finally{
                UnityEngine.Object.DestroyImmediate(otherFormat);
            }
        }

        [Test]
        public void VoxelRegistry_AirIsIdZeroWithAirLayer(){
            VoxelRegistry registry = StandardVoxels(out Voxel[] voxels);
            Assert.AreEqual(0, registry.GetIdOrThrow("std:air"));
            Assert.IsTrue(registry.VoxelMap.TryGetValue("std:air", out Voxel.VoxelDef air));
            Assert.AreEqual(0, air.Id);
            Assert.AreEqual(MeshLayer.Air, air.MeshLayer);
            Assert.IsTrue(air.IsAir);
            Assert.IsFalse(air.Collision);
            Assert.AreEqual(MeshLayer.Air, registry.GetVoxelGenData().GetMeshLayer(0));

            for (int i = 0; i < voxels.Length; i++){
                ushort id = registry.GetIdOrThrow(voxels[i].GetFullName());
                Assert.AreEqual(i + 1, id, "voxels follow air in registration order");
                Assert.AreEqual(MeshLayer.Solid, registry.GetVoxelGenData().GetMeshLayer(id));
                Assert.AreEqual(id, registry.GetVoxelGenData().GetRenderDef(id).Id);
                Assert.IsTrue(registry.TryGet(id, out Voxel so));
                Assert.AreSame(voxels[i], so);
            }

            Assert.AreEqual(voxels.Length + 1, registry.VoxelMap.Count);
            Assert.AreEqual(voxels.Length + 1, registry.VoxelRenderDefBuffer.count);
            Assert.AreEqual(1, registry.QuadBuffer.count, "one shared quad definition");
            Assert.AreEqual(voxels.Length, registry.QuadTexPairBuffer.count, "one quad/texture pair per voxel");
        }

        [Test]
        public void VoxelRegistry_RenderDefsMatchVoxelAssets(){
            VoxelRegistry registry = StandardVoxels(out Voxel[] voxels);
            voxels[1].collision = false; // not re-registered: the registry snapshotted the values
            ushort dirt = registry.GetIdOrThrow("std:Dirt");
            Voxel.VoxelDef def = registry.GetVoxelGenData().GetRenderDef(dirt);
            Assert.IsTrue(def.Collision, "registered value is kept");
            Assert.AreEqual(1u, def.Always.y, "one Always quad");
            Assert.AreEqual(0u, def.Up.y, "no Up quads");
            Assert.AreEqual(dirt - 1, (int)def.Always.x, "pairs are appended in registration order");
        }

        [Category("KnownBug")] // F-22
        [Test]
        public void BiomeRegistry_SameBiomeAssetInTwoPackages_IsRetrievableUnderBothIds(){
            // F-22: TopLevelRegistry.Register ignores the failed SO registration for the second name,
            // leaving a default (air) BiomeDef at the end of BiomeArray.
            VoxelRegistry voxels = StandardVoxels(out Voxel[] v);
            BiomeRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize();
            Biome plains = _fixtures.CreateBiome("Plains", v[0], v[1], v[2]);
            registry.Register("std", plains);
            registry.Register("mod", plains);
            registry.FinalizeRegistry(voxels);

            Assert.IsTrue(registry.TryGetId("std:Plains", out ushort first));
            Assert.IsTrue(registry.TryGet(first, out Biome so));
            Assert.AreSame(plains, so);

            if (registry.TryGetId("mod:Plains", out ushort second)){
                Assert.IsTrue(registry.TryGet(second, out so), "the second name must resolve to the asset");
                Assert.AreSame(plains, so);
                Assert.Less(second, registry.BiomeArray.Length);
                Assert.AreEqual(voxels.GetIdOrThrow("std:Grass"), registry.BiomeArray[second].topBlock);
            }

            ushort air = voxels.GetIdOrThrow("std:air");
            for (int i = 0; i < registry.BiomeArray.Length; i++)
                Assert.AreNotEqual(air, registry.BiomeArray[i].topBlock, $"BiomeArray[{i}] is an air biome");
        }

        [Test]
        public void VoxelRegistry_GpuVoxelDefMatchesRegisteredCount(){
            VoxelRegistry registry = StandardVoxels(out Voxel[] voxels);
            Type gpuDef = typeof(VoxelRegistry).GetNestedType("GPUVoxelDef", BindingFlags.NonPublic);
            Assert.IsNotNull(gpuDef, "GPUVoxelDef must exist for the struct size guard (TST-E17)");
            Assert.AreEqual(voxels.Length + 1, registry.VoxelRenderDefBuffer.count);
        }

        #endregion
    }
}
