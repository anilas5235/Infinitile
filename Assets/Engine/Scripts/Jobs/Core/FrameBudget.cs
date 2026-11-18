using System;
using System.Diagnostics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     The main-thread time the pipeline may spend in one frame. The scheduler starts it once per frame and the
    ///     stages ask it before every item of work (a pool claim, the upload of one chunk, one row of a queue
    ///     scan, the start of a batch). The first item of a frame always runs, so every frame makes progress; an
    ///     item after it runs only while time is left. Work that was refused continues in the next frame where it
    ///     stopped. An item that has started is finished, so a frame can exceed the budget by one item.
    ///     Under a frame cap whose period is longer than <see cref="GoalFrameMs" />, the frame has time to spare
    ///     that an uncapped frame does not have: the budget of such a frame grows by
    ///     <see cref="CapShare" /> of the part of the period above <see cref="GoalFrameMs" /> (see
    ///     <see cref="Effective" />). Under a cap faster than 120 frames per second the frame is shorter than
    ///     <see cref="GoalFrameMs" />, and the budget is at most <see cref="FastCapShare" /> of the cap's period
    ///     (300 frames: 1 ms). Uncapped the budget is <see cref="Milliseconds" />.
    /// </summary>
    internal sealed class FrameBudget{
        /// <summary>
        ///     The frame of the frame-time goal (120 frames per second, owner 2026-11-14). An uncapped frame is
        ///     meant to fit in it with the budget; the part of a capped frame's period above it is spare time.
        /// </summary>
        internal const float GoalFrameMs = 1000f / 120f;

        /// <summary>No limit: every item runs. For the callers outside the frame loop (shutdown, tests).</summary>
        internal static readonly FrameBudget Unlimited = new(0f);

        private readonly Func<long> _clock;
        private readonly double _ticksPerMillisecond;
        private long _budgetTicks;
        private float _capPeriodMs;
        private bool _first;
        private bool _used;
        private long _start;

        /// <param name="milliseconds">The budget per frame; 0 or less switches the limit off.</param>
        /// <param name="clock">Time source in ticks; the system's high-resolution clock when null.</param>
        /// <param name="ticksPerMillisecond">Ticks of <paramref name="clock" /> per millisecond.</param>
        /// <param name="capShare">
        ///     Share (0 to 1) of a capped frame's spare time added to the budget; 0 keeps the budget at
        ///     <paramref name="milliseconds" /> under every cap of 120 frames per second or slower.
        /// </param>
        /// <param name="fastCapShare">
        ///     Under a cap faster than 120 frames per second, the most of the cap's period (0 to 1) the budget may
        ///     take; 0 keeps the budget at <paramref name="milliseconds" /> under such a cap.
        /// </param>
        internal FrameBudget(float milliseconds, Func<long> clock = null, double ticksPerMillisecond = 0d,
            float capShare = 0f, float fastCapShare = 0f){
            _clock = clock ?? Stopwatch.GetTimestamp;
            if (clock == null) ticksPerMillisecond = Stopwatch.Frequency / 1000d;
            _ticksPerMillisecond = ticksPerMillisecond;
            Milliseconds = Math.Max(0f, milliseconds);
            CapShare = Math.Clamp(capShare, 0f, 1f);
            FastCapShare = Math.Clamp(fastCapShare, 0f, 1f);
            SetEffective(Milliseconds);
        }

        /// <summary>The budget per frame in milliseconds without a frame cap; 0 when the limit is switched off.</summary>
        internal float Milliseconds{ get; }

        /// <summary>Share of a capped frame's spare time that is added to the budget.</summary>
        internal float CapShare{ get; }

        /// <summary>The most of a fast cap's period (faster than 120 frames per second) the budget may take.</summary>
        internal float FastCapShare{ get; }

        /// <summary>The budget of the current frame in milliseconds, after the frame cap; 0 without a limit.</summary>
        internal float EffectiveMilliseconds{ get; private set; }

        /// <summary>False when the budget is switched off.</summary>
        internal bool Limited => Milliseconds > 0f;

        /// <summary>True when the budget grows under a frame cap: the caller then passes the cap's period.</summary>
        internal bool FollowsCap => Limited && (CapShare > 0f || FastCapShare > 0f);

        /// <summary>Frames in which an item was refused since the world was built.</summary>
        internal int FramesCut{ get; private set; }

        /// <summary>
        ///     For measurements: when true, the budget times the spans between <see cref="BeginFrame" />, every
        ///     <see cref="Next" /> and <see cref="EndFrame" /> with the system clock (also without a limit). A span is
        ///     an item of work with the bookkeeping around it, and the longest one of a frame is
        ///     <see cref="LongestItemMs" />: what the budget can overshoot by. Off, it costs one check per item.
        /// </summary>
        internal bool TimeItems;

        private long _itemStart;
        private long _longestItem;

        /// <summary>The longest span of the last frame in milliseconds (see <see cref="TimeItems" />).</summary>
        internal float LongestItemMs => (float)(_longestItem * 1000d / Stopwatch.Frequency);

        private void MarkItem(){
            long now = Stopwatch.GetTimestamp();
            _longestItem = Math.Max(_longestItem, now - _itemStart);
            _itemStart = now;
        }

        /// <summary>
        ///     Ends the current span without asking for a new item, e.g. after the last part of a piece of work that
        ///     wants to know its longest item (see <see cref="TimeItems" />).
        /// </summary>
        internal void EndItem(){
            if (TimeItems) MarkItem();
        }

        /// <summary>Ends the frame's last span (see <see cref="TimeItems" />).</summary>
        internal void EndFrame(){
            EndItem();
        }

        /// <summary>
        ///     The budget of a frame under a frame cap. A cap whose period is longer than <see cref="GoalFrameMs" />
        ///     adds <paramref name="capShare" /> of the part of <paramref name="capPeriodMs" /> above it to
        ///     <paramref name="milliseconds" />. A cap of 120 frames per second or faster limits the budget to
        ///     <paramref name="fastCapShare" /> of the period, but never raises it above
        ///     <paramref name="milliseconds" />. No cap (period 0) or a share of 0 leave the budget as it is; a
        ///     budget of 0 (no limit) stays 0.
        /// </summary>
        /// <example>
        ///     2 ms, shipped shares 0.5 and 0.3: 30 frames cap 14.5 ms, 60 frames 6.17 ms, 90 frames 3.39 ms,
        ///     120 and 144 frames 2 ms, 240 frames 1.25 ms, 300 frames 1 ms.
        /// </example>
        internal static float Effective(float milliseconds, float capPeriodMs, float capShare, float fastCapShare = 0f){
            if (milliseconds <= 0f) return 0f;
            if (capPeriodMs <= 0f) return milliseconds;

            float spare = capPeriodMs - GoalFrameMs;
            if (spare > 0f) return capShare > 0f ? milliseconds + Math.Min(capShare, 1f) * spare : milliseconds;
            return fastCapShare > 0f ? Math.Min(milliseconds, Math.Min(fastCapShare, 1f) * capPeriodMs) : milliseconds;
        }

        /// <summary>Starts the budget of a frame.</summary>
        /// <param name="capPeriodMs">
        ///     The period of the frame cap in force in milliseconds (<see cref="FrameCap.PeriodMs()" />); 0 for no
        ///     cap. Only read when <see cref="FollowsCap" />.
        /// </param>
        internal void BeginFrame(float capPeriodMs = 0f){
            if (TimeItems){
                _itemStart = Stopwatch.GetTimestamp();
                _longestItem = 0;
            }

            if (!Limited) return;

            if (FollowsCap && capPeriodMs != _capPeriodMs){
                _capPeriodMs = capPeriodMs;
                SetEffective(Effective(Milliseconds, capPeriodMs, CapShare, FastCapShare));
            }

            _first = true;
            _used = false;
            _start = _clock();
        }

        private void SetEffective(float milliseconds){
            EffectiveMilliseconds = milliseconds;
            _budgetTicks = milliseconds > 0f ? Math.Max(1L, (long)(milliseconds * _ticksPerMillisecond)) : 0L;
        }

        /// <summary>
        ///     Asks for the next item of work: true for the first item of the frame and while time is left. After
        ///     the first false every further call of the frame is false.
        /// </summary>
        internal bool Next(){
            if (TimeItems) MarkItem();
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
