using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Abstract base class for job schedulers that records execution timings
    /// and exposes a moving average over the last N runs.
    /// </summary>
    public abstract class JobScheduler
    {
        private readonly int _records;
        private readonly Queue<long> _timings;
        private readonly Stopwatch _watch;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobScheduler" /> class
        /// with the specified number of history records.
        /// </summary>
        /// <param name="records">Maximum number of timing entries to keep in history. Defaults to 16.</param>
        protected JobScheduler(int records = 16)
        {
            _records = records;
            _watch = new Stopwatch();
            _timings = new Queue<long>(_records);
        }

        /// <summary>
        /// Gets the average time of the last recorded runs. The value is derived from
        /// the internal millisecond timings and divided by 10 to preserve legacy scaling.
        /// </summary>
        public float AvgTime => (float)_timings.Sum() / 10;

        /// <summary>
        /// Starts timing for the current run.
        /// </summary>
        protected void StartRecord()
        {
            _watch.Restart();
        }

        /// <summary>
        /// Stops timing for the current run and enqueues the measured value in the history.
        /// </summary>
        /// <returns>The elapsed time in milliseconds for the current run.</returns>
        protected long StopRecord()
        {
            _watch.Stop();
            long ms = _watch.ElapsedMilliseconds;

            if (_timings.Count > _records) _timings.Dequeue();

            _timings.Enqueue(ms);
            return ms;
        }
    }
}