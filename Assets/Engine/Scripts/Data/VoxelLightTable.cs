using System;
using Unity.Collections;

namespace Engine.Scripts.Data{
    /// <summary>
    ///     LGT-02: light properties per voxel id for the light jobs: attenuation (extra loss per step, 0–15,
    ///     15 = opaque) and emission (block light packed like <see cref="VoxelLight" />, sky 0). Built by the
    ///     voxel registry; tests and benchmarks build their own.
    /// </summary>
    public struct VoxelLightTable : IDisposable{
        [ReadOnly] public NativeArray<byte> Attenuation;
        [ReadOnly] public NativeArray<ushort> Emission;

        public VoxelLightTable(int voxelCount, Allocator allocator){
            Attenuation = new NativeArray<byte>(voxelCount, allocator);
            Emission = new NativeArray<ushort>(voxelCount, allocator);
        }

        public bool IsCreated => Attenuation.IsCreated;

        public int Count => Attenuation.Length;

        public void Set(int voxelId, byte attenuation, ushort emission){
            Attenuation[voxelId] = attenuation;
            Emission[voxelId] = emission;
        }

        public void Dispose(){
            if (Attenuation.IsCreated) Attenuation.Dispose();
            if (Emission.IsCreated) Emission.Dispose();
        }
    }
}
