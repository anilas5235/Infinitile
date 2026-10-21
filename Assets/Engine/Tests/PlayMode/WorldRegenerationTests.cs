using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Render;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     <see cref="VoxelWorld.Regenerate" /> evicts every loaded chunk through the normal events (renderer buffers
    ///     and pool partitions are released), reloads around the focus with the new configuration, and going back to
    ///     the asset reproduces the original terrain.
    /// </summary>
    public class WorldRegenerationTests{
        private const float LoadTimeout = 60f;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Regenerate_EvictsEverything_ReloadsWithTheNewConfiguration_AndIsReversible(){
            VoxelWorld world = VoxelWorld.Instance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            Dictionary<int2, IntervalRange> buffers =
                PlayModeFixtures.GetPrivateField<Dictionary<int2, IntervalRange>>(renderer, "_voxelLists");
            ChunkPool pool = PlayModeFixtures.GetPrivateField<ChunkPool>(world, "_chunkPool");

            // The player must not fall while the colliders under it are gone; its controllers stay off until the
            // original terrain is back.
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            List<Behaviour> paused = new();
            foreach (MonoBehaviour behaviour in focus.GetComponentsInChildren<MonoBehaviour>()){
                if (!behaviour.enabled) continue;
                behaviour.enabled = false;
                paused.Add(behaviour);
            }

            try{
                int2 centre = world.Focus.Pos.xz;

                // A clean world first: earlier tests may have edited voxels.
                world.Regenerate();
                yield return WaitForChunk(world, buffers, centre);
                int[] original = SurfaceHeights(world, centre);

                WorldGenSettings variant = Object.Instantiate(world.Settings.WorldGen);
                variant.Architecture = variant.Architecture == GenArchitecture.BiomeFirst
                    ? GenArchitecture.TerrainFirst
                    : GenArchitecture.BiomeFirst;
                int evicted = 0;
                Action<int2> countEvictions = _ => evicted++;
                world.ChunkEvicted += countEvictions;
                try{
                    world.Regenerate(variant);
                }
                finally{
                    world.ChunkEvicted -= countEvictions;
                    Object.Destroy(variant); // the engine copied its values
                }

                Assert.Greater(evicted, 0, "regeneration must raise ChunkEvicted for the loaded chunks");
                Assert.IsFalse(world.ChunkManager.IsChunkLoaded(centre), "chunk data must be unloaded");
                Assert.AreEqual(0, buffers.Count, "the renderer must release every voxel buffer");
                for (int p = 0; p < PartitionsPerChunk; p++)
                    Assert.IsFalse(pool.IsPartitionActive(new int3(centre.x, p, centre.y)), "pool partitions must be reclaimed");

                yield return WaitForChunk(world, buffers, centre);
                int[] regenerated = SurfaceHeights(world, centre);
                Assert.AreNotEqual(original, regenerated, "the other architecture must change the terrain");

                world.Regenerate();
                yield return WaitForChunk(world, buffers, centre);
                Assert.AreEqual(original, SurfaceHeights(world, centre), "regenerating with the asset must reproduce it");

                // Courtesy for later tests: release the player once the ground under it is collidable again (or
                // after the timeout; a water surface has no collider of its own).
                int3 under = focus.position.Int3();
                under.y = original[(under.z - centre.y * ChunkDepth) * ChunkWidth + under.x - centre.x * ChunkWidth];
                float deadline = Time.realtimeSinceStartup + LoadTimeout;
                while (WorldToChunkPos(under).Equals(centre) && !world.IsCollidable(WorldToPartitionPos(under))){
                    if (Time.realtimeSinceStartup > deadline) break;
                    yield return null;
                }
            }
            finally{
                foreach (Behaviour behaviour in paused) behaviour.enabled = true;
            }
        }

        private static IEnumerator WaitForChunk(VoxelWorld world, Dictionary<int2, IntervalRange> buffers, int2 chunk){
            float deadline = Time.realtimeSinceStartup + LoadTimeout;
            while (!world.ChunkManager.IsChunkLoaded(chunk) || !buffers.ContainsKey(chunk)){
                if (Time.realtimeSinceStartup > deadline) Assert.Fail($"chunk {chunk} was not regenerated in time");
                yield return null;
            }
        }

        /// <summary>Top non-air voxel per column of <paramref name="chunk" />, row-major over (z, x).</summary>
        private static int[] SurfaceHeights(VoxelWorld world, int2 chunk){
            int[] heights = new int[ChunkWidth * ChunkDepth];
            for (int z = 0; z < ChunkDepth; z++)
            for (int x = 0; x < ChunkWidth; x++){
                int3 column = new(chunk.x * ChunkWidth + x, 0, chunk.y * ChunkDepth + z);
                int y = ChunkHeight - 1;
                while (y > 0 && world.GetVoxel(new int3(column.x, y, column.z)) == PlayModeFixtures.Air) y--;
                heights[z * ChunkWidth + x] = y;
            }

            return heights;
        }
    }
}
