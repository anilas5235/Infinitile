using System.Collections.Generic;
using VoxelSo = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Block ids for preview configs, which have no voxel registry: each voxel asset gets the next free id on
    ///     first use, 0 stays air. Ids are stable for the lifetime of the table, so they never match the engine's.
    /// </summary>
    internal sealed class LocalVoxelIds{
        private readonly Dictionary<VoxelSo, ushort> _ids = new();
        private readonly List<VoxelSo> _voxels = new(){ null };

        /// <summary>Number of ids handed out, plus one for air.</summary>
        public int Count => _voxels.Count;

        public ushort Id(VoxelSo voxel){
            if (_ids.TryGetValue(voxel, out ushort id)) return id;
            id = (ushort)_voxels.Count;
            _ids.Add(voxel, id);
            _voxels.Add(voxel);
            return id;
        }

        /// <summary>The voxel asset of <paramref name="id" />; null for air and unknown ids.</summary>
        public VoxelSo Voxel(ushort id){
            return id < _voxels.Count ? _voxels[id] : null;
        }
    }
}
