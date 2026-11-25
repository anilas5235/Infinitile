using System.Collections.Generic;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     <see cref="EvictionQueue" /> (DEC-236) must dequeue in exactly the order of the stable generic heap it
    ///     replaced: eviction priority around the focus, ties by insertion order.
    /// </summary>
    public class EvictionQueueTests{
        private unsafe delegate void ReprioritiseFn(EvictionQueue.Entry* heap, int* heapIndexOfSlot, int count,
            ref PriorityUtil.Focus focus, int planar);

        private static PriorityUtil.Focus FocusAt(int3 pos){
            return new PriorityUtil.Focus(pos, new float3(0, 0, 1));
        }

        /// <summary>
        ///     Random enqueues, removals, dequeues and focus moves on both queues; every dequeue, every head and the
        ///     final drain must agree.
        /// </summary>
        [TestCase(1u, false)]
        [TestCase(7u, false)]
        [TestCase(1u, true)]
        [TestCase(7u, true)]
        public void RandomOperations_MatchTheGenericHeap(uint seed, bool planar){
            Random rng = new(seed);
            EvictionQueue queue = new(16, planar); // small on purpose: the queue has to grow
            SimpleFastPriorityQueue<int3, float> reference = new();
            HashSet<int3> queued = new();
            List<int3> queuedList = new();
            PriorityUtil.Focus focus = FocusAt(int3.zero);

            float Priority(int3 p){
                if (!planar) return PriorityUtil.EvictionPriority(ref p, ref focus);
                int2 column = p.xz;
                return PriorityUtil.EvictionPriority(ref column, ref focus);
            }

            int3 RandomPos(){
                return new int3(rng.NextInt(-12, 13), planar ? 0 : rng.NextInt(0, 16), rng.NextInt(-12, 13));
            }

            for (int step = 0; step < 6000; step++){
                int op = rng.NextInt(0, 100);
                if (op < 45){
                    int3 p = RandomPos();
                    if (queued.Add(p)){
                        queuedList.Add(p);
                        queue.Enqueue(p);
                        reference.Enqueue(p, Priority(p));
                    }
                }
                else if (op < 60 && queuedList.Count > 0){
                    int i = rng.NextInt(0, queuedList.Count);
                    int3 p = queuedList[i];
                    queuedList[i] = queuedList[queuedList.Count - 1];
                    queuedList.RemoveAt(queuedList.Count - 1);
                    queued.Remove(p);
                    Assert.IsTrue(queue.TryRemove(p));
                    reference.Remove(p);
                    Assert.IsFalse(queue.TryRemove(p), "a removed position is gone");
                }
                else if (op < 85 && reference.Count > 0){
                    int3 expected = reference.Dequeue();
                    Assert.AreEqual(expected, queue.Dequeue(), $"dequeue at step {step}");
                    queued.Remove(expected);
                    queuedList.Remove(expected);
                }
                else if (op < 97){
                    // Mostly one step, sometimes only the view direction or only y, sometimes a jump
                    int3 move = rng.NextInt(0, 10) switch{
                        0 => int3.zero,
                        1 => new int3(0, rng.NextInt(-2, 3), 0),
                        2 => new int3(rng.NextInt(-20, 21), rng.NextInt(-8, 9), rng.NextInt(-20, 21)),
                        _ => new int3(rng.NextInt(-1, 2), rng.NextInt(-1, 2), rng.NextInt(-1, 2))
                    };
                    float3 forward = rng.NextFloat3Direction();
                    focus = new PriorityUtil.Focus(focus.Pos + move, forward);
                    queue.UpdateFocus(focus);
                    reference.UpdateAllPriorities(focus, (f, p) => Priority(p));
                }

                Assert.AreEqual(reference.Count, queue.Count);
                if (reference.Count > 0) Assert.AreEqual(reference.First, queue.First, $"head at step {step}");
                if (step % 500 == 0) Assert.IsTrue(queue.IsValid(), $"heap invalid at step {step}");
            }

            Assert.IsTrue(queue.IsValid());
            while (reference.Count > 0) Assert.AreEqual(reference.Dequeue(), queue.Dequeue());
            Assert.AreEqual(0, queue.Count);
        }

        [Test]
        public void EqualPriorities_LeaveInInsertionOrder(){
            EvictionQueue queue = new(8, false);
            // The eight ring-1 corners of the focus share one priority
            int3[] corners ={
                new(1, 1, 1), new(-1, 1, 1), new(1, -1, 1), new(-1, -1, 1),
                new(1, 1, -1), new(-1, 1, -1), new(1, -1, -1), new(-1, -1, -1)
            };
            foreach (int3 c in corners) queue.Enqueue(c);
            queue.UpdateFocus(FocusAt(new int3(0, 0, 1))); // reorder once, ties must still keep their order
            queue.UpdateFocus(FocusAt(int3.zero));
            foreach (int3 c in corners) Assert.AreEqual(c, queue.Dequeue());
        }

        [Test]
        public void FarthestLeavesFirst_ByRingNotStraightDistance(){
            EvictionQueue queue = new(4, false);
            queue.Enqueue(new int3(3, 0, 3)); // corner of ring 3, straight distance 4.2
            queue.Enqueue(new int3(4, 0, 0)); // ring 4, straight distance 4
            queue.Enqueue(new int3(1, 0, 0));
            Assert.AreEqual(new int3(4, 0, 0), queue.Dequeue());
            Assert.AreEqual(new int3(3, 0, 3), queue.Dequeue());
        }

        [Test]
        public void Planar_IgnoresY_AndAFocusMoveOnlyInY(){
            EvictionQueue queue = new(4, true);
            queue.Enqueue(new int2(2, 0));
            queue.Enqueue(new int2(-5, 0));
            Assert.IsTrue(queue.Contains(new int3(2, 9, 0)), "a planar queue keys by column");
            queue.UpdateFocus(FocusAt(new int3(0, 7, 0)));
            Assert.AreEqual(new int2(-5, 0), queue.FirstColumn);
            queue.UpdateFocus(FocusAt(new int3(-6, 3, 0)));
            Assert.AreEqual(new int2(2, 0), queue.DequeueColumn());
            Assert.IsTrue(queue.TryRemove(new int2(-5, 0)));
            Assert.AreEqual(0, queue.Count);
        }

        [Test]
        public void Enqueue_Twice_Throws(){
            EvictionQueue queue = new(2, false);
            queue.Enqueue(int3.zero);
            Assert.Throws<System.InvalidOperationException>(() => queue.Enqueue(int3.zero));
        }

        [Test]
        public void Clear_LeavesAQueueThatCanBeRefilled(){
            EvictionQueue queue = new(4, false);
            for (int i = 0; i < 10; i++) queue.Enqueue(new int3(i, 0, 0)); // grows twice
            queue.Clear();
            Assert.AreEqual(0, queue.Count);
            Assert.IsFalse(queue.Contains(new int3(3, 0, 0)));
            for (int i = 0; i < 10; i++) queue.Enqueue(new int3(0, 0, i));
            Assert.IsTrue(queue.IsValid());
            Assert.AreEqual(new int3(0, 0, 9), queue.First);
        }

        [Test]
        public unsafe void Reprioritise_CompilesWithBurst(){
            // Burst guard for the direct-call entry point (see WorldSamplerTests.SampleColumn_CompilesWithBurst):
            // a Burst error would leave the re-heap managed, slower but green.
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst compilation is disabled in the Editor (Jobs > Burst)");
            FunctionPointer<ReprioritiseFn> fp = BurstCompiler.CompileFunctionPointer<ReprioritiseFn>(EvictionHeap.Reprioritise);
            Assert.IsTrue(fp.IsCreated);

            EvictionQueue.Entry[] heap = new EvictionQueue.Entry[3];
            for (int i = 0; i < 3; i++) heap[i] = new EvictionQueue.Entry{ Pos = new int3(i, 0, 0), Order = i, Slot = i };
            int[] index = new int[3];
            PriorityUtil.Focus focus = FocusAt(int3.zero);
            fixed (EvictionQueue.Entry* h = heap)
            fixed (int* x = index){
                fp.Invoke(h, x, 3, ref focus, 0);
            }

            Assert.AreEqual(new int3(2, 0, 0), heap[0].Pos, "the farthest is the head");
            Assert.AreEqual(0, index[heap[0].Slot]);
            int3 p = new(2, 0, 0);
            Assert.AreEqual(PriorityUtil.EvictionPriority(ref p, ref focus), heap[0].Priority);
        }
    }
}
