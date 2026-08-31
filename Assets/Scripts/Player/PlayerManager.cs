using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Engine.Shaders;
using Unity.Mathematics;
using UnityEngine;

namespace Player
{
    public class PlayerManager : MonoBehaviour
    {
        private PlayerControllerManager _controllerManager;
        private VoxelEditor _voxelEditor;
        private VoxelPostProcessHandler _postProcessHandler;

        private bool _playerEnabled = true;
        private VoxelWorld _world;
        private VoxelRegistry _voxelRegistry;
        private bool _readyForPlayer;

        private void OnEnable()
        {
            _controllerManager = GetComponent<PlayerControllerManager>();
            _voxelEditor = GetComponent<VoxelEditor>();
            _postProcessHandler = GetComponent<VoxelPostProcessHandler>();
            _world = VoxelWorld.Instance;
            _voxelRegistry = DataImporter.Instance.VoxelRegistry;
        }

        private void Start()
        {
            DisablePlayer();
        }

        private void FixedUpdate()
        {
            if (_playerEnabled || _readyForPlayer || !ReadyForPlayer(out int3 firstCollidableVoxel)) return;
            
            transform.position = firstCollidableVoxel.GetVector3() + new Vector3(0.5f, 4f, 0.5f);
            _readyForPlayer = true;
            EnablePlayer();
        }

        public void DisablePlayer()
        {
            if (!_playerEnabled) return;
            _playerEnabled = false;
            _controllerManager.enabled = false;
            _voxelEditor.enabled = false;
            _postProcessHandler.enabled = false;
        }

        public void EnablePlayer()
        {
            if (_playerEnabled) return;
            _playerEnabled = true;
            _controllerManager.enabled = true;
            _voxelEditor.enabled = true;
            _postProcessHandler.enabled = true;
        }

        private bool ReadyForPlayer(out int3 firstCollidableVoxel)
        {
            int3 playerPos = transform.position.Int3();
            firstCollidableVoxel = playerPos + new int3(0, 1, 0);
            if (!_world.ChunkManager.IsChunkLoaded(VoxelConstants.WorldToChunkPos(playerPos))) return false;

            firstCollidableVoxel.y = math.min(firstCollidableVoxel.y, VoxelConstants.ChunkHeight);
            bool collidableVoxelFound = false;
            do
            {
                ushort voxelId = _world.GetVoxel(firstCollidableVoxel);
                firstCollidableVoxel.y--;
                if (voxelId == 0) continue;

                if (_voxelRegistry.TryGet(voxelId, out Voxel voxelDef) &&
                    !voxelDef.collision) continue;

                collidableVoxelFound = true;
            } while (!collidableVoxelFound && firstCollidableVoxel.y > 0);

            if (!collidableVoxelFound) return false;

            return _world.IsCollidable(VoxelConstants.WorldToPartitionPos(firstCollidableVoxel));
        }
    }
}