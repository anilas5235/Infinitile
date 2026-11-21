using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Render.Post;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The simple liquid rendering (DEC-119): column biomes kept with the chunk, the underwater fog colour from
    ///     the biomes and the assets it needs. The biome tint map is in <see cref="BiomeTintTests" />.
    /// </summary>
    public class WaterRenderingTests{
        private ChunkVoxelData _data;

        [TearDown]
        public void TearDown(){
            if (_data.Biomes.IsCreated) _data.Biomes.Dispose();
            _data = default;
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

        [Test]
        public void FogColor_AveragesTheLoadedColumnsInLinearLight(){
            // biome 0 left of x = 0, biome 1 from x = 0; column (2, *) not loaded
            bool BiomeAt(int x, int z, out int biome){
                biome = x < 0 ? 0 : 1;
                return x != 2;
            }

            Color[] colors ={ Color.black, Color.white };
            Color fallback = Color.magenta;

            Color mixed = VoxelPostProcessHandler.AverageBiomeColor(0, 0, BiomeAt, i => colors[i], fallback);
            // sampled columns x = -6, -2, 2, 6: two black, one white (x = 2 is not loaded)
            Assert.AreEqual(Mathf.LinearToGammaSpace(1f / 3f), mixed.r, 1e-4f);
            Assert.AreEqual(1f, mixed.a);

            Color inside = VoxelPostProcessHandler.AverageBiomeColor(-20, 0, BiomeAt, i => colors[i], fallback);
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
            Assert.Greater(water.postProcess.sceneTint, 0f, "the scene under water takes on the fog's blue");
        }

        [Test]
        public void BiomeWaterColors_StayCloseToOneBlue(){
            // Owner 2026-11-20: subtle shifts around one blue, only the Swamp's water is murky.
            string[] guids = AssetDatabase.FindAssets("t:Biome",
                new[]{ "Assets/Engine/Resources/VoxelDataPackages/Standard/Bioms" });
            Assert.Greater(guids.Length, 5);
            foreach (string guid in guids){
                Biome biome = AssetDatabase.LoadAssetAtPath<Biome>(AssetDatabase.GUIDToAssetPath(guid));
                if (biome.name == "Swamp") continue;
                AssertClose(Biome.DefaultWaterColor, biome.WaterColor, 0.08f, biome.name + " water");
                AssertClose(Biome.DefaultWaterFogColor, biome.WaterFogColor, 0.13f, biome.name + " fog");
            }
        }

        private static void AssertClose(Color expected, Color actual, float delta, string what){
            Assert.AreEqual(expected.r, actual.r, delta, what + " red");
            Assert.AreEqual(expected.g, actual.g, delta, what + " green");
            Assert.AreEqual(expected.b, actual.b, delta, what + " blue");
        }
    }
}
