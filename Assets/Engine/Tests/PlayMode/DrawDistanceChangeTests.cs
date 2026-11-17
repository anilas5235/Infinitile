using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Render;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     <see cref="VoxelWorld.ChangeDrawDistance" /> builds the running world again at another draw distance: the
    ///     pools, the renderer's interval store and build slots follow the new distance, the world around the focus is
    ///     built again, the settings asset keeps its value, and going back gives the original sizes.
    /// </summary>
    public class DrawDistanceChangeTests{
        private const float LoadTimeout = 120f;
        private const int Smaller = 4;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ChangeDrawDistance_RebuildsTheWorldAtTheNewSize_AndBack(){
            VoxelWorld world = VoxelWorld.Instance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            int original = world.DrawDistance;
            Assert.AreEqual(PlayModeFixtures.BaselineDrawDistance, original);
            Assert.AreNotEqual(Smaller, original);

            // The player must not fall while the colliders under it are gone. The focus is the player's camera, so
            // the scripts of the whole player object are paused, not only the camera's.
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            Transform player = focus.root;
            Vector3 stood = player.position;
            List<Behaviour> paused = new();
            foreach (MonoBehaviour behaviour in player.GetComponentsInChildren<MonoBehaviour>()){
                if (!behaviour.enabled) continue;
                behaviour.enabled = false;
                paused.Add(behaviour);
            }

            int changedTo = 0;
            Action<int> onChanged = distance => changedTo = distance;
            world.DrawDistanceChanged += onChanged;
            try{
                yield return WaitUntilBuilt(world, renderer);
                GpuMemory before = renderer.GpuMemory;
                Assert.Greater(before.IntervalStoreBytes, 0L);
                Assert.AreEqual(world.Settings.Scheduler.BuildSlotsAt(original), before.BuildSlots);

                Assert.IsTrue(world.ChangeDrawDistance(Smaller), "a new distance rebuilds the world");
                Assert.AreEqual(Smaller, changedTo, "DrawDistanceChanged with the new distance");
                Assert.AreEqual(Smaller, world.DrawDistance);
                Assert.AreEqual(original, world.Settings.Chunk.DrawDistance, "the settings asset keeps its value");
                Assert.AreEqual(Smaller + 3, world.Settings.Chunk.LoadDistance, "the load distance follows");

                GpuMemory smaller = renderer.GpuMemory;
                Assert.Less(smaller.IntervalStoreBytes, before.IntervalStoreBytes, "the interval store is sized anew");
                Assert.AreEqual(0, smaller.IntervalStoreUsedPages, "no chunk is uploaded yet");
                Assert.AreEqual(world.Settings.Scheduler.BuildSlotsAt(Smaller), smaller.BuildSlots);

                yield return WaitUntilBuilt(world, renderer);
                Assert.Greater(renderer.GpuMemory.IntervalStoreUsedPages, 0, "the world around the focus is uploaded");

                Assert.IsFalse(world.ChangeDrawDistance(Smaller), "the same distance changes nothing");

                Assert.IsTrue(world.ChangeDrawDistance(original));
                Assert.AreEqual(original, world.DrawDistance);
                Assert.AreEqual(before.IntervalStoreBytes, renderer.GpuMemory.IntervalStoreBytes,
                    "back at the original distance, the original store size");
                Assert.AreEqual(before.BuildSlots, renderer.GpuMemory.BuildSlots);
                yield return WaitUntilBuilt(world, renderer);
                Assert.AreEqual(stood.y, player.position.y, 0.01f, "the player stays where it stood while the world is rebuilt");

                Assert.Throws<ArgumentOutOfRangeException>(() => world.ChangeDrawDistance(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => world.ChangeDrawDistance(VoxelWorld.MaxDrawDistance + 1));
            }
            finally{
                world.DrawDistanceChanged -= onChanged;
                if (world && world.DrawDistance != original) world.ChangeDrawDistance(original);
                VoxelWorld.RequestedDrawDistance = 0;
                foreach (Behaviour behaviour in paused)
                    if (behaviour)
                        behaviour.enabled = true;
            }
        }

        /// <summary>Waits until the chunk under the focus is loaded and uploaded and the scheduler sleeps.</summary>
        private static IEnumerator WaitUntilBuilt(VoxelWorld world, VoxelWorldRenderer renderer){
            Dictionary<int2, IntervalRange> lists =
                PlayModeFixtures.GetPrivateField<Dictionary<int2, IntervalRange>>(renderer, "_voxelLists");
            int2 centre = world.Focus.Pos.xz;
            float deadline = Time.realtimeSinceStartup + LoadTimeout;
            yield return null; // the first scheduler update after a rebuild wakes the stages
            while (!world.ChunkManager.IsChunkLoaded(centre) || !lists.ContainsKey(centre) ||
                   !PlayModeFixtures.SchedulerSleeping()){
                if (Time.realtimeSinceStartup > deadline) Assert.Fail($"the world around {centre} was not built in time");
                yield return null;
            }
        }
    }
}
