using System.Collections.Generic;
using System.Reflection;
using Engine.Scripts.Jobs.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>Priority ordering, Focus, vertical view directions and the scheduler's AvgTime window.</summary>
    public class PriorityUtilTests{
        private static PriorityUtil.Focus LookingForward(){
            return new PriorityUtil.Focus(int3.zero, new float3(0, 0, 1));
        }

        private static float Prio(int3 position, PriorityUtil.Focus focus){
            return PriorityUtil.DistPriority(ref position, ref focus);
        }

        private static float Prio(int2 position, PriorityUtil.Focus focus){
            return PriorityUtil.DistPriority(ref position, ref focus);
        }

        #region Eviction priority

        [Test]
        public void EvictionPriority_IsPlainDistance_IgnoringViewDirection(){
            // Eviction must not weight chunks behind the player x5.
            PriorityUtil.Focus focus = LookingForward();
            int3 behind = new(0, 0, -3);
            int3 front = new(0, 0, 6);
            Assert.AreEqual(-9f, PriorityUtil.EvictionPriority(ref behind, ref focus));
            Assert.AreEqual(-36f, PriorityUtil.EvictionPriority(ref front, ref focus));
            Assert.Greater(PriorityUtil.EvictionPriority(ref behind, ref focus), PriorityUtil.EvictionPriority(ref front, ref focus),
                "the farther chunk must have the lower (first dequeued) priority");

            int2 behind2 = new(0, -3);
            int2 front2 = new(0, 6);
            Assert.AreEqual(-9f, PriorityUtil.EvictionPriority(ref behind2, ref focus));
            Assert.AreEqual(-36f, PriorityUtil.EvictionPriority(ref front2, ref focus));
        }

        #endregion

        #region Priority ordering

        [Test]
        public void DistPriority_CloserIsLowerValue(){
            PriorityUtil.Focus focus = LookingForward();
            Assert.AreEqual(0f, Prio(int3.zero, focus));
            Assert.Less(Prio(new int3(0, 0, 1), focus), Prio(new int3(0, 0, 5), focus));
            Assert.Less(Prio(new int3(1, 0, 0), focus), Prio(new int3(3, 0, 0), focus));
            Assert.Less(Prio(new int3(0, 1, 0), focus), Prio(new int3(0, 4, 0), focus));

            Assert.AreEqual(0f, Prio(int2.zero, focus));
            Assert.Less(Prio(new int2(0, 1), focus), Prio(new int2(0, 5), focus));
            Assert.Less(Prio(new int2(1, 0), focus), Prio(new int2(3, 0), focus));
        }

        [Test]
        public void DistPriority_InViewDirectionRanksBeforeBehind(){
            PriorityUtil.Focus focus = LookingForward();
            Assert.Less(Prio(new int3(0, 0, 5), focus), Prio(new int3(0, 0, -5), focus));
            Assert.Less(Prio(new int3(0, 0, 5), focus), Prio(new int3(5, 0, 0), focus), "ahead before sideways");
            Assert.Less(Prio(new int3(5, 0, 0), focus), Prio(new int3(0, 0, -5), focus), "sideways before behind");
            Assert.Less(Prio(new int2(0, 5), focus), Prio(new int2(0, -5), focus));
            Assert.Less(Prio(new int2(0, 5), focus), Prio(new int2(5, 0), focus));
        }

        [Test]
        public void Focus_EqualityAndHashCode(){
            PriorityUtil.Focus a = new(new int3(1, 2, 3), new float3(0, 0, 1));
            PriorityUtil.Focus b = new(new int3(1, 2, 3), new float3(0, 0, 1));
            PriorityUtil.Focus quantisedSame = new(new int3(1, 2, 3), new float3(0.1f, 0.05f, 5f));
            PriorityUtil.Focus otherPos = new(new int3(1, 2, 4), new float3(0, 0, 1));
            PriorityUtil.Focus otherDir = new(new int3(1, 2, 3), new float3(1, 0, 0));

            Assert.IsTrue(a.Equals(b));
            Assert.IsTrue(a.Equals((object)b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.IsTrue(a.Equals(quantisedSame), "forward is quantised to 45 degree steps");
            Assert.AreEqual(a.GetHashCode(), quantisedSame.GetHashCode());
            Assert.IsFalse(a.Equals(otherPos));
            Assert.IsFalse(a.Equals(otherDir));
            Assert.IsFalse(a.Equals(null));
        }

        #endregion

        #region Vertical forward and AvgTime

        [Test]
        public void DistPriority2D_VerticalForward_GivesFinitePriorities(){
            // Forward (0, ±1, 0) has xz = (0, 0); the 2D overload must not produce NaN.
            PriorityUtil.Focus up = new(int3.zero, new float3(0, 1, 0));
            PriorityUtil.Focus down = new(int3.zero, new float3(0, -1, 0));
            int2 position = new(3, 4);
            Assert.IsTrue(float.IsFinite(Prio(position, up)), $"up: {Prio(position, up)}");
            Assert.IsTrue(float.IsFinite(Prio(position, down)), $"down: {Prio(position, down)}");
            Assert.Less(Prio(new int2(1, 0), up), Prio(new int2(5, 0), up), "closer still ranks first");
        }

        [Test]
        public void DistPriority3D_VerticalForward_GivesFinitePriorities(){
            PriorityUtil.Focus up = new(int3.zero, new float3(0, 1, 0));
            int3 position = new(3, 4, 5);
            Assert.IsTrue(float.IsFinite(Prio(position, up)));
            Assert.Less(Prio(new int3(0, 2, 0), up), Prio(new int3(0, -2, 0), up), "looking up: above ranks before below");
        }

        private sealed class TestScheduler : JobScheduler{
            public TestScheduler(int records) : base(records){ }

            public void Record(double milliseconds){
                RecordTiming(milliseconds); // the same window handling StopRecord uses, without a Stopwatch
            }

            public Queue<double> Timings =>
                (Queue<double>)typeof(JobScheduler).GetField("_timings", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(this);
        }

        [Test]
        public void AvgTime_OfConstantTimings_EqualsThatConstant(){
            // AvgTime divides by the number of samples, not by a constant.
            TestScheduler scheduler = new(16);
            for (int i = 0; i < 4; i++) scheduler.Record(5);
            Assert.AreEqual(5f, scheduler.AvgTime, 1e-5f, "4 samples of 5 ms");
            for (int i = 0; i < 30; i++) scheduler.Record(5);
            Assert.AreEqual(5f, scheduler.AvgTime, 1e-5f, "window full of 5 ms samples");
        }

        [Test]
        public void AvgTime_WindowIsBoundedByRecordCount(){
            // The queue must not keep more than records entries.
            TestScheduler scheduler = new(16);
            for (int i = 0; i < 40; i++) scheduler.Record(1);
            Assert.LessOrEqual(scheduler.Timings.Count, 16);
        }

        [Test]
        public void AvgTime_KeepsSubMillisecondTimings(){
            // Runs below 1 ms must not be truncated to 0.
            TestScheduler scheduler = new(16);
            for (int i = 0; i < 4; i++) scheduler.Record(0.4);
            Assert.AreEqual(0.4f, scheduler.AvgTime, 1e-5f);
        }

        #endregion
    }
}
