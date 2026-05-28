using Engine.Scripts.VoxelConfig.Data;
using Player;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Simple UI widget that displays the texture of the currently selected voxel
    /// and listens to changes from the <see cref="VoxelEditor"/>.
    /// </summary>
    public class VoxelSelect : MonoBehaviour
    {
        /// <summary>
        /// UI element that shows the preview texture of the selected voxel.
        /// </summary>
        public RawImage voxelImage;

        /// <summary>
        /// Currently selected voxel ID mirrored from the <see cref="VoxelEditor"/>.
        /// </summary>
        public ushort voxelId;

        /// <summary>
        /// Reference to the voxel editor that raises selection change events.
        /// </summary>
        public VoxelEditor voxelEditor;

        private void OnEnable()
        {
            voxelEditor.OnVoxelIdChanged += OnVoxelIdChanged;
            OnVoxelIdChanged(voxelEditor.voxelId);
        }

        private void OnDisable()
        {
            voxelEditor.OnVoxelIdChanged -= OnVoxelIdChanged;
        }

        /// <summary>
        /// Updates the stored voxel ID and UI texture whenever the editor selection changes.
        /// </summary>
        /// <param name="newVoxelId">Newly selected voxel ID.</param>
        private void OnVoxelIdChanged(ushort newVoxelId)
        {
            voxelId = newVoxelId;
            if (VoxelDataImporter.Instance.VoxelRegistry.GetVoxelDefinition(voxelId, out VoxelDefinition definition))
            {
                Texture2D tex = definition.GetDisplayTexture(QuadDrawCondition.Forward);
                voxelImage.texture = tex;
            }
            else
            {
                voxelImage.texture = null;
            }
        }
    }
}