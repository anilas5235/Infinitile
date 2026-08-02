using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.Render
{
    [BurstCompile]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Vertex
    {
        public float3 Position;
        private uint4 PackedData;
    }
}