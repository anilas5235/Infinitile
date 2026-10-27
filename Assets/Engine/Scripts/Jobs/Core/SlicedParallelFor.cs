using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     A parallel job whose <c>Execute(index)</c> works on element <c>Offset + index</c>, so a range of elements
    ///     can be split over several scheduled jobs (<see cref="SlicedParallelFor" />).
    /// </summary>
    public interface ISlicedParallelFor : IJobParallelFor{
        int Offset { set; }
    }

    /// <summary>
    ///     Schedules a parallel job over many elements as a chain of slices. The Temp allocations a job makes while it
    ///     runs are released only when that job completes, so a job that generates a chunk per index holds all of its
    ///     chunks' scratch memory (about 0.35 MB each) until its last index is done; thousands of indices in one job
    ///     reach gigabytes. A slice bounds that to <c>perSlice</c> chunks.
    /// </summary>
    public static class SlicedParallelFor{
        /// <summary>Elements per slice for jobs that generate one chunk per element: ~90 MB of scratch at most.</summary>
        public const int ChunksPerSlice = 256;

        /// <summary>
        ///     Runs <paramref name="job" /> over elements 0 … <paramref name="count" /> − 1 in slices of at most
        ///     <paramref name="perSlice" /> elements, each slice after the one before. The returned handle completes
        ///     the last slice; a count of 0 returns <paramref name="dependsOn" />.
        /// </summary>
        public static JobHandle Schedule<T>(T job, int count, int perSlice, JobHandle dependsOn = default)
            where T : struct, ISlicedParallelFor{
            JobHandle handle = dependsOn;
            for (int start = 0; start < count; start += perSlice){
                job.Offset = start;
                handle = job.Schedule(math.min(perSlice, count - start), 1, handle);
            }

            return handle;
        }
    }
}
