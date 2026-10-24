using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using UnityEngine;

namespace Engine.Scripts.Render.Post{
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

        /// <summary>
        ///     How far up the surface of the liquid is searched for the depth darkening; deeper counts as this deep.
        /// </summary>
        private const int MaxSurfaceSearch = 64;

        private PostProcessManager _postProcessManager;

        private VoxelRegistry _voxelRegistry;

        private void FixedUpdate(){
            if (!target){
                Debug.LogWarning("VoxelPostProcessHandler: Target camera is not assigned.");
                return;
            }

            Vector3Int camPos = Vector3Int.FloorToInt(target.transform.position);

            ushort voxelId = VoxelWorld.Instance.GetVoxel(camPos.Int3());
            if (voxelId != currentVoxelId){
                currentVoxelId = voxelId;
                UpdatePostProcessing(currentVoxelId);
            }

            if (UnderwaterFogFeature.Enabled) UnderwaterFogFeature.SurfaceHeight = FindSurface(camPos, voxelId);
        }

        /// <summary>
        ///     Height of the liquid's surface above <paramref name="camPos" />: the bottom of the first voxel up the
        ///     column that is not <paramref name="liquid" />.
        /// </summary>
        private static float FindSurface(Vector3Int camPos, ushort liquid){
            VoxelWorld world = VoxelWorld.Instance;
            int y = camPos.y + 1;
            int top = camPos.y + MaxSurfaceSearch;
            while (y < top && world.GetVoxel(new Vector3Int(camPos.x, y, camPos.z).Int3()) == liquid) y++;
            return y;
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
            UnderwaterFogFeature.DepthDarkening = info.postProcess.depthDarkening;
            Vector3Int emission = info.lightEmission;
            UnderwaterFogFeature.SelfIllumination = Mathf.Max(emission.x, emission.y, emission.z) / 15f;
        }
    }
}
