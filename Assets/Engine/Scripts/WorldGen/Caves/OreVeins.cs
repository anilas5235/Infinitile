using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     One ore vein: a random walk of <see cref="Size" /> voxels from <see cref="Seed" />, every roll from
    ///     <see cref="Hash" />. <see cref="OreVeins.Begin" /> starts it, <see cref="OreVeins.Voxel" /> walks it.
    /// </summary>
    public struct OreVein{
        public int3 Seed;
        public int Size;
        public uint Hash;
    }

    /// <summary>
    ///     Ore veins in the rock: the world is cut into cubic cells of <see cref="CellSize" />, and every cell holds
    ///     a hashed number of vein seeds per ore rule (mean <c>VeinsPerCell</c>). A vein is a random walk of a hashed
    ///     length that steps one block along a hashed axis each time, so it stays a small blob around its seed (like
    ///     the ore blobs of Minecraft's generator, after the depth-picked ores of Infinitile v0.3.0,
    ///     ChunkGenerationCavesOres.PlaceOres). Rules apply in list order and each replaces only its host voxel, within
    ///     its y range. No per-voxel noise: a vein is a handful of hashes.
    /// </summary>
    public static class OreVeins{
        /// <summary>Edge in blocks of the cubic cells that hold the vein seeds.</summary>
        public const int CellSize = 16;

        private const int RollSeedX = 0;
        private const int RollSeedY = 1;
        private const int RollSeedZ = 2;
        private const int RollSize = 3;

        /// <summary>Number of veins of <paramref name="rule" /> in cell <paramref name="cell" />: the whole part of VeinsPerCell, plus one with its fraction.</summary>
        public static int Count(int3 cell, int rule, in OreRuleDef def, in WorldGenConfig cfg){
            float whole = math.floor(def.VeinsPerCell);
            float roll = Hash.Hash01(rule, 0, CellHash(cell, cfg), HashSalts.OreCount);
            return (int)whole + (roll < def.VeinsPerCell - whole ? 1 : 0);
        }

        private static uint CellHash(int3 cell, in WorldGenConfig cfg){
            return Hash.Hash32(cell.x, cell.y, cell.z, cfg.Seed, HashSalts.OreCell);
        }

        /// <summary>Vein <paramref name="index" /> of <paramref name="rule" /> in cell <paramref name="cell" />.</summary>
        public static OreVein Begin(int3 cell, int rule, int index, in OreRuleDef def, in WorldGenConfig cfg){
            uint hash = Hash.Hash32(rule, index, CellHash(cell, cfg), HashSalts.OreVein);
            int3 offset = new((int)(Roll(hash, RollSeedX) % CellSize), (int)(Roll(hash, RollSeedY) % CellSize),
                (int)(Roll(hash, RollSeedZ) % CellSize));
            int span = def.VeinSize.y - def.VeinSize.x + 1;
            return new OreVein{
                Seed = cell * CellSize + offset,
                Size = def.VeinSize.x + (int)(Roll(hash, RollSize) % (uint)span),
                Hash = hash
            };
        }

        private static uint Roll(uint hash, int roll){
            return Hash.Hash32(roll, 0, hash, HashSalts.OreVein);
        }

        /// <summary>
        ///     Voxel <paramref name="step" /> of a vein given voxel <paramref name="step" /> − 1 at <paramref name="previous" />:
        ///     one block along a hashed axis and sign. Step 0 is the seed.
        /// </summary>
        public static int3 Next(in OreVein vein, int step, int3 previous){
            uint roll = Hash.Hash32(step, 0, vein.Hash, HashSalts.OreStep) % 6u;
            int sign = (roll & 1u) == 0 ? 1 : -1;
            int axis = (int)(roll >> 1);
            return previous + math.select(int3.zero, new int3(sign), new bool3(axis == 0, axis == 1, axis == 2));
        }

        /// <summary>Cell that holds voxel coordinate <paramref name="v" /> (floor division).</summary>
        public static int3 Cell(int3 v){
            int3 q = v / CellSize;
            return q - math.select(int3.zero, new int3(1), (v % CellSize != 0) & (v < 0));
        }

        /// <summary>Largest distance (per axis) of a vein voxel from its seed over all rules: the longest walk − 1.</summary>
        public static int Padding(in CaveConfig c){
            int padding = 0;
            for (int r = 0; r < c.Ores.Length; r++) padding = math.max(padding, c.Ores[r].VeinSize.y - 1);
            return padding;
        }

        /// <summary>Some vein of rule <paramref name="rule" /> passes through voxel <paramref name="p" />. Slow; the pure reference.</summary>
        public static bool Covers(int3 p, int rule, in WorldGenConfig cfg){
            OreRuleDef def = cfg.Caves.Ores[rule];
            int reach = def.VeinSize.y - 1;
            int3 first = Cell(p - reach), last = Cell(p + reach);
            for (int cx = first.x; cx <= last.x; cx++)
            for (int cy = first.y; cy <= last.y; cy++)
            for (int cz = first.z; cz <= last.z; cz++){
                int3 cell = new(cx, cy, cz);
                int count = Count(cell, rule, def, cfg);
                for (int k = 0; k < count; k++){
                    OreVein vein = Begin(cell, rule, k, def, cfg);
                    int3 v = vein.Seed;
                    for (int s = 0; s < vein.Size; s++){
                        if (s > 0) v = Next(vein, s, v);
                        if (math.all(v == p)) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        ///     The voxel the ore rules leave at <paramref name="p" /> over the terrain voxel <paramref name="fill" />:
        ///     each rule in order replaces its host where one of its veins passes, within its y range. Slow; the pure
        ///     reference of the chunk pass.
        /// </summary>
        public static ushort Apply(int3 p, ushort fill, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            if (!c.Enabled) return fill;
            ushort v = fill;
            for (int r = 0; r < c.Ores.Length; r++){
                OreRuleDef def = c.Ores[r];
                if (v != def.Host || p.y < def.MinY || p.y > def.MaxY || p.y <= CaveColumns.Floor) continue;
                if (Covers(p, r, cfg)) v = def.Ore;
            }

            return v;
        }
    }
}
