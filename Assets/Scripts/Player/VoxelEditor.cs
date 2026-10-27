using System;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    ///     Simple in-game voxel editor that allows the player to destroy and place voxels
    ///     in the world at the point they are looking at.
    /// </summary>
    public class VoxelEditor : MonoBehaviour{
        /// <summary>
        ///     If true, destruction mode is active and targeted voxels will be removed.
        /// </summary>
        public bool destruct;

        /// <summary>
        ///     If true, placement mode is active and voxels will be placed instead of removed.
        /// </summary>
        public bool place;

        /// <summary>
        ///     Currently selected voxel ID that will be placed in placement mode; always a base voxel, turned on
        ///     placement to face the way it was placed.
        /// </summary>
        public ushort voxelId = 1;

        public LayerMask voxelLayerMask;
        private Camera _camera;

        private void FixedUpdate(){
            if (destruct){
                if (!GetLookAtVoxelPos(out Vector3Int voxelWorldPos, out _)) return;
                if (IsUnbreakable(VoxelWorld.Instance.GetVoxel(voxelWorldPos.Int3()))) return;
                VoxelWorld.Instance.SetVoxel(0, voxelWorldPos);
            }
            else if (place){
                if (!GetLookAtVoxelPos(out Vector3Int voxelWorldPos, out RaycastHit hitInfo, true)) return;
                if (Physics.CheckBox(voxelWorldPos + Vector3.one * .5f, Vector3.one * 0.45f)) return;
                ushort id = VoxelPlacement.Resolve(DataImporter.Instance.VoxelRegistry, voxelId, hitInfo.normal,
                    hitInfo.point, _camera.transform.forward);
                VoxelWorld.Instance.SetVoxel(id, voxelWorldPos);
            }
        }

        /// <summary>True when the voxel (or the base of a turned variant) is flagged unbreakable.</summary>
        private static bool IsUnbreakable(ushort id){
            return DataImporter.Instance.VoxelRegistry.TryGet(id, out Engine.Scripts.VoxelConfig.Data.Voxel.Voxel so) &&
                   so.unbreakable;
        }

        private void OnEnable(){
            _camera = Camera.main;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos(){
            if (!enabled) return;
            if (Application.isPlaying && GetLookAtVoxelPos(out Vector3Int voxelWorldPos, out _)){
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(voxelWorldPos + Vector3.one * .5f, Vector3.one * 0.45f);
            }
        }
#endif

        /// <summary>
        ///     Raised whenever <see cref="voxelId" /> changes (for example to update UI).
        /// </summary>
        public event Action<ushort> OnVoxelIdChanged;

        /// <summary>
        ///     Input System callback for the primary action (attack) used to toggle destruction mode.
        /// </summary>
        /// <param name="value">Button state indicating whether destroy should be active.</param>
        public void OnAttack(InputValue value){
            destruct = value.isPressed;
        }

        /// <summary>
        ///     Input System callback to sample the voxel currently looked at and set it as the active <see cref="voxelId" />;
        ///     a turned voxel gives its base.
        /// </summary>
        /// <param name="value">Button state indicating whether selection is triggered.</param>
        public void OnSelectVoxel(InputValue value){
            if (!value.isPressed) return;
            if (!GetLookAtVoxelPos(out Vector3Int voxelWorldPos, out _)) return;
            SelectVoxel(DataImporter.Instance.VoxelRegistry.BaseOf(VoxelWorld.Instance.GetVoxel(voxelWorldPos.Int3())));
        }

        /// <summary>Makes <paramref name="id" /> the placed voxel (air is ignored) and raises <see cref="OnVoxelIdChanged" />.</summary>
        public void SelectVoxel(ushort id){
            if (id == 0 || id == voxelId) return;
            voxelId = id;
            OnVoxelIdChanged?.Invoke(voxelId);
        }

        /// <summary>
        ///     Input System callback to toggle voxel placement mode.
        /// </summary>
        /// <param name="value">Button state indicating whether placement should be active.</param>
        public void OnPlaceVoxel(InputValue value){
            place = value.isPressed;
        }

        /// <summary>
        ///     Calculates the world voxel position the camera is currently looking at within a short range.
        /// </summary>
        /// <param name="voxelWorldPos">Resulting voxel world position when the raycast hits.</param>
        /// <param name="hitInfo">The raycast hit on the voxel's face.</param>
        /// <param name="placeMode">If true, the position is offset for placement in front of the hit surface.</param>
        /// <returns><c>true</c> if a voxel position could be determined; otherwise, <c>false</c>.</returns>
        private bool GetLookAtVoxelPos(out Vector3Int voxelWorldPos, out RaycastHit hitInfo, bool placeMode = false){
            voxelWorldPos = Vector3Int.zero;
            bool res = Physics.Raycast(_camera.transform.position, _camera.transform.forward, out hitInfo,
                5f, voxelLayerMask);
            if (!res) return false;

            Vector3 worldPos = hitInfo.point + _camera.transform.forward * (.001f * (placeMode ? -1 : 1));
            voxelWorldPos = Vector3Int.FloorToInt(worldPos);
            return true;
        }
    }
}
