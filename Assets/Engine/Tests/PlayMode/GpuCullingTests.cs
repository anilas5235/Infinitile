using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Render;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
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

        /// <summary>
        ///     Draw distance for the timing comparison: at the scene default (6 chunks) the Editor is not GPU-bound
        ///     and culling on/off measure the same frame time, so the world is reloaded with this distance.
        /// </summary>
        private const int TimingDrawDistance = 10;

        private const int TimingWidth = 1280;
        private const int TimingHeight = 720;

        /// <summary>
        ///     The viewpoint is rendered this many times per frame so the voxel draw dominates the GPU frame (one
        ///     Editor frame is CPU-bound and its GPU time is mostly idle gaps, hiding the saving).
        /// </summary>
        private const int TimingCameraCount = 4;
        /// <summary>
        ///     Culling on and off are measured in interleaved rounds and compared by median frame time, so drift and
        ///     spikes from whatever else uses the GPU (other windows, the Scene view) cancel out as far as possible.
        /// </summary>
        private const int TimingRounds = 5;
        private const int FramesPerRound = 30;
        private const int WarmupFrames = 5;

        /// <summary>
        ///     If the per-round medians of one state spread wider than this fraction of the overall median, the
        ///     machine is too busy for a meaningful GPU comparison and only the page counts are asserted.
        /// </summary>
        private const double MaxRoundSpread = 0.3;

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
        public IEnumerator FreezeCulling_AppliesTheGameFrustumToEveryCamera(){
            // CUL-06: while frozen, a Scene view camera culls with the game camera's last unfrozen frustum, so the
            // Scene view shows exactly the pages the game camera kept; once unfrozen it goes back to its own
            VoxelWorldRenderer renderer = Renderer();
            ComputeShader shader = PlayModeFixtures.GetPrivateField<ComputeShader>(Culler(renderer), "_shader");
            using GpuCuller culler = new(shader);
            GameObject sceneObject = new("GpuCullingTests scene view camera");
            Camera sceneCamera = sceneObject.AddComponent<Camera>();
            sceneCamera.enabled = false;
            sceneCamera.cameraType = CameraType.SceneView;
            sceneCamera.transform.SetPositionAndRotation(new Vector3(300f, 120f, -50f), Quaternion.Euler(45f, 180f, 0f));

            Assert.AreEqual(CameraType.Game, _camera.cameraType);
            Assert.IsFalse(culler.TryGetFrozenFrustum(out _), "no game frustum before a game camera was seen");
            _camera.transform.SetPositionAndRotation(new Vector3(0f, 50f, 0f), Quaternion.identity);
            FrustumPlanes game = culler.BeginCamera(_camera, false, 1).Planes;
            FrustumPlanes own = culler.BeginCamera(sceneCamera, false, 1).Planes;
            Assert.AreNotEqual(game.P0, own.P0, "the two cameras must look at different things");
            Assert.IsTrue(culler.TryGetFrozenFrustum(out Matrix4x4 inverseViewProjection));
            Assert.AreNotEqual(Matrix4x4.zero, inverseViewProjection);

            FrustumPlanes frozenScene = culler.BeginCamera(sceneCamera, true, 2).Planes;
            FrustumPlanes frozenGame = culler.BeginCamera(_camera, true, 2).Planes;
            for (int i = 0; i < FrustumPlanes.Count; i++){
                Assert.AreEqual(game[i], frozenScene[i], $"scene view plane {i} must be the game camera's while frozen");
                Assert.AreEqual(game[i], frozenGame[i], $"game plane {i} changed while frozen");
            }

            FrustumPlanes refreshed = culler.BeginCamera(sceneCamera, false, 3).Planes;
            for (int i = 0; i < FrustumPlanes.Count; i++)
                Assert.AreEqual(own[i], refreshed[i], $"scene view plane {i} must be its own again once unfrozen");

            Object.Destroy(sceneObject);
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

        /// <summary>
        ///     Reloads the game scene with another draw distance; the settings asset is shared in memory. Waits for
        ///     an idle scheduler first: unloading the scene while a CMeshBuildJob runs frees the registry's
        ///     VoxelRenderDefs under the job (shutdown order between DataImporter and VoxelWorld, not Phase C).
        /// </summary>
        private static IEnumerator ReloadWorldWithDrawDistance(int drawDistance){
            float deadline = Time.realtimeSinceStartup + 300f;
            while (!SchedulerSleeping() && Time.realtimeSinceStartup < deadline) yield return null;
            VoxelWorld.Instance.Settings.Chunk.DrawDistance = drawDistance;
            AsyncOperation load = SceneManager.LoadSceneAsync(PlayModeFixtures.SceneName, LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return PlayModeFixtures.LoadGameScene();
        }

        /// <summary>True when every scheduler handler sleeps, i.e. the world around the focus is fully built.</summary>
        private static bool SchedulerSleeping(){
            VoxelEngineScheduler scheduler =
                PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(VoxelWorld.Instance, "_scheduler");
            DataJobStateHandler data = PlayModeFixtures.GetPrivateField<DataJobStateHandler>(scheduler, "_dataJobHandler");
            MeshJobStateHandler mesh = PlayModeFixtures.GetPrivateField<MeshJobStateHandler>(scheduler, "_meshJobHandler");
            ColliderJobStateHandler collider =
                PlayModeFixtures.GetPrivateField<ColliderJobStateHandler>(scheduler, "_colliderJobHandler");
            return data.Sleeping && mesh.Sleeping && collider.Sleeping;
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator CullingOnVsOff_AtFixedViewpoint_ReducesVisiblePagesAndGpuTime(){
            // CUL-08 performance comparison: same viewpoint, culling on and off, in a world with a larger draw
            // distance so the draw is GPU-bound. The visible page count must drop and, where FrameTimingManager
            // provides GPU times, the GPU frame time must drop too; CPU frame time is logged. The world is always
            // reloaded with the default draw distance before anything is asserted (no yield in finally).
            int originalDrawDistance = VoxelWorld.Instance.Settings.Chunk.DrawDistance;
            yield return ReloadWorldWithDrawDistance(TimingDrawDistance);
            VoxelWorldRenderer renderer = Renderer();
            GpuCuller culler = Culler(renderer);

            string failure = null;
            float deadline = Time.realtimeSinceStartup + 300f;
            while (!(SchedulerSleeping() && SolidPages(renderer) >= MinPagesForTiming)){
                if (Time.realtimeSinceStartup > deadline){
                    failure = $"the world did not reach a steady state with {MinPagesForTiming} solid pages in 300 s";
                    break;
                }

                yield return null;
            }

            // The test camera renders the game camera's view into a render texture every frame, independent of
            // whether the Editor's Game view is visible, at a fixed resolution (the Game view may not render at all
            // while the Test Runner covers it).
            // Created after the reload: the fixture's camera belonged to the unloaded scene.
            GameObject cameraObject = new("GpuCullingTests timing cameras");
            Camera[] cameras = new Camera[TimingCameraCount];
            RenderTexture target = new(TimingWidth, TimingHeight, 24);
            if (failure == null){
                Camera game = GameCamera(); // after the wait: the player (and its camera) spawns a few frames in
                for (int i = 0; i < TimingCameraCount; i++){
                    GameObject child = new($"camera {i}");
                    child.transform.SetParent(cameraObject.transform, false);
                    cameras[i] = child.AddComponent<Camera>();
                    cameras[i].CopyFrom(game);
                    cameras[i].targetTexture = target;
                    cameras[i].enabled = true;
                }

                yield return null;
                yield return null;
            }

            Camera cam = cameras[0];

            uint visibleOn = 0, visibleOff = 0, allocated = 0;
            int totalPages = 0;
            FrameTimes on = new(), off = new();
            if (failure == null){
                renderer.FreezeCulling = true; // fixed viewpoint even if the player drifts
                yield return null;
                try{
                    Assert.IsTrue(culler.TryGetState(cam, out CameraCullState state),
                        $"game camera '{cam.name}' has no cull state ({culler.CameraCount} cameras culled, frame {Time.frameCount})");
                    RenderBufferManager solid = Layers(renderer)[0];
                    solid.RebuildBuffers();
                    RenderBuffer buffer = solid.Buffers[0];
                    allocated = (uint)buffer.Pages.AllocatedPages; // same frame as the read-back
                    totalPages = SolidPages(renderer);
                    CullList list = new(buffer);
                    try{
                        visibleOn = CullAndReadBack(culler, buffer, list, state.Planes, float3.zero, true);
                        visibleOff = CullAndReadBack(culler, buffer, list, state.Planes, float3.zero, false);
                    }
                    finally{
                        list.Dispose();
                    }
                }
                catch (Exception e){
                    failure = e.Message;
                }
            }

            if (failure == null){
                for (int round = 0; round < TimingRounds; round++){
                    renderer.GpuCulling = true;
                    yield return MeasureRound(on);
                    renderer.GpuCulling = false;
                    yield return MeasureRound(off);
                }

                renderer.GpuCulling = true;
            }

            for (int i = 0; i < TimingCameraCount; i++){
                if (cameras[i] == null) continue;
                cameras[i].enabled = false;
                cameras[i].targetTexture = null;
            }

            Object.Destroy(cameraObject);
            target.Release();
            Object.Destroy(target);
            yield return ReloadWorldWithDrawDistance(originalDrawDistance); // later tests expect the default world
            Assert.IsNull(failure, failure);

            LogTiming("culling on", on);
            LogTiming("culling off", off);
            Debug.Log($"solid buffer 0 at the fixed viewpoint (draw distance {TimingDrawDistance}, {TimingCameraCount} cameras): {visibleOn} pages with culling on, {visibleOff} with culling off, {totalPages} solid pages in total");
            Assert.AreEqual(allocated, visibleOff);
            Assert.Less(visibleOn, visibleOff, "culling must remove pages at the benchmark viewpoint");
            if (!on.GpuAvailable || !off.GpuAvailable)
                Debug.LogWarning("FrameTimingManager gave no GPU times; only the page counts were checked");
            else if (on.RoundSpread > MaxRoundSpread || off.RoundSpread > MaxRoundSpread)
                Debug.LogWarning($"GPU frame times too unstable for a comparison (round spread on {on.RoundSpread:P0}, off {off.RoundSpread:P0}); only the page counts were checked");
            else
                Assert.Less(on.GpuMs, off.GpuMs, "GPU frame time must drop with culling on");
        }

        /// <summary>Per-frame CPU and GPU times of one state over all rounds, summarised by medians.</summary>
        private sealed class FrameTimes{
            private readonly List<double> _cpu = new();
            private readonly List<double> _gpu = new();
            private readonly List<double> _roundGpuMedians = new();

            public double CpuMs => Median(_cpu);
            public double GpuMs => Median(_gpu);
            public bool GpuAvailable => _gpu.Count > 0;
            public int Frames => _cpu.Count;

            /// <summary>Widest deviation of a round's GPU median from the overall GPU median, as a fraction.</summary>
            public double RoundSpread{
                get{
                    if (_roundGpuMedians.Count == 0) return 0;
                    double overall = GpuMs;
                    double spread = 0;
                    foreach (double m in _roundGpuMedians) spread = Math.Max(spread, Math.Abs(m - overall) / overall);
                    return spread;
                }
            }

            public void AddRound(List<double> cpu, List<double> gpu){
                _cpu.AddRange(cpu);
                _gpu.AddRange(gpu);
                if (gpu.Count > 0) _roundGpuMedians.Add(Median(gpu));
            }

            private static double Median(List<double> values){
                if (values.Count == 0) return 0;
                List<double> sorted = new(values);
                sorted.Sort();
                int mid = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
            }
        }

        private static IEnumerator MeasureRound(FrameTimes into){
            FrameTiming[] timing = new FrameTiming[1];
            for (int i = 0; i < WarmupFrames; i++) yield return null;

            List<double> cpu = new(FramesPerRound);
            List<double> gpu = new(FramesPerRound);
            for (int i = 0; i < FramesPerRound; i++){
                yield return null;
                cpu.Add(Time.unscaledDeltaTime * 1000.0);
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) == 1 && timing[0].gpuFrameTime > 0)
                    gpu.Add(timing[0].gpuFrameTime);
            }

            into.AddRound(cpu, gpu);
        }

        private static void LogTiming(string label, FrameTimes t){
            string gpu = t.GpuAvailable
                ? $"GPU {t.GpuMs:0.00} ms (round spread {t.RoundSpread:P0})"
                : "GPU timing unavailable (FrameTimingManager)";
            Debug.Log($"{label}: median CPU frame {t.CpuMs:0.00} ms, median {gpu}, {t.Frames} frames in {TimingRounds} rounds");
        }
    }
}
