using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Unity.Collections;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

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

            Vector3 position = target.transform.position;
            Vector3Int camPos = Vector3Int.FloorToInt(position);

            // Base voxel: a turned variant shows its base's effects
            ushort voxelId = _voxelRegistry.BaseOf(VoxelWorld.Instance.GetVoxel(camPos.Int3()));

            // The top layer of a liquid is drawn LiquidSurfaceDrop lower: above its surface the camera is in the
            // voxel above
            if (position.y - camPos.y > 1f - LiquidSurfaceDrop){
                ushort above = _voxelRegistry.BaseOf(VoxelWorld.Instance.GetVoxel((camPos + Vector3Int.up).Int3()));
                if (IsLiquidTop(_voxelRegistry, voxelId, above)) voxelId = above;
            }
            if (voxelId != currentVoxelId){
                currentVoxelId = voxelId;
                UpdatePostProcessing(currentVoxelId);
            }

            if (UnderwaterFogFeature.Enabled)
                UnderwaterFogFeature.SurfaceHeight = FindSurface(_voxelRegistry, camPos, voxelId);
        }

        /// <summary>
        ///     Height of the liquid's surface above <paramref name="camPos" />: the bottom of the first voxel up the
        ///     column that is not <paramref name="liquid" /> (a base voxel id), less the drop of a liquid's top layer.
        /// </summary>
        private static float FindSurface(VoxelRegistry registry, Vector3Int camPos, ushort liquid){
            VoxelWorld world = VoxelWorld.Instance;
            int y = camPos.y + 1;
            int top = camPos.y + MaxSurfaceSearch;
            ushort above = liquid;
            while (y < top){
                above = registry.BaseOf(world.GetVoxel(new Vector3Int(camPos.x, y, camPos.z).Int3()));
                if (above != liquid) break;
                y++;
            }

            return IsLiquidTop(registry, liquid, above) ? y - LiquidSurfaceDrop : y;
        }

        /// <summary>
        ///     True when <paramref name="voxelId" /> (a base id) is a liquid whose surface is drawn lower with
        ///     <paramref name="above" /> (a base id) on top of it: another voxel that is not a full solid block, so a
        ///     slab or another partial solid shape still lowers the surface. Same rule as PointBuilder.compute,
        ///     is_liquid_top.
        /// </summary>
        internal static bool IsLiquidTop(VoxelRegistry registry, ushort voxelId, ushort above){
            if (above == voxelId) return false;
            NativeArray<Voxel.VoxelDef> defs = registry.GetVoxelGenData().VoxelRenderDefs;
            if (!defs.IsCreated || voxelId >= defs.Length || !defs[voxelId].Liquid) return false;
            if (above >= defs.Length) return true;
            Voxel.VoxelDef top = defs[above];
            return top.MeshLayer != MeshLayer.Solid || !top.HidesNeighbourFaces;
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
