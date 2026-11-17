using Engine.Scripts.Render.Post;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Unity.Mathematics;
using UnityEngine;

namespace Player{
    public class PlayerManager : MonoBehaviour{
        /// <summary>Gap between the feet and the ground when a held player that reaches into the ground is set down.</summary>
        private const float SetDownClearance = 0.25f;

        /// <summary>Free voxels above the ground a body needs to stand (the capsule is 1.8 high).</summary>
        private const int StandingRoom = 2;

        private CharacterController _body;
        private PlayerControllerManager _controllerManager;

        private bool _holding;
        private bool _playerEnabled = true;
        private VoxelPostProcessHandler _postProcessHandler;
        private bool _readyForPlayer;
        private VoxelEditor _voxelEditor;
        private VoxelRegistry _voxelRegistry;
        private VoxelWorld _world;

        private void Start(){
            DisablePlayer();
            if (_world) _world.DrawDistanceChanged += OnDrawDistanceChanged;
        }

        private void OnDestroy(){
            if (_world) _world.DrawDistanceChanged -= OnDrawDistanceChanged;
        }

        private void FixedUpdate(){
            if (_holding){
                TryReleaseHold();
                return;
            }

            if (_playerEnabled || _readyForPlayer || !ReadyForPlayer(out int3 firstCollidableVoxel)) return;

            transform.position = firstCollidableVoxel.GetVector3() + new Vector3(0.5f, 4f, 0.5f);
            _readyForPlayer = true;
            EnablePlayer();
        }

        private void OnEnable(){
            _body = GetComponent<CharacterController>();
            _controllerManager = GetComponent<PlayerControllerManager>();
            _voxelEditor = GetComponent<VoxelEditor>();
            _postProcessHandler = GetComponent<VoxelPostProcessHandler>();
            _world = VoxelWorld.Instance;
            _voxelRegistry = DataImporter.Instance.VoxelRegistry;
        }

        /// <summary>
        ///     Freezes the player where it is (no input, no gravity, no move) until the ground under it is loaded and
        ///     its collider is ready again, e.g. while the world is built anew at another draw distance, after
        ///     <see cref="VoxelWorld.Regenerate" /> or after a teleport. Then the player goes on from where it was; only
        ///     a body that reaches into the ground is set down just above it.
        /// </summary>
        public void HoldUntilGrounded(){
            DisablePlayer();
            _holding = true;
        }

        private void OnDrawDistanceChanged(int drawDistance){
            HoldUntilGrounded();
        }

        private void TryReleaseHold(){
            if (!TryFindGround(out float groundTop)) return;

            float feet = FeetY;
            if (feet < groundTop){
                Vector3 position = transform.position;
                transform.position = new Vector3(position.x, position.y + groundTop + SetDownClearance - feet, position.z);
            }

            _holding = false;
            EnablePlayer();
        }

        /// <summary>World height of the bottom of the player's capsule.</summary>
        private float FeetY => transform.position.y + (_body.center.y - _body.height * 0.5f) * transform.lossyScale.y;

        /// <summary>
        ///     The top of the ground the player stands on or over, once the voxel data is loaded and the partition of
        ///     that ground has its collider. The ground is the first collidable voxel down from the head; if that
        ///     voxel reaches above the feet, the body is in the ground and the ground is the floor of the first free
        ///     space above it that the body fits in.
        /// </summary>
        private bool TryFindGround(out float groundTop){
            groundTop = 0f;
            Vector3 position = transform.position;
            if (!_world.IsChunkLoaded(position.Int3())) return false;

            float feet = FeetY;
            int top = VoxelConstants.ChunkHeight - 1;
            int3 ground = new((int)math.floor(position.x), 0, (int)math.floor(position.z));
            ground.y = math.min((int)math.floor(feet + _body.height * transform.lossyScale.y), top);
            while (ground.y >= 0 && !IsSolid(ground)) ground.y--;
            if (ground.y < 0) return false;

            if (ground.y + 1 > feet){
                // in the ground: climb to the first gap the body fits in
                int free = 0;
                int3 probe = ground;
                while (probe.y < top && free < StandingRoom){
                    probe.y++;
                    if (IsSolid(probe)){
                        free = 0;
                        ground.y = probe.y;
                    }
                    else{
                        free++;
                    }
                }
            }

            if (!_world.IsCollidable(VoxelConstants.WorldToPartitionPos(ground))) return false;
            groundTop = ground.y + 1;
            return true;
        }

        /// <summary>Whether the voxel there stops the player; an id the registry does not know counts as solid.</summary>
        private bool IsSolid(int3 position){
            ushort voxelId = _world.GetVoxel(position);
            if (voxelId == 0) return false;
            return !_voxelRegistry.TryGet(voxelId, out Voxel voxelDef) || voxelDef.collision;
        }

        public void DisablePlayer(){
            if (!_playerEnabled) return;
            _playerEnabled = false;
            _controllerManager.enabled = false;
            _voxelEditor.enabled = false;
            _postProcessHandler.enabled = false;
        }

        public void EnablePlayer(){
            if (_playerEnabled) return;
            _playerEnabled = true;
            _controllerManager.enabled = true;
            _voxelEditor.enabled = true;
            _postProcessHandler.enabled = true;
        }

        private bool ReadyForPlayer(out int3 firstCollidableVoxel){
            int3 playerPos = transform.position.Int3();
            firstCollidableVoxel = playerPos + new int3(0, 1, 0);
            if (!_world.IsChunkLoaded(playerPos)) return false;

            firstCollidableVoxel.y = math.min(firstCollidableVoxel.y, VoxelConstants.ChunkHeight - 1);
            for (; firstCollidableVoxel.y >= 0; firstCollidableVoxel.y--){
                ushort voxelId = _world.GetVoxel(firstCollidableVoxel);
                if (voxelId == 0) continue;

                if (_voxelRegistry.TryGet(voxelId, out Voxel voxelDef) &&
                    !voxelDef.collision) continue;

                return _world.IsCollidable(VoxelConstants.WorldToPartitionPos(firstCollidableVoxel));
            }

            return false;
        }
    }
}
