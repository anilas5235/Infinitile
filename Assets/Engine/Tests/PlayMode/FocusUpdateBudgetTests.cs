using System.Collections;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Utils;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     The focus update under the frame's budget (§16.16): the world asks for it, and its parts run first in the
    ///     next ticks, one item of the budget each, before any stage has its turn. The tests stop the world's own
    ///     ticks and drive the scheduler with a budget whose clock moves 1 ms on every reading: under a budget of
    ///     3 ms a frame runs three items.
    /// </summary>
    public class FocusUpdateBudgetTests{
        private const float BudgetMs = 3f;
        private const int ItemsPerFrame = 3;

        private VoxelWorld _world;
        private VoxelEngineScheduler _scheduler;
        private FrameBudget _savedBudget;
        private PriorityUtil.Focus _original;
        private PriorityUtil.Focus _shifted;
        private long _ticks;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
            _world = VoxelWorld.Instance;
            _world.enabled = false; // no Update and no focus check: the test gives the ticks
            _scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(_world, "_scheduler");
            _savedBudget = _scheduler.Budget;
            _original = _world.Focus;
            _shifted = new PriorityUtil.Focus(_original.Pos + new int3(1, 0, 0), _original.Forward);
            _scheduler.FocusUpdate(_original); // drops a request the world may have left
            _ticks = 0;
            _scheduler.Budget = new FrameBudget(BudgetMs, () => ++_ticks, 1d);
        }

        [TearDown]
        public void TearDown(){
            if (_scheduler != null){
                _scheduler.FocusInOneItem = false;
                _scheduler.Budget = _savedBudget;
                _scheduler.FocusUpdate(_original);
            }

            if (_world) _world.enabled = true;
        }

        [Test]
        public void ARequest_RunsItsPartsOverFrames_AndNoStageWorksMeanwhile(){
            _scheduler.RequestFocusUpdate(_shifted);
            Assert.AreEqual(VoxelEngineScheduler.FocusParts, _scheduler.FocusPartsLeft, "nothing runs before the tick");

            int left = VoxelEngineScheduler.FocusParts;
            while (left > ItemsPerFrame){
                _scheduler.ScheduleUpdate(_shifted);
                left -= ItemsPerFrame;
                Assert.AreEqual(left, _scheduler.FocusPartsLeft, "one part per item of the frame");
                Assert.AreEqual(-1, _scheduler.LastStage, "no stage has a turn while parts are left");
            }

            _scheduler.ScheduleUpdate(_shifted);
            Assert.IsFalse(_scheduler.FocusPending, "the last parts fit in the frame");
        }

        [Test]
        public void ANewRequest_StartsTheParts_Again(){
            _scheduler.RequestFocusUpdate(_shifted);
            _scheduler.ScheduleUpdate(_shifted);
            Assert.AreEqual(VoxelEngineScheduler.FocusParts - ItemsPerFrame, _scheduler.FocusPartsLeft);

            _scheduler.RequestFocusUpdate(_original);
            Assert.AreEqual(VoxelEngineScheduler.FocusParts, _scheduler.FocusPartsLeft,
                "a newer focus updates every queue again");
        }

        [Test]
        public void WithoutALimit_EveryPartRuns_InOneFrame(){
            _scheduler.Budget = new FrameBudget(0f);
            _scheduler.RequestFocusUpdate(_shifted);
            _scheduler.ScheduleUpdate(_shifted);
            Assert.IsFalse(_scheduler.FocusPending);
        }

        [Test]
        public void InOneItem_TheRequestRunsAtOnce(){
            _scheduler.FocusInOneItem = true;
            _scheduler.RequestFocusUpdate(_shifted);
            Assert.IsFalse(_scheduler.FocusPending, "the measurement switch runs the whole update in the request");
        }

        [Test]
        public void TheImmediateUpdate_DropsTheLeftParts(){
            _scheduler.RequestFocusUpdate(_shifted);
            _scheduler.ScheduleUpdate(_shifted);
            Assert.IsTrue(_scheduler.FocusPending);
            _scheduler.FocusUpdate(_shifted);
            Assert.IsFalse(_scheduler.FocusPending);
        }
    }
}
