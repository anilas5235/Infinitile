using System.Collections.Generic;
using Engine.Scripts.Jobs.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The main-thread budget of the pipeline (<see cref="FrameBudget" />): the first item of work of a frame
    ///     always runs, an item after it only while time is left, and a stage's turn that was cut continues in the
    ///     next frame where it stopped, before any other stage (<see cref="StageRing" />). Time is a fake clock
    ///     that only the items of work advance, so every test is deterministic.
    /// </summary>
    public class FrameBudgetTests{
        private const double TicksPerMillisecond = 1000d;

        private sealed class FakeClock{
            private long _ticks = 5000; // not zero: a budget must not depend on where the clock starts

            public long Now(){
                return _ticks;
            }

            public void Spend(float milliseconds){
                _ticks += (long)(milliseconds * TicksPerMillisecond);
            }

            public FrameBudget Budget(float milliseconds){
                return new FrameBudget(milliseconds, Now, TicksPerMillisecond);
            }
        }

        private static readonly PriorityUtil.Focus Origin = new(int3.zero, new float3(0, 0, 1));

        #region Budget

        [Test]
        public void TheFirstItemOfAFrame_AlwaysRuns(){
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(1f);

            for (int frame = 0; frame < 3; frame++){
                budget.BeginFrame();
                clock.Spend(5f); // the frame's time is gone before the pipeline asks
                Assert.IsTrue(budget.Next(), "frame {0}: the first item", frame);
                Assert.IsFalse(budget.Next(), "frame {0}: the second item", frame);
            }

            Assert.AreEqual(3, budget.FramesCut);
        }

        [Test]
        public void Items_RunWhileTimeIsLeft(){
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(2f);
            budget.BeginFrame();

            int items = 0;
            while (budget.Next()){
                items++;
                clock.Spend(0.5f);
                Assert.Less(items, 100, "the budget never refused");
            }

            // Items started at 0, 0.5, 1 and 1.5 ms; at 2 ms the budget is used up
            Assert.AreEqual(4, items);
            Assert.AreEqual(1, budget.FramesCut);
        }

        [Test]
        public void AnItemThatStarted_IsNotCounted_UntilTheNextOneAsks(){
            // The budget cannot stop an item, so a frame exceeds it by at most the one item that ran last
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(2f);
            budget.BeginFrame();
            Assert.IsTrue(budget.Next());
            clock.Spend(1.9f);
            Assert.IsTrue(budget.Next(), "0.1 ms are left");
            clock.Spend(7f);
            Assert.IsFalse(budget.Next());
        }

        [Test]
        public void AfterARefusal_NothingMoreRunsInTheFrame(){
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(1f);
            budget.BeginFrame();
            Assert.IsTrue(budget.Next());
            clock.Spend(1f);
            Assert.IsFalse(budget.Next());
            clock.Spend(-1f); // even if the clock said there was time again
            Assert.IsFalse(budget.Next());
            Assert.IsFalse(budget.Next());
            Assert.AreEqual(1, budget.FramesCut, "a frame is counted once");

            budget.BeginFrame();
            Assert.IsTrue(budget.Next(), "the next frame starts over");
        }

        [Test]
        public void AFrameInsideTheBudget_IsNotCountedAsCut(){
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(3f);
            for (int frame = 0; frame < 5; frame++){
                budget.BeginFrame();
                for (int item = 0; item < 3; item++){
                    Assert.IsTrue(budget.Next());
                    clock.Spend(0.9f);
                }
            }

            Assert.AreEqual(0, budget.FramesCut);
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void WithoutALimit_EveryItemRuns(float milliseconds){
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(milliseconds);
            Assert.IsFalse(budget.Limited);

            budget.BeginFrame();
            for (int item = 0; item < 1000; item++){
                Assert.IsTrue(budget.Next());
                clock.Spend(10f);
            }

            Assert.AreEqual(0, budget.FramesCut);
            Assert.IsFalse(FrameBudget.Unlimited.Limited);
            Assert.IsTrue(FrameBudget.Unlimited.Next());
        }

        [Test]
        public void TheSystemClock_IsTheDefault(){
            FrameBudget budget = new(1000f);
            Assert.IsTrue(budget.Limited);
            Assert.AreEqual(1000f, budget.Milliseconds);
            budget.BeginFrame();
            Assert.IsTrue(budget.Next());
            Assert.IsTrue(budget.Next(), "a second item within one second");
        }

        #endregion

        #region Frame cap

        // The period of a capped frame: vertical sync with the refresh rate wins over the target frame rate
        [TestCase(0, -1, 60d, 0f, TestName = "FrameCap_Off")]
        [TestCase(0, 0, 60d, 0f, TestName = "FrameCap_TargetZero_IsOff")]
        [TestCase(0, 60, 144d, 1000f / 60f, TestName = "FrameCap_Target60")]
        [TestCase(0, 120, 60d, 1000f / 120f, TestName = "FrameCap_Target120")]
        [TestCase(0, 144, 60d, 1000f / 144f, TestName = "FrameCap_Target144")]
        [TestCase(0, 30, 60d, 1000f / 30f, TestName = "FrameCap_Target30")]
        [TestCase(1, -1, 60d, 1000f / 60f, TestName = "FrameCap_VSync60Hz")]
        [TestCase(2, -1, 60d, 1000f / 30f, TestName = "FrameCap_VSyncEverySecondBlank60Hz")]
        [TestCase(1, -1, 144d, 1000f / 144f, TestName = "FrameCap_VSync144Hz")]
        [TestCase(1, 30, 144d, 1000f / 144f, TestName = "FrameCap_VSync_WinsOverTarget")]
        [TestCase(1, -1, 0d, 0f, TestName = "FrameCap_VSyncWithoutRefreshRate_IsOff")]
        public void FrameCap_Period(int vSyncCount, int targetFrameRate, double refreshHz, float expectedMs){
            Assert.AreEqual(expectedMs, FrameCap.PeriodMs(vSyncCount, targetFrameRate, refreshHz), 1e-4f);
        }

        // The budget of a capped frame: 2 ms plus the share of the period above 8.33 ms
        [TestCase(2f, 0f, 1f, 2f, TestName = "Effective_Uncapped_IsTheBudget")]
        [TestCase(2f, 1000f / 60f, 1f, 2f + 1000f / 120f, TestName = "Effective_Cap60")]
        [TestCase(2f, 1000f / 120f, 1f, 2f, TestName = "Effective_Cap120_HasNoSpareTime")]
        [TestCase(2f, 1000f / 144f, 1f, 2f, TestName = "Effective_Cap144_HasNoSpareTime")]
        [TestCase(2f, 1000f / 30f, 1f, 27f, TestName = "Effective_Cap30")]
        [TestCase(2f, 1000f / 60f, 0.5f, 2f + 1000f / 240f, TestName = "Effective_Cap60_HalfShare")]
        [TestCase(2f, 1000f / 60f, 0f, 2f, TestName = "Effective_Cap60_ShareZero_IsTheBudget")]
        [TestCase(2f, 1000f / 60f, 3f, 2f + 1000f / 120f, TestName = "Effective_ShareAboveOne_IsOne")]
        [TestCase(0f, 1000f / 60f, 1f, 0f, TestName = "Effective_NoLimit_StaysNoLimit")]
        [TestCase(0f, 0f, 1f, 0f, TestName = "Effective_NoLimitUncapped_StaysNoLimit")]
        public void EffectiveBudget(float milliseconds, float capPeriodMs, float share, float expectedMs){
            Assert.AreEqual(expectedMs, FrameBudget.Effective(milliseconds, capPeriodMs, share), 1e-4f);
        }

        // The shipped shares (0.5 above 8.33 ms, at most 0.3 of a faster cap's period) at the menu's frame rates
        [TestCase(2f, 0f, 2f, TestName = "Shipped_Uncapped_2ms")]
        [TestCase(2f, 1000f / 60f, 2f + 1000f / 240f, TestName = "Shipped_Cap60_6_17ms")]
        [TestCase(2f, 1000f / 90f, 2f + 0.5f * (1000f / 90f - 1000f / 120f), TestName = "Shipped_Cap90_3_39ms")]
        [TestCase(2f, 1000f / 120f, 2f, TestName = "Shipped_Cap120_2ms")]
        [TestCase(2f, 1000f / 144f, 2f, TestName = "Shipped_Cap144_CeilingAbove2ms_2ms")]
        [TestCase(2f, 1000f / 240f, 1.25f, TestName = "Shipped_Cap240_1_25ms")]
        [TestCase(2f, 1000f / 300f, 1f, TestName = "Shipped_Cap300_1ms")]
        [TestCase(0f, 1000f / 300f, 0f, TestName = "Shipped_NoLimit_StaysNoLimit")]
        public void EffectiveBudget_ShippedShares(float milliseconds, float capPeriodMs, float expectedMs){
            Assert.AreEqual(expectedMs, FrameBudget.Effective(milliseconds, capPeriodMs, 0.5f, 0.3f), 1e-4f);
        }

        [TestCase(2f, 1000f / 300f, 0f, 2f, TestName = "FastCapShareZero_Cap300_IsTheBudget")]
        [TestCase(5f, 1000f / 300f, 3f, 1000f / 300f, TestName = "FastCapShareAboveOne_IsOne")]
        [TestCase(2f, 1000f / 300f, 3f, 2f, TestName = "FastCapShare_NeverRaisesTheBudget")]
        [TestCase(2f, 1000f / 60f, 0.3f, 2f, TestName = "FastCapShare_LeavesSlowCapsAlone")]
        public void EffectiveBudget_FastCapShare(float milliseconds, float capPeriodMs, float fastShare, float expectedMs){
            Assert.AreEqual(expectedMs, FrameBudget.Effective(milliseconds, capPeriodMs, 0f, fastShare), 1e-4f);
        }

        /// <summary>Items of 1 ms that one frame lets run, after <see cref="FrameBudget.BeginFrame" /> with the cap.</summary>
        private static int ItemsOfOneMillisecond(FakeClock clock, FrameBudget budget, float capPeriodMs){
            budget.BeginFrame(capPeriodMs);
            int items = 0;
            while (budget.Next()){
                items++;
                clock.Spend(1f);
                Assert.Less(items, 1000, "the budget never refused");
            }

            return items;
        }

        [Test]
        public void UnderACap_TheFrameGetsTheSpareTime_AndFollowsACapChangedAtRunTime(){
            FakeClock clock = new();
            FrameBudget budget = new(2f, clock.Now, TicksPerMillisecond, 1f);
            Assert.IsTrue(budget.FollowsCap);

            // Items start at 0, 1, 2 ... ms; the last one that starts is the one before the budget is used up
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 0f), "uncapped: 2 ms");
            Assert.AreEqual(2f, budget.EffectiveMilliseconds);
            Assert.AreEqual(11, ItemsOfOneMillisecond(clock, budget, 1000f / 60f), "60 frames cap: 10.33 ms");
            Assert.AreEqual(2f + 1000f / 120f, budget.EffectiveMilliseconds, 1e-4f);
            Assert.AreEqual(11, ItemsOfOneMillisecond(clock, budget, 1000f / 60f), "the same cap in the next frame");
            Assert.AreEqual(27, ItemsOfOneMillisecond(clock, budget, 1000f / 30f), "30 frames cap: 27 ms");
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 1000f / 144f), "144 frames cap: 2 ms");
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 0f), "the cap switched off again");
            Assert.AreEqual(2f, budget.EffectiveMilliseconds);
            Assert.AreEqual(2f, budget.Milliseconds, "the setting itself does not change");
        }

        [Test]
        public void UnderAFastCap_TheBudgetShrinks_AndFollowsTheCap(){
            FakeClock clock = new();
            FrameBudget budget = new(2f, clock.Now, TicksPerMillisecond, 0f, 0.3f);
            Assert.IsTrue(budget.FollowsCap, "a fast-cap share alone follows the cap");
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 0f), "uncapped: 2 ms");
            Assert.AreEqual(1, ItemsOfOneMillisecond(clock, budget, 1000f / 300f), "300 frames cap: 1 ms");
            Assert.AreEqual(1f, budget.EffectiveMilliseconds, 1e-4f);
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 1000f / 60f), "60 frames cap, no spare share: 2 ms");
            Assert.AreEqual(2f, budget.Milliseconds, "the setting itself does not change");
        }

        [Test]
        public void WithShareZero_ACapChangesNothing(){
            FakeClock clock = new();
            FrameBudget budget = clock.Budget(2f);
            Assert.IsFalse(budget.FollowsCap);
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 1000f / 60f));
            Assert.AreEqual(2, ItemsOfOneMillisecond(clock, budget, 1000f / 30f));
            Assert.AreEqual(2f, budget.EffectiveMilliseconds);
        }

        [Test]
        public void WithoutALimit_ACapChangesNothing(){
            FakeClock clock = new();
            FrameBudget budget = new(0f, clock.Now, TicksPerMillisecond, 1f);
            Assert.IsFalse(budget.Limited);
            Assert.IsFalse(budget.FollowsCap);
            budget.BeginFrame(1000f / 60f);
            for (int item = 0; item < 100; item++){
                Assert.IsTrue(budget.Next());
                clock.Spend(1f);
            }

            Assert.AreEqual(0f, budget.EffectiveMilliseconds);
            Assert.AreEqual(0, budget.FramesCut);
        }

        #endregion

        #region A stage's turn

        /// <summary>
        ///     A stage over five rows of numbered work items. Every row of a scan, taking a batch from the queue,
        ///     starting an item and applying an item is one item of work: it is written to <see cref="Log" /> with
        ///     the frame it ran in and costs <see cref="_itemMilliseconds" /> on the clock.
        /// </summary>
        private sealed class FakeStage : JobStateHandler<int>{
            private const int Rows = 2; // the scan visits the rows -2 to 2
            private const int Batch = 4;

            private readonly FakeClock _clock;
            private readonly float _itemMilliseconds;
            private readonly List<int>[] _pending = new List<int>[2 * Rows + 1];
            private readonly List<int> _batch = new(); // the batch in flight, in the order it was taken
            private readonly HashSet<int> _applied = new();
            private bool _taken;
            private int _started, _collected;

            public readonly List<string> Log = new();
            public readonly List<int> LogFrames = new();
            public readonly List<int> Applied = new();
            public int Frame;

            public FakeStage(FakeClock clock, float itemMilliseconds, int items) : base(null, null, null){
                _clock = clock;
                _itemMilliseconds = itemMilliseconds;
                Reserve(items, Batch);
                for (int row = 0; row < _pending.Length; row++) _pending[row] = new List<int>();
                for (int item = 0; item < items; item++) _pending[item * 7 % _pending.Length].Add(item);
            }

            private void Work(string what){
                Log.Add(what);
                LogFrames.Add(Frame);
                _clock.Spend(_itemMilliseconds);
            }

            /// <summary>The window moved: like the mesh stage, the queue is pruned (here nothing leaves it).</summary>
            public override void FocusUpdate(PriorityUtil.Focus focus){
                PruneQueue(focus, static (_, _) => false);
            }

            /// <summary>New work in <paramref name="row" />, as an edit would bring it.</summary>
            public void AddPending(int row, int item){
                _pending[row + Rows].Add(item);
            }

            protected override int ScanDistance => Rows;

            protected override void EnqueueRow(PriorityUtil.Focus focus, int x){
                Work($"scan row {x}");
                foreach (int item in _pending[x + Rows]){
                    if (_applied.Contains(item) || Set.Contains(item) || Queue.Contains(item)) continue;

                    Queue.Enqueue(item, item);
                }
            }

            protected override StageTurn JobUpdateStep(PriorityUtil.Focus focus, FrameBudget budget){
                if (!_taken){
                    if (!budget.Next()) return StageTurn.Cut;

                    Work("take a batch");
                    while (Set.Count < Batch && Queue.Count > 0){
                        int item = Queue.Dequeue();
                        Set.Add(item);
                        _batch.Add(item);
                    }

                    _taken = true;
                    _started = 0;
                }

                for (; _started < _batch.Count; _started++){
                    if (!budget.Next()) return StageTurn.Cut;

                    Work($"start {_batch[_started]}");
                }

                _taken = false;
                return StageTurn.Done;
            }

            protected override StageTurn CollectResultsStep(FrameBudget budget){
                for (; _collected < _batch.Count; _collected++){
                    if (!budget.Next()) return StageTurn.Cut;

                    Work($"apply {_batch[_collected]}");
                    Applied.Add(_batch[_collected]);
                    _applied.Add(_batch[_collected]);
                }

                _batch.Clear();
                _collected = 0;
                Set.Clear();
                return StageTurn.Done;
            }

            /// <summary>One <see cref="JobStateHandler{T}.Update" /> per frame until the stage sleeps.</summary>
            public List<StageTurn> RunUntilAsleep(FrameBudget budget){
                List<StageTurn> turns = new();
                while (!Sleeping){
                    Assert.Less(Frame, 10000, "the stage never went to sleep");
                    budget?.BeginFrame();
                    turns.Add(Update(Origin, budget));
                    Frame++;
                }

                return turns;
            }
        }

        private const int Items = 23;

        [Test]
        public void ATurnTheBudgetCut_IsAppliedCompletelyAndInOrder(){
            FakeClock clock = new();
            FakeStage whole = new(clock, 0.4f, Items);
            List<StageTurn> wholeTurns = whole.RunUntilAsleep(null);
            CollectionAssert.DoesNotContain(wholeTurns, StageTurn.Cut);
            Assert.AreEqual(Items, whole.Applied.Count, "the stage without a budget did not finish its work");

            // 1 ms and items of 0.4 ms: three items per frame (started at 0, 0.4 and 0.8 ms)
            FakeStage cut = new(clock, 0.4f, Items);
            FrameBudget budget = clock.Budget(1f);
            List<StageTurn> cutTurns = cut.RunUntilAsleep(budget);

            CollectionAssert.AreEqual(whole.Log, cut.Log, "the same items of work in the same order");
            CollectionAssert.AreEqual(whole.Applied, cut.Applied);
            CollectionAssert.Contains(cutTurns, StageTurn.Cut);
            Assert.Greater(cut.Frame, whole.Frame, "the cut turns took more frames");
            Assert.Greater(budget.FramesCut, 0);
            // One Done per turn, as without the budget; every other frame is a Cut
            Assert.AreEqual(wholeTurns.Count, cutTurns.FindAll(turn => turn == StageTurn.Done).Count);
            Assert.AreEqual(budget.FramesCut, cutTurns.FindAll(turn => turn == StageTurn.Cut).Count);
        }

        [Test]
        public void NoFrameStartsAnItem_OnceTheBudgetIsUsed(){
            FakeClock clock = new();
            FakeStage stage = new(clock, 0.4f, Items);
            stage.RunUntilAsleep(clock.Budget(1f));

            int[] itemsInFrame = new int[stage.Frame];
            foreach (int frame in stage.LogFrames) itemsInFrame[frame]++;
            for (int frame = 0; frame < itemsInFrame.Length; frame++){
                Assert.GreaterOrEqual(itemsInFrame[frame], 1, "frame {0} made no progress", frame);
                Assert.LessOrEqual(itemsInFrame[frame], 3, "frame {0} started an item after 1 ms", frame);
            }
        }

        [Test]
        public void AnItemLongerThanTheBudget_StillRuns_OnePerFrame(){
            FakeClock clock = new();
            FakeStage whole = new(clock, 5f, Items);
            whole.RunUntilAsleep(null);

            FakeStage cut = new(clock, 5f, Items);
            cut.RunUntilAsleep(clock.Budget(1f));

            CollectionAssert.AreEqual(whole.Log, cut.Log);
            Assert.AreEqual(cut.Log.Count, cut.Frame, "one item of work in every frame");
            for (int i = 0; i < cut.LogFrames.Count; i++) Assert.AreEqual(i, cut.LogFrames[i]);
        }

        /// <summary>
        ///     Starts a scan the budget cuts after its first row (the row -2), so the rest of it waits for the next
        ///     frame. Returns the budget of one item per frame.
        /// </summary>
        private static FrameBudget ScanCutAfterTheFirstRow(FakeClock clock, FakeStage stage){
            stage.RunUntilAsleep(null);
            stage.WakeUp();
            FrameBudget budget = clock.Budget(1f);
            budget.BeginFrame();
            Assert.AreEqual(StageTurn.Cut, stage.Update(Origin, budget), "the scan waits for the next frame");
            Assert.AreEqual("scan row -2", stage.Log[stage.Log.Count - 1]);
            return budget;
        }

        [Test]
        public void AWakeUpWhileAScanIsCut_LeadsToAnotherScan(){
            // An edit flags work in a row the cut scan has already visited and wakes the stage. The rest of the
            // scan finds nothing; the stage must scan again instead of going to sleep with the work flagged.
            FakeClock clock = new();
            FakeStage stage = new(clock, 1f, Items);
            FrameBudget budget = ScanCutAfterTheFirstRow(clock, stage);

            stage.AddPending(-2, 100);
            stage.WakeUp();
            stage.RunUntilAsleep(budget);

            CollectionAssert.Contains(stage.Applied, 100, "the work the wake-up brought");
        }

        [Test]
        public void APruneWhileAScanIsCut_LeadsToAnotherScan(){
            // The focus moved while the scan waited: the rows already visited belong to the old window, so the
            // stage scans again, as after any prune.
            FakeClock clock = new();
            FakeStage stage = new(clock, 1f, Items);
            FrameBudget budget = ScanCutAfterTheFirstRow(clock, stage);

            stage.AddPending(-2, 100);
            stage.FocusUpdate(Origin);
            stage.RunUntilAsleep(budget);

            CollectionAssert.Contains(stage.Applied, 100, "the work in a row the cut scan had visited");
        }

        [Test]
        public void ASleepingStage_TakesNoItemOfTheFrame(){
            FakeClock clock = new();
            FakeStage stage = new(clock, 0.4f, Items);
            stage.RunUntilAsleep(null);

            FrameBudget budget = clock.Budget(1f);
            budget.BeginFrame();
            clock.Spend(5f);
            Assert.AreEqual(StageTurn.None, stage.Update(Origin, budget));
            Assert.IsTrue(budget.Next(), "the first item of the frame is still free for the next stage");
        }

        #endregion

        #region Fairness between the stages

        /// <summary>A stage that answers its turns from a script; without a script entry it has no work.</summary>
        private sealed class ScriptedStage : IPipelineStage{
            private readonly Queue<StageTurn> _script = new();
            private readonly StageTurn[] _repeat;
            private int _next;

            public int Calls;

            /// <param name="repeat">Answers given over and over once <see cref="Then" /> has run out; none: no work.</param>
            public ScriptedStage(params StageTurn[] repeat){
                _repeat = repeat;
            }

            public ScriptedStage Then(params StageTurn[] turns){
                foreach (StageTurn turn in turns) _script.Enqueue(turn);
                return this;
            }

            public StageTurn Update(PriorityUtil.Focus focus, FrameBudget budget){
                Calls++;
                if (_script.Count > 0) return _script.Dequeue();
                return _repeat.Length == 0 ? StageTurn.None : _repeat[_next++ % _repeat.Length];
            }
        }

        private static int[] Frames(StageRing ring, int count){
            int[] stages = new int[count];
            for (int i = 0; i < count; i++) stages[i] = ring.Turn(Origin, FrameBudget.Unlimited);
            return stages;
        }

        [Test]
        public void TheStages_TakeTurns(){
            StageRing ring = new(new IPipelineStage[]{
                new ScriptedStage(StageTurn.Done), new ScriptedStage(StageTurn.Done), new ScriptedStage(StageTurn.Done)
            });
            CollectionAssert.AreEqual(new[]{ 0, 1, 2, 0, 1, 2, 0 }, Frames(ring, 7));
        }

        [Test]
        public void AStageWithoutWork_PassesTheFrameOn(){
            ScriptedStage idle = new();
            StageRing ring = new(new IPipelineStage[]{
                new ScriptedStage(StageTurn.Done), idle, new ScriptedStage(StageTurn.Done)
            });
            CollectionAssert.AreEqual(new[]{ 0, 2, 0, 2, 0 }, Frames(ring, 5));
            Assert.Greater(idle.Calls, 0, "the idle stage was asked");

            StageRing none = new(new IPipelineStage[]{ new ScriptedStage(), new ScriptedStage() });
            Assert.AreEqual(-1, none.Turn(Origin, FrameBudget.Unlimited), "no stage has work");
        }

        [Test]
        public void ACutStage_KeepsTheFrames_UntilItsTurnIsFinished(){
            ScriptedStage first = new(StageTurn.Done);
            ScriptedStage cut = new ScriptedStage(StageTurn.Done).Then(StageTurn.Cut, StageTurn.Cut, StageTurn.Done);
            ScriptedStage last = new(StageTurn.Done);
            StageRing ring = new(new IPipelineStage[]{ first, cut, last });

            CollectionAssert.AreEqual(new[]{ 0, 1, 1 }, Frames(ring, 3));
            // While the turn is cut, no other stage is asked: its work is never interleaved with theirs
            Assert.AreEqual(1, first.Calls);
            Assert.AreEqual(0, last.Calls);

            CollectionAssert.AreEqual(new[]{ 1, 2, 0, 1, 2 }, Frames(ring, 5), "then the stage after it is next");
        }

        [Test]
        public void AStageThatIsCutInEveryTurn_DoesNotStarveTheOthers(){
            // The first stage needs three frames for every turn, the others one
            ScriptedStage heavy = new(StageTurn.Cut, StageTurn.Cut, StageTurn.Done);
            ScriptedStage a = new(StageTurn.Done);
            ScriptedStage b = new(StageTurn.Done);
            StageRing ring = new(new IPipelineStage[]{ heavy, a, b });

            // Rounds of five frames, 0 0 0 1 2: every stage finishes one turn per round
            int[] frames = Frames(ring, 50);
            for (int i = 0; i < frames.Length; i++) Assert.AreEqual(i % 5 < 3 ? 0 : i % 5 - 2, frames[i], "frame {0}", i);
            Assert.AreEqual(10, a.Calls);
            Assert.AreEqual(10, b.Calls);
        }

        [Test]
        public void StagesUnderABudget_FinishTheSameWork_AsWithoutOne(){
            // Three real stage handlers with different amounts of work share one budget through the ring
            FakeClock clock = new();
            FakeStage[] whole ={ new(clock, 0.4f, 23), new(clock, 0.4f, 5), new(clock, 0.4f, 40) };
            FakeStage[] cut ={ new(clock, 0.4f, 23), new(clock, 0.4f, 5), new(clock, 0.4f, 40) };

            RunRing(whole, FrameBudget.Unlimited);
            FrameBudget budget = clock.Budget(1f);
            int frames = RunRing(cut, budget);

            for (int i = 0; i < whole.Length; i++){
                CollectionAssert.AreEqual(whole[i].Log, cut[i].Log, "stage {0}", i);
                CollectionAssert.AreEqual(whole[i].Applied, cut[i].Applied, "stage {0}", i);
            }

            Assert.Greater(budget.FramesCut, 0);
            // No frame started more than three items, over all stages together
            int[] itemsInFrame = new int[frames];
            foreach (FakeStage stage in cut)
            foreach (int frame in stage.LogFrames)
                itemsInFrame[frame]++;
            foreach (int items in itemsInFrame) Assert.LessOrEqual(items, 3);
        }

        private static int RunRing(FakeStage[] stages, FrameBudget budget){
            StageRing ring = new(new IPipelineStage[]{ stages[0], stages[1], stages[2] });
            int frame = 0;
            for (;; frame++){
                Assert.Less(frame, 10000, "the stages never went to sleep");
                foreach (FakeStage stage in stages) stage.Frame = frame;
                budget.BeginFrame();
                if (ring.Turn(Origin, budget) < 0) break;
            }

            return frame;
        }

        #endregion
    }
}
