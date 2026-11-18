using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Engine.Scripts.Render.Post{
    /// <summary>
    ///     Draws the voxel glass (the <see cref="LightMode" /> pass of TransparentVoxel.shader) after the skybox and
    ///     before URP copies the opaque colour. A liquid surface shows the opaque colour copy as the scene behind it,
    ///     so glass under water stays visible from above; drawn after the skybox, the glass blends over the sky.
    ///     The glass writes no depth, so a liquid surface behind it is still drawn; after the transparents the
    ///     <see cref="OverLightMode" /> pass draws the glass again on the pixels a liquid covers (stencil) where the
    ///     glass lies in front of the liquid's surface (depth test), so the liquid does not hide it.
    ///     Without this feature in the renderer, glass is not drawn.
    /// </summary>
    public class VoxelGlassFeature : ScriptableRendererFeature{
        /// <summary>The LightMode tag of the glass pass.</summary>
        public const string LightMode = "VoxelGlass";

        /// <summary>The LightMode tag of the glass pass drawn over the liquids.</summary>
        public const string OverLightMode = "VoxelGlassOver";

        private GlassPass _pass;
        private GlassPass _overPass;

        public override void Create(){
            _pass = new GlassPass(LightMode, "Voxel Glass", RenderPassEvent.AfterRenderingSkybox);
            _overPass = new GlassPass(OverLightMode, "Voxel Glass Over Liquids",
                RenderPassEvent.AfterRenderingTransparents);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData){
            renderer.EnqueuePass(_pass);
            renderer.EnqueuePass(_overPass);
        }

        private class GlassPass : ScriptableRenderPass{
            private readonly ShaderTagId _tag;
            private readonly string _name;

            public GlassPass(string lightMode, string name, RenderPassEvent passEvent){
                _tag = new ShaderTagId(lightMode);
                _name = name;
                renderPassEvent = passEvent;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData){
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();

                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(_tag, renderingData, cameraData,
                    lightData, SortingCriteria.CommonTransparent);
                FilteringSettings filtering = new(RenderQueueRange.all);
                RendererListParams listParams = new(renderingData.cullResults, drawing, filtering);

                using IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass(_name, out PassData data);
                data.List = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(data.List);
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => ctx.cmd.DrawRendererList(d.List));
            }

            private class PassData{
                public RendererListHandle List;
            }
        }
    }
}
