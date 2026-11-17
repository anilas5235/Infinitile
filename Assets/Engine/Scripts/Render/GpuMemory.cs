using UnityEngine;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     The GPU memory of the engine's own buffers, in bytes, as the renderer reads it at one moment. Counts the
    ///     buffers the engine creates (count × stride); textures, the render targets of the pipeline and the driver's
    ///     own memory are not in it.
    /// </summary>
    public readonly struct GpuMemory{
        /// <summary>The shared interval store (voxel data and light of the loaded chunks).</summary>
        public readonly long IntervalStoreBytes;

        /// <summary>Pages of the interval store (capacity).</summary>
        public readonly int IntervalStorePages;

        /// <summary>Pages of the interval store in use now.</summary>
        public readonly int IntervalStoreUsedPages;

        /// <summary>The most pages of the interval store in use at once.</summary>
        public readonly int IntervalStorePeakPages;

        /// <summary>Times the interval store was replaced by a larger buffer.</summary>
        public readonly int IntervalStoreGrowCount;

        /// <summary>Render buffers of the solid layer.</summary>
        public readonly int SolidBuffers;

        /// <summary>Render buffers of the transparent layer.</summary>
        public readonly int TransparentBuffers;

        /// <summary>Render buffers of the foliage layer.</summary>
        public readonly int FoliageBuffers;

        /// <summary>All render buffers of the three layers together.</summary>
        public readonly long RenderBufferBytes;

        /// <summary>Partitions built on the GPU at the same time, each with its own point builder.</summary>
        public readonly int BuildSlots;

        /// <summary>The point builders of all build slots together.</summary>
        public readonly long BuildSlotBytes;

        /// <summary>Culling lists of every camera, the copy pass and the voxel definitions.</summary>
        public readonly long OtherBytes;

        public GpuMemory(long intervalStoreBytes, int intervalStorePages, int intervalStoreUsedPages,
            int intervalStorePeakPages, int intervalStoreGrowCount, int solidBuffers, int transparentBuffers,
            int foliageBuffers, long renderBufferBytes, int buildSlots, long buildSlotBytes, long otherBytes){
            IntervalStoreBytes = intervalStoreBytes;
            IntervalStorePages = intervalStorePages;
            IntervalStoreUsedPages = intervalStoreUsedPages;
            IntervalStorePeakPages = intervalStorePeakPages;
            IntervalStoreGrowCount = intervalStoreGrowCount;
            SolidBuffers = solidBuffers;
            TransparentBuffers = transparentBuffers;
            FoliageBuffers = foliageBuffers;
            RenderBufferBytes = renderBufferBytes;
            BuildSlots = buildSlots;
            BuildSlotBytes = buildSlotBytes;
            OtherBytes = otherBytes;
        }

        /// <summary>All of the engine's buffers together.</summary>
        public long TotalBytes => IntervalStoreBytes + RenderBufferBytes + BuildSlotBytes + OtherBytes;

        /// <summary>Share of the interval store pages in use now, 0–1.</summary>
        public float IntervalStoreUsed =>
            IntervalStorePages > 0 ? (float)IntervalStoreUsedPages / IntervalStorePages : 0f;

        /// <summary>Share of the interval store pages in use at the peak, 0–1.</summary>
        public float IntervalStorePeak =>
            IntervalStorePages > 0 ? (float)IntervalStorePeakPages / IntervalStorePages : 0f;
    }

    /// <summary>Byte size of a GPU buffer, 0 when it is missing or released.</summary>
    internal static class GpuBytes{
        internal static long Of(GraphicsBuffer buffer){
            return buffer != null && buffer.IsValid() ? (long)buffer.count * buffer.stride : 0L;
        }
    }
}
