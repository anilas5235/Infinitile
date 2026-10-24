using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Engine.Scripts.ThirdParty.Priority_Queue{
    /// <summary>
    ///     A simplified priority queue implementation.  Is stable, auto-resizes, and thread-safe, at the cost of being
    ///     slightly slower than
    ///     FastPriorityQueue
    ///     Methods tagged as O(1) or O(log n) are assuming there are no duplicates.  Duplicates may increase the algorithmic
    ///     complexity.
    /// </summary>
    /// <typeparam name="TItem">The type to enqueue</typeparam>
    /// <typeparam name="TPriority">The priority-type to use for nodes.  Must extend IComparable&lt;TPriority&gt;</typeparam>
    public class SimpleFastPriorityQueue<TItem, TPriority> : IPriorityQueue<TItem, TPriority>{
        private const int INITIAL_QUEUE_SIZE = 10;
        private readonly Dictionary<TItem, IList<SimpleNode>> _itemToNodesCache;
        private readonly IList<SimpleNode> _nullNodesCache;
        private readonly GenericPriorityQueue<SimpleNode, TPriority> _queue;

        // "item == null" on an unconstrained TItem boxes every value-type item under Mono; the null checks
        // are skipped entirely for value types.
        private static readonly bool ItemIsValueType = typeof(TItem).IsValueType;

        private static bool IsNull(TItem item){
            return !ItemIsValueType && item == null;
        }

        // Nodes and per-item lists are pooled so steady-state Enqueue/Dequeue do not allocate.
        private readonly Stack<SimpleNode> _nodePool = new();
        private readonly Stack<List<SimpleNode>> _listPool = new();

        /// <summary>
        ///     Returns the number of nodes in the queue.
        ///     O(1)
        /// </summary>
        public int Count => _queue.Count;

        /// <summary>
        ///     Returns the head of the queue, without removing it (use Dequeue() for that).
        ///     Throws an exception when the queue is empty.
        ///     O(1)
        /// </summary>
        public TItem First{
            get{
                if (_queue.Count <= 0) throw new InvalidOperationException("Cannot call .First on an empty queue");

                return _queue.First.Data;
            }
        }

        /// <summary>
        ///     Pre-sizes the heap, the item cache and the node/list pools so that growing to
        ///     <paramref name="capacity" /> items later does not allocate.
        /// </summary>
        public void Reserve(int capacity){
            if (capacity <= 0) return;
            if (_queue.MaxSize < capacity) _queue.Resize(capacity);
            _itemToNodesCache.EnsureCapacity(capacity);
            for (int i = _nodePool.Count + _queue.Count; i < capacity; i++) _nodePool.Push(new SimpleNode(default));
            for (int i = _listPool.Count + _itemToNodesCache.Count; i < capacity; i++) _listPool.Push(new List<SimpleNode>(1));
        }

        /// <summary>
        ///     Removes every node from the queue.
        ///     O(n)
        /// </summary>
        public void Clear(){
            _queue.Clear();
            foreach (IList<SimpleNode> nodes in _itemToNodesCache.Values) ReturnList(nodes);
            _itemToNodesCache.Clear();
            _nullNodesCache.Clear();
        }

        private List<SimpleNode> RentList(){
            return _listPool.Count > 0 ? _listPool.Pop() : new List<SimpleNode>();
        }

        private void ReturnList(IList<SimpleNode> nodes){
            if (nodes is not List<SimpleNode> list) return;
            list.Clear();
            _listPool.Push(list);
        }

        private void Recycle(SimpleNode node){
            node.Data = default;
            _queue.ResetNode(node);
            _nodePool.Push(node);
        }

        /// <summary>
        ///     Returns whether the given item is in the queue.
        ///     O(1)
        /// </summary>
        public bool Contains(TItem item){
            return IsNull(item) ? _nullNodesCache.Count > 0 : _itemToNodesCache.ContainsKey(item);
        }

        /// <summary>
        ///     Removes the head of the queue (node with minimum priority; ties are broken by order of insertion), and returns it.
        ///     If queue is empty, throws an exception
        ///     O(log n)
        /// </summary>
        public TItem Dequeue(){
            if (_queue.Count <= 0) throw new InvalidOperationException("Cannot call Dequeue() on an empty queue");

            SimpleNode node = _queue.Dequeue();
            RemoveFromNodeCache(node);
            TItem data = node.Data;
            Recycle(node);
            return data;
        }

        /// <summary>
        ///     Enqueue a node to the priority queue.  Lower values are placed in front. Ties are broken by first-in-first-out.
        ///     This queue automatically resizes itself, so there's no concern of the queue becoming 'full'.
        ///     Duplicates and null-values are allowed.
        ///     O(log n)
        /// </summary>
        public void Enqueue(TItem item, TPriority priority){
            IList<SimpleNode> nodes;
            if (IsNull(item))
                nodes = _nullNodesCache;
            else if (!_itemToNodesCache.TryGetValue(item, out nodes)){
                nodes = RentList();
                _itemToNodesCache[item] = nodes;
            }

            SimpleNode node = EnqueueNoLockOrCache(item, priority);
            nodes.Add(node);
        }

        /// <summary>
        ///     Removes an item from the queue.  The item does not need to be the head of the queue.
        ///     If the item is not in the queue, an exception is thrown.  If unsure, check Contains() first.
        ///     If multiple copies of the item are enqueued, only the first one is removed.
        ///     O(log n)
        /// </summary>
        public void Remove(TItem item){
            SimpleNode removeMe;
            IList<SimpleNode> nodes;
            if (IsNull(item)){
                if (_nullNodesCache.Count == 0){
                    throw new InvalidOperationException("Cannot call Remove() on a node which is not enqueued: " +
                                                        item);
                }

                removeMe = _nullNodesCache[0];
                nodes = _nullNodesCache;
            }
            else{
                if (!_itemToNodesCache.TryGetValue(item, out nodes)) return;

                removeMe = nodes[0];
                if (nodes.Count == 1) _itemToNodesCache.Remove(item);
            }

            _queue.Remove(removeMe);
            nodes.Remove(removeMe);
            if (!IsNull(item) && nodes.Count == 0) ReturnList(nodes);
            Recycle(removeMe);
        }

        /// <summary>
        ///     Call this method to change the priority of an item.
        ///     Calling this method on an item not in the queue will throw an exception.
        ///     If the item is enqueued multiple times, only the first one will be updated.
        ///     (If your requirements are complex enough that you need to enqueue the same item multiple times <i>and</i> be able
        ///     to update all of them, please wrap your items in a wrapper class so they can be distinguished).
        ///     O(log n)
        /// </summary>
        public void UpdatePriority(TItem item, TPriority priority){
            SimpleNode updateMe = GetExistingNode(item);
            if (updateMe == null){
                throw new InvalidOperationException("Cannot call UpdatePriority() on a node which is not enqueued: " +
                                                    item);
            }

            _queue.UpdatePriority(updateMe, priority);
        }

        public IEnumerator<TItem> GetEnumerator(){
            return _queue.Select(x => x.Data).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator(){
            return GetEnumerator();
        }

        /// <summary>
        ///     Given an item of type T, returns the existing SimpleNode in the queue
        /// </summary>
        private SimpleNode GetExistingNode(TItem item){
            if (IsNull(item)) return _nullNodesCache.Count > 0 ? _nullNodesCache[0] : null;

            if (!_itemToNodesCache.TryGetValue(item, out IList<SimpleNode> nodes)) return null;

            return nodes[0];
        }

        /// <summary>
        ///     Adds an item to the Node-cache to allow for many methods to be O(1) or O(log n)
        /// </summary>
        private void AddToNodeCache(SimpleNode node){
            if (IsNull(node.Data)){
                _nullNodesCache.Add(node);
                return;
            }

            if (!_itemToNodesCache.TryGetValue(node.Data, out IList<SimpleNode> nodes)){
                nodes = RentList();
                _itemToNodesCache[node.Data] = nodes;
            }

            nodes.Add(node);
        }

        /// <summary>
        ///     Removes an item to the Node-cache to allow for many methods to be O(1) or O(log n) (assuming no duplicates)
        /// </summary>
        private void RemoveFromNodeCache(SimpleNode node){
            if (IsNull(node.Data)){
                _nullNodesCache.Remove(node);
                return;
            }

            if (!_itemToNodesCache.TryGetValue(node.Data, out IList<SimpleNode> nodes)) return;

            nodes.Remove(node);
            if (nodes.Count != 0) return;
            _itemToNodesCache.Remove(node.Data);
            ReturnList(nodes);
        }

        /// <summary>
        ///     Enqueue the item with the given priority, without calling lock(_queue) or AddToNodeCache(node)
        /// </summary>
        /// <param name="item"></param>
        /// <param name="priority"></param>
        /// <returns></returns>
        private SimpleNode EnqueueNoLockOrCache(TItem item, TPriority priority){
            SimpleNode node;
            if (_nodePool.Count > 0){
                node = _nodePool.Pop();
                node.Data = item;
            }
            else node = new SimpleNode(item);

            if (_queue.Count == _queue.MaxSize) _queue.Resize(_queue.MaxSize * 2 + 1);

            _queue.Enqueue(node, priority);
            return node;
        }

        /// <summary>
        ///     Enqueue a node to the priority queue if it doesn't already exist.  Lower values are placed in front. Ties are
        ///     broken by first-in-first-out.
        ///     This queue automatically resizes itself, so there's no concern of the queue becoming 'full'.  Null values are
        ///     allowed.
        ///     Returns true if the node was successfully enqueued; false if it already exists.
        ///     O(log n)
        /// </summary>
        public bool EnqueueWithoutDuplicates(TItem item, TPriority priority){
            IList<SimpleNode> nodes;
            if (IsNull(item)){
                if (_nullNodesCache.Count > 0) return false;

                nodes = _nullNodesCache;
            }
            else if (_itemToNodesCache.ContainsKey(item))
                return false;
            else{
                nodes = RentList();
                _itemToNodesCache[item] = nodes;
            }

            SimpleNode node = EnqueueNoLockOrCache(item, priority);
            nodes.Add(node);
            return true;
        }

        public void UpdateAllPriorities(Func<TItem, TPriority> updateFunc){
            _queue.UpdateAllPriorities(updateFunc, static (f, node) => f(node.Data));
        }

        /// <summary>
        ///     Allocation-free variant; pass the state explicitly and a static lambda so no closure
        ///     is created per call.
        /// </summary>
        public void UpdateAllPriorities<TState>(TState state, Func<TState, TItem, TPriority> updateFunc){
            _queue.UpdateAllPriorities((state, updateFunc), static (s, node) => s.updateFunc(s.state, node.Data));
        }

        /// <summary>
        ///     Returns the priority of the given item.
        ///     Calling this method on an item not in the queue will throw an exception.
        ///     If the item is enqueued multiple times, only the priority of the first will be returned.
        ///     (If your requirements are complex enough that you need to enqueue the same item multiple times <i>and</i> be able
        ///     to query all their priorities, please wrap your items in a wrapper class so they can be distinguished).
        ///     O(1)
        /// </summary>
        public TPriority GetPriority(TItem item){
            SimpleNode findMe = GetExistingNode(item);
            if (findMe == null){
                throw new InvalidOperationException(
                    "Cannot call GetPriority() on a node which is not enqueued: " + item);
            }

            return findMe.Priority;
        }

        public bool IsValidQueue(){
            // Check all items in cache are in the queue
            foreach (IList<SimpleNode> nodes in _itemToNodesCache.Values)
            foreach (SimpleNode node in nodes){
                if (!_queue.Contains(node))
                    return false;
            }

            // Check all items in queue are in cache
            foreach (SimpleNode node in _queue){
                if (GetExistingNode(node.Data) == null)
                    return false;
            }

            // Check queue structure itself
            return _queue.IsValidQueue();
        }

        private class SimpleNode : GenericPriorityQueueNode<TPriority>{
            public SimpleNode(TItem data){
                Data = data;
            }

            public TItem Data{ get; set; }
        }

        #region Constructors

        /// <summary>
        ///     Instantiate a new Priority Queue
        /// </summary>
        public SimpleFastPriorityQueue() : this(Comparer<TPriority>.Default, EqualityComparer<TItem>.Default){ }

        /// <summary>
        ///     Instantiate a new Priority Queue
        /// </summary>
        /// <param name="priorityComparer">
        ///     The comparer used to compare TPriority values.  Defaults to Comparer&lt;TPriority&gt;
        ///     .default
        /// </param>
        public SimpleFastPriorityQueue(IComparer<TPriority> priorityComparer) : this(priorityComparer.Compare,
            EqualityComparer<TItem>.Default){ }

        /// <summary>
        ///     Instantiate a new Priority Queue
        /// </summary>
        /// <param name="priorityComparer">The comparison function to use to compare TPriority values</param>
        public SimpleFastPriorityQueue(Comparison<TPriority> priorityComparer) : this(priorityComparer,
            EqualityComparer<TItem>.Default){ }

        /// <summary>
        ///     Instantiate a new Priority Queue
        /// </summary>
        /// <param name="itemEquality">The equality comparison function to use to compare TItem values</param>
        public SimpleFastPriorityQueue(IEqualityComparer<TItem> itemEquality) : this(Comparer<TPriority>.Default,
            itemEquality){ }

        /// <summary>
        ///     Instantiate a new Priority Queue
        /// </summary>
        /// <param name="priorityComparer">
        ///     The comparer used to compare TPriority values.  Defaults to Comparer&lt;TPriority&gt;
        ///     .default
        /// </param>
        /// <param name="itemEquality">The equality comparison function to use to compare TItem values</param>
        public SimpleFastPriorityQueue(IComparer<TPriority> priorityComparer, IEqualityComparer<TItem> itemEquality) :
            this(priorityComparer.Compare, itemEquality){ }

        /// <summary>
        ///     Instantiate a new Priority Queue
        /// </summary>
        /// <param name="priorityComparer">The comparison function to use to compare TPriority values</param>
        /// <param name="itemEquality">The equality comparison function to use to compare TItem values</param>
        public SimpleFastPriorityQueue(Comparison<TPriority> priorityComparer, IEqualityComparer<TItem> itemEquality){
            _queue = new GenericPriorityQueue<SimpleNode, TPriority>(INITIAL_QUEUE_SIZE, priorityComparer);
            _itemToNodesCache = new Dictionary<TItem, IList<SimpleNode>>(itemEquality);
            _nullNodesCache = new List<SimpleNode>();
        }

        #endregion
    }
}
