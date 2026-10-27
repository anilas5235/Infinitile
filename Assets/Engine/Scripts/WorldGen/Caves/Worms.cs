using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     State of one worm on its path: the centre and radius of the sphere it carves at <see cref="Step" />, the
    ///     turn rates that carry it to the next step and, for a connector, the target it steers towards.
    ///     <see cref="Worms.Begin(int2,int,in WorldGenConfig,in BlendCache,out Worm)" /> starts it,
    ///     <see cref="Worms.Next" /> walks it, <see cref="Worms.Branch" /> forks it.
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

        /// <summary>Radius knot the current step lies in, with the radii rolled at that knot and the next.</summary>
        public int RadiusKnot;

        public float RadiusFrom;
        public float RadiusTo;

        /// <summary>
        ///     Start of the random worm this path belongs to (x, z): the worm, its branch and its connectors stay within
        ///     <c>WormReach</c> of it.
        /// </summary>
        public float2 Origin;

        /// <summary>Step at which the branch leaves, −1 for none (and on a branch or a connector).</summary>
        public int BranchStep;

        /// <summary>
        ///     A worm that starts in the entrance mask goes straight down while it is above this y, so its shaft stays
        ///     in the mask until the column limits outside it let the tunnel through; int.MaxValue for others.
        /// </summary>
        public int ShaftAbove;

        /// <summary>Steps of that shaft, straight down from the start; 0 for the others. A connector leaves its worm after the shaft.</summary>
        public int Shaft;

        /// <summary>The path is a connector: it steers towards <see cref="Target" /> and ends where its sphere reaches it.</summary>
        public bool HasTarget;

        public float3 Target;
    }

    /// <summary>
    ///     Start of a random worm: what <see cref="Worms.Begin(int2,int,in WorldGenConfig,in BlendCache,out Worm)" />
    ///     needs of it, and the point the connectors of other worms measure their way to it from. The same for every
    ///     block, like the worm itself.
    /// </summary>
    public struct WormAnchor{
        /// <summary>The worm exists and its start column has room for it.</summary>
        public bool Present;

        public int2 Column;

        /// <summary>Start y before the clamp to <c>WormMinY</c>.</summary>
        public int Y;

        public bool Entrance;
        public int ShaftAbove;

        /// <summary>Centre of the first sphere of a free worm, the bottom of the shaft of an entrance worm: where its random walk begins.</summary>
        public float3 Aim;
    }

    /// <summary>
    ///     The anchors of the random worms of a range of cells, computed once per block so that the connectors, which
    ///     choose among the starts of their 3 × 3 cells, need no second surface lookup. The default (no table) makes
    ///     <see cref="Worms.Begin(int2,int,in WorldGenConfig,in BlendCache,in WormAnchors,out Worm)" /> compute every
    ///     anchor it needs on demand.
    /// </summary>
    public struct WormAnchors{
        public NativeArray<WormAnchor> Table;
        public int2 First;
        public int2 Count;

        /// <summary>Anchors of the cells <paramref name="first" /> … <paramref name="last" />.</summary>
        public static WormAnchors Create(int2 first, int2 last, in WorldGenConfig cfg, in BlendCache cache, Allocator allocator){
            WormAnchors anchors = new(){ First = first, Count = last - first + 1 };
            anchors.Table = new NativeArray<WormAnchor>(anchors.Count.x * anchors.Count.y * Worms.PrimariesPerCell, allocator,
                NativeArrayOptions.UninitializedMemory);
            for (int cx = first.x; cx <= last.x; cx++)
            for (int cz = first.y; cz <= last.y; cz++)
            for (int k = 0; k < Worms.PrimariesPerCell; k++){
                int2 cell = new(cx, cz);
                anchors.Table[anchors.Index(cell, k)] = Worms.Anchor(cell, k, cfg, cache);
            }

            return anchors;
        }

        public bool Has(int2 cell){
            return Table.IsCreated && math.all(cell >= First) && math.all(cell < First + Count);
        }

        public WormAnchor Get(int2 cell, int k){
            return Table[Index(cell, k)];
        }

        private int Index(int2 cell, int k){
            return ((cell.x - First.x) * Count.y + cell.y - First.y) * Worms.PrimariesPerCell + k;
        }

        public void Dispose(){
            if (Table.IsCreated) Table.Dispose();
        }
    }

    /// <summary>
    ///     Worm tunnels after the cave carvers of Minecraft (MapGenCaves of the Java Edition), in three layers over
    ///     the halls. Random worms start in square cells, walk in steps of one block with yaw and pitch turning in a
    ///     damped random walk, carve a sphere at every step whose radius changes smoothly, and may branch once; they
    ///     steer at nothing. Every random worm then sends two connector worms from hashed points of its path: a link
    ///     to a hashed point of the path of the nearest other random worm of its 3 × 3 cells, and a hall link down to
    ///     the nearest region of the cavern mask, where the halls are; a connector steers at its target and ends where
    ///     it reaches it. Every roll comes from the worm's own hash, so a path is the same in every block that walks
    ///     it; a block walks every worm that can reach it in full and carves only the steps that touch it.
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

        /// <summary>Rolls of a connector: the step of its worm it leaves at, the step of the target worm it heads for, the offset of its hall target.</summary>
        private const int RollPort = 9;

        private const int RollEnd = 10;
        private const int RollHallX = 11;
        private const int RollHallZ = 12;

        /// <summary>
        ///     Random worms a start cell rolls for: slot k is present with chance WormsPerCell − k (clamped to 0…1), so
        ///     a cell holds the whole part of WormsPerCell and one more with the fraction as its chance.
        /// </summary>
        public const int PrimariesPerCell = 2;

        /// <summary>Connector slots of every random worm: its link to another worm, then its link to a hall.</summary>
        public const int ConnectorsPerWorm = 2;

        /// <summary>Slots a start cell holds: the random worms, then the connectors of each (<see cref="LinkSlot" />, <see cref="HallLinkSlot" />).</summary>
        public const int MaxPerCell = PrimariesPerCell * (1 + ConnectorsPerWorm);

        /// <summary>
        ///     Share of <c>WormReach</c>, measured horizontally from the start of the random worm a connector belongs
        ///     to, within which its target must lie: the rest of the reach absorbs the wander of the steered path,
        ///     which ends at the reach.
        /// </summary>
        public const float TargetReach = .75f;

        /// <summary>
        ///     Steps granted per block of the estimated path to a target (the horizontal distance or, when longer,
        ///     the climb at the largest pitch), with <see cref="TargetSlack" /> on top: the wander around the straight
        ///     line.
        /// </summary>
        public const float TargetStepsPerBlock = 1.5f;

        public const int TargetSlack = 16;

        /// <summary>
        ///     Share of the slope at the largest pitch a target may lie above or below the connector's start: steeper
        ///     targets are left out, since a worm that has to climb more than it walks circles up or down under its target.
        /// </summary>
        public const float TargetSlopeShare = .6f;

        /// <summary>
        ///     Spread of the hashed factor (1…1 + this) on the estimated way to a start, so that the worms around one
        ///     start do not all link to it.
        /// </summary>
        public const float TargetSpread = .5f;

        /// <summary>Points a link tries on the path of its target worm, spread evenly along it from a hashed step; it ends at the nearest of them.</summary>
        public const int EndCandidates = 4;

        /// <summary>Spacing of the world-aligned lattice the hall search samples the cavern mask on, in blocks.</summary>
        public const int HallSearchStep = 32;

        /// <summary>Blocks above the floor of the cavern band a hall link aims at, where the halls open widest.</summary>
        public const int HallAimAbove = 6;

        /// <summary>Random worm the connector in slot <paramref name="k" /> leaves from; −1 for a random worm.</summary>
        public static int Source(int k){
            return k < PrimariesPerCell ? -1 : (k - PrimariesPerCell) / ConnectorsPerWorm;
        }

        /// <summary>Slot <paramref name="k" /> is a hall link (else a link to another worm, or a random worm).</summary>
        public static bool IsHallLink(int k){
            return k >= PrimariesPerCell && (k - PrimariesPerCell) % ConnectorsPerWorm == 1;
        }

        /// <summary>Slot of the link of random worm <paramref name="source" /> to another worm.</summary>
        public static int LinkSlot(int source){
            return PrimariesPerCell + source * ConnectorsPerWorm;
        }

        /// <summary>Slot of the hall link of random worm <paramref name="source" />.</summary>
        public static int HallLinkSlot(int source){
            return LinkSlot(source) + 1;
        }

        /// <summary>
        ///     Slot <paramref name="k" /> of start cell <paramref name="cell" /> may exist: a random worm by its roll,
        ///     a connector when its kind is on (<c>WormLinks</c>, <c>WormHallLinks</c>) and its worm rolls.
        ///     <see cref="Begin(int2,int,in WorldGenConfig,in BlendCache,in WormAnchors,out Worm)" /> still returns
        ///     false when the start has no room or a connector no target.
        /// </summary>
        public static bool Present(int2 cell, int k, in WorldGenConfig cfg){
            if (k < PrimariesPerCell)
                return Hash.Hash01(cell.x, k, cell.y, cfg.Seed, HashSalts.WormCount) < math.saturate(cfg.Caves.WormsPerCell - k);
            if (!(IsHallLink(k) ? cfg.Caves.WormHallLinks : cfg.Caves.WormLinks)) return false;
            return Present(cell, Source(k), cfg);
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

        /// <summary>
        ///     Start column of slot <paramref name="k" /> of <paramref name="cell" />, whether or not it exists; a
        ///     connector counts as starting in the column of its worm, which its reach is measured from.
        /// </summary>
        public static int2 StartColumn(int2 cell, int k, in WorldGenConfig cfg){
            if (k >= PrimariesPerCell) k = Source(k);
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
        ///     Anchor of random worm <paramref name="k" /> of <paramref name="cell" />: not present when the cell has
        ///     no such worm or its start column has no room for one (surface − Cover below the lowest start height
        ///     outside an entrance; outside the entrance mask the surface is the unblended height).
        ///     A worm in the entrance mask starts up to <c>EntranceStartDepth</c> below the surface and goes straight
        ///     down until its sphere lies below <c>Cover</c>, so the opening is a vertical pit that leads down rather
        ///     than a sloped scrape. The start does not test for water: starts lie up to <c>WormPadding</c> away, and
        ///     the column limits keep every worm <c>WaterCover</c> deep wherever water is near.
        /// </summary>
        public static WormAnchor Anchor(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            WormAnchor a = default;
            if (!Present(cell, k, cfg)) return a;
            uint hash = WormHash(cell, k, cfg);
            a.Column = StartColumn(cell, k, cfg);
            // Only a start in the entrance mask needs the exact surface, for its opening. The depth range of the others
            // takes the unblended height, which in Biome-first saves a blend of every kernel tap per start; a start that
            // lands above the exact surface carves nothing there, since nothing above the top block is carved.
            bool inMask = CaveMasks.Entrance(a.Column.x, a.Column.y, cfg) > c.EntranceThreshold;
            int surface = inMask
                ? CaveColumns.SurfaceY(a.Column.x, a.Column.y, cfg, cache)
                : CaveColumns.UnblendedSurfaceY(a.Column.x, a.Column.y, cfg);
            a.Entrance = inMask && !CaveColumns.Submerged(surface, cfg);
            int aimY;
            if (a.Entrance){
                a.Y = surface - (int)(Roll(hash, RollY) * (c.EntranceStartDepth + 1));
                // The shaft steps straight down without the bounce of Next and ends one voxel below ShaftAbove, so
                // it ends at WormMinY at the lowest.
                a.ShaftAbove = math.max(surface - c.Cover - (int)math.ceil(c.WormMaxRadius), c.WormMinY + 1);
                aimY = math.min(a.Y, a.ShaftAbove - 1);
            }
            else{
                int top = surface - c.Cover;
                if (top < c.WormStartMinY) return a;
                a.Y = c.WormStartMinY + (int)(Roll(hash, RollY) * (top - c.WormStartMinY + 1));
                a.ShaftAbove = int.MaxValue;
                aimY = math.max(a.Y, c.WormMinY);
            }

            a.Aim = new float3(a.Column.x + .5f, aimY + .5f, a.Column.y + .5f);
            a.Present = true;
            return a;
        }

        /// <summary>Anchor of random worm <paramref name="k" /> of <paramref name="cell" />, from the table when it holds the cell.</summary>
        private static WormAnchor Lookup(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors){
            return anchors.Has(cell) ? anchors.Get(cell, k) : Anchor(cell, k, cfg, cache);
        }

        /// <summary>Steps of the shaft of an entrance worm, straight down from its start to its aim; 0 for a free worm.</summary>
        private static int ShaftSteps(in WormAnchor a){
            return a.Entrance ? math.max(a.Y - a.ShaftAbove + 1, 0) : 0;
        }

        /// <summary>Starts slot <paramref name="k" /> of <paramref name="cell" />, computing every anchor it needs.</summary>
        public static bool Begin(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, out Worm worm){
            return Begin(cell, k, cfg, cache, default, out worm);
        }

        /// <summary>
        ///     Starts slot <paramref name="k" /> of <paramref name="cell" />; false when the slot holds no worm (see
        ///     <see cref="Anchor" />) or a connector finds no target. A random worm walks from its anchor with a rolled
        ///     length (<see cref="BeginRandom" />). A connector leaves its worm at the <see cref="PortStep" /> of its
        ///     path, which this walks to; a block that walks the worm anyway records that state and starts the
        ///     connector from it with <see cref="Begin(int2,int,in Worm,in WorldGenConfig,in BlendCache,in WormAnchors,out Worm)" />.
        ///     <paramref name="anchors" /> may hold the anchors of the cells, else they are computed.
        /// </summary>
        public static bool Begin(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors, out Worm worm){
            worm = default;
            if (!Present(cell, k, cfg)) return false;
            if (k < PrimariesPerCell){
                WormAnchor a = Lookup(cell, k, cfg, cache, anchors);
                if (!a.Present) return false;
                BeginRandom(cell, k, a, cfg, out worm);
                return true;
            }

            if (!Begin(cell, Source(k), cfg, cache, anchors, out Worm source)) return false;
            Worm port = Port(source, PortStep(cell, k, source, cfg), cfg.Caves);
            return Begin(cell, k, port, cfg, cache, anchors, out worm);
        }

        /// <summary>
        ///     Step of its worm <paramref name="source" /> the connector in slot <paramref name="k" /> leaves at: a
        ///     hashed step after the shaft, anywhere along the rolled length.
        /// </summary>
        public static int PortStep(int2 cell, int k, in Worm source, in WorldGenConfig cfg){
            int span = math.max(source.Length - source.Shaft, 1);
            return source.Shaft + (int)(RollBits(WormHash(cell, k, cfg), RollPort) % (uint)span);
        }

        /// <summary>The state of <paramref name="worm" /> at <paramref name="step" />, or at its last step when the path ends before it.</summary>
        private static Worm Port(Worm worm, int step, in CaveConfig c){
            while (worm.Step < step && Next(ref worm, c)){ }

            return worm;
        }

        /// <summary>
        ///     Starts the connector in slot <paramref name="k" /> of <paramref name="cell" /> from <paramref name="port" />,
        ///     the state of its worm at the step it leaves; false when it finds no target. A link heads for a hashed
        ///     point of the path of the nearest other random worm (<see cref="LinkTarget" />), a hall link for the
        ///     nearest region of the cavern mask (<see cref="HallTarget" />); each gets the length to arrive, keeps the
        ///     reach of its worm and ends where its sphere reaches the target.
        /// </summary>
        public static bool Begin(int2 cell, int k, in Worm port, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors,
            out Worm worm){
            ref readonly CaveConfig c = ref cfg.Caves;
            worm = default;
            uint hash = WormHash(cell, k, cfg);
            float3 target;
            bool found = IsHallLink(k)
                ? HallTarget(port, hash, cfg, out target)
                : LinkTarget(cell, Source(k), port, hash, cfg, cache, anchors, out target);
            if (!found) return false;

            float3 d = target - port.Position;
            worm.Position = port.Position;
            worm.Origin = port.Origin;
            worm.Yaw = math.atan2(d.z, d.x);
            worm.Pitch = math.clamp(math.atan2(d.y, math.length(d.xz)), -c.WormMaxPitch, c.WormMaxPitch);
            worm.Hash = hash;
            worm.Length = StepsTo(port.Position, target, c);
            worm.BranchStep = -1;
            worm.ShaftAbove = int.MaxValue;
            worm.Shaft = 0;
            worm.HasTarget = true;
            worm.Target = target;
            SetRadius(ref worm, 0, c);
            return true;
        }

        /// <summary>
        ///     The start state of random worm <paramref name="k" /> of <paramref name="cell" /> at anchor
        ///     <paramref name="a" />: position, heading, hash, shaft, rolled length, branch step (after the shaft) and
        ///     radius; no target.
        /// </summary>
        private static void BeginRandom(int2 cell, int k, in WormAnchor a, in WorldGenConfig cfg, out Worm worm){
            ref readonly CaveConfig c = ref cfg.Caves;
            uint hash = WormHash(cell, k, cfg);
            worm = default;
            worm.Position = new float3(a.Column.x + .5f, math.max(a.Y, c.WormMinY) + .5f, a.Column.y + .5f);
            worm.Origin = worm.Position.xz;
            worm.Pitch = a.Entrance ? -c.WormMaxPitch : (Roll(hash, RollPitch) * 2f - 1f) * .5f * c.WormMaxPitch;
            worm.Yaw = Roll(hash, RollYaw) * 2f * math.PI;
            worm.Hash = hash;
            worm.ShaftAbove = a.ShaftAbove;
            worm.Shaft = ShaftSteps(a);
            int rolled = c.WormLength.x + (int)(RollBits(hash, RollLength) % (uint)(c.WormLength.y - c.WormLength.x + 1));
            worm.Length = worm.Shaft + rolled;
            worm.BranchStep = rolled >= 2 && Roll(hash, RollBranch) < c.WormBranchChance
                ? worm.Shaft + 1 + (int)(RollBits(hash, RollBranchStep) % (uint)(rolled - 1))
                : -1;
            SetRadius(ref worm, 0, c);
        }

        /// <summary>
        ///     Target of the link of random worm <paramref name="source" /> of <paramref name="cell" /> that leaves at
        ///     <paramref name="port" />: a point on the path of the nearest other random worm of the 3 × 3 cells by
        ///     the estimated way from the port (<see cref="LinkCost" />) times a hashed factor of
        ///     1…1 + <see cref="TargetSpread" />, so that the worms around one start do not all link to it. The point is
        ///     the nearest in reach of <see cref="EndCandidates" /> hashed steps spread along that path, found in one
        ///     walk of it; the start itself when none is in reach. False when no start is in reach.
        /// </summary>
        private static bool LinkTarget(int2 cell, int source, in Worm port, uint hash, in WorldGenConfig cfg, in BlendCache cache,
            in WormAnchors anchors, out float3 target){
            ref readonly CaveConfig c = ref cfg.Caves;
            target = default;
            float best = float.MaxValue;
            int2 bestCell = default;
            int bestSlot = -1;
            WormAnchor bestAnchor = default;
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            for (int j = 0; j < PrimariesPerCell; j++){
                if (dx == 0 && dz == 0 && j == source) continue;
                WormAnchor o = Lookup(cell + new int2(dx, dz), j, cfg, cache, anchors);
                if (!o.Present) continue;
                float cost = LinkCost(port.Origin, port.Position, o.Aim, c);
                if (cost == float.MaxValue) continue;
                cost *= 1f + TargetSpread * Hash.Hash01((dx + 1) * 3 + dz + 1, j, hash, HashSalts.WormTarget);
                if (cost < best){
                    best = cost;
                    bestCell = cell + new int2(dx, dz);
                    bestSlot = j;
                    bestAnchor = o;
                }
            }

            if (bestSlot < 0) return false;
            target = bestAnchor.Aim;

            // The candidate steps of the other path, in ascending order so that one walk visits them all.
            BeginRandom(bestCell, bestSlot, bestAnchor, cfg, out Worm other);
            int span = math.max(other.Length - other.Shaft, 1);
            int first = other.Shaft + (int)(RollBits(hash, RollEnd) % (uint)span);
            int4 steps = default;
            for (int i = 0; i < EndCandidates; i++){
                int s = first + i * span / EndCandidates;
                steps[i] = s >= other.Shaft + span ? s - span : s;
            }

            for (int i = 1; i < EndCandidates; i++)
            for (int j = i; j > 0 && steps[j] < steps[j - 1]; j--){
                int t = steps[j];
                steps[j] = steps[j - 1];
                steps[j - 1] = t;
            }

            best = float.MaxValue;
            for (int i = 0; i < EndCandidates; i++){
                other = Port(other, steps[i], c);
                if (other.Step != steps[i]) break;
                float cost = LinkCost(port.Origin, port.Position, other.Position, c);
                if (cost >= best) continue;
                best = cost;
                target = other.Position;
            }

            return true;
        }

        /// <summary>
        ///     Target of the hall link that leaves at <paramref name="port" />: the nearest column to its worm's start
        ///     on the hall lattice whose cavern mask is fully open (<see cref="HallColumn" />), offset by a hashed
        ///     quarter of the lattice step so that nearby hall links do not all end at one point, at
        ///     <see cref="HallAimAbove" /> blocks over the floor of the cavern band or as deep as
        ///     <see cref="TargetSlopeShare" /> of the largest pitch gets from the port. False when no such column lies
        ///     within <see cref="TargetReach" />.
        /// </summary>
        private static bool HallTarget(in Worm port, uint hash, in WorldGenConfig cfg, out float3 target){
            ref readonly CaveConfig c = ref cfg.Caves;
            target = default;
            if (!HallColumn((int2)math.floor(port.Origin), cfg, out int2 column)) return false;
            float2 offset = (new float2(Roll(hash, RollHallX), Roll(hash, RollHallZ)) - .5f) * (HallSearchStep * .5f);
            float2 xz = (float2)column + .5f + offset;
            float flat = math.length(xz - port.Position.xz);
            float floorAim = math.max(c.CavernBandMin + HallAimAbove, c.WormMinY + 1) + .5f;
            float y = math.max(floorAim, port.Position.y - TargetSlopeShare * Slope(c) * flat);
            target = new float3(xz.x, y, xz.y);
            return true;
        }

        /// <summary>
        ///     Nearest point to <paramref name="origin" /> on the world-aligned lattice of <see cref="HallSearchStep" />
        ///     whose cavern mask is fully open (<see cref="Caverns.MaskFull" />) and that lies within
        ///     <see cref="TargetReach" /> of <c>WormReach</c>, searched ring by ring around the lattice point nearest
        ///     the origin; false when none.
        /// </summary>
        public static bool HallColumn(int2 origin, in WorldGenConfig cfg, out int2 column){
            ref readonly CaveConfig c = ref cfg.Caves;
            column = default;
            float reach = TargetReach * c.WormReach;
            int2 centre = (int2)math.floor((float2)origin / HallSearchStep + .5f) * HallSearchStep;
            int rings = (int)(reach / HallSearchStep) + 1;
            for (int r = 0; r <= rings; r++){
                float best = float.MaxValue;
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++){
                    if (math.max(math.abs(dx), math.abs(dz)) != r) continue;
                    int2 p = centre + new int2(dx, dz) * HallSearchStep;
                    float d = math.lengthsq((float2)(p - origin));
                    if (d > reach * reach || d >= best) continue;
                    if (!Caverns.MaskFull(CaveMasks.Cavern(p.x, p.y, cfg), c)) continue;
                    best = d;
                    column = p;
                }

                if (best < float.MaxValue) return true;
            }

            return false;
        }

        /// <summary>Blocks a worm climbs or descends per step at its largest pitch, at least 0.1.</summary>
        private static float Climb(in CaveConfig c){
            return math.max(math.sin(c.WormMaxPitch), .1f);
        }

        /// <summary>Blocks a worm climbs or descends per block of horizontal way at its largest pitch, at least 0.1.</summary>
        private static float Slope(in CaveConfig c){
            return math.max(math.tan(c.WormMaxPitch), .1f);
        }

        /// <summary>
        ///     Estimated path length of a connector from <paramref name="from" /> to <paramref name="to" />: the
        ///     horizontal distance plus the climb at the largest pitch; float.MaxValue when the target lies beyond
        ///     <see cref="TargetReach" /> of the connector's worm start <paramref name="origin" /> or steeper than
        ///     <see cref="TargetSlopeShare" /> of the largest pitch from <paramref name="from" />.
        /// </summary>
        private static float LinkCost(float2 origin, float3 from, float3 to, in CaveConfig c){
            float3 d = to - from;
            float flat = math.length(d.xz);
            if (math.length(to.xz - origin) > TargetReach * c.WormReach || math.abs(d.y) > TargetSlopeShare * Slope(c) * flat)
                return float.MaxValue;
            return flat + math.abs(d.y) / Climb(c);
        }

        /// <summary>Steps granted to walk from <paramref name="from" /> to <paramref name="to" />.</summary>
        private static int StepsTo(float3 from, float3 to, in CaveConfig c){
            float3 d = to - from;
            return (int)math.ceil(TargetStepsPerBlock * math.max(math.length(d.xz), math.abs(d.y) / Climb(c))) + TargetSlack;
        }

        /// <summary>
        ///     Most steps one slot of a cell walks with its branch: a random worm with the shaft from the top of the
        ///     world, the longest rolled length and a branch of half of it, or a connector that leaves at the reach of
        ///     its worm for a target at <see cref="TargetReach" /> on the far side, or climbs the world.
        /// </summary>
        public static int MaxSteps(in CaveConfig c){
            int main = c.Cover + (int)math.ceil(c.WormMaxRadius) + 1 + c.WormLength.y;
            float flat = (1f + TargetReach) * c.WormReach, climb = ChunkHeight / Climb(c);
            int connector = (int)math.ceil(TargetStepsPerBlock * math.max(flat, climb)) + TargetSlack;
            return math.max(main + main / 2, connector);
        }

        /// <summary>
        ///     The branch of <paramref name="parent" />, which stands at its <see cref="Worm.BranchStep" />: it turns a
        ///     quarter to one side, takes half the remaining length and walks from its own hash. False when that
        ///     length is 0.
        /// </summary>
        public static bool Branch(in Worm parent, in CaveConfig c, out Worm branch){
            branch = parent;
            branch.Hash = Hash.Hash32(1, 0, parent.Hash, HashSalts.Worm);
            branch.Step = 0;
            branch.Length = (parent.Length - parent.Step) / 2;
            branch.BranchStep = -1;
            branch.Shaft = 0;
            branch.Yaw += (Roll(parent.Hash, RollBranchSide) < .5f ? .5f : -.5f) * math.PI;
            branch.YawRate = 0f;
            branch.PitchRate = 0f;
            branch.HasTarget = false;
            branch.Target = default;
            SetRadius(ref branch, 0, c);
            return branch.Length > 0;
        }

        /// <summary>The sphere of <paramref name="worm" /> reaches its target: the two tunnels overlap.</summary>
        public static bool Reached(in Worm worm, in CaveConfig c){
            float r = worm.Radius + c.WormMinRadius;
            return math.lengthsq(worm.Target - worm.Position) <= r * r;
        }

        /// <summary>
        ///     Moves <paramref name="worm" /> one block on: the yaw and pitch rates keep <c>WormTurnDamping</c> of
        ///     their value and gain a jitter, the pitch relaxes towards level and stays within ±<c>WormMaxPitch</c>,
        ///     and the worm bounces off <c>WormMinY</c>. A connector turns its yaw <c>WormSteer</c> of the way towards
        ///     its target instead and relaxes its pitch towards it. An entrance worm above
        ///     <see cref="Worm.ShaftAbove" /> steps straight down instead. False when the path ends: its length is used
        ///     up, a connector has reached its target, or the next step would leave <c>WormReach</c> of the start.
        /// </summary>
        public static bool Next(ref Worm worm, in CaveConfig c){
            int step = worm.Step + 1;
            if (step >= worm.Length) return false;
            if (worm.HasTarget && Reached(worm, c)) return false;
            if (worm.Position.y > worm.ShaftAbove){
                worm.Position.y -= 1f;
                worm.Step = step;
                SetRadius(ref worm, step, c);
                return true;
            }

            float damping = c.WormTurnDamping;
            float2 jitter = Hash.HashSigned2(step, 0, worm.Hash, HashSalts.WormTurn);
            worm.YawRate = worm.YawRate * damping + c.WormYawJitter * jitter.x;
            worm.PitchRate = worm.PitchRate * damping + c.WormPitchJitter * jitter.y;
            worm.Yaw += worm.YawRate;
            float target = 0f;
            if (worm.HasTarget){
                float3 d = worm.Target - worm.Position;
                Steer(ref worm, d.xz, c);
                target = math.clamp(math.atan2(d.y, math.length(d.xz)), -c.WormMaxPitch, c.WormMaxPitch);
            }

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
            SetRadius(ref worm, step, c);
            return true;
        }

        /// <summary>
        ///     Sets the radius at <paramref name="step" /> as <see cref="RadiusAt" /> does, rolling the knot radii only
        ///     when the step enters a new knot: step 0 starts the knots afresh (a branch has a new hash).
        /// </summary>
        private static void SetRadius(ref Worm worm, int step, in CaveConfig c){
            int period = c.WormRadiusPeriod;
            int knot = step / period;
            if (step == 0){
                worm.RadiusKnot = 0;
                worm.RadiusFrom = KnotRadius(worm.Hash, 0, c);
                worm.RadiusTo = KnotRadius(worm.Hash, 1, c);
            }
            else if (knot != worm.RadiusKnot){
                worm.RadiusKnot = knot;
                worm.RadiusFrom = worm.RadiusTo;
                worm.RadiusTo = KnotRadius(worm.Hash, knot + 1, c);
            }

            float f = (step - knot * period) / (float)period;
            worm.Radius = math.lerp(worm.RadiusFrom, worm.RadiusTo, math.smoothstep(0f, 1f, f));
        }

        private static float KnotRadius(uint hash, int knot, in CaveConfig c){
            return math.lerp(c.WormMinRadius, c.WormMaxRadius, Hash.Hash01(knot, 0, hash, HashSalts.WormRadius));
        }

        /// <summary>
        ///     Turns the yaw <c>WormSteer</c> of the way towards the horizontal direction <paramref name="d" />: the
        ///     signed angle from the heading to it, from their cross and dot products, so one atan2 does the wrap.
        /// </summary>
        private static void Steer(ref Worm worm, float2 d, in CaveConfig c){
            // Over the point itself the direction means nothing; the worm keeps its heading.
            if (math.lengthsq(d) < 1f) return;
            math.sincos(worm.Yaw, out float hz, out float hx);
            worm.Yaw += c.WormSteer * math.atan2(hx * d.y - hz * d.x, hx * d.x + hz * d.y);
        }

        /// <summary>
        ///     Radius at <paramref name="step" />: a smoothstep between radii rolled every <c>WormRadiusPeriod</c> steps
        ///     in <c>WormMinRadius</c>…<c>WormMaxRadius</c>.
        /// </summary>
        public static float RadiusAt(uint hash, int step, in CaveConfig c){
            int period = c.WormRadiusPeriod;
            int knot = step / period;
            float f = (step - knot * period) / (float)period;
            return math.lerp(KnotRadius(hash, knot, c), KnotRadius(hash, knot + 1, c), math.smoothstep(0f, 1f, f));
        }

        /// <summary>Voxel <paramref name="voxel" /> lies in the sphere a worm carves at its current step.</summary>
        public static bool Contains(in Worm worm, int3 voxel){
            return math.lengthsq((float3)voxel + .5f - worm.Position) <= worm.Radius * worm.Radius;
        }

        /// <summary>
        ///     Some worm sphere contains voxel (x, y, z), whatever the column limits: walks every worm whose start lies
        ///     within <c>WormPadding</c> of the column, with its branch and its connectors. The pure reference of the
        ///     chunk pass, slow.
        /// </summary>
        public static bool Covers(int x, int y, int z, in WorldGenConfig cfg, in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            int3 voxel = new(x, y, z);
            int padding = c.WormPadding;
            int2 first = Cell(new int2(x, z) - padding, cfg), last = Cell(new int2(x, z) + padding, cfg);
            WormAnchors anchors = WormAnchors.Create(first - 1, last + 1, cfg, cache, Allocator.Temp);
            bool covers = false;
            for (int cx = first.x; cx <= last.x && !covers; cx++)
            for (int cz = first.y; cz <= last.y && !covers; cz++)
            for (int k = 0; k < MaxPerCell && !covers; k++){
                int2 cell = new(cx, cz);
                int2 start = StartColumn(cell, k, cfg);
                if (math.any(math.abs(start - new int2(x, z)) > padding)) continue;
                if (!Begin(cell, k, cfg, cache, anchors, out Worm worm)) continue;
                Worm branch = default;
                bool branched = false;
                do{
                    if (Contains(worm, voxel)) covers = true;
                    if (worm.Step == worm.BranchStep) branched = Branch(worm, c, out branch);
                } while (!covers && Next(ref worm, c));

                if (covers || !branched) continue;
                do{
                    if (Contains(branch, voxel)) covers = true;
                } while (!covers && Next(ref branch, c));
            }

            anchors.Dispose();
            return covers;
        }
    }
}
