using System;
using Engine.Scripts.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player
{
    /// <summary>
    /// Simple in-game voxel editor that allows the player to destroy and place voxels
    /// in the world at the point they are looking at.
    /// </summary>
    public class VoxelEditor : MonoBehaviour
    {
        private Camera _camera;

        /// <summary>
        /// If true, destruction mode is active and targeted voxels will be removed.
        /// </summary>
        public bool destruct;

        /// <summary>
        /// If true, placement mode is active and voxels will be placed instead of removed.
        /// </summary>
        public bool place;

        /// <summary>
        /// Currently selected voxel ID that will be placed in placement mode.
        /// </summary>
        public ushort voxelId = 1;
        
        public LayerMask voxelLayerMask;

        /// <summary>
        /// Raised whenever <see cref="voxelId"/> changes (for example to update UI).
        /// </summary>
        public event Action<ushort> OnVoxelIdChanged;

        private void OnEnable()
        {
            _camera = Camera.main;
        }

        /// <summary>
        /// Input System callback for the primary action (attack) used to toggle destruction mode.
        /// </summary>
        /// <param name="value">Button state indicating whether destroy should be active.</param>
        public void OnAttack(InputValue value)
        {
            destruct = value.isPressed;
        }

        /// <summary>
        /// Input System callback to sample the voxel currently looked at and set it as the active <see cref="voxelId"/>.
        /// </summary>
        /// <param name="value">Button state indicating whether selection is triggered.</param>
        public void OnSelectVoxel(InputValue value)
        {
            if (!value.isPressed) return;
            if (!GetLookAtVoxelPos(out Vector3Int voxelWorldPos)) return;
            ushort voxId = VoxelWorld.Instance.GetVoxel(voxelWorldPos);
            if (voxId > 0 && voxId != voxelId)
            {
                voxelId = voxId;
                OnVoxelIdChanged?.Invoke(voxelId);
            }
        }

        /// <summary>
        /// Input System callback to toggle voxel placement mode.
        /// </summary>
        /// <param name="value">Button state indicating whether placement should be active.</param>
        public void OnPlaceVoxel(InputValue value)
        {
            place = value.isPressed;
        }

        private void FixedUpdate()
        {
            if (destruct)
            {
                if (!GetLookAtVoxelPos(out Vector3Int voxelWorldPos)) return;
                VoxelWorld.Instance.SetVoxel(0, voxelWorldPos);
            }
            else if (place)
            {
                if (!GetLookAtVoxelPos(out Vector3Int voxelWorldPos, true)) return;
                if (Physics.CheckBox(voxelWorldPos+ Vector3.one * .5f , Vector3.one * 0.45f)) return;
                VoxelWorld.Instance.SetVoxel(voxelId, voxelWorldPos);
            }
        }

        /// <summary>
        /// Calculates the world voxel position the camera is currently looking at within a short range.
        /// </summary>
        /// <param name="voxelWorldPos">Resulting voxel world position when the raycast hits.</param>
        /// <param name="placeMode">If true, the position is offset for placement in front of the hit surface.</param>
        /// <returns><c>true</c> if a voxel position could be determined; otherwise, <c>false</c>.</returns>
        private bool GetLookAtVoxelPos(out Vector3Int voxelWorldPos, bool placeMode = false)
        {
            voxelWorldPos = Vector3Int.zero;
            bool res = Physics.Raycast(_camera.transform.position, _camera.transform.forward, out RaycastHit hitInfo,
                5f,voxelLayerMask);
            if (!res) return false;

            Vector3 worldPos = hitInfo.point + _camera.transform.forward * (.001f * (placeMode ? -1 : 1));
            voxelWorldPos = Vector3Int.FloorToInt(worldPos);
            return true;
        }
        
#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (Application.isPlaying && GetLookAtVoxelPos(out Vector3Int voxelWorldPos))
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(voxelWorldPos + Vector3.one * .5f , Vector3.one * 0.45f);
            }
        }
#endif
    }
}