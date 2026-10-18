using Engine.Scripts.Render;
using NUnit.Framework;
using Unity.Collections;
using Unity.PerformanceTesting;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     PERF-02: page allocation. Fills one buffer with three-page partitions, then releases them all.
    /// </summary>
    [TestFixture]
    internal class PageAllocatorBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;
        private const int PagesPerPartition = 3;
        private const int PointsPerPartition = PagesPerPartition * PointsPerPage - 10;
        private const int PartitionCount = PagesPerBuffer / PagesPerPartition;

        private PageAllocator _allocator;
        private AllocInfo[] _allocations;

        [SetUp]
        public void SetUp(){
            _allocations = new AllocInfo[PartitionCount];
        }

        [TearDown]
        public void TearDown(){
            _allocator?.Dispose();
            _allocator = null;
        }

        [Test]
        [Performance]
        public void TryAllocPages_FillBuffer_ThenReleaseAll(){
            Measure.Method(() => {
                    for (int i = 0; i < PartitionCount; i++)
                        _allocator.TryAllocPages(PointsPerPartition, PagesPerPartition, 0, default, out _allocations[i]);
                    for (int i = 0; i < PartitionCount; i++) _allocator.Release(_allocations[i]);
                })
                .SampleGroup(new SampleGroup("PageAllocator fill+release 1 buffer ms", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .SetUp(() => _allocator = new PageAllocator(Allocator.Persistent))
                .CleanUp(() => {
                    _allocator.Dispose();
                    _allocator = null;
                })
                .Run();
        }
    }
}
