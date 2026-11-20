using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Render;
using Engine.Scripts.Render.Post;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The simple liquid rendering (DEC-119): column biomes kept with the chunk, the world tint map, the
    ///     underwater fog colour from the biomes and the assets it needs.
    /// </summary>
    public class WaterRenderingTests{
        private readonly List<WaterTintMap> _maps = new();
        private ChunkVoxelData _data;

        [TearDown]
        public void TearDown(){
            foreach (WaterTintMap map in _maps) map.Dispose();
            _maps.Clear();
            if (_data.Biomes.IsCreated) _data.Biomes.Dispose();
            _data = default;
        }

        private WaterTintMap Map(int drawDistance){
            WaterTintMap map = new(drawDistance, new Color32(10, 20, 30, 255));
            _maps.Add(map);
            return map;
        }

        /// <summary>Column biome = (x + 2z) % 3, through the chunk job's own writer.</summary>
        private void WriteTestBiomes(){
            NativeArray<ChunkColumn> columns = new(ChunkWidth * ChunkDepth, Allocator.Temp);
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
                columns[z + x * ChunkDepth] = new ChunkColumn{ Biome = (ushort)((x + 2 * z) % 3) };
            ChunkJob.WriteBiomes(columns, ref _data);
            columns.Dispose();
        }

        [Test]
        public void ChunkJob_KeepsTheColumnBiomes(){
            Assert.IsFalse(_data.HasBiomes);
            Assert.AreEqual(0, _data.GetBiome(3, 4), "no biomes read as biome 0");

            WriteTestBiomes();

            Assert.IsTrue(_data.HasBiomes);
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
                Assert.AreEqual((x + 2 * z) % 3, _data.GetBiome(x, z), $"column ({x}, {z})");
        }

        [Test]
        public void ChunkJob_ClampsBiomesToAByte(){
            NativeArray<ChunkColumn> columns = new(ChunkWidth * ChunkDepth, Allocator.Temp);
            columns[0] = new ChunkColumn{ Biome = 300 };
            ChunkJob.WriteBiomes(columns, ref _data);
            columns.Dispose();
            Assert.AreEqual(255, _data.GetBiome(0, 0));
        }

        [TestCase(1, 6)]
        [TestCase(12, 28)]
        [TestCase(32, 68)]
        public void TintMap_HoldsTheDrawnSquareWithSlack(int drawDistance, int chunks){
            Assert.AreEqual(chunks, WaterTintMap.SizeChunksAt(drawDistance));
            Assert.Greater(chunks, 2 * drawDistance + 1, "two drawn chunks never share a slot");
        }

        [TestCase(0, 0)]
        [TestCase(27, 27)]
        [TestCase(28, 0)]
        [TestCase(-1, 27)]
        [TestCase(-28, 0)]
        [TestCase(-29, 27)]
        public void TintMap_SlotWrapsNegativeChunks(int chunk, int slot){
            Assert.AreEqual(slot, WaterTintMap.Slot(chunk, 28));
        }

        [Test]
        public void TintMap_FillPixels_MapsColumnsToTexelsAndFallsBack(){
            WriteTestBiomes();
            Color32[] colors ={ new(255, 0, 0, 255), new(0, 255, 0, 255) }; // biome 2 has no colour
            Color32 fallback = new(1, 2, 3, 255);
            Color32[] pixels = new Color32[ChunkWidth * ChunkDepth];

            WaterTintMap.FillPixels(_data, colors, fallback, pixels);

            Assert.AreEqual(colors[0], pixels[0 + 0 * ChunkWidth], "(0, 0) is biome 0");
            Assert.AreEqual(colors[1], pixels[1 + 0 * ChunkWidth], "(1, 0) is biome 1: x runs along a row");
            Assert.AreEqual(fallback, pixels[0 + 1 * ChunkWidth], "(0, 1) is biome 2, without a colour");
            Assert.AreEqual(colors[1], pixels[0 + 2 * ChunkWidth], "(0, 2) is biome 1: z picks the row");

            ChunkVoxelData none = default;
            WaterTintMap.FillPixels(none, colors, fallback, pixels);
            Assert.That(pixels, Is.All.EqualTo(fallback), "a chunk without biomes is all fallback");
        }

        [Test]
        public void TintMap_Ensure_WritesOnceAndAgainAfterForgetOrNewColours(){
            WriteTestBiomes();
            WaterTintMap map = Map(1); // 6 chunks
            Color32[] colors ={ new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255) };
            int2 chunk = new(-2, 5);

            Assert.IsTrue(map.Ensure(chunk, _data, colors, 1), "first write");
            Assert.IsTrue(map.Owns(chunk));
            Assert.IsFalse(map.Ensure(chunk, _data, colors, 1), "held already");

            int2 sameSlot = chunk + new int2(6, -6);
            Assert.AreEqual(map.SlotIndex(chunk), map.SlotIndex(sameSlot));
            map.Forget(sameSlot);
            Assert.IsTrue(map.Owns(chunk), "forgetting another chunk on the slot keeps the owner");
            Assert.IsTrue(map.Ensure(sameSlot, _data, colors, 1), "the other chunk takes the slot");
            Assert.IsFalse(map.Owns(chunk));

            map.Forget(sameSlot);
            Assert.IsFalse(map.Owns(sameSlot));
            Assert.IsTrue(map.Ensure(sameSlot, _data, colors, 1), "written again after eviction");
            Assert.IsTrue(map.Ensure(sameSlot, _data, colors, 2), "and again for new biome colours");
        }

        [Test]
        public void TintMap_WritesTheChunkIntoItsSlot(){
            WriteTestBiomes();
            WaterTintMap map = Map(1); // 6 chunks, 192 texels
            Color32[] colors ={ new(200, 0, 0, 255), new(0, 200, 0, 255), new(0, 0, 200, 255) };
            int2 chunk = new(-1, 2); // slot (5, 2)
            map.Ensure(chunk, _data, colors, 1);

            Color32[] texels = ReadBack(map.Texture);
            int size = map.SizeTexels;
            Assert.AreEqual(6 * ChunkWidth, size);
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                Color32 texel = texels[5 * ChunkWidth + x + (2 * ChunkDepth + z) * size];
                Color32 expected = colors[(x + 2 * z) % 3];
                Assert.AreEqual(expected.r, texel.r, 1, $"column ({x}, {z})");
                Assert.AreEqual(expected.g, texel.g, 1, $"column ({x}, {z})");
                Assert.AreEqual(expected.b, texel.b, 1, $"column ({x}, {z})");
            }

            Color32 untouched = texels[0];
            Assert.AreEqual(10, untouched.r, 2, "the rest keeps the default colour");
            Assert.AreEqual(20, untouched.g, 2);
            Assert.AreEqual(30, untouched.b, 2);
        }

        private static Color32[] ReadBack(RenderTexture texture){
            RenderTexture previous = RenderTexture.active;
            Texture2D read = new(texture.width, texture.height, TextureFormat.RGBA32, false, false);
            try{
                RenderTexture.active = texture;
                read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                read.Apply(false);
                return read.GetPixels32();
            }
            finally{
                RenderTexture.active = previous;
                Object.DestroyImmediate(read);
            }
        }

        [Test]
        public void FogColor_AveragesTheLoadedColumnsInLinearLight(){
            // biome 0 left of x = 0, biome 1 from x = 0; column (5, *) not loaded
            bool BiomeAt(int x, int z, out int biome){
                biome = x < 0 ? 0 : 1;
                return x != 1;
            }

            Color[] colors ={ Color.black, Color.white };
            Color fallback = Color.magenta;

            Color mixed = VoxelPostProcessHandler.AverageBiomeColor(0, 0, BiomeAt, i => colors[i], fallback);
            // columns x = -2..1: two black, one white (x = 1 is not loaded)
            Assert.AreEqual(Mathf.LinearToGammaSpace(1f / 3f), mixed.r, 1e-4f);
            Assert.AreEqual(1f, mixed.a);

            Color inside = VoxelPostProcessHandler.AverageBiomeColor(-10, 0, BiomeAt, i => colors[i], fallback);
            Assert.AreEqual(Color.black, inside);

            bool NoneLoaded(int x, int z, out int biome){
                biome = 0;
                return false;
            }

            Assert.AreEqual(fallback,
                VoxelPostProcessHandler.AverageBiomeColor(0, 0, NoneLoaded, i => colors[i], fallback));
        }

        [Test]
        public void PcPipeline_HasNoOpaqueCopyButADepthTexture(){
            UniversalRenderPipelineAsset asset =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            Assert.IsNotNull(asset);
            Assert.IsFalse(asset.supportsCameraOpaqueTexture, "no shader reads the opaque colour copy since DEC-119");
            Assert.IsTrue(asset.supportsCameraDepthTexture, "the liquid's depth tint reads the scene depth");
        }

        [Test]
        public void WaterAsset_IsABiomeTintedLiquid(){
            Voxel water = AssetDatabase.LoadAssetAtPath<Voxel>(
                "Assets/Engine/Resources/VoxelDataPackages/Standard/Transparent/Water.asset");
            Assert.IsNotNull(water);
            Assert.AreEqual(VoxelCategory.Liquids, water.category);
            Assert.IsTrue(water.liquidSurface.biomeTint);
            Assert.Greater(water.depthFadeDistance, 0f, "the depth tint is on");
            Assert.GreaterOrEqual(water.liquidSurface.depthAlpha, water.surfaceAlpha);
        }
    }
}
