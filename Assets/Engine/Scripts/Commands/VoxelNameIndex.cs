using System;
using System.Collections.Generic;

namespace Engine.Scripts.Commands{
    /// <summary>
    ///     Base voxel names (full ids, package:Name) for commands: lookup with the case ignored, and completion by the
    ///     start of the full id or of the name after the package.
    /// </summary>
    public sealed class VoxelNameIndex{
        private readonly Dictionary<string, (ushort id, string name)> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, string> _byId = new();
        private readonly List<string> _names = new();

        /// <param name="voxels">Id and full id of every base voxel; a name without a package colon is skipped.</param>
        public VoxelNameIndex(IEnumerable<(ushort id, string name)> voxels){
            foreach ((ushort id, string name) in voxels){
                if (string.IsNullOrEmpty(name) || name.IndexOf(':') <= 0 || _byName.ContainsKey(name)) continue;
                _byName[name] = (id, name);
                _byId.TryAdd(id, name);
                _names.Add(name);
            }

            _names.Sort(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>All full ids, sorted with the case ignored.</summary>
        public IReadOnlyList<string> Names => _names;

        public int Count => _names.Count;

        /// <summary>The voxel of a full id (case ignored) and its name as registered.</summary>
        public bool TryGet(string fullId, out ushort id, out string name){
            if (fullId != null && _byName.TryGetValue(fullId, out (ushort id, string name) entry)){
                id = entry.id;
                name = entry.name;
                return true;
            }

            id = 0;
            name = null;
            return false;
        }

        /// <summary>The full id of a voxel id; false for an id not in the index.</summary>
        public bool TryGetName(ushort id, out string name){
            return _byId.TryGetValue(id, out name);
        }

        /// <summary>
        ///     The full ids whose name (the part after the package) is <paramref name="name" />, case ignored; for a
        ///     "did you mean" when the package was left out.
        /// </summary>
        public void FindByShortName(string name, List<string> into){
            foreach (string full in _names)
                if (string.Equals(ShortName(full), name, StringComparison.OrdinalIgnoreCase))
                    into.Add(full);
        }

        /// <summary>
        ///     Adds the full ids that start with <paramref name="prefix" />, then those whose short name does, case
        ///     ignored; each list sorted.
        /// </summary>
        public void Complete(string prefix, List<string> into){
            prefix ??= "";
            foreach (string full in _names)
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    into.Add(full);
            if (prefix.Length == 0 || prefix.IndexOf(':') >= 0) return;
            foreach (string full in _names)
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    ShortName(full).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    into.Add(full);
        }

        private static string ShortName(string full){
            int colon = full.IndexOf(':');
            return colon >= 0 ? full[(colon + 1)..] : full;
        }
    }
}
