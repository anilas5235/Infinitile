using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Engine.Scripts.Render.Post{
    /// <summary>
    ///     Draws the voxel glass (the <see cref="LightMode" /> pass of TransparentVoxel.shader) after the skybox and
    ///     before URP copies the opaque colour. A liquid surface shows the opaque colour copy as the scene behind it,
    ///     so glass under water stays visible from above; drawn after the skybox, the glass blends over the sky.
    ///     Without this feature in the renderer, glass is not drawn.
    /// </summary>
    public class VoxelGlassFeature : ScriptableRendererFeature{
        /// <summary>The LightMode tag of the glass pass.</summary>
        public const string LightMode = "VoxelGlass";

        private GlassPass _pass;

        public override void Create(){
            _pass = new GlassPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData){
            renderer.EnqueuePass(_pass);
        }

        private class GlassPass : ScriptableRenderPass{
            private static readonly ShaderTagId GlassTag = new(LightMode);

            public GlassPass(){
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData){
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();

                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(GlassTag, renderingData, cameraData,
                    lightData, SortingCriteria.CommonTransparent);
                FilteringSettings filtering = new(RenderQueueRange.all);
                RendererListParams listParams = new(renderingData.cullResults, drawing, filtering);

                using IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass("Voxel Glass", out PassData data);
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
