using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Engine.Shaders{
    /// <summary>
    ///     Full-screen distance fog for liquids, drawn after transparents and before URP post-processing.
    ///     Driven by <see cref="RenderSettings.fog" />, <see cref="RenderSettings.fogColor" /> and
    ///     <see cref="RenderSettings.fogDensity" />, which <see cref="VoxelPostProcessHandler" /> sets per liquid.
    ///     The fog is lit by the sky like an open voxel, so it follows day and night.
    /// </summary>
    public class UnderwaterFogFeature : ScriptableRendererFeature{
        /// <summary>
        ///     Minimum fog brightness (0-1) regardless of the sky, for liquids that emit light such as lava.
        /// </summary>
        public static float SelfIllumination;

        [SerializeField] private Shader shader;

        private Material _material;
        private FogPass _pass;

        public override void Create(){
            if (!shader) shader = Shader.Find("Hidden/Engine/UnderwaterFog");
            if (!shader) return;

            _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = new FogPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData){
            if (_pass == null || !RenderSettings.fog) return;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing){
            CoreUtils.Destroy(_material);
        }

        private class FogPass : ScriptableRenderPass{
            private static readonly int FogColorId = Shader.PropertyToID("_UnderwaterFogColor");
            private static readonly int FogDensityId = Shader.PropertyToID("_UnderwaterFogDensity");
            private static readonly int FogEmissionId = Shader.PropertyToID("_UnderwaterFogEmission");
            private static readonly int FogDepthId = Shader.PropertyToID("_UnderwaterFogDepth");

            private readonly Material _material;

            public FogPass(Material material){
                _material = material;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData){
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                // Only the player's view is inside the liquid; scene view and previews stay clear.
                if (cameraData.cameraType != CameraType.Game || resources.isActiveTargetBackBuffer) return;

                _material.SetColor(FogColorId, RenderSettings.fogColor);
                _material.SetFloat(FogDensityId, RenderSettings.fogDensity);
                _material.SetFloat(FogEmissionId, SelfIllumination);

                TextureHandle source = resources.activeColorTexture;
                TextureDesc desc = renderGraph.GetTextureDesc(source);
                desc.name = "_UnderwaterFogTarget";
                desc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(desc);

                // The depth attachment also holds the liquid's faces (transparents write depth), so the fog covers
                // only the way through the liquid; the depth texture (opaques only) is the fallback under MSAA.
                TextureHandle depth = resources.activeDepthTexture;
                if (!depth.IsValid() || renderGraph.GetTextureDesc(depth).msaaSamples != MSAASamples.None)
                    depth = resources.cameraDepthTexture;

                using (IRasterRenderGraphBuilder builder =
                       renderGraph.AddRasterRenderPass("Underwater Fog", out PassData data)){
                    data.Source = source;
                    data.Depth = depth;
                    data.Material = _material;
                    builder.UseTexture(source);
                    builder.UseTexture(depth);
                    builder.SetRenderAttachment(destination, 0);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => {
                        d.Material.SetTexture(FogDepthId, (RTHandle)d.Depth);
                        Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1, 1, 0, 0), d.Material, 0);
                    });
                }

                resources.cameraColor = destination;
            }

            private class PassData{
                public TextureHandle Depth;
                public Material Material;
                public TextureHandle Source;
            }
        }
    }
}
