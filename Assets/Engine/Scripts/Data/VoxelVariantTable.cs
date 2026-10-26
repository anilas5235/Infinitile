using System;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;

namespace Engine.Scripts.Data{
    /// <summary>
    ///     Base voxel and orientation of every voxel id, and the variant id of every base and orientation, for Burst
    ///     code. Built by the voxel registry: base voxels keep their ids, their turned variants follow after all bases
    ///     (see <see cref="VoxelOrientation" />). Variant ids depend on the package, so callers resolve them here at
    ///     bake or test time and never store them.
    /// </summary>
    public struct VoxelVariantTable : IDisposable{
        [ReadOnly] private NativeArray<ushort> _baseOf;
        [ReadOnly] private NativeArray<byte> _orientationOf;

        // VoxelOrientation.Slots entries per base id; an orientation the base does not have holds the base id
        [ReadOnly] private NativeArray<ushort> _variants;

        public VoxelVariantTable(int voxelCount, int baseCount, Allocator allocator){
            _baseOf = new NativeArray<ushort>(voxelCount, allocator);
            _orientationOf = new NativeArray<byte>(voxelCount, allocator);
            _variants = new NativeArray<ushort>(baseCount * VoxelOrientation.Slots, allocator);
            for (int id = 0; id < voxelCount; id++) _baseOf[id] = (ushort)id;
            for (int i = 0; i < _variants.Length; i++) _variants[i] = (ushort)(i / VoxelOrientation.Slots);
        }

        public bool IsCreated => _baseOf.IsCreated;

        /// <summary>Number of voxel ids, variants included.</summary>
        public int Count => _baseOf.Length;

        /// <summary>Number of base voxel ids; every id below it is a base.</summary>
        public int BaseCount => _variants.Length / VoxelOrientation.Slots;

        /// <summary>Records a variant; the registry calls it once per variant id.</summary>
        public void SetVariant(ushort id, ushort baseId, byte orientation){
            _baseOf[id] = baseId;
            _orientationOf[id] = orientation;
            _variants[baseId * VoxelOrientation.Slots + orientation] = id;
        }

        /// <summary>The base voxel of an id; a base (or an unknown id) is its own base.</summary>
        public readonly ushort BaseOf(ushort id){
            return id < _baseOf.Length ? _baseOf[id] : id;
        }

        /// <summary>The orientation of an id; 0 for a base.</summary>
        public readonly byte OrientationOf(ushort id){
            return id < _orientationOf.Length ? _orientationOf[id] : (byte)0;
        }

        /// <summary>
        ///     The id of a base voxel in an orientation; the base id itself when the base has no such orientation (a
        ///     trunk voxel without an axis mode stays upright).
        /// </summary>
        public readonly ushort VariantOf(ushort baseId, byte orientation){
            int slot = baseId * VoxelOrientation.Slots + orientation;
            return orientation < VoxelOrientation.Slots && slot < _variants.Length ? _variants[slot] : baseId;
        }

        public void Dispose(){
            if (_baseOf.IsCreated) _baseOf.Dispose();
            if (_orientationOf.IsCreated) _orientationOf.Dispose();
            if (_variants.IsCreated) _variants.Dispose();
        }
    }
}
