using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Engine.Scripts.Jobs.ColliderMeshing{
    /// <summary>
    ///     Compact vertex structure for collider mesh (position only: PhysX bakes positions and indices and
    ///     ignores normals). Four halves, because a vertex attribute must be a multiple of 4 bytes.
    /// </summary>
    [BurstCompile]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CVertex{
        public half4 Position;

        /// <summary>
        ///     Creates a collider vertex.
        /// </summary>
        public CVertex(float3 position){
            Position = new half4((half3)position, half.zero);
        }

        /// <summary>Vertex layout matching <see cref="CVertex" />, for <c>Mesh.MeshData.SetVertexBufferParams</c>.</summary>
        internal static NativeArray<VertexAttributeDescriptor> CreateLayout(Allocator allocator){
            return new NativeArray<VertexAttributeDescriptor>(1, allocator){
                [0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 4)
            };
        }
    }

    /// <summary>
    ///     Buffer collection for mesh & collider building (native lists). Internal data container for mesher.
    /// </summary>
    [BurstCompile]
    internal struct CMeshBuffer{
        public NativeList<CVertex> CVertexBuffer;
        public NativeList<int> CIndexBuffer; // int so meshes above 65536 vertices can use 32-bit indices

        /// <summary>
        ///     Disposes all native lists.
        /// </summary>
        internal void Dispose(){
            CVertexBuffer.Dispose();
            CIndexBuffer.Dispose();
        }

        public void AddCIndex(int index){
            EnsureCapacity(CIndexBuffer, 1);
            CIndexBuffer.AddNoResize(index);
        }

        public void AddCVertex(CVertex vertex){
            EnsureCapacity(CVertexBuffer, 1);
            CVertexBuffer.AddNoResize(vertex);
        }

        private void EnsureCapacity<T>(NativeList<T> list, int add) where T : unmanaged{
            int need = list.Length + add;
            if (need <= list.Capacity) return;
            int newCap = math.max(list.Capacity * 2, need);
            list.Capacity = newCap;
        }
    }
}
