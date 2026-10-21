using Engine.Scripts.Utils.Collections;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     UnsafeIntervalList.Set with 10^4 random edits on a chunk-sized, terrain-like list
    ///     (one column pattern of stone/dirt/grass/air repeated for every x,z column, as ChunkJob writes it).
    /// </summary>
    [TestFixture]
    internal class UnsafeIntervalListBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;
        private const int EditCount = 10_000;
        private const uint RandomSeed = 0x9E3779B9u;

        private UnsafeIntervalList<ushort> _list;
        private bool _created;

        [TearDown]
        public void TearDown(){
            DisposeList();
        }

        [Test]
        [Performance]
        public void Set_10kRandomEdits_ChunkSizedList(){
            Measure.Method(() => {
                    Random rng = new(RandomSeed);
                    for (int i = 0; i < EditCount; i++)
                        _list.Set(rng.NextInt(0, VoxelsPerChunk), (ushort)rng.NextInt(0, 4));
                })
                .SampleGroup(new SampleGroup("UnsafeIntervalList.Set 10k edits ms", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .SetUp(CreateList)
                .CleanUp(DisposeList)
                .Run();
        }

        private void CreateList(){
            _list = new UnsafeIntervalList<ushort>(ChunkWidth * ChunkDepth * 4, Allocator.Persistent);
            _created = true;
            // Column layout in x,z,y order: 100 stone, 4 dirt, 1 grass, rest air.
            for (int column = 0; column < ChunkWidth * ChunkDepth; column++){
                _list.AddInterval(3, 100);
                _list.AddInterval(2, 4);
                _list.AddInterval(1, 1);
                _list.AddInterval(0, ChunkHeight - 105);
            }
        }

        private void DisposeList(){
            if (!_created) return;
            _list.Dispose();
            _created = false;
        }
    }
}
