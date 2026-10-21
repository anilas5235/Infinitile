using Engine.Scripts.Render;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using FrustumPlanes = Engine.Scripts.Render.FrustumPlanes;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     The CPU frustum test reference. Tests one buffer of page bounds (1024 pages, a block of
    ///     partitions around the camera, part of them in view) against six planes with
    ///     <see cref="FrustumCulling.IsVisible(in PageBounds, in FrustumPlanes)" />. The GPU kernel does the same
    ///     work per page, so this is what a CPU fallback would cost per buffer and camera. Each sample is the total
    ///     of <see cref="IterationsPerMeasurement" /> passes over the buffer (Measure.Method does not average them).
    /// </summary>
    [TestFixture]
    internal class FrustumCullingBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;
        private const int IterationsPerMeasurement = 10;

        private GameObject _cameraObject;
        private FrustumPlanes _planes;
        private NativeArray<PageBounds> _bounds;
        private int _visible; // keeps the loop from being optimised away

        [SetUp]
        public void SetUp(){
            _cameraObject = new GameObject("FrustumCullingBenchmark camera");
            Camera cam = _cameraObject.AddComponent<Camera>();
            cam.enabled = false;
            cam.transform.SetPositionAndRotation(new Vector3(16f, 80f, 16f), Quaternion.Euler(20f, 35f, 0f));
            cam.fieldOfView = 60f;
            cam.aspect = 16f / 9f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 500f;
            _planes = FrustumPlanes.FromCamera(cam, new Plane[FrustumPlanes.Count]);

            // 1024 pages = a 16 x 4 x 16 block of partitions centred on the camera column
            _bounds = new NativeArray<PageBounds>(PagesPerBuffer, Allocator.Persistent);
            int i = 0;
            for (int x = -8; x < 8; x++)
            for (int y = 0; y < 4; y++)
            for (int z = -8; z < 8; z++)
                _bounds[i++] = PageBounds.FromPartition(new int3(x, y, z));
            Assert.AreEqual(PagesPerBuffer, i);
        }

        [TearDown]
        public void TearDown(){
            if (_bounds.IsCreated) _bounds.Dispose();
            Object.DestroyImmediate(_cameraObject);
        }

        [Test]
        [Performance]
        public void IsVisible_OneBufferOfPages(){
            Measure.Method(() => {
                    int visible = 0;
                    for (int i = 0; i < PagesPerBuffer; i++)
                        if (FrustumCulling.IsVisible(_bounds[i], _planes)) visible++;
                    _visible = visible;
                })
                .SampleGroup(new SampleGroup("FrustumCulling.IsVisible 1024 pages us", SampleUnit.Microsecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .Run();

            Assert.Greater(_visible, 0, "the benchmark viewpoint should see some pages");
            Assert.Less(_visible, PagesPerBuffer, "the benchmark viewpoint should cull some pages");
        }
    }
}
