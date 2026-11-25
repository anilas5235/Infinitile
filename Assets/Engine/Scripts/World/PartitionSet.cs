using System.Collections.Generic;
using Unity.Mathematics;

namespace Engine.Scripts.World{
    /// <summary>
    ///     A set of partitions that also counts its entries per chunk column, so the scans of the mesh and bake
    ///     stages can skip a column without one in a single lookup instead of one per partition (DEC-232). Adding
    ///     and removing do not allocate once the capacity is reserved.
    /// </summary>
    internal sealed class PartitionSet{
        private readonly HashSet<int3> _partitions;
        private readonly Dictionary<int2, int> _perColumn;

        internal PartitionSet(int capacity, int columnCapacity){
            _partitions = new HashSet<int3>(capacity);
            _perColumn = new Dictionary<int2, int>(columnCapacity);
        }

        internal int Count => _partitions.Count;

        internal bool Contains(int3 partition){
            return _partitions.Contains(partition);
        }

        /// <returns>True if the partition was not in the set.</returns>
        internal bool Add(int3 partition){
            if (!_partitions.Add(partition)) return false;
            _perColumn.TryGetValue(partition.xz, out int count);
            _perColumn[partition.xz] = count + 1;
            return true;
        }

        /// <returns>True if the partition was in the set.</returns>
        internal bool Remove(int3 partition){
            if (!_partitions.Remove(partition)) return false;
            int count = _perColumn[partition.xz] - 1;
            if (count == 0) _perColumn.Remove(partition.xz);
            else _perColumn[partition.xz] = count;
            return true;
        }

        /// <summary>True when a partition of the column is in the set.</summary>
        internal bool AnyInColumn(int2 column){
            return _perColumn.ContainsKey(column);
        }
    }
}
