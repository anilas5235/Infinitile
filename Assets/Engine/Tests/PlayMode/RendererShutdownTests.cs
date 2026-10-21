using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Render;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     Renderer shutdown: a point builder may be disposed while its GPU read-back is still pending, e.g. when
    ///     the renderer is destroyed mid-build, and a rejected chunk upload must keep the chunk's existing interval
    ///     list. Needs PlayMode for the compute dispatch and the live renderer.
    /// </summary>
    public class RendererShutdownTests{
        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        /// <summary>A chunk-wide list of <paramref name="value" /> in <paramref name="store" />.</summary>
        private static IntervalRange UploadUniformList(ChunkIntervalStore store, ushort value){
            UnsafeIntervalList<ushort> list = new(1, Allocator.Temp);
            list.AddInterval(value, VoxelsPerChunk);
            IntervalRange range = default;
            store.Upload(ref range, list);
            list.Dispose();
            return range;
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator PointBuilderHandler_DisposedWhileReadBackPending_DoesNotThrowOrLog(){
            // Releasing the read-back array while an AsyncGPUReadback into it is still pending would throw from
            // Dispose and leak every resource released after it.
            VoxelRegistry registry = DataImporter.Instance.VoxelRegistry;
            ComputeShader pointBuilder = VoxelWorld.Instance.Settings.Renderer.pointBuilder;
            ChunkIntervalStore store = new(18, 18, pointBuilder);
            Dictionary<int2, IntervalRange> voxelLists = new();
            Dictionary<int2, IntervalRange> lightLists = new(); // same interval format, all dark
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++){
                voxelLists[new int2(x, z)] =
                    UploadUniformList(store, x == 0 && z == 0 ? PlayModeFixtures.Grass : PlayModeFixtures.Air);
                lightLists[new int2(x, z)] = UploadUniformList(store, 0);
            }

            // Only errors matter here; the first compute dispatch of a session may log a shader compile warning.
            int errors = 0;
            Application.LogCallback onLog = (condition, _, type) => {
                if (type is LogType.Error or LogType.Exception or LogType.Assert) errors++;
            };
            Application.logMessageReceived += onLog;

            PointBuilderHandler handler = new(pointBuilder, registry.VoxelRenderDefBuffer, registry.QuadTexPairBuffer);
            try{
                PartitionBuildRequest request = new(new int3(0, 0, 0));
                request.CollectLists(voxelLists, lightLists);
                Assert.IsTrue(request.IsValid);

                handler.BuildPoints(request, store);
                Assert.IsFalse(handler.IsReadBackDone, "the read-back should still be pending right after the dispatch");

                PointBuilderHandler disposing = handler;
                handler = null;
                Assert.DoesNotThrow(() => disposing.Dispose());

                float deadline = Time.realtimeSinceStartup + 10f;
                while (!disposing.IsReadBackDone && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(disposing.IsReadBackDone, "the build must still finish after the handler was disposed");
                int[] counts = null;
                bool built = true;
                Assert.DoesNotThrow(() => built = disposing.TryCompleteBuild(out counts));
                Assert.IsFalse(built, "a build of a released handler says nothing about the partition");
                Assert.IsNotNull(counts);
                yield return null;
                Assert.AreEqual(0, errors, "no error may be logged while the pending read-back finishes");
            }
            finally{
                Application.logMessageReceived -= onLog;
                handler?.Dispose();
                store.Dispose();
            }
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator AddOrUpdateChunk_RejectedLength_KeepsTheExistingList(){
            // A rejected update must leave the chunk's list unchanged, not a disposed buffer the next partition
            // build would bind.
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            Dictionary<int2, IntervalRange> buffers =
                PlayModeFixtures.GetPrivateField<Dictionary<int2, IntervalRange>>(renderer, "_voxelLists");
            float deadline = Time.realtimeSinceStartup + 30f;
            while (buffers.Count == 0){
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("no chunk was uploaded to the renderer");
                yield return null;
            }

            int2 chunk = default;
            IntervalRange before = default;
            foreach (KeyValuePair<int2, IntervalRange> pair in buffers){
                chunk = pair.Key;
                before = pair.Value;
                break;
            }

            MethodInfo addOrUpdate = PlayModeFixtures.GetPrivateMethod(typeof(VoxelWorldRenderer), "AddOrUpdateChunk");
            UnsafeIntervalList<ushort> tooShort = new(1, Allocator.Temp);
            tooShort.AddInterval(PlayModeFixtures.Air, 5);
            try{
                TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(
                    () => addOrUpdate.Invoke(renderer, new object[]{ chunk, tooShort }));
                Assert.IsNotNull(thrown.InnerException);
            }
            finally{
                tooShort.Dispose();
            }

            Assert.IsTrue(buffers.TryGetValue(chunk, out IntervalRange after), "the chunk must keep a list");
            Assert.IsTrue(after.IsCreated, "the kept list must not be released");
            Assert.AreEqual(before, after, "the existing list must be kept unchanged");
        }
    }
}
