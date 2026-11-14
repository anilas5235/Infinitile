using Engine.Scripts.ThirdParty.Priority_Queue;
using NUnit.Framework;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     <c>RemoveAll</c> of the scheduler queues: the focus update uses it to drop the entries that left a stage's
    ///     window, so a queue never holds more than the window it was reserved for.
    /// </summary>
    public class PriorityQueueTests{
        private static SimpleFastPriorityQueue<int2, float> QueueOfColumns(int count){
            SimpleFastPriorityQueue<int2, float> queue = new();
            queue.Reserve(count);
            for (int x = 0; x < count; x++) queue.Enqueue(new int2(x, 0), x); // the lowest x leaves first
            return queue;
        }

        [Test]
        public void RemoveAll_RemovesTheMatchingItems_AndKeepsTheOrderOfTheRest(){
            SimpleFastPriorityQueue<int2, float> queue = QueueOfColumns(10);

            int removed = queue.RemoveAll(4, static (limit, pos) => pos.x > limit);

            Assert.AreEqual(5, removed);
            Assert.AreEqual(5, queue.Count);
            Assert.IsTrue(queue.IsValidQueue());
            Assert.IsFalse(queue.Contains(new int2(5, 0)));
            for (int x = 0; x < 5; x++) Assert.AreEqual(new int2(x, 0), queue.Dequeue());
        }

        [Test]
        public void RemoveAll_RemovesEveryCopyOfAnItem(){
            SimpleFastPriorityQueue<int2, float> queue = QueueOfColumns(3);
            queue.Enqueue(new int2(1, 0), 7f); // a second copy of the same column

            int removed = queue.RemoveAll(1, static (x, pos) => pos.x == x);

            Assert.AreEqual(1, removed, "distinct items");
            Assert.AreEqual(2, queue.Count);
            Assert.IsFalse(queue.Contains(new int2(1, 0)));
            Assert.IsTrue(queue.IsValidQueue());
        }

        [Test]
        public void RemoveAll_WithoutAMatch_ChangesNothing(){
            SimpleFastPriorityQueue<int2, float> queue = QueueOfColumns(4);

            Assert.AreEqual(0, queue.RemoveAll(0, static (_, _) => false));

            Assert.AreEqual(4, queue.Count);
            Assert.AreEqual(new int2(0, 0), queue.Dequeue());
        }

        [Test]
        public void RemoveAll_OfEverything_LeavesAQueueThatCanBeRefilled(){
            SimpleFastPriorityQueue<int2, float> queue = QueueOfColumns(6);

            Assert.AreEqual(6, queue.RemoveAll(0, static (_, _) => true));
            Assert.AreEqual(0, queue.Count);

            queue.Enqueue(new int2(9, 9), 1f);
            queue.Enqueue(new int2(8, 8), 0f);
            Assert.AreEqual(new int2(8, 8), queue.Dequeue());
            Assert.AreEqual(new int2(9, 9), queue.Dequeue());
        }
    }
}
