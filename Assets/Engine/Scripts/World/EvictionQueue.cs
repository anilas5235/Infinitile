using System;
using System.Collections.Generic;
using Engine.Scripts.Jobs.Core;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.World{
    /// <summary>
    ///     The eviction queue of the chunk pool and the chunk store: positions ordered by
    ///     <see cref="PriorityUtil.EvictionPriority(ref int3, ref PriorityUtil.Focus)" /> around the queue's focus, the
    ///     farthest first; equal priorities leave in the order they came in. The dequeue order is the same as the
    ///     stable generic heap it replaces (DEC-236).
    ///     <para>
    ///         An indexed binary heap over a struct array: no node objects, no comparer delegates. A focus move
    ///         recomputes every priority and rebuilds the heap in one Burst call (<see cref="EvictionHeap.Reprioritise" />), and a
    ///         focus that moved only in the view direction, or only vertically for a planar queue, costs nothing.
    ///         Priorities always belong to the queue's own focus, so the heap never mixes two foci.
    ///     </para>
    /// </summary>
    internal sealed class EvictionQueue{
        /// <summary>A heap entry; <see cref="Slot" /> is the position's stable index into the slot table.</summary>
        public struct Entry{
            public int3 Pos;
            public float Priority;
            public long Order;
            public int Slot;
        }

        private readonly bool _planar;
        private readonly Dictionary<int3, int> _slotOf;
        private Entry[] _heap;
        private int[] _heapIndexOfSlot;
        private int[] _freeSlots;
        private int _freeCount;
        private int _count;
        private long _nextOrder;
        private PriorityUtil.Focus _focus;

        /// <param name="capacity">Entries the queue holds without growing.</param>
        /// <param name="planar">
        ///     Positions are columns (x, z); y is ignored, and the priority is the 2D ring around the focus's xz.
        /// </param>
        internal EvictionQueue(int capacity, bool planar){
            capacity = math.max(capacity, 1);
            _planar = planar;
            _slotOf = new Dictionary<int3, int>(capacity);
            _heap = new Entry[capacity];
            _heapIndexOfSlot = new int[capacity];
            _freeSlots = new int[capacity];
            for (int i = 0; i < capacity; i++) _freeSlots[i] = capacity - 1 - i; // slot 0 is handed out first
            _freeCount = capacity;
            _focus = default;
        }

        /// <summary>Entries in the queue.</summary>
        internal int Count => _count;

        /// <summary>The entry that leaves next (the farthest); throws when the queue is empty.</summary>
        internal int3 First{
            get{
                if (_count == 0) throw new InvalidOperationException("The eviction queue is empty");
                return _heap[0].Pos;
            }
        }

        /// <summary><see cref="First" /> of a planar queue as a column.</summary>
        internal int2 FirstColumn => First.xz;

        internal bool Contains(int3 pos){
            return _slotOf.ContainsKey(Key(pos));
        }

        internal bool Contains(int2 column){
            return _slotOf.ContainsKey(new int3(column.x, 0, column.y));
        }

        /// <summary>Adds a position with its priority around the queue's focus. A position may be queued once.</summary>
        internal void Enqueue(int3 pos){
            pos = Key(pos);
            if (_slotOf.ContainsKey(pos)) throw new InvalidOperationException($"{pos} is already queued");
            if (_count == _heap.Length) Grow();

            int slot = _freeSlots[--_freeCount];
            _slotOf.Add(pos, slot);
            Entry entry = new(){ Pos = pos, Priority = Priority(pos), Order = _nextOrder++, Slot = slot };
            SiftUp(_count++, entry);
        }

        internal void Enqueue(int2 column){
            Enqueue(new int3(column.x, 0, column.y));
        }

        /// <summary>Removes and returns the entry that leaves next; throws when the queue is empty.</summary>
        internal int3 Dequeue(){
            int3 first = First;
            RemoveAt(0);
            return first;
        }

        /// <summary><see cref="Dequeue" /> of a planar queue as a column.</summary>
        internal int2 DequeueColumn(){
            return Dequeue().xz;
        }

        /// <summary>Removes the position if it is queued; returns whether it was.</summary>
        internal bool TryRemove(int3 pos){
            if (!_slotOf.TryGetValue(Key(pos), out int slot)) return false;
            RemoveAt(_heapIndexOfSlot[slot]);
            return true;
        }

        internal bool TryRemove(int2 column){
            return TryRemove(new int3(column.x, 0, column.y));
        }

        /// <summary>Removes every entry; the focus stays.</summary>
        internal void Clear(){
            _slotOf.Clear();
            _count = 0;
            for (int i = 0; i < _heap.Length; i++) _freeSlots[i] = _heap.Length - 1 - i;
            _freeCount = _heap.Length;
        }

        /// <summary>
        ///     Moves the queue's focus and reorders every entry for it. Nothing happens when the priorities do not
        ///     change: the eviction priority ignores the view direction, and a planar queue ignores y.
        /// </summary>
        internal void UpdateFocus(PriorityUtil.Focus focus){
            bool same = _planar ? focus.Pos.xz.Equals(_focus.Pos.xz) : focus.Pos.Equals(_focus.Pos);
            _focus = focus;
            if (same || _count == 0) return;
            unsafe{
                fixed (Entry* heap = _heap)
                fixed (int* heapIndexOfSlot = _heapIndexOfSlot){
                    EvictionHeap.Reprioritise(heap, heapIndexOfSlot, _count, ref focus, _planar ? 1 : 0);
                }
            }
        }

        private int3 Key(int3 pos){
            return _planar ? new int3(pos.x, 0, pos.z) : pos;
        }

        private float Priority(int3 pos){
            if (!_planar) return PriorityUtil.EvictionPriority(ref pos, ref _focus);
            int2 column = pos.xz;
            return PriorityUtil.EvictionPriority(ref column, ref _focus);
        }

        private void RemoveAt(int index){
            ref Entry removed = ref _heap[index];
            _slotOf.Remove(removed.Pos);
            _freeSlots[_freeCount++] = removed.Slot;

            int last = --_count;
            if (index == last) return;
            Entry moved = _heap[last];
            // The moved entry came from the bottom: it goes down, or up when it leaves before the removed one's parent
            if (index > 0 && EvictionHeap.Before(ref moved, ref _heap[(index - 1) >> 1])) SiftUp(index, moved);
            else SiftDown(index, moved);
        }

        /// <summary>Places <paramref name="entry" /> at the hole <paramref name="index" /> and moves it up.</summary>
        private void SiftUp(int index, Entry entry){
            while (index > 0){
                int parent = (index - 1) >> 1;
                ref Entry p = ref _heap[parent];
                if (!EvictionHeap.Before(ref entry, ref p)) break;
                _heap[index] = p;
                _heapIndexOfSlot[p.Slot] = index;
                index = parent;
            }

            _heap[index] = entry;
            _heapIndexOfSlot[entry.Slot] = index;
        }

        /// <summary>Places <paramref name="entry" /> at the hole <paramref name="index" /> and moves it down.</summary>
        private void SiftDown(int index, Entry entry){
            while (true){
                int child = 2 * index + 1;
                if (child >= _count) break;
                if (child + 1 < _count && EvictionHeap.Before(ref _heap[child + 1], ref _heap[child])) child++;
                ref Entry c = ref _heap[child];
                if (!EvictionHeap.Before(ref c, ref entry)) break;
                _heap[index] = c;
                _heapIndexOfSlot[c.Slot] = index;
                index = child;
            }

            _heap[index] = entry;
            _heapIndexOfSlot[entry.Slot] = index;
        }

        private void Grow(){
            int old = _heap.Length;
            int size = old * 2;
            Array.Resize(ref _heap, size);
            Array.Resize(ref _heapIndexOfSlot, size);
            int[] free = new int[size];
            Array.Copy(_freeSlots, free, _freeCount);
            for (int slot = size - 1; slot >= old; slot--) free[_freeCount++] = slot;
            _freeSlots = free;
        }

        /// <summary>Tests: whether the heap order and the slot table hold.</summary>
        internal bool IsValid(){
            if (_slotOf.Count != _count || _freeCount + _count != _heap.Length) return false;
            for (int i = 0; i < _count; i++){
                if (_heapIndexOfSlot[_heap[i].Slot] != i) return false;
                if (!_slotOf.TryGetValue(_heap[i].Pos, out int slot) || slot != _heap[i].Slot) return false;
                if (i > 0 && EvictionHeap.Before(ref _heap[i], ref _heap[(i - 1) >> 1])) return false;
            }

            return true;
        }
    }

    /// <summary>The Burst part of <see cref="EvictionQueue" />: the heap rebuild after a focus move.</summary>
    [BurstCompile]
    internal static unsafe class EvictionHeap{
        /// <summary>
        ///     Recomputes the priority of the first <paramref name="count" /> entries for <paramref name="focus" />,
        ///     rebuilds the heap (Floyd) and the slot table. Burst direct call; runs managed when Burst is off.
        /// </summary>
        [BurstCompile]
        public static void Reprioritise(EvictionQueue.Entry* heap, int* heapIndexOfSlot, int count,
            ref PriorityUtil.Focus focus, int planar){
            if (planar != 0){
                for (int i = 0; i < count; i++){
                    int2 column = heap[i].Pos.xz;
                    heap[i].Priority = PriorityUtil.EvictionPriority(ref column, ref focus);
                }
            }
            else{
                for (int i = 0; i < count; i++) heap[i].Priority = PriorityUtil.EvictionPriority(ref heap[i].Pos, ref focus);
            }

            for (int i = (count >> 1) - 1; i >= 0; i--){
                EvictionQueue.Entry entry = heap[i];
                int hole = i;
                while (true){
                    int child = 2 * hole + 1;
                    if (child >= count) break;
                    if (child + 1 < count && Before(ref heap[child + 1], ref heap[child])) child++;
                    if (!Before(ref heap[child], ref entry)) break;
                    heap[hole] = heap[child];
                    hole = child;
                }

                heap[hole] = entry;
            }

            for (int i = 0; i < count; i++) heapIndexOfSlot[heap[i].Slot] = i;
        }

        /// <summary>Whether <paramref name="a" /> leaves before <paramref name="b" />.</summary>
        internal static bool Before(ref EvictionQueue.Entry a, ref EvictionQueue.Entry b){
            return a.Priority < b.Priority || (a.Priority == b.Priority && a.Order < b.Order);
        }
    }
}
