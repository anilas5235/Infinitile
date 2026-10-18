using Engine.Scripts.Render;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;
using FrustumPlanes = Engine.Scripts.Render.FrustumPlanes;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     CUL-08: pins the C# reference of the GPU AABB-frustum test (<see cref="FrustumCulling" />) for boxes
    ///     inside, outside, intersecting, behind the camera and containing the camera, plus the extruded shadow
    ///     test (CUL-05) and the per-page bounds helpers (CUL-01).
    /// </summary>
    public class FrustumCullingTests{
        private const float Near = 0.3f;
        private const float Far = 100f;
        private const float Fov = 60f;

        private GameObject _cameraObject;
        private Camera _camera;
        private FrustumPlanes _planes;

        [SetUp]
        public void SetUp(){
            // camera at the origin looking down +Z; 60 deg vertical FOV, 16:9, near 0.3, far 100
            _cameraObject = new GameObject("FrustumCullingTests camera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _camera.fieldOfView = Fov;
            _camera.aspect = 16f / 9f;
            _camera.nearClipPlane = Near;
            _camera.farClipPlane = Far;
            _planes = FrustumPlanes.FromCamera(_camera, new Plane[FrustumPlanes.Count]);
        }

        [TearDown]
        public void TearDown(){
            Object.DestroyImmediate(_cameraObject);
        }

        private static PageBounds Box(float3 center, float halfSize){
            return new PageBounds(center - halfSize, center + halfSize);
        }

        private bool Visible(in PageBounds box){
            return FrustumCulling.IsVisible(box, _planes);
        }

        #region planes

        [Test]
        public void FromCamera_NormalsPointInward_PointAheadIsInsideEveryPlane(){
            // a point straight ahead inside the frustum has a positive signed distance to all six planes
            float3 p = new(0f, 0f, 10f);
            for (int i = 0; i < FrustumPlanes.Count; i++){
                float4 plane = _planes[i];
                Assert.Greater(math.dot(plane.xyz, p) + plane.w, 0f, $"plane {i} should face inward");
            }
        }

        [Test]
        public void Indexer_RoundTripsAllSixPlanes(){
            FrustumPlanes planes = default;
            for (int i = 0; i < FrustumPlanes.Count; i++) planes[i] = new float4(i, i + 1, i + 2, i + 3);
            for (int i = 0; i < FrustumPlanes.Count; i++) Assert.AreEqual(new float4(i, i + 1, i + 2, i + 3), planes[i]);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _ = planes[6]);
        }

        [Test]
        public void CopyTo_WritesTheUploadArrayInPlaneOrder(){
            Vector4[] upload = new Vector4[FrustumPlanes.Count];
            _planes.CopyTo(upload);
            for (int i = 0; i < FrustumPlanes.Count; i++) Assert.AreEqual((Vector4)_planes[i], upload[i]);
        }

        #endregion

        #region visibility

        [Test]
        public void Box_Inside_IsVisible(){
            Assert.IsTrue(Visible(Box(new float3(0f, 0f, 10f), 1f)));
        }

        [Test]
        public void Box_OutsideToTheSide_IsCulled(){
            // at z = 10 the frustum half-width is tan(30 deg) * 16/9 * 10, about 10.3; x = 100 is far outside
            Assert.IsFalse(Visible(Box(new float3(100f, 0f, 10f), 1f)));
        }

        [Test]
        public void Box_OutsideAbove_IsCulled(){
            Assert.IsFalse(Visible(Box(new float3(0f, 100f, 10f), 1f)));
        }

        [Test]
        public void Box_BeyondFarPlane_IsCulled(){
            Assert.IsFalse(Visible(Box(new float3(0f, 0f, Far + 50f), 1f)));
        }

        [Test]
        public void Box_IntersectingTheSideOfTheFrustum_IsVisible(){
            // the frustum edge at z = 10 is at x of about 10.3; a box from x = 9 to x = 13 straddles it
            Assert.IsTrue(Visible(new PageBounds(new float3(9f, -1f, 9f), new float3(13f, 1f, 11f))));
        }

        [Test]
        public void Box_IntersectingTheFarPlane_IsVisible(){
            Assert.IsTrue(Visible(new PageBounds(new float3(-1f, -1f, Far - 1f), new float3(1f, 1f, Far + 20f))));
        }

        [Test]
        public void Box_BehindTheCamera_IsCulled(){
            Assert.IsFalse(Visible(Box(new float3(0f, 0f, -10f), 1f)));
        }

        [Test]
        public void Box_BehindTheCameraOnTheViewAxis_IsCulled(){
            // ends just before the near plane; must not be reported visible although it is on the view axis
            Assert.IsFalse(Visible(new PageBounds(new float3(-5f, -5f, -20f), new float3(5f, 5f, 0f))));
        }

        [Test]
        public void Box_ContainingTheCamera_IsVisible(){
            Assert.IsTrue(Visible(Box(float3.zero, 50f)));
        }

        [Test]
        public void Box_ContainingTheWholeFrustum_IsVisible(){
            Assert.IsTrue(Visible(Box(float3.zero, 10000f)));
        }

        [Test]
        public void Box_InFrontButClearlyLeftOfTheFrustum_IsCulled(){
            Assert.IsFalse(Visible(Box(new float3(-40f, 0f, 20f), 2f)));
        }

        [Test]
        public void RotatedCamera_UsesTheRotatedFrustum(){
            // look down -X: a box at -X is visible, the box that was in front at +Z is now culled
            _camera.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            FrustumPlanes rotated = FrustumPlanes.FromCamera(_camera, new Plane[FrustumPlanes.Count]);
            Assert.IsTrue(FrustumCulling.IsVisible(Box(new float3(-10f, 0f, 0f), 1f), rotated));
            Assert.IsFalse(FrustumCulling.IsVisible(Box(new float3(0f, 0f, 10f), 1f), rotated));
        }

        #endregion

        #region shadow extrusion (CUL-05)

        [Test]
        public void Extruded_BoxAboveTheFrustum_BecomesVisibleWhenExtrudedAlongTheLight(){
            // a sun shining straight down: a box 30 units above the view casts a shadow into it
            PageBounds box = Box(new float3(0f, 30f, 10f), 1f);
            float3 lightDirectionTimesDistance = new(0f, -40f, 0f);
            Assert.IsFalse(Visible(box), "unextruded box must be outside");
            Assert.IsTrue(FrustumCulling.IsVisibleExtruded(box, lightDirectionTimesDistance, _planes));
        }

        [Test]
        public void Extruded_ShadowDistanceTooShort_StaysCulled(){
            PageBounds box = Box(new float3(0f, 30f, 10f), 1f);
            Assert.IsFalse(FrustumCulling.IsVisibleExtruded(box, new float3(0f, -5f, 0f), _planes));
        }

        [Test]
        public void Extruded_AwayFromTheFrustum_StaysCulled(){
            // the same box with the light shining upwards never reaches the view
            PageBounds box = Box(new float3(0f, 30f, 10f), 1f);
            Assert.IsFalse(FrustumCulling.IsVisibleExtruded(box, new float3(0f, 40f, 0f), _planes));
        }

        [Test]
        public void Extruded_ZeroExtrusion_EqualsTheMainTest(){
            PageBounds inside = Box(new float3(0f, 0f, 10f), 1f);
            PageBounds outside = Box(new float3(100f, 0f, 10f), 1f);
            Assert.AreEqual(Visible(inside), FrustumCulling.IsVisibleExtruded(inside, float3.zero, _planes));
            Assert.AreEqual(Visible(outside), FrustumCulling.IsVisibleExtruded(outside, float3.zero, _planes));
        }

        #endregion

        #region PageBounds (CUL-01)

        [Test]
        public void PageBounds_Extrude_GrowsTheBoxAlongTheExtrusionOnly(){
            PageBounds box = new(new float3(0f, 0f, 0f), new float3(1f, 1f, 1f));
            PageBounds extruded = box.Extrude(new float3(2f, -3f, 0f));
            Assert.AreEqual(new float3(0f, -3f, 0f), extruded.Min);
            Assert.AreEqual(new float3(3f, 1f, 1f), extruded.Max);
        }

        [Test]
        public void PageBounds_FromPartition_IsOnePartitionWide(){
            PageBounds bounds = PageBounds.FromPartition(new int3(-1, 2, 3));
            Assert.AreEqual(new float3(PartitionSize), bounds.Max - bounds.Min);
            Assert.AreEqual((float3)(new int3(-1, 2, 3) * PartitionSize), bounds.Min);
        }

        #endregion
    }
}
