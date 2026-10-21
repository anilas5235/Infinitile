using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using UnityEngine;

namespace Engine.Shaders{
    /// <summary>
    ///     Applies voxel-specific post-processing effects based on the voxel the target camera is currently inside.
    ///     Uses voxel post-process data for liquids to adjust color grading and fog.
    /// </summary>
    public class VoxelPostProcessHandler : MonoBehaviour{
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

        private void FixedUpdate(){
            if (!target){
                Debug.LogWarning("VoxelPostProcessHandler: Target camera is not assigned.");
                return;
            }

            Vector3 camPos = target.transform.position;

            ushort voxelId = VoxelWorld.Instance.GetVoxel(Vector3Int.FloorToInt(camPos).Int3());
            if (voxelId == currentVoxelId) return;

            currentVoxelId = voxelId;
            UpdatePostProcessing(currentVoxelId);
        }

        private void OnEnable(){
            _voxelRegistry = DataImporter.Instance.VoxelRegistry;
            _postProcessManager = PostProcessManager.Instance;
        }

        private void OnDisable(){
            // The fog state is static and would outlive Play mode.
            UnderwaterFogFeature.Enabled = false;
            currentVoxelId = 0;
        }

        /// <summary>
        ///     Updates post-processing state based on the voxel definition associated with the given ID.
        ///     Resets previous overrides and only applies effects for transparent liquid voxels.
        /// </summary>
        /// <param name="voxelId">Voxel ID at the camera position.</param>
        private void UpdatePostProcessing(ushort voxelId){
            _postProcessManager.ResetLiftGammaGain();
            _postProcessManager.ResetColorAdjustments();
            UnderwaterFogFeature.Enabled = false;
            UnderwaterFogFeature.SelfIllumination = 0f;

            if (!_voxelRegistry.TryGet(voxelId, out Voxel info)) return;

            if (info.meshLayer != MeshLayer.Transparent) return;
            if (!info.usePostProcess) return;

            _postProcessManager.SetLiftGammaGain(info.postProcess.postProcessColor);
            _postProcessManager.SetColorAdjustments(info.postProcess.contrast, info.postProcess.saturation);
            UnderwaterFogFeature.Enabled = info.postProcess.enableFog;
            UnderwaterFogFeature.FogColor = info.postProcess.fogColor;
            UnderwaterFogFeature.FogDensity = info.postProcess.fogDensity;
            Vector3Int emission = info.lightEmission;
            UnderwaterFogFeature.SelfIllumination = Mathf.Max(emission.x, emission.y, emission.z) / 15f;
        }
    }
}
