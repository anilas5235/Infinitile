using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     The six camera frustum planes as (normal, distance) with normals pointing inward, in the order
    ///     <see cref="GeometryUtility.CalculateFrustumPlanes(Camera, Plane[])" /> produces them. Holds no managed
    ///     memory so it can be stored per camera, copied and used from Burst; <see cref="FrustumCulling" /> is the
    ///     C# reference of the GPU test.
    /// </summary>
    public struct FrustumPlanes{
        public const int Count = 6;

        public float4 P0, P1, P2, P3, P4, P5;

        public float4 this[int i]{
            get => i switch{
                0 => P0, 1 => P1, 2 => P2, 3 => P3, 4 => P4, 5 => P5,
                _ => throw new System.ArgumentOutOfRangeException(nameof(i))
            };
            set{
                switch (i){
                    case 0: P0 = value; break;
                    case 1: P1 = value; break;
                    case 2: P2 = value; break;
                    case 3: P3 = value; break;
                    case 4: P4 = value; break;
                    case 5: P5 = value; break;
                    default: throw new System.ArgumentOutOfRangeException(nameof(i));
                }
            }
        }

        /// <summary>
        ///     Reads the planes of <paramref name="cam" /> without allocating; <paramref name="scratch" /> must hold six
        ///     entries and is reused by the caller.
        /// </summary>
        public static FrustumPlanes FromCamera(Camera cam, Plane[] scratch){
            GeometryUtility.CalculateFrustumPlanes(cam, scratch);
            FrustumPlanes planes = default;
            for (int i = 0; i < Count; i++){
                Plane p = scratch[i];
                planes[i] = new float4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }

            return planes;
        }

        /// <summary>Writes the planes into the array that is uploaded with <c>ComputeShader.SetVectorArray</c>.</summary>
        public void CopyTo(Vector4[] destination){
            for (int i = 0; i < Count; i++) destination[i] = this[i];
        }
    }

    /// <summary>
    ///     C# reference of the AABB-frustum test that <c>PageCulling.compute</c> runs per page; the tests compare the
    ///     GPU visible set against it.
    /// </summary>
    public static class FrustumCulling{
        /// <summary>
        ///     Positive-vertex test: for each plane take the box corner furthest along the inward normal; if even that
        ///     corner is behind the plane (dot(n, p) + d &lt; 0) the box is completely outside. Boxes that merely
        ///     straddle an edge of the frustum pass (conservative), which is what a culling test needs.
        /// </summary>
        public static bool IsVisible(in float3 min, in float3 max, in FrustumPlanes planes){
            return IsInside(min, max, planes.P0) && IsInside(min, max, planes.P1) && IsInside(min, max, planes.P2) &&
                   IsInside(min, max, planes.P3) && IsInside(min, max, planes.P4) && IsInside(min, max, planes.P5);
        }

        /// <inheritdoc cref="IsVisible(in float3, in float3, in FrustumPlanes)" />
        public static bool IsVisible(in PageBounds bounds, in FrustumPlanes planes){
            return IsVisible(bounds.Min, bounds.Max, planes);
        }

        /// <summary>
        ///     Shadow variant: tests the bounds extruded along the light direction by the shadow distance.
        /// </summary>
        public static bool IsVisibleExtruded(in PageBounds bounds, float3 extrusion, in FrustumPlanes planes){
            return IsVisible(bounds.Extrude(extrusion), planes);
        }

        private static bool IsInside(in float3 min, in float3 max, in float4 plane){
            float3 n = plane.xyz;
            float3 positive = math.select(min, max, n > 0f);
            return math.dot(n, positive) + plane.w >= 0f;
        }
    }
}
