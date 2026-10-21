using System.Collections.Generic;
using System.Diagnostics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Abstract base class for job schedulers that records execution timings
    ///     and exposes a moving average over the last N runs.
    /// </summary>
    public abstract class JobScheduler{
        private readonly int _records;
        private readonly Queue<double> _timings;
        private readonly Stopwatch _watch;
        private double _sum;

        /// <summary>
        ///     Initializes a new instance of the <see cref="JobScheduler" /> class
        ///     with the specified number of history records.
        /// </summary>
        /// <param name="records">Maximum number of timing entries to keep in history. Defaults to 16.</param>
        protected JobScheduler(int records = 16){
            _records = records;
            _watch = new Stopwatch();
            _timings = new Queue<double>(_records);
        }

        /// <summary>
        ///     Gets the average time in milliseconds of the last recorded runs (at most <c>records</c> of them).
        /// </summary>
        public float AvgTime => _timings.Count == 0 ? 0f : (float)_sum / _timings.Count;

        /// <summary>
        ///     Starts timing for the current run. The run lasts from scheduling the job until its results are
        ///     collected, so it includes the frames the job waited for the scheduler.
        /// </summary>
        protected void StartRecord(){
            _watch.Restart();
        }

        /// <summary>
        ///     Stops timing for the current run and enqueues the measured value in the history.
        /// </summary>
        /// <returns>The elapsed time in milliseconds for the current run.</returns>
        protected double StopRecord(){
            _watch.Stop();
            double ms = _watch.Elapsed.TotalMilliseconds; // sub-millisecond resolution
            RecordTiming(ms);
            return ms;
        }

        /// <summary>
        ///     Adds a measured run to the fixed window of the last <c>records</c> runs.
        /// </summary>
        protected void RecordTiming(double milliseconds){
            if (_timings.Count >= _records) _sum -= _timings.Dequeue();
            _timings.Enqueue(milliseconds);
            _sum += milliseconds;
        }
    }
}
