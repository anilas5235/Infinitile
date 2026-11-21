using System.Collections.Generic;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Commands{
    /// <summary>
    ///     <see cref="ICommandWorld" /> over the running <see cref="VoxelWorld" /> and the voxel registry: the base
    ///     voxels by full id (turned variants are placed from their base), and <see cref="VoxelWorld.SetVoxel" />.
    /// </summary>
    public sealed class VoxelWorldCommands : ICommandWorld{
        private int _indexedCount = -1;
        private VoxelNameIndex _voxels = new(System.Array.Empty<(ushort, string)>());

        /// <summary>Built anew when the registry's base voxel count changes (it fills in the DataImporter's Awake).</summary>
        public VoxelNameIndex Voxels{
            get{
                VoxelRegistry registry = DataImporter.Instance ? DataImporter.Instance.VoxelRegistry : null;
                int count = registry?.BaseCount ?? 0;
                if (count != _indexedCount){
                    _indexedCount = count;
                    _voxels = new VoxelNameIndex(BaseVoxels(registry));
                }

                return _voxels;
            }
        }

        public int Height => VoxelConstants.ChunkHeight;

        public bool IsLoaded(Vector3Int position){
            VoxelWorld world = VoxelWorld.Instance;
            return world && world.IsChunkLoaded(new int3(position.x, position.y, position.z));
        }

        public bool SetVoxel(ushort id, Vector3Int position){
            VoxelWorld world = VoxelWorld.Instance;
            return world && world.SetVoxel(id, position);
        }

        private static IEnumerable<(ushort, string)> BaseVoxels(VoxelRegistry registry){
            if (registry == null) yield break;
            foreach (KeyValuePair<ushort, FixedString32Bytes> entry in registry.GetAllEntries())
                if (entry.Key < registry.BaseCount)
                    yield return (entry.Key, entry.Value.ToString());
        }
    }
}
