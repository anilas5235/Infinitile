using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.Scripts.Render;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     F-17 (TST-E20 addition): a point builder may be disposed while its GPU read-back is still pending,
    ///     e.g. when the renderer is destroyed mid-build. Needs PlayMode for the compute dispatch.
    /// </summary>
    public class RendererShutdownTests{
        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        /// <summary>Chunk voxel buffer in the renderer's interval format: [count, lastEnd] then (value, end) nodes.</summary>
        private static GraphicsBuffer CreateUniformChunkBuffer(ushort voxelId){
            uint2[] intervals = { new(1u, (uint)VoxelsPerChunk), new(voxelId, (uint)VoxelsPerChunk) };
            GraphicsBuffer buffer = new(GraphicsBuffer.Target.Structured, intervals.Length, Marshal.SizeOf<uint2>());
            buffer.SetData(intervals);
            return buffer;
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator PointBuilderHandler_DisposedWhileReadBackPending_DoesNotThrowOrLog(){
            // F-17: Dispose released the read-back array while an AsyncGPUReadback into it was still pending,
            // which throws from Dispose and leaks every resource released after it.
            VoxelRegistry registry = DataImporter.Instance.VoxelRegistry;
            ComputeShader pointBuilder = VoxelWorld.Instance.Settings.Renderer.pointBuilder;
            Dictionary<int2, GraphicsBuffer> chunkBuffers = new();
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                chunkBuffers[new int2(x, z)] =
                    CreateUniformChunkBuffer(x == 0 && z == 0 ? PlayModeFixtures.Grass : PlayModeFixtures.Air);

            // Only errors matter here; the first compute dispatch of a session may log a shader compile warning.
            int errors = 0;
            Application.LogCallback onLog = (condition, _, type) => {
                if (type is LogType.Error or LogType.Exception or LogType.Assert) errors++;
            };
            Application.logMessageReceived += onLog;

            PointBuilderHandler handler = new(pointBuilder, registry.VoxelRenderDefBuffer, registry.QuadTexPairBuffer);
            try{
                PartitionBuildRequest request = new(new int3(0, 0, 0));
                request.CollectBuffers(chunkBuffers);
                Assert.IsTrue(request.IsValid);

                Awaitable<int[]>.Awaiter build = handler.BuildPoints(request).GetAwaiter();
                Assert.IsFalse(build.IsCompleted, "the read-back should still be pending right after the dispatch");

                PointBuilderHandler disposing = handler;
                handler = null;
                Assert.DoesNotThrow(() => disposing.Dispose());

                float deadline = Time.realtimeSinceStartup + 10f;
                while (!build.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(build.IsCompleted, "the build must still finish after the handler was disposed");
                int[] counts = null;
                Assert.DoesNotThrow(() => counts = build.GetResult());
                Assert.IsNotNull(counts);
                yield return null;
                Assert.AreEqual(0, errors, "no error may be logged while the pending read-back finishes");
            }
            finally{
                Application.logMessageReceived -= onLog;
                handler?.Dispose();
                foreach (GraphicsBuffer buffer in chunkBuffers.Values) buffer.Dispose();
            }
        }
    }
}
