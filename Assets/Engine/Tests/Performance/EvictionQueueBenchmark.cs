using Engine.Scripts.Jobs.Core;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     The pool's partition eviction queue after a one-chunk focus step (DEC-233/236): the generic heap it
    ///     replaced against <see cref="EvictionQueue" />. The queue is full at draw distance 14 (29² columns × 16
    ///     partitions, about the 13 400 entries the H1 goal run saw).
    /// </summary>
    [TestFixture]
    internal class EvictionQueueBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 30;
        private const int Radius = 14;

        private static int _step;

        private static PriorityUtil.Focus NextFocus(){
            // Back and forth along x, so every measurement moves the focus one chunk
            int x = (_step++ & 1) == 0 ? 1 : 0;
            return new PriorityUtil.Focus(new int3(x, 4, 0), new float3(1, 0, 0));
        }

        [Test]
        [Performance]
        public void GenericHeap_FocusStep_FullPartitionQueue(){
            SimpleFastPriorityQueue<int3, float> queue = new();
            PriorityUtil.Focus focus = new(new int3(0, 4, 0), new float3(1, 0, 0));
            for (int x = -Radius; x <= Radius; x++)
            for (int z = -Radius; z <= Radius; z++)
            for (int y = 0; y < PartitionsPerChunk; y++){
                int3 p = new(x, y, z);
                queue.Enqueue(p, PriorityUtil.EvictionPriority(ref p, ref focus));
            }

            Measure.Method(() => {
                    PriorityUtil.Focus f = NextFocus();
                    queue.UpdateAllPriorities(f, static (s, pos) => PriorityUtil.EvictionPriority(ref pos, ref s));
                })
                .SampleGroup(new SampleGroup("Generic heap focus step ms", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .Run();
        }

        [Test]
        [Performance]
        public void EvictionQueue_FocusStep_FullPartitionQueue(){
            EvictionQueue queue = new((2 * Radius + 1) * (2 * Radius + 1) * PartitionsPerChunk + 1, false);
            queue.UpdateFocus(new PriorityUtil.Focus(new int3(0, 4, 0), new float3(1, 0, 0)));
            for (int x = -Radius; x <= Radius; x++)
            for (int z = -Radius; z <= Radius; z++)
            for (int y = 0; y < PartitionsPerChunk; y++)
                queue.Enqueue(new int3(x, y, z));

            Measure.Method(() => queue.UpdateFocus(NextFocus()))
                .SampleGroup(new SampleGroup("EvictionQueue focus step ms", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .Run();
        }
    }
}
