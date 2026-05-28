using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Meshing
{
    [BurstCompile]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Vertex
    {
        public float3 Position;
        private uint4 PackedData;
    }

    /// <summary>
    ///     Compact vertex structure for collider mesh (position + normal).
    /// </summary>
    [BurstCompile]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CVertex
    {
        public half4 Position;
        public half4 Normal;

        /// <summary>
        ///     Creates a collider vertex.
        /// </summary>
        public CVertex(float3 position, float3 normal)
        {
            Position = new half4((half3)position, half.zero);
            Normal = new half4((half3)normal, half.zero);
        }
    }

    /// <summary>
    ///     Buffer collection for mesh & collider building (native lists). Internal data container for mesher.
    /// </summary>
    [BurstCompile]
    internal struct MeshBuffer
    {
        public NativeList<CVertex> CVertexBuffer;
        public NativeList<ushort> CIndexBuffer;

        /// <summary>
        ///     Disposes all native lists.
        /// </summary>
        internal void Dispose()
        {
            CVertexBuffer.Dispose();
            CIndexBuffer.Dispose();
        }

        public void AddCIndex(int index)
        {
            EnsureCapacity(CIndexBuffer, 1);
            CIndexBuffer.AddNoResize((ushort)index);
        }

        public void AddCVertex(CVertex vertex)
        {
            EnsureCapacity(CVertexBuffer, 1);
            CVertexBuffer.AddNoResize(vertex);
        }

        private void EnsureCapacity<T>(NativeList<T> list, int add) where T : unmanaged
        {
            int need = list.Length + add;
            if (need <= list.Capacity) return;
            int newCap = math.max(list.Capacity * 2, need);
            list.Capacity = newCap;
        }
    }

    internal enum SubMeshType : byte
    {
        Solid = 0,
        Transparent = 1,
        Foliage = 2,
        Collider = 3
    }
}