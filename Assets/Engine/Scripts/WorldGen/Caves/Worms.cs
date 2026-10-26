using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     State of one worm on its path: the centre and radius of the sphere it carves at <see cref="Step" />, and
    ///     the turn rates that carry it to the next step. <see cref="Worms.Begin" /> starts it, <see cref="Worms.Next" />
    ///     walks it, <see cref="Worms.Branch" /> forks it.
    /// </summary>
    public struct Worm{
        public float3 Position;
        public float Radius;
        public float Yaw;
        public float Pitch;
        public float YawRate;
        public float PitchRate;
        public int Step;
        public int Length;

        /// <summary>Hash every roll of this path follows from.</summary>
        public uint Hash;

        /// <summary>Start of the main worm (x, z); the path and its branch stay within <c>WormReach</c> of it.</summary>
        public float2 Origin;

        /// <summary>Step at which the branch leaves, −1 for none (and on a branch).</summary>
        public int BranchStep;

        /// <summary>
        ///     A worm that starts in the entrance mask goes straight down while it is above this y, so its shaft stays
        ///     in the mask until the column limits outside it let the tunnel through; int.MaxValue for others.
        /// </summary>
        public int ShaftAbove;

        /// <summary>A worm that starts in the entrance mask aims down while it is above this y; int.MaxValue for others.</summary>
        public int DescendAbove;
    }

    /// <summary>
    ///     Worm tunnels after the cave carvers of Minecraft (MapGenCaves of the Java Edition): worms start in square
    ///     cells, walk in steps of one block with yaw and pitch turning in a damped random walk, carve a sphere at
    ///     every step whose radius changes smoothly, and may branch once. Every roll comes from the worm's own hash,
    ///     so a path is the same in every block that walks it; a block walks every worm that can reach it in full and
    ///     carves only the steps that touch it.
    /// </summary>
    public static class Worms{
        private const int RollColumnX = 0;
        private const int RollColumnZ = 1;
        private const int RollY = 2;
        private const int RollYaw = 3;
        private const int RollPitch = 4;
        private const int RollLength = 5;
        private const int RollBranch = 6;
        private const int RollBranchStep = 7;
        private const int RollBranchSide = 8;

        /// <summary>Most worms a start cell holds: one roll per possible worm, each present with WormsPerCell / 2.</summary>
        public const int MaxPerCell = 2;

        /// <summary>Worm <paramref name="k" /> (0 or 1) of start cell <paramref name="cell" /> exists.</summary>
        public static bool Present(int2 cell, int k, in WorldGenConfig cfg){
            return Hash.Hash01(cell.x, k, cell.y, cfg.Seed, HashSalts.WormCount) < .5f * cfg.Caves.WormsPerCell;
        }

        private static uint WormHash(int2 cell, int k, in WorldGenConfig cfg){
            return Hash.Hash32(cell.x, k, cell.y, cfg.Seed, HashSalts.Worm);
        }

        private static float Roll(uint hash, int roll){
            return Hash.Hash01(roll, 0, hash, HashSalts.WormStart);
        }

        private static uint RollBits(uint hash, int roll){
            return Hash.Hash32(roll, 0, hash, HashSalts.WormStart);
        }

        /// <summary>Start column of worm <paramref name="k" /> of <paramref name="cell" />, whether or not it exists.</summary>
        public static int2 StartColumn(int2 cell, int k, in WorldGenConfig cfg){
            int size = cfg.Caves.WormCellSize;
            uint hash = WormHash(cell, k, cfg);
            return cell * size + new int2((int)(RollBits(hash, RollColumnX) % (uint)size),
                (int)(RollBits(hash, RollColumnZ) % (uint)size));
        }

        /// <summary>Start cell of column (x, z).</summary>
        public static int2 Cell(int2 column, in WorldGenConfig cfg){
            int size = cfg.Caves.WormCellSize;
            int2 q = column / size;
            return q - math.select(int2.zero, new int2(1), (column % size != 0) & (column < 0));
        }

        /// <summary>
        ///     Starts worm <paramref name="k" /> of <paramref name="cell" />; false when the cell has no such worm or its
        ///     start column has no room for one (surface − Cover below the lowest start height outside an entrance;
        ///     outside the entrance mask the surface is the unblended height).
        ///     A worm in the entrance mask starts up to <c>EntranceStartDepth</c> below the surface, goes straight down
        ///     until its sphere lies below <c>Cover</c> (outside the mask nothing above it is carved, so a sloped start
        ///     left the opening a dead end), then aims down to the top of the cavern band, where the halls and the
        ///     other worms run; its length grows by the steps of that descent. The start leaves out the water test of the
        ///     entrance: starts lie up to <c>WormPadding</c> away, beyond the block's probes, and the column limits keep
        ///     the worm <c>Cover</c> deep wherever water is near.
        /// </summary>
        public static bool Begin(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, out Worm worm){
            ref readonly CaveConfig c = ref cfg.Caves;
            worm = default;
            if (!Present(cell, k, cfg)) return false;
            uint hash = WormHash(cell, k, cfg);
            int2 column = StartColumn(cell, k, cfg);
            // Only a start in the entrance mask needs the exact surface, for its opening. The depth range of the others
            // takes the unblended height, which in Biome-first saves a blend of every kernel tap per start; the column
            // limits still keep those worms Cover below the exact surface.
            bool inMask = CaveMasks.Entrance(column.x, column.y, cfg) > c.EntranceThreshold;
            int surface = inMask
                ? CaveColumns.SurfaceY(column.x, column.y, cfg, cache)
                : CaveColumns.UnblendedSurfaceY(column.x, column.y, cfg);
            bool entrance = inMask && !CaveColumns.Submerged(surface, cfg);
            int y;
            int extraSteps = 0;
            if (entrance){
                y = surface - (int)(Roll(hash, RollY) * (c.EntranceStartDepth + 1));
                worm.ShaftAbove = surface - c.Cover - (int)math.ceil(c.WormMaxRadius);
                worm.DescendAbove = math.min(worm.ShaftAbove, c.CavernBandMax);
                worm.Pitch = -c.WormMaxPitch;
                extraSteps = math.max(y - worm.ShaftAbove, 0) +
                             (int)math.ceil((worm.ShaftAbove - worm.DescendAbove) / math.max(math.sin(c.WormMaxPitch), .1f));
            }
            else{
                int top = surface - c.Cover;
                if (top < c.WormStartMinY) return false;
                y = c.WormStartMinY + (int)(Roll(hash, RollY) * (top - c.WormStartMinY + 1));
                worm.ShaftAbove = int.MaxValue;
                worm.DescendAbove = int.MaxValue;
                worm.Pitch = (Roll(hash, RollPitch) * 2f - 1f) * .5f * c.WormMaxPitch;
            }

            worm.Position = new float3(column.x + .5f, math.max(y, c.WormMinY) + .5f, column.y + .5f);
            worm.Origin = worm.Position.xz;
            worm.Yaw = Roll(hash, RollYaw) * 2f * math.PI;
            worm.Hash = hash;
            worm.Length = c.WormLength.x + (int)(RollBits(hash, RollLength) % (uint)(c.WormLength.y - c.WormLength.x + 1)) +
                          extraSteps;
            worm.BranchStep = worm.Length >= 2 && Roll(hash, RollBranch) < c.WormBranchChance
                ? 1 + (int)(RollBits(hash, RollBranchStep) % (uint)(worm.Length - 1))
                : -1;
            worm.Radius = RadiusAt(hash, 0, c);
            return true;
        }

        /// <summary>
        ///     The branch of <paramref name="parent" />, which stands at its <see cref="Worm.BranchStep" />: it turns a
        ///     quarter to one side, takes half the remaining length and walks from its own hash. False when that length
        ///     is 0.
        /// </summary>
        public static bool Branch(in Worm parent, in CaveConfig c, out Worm branch){
            branch = parent;
            branch.Hash = Hash.Hash32(1, 0, parent.Hash, HashSalts.Worm);
            branch.Step = 0;
            branch.Length = (parent.Length - parent.Step) / 2;
            branch.BranchStep = -1;
            branch.Yaw += (Roll(parent.Hash, RollBranchSide) < .5f ? .5f : -.5f) * math.PI;
            branch.YawRate = 0f;
            branch.PitchRate = 0f;
            branch.Radius = RadiusAt(branch.Hash, 0, c);
            return branch.Length > 0;
        }

        /// <summary>
        ///     Moves <paramref name="worm" /> one block on: the yaw and pitch rates keep <c>WormTurnDamping</c> of
        ///     their value and gain a jitter, the pitch relaxes towards level (towards down while an entrance worm is
        ///     still high) and stays within ±<c>WormMaxPitch</c>, and the worm bounces off <c>WormMinY</c>. An entrance
        ///     worm above <see cref="Worm.ShaftAbove" /> steps straight down instead. False when the path ends: its
        ///     length is used up or the next step would leave <c>WormReach</c> of the start.
        /// </summary>
        public static bool Next(ref Worm worm, in CaveConfig c){
            int step = worm.Step + 1;
            if (step >= worm.Length) return false;
            if (worm.Position.y > worm.ShaftAbove){
                worm.Position.y -= 1f;
                worm.Step = step;
                worm.Radius = RadiusAt(worm.Hash, step, c);
                return true;
            }

            float damping = c.WormTurnDamping;
            worm.YawRate = worm.YawRate * damping + c.WormYawJitter * Hash.HashSigned(step, 0, worm.Hash, HashSalts.WormTurn);
            worm.PitchRate = worm.PitchRate * damping + c.WormPitchJitter * Hash.HashSigned(step, 1, worm.Hash, HashSalts.WormTurn);
            worm.Yaw += worm.YawRate;
            float target = worm.Position.y > worm.DescendAbove ? -c.WormMaxPitch : 0f;
            worm.Pitch = math.clamp(target + (worm.Pitch - target) * damping + worm.PitchRate, -c.WormMaxPitch, c.WormMaxPitch);

            float level = math.cos(worm.Pitch);
            float3 next = worm.Position + new float3(level * math.cos(worm.Yaw), math.sin(worm.Pitch), level * math.sin(worm.Yaw));
            float2 offset = next.xz - worm.Origin;
            if (math.lengthsq(offset) > (float)c.WormReach * c.WormReach) return false;
            float floor = c.WormMinY + .5f;
            if (next.y < floor){
                next.y = floor;
                worm.Pitch = math.abs(worm.Pitch);
                worm.PitchRate = math.abs(worm.PitchRate);
            }

            worm.Position = next;
            worm.Step = step;
            worm.Radius = RadiusAt(worm.Hash, step, c);
            return true;
        }

        /// <summary>
        ///     Radius at <paramref name="step" />: a smoothstep between radii rolled every <c>WormRadiusPeriod</c> steps
        ///     in <c>WormMinRadius</c>…<c>WormMaxRadius</c>.
        /// </summary>
        public static float RadiusAt(uint hash, int step, in CaveConfig c){
            int period = c.WormRadiusPeriod;
            int knot = step / period;
            float f = (step - knot * period) / (float)period;
            float r0 = math.lerp(c.WormMinRadius, c.WormMaxRadius, Hash.Hash01(knot, 0, hash, HashSalts.WormRadius));
            float r1 = math.lerp(c.WormMinRadius, c.WormMaxRadius, Hash.Hash01(knot + 1, 0, hash, HashSalts.WormRadius));
            return math.lerp(r0, r1, math.smoothstep(0f, 1f, f));
        }

        /// <summary>Voxel <paramref name="voxel" /> lies in the sphere a worm carves at its current step.</summary>
        public static bool Contains(in Worm worm, int3 voxel){
            return math.lengthsq((float3)voxel + .5f - worm.Position) <= worm.Radius * worm.Radius;
        }

        /// <summary>
        ///     Some worm sphere contains voxel (x, y, z), whatever the column limits: walks every worm whose start lies
        ///     within <c>WormPadding</c> of the column, with its branch. The pure reference of the chunk pass, slow.
        /// </summary>
        public static bool Covers(int x, int y, int z, in WorldGenConfig cfg, in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            int3 voxel = new(x, y, z);
            int padding = c.WormPadding;
            int2 first = Cell(new int2(x, z) - padding, cfg), last = Cell(new int2(x, z) + padding, cfg);
            for (int cx = first.x; cx <= last.x; cx++)
            for (int cz = first.y; cz <= last.y; cz++)
            for (int k = 0; k < MaxPerCell; k++){
                int2 cell = new(cx, cz);
                int2 start = StartColumn(cell, k, cfg);
                if (math.any(math.abs(start - new int2(x, z)) > padding)) continue;
                if (!Begin(cell, k, cfg, cache, out Worm worm)) continue;
                Worm branch = default;
                bool branched = false;
                do{
                    if (Contains(worm, voxel)) return true;
                    if (worm.Step == worm.BranchStep) branched = Branch(worm, c, out branch);
                } while (Next(ref worm, c));

                if (!branched) continue;
                do{
                    if (Contains(branch, voxel)) return true;
                } while (Next(ref branch, c));
            }

            return false;
        }
    }
}
