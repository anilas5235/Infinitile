using Runtime.Engine.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine;

namespace Runtime.Engine.VoxelConfig.Data
{
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

        public void RecalculateNormal()
        {
            CalculateBasis(out normal, out up, out right);
        }

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

        private void CalculateBasis(out Vector3 calculatedNormal, out Vector3 calculatedUp, out Vector3 calculatedRight)
        {
            const float epsilon = 1e-6f;

            Vector3 edgeA = position01 - position00;
            Vector3 edgeB = position02 - position00;

            Vector3 cross = Vector3.Cross(edgeA, edgeB);
            if (cross.sqrMagnitude < epsilon)
            {
                // Fallback for degenerate/collinear points using the second triangle.
                cross = Vector3.Cross(position02 - position00, position03 - position00);
            }

            calculatedNormal = cross.sqrMagnitude > epsilon ? cross.normalized : Vector3.up;

          Vector3 referenceUp = Mathf.Abs(Vector3.Dot(calculatedNormal, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;
          calculatedRight = Vector3.Cross(referenceUp, calculatedNormal).normalized;
          calculatedUp = Vector3.Cross(calculatedNormal, calculatedRight).normalized;

          calculatedRight *= -1;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            RecalculateNormal();
        }
#endif

        public struct QuadData
        {
            public float3 position00; // vertex offset from voxel origin
            public float3 position01;
            public float3 position02;
            public float3 position03;
            public float3 normal;
            public float3 up;
            public float3 right;
            public float2 uv00;
            public float2 uv01;
            public float2 uv02;
            public float2 uv03;
        }
    }
}