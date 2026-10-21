using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Light{
    /// <summary>
    ///     LGT-11: FIFO of box voxel indices for the light BFS of <see cref="LightRegion" />. A ring buffer that
    ///     doubles when full; <see cref="NativeQueue{T}" /> cost about 20 ns per element in the initial light job.
    ///     Single-threaded, for use inside one job.
    /// </summary>
    internal unsafe struct LightQueue : IDisposable{
        private int* _items;
        private int _mask;
        private int _head;
        private int _count;
        private readonly Allocator _allocator;

        public LightQueue(int capacity, Allocator allocator){
            int size = math.ceilpow2(math.max(capacity, 16));
            _allocator = allocator;
            _items = (int*)UnsafeUtility.Malloc(size * sizeof(int), UnsafeUtility.AlignOf<int>(), allocator);
            _mask = size - 1;
            _head = 0;
            _count = 0;
        }

        public bool IsCreated => _items != null;

        public int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(int index){
            if (_count > _mask) Grow();
            _items[(_head + _count) & _mask] = index;
            _count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out int index){
            if (_count == 0){
                index = 0;
                return false;
            }

            index = _items[_head];
            _head = (_head + 1) & _mask;
            _count--;
            return true;
        }

        public void Clear(){
            _head = 0;
            _count = 0;
        }

        private void Grow(){
            int size = _mask + 1;
            int* items = (int*)UnsafeUtility.Malloc(2 * size * sizeof(int), UnsafeUtility.AlignOf<int>(), _allocator);
            int first = size - _head;
            UnsafeUtility.MemCpy(items, _items + _head, first * sizeof(int));
            UnsafeUtility.MemCpy(items + first, _items, _head * sizeof(int));
            UnsafeUtility.Free(_items, _allocator);
            _items = items;
            _mask = 2 * size - 1;
            _head = 0;
        }

        public void Dispose(){
            if (_items == null) return;
            UnsafeUtility.Free(_items, _allocator);
            _items = null;
        }
    }
}
