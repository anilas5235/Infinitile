using System.Runtime.InteropServices;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     CUL-01: world-space AABB of one render page, taken from the partition that owns the page. Mirrors the
    ///     <c>PageBounds</c> struct in <c>PageCulling.compute</c> (two float3, 24 bytes); the GPU copy lives next to
    ///     <c>_CountsPerPage</c> in every <see cref="RenderBuffer" />.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PageBounds{
        public readonly float3 Min;
        public readonly float3 Max;

        public PageBounds(float3 min, float3 max){
            Min = min;
            Max = max;
        }

        /// <summary>
        ///     Bounds of every point a partition can produce: the points are built in world space from the partition
        ///     origin, and a quad corner reaches at most one voxel past the partition's last voxel.
        /// </summary>
        public static PageBounds FromPartition(int3 partitionPos){
            float3 min = PartitionToWorldPos(partitionPos);
            return new PageBounds(min, min + (float3)PartitionSize);
        }

        /// <summary>
        ///     CUL-05: the box swept along <paramref name="extrusion" /> (main light direction times shadow distance).
        ///     Everything inside it can cast a shadow onto the original box, so a page whose extruded bounds touch the
        ///     frustum may shadow visible geometry. A zero extrusion returns the bounds unchanged.
        /// </summary>
        public PageBounds Extrude(float3 extrusion){
            return new PageBounds(math.min(Min, Min + extrusion), math.max(Max, Max + extrusion));
        }

        public override string ToString(){
            return $"[{Min} .. {Max}]";
        }
    }
}
