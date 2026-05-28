using Engine.Scripts.VoxelConfig.Data;
using Engine.Scripts.World;
using UnityEngine;

namespace Engine.Shaders
{
    /// <summary>
    ///     Applies voxel-specific post-processing effects based on the voxel the target camera is currently inside.
    ///     Uses voxel post-process data for liquids to adjust color grading and fog.
    /// </summary>
    public class VoxelPostProcessHandler : MonoBehaviour
    {
        /// <summary>
        ///     Camera whose position is used to sample the current voxel.
        /// </summary>
        public Camera target;

        /// <summary>
        ///     Last voxel ID that was used to drive post-processing.
        /// </summary>
        public ushort currentVoxelId;

        private PostProcessManager _postProcessManager;

        private VoxelRegistry _voxelRegistry;

        private void FixedUpdate()
        {
            if (!target)
            {
                Debug.LogWarning("VoxelPostProcessHandler: Target camera is not assigned.");
                return;
            }

            Vector3 camPos = target.transform.position;

            ushort voxelId = VoxelWorld.Instance.GetVoxel(Vector3Int.FloorToInt(camPos));
            if (voxelId == currentVoxelId) return;

            currentVoxelId = voxelId;
            UpdatePostProcessing(currentVoxelId);
        }

        private void OnEnable()
        {
            _voxelRegistry = VoxelDataImporter.Instance.VoxelRegistry;
            _postProcessManager = PostProcessManager.Instance;
        }

        /// <summary>
        ///     Updates post-processing state based on the voxel definition associated with the given ID.
        ///     Resets previous overrides and only applies effects for transparent liquid voxels.
        /// </summary>
        /// <param name="voxelId">Voxel ID at the camera position.</param>
        private void UpdatePostProcessing(ushort voxelId)
        {
            _postProcessManager.ResetLiftGammaGain();
            _postProcessManager.ResetColorAdjustments();
            RenderSettings.fog = false;

            if (!_voxelRegistry.GetVoxelDefinition(voxelId, out VoxelDefinition info)) return;

            if (info.meshLayer != MeshLayer.Transparent) return;
            if (!info.usePostProcess) return;

            _postProcessManager.SetLiftGammaGain(info.postProcess.postProcessColor);
            _postProcessManager.SetColorAdjustments(info.postProcess.contrast, info.postProcess.saturation);
            RenderSettings.fog = info.postProcess.enableFog;
            RenderSettings.fogColor = info.postProcess.postProcessColor;
            RenderSettings.fogDensity = info.postProcess.fogDensity;
        }
    }
}