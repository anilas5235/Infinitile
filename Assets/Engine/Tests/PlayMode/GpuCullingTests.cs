using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Render;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;
using FrustumPlanes = Engine.Scripts.Render.FrustumPlanes;
using RenderBuffer = Engine.Scripts.Render.RenderBuffer;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     CUL-08 (Phase C): the GPU visible-page set of <c>PageCulling.compute</c> read back for random camera poses
    ///     equals the C# reference <see cref="FrustumCulling" /> on the live render buffers; the "all pages visible"
    ///     fallback, the shadow list (CUL-05), per-camera state life-cycle (CUL-04), freeze (CUL-06), the allocation
    ///     rule for the draw path (PERF-04) and a culling on/off frame-time comparison at a fixed viewpoint.
    /// </summary>
    public class GpuCullingTests{
        private const int RandomPoses = 50;
        private const int ShadowPoses = 10;
        private const uint Seed = 0x5EEDu;

        /// <summary>
        ///     Pages whose extreme corner lies within this distance of a plane may be classified differently by the
        ///     GPU (fp32 with different operation order) and are not counted as mismatches.
        /// </summary>
        private const float BorderlineMargin = 0.01f;

        private const int MinPagesForComparison = 256;
        private const int MinPagesForTiming = 512;
        private const int FrameSamples = 60;
        private const int WarmupFrames = 10;

        private GameObject _cameraObject;
        private Camera _camera;
        private readonly Plane[] _planeScratch = new Plane[FrustumPlanes.Count];
        private readonly uint[] _args = new uint[CullList.ArgsCount];
        private readonly uint[] _pages = new uint[PagesPerBuffer];
        private readonly HashSet<uint> _gpuVisible = new();

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
            _cameraObject = new GameObject("GpuCullingTests camera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false; // only a source of frustum planes; never renders
            _camera.fieldOfView = 70f;
            _camera.aspect = 16f / 9f;
            _camera.nearClipPlane = 0.3f;
            _camera.farClipPlane = 400f;
        }

        [TearDown]
        public void TearDown(){
            if (_cameraObject != null) Object.Destroy(_cameraObject);
        }

        #region helpers

        private static VoxelWorldRenderer Renderer(){
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            return renderer;
        }

        private static GpuCuller Culler(VoxelWorldRenderer renderer){
            return PlayModeFixtures.GetPrivateField<GpuCuller>(renderer, "_culler");
        }

        private static RenderBufferManager[] Layers(VoxelWorldRenderer renderer){
            return PlayModeFixtures.GetPrivateField<RenderBufferManager[]>(renderer, "_layers");
        }

        private static Camera GameCamera(){
            Camera cam = Camera.main;
            if (cam != null) return cam;
            foreach (Camera c in Camera.allCameras)
                if (c.cameraType == CameraType.Game) return c;
            Assert.Fail("no game camera in the scene");
            return null;
        }

        private static int SolidPages(VoxelWorldRenderer renderer){
            int pages = 0;
            foreach (RenderBuffer buffer in Layers(renderer)[0].Buffers) pages += buffer.Pages.AllocatedPages;
            return pages;
        }

        /// <summary>Waits until the solid layer holds enough pages for a meaningful comparison.</summary>
        private static IEnumerator WaitForPages(VoxelWorldRenderer renderer, int minPages, float timeoutSeconds){
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (SolidPages(renderer) < minPages){
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"only {SolidPages(renderer)} solid pages allocated after {timeoutSeconds}s");
                yield return null;
            }
        }

        private FrustumPlanes PlanesFor(float3 position, quaternion rotation){
            _camera.transform.SetPositionAndRotation(position, rotation);
            return FrustumPlanes.FromCamera(_camera, _planeScratch);
        }

        /// <summary>Random pose around the focus: anywhere in a 192 m cube, any orientation.</summary>
        private static (float3 position, quaternion rotation) RandomPose(ref Random random, float3 centre){
            float3 position = centre + random.NextFloat3(new float3(-96f, -32f, -96f), new float3(96f, 96f, 96f));
            return (position, random.NextQuaternionRotation());
        }

        /// <summary>Runs the kernel for one buffer and reads back the visible page set and the args.</summary>
        private uint CullAndReadBack(GpuCuller culler, RenderBuffer buffer, CullList list, in FrustumPlanes planes,
            float3 extrusion, bool cullingEnabled){
            culler.Cull(buffer, list, planes, extrusion, cullingEnabled);
            list.Args.GetData(_args);
            uint count = _args[CullList.VisibleCountIndex];
            Assert.LessOrEqual(count, (uint)PagesPerBuffer, "visible count exceeds the page count");
            Assert.AreEqual(count * PointsPerPage * 6u, _args[0], "args[0] must be visible pages x points x 6");
            Assert.AreEqual(1u, _args[1], "instance count");

            _gpuVisible.Clear();
            if (count > 0){
                list.VisiblePages.GetData(_pages, 0, 0, (int)count);
                for (int i = 0; i < count; i++) _gpuVisible.Add(_pages[i]);
            }

            Assert.AreEqual(count, (uint)_gpuVisible.Count, "the visible list contains duplicate pages");
            return count;
        }

        /// <summary>Signed distance of the positive vertex to the nearest plane; near zero means borderline.</summary>
        private static float Margin(in PageBounds bounds, in FrustumPlanes planes){
            float margin = float.MaxValue;
            for (int i = 0; i < FrustumPlanes.Count; i++){
                float4 plane = planes[i];
                float3 positive = math.select(bounds.Min, bounds.Max, plane.xyz > 0f);
                margin = math.min(margin, math.dot(plane.xyz, positive) + plane.w);
            }

            return margin;
        }

        /// <summary>
        ///     Compares the GPU set with the C# reference for every page of the buffer; returns the number of
        ///     reference-visible pages. Borderline pages are reported but tolerated.
        /// </summary>
        private int CompareWithReference(RenderBuffer buffer, in FrustumPlanes planes, float3 extrusion,
            string context){
            int referenceVisible = 0;
            for (int page = 0; page < PagesPerBuffer; page++){
                bool hasPoints = buffer.Pages.CountsPerPage[page] > 0;
                PageBounds bounds = buffer.Pages.BoundsPerPage[page].Extrude(extrusion);
                bool expected = hasPoints && FrustumCulling.IsVisible(bounds, planes);
                if (expected) referenceVisible++;
                bool actual = _gpuVisible.Contains((uint)page);
                if (expected == actual) continue;

                float margin = Margin(bounds, planes);
                Assert.Less(math.abs(margin), BorderlineMargin,
                    $"{context}: page {page} {bounds} reference={expected} gpu={actual} (margin {margin})");
            }

            return referenceVisible;
        }

        #endregion

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator GpuVisibleSet_MatchesCSharpReference_For50RandomPoses(){
            // CUL-08: for every buffer that draws, 50 random camera poses; the GPU set must equal the reference
            VoxelWorldRenderer renderer = Renderer();
            GpuCuller culler = Culler(renderer);
            yield return WaitForPages(renderer, MinPagesForComparison, 90f);

            Random random = new(Seed);
            float3 centre = GameCamera().transform.position;
            int comparedBuffers = 0;
            int nonTrivialPoses = 0;
            foreach (RenderBufferManager layer in Layers(renderer)){
                layer.RebuildBuffers(); // the GPU copy of counts and bounds must equal the CPU state
                foreach (RenderBuffer buffer in layer.Buffers){
                    if (!buffer.ShouldDraw) continue;
                    comparedBuffers++;
                    CullList list = new(buffer);
                    try{
                        for (int pose = 0; pose < RandomPoses; pose++){
                            (float3 position, quaternion rotation) = RandomPose(ref random, centre);
                            FrustumPlanes planes = PlanesFor(position, rotation);
                            uint gpuCount = CullAndReadBack(culler, buffer, list, planes, float3.zero, true);
                            int expected = CompareWithReference(buffer, planes, float3.zero,
                                $"{layer.LayerName}[{buffer.BufferIndex}] pose {pose}");
                            if (gpuCount > 0 && gpuCount < buffer.Pages.AllocatedPages) nonTrivialPoses++;
                            Assert.LessOrEqual(math.abs((int)gpuCount - expected), PagesPerBuffer / 64,
                                "GPU and reference counts differ by more than the borderline allowance");
                        }
                    }
                    finally{
                        list.Dispose();
                    }
                }
            }

            Assert.Greater(comparedBuffers, 0, "no render buffer had pages to cull");
            Assert.Greater(nonTrivialPoses, 0, "no pose produced a partially visible set");
            Debug.Log($"compared {comparedBuffers} buffers x {RandomPoses} poses; {nonTrivialPoses} poses partially visible");
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator ShadowList_MatchesExtrudedReference(){
            // CUL-05: the shadow list tests the bounds extruded along the light; compared with IsVisibleExtruded
            VoxelWorldRenderer renderer = Renderer();
            GpuCuller culler = Culler(renderer);
            yield return WaitForPages(renderer, MinPagesForComparison, 90f);

            Random random = new(Seed + 1);
            float3 centre = GameCamera().transform.position;
            RenderBufferManager solid = Layers(renderer)[0];
            solid.RebuildBuffers();
            RenderBuffer buffer = solid.Buffers[0];
            CullList list = new(buffer);
            int grewByExtrusion = 0;
            try{
                for (int pose = 0; pose < ShadowPoses; pose++){
                    (float3 position, quaternion rotation) = RandomPose(ref random, centre);
                    FrustumPlanes planes = PlanesFor(position, rotation);
                    float3 extrusion = math.normalize(random.NextFloat3Direction()) * 50f;

                    uint mainCount = CullAndReadBack(culler, buffer, list, planes, float3.zero, true);
                    uint shadowCount = CullAndReadBack(culler, buffer, list, planes, extrusion, true);
                    CompareWithReference(buffer, planes, extrusion, $"shadow pose {pose}");
                    Assert.GreaterOrEqual(shadowCount, mainCount, "the extruded set must contain the main set");
                    if (shadowCount > mainCount) grewByExtrusion++;
                }
            }
            finally{
                list.Dispose();
            }

            Debug.Log($"{grewByExtrusion}/{ShadowPoses} shadow poses added pages beyond the main list");
        }

        [UnityTest]
        public IEnumerator CullingDisabled_AppendsEveryPageWithPoints(){
            // CUL-06: the fallback draws all valid pages, so the overlay and the image match the pre-Phase-C path
            VoxelWorldRenderer renderer = Renderer();
            GpuCuller culler = Culler(renderer);
            yield return WaitForPages(renderer, 1, 90f);

            RenderBufferManager solid = Layers(renderer)[0];
            solid.RebuildBuffers();
            RenderBuffer buffer = solid.Buffers[0];
            FrustumPlanes planes = PlanesFor(new float3(0f, 10000f, 0f), quaternion.identity); // sees nothing
            CullList list = new(buffer);
            try{
                uint culled = CullAndReadBack(culler, buffer, list, planes, float3.zero, true);
                uint all = CullAndReadBack(culler, buffer, list, planes, float3.zero, false);
                Assert.AreEqual(0u, culled, "a camera far above the world should see no page");
                Assert.AreEqual((uint)buffer.Pages.AllocatedPages, all, "disabled culling must list every allocated page");
                for (int page = 0; page < PagesPerBuffer; page++)
                    Assert.AreEqual(buffer.Pages.CountsPerPage[page] > 0, _gpuVisible.Contains((uint)page), $"page {page}");
            }
            finally{
                list.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator CameraState_IsCreatedOnFirstSight_AndReleasedWhenDestroyedOrIdle(){
            // CUL-04: per-camera buffers appear on the first BeginCamera and go away with the camera or after
            // MaxIdleFrames frames without a draw
            VoxelWorldRenderer renderer = Renderer();
            ComputeShader shader = PlayModeFixtures.GetPrivateField<ComputeShader>(Culler(renderer), "_shader");
            using GpuCuller culler = new(shader);
            GameObject temporary = new("GpuCullingTests temporary camera");
            Camera cam = temporary.AddComponent<Camera>();
            cam.enabled = false;

            CameraCullState state = culler.BeginCamera(cam, false, 100);
            Assert.IsTrue(state.HasPlanes);
            Assert.AreEqual(1, culler.CameraCount);
            Assert.AreSame(state, culler.BeginCamera(cam, false, 101), "the state is reused per camera");

            culler.ReleaseStaleCameras(101 + GpuCuller.MaxIdleFrames);
            Assert.AreEqual(1, culler.CameraCount, "a camera seen exactly MaxIdleFrames ago is kept");
            culler.ReleaseStaleCameras(102 + GpuCuller.MaxIdleFrames);
            Assert.AreEqual(0, culler.CameraCount, "an idle camera is released");

            culler.BeginCamera(cam, false, 500);
            Assert.AreEqual(1, culler.CameraCount);
            Object.Destroy(temporary);
            yield return null; // destruction completes at the end of the frame
            culler.ReleaseStaleCameras(501);
            Assert.AreEqual(0, culler.CameraCount, "a destroyed camera is released");
        }

        [UnityTest]
        public IEnumerator FreezeCulling_KeepsThePlanesOfTheLastUnfrozenFrame(){
            // CUL-06: with freeze on, BeginCamera must not refresh the planes although the camera moved
            VoxelWorldRenderer renderer = Renderer();
            ComputeShader shader = PlayModeFixtures.GetPrivateField<ComputeShader>(Culler(renderer), "_shader");
            using GpuCuller culler = new(shader);

            _camera.transform.SetPositionAndRotation(new Vector3(0f, 50f, 0f), Quaternion.identity);
            FrustumPlanes original = culler.BeginCamera(_camera, false, 1).Planes;
            _camera.transform.SetPositionAndRotation(new Vector3(500f, 50f, 0f), Quaternion.Euler(0f, 90f, 0f));
            FrustumPlanes frozen = culler.BeginCamera(_camera, true, 2).Planes;
            FrustumPlanes refreshed = culler.BeginCamera(_camera, false, 3).Planes;

            for (int i = 0; i < FrustumPlanes.Count; i++){
                Assert.AreEqual(original[i], frozen[i], $"plane {i} changed while frozen");
            }

            Assert.AreNotEqual(original.P0, refreshed.P0, "planes must refresh once unfrozen");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Draw_DoesNotAllocateAfterWarmUp(){
            // PERF-04 for the draw callback: culling and indirect draw submission per camera must be alloc-free
            VoxelWorldRenderer renderer = Renderer();
            yield return WaitForPages(renderer, 1, 90f);
            Camera cam = GameCamera();
            Action<ScriptableRenderContext, Camera> draw = (Action<ScriptableRenderContext, Camera>)Delegate
                .CreateDelegate(typeof(Action<ScriptableRenderContext, Camera>), renderer,
                    PlayModeFixtures.GetPrivateMethod(typeof(VoxelWorldRenderer), "Draw"));

            draw(default, cam); // warm-up: creates the camera state and its lists
            draw(default, cam);
            Assert.That(() => draw(default, cam), Is.Not.AllocatingGCMemory());
            yield return null;
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator CullingOnVsOff_AtFixedViewpoint_ReducesVisiblePages_AndLogsFrameTimes(){
            // CUL-08 performance comparison: same viewpoint, culling on and off. The visible page count is the
            // deterministic part (the kernel must cull something); frame times are logged, GPU time via
            // FrameTimingManager where the platform provides it, else CPU frame time.
            VoxelWorldRenderer renderer = Renderer();
            yield return WaitForPages(renderer, MinPagesForTiming, 120f);
            GpuCuller culler = Culler(renderer);
            Camera cam = GameCamera();
            bool originalCulling = renderer.GpuCulling;
            bool originalFreeze = renderer.FreezeCulling;
            try{
                renderer.FreezeCulling = true; // fixed viewpoint even if the player drifts
                yield return null;

                // deterministic part: visible pages with culling on < pages with culling off (= allocated)
                Assert.IsTrue(culler.TryGetState(cam, out CameraCullState state), "the game camera has no cull state");
                RenderBufferManager solid = Layers(renderer)[0];
                solid.RebuildBuffers();
                RenderBuffer buffer = solid.Buffers[0];
                CullList list = new(buffer);
                uint visibleOn, visibleOff;
                uint allocated = (uint)buffer.Pages.AllocatedPages; // same frame as the read-back: the world keeps streaming
                try{
                    visibleOn = CullAndReadBack(culler, buffer, list, state.Planes, float3.zero, true);
                    visibleOff = CullAndReadBack(culler, buffer, list, state.Planes, float3.zero, false);
                }
                finally{
                    list.Dispose();
                }

                renderer.GpuCulling = true;
                yield return MeasureFrames(FrameSamples, result => LogTiming("culling on", result));
                renderer.GpuCulling = false;
                yield return MeasureFrames(FrameSamples, result => LogTiming("culling off", result));

                Debug.Log($"solid buffer 0 at the fixed viewpoint: {visibleOn} pages with culling on, {visibleOff} with culling off");
                Assert.AreEqual(allocated, visibleOff);
                Assert.Less(visibleOn, visibleOff, "culling must remove pages at the benchmark viewpoint");
            }
            finally{
                renderer.GpuCulling = originalCulling;
                renderer.FreezeCulling = originalFreeze;
            }
        }

        private struct FrameTimes{
            public double CpuMs;
            public double GpuMs;
            public bool GpuAvailable;
        }

        private static IEnumerator MeasureFrames(int frames, Action<FrameTimes> report){
            FrameTiming[] timing = new FrameTiming[1];
            for (int i = 0; i < WarmupFrames; i++) yield return null;

            double cpu = 0, gpu = 0;
            int gpuSamples = 0;
            for (int i = 0; i < frames; i++){
                yield return null;
                cpu += Time.unscaledDeltaTime * 1000.0;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) == 1 && timing[0].gpuFrameTime > 0){
                    gpu += timing[0].gpuFrameTime;
                    gpuSamples++;
                }
            }

            report(new FrameTimes{
                CpuMs = cpu / frames,
                GpuMs = gpuSamples > 0 ? gpu / gpuSamples : 0,
                GpuAvailable = gpuSamples > 0
            });
        }

        private static void LogTiming(string label, FrameTimes t){
            string gpu = t.GpuAvailable ? $"GPU {t.GpuMs:0.00} ms" : "GPU timing unavailable (FrameTimingManager)";
            Debug.Log($"{label}: CPU frame {t.CpuMs:0.00} ms, {gpu}, {FrameSamples} frames");
        }
    }
}
