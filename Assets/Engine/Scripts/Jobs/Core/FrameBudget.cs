using System;
using System.Diagnostics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     The main-thread time the pipeline may spend in one frame. The scheduler starts it once per frame and the
    ///     stages ask it before every item of work (a pool claim, the upload of one chunk, one column of a queue
    ///     scan, the start of a batch). The first item of a frame always runs, so every frame makes progress; an
    ///     item after it runs only while time is left. Work that was refused continues in the next frame where it
    ///     stopped. An item that has started is finished, so a frame can exceed the budget by one item.
    /// </summary>
    internal sealed class FrameBudget{
        /// <summary>No limit: every item runs. For the callers outside the frame loop (shutdown, tests).</summary>
        internal static readonly FrameBudget Unlimited = new(0f);

        private readonly long _budgetTicks;
        private readonly Func<long> _clock;
        private bool _first;
        private bool _used;
        private long _start;

        /// <param name="milliseconds">The budget per frame; 0 or less switches the limit off.</param>
        /// <param name="clock">Time source in ticks; the system's high-resolution clock when null.</param>
        /// <param name="ticksPerMillisecond">Ticks of <paramref name="clock" /> per millisecond.</param>
        internal FrameBudget(float milliseconds, Func<long> clock = null, double ticksPerMillisecond = 0d){
            _clock = clock ?? Stopwatch.GetTimestamp;
            if (clock == null) ticksPerMillisecond = Stopwatch.Frequency / 1000d;
            _budgetTicks = milliseconds > 0f ? Math.Max(1L, (long)(milliseconds * ticksPerMillisecond)) : 0L;
            Milliseconds = Math.Max(0f, milliseconds);
        }

        /// <summary>The budget per frame in milliseconds; 0 when the limit is switched off.</summary>
        internal float Milliseconds{ get; }

        /// <summary>False when the budget is switched off.</summary>
        internal bool Limited => _budgetTicks > 0L;

        /// <summary>Frames in which an item was refused since the world was built.</summary>
        internal int FramesCut{ get; private set; }

        /// <summary>Starts the budget of a frame.</summary>
        internal void BeginFrame(){
            if (!Limited) return;

            _first = true;
            _used = false;
            _start = _clock();
        }

        /// <summary>
        ///     Asks for the next item of work: true for the first item of the frame and while time is left. After
        ///     the first false every further call of the frame is false.
        /// </summary>
        internal bool Next(){
            if (!Limited) return true;
            if (_first){
                _first = false;
                return true;
            }

            if (_used) return false;
            if (_clock() - _start < _budgetTicks) return true;

            _used = true;
            FramesCut++;
            return false;
        }
    }
}
