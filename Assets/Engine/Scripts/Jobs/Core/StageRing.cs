using Unity.Profiling;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>What a stage did with the turn it was offered.</summary>
    internal enum StageTurn : byte{
        /// <summary>Nothing to do on the main thread: the stage sleeps, its batch still runs or it may not dispatch.</summary>
        None,

        /// <summary>The turn is finished.</summary>
        Done,

        /// <summary>The frame's budget ran out inside the turn; the stage continues it in the next frame.</summary>
        Cut
    }

    /// <summary>A stage of the pipeline as the scheduler sees it.</summary>
    internal interface IPipelineStage{
        /// <summary>
        ///     One turn of the stage, or the rest of the turn the budget cut in an earlier frame. The stage asks
        ///     <paramref name="budget" /> before every item of work.
        /// </summary>
        StageTurn Update(PriorityUtil.Focus focus, FrameBudget budget);
    }

    /// <summary>
    ///     The round robin over the stages: every frame the first stage in line that has main-thread work gets the
    ///     frame, and the stage after it is first in line in the next frame. A stage whose turn the budget cut stays
    ///     first in line until the turn is finished, so a turn is never interleaved with another stage's work and
    ///     no stage waits longer than the turns of the others take.
    /// </summary>
    internal sealed class StageRing{
        private readonly IPipelineStage[] _stages;
        private readonly ProfilerMarker[] _markers;
        private int _first;

        /// <param name="stages">The stages in the order of their turns.</param>
        /// <param name="markers">One profiler marker per stage, or null.</param>
        internal StageRing(IPipelineStage[] stages, ProfilerMarker[] markers = null){
            _stages = stages;
            _markers = markers;
        }

        /// <summary>Gives the frame to one stage. Returns its index, or -1 when no stage had work.</summary>
        internal int Turn(PriorityUtil.Focus focus, FrameBudget budget){
            for (int i = 0; i < _stages.Length; i++){
                int stage = (_first + i) % _stages.Length;
                StageTurn turn;
                if (_markers == null){
                    turn = _stages[stage].Update(focus, budget);
                }
                else{
                    using (_markers[stage].Auto()) turn = _stages[stage].Update(focus, budget);
                }

                if (turn == StageTurn.None) continue;

                _first = turn == StageTurn.Cut ? stage : (stage + 1) % _stages.Length;
                return stage;
            }

            return -1;
        }
    }
}
