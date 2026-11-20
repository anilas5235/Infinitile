using System;
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
    ///     Applies voxel-specific post-processing effects based on the voxel the target camera is currently inside:
    ///     the underwater fog of a liquid (no colour grading). A biome-tinted liquid takes the fog colour of the
    ///     biomes around the camera, as its surface takes their water colour.
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

        /// <summary>Width in blocks of the square the biome fog colour is averaged over, as the shader's ~16-block border.</summary>
        internal const int FogColorBlend = 16;

        /// <summary>Spacing in blocks of the columns sampled inside <see cref="FogColorBlend" /> (4 × 4 samples).</summary>
        internal const int FogColorStep = 4;

        private bool _biomeFog;
        private Vector2Int _fogColumn = new(int.MinValue, int.MinValue);
        private int _fogColorsVersion = -1;

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

            if (_biomeFog) UpdateBiomeFogColor(camPos.x, camPos.z);

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

        /// <summary>The biome fog colour at the camera's column, again only when the column or the colours changed.</summary>
        private void UpdateBiomeFogColor(int x, int z){
            VoxelWorld world = VoxelWorld.Instance;
            Vector2Int column = new(x, z);
            if (column == _fogColumn && world.BiomeColorsVersion == _fogColorsVersion) return;
            _fogColumn = column;
            _fogColorsVersion = world.BiomeColorsVersion;
            UnderwaterFogFeature.FogColor = AverageBiomeColor(x, z, world.TryGetColumnBiome,
                world.BiomeWaterFogColor, UnderwaterFogFeature.FogColor);
        }

        /// <summary>Looks up the biome of column (x, z); false when it is not loaded.</summary>
        internal delegate bool ColumnBiome(int x, int z, out int biome);

        /// <summary>
        ///     Mean of <paramref name="colorOf" /> over the <see cref="FogColorBlend" /> × <see cref="FogColorBlend" />
        ///     square around (<paramref name="x" />, <paramref name="z" />), sampled every <see cref="FogColorStep" />
        ///     blocks (at the centres of the step cells) and averaged in linear light; the columns that are not loaded
        ///     are left out, <paramref name="fallback" /> when none is.
        /// </summary>
        internal static Color AverageBiomeColor(int x, int z, ColumnBiome biomeAt, Func<int, Color> colorOf,
            Color fallback){
            Color sum = Color.clear;
            int count = 0;
            int first = -FogColorBlend / 2 + FogColorStep / 2; // -6, -2, 2, 6
            for (int dx = first; dx < FogColorBlend / 2; dx += FogColorStep)
            for (int dz = first; dz < FogColorBlend / 2; dz += FogColorStep){
                if (!biomeAt(x + dx, z + dz, out int biome)) continue;
                sum += colorOf(biome).linear;
                count++;
            }

            if (count == 0) return fallback;
            Color mean = (sum / count).gamma;
            mean.a = 1f;
            return mean;
        }

        private void OnEnable(){
            _voxelRegistry = DataImporter.Instance.VoxelRegistry;
        }

        private void OnDisable(){
            // The fog state is static and would outlive Play mode.
            UnderwaterFogFeature.Enabled = false;
            currentVoxelId = 0;
            _biomeFog = false;
        }

        /// <summary>
        ///     Updates post-processing state based on the voxel definition associated with the given ID.
        ///     Clears the previous fog and only applies it for transparent voxels with post-process fog.
        /// </summary>
        /// <param name="voxelId">Voxel ID at the camera position.</param>
        private void UpdatePostProcessing(ushort voxelId){
            UnderwaterFogFeature.Enabled = false;
            UnderwaterFogFeature.SelfIllumination = 0f;
            _biomeFog = false;

            if (!_voxelRegistry.TryGet(voxelId, out Voxel info)) return;

            if (info.meshLayer != MeshLayer.Transparent) return;
            if (!info.usePostProcess) return;

            UnderwaterFogFeature.Enabled = info.postProcess.enableFog;
            UnderwaterFogFeature.FogColor = info.postProcess.fogColor;
            _biomeFog = info.postProcess.enableFog && info.liquidSurface is{ biomeTint: true } &&
                        info.category == VoxelCategory.Liquids;
            _fogColumn = new Vector2Int(int.MinValue, int.MinValue); // the biome colour is looked up afresh
            UnderwaterFogFeature.FogDensity = info.postProcess.fogDensity;
            UnderwaterFogFeature.DepthDarkening = info.postProcess.depthDarkening;
            UnderwaterFogFeature.SceneTint = info.postProcess.sceneTint;
            Vector3Int emission = info.lightEmission;
            UnderwaterFogFeature.SelfIllumination = Mathf.Max(emission.x, emission.y, emission.z) / 15f;
        }
    }
}
