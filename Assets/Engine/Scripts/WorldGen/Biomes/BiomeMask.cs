using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>
    ///     A set of biome ids as a 256-bit mask (AD-20, DEC-144): biome ids are bytes, so every biome set (rule
    ///     conditions, never-snow, implausible pairs, the water biomes of the dry-column rule) is four words, and a test
    ///     is one shift and one mask whatever the number of biomes.
    /// </summary>
    public struct BiomeMask{
        public ulong W0, W1, W2, W3;

        public static BiomeMask Of(params int[] ids){
            BiomeMask m = default;
            foreach (int id in ids) m.Add(id);
            return m;
        }

        public void Add(int id){
            ulong bit = 1UL << (id & 63);
            switch (id >> 6){
                case 0: W0 |= bit; break;
                case 1: W1 |= bit; break;
                case 2: W2 |= bit; break;
                default: W3 |= bit; break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Contains(int id){
            int word = (id >> 6) & 3;
            ulong w = word == 0 ? W0 : word == 1 ? W1 : word == 2 ? W2 : W3;
            return ((w >> (id & 63)) & 1UL) != 0;
        }

        public readonly bool IsEmpty => (W0 | W1 | W2 | W3) == 0;

        public readonly int Count => math.countbits(W0) + math.countbits(W1) + math.countbits(W2) + math.countbits(W3);

        public override string ToString(){
            System.Text.StringBuilder s = new("{");
            for (int i = 0; i < 256; i++)
                if (Contains(i)){
                    if (s.Length > 1) s.Append(',');
                    s.Append(i);
                }

            return s.Append('}').ToString();
        }
    }
}
