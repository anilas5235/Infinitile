using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Mathematics;

namespace Engine.Scripts.World{
    /// <summary>
    ///     Picks the orientation of a voxel the player places, like Minecraft: a log lies along the axis of the clicked
    ///     face, a facing voxel turns its front to the player, a top/bottom voxel goes to the half that was clicked.
    ///     Pure functions of the click; they read no world state.
    /// </summary>
    public static class VoxelPlacement{
        /// <summary>
        ///     The voxel id to place for a base voxel: its variant for the click. A variant passed in is placed as its
        ///     base would be; a voxel without facing or top/bottom stays as it is.
        /// </summary>
        /// <param name="registry">The finalised voxel registry.</param>
        /// <param name="baseId">The voxel to place.</param>
        /// <param name="hitNormal">Normal of the clicked face.</param>
        /// <param name="hitPoint">Clicked point in world space.</param>
        /// <param name="lookDir">The player's view direction.</param>
        public static ushort Resolve(VoxelRegistry registry, ushort baseId, float3 hitNormal, float3 hitPoint,
            float3 lookDir){
            baseId = registry.BaseOf(baseId);
            if (!registry.TryGet(baseId, out Voxel voxel)) return baseId;
            return registry.VariantOf(baseId,
                Orientation(voxel.facing, voxel.topBottom, hitNormal, hitPoint, lookDir));
        }

        /// <summary>
        ///     The orientation (see <see cref="VoxelOrientation" />) for a click. Axis: the axis of the clicked face's
        ///     normal. Horizontal: the front faces the player, from the larger horizontal component of −lookDir.
        ///     Full: the same with y included, so looking steeply down gives front +Y. Top/bottom: the top face of a
        ///     block gives the lower half, its bottom face the upper half, and a side face the half the hit point lies
        ///     in. Ties break horizontal before vertical, then X before Z.
        /// </summary>
        public static byte Orientation(VoxelFacing facing, bool topBottom, float3 hitNormal, float3 hitPoint,
            float3 lookDir){
            int facingIndex = facing switch{
                VoxelFacing.Axis => Dominant(math.abs(hitNormal), true) switch{
                    0 => VoxelOrientation.AxisX,
                    1 => VoxelOrientation.AxisY,
                    _ => VoxelOrientation.AxisZ
                },
                VoxelFacing.Horizontal => Front(-lookDir, false),
                VoxelFacing.Full => Front(-lookDir, true),
                _ => 0
            };

            bool upper = topBottom && (hitNormal.y < -0.5f ||
                                       (hitNormal.y <= 0.5f && hitPoint.y - math.floor(hitPoint.y) > 0.5f));
            return VoxelOrientation.Make(facingIndex, upper);
        }

        /// <summary>Front facing towards <paramref name="toPlayer" />, from its dominant component.</summary>
        private static byte Front(float3 toPlayer, bool withY){
            return Dominant(math.abs(toPlayer), withY) switch{
                0 => toPlayer.x >= 0f ? VoxelOrientation.FrontPosX : VoxelOrientation.FrontNegX,
                1 => toPlayer.y >= 0f ? VoxelOrientation.FrontPosY : VoxelOrientation.FrontNegY,
                _ => toPlayer.z >= 0f ? VoxelOrientation.FrontPosZ : VoxelOrientation.FrontNegZ
            };
        }

        /// <summary>Axis (0 x, 1 y, 2 z) of the largest component; ties go to x, then z, then y.</summary>
        private static int Dominant(float3 size, bool withY){
            float horizontal = math.max(size.x, size.z);
            if (withY && size.y > horizontal) return 1;
            return size.x >= size.z ? 0 : 2;
        }
    }
}
