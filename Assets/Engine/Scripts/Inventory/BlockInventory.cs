using System;
using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.Inventory{
    /// <summary>How the inventory counts blocks. Only <see cref="Creative" /> is played for now.</summary>
    public enum InventoryMode : byte{
        /// <summary>Every block is unlimited; no counts are shown and placing takes nothing.</summary>
        Creative,

        /// <summary>Blocks have counts (reserved: the model keeps <see cref="BlockStack.Count" /> for it).</summary>
        Survival
    }

    /// <summary>One inventory slot: a base voxel id (0 = empty) and its count.</summary>
    [Serializable]
    public struct BlockStack : IEquatable<BlockStack>{
        /// <summary>The count of a creative stack: no limit, nothing shown.</summary>
        public const int Unlimited = -1;

        /// <summary>Base voxel id; 0 (air) is an empty slot.</summary>
        public ushort Id;

        /// <summary>Number of blocks, or <see cref="Unlimited" />.</summary>
        public int Count;

        public BlockStack(ushort id, int count = Unlimited){
            Id = id;
            Count = id == 0 ? 0 : count;
        }

        public static BlockStack Empty => default;
        public bool IsEmpty => Id == 0;

        public bool Equals(BlockStack other){
            return Id == other.Id && Count == other.Count;
        }

        public override bool Equals(object obj){
            return obj is BlockStack other && Equals(other);
        }

        public override int GetHashCode(){
            return (Id * 397) ^ Count;
        }

        public override string ToString(){
            return IsEmpty ? "empty" : Count == Unlimited ? $"{Id}" : $"{Id}x{Count}";
        }
    }

    /// <summary>
    ///     The player's blocks: a hotbar of <see cref="HotbarSize" /> slots and a backpack of
    ///     <see cref="BackpackSize" /> slots. In <see cref="InventoryMode.Creative" /> every stack is
    ///     <see cref="BlockStack.Unlimited" /> and the same block may sit in many slots. Saved by block name, so a
    ///     save survives changes to the voxel ids.
    /// </summary>
    public sealed class BlockInventory{
        /// <summary>The two slot areas.</summary>
        public enum Area : byte{
            Hotbar,
            Backpack
        }

        public const int HotbarSize = 9;
        public const int BackpackColumns = 12;
        public const int BackpackRows = 6;
        public const int BackpackSize = BackpackColumns * BackpackRows;

        private readonly BlockStack[] _backpack = new BlockStack[BackpackSize];
        private readonly BlockStack[] _hotbar = new BlockStack[HotbarSize];

        /// <summary>The counting mode; always <see cref="InventoryMode.Creative" /> for now.</summary>
        public InventoryMode Mode{ get; set; } = InventoryMode.Creative;

        /// <summary>Raised after any slot changed.</summary>
        public event Action Changed;

        /// <summary>Number of slots in <paramref name="area" />.</summary>
        public static int SizeOf(Area area){
            return area == Area.Hotbar ? HotbarSize : BackpackSize;
        }

        public BlockStack Get(Area area, int index){
            return Slots(area)[index];
        }

        /// <summary>Puts <paramref name="id" /> on the slot (0 clears it) with an unlimited count.</summary>
        public void Set(Area area, int index, ushort id){
            Set(area, index, new BlockStack(id));
        }

        public void Set(Area area, int index, BlockStack stack){
            BlockStack[] slots = Slots(area);
            if (slots[index].Equals(stack)) return;
            slots[index] = stack;
            Changed?.Invoke();
        }

        public void Clear(Area area, int index){
            Set(area, index, BlockStack.Empty);
        }

        /// <summary>Swaps two slots (drag and drop); a drop onto the same slot does nothing.</summary>
        public void Move(Area fromArea, int from, Area toArea, int to){
            if (fromArea == toArea && from == to) return;
            BlockStack[] a = Slots(fromArea), b = Slots(toArea);
            if (a[from].Equals(b[to])) return;
            (a[from], b[to]) = (b[to], a[from]);
            Changed?.Invoke();
        }

        /// <summary>
        ///     Shift-click: moves the slot into the first empty slot of the other area. When the other area is full
        ///     nothing changes. Returns the target slot, or -1.
        /// </summary>
        public int QuickMove(Area area, int index){
            BlockStack stack = Get(area, index);
            if (stack.IsEmpty) return -1;
            Area other = area == Area.Hotbar ? Area.Backpack : Area.Hotbar;
            int target = FirstEmpty(other);
            if (target < 0) return -1;
            Slots(other)[target] = stack;
            Slots(area)[index] = BlockStack.Empty;
            Changed?.Invoke();
            return target;
        }

        /// <summary>Index of the first empty slot in <paramref name="area" />, or -1 when it is full.</summary>
        public int FirstEmpty(Area area){
            BlockStack[] slots = Slots(area);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].IsEmpty)
                    return i;
            return -1;
        }

        /// <summary>Index of the first slot holding <paramref name="id" />, or -1.</summary>
        public int IndexOf(Area area, ushort id){
            if (id == 0) return -1;
            BlockStack[] slots = Slots(area);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Id == id)
                    return i;
            return -1;
        }

        /// <summary>Number of slots in <paramref name="area" /> that hold a block.</summary>
        public int CountFilled(Area area){
            int n = 0;
            foreach (BlockStack s in Slots(area))
                if (!s.IsEmpty)
                    n++;
            return n;
        }

        /// <summary>Number of slots in <paramref name="area" /> holding <paramref name="id" />.</summary>
        public int CountOf(Area area, ushort id){
            if (id == 0) return 0;
            int n = 0;
            foreach (BlockStack s in Slots(area))
                if (s.Id == id)
                    n++;
            return n;
        }

        /// <summary>
        ///     "Sort by kind": orders the backpack by <paramref name="kindOf" /> (the voxel category), then by id, and
        ///     moves the empty slots to the end. Equal blocks keep their order.
        /// </summary>
        public void SortBackpack(Func<ushort, int> kindOf){
            List<(int kind, int order, BlockStack stack)> keyed = new(BackpackSize);
            for (int i = 0; i < BackpackSize; i++)
                if (!_backpack[i].IsEmpty)
                    keyed.Add((kindOf(_backpack[i].Id), i, _backpack[i]));
            keyed.Sort((x, y) => {
                int c = x.kind.CompareTo(y.kind);
                if (c != 0) return c;
                c = x.stack.Id.CompareTo(y.stack.Id);
                return c != 0 ? c : x.order.CompareTo(y.order);
            });
            bool changed = false;
            for (int i = 0; i < BackpackSize; i++){
                BlockStack next = i < keyed.Count ? keyed[i].stack : BlockStack.Empty;
                if (_backpack[i].Equals(next)) continue;
                _backpack[i] = next;
                changed = true;
            }

            if (changed) Changed?.Invoke();
        }

        /// <summary>
        ///     "Fill hotbar": copies the first nine backpack blocks (in slot order) onto the hotbar; hotbar slots past
        ///     the last copied block stay as they are.
        /// </summary>
        public void FillHotbar(){
            int h = 0;
            bool changed = false;
            for (int i = 0; i < BackpackSize && h < HotbarSize; i++){
                if (_backpack[i].IsEmpty) continue;
                if (!_hotbar[h].Equals(_backpack[i])){
                    _hotbar[h] = _backpack[i];
                    changed = true;
                }

                h++;
            }

            if (changed) Changed?.Invoke();
        }

        /// <summary>Empties every slot.</summary>
        public void ClearAll(){
            Array.Clear(_hotbar, 0, _hotbar.Length);
            Array.Clear(_backpack, 0, _backpack.Length);
            Changed?.Invoke();
        }

        /// <summary>The save data: every slot as a block name ("" for empty) and a count.</summary>
        public SaveData ToSaveData(Func<ushort, string> nameOf){
            SaveData data = new(){ mode = Mode };
            data.hotbar = ToNames(_hotbar, nameOf, out data.hotbarCounts);
            data.backpack = ToNames(_backpack, nameOf, out data.backpackCounts);
            return data;
        }

        /// <summary>
        ///     Loads <paramref name="data" />; a name <paramref name="idOf" /> cannot resolve (returns 0) leaves its
        ///     slot empty, a missing or zero count is unlimited. Returns the number of names that were dropped.
        /// </summary>
        public int Load(SaveData data, Func<string, ushort> idOf){
            if (data == null) return 0;
            Mode = data.mode;
            int dropped = FromNames(_hotbar, data.hotbar, data.hotbarCounts, idOf)
                          + FromNames(_backpack, data.backpack, data.backpackCounts, idOf);
            Changed?.Invoke();
            return dropped;
        }

        /// <summary>JSON of <see cref="ToSaveData" />.</summary>
        public string ToJson(Func<ushort, string> nameOf){
            return JsonUtility.ToJson(ToSaveData(nameOf));
        }

        /// <summary>
        ///     Loads JSON written by <see cref="ToJson" />; false (and nothing changed) for empty or broken JSON, and when
        ///     the save names blocks but none of them exists any more (a renamed package), so the caller falls back to
        ///     its defaults. Dropped names are logged.
        /// </summary>
        public bool TryLoadJson(string json, Func<string, ushort> idOf){
            if (string.IsNullOrEmpty(json)) return false;
            SaveData data;
            try{
                data = JsonUtility.FromJson<SaveData>(json);
            }
            catch (ArgumentException){
                return false;
            }

            if (data == null || (data.hotbar == null && data.backpack == null)) return false;
            int named = CountNamed(data.hotbar) + CountNamed(data.backpack);
            int dropped = CountDropped(data.hotbar, idOf) + CountDropped(data.backpack, idOf);
            if (dropped > 0)
                Debug.LogWarning($"Inventory save: {dropped} of {named} block names no longer exist" +
                                 (dropped == named ? "; the save is ignored." : "; their slots stay empty."));
            if (named > 0 && dropped == named) return false;
            Load(data, idOf);
            return true;
        }

        private static int CountNamed(string[] names){
            int count = 0;
            if (names != null)
                foreach (string name in names)
                    if (!string.IsNullOrEmpty(name)) count++;
            return count;
        }

        private static int CountDropped(string[] names, Func<string, ushort> idOf){
            int count = 0;
            if (names != null)
                foreach (string name in names)
                    if (!string.IsNullOrEmpty(name) && idOf(name) == 0) count++;
            return count;
        }

        private BlockStack[] Slots(Area area){
            return area == Area.Hotbar ? _hotbar : _backpack;
        }

        private static string[] ToNames(BlockStack[] slots, Func<ushort, string> nameOf, out int[] counts){
            string[] names = new string[slots.Length];
            counts = new int[slots.Length];
            for (int i = 0; i < slots.Length; i++){
                names[i] = slots[i].IsEmpty ? "" : nameOf(slots[i].Id) ?? "";
                counts[i] = slots[i].Count;
            }

            return names;
        }

        private static int FromNames(BlockStack[] slots, string[] names, int[] counts, Func<string, ushort> idOf){
            int dropped = 0;
            for (int i = 0; i < slots.Length; i++){
                string name = names != null && i < names.Length ? names[i] : null;
                if (string.IsNullOrEmpty(name)){
                    slots[i] = BlockStack.Empty;
                    continue;
                }

                ushort id = idOf(name);
                if (id == 0) dropped++;
                int count = counts != null && i < counts.Length ? counts[i] : BlockStack.Unlimited;
                slots[i] = new BlockStack(id, count == 0 ? BlockStack.Unlimited : count);
            }

            return dropped;
        }

        /// <summary>What is saved: block names per slot ("" = empty) and their counts.</summary>
        [Serializable]
        public sealed class SaveData{
            public InventoryMode mode;
            public string[] hotbar;
            public int[] hotbarCounts;
            public string[] backpack;
            public int[] backpackCounts;
        }
    }
}
