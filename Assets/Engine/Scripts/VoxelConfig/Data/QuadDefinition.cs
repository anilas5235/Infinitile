using Engine.Scripts.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    /// Describes a single quad face used by voxel shapes, including vertex positions, basis vectors, and UVs.
    /// </summary>
    [CreateAssetMenu(menuName = "Voxel/Shape/Quad Definition", fileName = "QuadDefinition")]
    public class QuadDefinition : ScriptableObject
    {
        public Vector3 position00;
        public Vector3 position01;
        public Vector3 position02;
        public Vector3 position03;
        public Vector3 normal;
        public Vector3 up;
        public Vector3 right;
        public Vector2 uv00;
        public Vector2 uv01;
        public Vector2 uv02;
        public Vector2 uv03;

#if UNITY_EDITOR
        /// <summary>
        /// Recalculates the quad basis when edited in the Unity editor.
        /// </summary>
        private void OnValidate()
        {
            RecalculateNormal();
        }
#endif

        /// <summary>
        /// Recalculates the normal, up, and right vectors from the current vertex positions.
        /// </summary>
        public void RecalculateNormal()
        {
            CalculateBasis(out normal, out up, out right);
        }

        /// <summary>
        /// Converts this quad definition into its Burst-friendly data representation.
        /// </summary>
        /// <returns>A <see cref="QuadData" /> struct containing the quad data.</returns>
        public QuadData ToStruct()
        {
            CalculateBasis(out Vector3 calculatedNormal, out Vector3 calculatedUp, out Vector3 calculatedRight);

            return new QuadData
            {
                position00 = position00.Float3(),
                position01 = position01.Float3(),
                position02 = position02.Float3(),
                position03 = position03.Float3(),
                normal = calculatedNormal.Float3(),
                up = calculatedUp.Float3(),
                right = calculatedRight.Float3(),
                uv00 = uv00.Float2(),
                uv01 = uv01.Float2(),
                uv02 = uv02.Float2(),
                uv03 = uv03.Float2()
            };
        }

        /// <summary>
        /// Calculates the local basis vectors for the quad from its vertex positions.
        /// </summary>
        /// <param name="calculatedNormal">The calculated normal vector.</param>
        /// <param name="calculatedUp">The calculated up vector.</param>
        /// <param name="calculatedRight">The calculated right vector.</param>
        private void CalculateBasis(out Vector3 calculatedNormal, out Vector3 calculatedUp, out Vector3 calculatedRight)
        {
            const float epsilon = 1e-6f;

            Vector3 edgeA = position01 - position00;
            Vector3 edgeB = position02 - position00;

            Vector3 cross = Vector3.Cross(edgeA, edgeB);
            if (cross.sqrMagnitude < epsilon)
                // Fallback for degenerate/collinear points using the second triangle.
                cross = Vector3.Cross(position02 - position00, position03 - position00);

            calculatedNormal = cross.sqrMagnitude > epsilon ? cross.normalized : Vector3.up;

            Vector3 referenceUp = Mathf.Abs(Vector3.Dot(calculatedNormal, Vector3.up)) > 0.999f
                ? Vector3.forward
                : Vector3.up;
            calculatedRight = Vector3.Cross(referenceUp, calculatedNormal).normalized;
            calculatedUp = Vector3.Cross(calculatedNormal, calculatedRight).normalized;

            calculatedRight *= -1;
        }

        /// <summary>
        /// Burst-friendly quad data used by runtime rendering systems.
        /// </summary>
        public struct QuadData
        {
            /// <summary>
            /// Vertex position 00 relative to the voxel origin.
            /// </summary>
            public float3 position00;

            /// <summary>
            /// Vertex position 01 relative to the voxel origin.
            /// </summary>
            public float3 position01;

            /// <summary>
            /// Vertex position 02 relative to the voxel origin.
            /// </summary>
            public float3 position02;

            /// <summary>
            /// Vertex position 03 relative to the voxel origin.
            /// </summary>
            public float3 position03;

            /// <summary>
            /// Quad normal vector.
            /// </summary>
            public float3 normal;

            /// <summary>
            /// Quad up vector.
            /// </summary>
            public float3 up;

            /// <summary>
            /// Quad right vector.
            /// </summary>
            public float3 right;

            /// <summary>
            /// UV coordinate for vertex 00.
            /// </summary>
            public float2 uv00;

            /// <summary>
            /// UV coordinate for vertex 01.
            /// </summary>
            public float2 uv01;

            /// <summary>
            /// UV coordinate for vertex 02.
            /// </summary>
            public float2 uv02;

            /// <summary>
            /// UV coordinate for vertex 03.
            /// </summary>
            public float2 uv03;
        }
    }
}