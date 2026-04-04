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
        public Vector2 uv00;
        public Vector2 uv01;
        public Vector2 uv02;
        public Vector2 uv03;

        public void RecalculateNormal()
        {
            normal = CalculateNormal();
        }

        public QuadData ToStruct()
        {
            return new QuadData
            {
                position00 = position00.Float3(),
                position01 = position01.Float3(),
                position02 = position02.Float3(),
                position03 = position03.Float3(),
                normal = CalculateNormal().Float3(),
                uv00 = uv00.Float2(),
                uv01 = uv01.Float2(),
                uv02 = uv02.Float2(),
                uv03 = uv03.Float2()
            };
        }

        private Vector3 CalculateNormal()
        {
            return Vector3.Cross(position01 - position00, position02 - position00).normalized;
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
            public float2 uv00;
            public float2 uv01;
            public float2 uv02;
            public float2 uv03;
        }
    }
}