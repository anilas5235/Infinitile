using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     State of one worm on its path: the centre and radius of the sphere it carves at <see cref="Step" />, the
    ///     turn rates that carry it to the next step and the targets it steers towards. <see cref="Worms.Begin" />
    ///     starts it, <see cref="Worms.Next" /> walks it, <see cref="Worms.Branch" /> forks it.
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

        /// <summary>Start of the main worm (x, z); the path and its branch stay within <c>WormReach</c> of it.</summary>
        public float2 Origin;

        /// <summary>Step at which the branch leaves, −1 for none (and on a branch).</summary>
        public int BranchStep;

        /// <summary>
        ///     A worm that starts in the entrance mask goes straight down while it is above this y, so its shaft stays
        ///     in the mask until the column limits outside it let the tunnel through; int.MaxValue for others.
        /// </summary>
        public int ShaftAbove;

        /// <summary>A worm aims down while it is above this y (entrance worms, starts in the cavern mask); int.MaxValue for others.</summary>
        public int DescendAbove;

        /// <summary>Column (x, z) the worm steers towards while it is above <see cref="DescendAbove" />: where its descent ends.</summary>
        public float2 DescendTowards;

        /// <summary>
        ///     Step after which the path steers at its targets: the shaft plus <see cref="Worms.TrunkSteps" /> for a
        ///     primary worm, whose first steps other worms aim at and so must not depend on its own targets; 0 for a
        ///     bridge and a branch.
        /// </summary>
        public int SteerFrom;

        /// <summary>Point the path steers towards while <see cref="TargetsLeft" /> is positive: a point on the trunk of another worm.</summary>
        public float3 Target;

        /// <summary>The target after <see cref="Target" /> when <see cref="TargetsLeft" /> is 2.</summary>
        public float3 NextTarget;

        /// <summary>Targets still ahead on this path, 0…2.</summary>
        public int TargetsLeft;

        /// <summary>Target the branch takes when <see cref="BranchTargets" /> is 1.</summary>
        public float3 BranchTarget;

        public int BranchTargets;
    }

    /// <summary>
    ///     Start of a primary worm: what <see cref="Worms.Begin" /> needs of it, and the point other worms aim at. The
    ///     same for every block, like the worm itself.
    /// </summary>
    public struct WormAnchor{
        /// <summary>The worm exists and its start column has room for it.</summary>
        public bool Present;

        public int2 Column;

        /// <summary>Start y before the clamp to <c>WormMinY</c>.</summary>
        public int Y;

        public bool Entrance;
        public int ShaftAbove;
        public int DescendAbove;

        /// <summary>
        ///     Centre of the first sphere of a free worm, the bottom of the shaft of an entrance worm: the start of the
        ///     trunk other worms aim at, and the point their choice of targets measures.
        /// </summary>
        public float3 Aim;

        /// <summary>
        ///     Starts of the 3 × 3 cells the worm steers at, nearest first (<see cref="Worms.Steering" />), each
        ///     <see cref="Worms.PackPick" />ed, −1 for none; <see cref="Found" /> is −1 until they are computed.
        /// </summary>
        public int Pick0;

        public int Pick1;
        public int Pick2;
        public int Found;

        /// <summary>Column the worm steers at while it descends: where the descent ends on the way to its first pick.</summary>
        public float2 DescendTowards;

        /// <summary>The picks and the descent are computed.</summary>
        public bool Steered => Found >= 0;
    }

    /// <summary>
    ///     The anchors of the primary worms of a range of cells, computed once per block so that the targets of each
    ///     worm need no second surface lookup, with the steering of all but the outer ring of cells (whose 3 × 3 the
    ///     table does not hold). The default (no table) makes <see cref="Worms.Begin" /> compute every anchor it needs
    ///     on demand.
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

            for (int cx = first.x + 1; cx < last.x; cx++)
            for (int cz = first.y + 1; cz < last.y; cz++)
            for (int k = 0; k < Worms.PrimariesPerCell; k++){
                int2 cell = new(cx, cz);
                WormAnchor a = anchors.Get(cell, k);
                if (!a.Present) continue;
                Worms.Steering(cell, k, ref a, cfg, cache, anchors);
                anchors.Table[anchors.Index(cell, k)] = a;
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
    ///     Worm tunnels after the cave carvers of Minecraft (MapGenCaves of the Java Edition): worms start in square
    ///     cells, walk in steps of one block with yaw and pitch turning in a damped random walk, carve a sphere at
    ///     every step whose radius changes smoothly, and may branch once. Every roll comes from the worm's own hash,
    ///     so a path is the same in every block that walks it; a block walks every worm that can reach it in full and
    ///     carves only the steps that touch it.
    ///     So that no tunnel lies alone, a worm steers towards the nearest other starts of its 3 × 3 cells
    ///     (<c>WormTargets</c> of them, its branch takes the next), with its length extended to reach the first, and
    ///     each cell with a worm sends a bridge worm to the nearest start of its +x and +z neighbour cells
    ///     (<c>WormBridges</c>). A start in the cavern mask descends to the top of the cavern band first, where the
    ///     halls are. The first <see cref="TrunkSteps" /> steps of a primary worm, its trunk, do not steer at its
    ///     targets, so they follow from its anchor alone; the worms that aim at it aim at hashed points along the
    ///     trunk, and its bridges leave from such points, so the tunnels meet along the trunk instead of at one point.
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
        private const int RollTrunkStep = 9;

        /// <summary>
        ///     Worms a start cell rolls for: slot k is present with chance WormsPerCell − k (clamped to 0…1), so a cell
        ///     holds the whole part of WormsPerCell and one more with the fraction as its chance.
        /// </summary>
        public const int PrimariesPerCell = 2;

        /// <summary>Slot of the bridge to the +x neighbour cell.</summary>
        public const int BridgeX = PrimariesPerCell;

        /// <summary>Slot of the bridge to the +z neighbour cell.</summary>
        public const int BridgeZ = PrimariesPerCell + 1;

        /// <summary>Slots a start cell holds: the primary worms and the two bridges.</summary>
        public const int MaxPerCell = PrimariesPerCell + 2;

        /// <summary>Most targets a path takes, the slots of <see cref="Worm.Target" /> and <see cref="Worm.NextTarget" />.</summary>
        public const int MaxTargets = 2;

        /// <summary>
        ///     Share of <c>WormReach</c> within which a start is a target, measured horizontally from the worm's start:
        ///     the rest of the reach absorbs the wander of the steered path, which ends at the reach.
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
        ///     Share of the slope at the largest pitch a target may lie above or below its worm: steeper starts are
        ///     left out, since a worm that has to climb more than it walks circles up or down under its target.
        /// </summary>
        public const float TargetSlopeShare = .6f;

        /// <summary>
        ///     Spread of the hashed factor (1…1 + this) on the estimated way to a target, so that the worms around one
        ///     start do not all pick it as their nearest and meet at one point.
        /// </summary>
        public const float TargetSpread = .5f;

        /// <summary>
        ///     Cosine of the angle from the first target within which a later target counts as the same direction and
        ///     its way counts double, so the later targets lead the worm into other directions.
        /// </summary>
        public const float TargetSameDirection = .5f;

        /// <summary>
        ///     Steps of the trunk of a primary worm: its path after its shaft and before it steers at its targets, along
        ///     which other worms arrive and its bridges leave.
        /// </summary>
        public const int TrunkSteps = 16;

        /// <summary>
        ///     Worm <paramref name="k" /> of start cell <paramref name="cell" /> may exist: a primary slot (0, 1) by its
        ///     roll, a bridge slot (2, 3) when bridges are on and the cell has a primary worm to leave from.
        ///     <see cref="Begin" /> still returns false when the start has no room or a bridge no target.
        /// </summary>
        public static bool Present(int2 cell, int k, in WorldGenConfig cfg){
            if (k < PrimariesPerCell)
                return Hash.Hash01(cell.x, k, cell.y, cfg.Seed, HashSalts.WormCount) < math.saturate(cfg.Caves.WormsPerCell - k);
            return cfg.Caves.WormBridges && BridgeSource(cell, k, cfg) >= 0;
        }

        /// <summary>
        ///     Primary slot bridge <paramref name="k" /> of <paramref name="cell" /> leaves from: the first present
        ///     worm for the +x bridge, the last for the +z bridge, so a cell with two worms sends one from each; −1
        ///     when the cell rolls no worm.
        /// </summary>
        public static int BridgeSource(int2 cell, int k, in WorldGenConfig cfg){
            for (int i = 0; i < PrimariesPerCell; i++){
                int j = k == BridgeZ ? PrimariesPerCell - 1 - i : i;
                if (Present(cell, j, cfg)) return j;
            }

            return -1;
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
        ///     Start column of worm <paramref name="k" /> of <paramref name="cell" />, whether or not it exists; a
        ///     bridge starts in the column of its source.
        /// </summary>
        public static int2 StartColumn(int2 cell, int k, in WorldGenConfig cfg){
            if (k >= PrimariesPerCell) k = math.max(BridgeSource(cell, k, cfg), 0);
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
        ///     Anchor of primary worm <paramref name="k" /> of <paramref name="cell" />: not present when the cell has
        ///     no such worm or its start column has no room for one (surface − Cover below the lowest start height
        ///     outside an entrance; outside the entrance mask the surface is the unblended height).
        ///     A worm in the entrance mask starts up to <c>EntranceStartDepth</c> below the surface, goes straight down
        ///     until its sphere lies below <c>Cover</c> (outside the mask nothing above it is carved, so a sloped start
        ///     left the opening a dead end), then aims down to the top of the cavern band, where the halls and the
        ///     other worms run; a free worm that starts in the cavern mask above the band aims down to it as well. The
        ///     start leaves out the water test of the entrance: starts lie up to <c>WormPadding</c> away, beyond the
        ///     block's probes, and the column limits keep the worm <c>Cover</c> deep wherever water is near.
        /// </summary>
        public static WormAnchor Anchor(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            WormAnchor a = default;
            if (!Present(cell, k, cfg)) return a;
            uint hash = WormHash(cell, k, cfg);
            a.Column = StartColumn(cell, k, cfg);
            // Only a start in the entrance mask needs the exact surface, for its opening. The depth range of the others
            // takes the unblended height, which in Biome-first saves a blend of every kernel tap per start; the column
            // limits still keep those worms Cover below the exact surface.
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
                a.DescendAbove = math.min(a.ShaftAbove, c.CavernBandMax);
                aimY = math.min(a.Y, a.ShaftAbove - 1);
            }
            else{
                int top = surface - c.Cover;
                if (top < c.WormStartMinY) return a;
                a.Y = c.WormStartMinY + (int)(Roll(hash, RollY) * (top - c.WormStartMinY + 1));
                a.ShaftAbove = int.MaxValue;
                bool inCaverns = CaveMasks.Cavern(a.Column.x, a.Column.y, cfg) > c.CavernMaskThreshold;
                a.DescendAbove = inCaverns && a.Y > c.CavernBandMax ? c.CavernBandMax : int.MaxValue;
                aimY = math.max(a.Y, c.WormMinY);
            }

            a.Aim = new float3(a.Column.x + .5f, aimY + .5f, a.Column.y + .5f);
            a.Present = true;
            a.Found = -1;
            return a;
        }

        /// <summary>Anchor of worm <paramref name="k" /> of <paramref name="cell" />, from the table when it holds the cell.</summary>
        private static WormAnchor Lookup(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors){
            return anchors.Has(cell) ? anchors.Get(cell, k) : Anchor(cell, k, cfg, cache);
        }

        /// <summary>The anchor with its <see cref="Steering" />, computed when the table does not hold it.</summary>
        private static WormAnchor LookupSteered(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors){
            WormAnchor a = Lookup(cell, k, cfg, cache, anchors);
            if (a.Present && !a.Steered) Steering(cell, k, ref a, cfg, cache, anchors);
            return a;
        }

        /// <summary>Pick of start <paramref name="j" /> of the cell (<paramref name="dx" />, <paramref name="dz" />) away, −1…1 each.</summary>
        public static int PackPick(int dx, int dz, int j){
            return ((dx + 1) * 3 + dz + 1) * PrimariesPerCell + j;
        }

        /// <summary>Cell offset and slot of a <see cref="PackPick" />ed pick.</summary>
        public static void UnpackPick(int pick, out int2 offset, out int j){
            int idx = pick / PrimariesPerCell;
            offset = new int2(idx / 3 - 1, idx % 3 - 1);
            j = pick % PrimariesPerCell;
        }

        /// <summary>
        ///     Fills the steering of anchor <paramref name="a" /> of worm <paramref name="k" /> of <paramref name="cell" />:
        ///     its picks, the other starts of the 3 × 3 cells within <see cref="TargetReach" /> and
        ///     <see cref="TargetSlopeShare" /> of the point where its steering begins (the end of its descent, else its
        ///     aim), by the estimated way there times a hashed factor of 1…1 + <see cref="TargetSpread" />: the first
        ///     is the nearest by that measure, and for the later ones the way of a start in the direction of the first
        ///     counts double, so the worm leaves in other directions after it. A descent ends where a path at the
        ///     largest pitch, headed for the first pick (or along the start yaw), gets below the band top; the worm
        ///     steers at that column while it descends, so it comes out of the descent on its way.
        /// </summary>
        public static void Steering(int2 cell, int k, ref WormAnchor a, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors){
            ref readonly CaveConfig c = ref cfg.Caves;
            uint hash = WormHash(cell, k, cfg);
            float3 origin = SteerOrigin(a);

            // The first pick: the nearest other start by the spread way.
            int found = 0, p0 = -1, p1 = -1, p2 = -1;
            float3 t0 = default;
            float c0 = float.MaxValue, c1 = float.MaxValue, c2 = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            for (int j = 0; j < PrimariesPerCell; j++){
                if (dx == 0 && dz == 0 && j == k) continue;
                WormAnchor o = Lookup(cell + new int2(dx, dz), j, cfg, cache, anchors);
                if (!o.Present) continue;
                float cost = TargetCost(origin, o.Aim, hash, dx, dz, j, c);
                if (cost == float.MaxValue) continue;
                found = math.min(found + 1, MaxTargets + 1);
                if (cost < c0){
                    c0 = cost;
                    t0 = o.Aim;
                    p0 = PackPick(dx, dz, j);
                }
            }

            // The later picks: the next nearest, with the way of a start in the first pick's direction doubled.
            if (found > 1){
                float2 dir0 = math.normalizesafe((t0 - origin).xz);
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                for (int j = 0; j < PrimariesPerCell; j++){
                    if (dx == 0 && dz == 0 && j == k) continue;
                    int pick = PackPick(dx, dz, j);
                    if (pick == p0) continue;
                    WormAnchor o = Lookup(cell + new int2(dx, dz), j, cfg, cache, anchors);
                    if (!o.Present) continue;
                    float cost = TargetCost(origin, o.Aim, hash, dx, dz, j, c);
                    if (cost == float.MaxValue) continue;
                    if (math.dot(dir0, math.normalizesafe((o.Aim - origin).xz)) > TargetSameDirection) cost *= 2f;
                    if (cost < c1){
                        c2 = c1; p2 = p1;
                        c1 = cost; p1 = pick;
                    }
                    else if (cost < c2){
                        c2 = cost; p2 = pick;
                    }
                }
            }

            a.Pick0 = p0;
            a.Pick1 = p1;
            a.Pick2 = p2;
            a.Found = found;

            a.DescendTowards = a.Aim.xz;
            if (Descends(a)){
                float yaw = Roll(hash, RollYaw) * 2f * math.PI;
                float2 heading = new(math.cos(yaw), math.sin(yaw));
                float2 dir = found > 0 ? math.normalizesafe((t0 - a.Aim).xz, heading) : heading;
                a.DescendTowards = a.Aim.xz + dir * ((a.Aim.y - a.DescendAbove) / Slope(c));
            }
        }

        /// <summary>Steps of the shaft of an entrance worm, straight down from its start to its aim; 0 for a free worm.</summary>
        private static int ShaftSteps(in WormAnchor a){
            return a.Entrance ? math.max(a.Y - a.ShaftAbove + 1, 0) : 0;
        }

        /// <summary>The worm descends to the cavern band before it steers at its targets.</summary>
        private static bool Descends(in WormAnchor a){
            return a.DescendAbove != int.MaxValue && a.Aim.y > a.DescendAbove;
        }

        /// <summary>Point the worm's targets are measured from: the end of its descent, else its aim.</summary>
        private static float3 SteerOrigin(in WormAnchor a){
            return Descends(a) ? new float3(a.Aim.x, a.DescendAbove - .5f, a.Aim.z) : a.Aim;
        }

        /// <summary>
        ///     Position of worm <paramref name="k" /> of <paramref name="cell" /> (anchor <paramref name="a" />) after
        ///     its shaft and <paramref name="steps" /> steps of its trunk, which follow from the anchor alone.
        /// </summary>
        public static float3 TrunkPoint(int2 cell, int k, in WormAnchor a, int steps, in WorldGenConfig cfg){
            Worm worm = default;
            BeginTrunk(cell, k, a, cfg, ref worm);
            steps += ShaftSteps(a);
            worm.Length = steps + 1;
            for (int i = 0; i < steps && Next(ref worm, cfg.Caves); i++){ }

            return worm.Position;
        }

        /// <summary>
        ///     Point on the trunk of the start <paramref name="target" /> (worm <paramref name="tk" /> of
        ///     <paramref name="tcell" />) a worm with hash <paramref name="hash" /> aims at for its target
        ///     <paramref name="slot" />: a hashed step of the trunk, or the aim itself when that point lies out of reach
        ///     or too steep from <paramref name="origin" />.
        /// </summary>
        private static float3 Arrival(int2 tcell, int tk, in WormAnchor target, uint hash, int slot, float3 origin, in WorldGenConfig cfg){
            int steps = (int)(Hash.Hash32(slot, 0, hash, HashSalts.WormTarget) % (uint)(TrunkSteps + 1));
            float3 point = TrunkPoint(tcell, tk, target, steps, cfg);
            return TargetCost(origin, point, cfg.Caves) == float.MaxValue ? target.Aim : point;
        }

        /// <summary>Starts worm <paramref name="k" /> of <paramref name="cell" />, computing every anchor it needs.</summary>
        public static bool Begin(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, out Worm worm){
            return Begin(cell, k, cfg, cache, default, out worm);
        }

        /// <summary>
        ///     Starts worm <paramref name="k" /> of <paramref name="cell" />; false when the slot holds no worm (see
        ///     <see cref="Anchor" />, and a bridge whose neighbour cell has no start in reach or whose target the source
        ///     worm steers at already). A primary worm takes the picks of its anchor as targets (see
        ///     <see cref="Steering" /> and <see cref="BeginPrimary" />), its length extended to reach the first; its
        ///     branch takes the next one. A bridge starts at a hashed point of its source's trunk with a hashed trunk
        ///     point of the nearest start of the neighbour cell as its one target and the length to reach it.
        ///     <paramref name="anchors" /> may hold the anchors of the cells, else they are computed.
        /// </summary>
        public static bool Begin(int2 cell, int k, in WorldGenConfig cfg, in BlendCache cache, in WormAnchors anchors, out Worm worm){
            ref readonly CaveConfig c = ref cfg.Caves;
            worm = default;
            if (!Present(cell, k, cfg)) return false;
            if (k < PrimariesPerCell){
                WormAnchor self = LookupSteered(cell, k, cfg, cache, anchors);
                if (!self.Present) return false;
                BeginPrimary(cell, k, self, cfg, cache, anchors, out worm, out _);
                return true;
            }

            int source = BridgeSource(cell, k, cfg);
            WormAnchor from = LookupSteered(cell, source, cfg, cache, anchors);
            if (!from.Present) return false;
            int2 offset = k == BridgeX ? new int2(1, 0) : new int2(0, 1);
            int2 neighbour = cell + offset;
            float best = float.MaxValue;
            int pick = -1;
            WormAnchor target = default;
            for (int j = 0; j < PrimariesPerCell; j++){
                WormAnchor a = Lookup(neighbour, j, cfg, cache, anchors);
                if (!a.Present) continue;
                float cost = TargetCost(from.Aim, a.Aim, c);
                if (cost < best){
                    best = cost;
                    pick = PackPick(offset.x, offset.y, j);
                    target = a;
                }
            }

            if (best == float.MaxValue) return false;
            BeginPrimary(cell, source, from, cfg, cache, anchors, out _, out int3 picks);
            if (math.any(picks == pick)) return false;

            uint hash = WormHash(cell, k, cfg);
            float3 start = TrunkPoint(cell, source, from, 1 + (int)(RollBits(hash, RollTrunkStep) % (uint)TrunkSteps), cfg);
            target = LookupSteered(neighbour, pick % PrimariesPerCell, cfg, cache, anchors);
            float3 aim = Arrival(neighbour, pick % PrimariesPerCell, target, hash, 0, start, cfg);
            float3 d = aim - start;
            worm.Position = start;
            worm.Origin = from.Aim.xz;
            worm.Yaw = math.atan2(d.z, d.x);
            worm.Pitch = math.clamp(math.atan2(d.y, math.length(d.xz)), -c.WormMaxPitch, c.WormMaxPitch);
            worm.Hash = hash;
            worm.Length = StepsTo(start, aim, c);
            worm.BranchStep = -1;
            worm.ShaftAbove = int.MaxValue;
            worm.DescendAbove = int.MaxValue;
            worm.SteerFrom = 0;
            worm.Target = aim;
            worm.TargetsLeft = 1;
            SetRadius(ref worm, 0, c);
            return true;
        }

        /// <summary>
        ///     Starts primary worm <paramref name="k" /> at its steered anchor <paramref name="self" />: its targets are
        ///     the <see cref="Arrival" /> points on the trunks of its picks, the first <c>WormTargets</c> on the main
        ///     path and the next one for the branch. <paramref name="picks" /> returns the picks those targets came
        ///     from (−1 for none): the main targets in x and y, the branch pick in z when the worm branches.
        /// </summary>
        private static void BeginPrimary(int2 cell, int k, in WormAnchor self, in WorldGenConfig cfg, in BlendCache cache,
            in WormAnchors anchors, out Worm worm, out int3 picks){
            ref readonly CaveConfig c = ref cfg.Caves;
            worm = default;
            picks = -1;
            uint hash = WormHash(cell, k, cfg);
            BeginTrunk(cell, k, self, cfg, ref worm);
            int shaft = ShaftSteps(self);
            worm.SteerFrom = shaft + TrunkSteps;

            int targets = math.min(c.WormTargets, self.Found);
            float3 origin = SteerOrigin(self);
            if (targets > 0){
                worm.Target = Arrival(cell, self.Pick0, 0, origin, hash, cfg, cache, anchors);
                picks.x = self.Pick0;
            }

            if (targets > 1){
                worm.NextTarget = Arrival(cell, self.Pick1, 1, origin, hash, cfg, cache, anchors);
                picks.y = self.Pick1;
            }

            worm.TargetsLeft = targets;
            int branchPick = targets == 0 ? self.Pick0 : targets == 1 ? self.Pick1 : self.Pick2;
            if (self.Found > targets) worm.BranchTargets = 1;

            // The length covers the shaft and the descent, then the larger of the rolled length and the way to the
            // first target, with the part of the trunk beyond the descent and the way it may lead off.
            float3 from = Descends(self) ? new float3(self.DescendTowards, self.DescendAbove - .5f).xzy : self.Aim;
            int descent = Descends(self) ? (int)math.ceil(TargetStepsPerBlock * (self.Aim.y - self.DescendAbove) / Climb(c)) + TargetSlack : 0;
            int trunk = math.max(TrunkSteps - descent, 0);
            int rolled = c.WormLength.x + (int)(RollBits(hash, RollLength) % (uint)(c.WormLength.y - c.WormLength.x + 1));
            int steer = targets > 0 ? trunk + (int)math.ceil(TargetStepsPerBlock * trunk) + StepsTo(from, worm.Target, c) : 0;
            worm.Length = shaft + descent + math.max(rolled, steer);
            worm.BranchStep = worm.Length >= 2 && Roll(hash, RollBranch) < c.WormBranchChance
                ? 1 + (int)(RollBits(hash, RollBranchStep) % (uint)(worm.Length - 1))
                : -1;
            if (worm.BranchStep >= 0 && worm.BranchTargets > 0){
                worm.BranchTarget = Arrival(cell, branchPick, 2, origin, hash, cfg, cache, anchors);
                picks.z = branchPick;
            }
            else{
                worm.BranchTargets = 0;
            }
        }

        /// <summary>
        ///     The start state of primary worm <paramref name="k" /> of <paramref name="cell" /> at anchor
        ///     <paramref name="a" />, shared by the worm and the walk of its trunk: position, heading, hash, shaft,
        ///     descent and radius, no targets.
        /// </summary>
        private static void BeginTrunk(int2 cell, int k, in WormAnchor a, in WorldGenConfig cfg, ref Worm worm){
            ref readonly CaveConfig c = ref cfg.Caves;
            uint hash = WormHash(cell, k, cfg);
            worm.Position = new float3(a.Column.x + .5f, math.max(a.Y, c.WormMinY) + .5f, a.Column.y + .5f);
            worm.Origin = worm.Position.xz;
            worm.Pitch = a.Entrance ? -c.WormMaxPitch : (Roll(hash, RollPitch) * 2f - 1f) * .5f * c.WormMaxPitch;
            worm.Yaw = Roll(hash, RollYaw) * 2f * math.PI;
            worm.Hash = hash;
            worm.ShaftAbove = a.ShaftAbove;
            worm.DescendAbove = a.DescendAbove;
            worm.DescendTowards = a.DescendTowards;
            worm.Length = int.MaxValue;
            worm.BranchStep = -1;
            SetRadius(ref worm, 0, c);
        }

        /// <summary>
        ///     <see cref="Arrival(int2,int,in WormAnchor,uint,int,float3,in WorldGenConfig)" /> on the trunk of pick
        ///     <paramref name="pick" /> of <paramref name="cell" />, whose anchor comes from the table or is computed.
        /// </summary>
        private static float3 Arrival(int2 cell, int pick, int slot, float3 origin, uint hash, in WorldGenConfig cfg,
            in BlendCache cache, in WormAnchors anchors){
            UnpackPick(pick, out int2 offset, out int j);
            int2 tcell = cell + offset;
            WormAnchor target = LookupSteered(tcell, j, cfg, cache, anchors);
            return Arrival(tcell, j, target, hash, slot, origin, cfg);
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
        ///     Estimated path length from <paramref name="from" /> to <paramref name="to" />: the horizontal distance
        ///     plus the climb at the largest pitch; float.MaxValue when the target lies beyond <see cref="TargetReach" />
        ///     or steeper than <see cref="TargetSlopeShare" /> of the largest pitch.
        /// </summary>
        private static float TargetCost(float3 from, float3 to, in CaveConfig c){
            float3 d = to - from;
            float flat = math.length(d.xz);
            if (flat > TargetReach * c.WormReach || math.abs(d.y) > TargetSlopeShare * Slope(c) * flat) return float.MaxValue;
            return flat + math.abs(d.y) / Climb(c);
        }

        /// <summary>
        ///     <see cref="TargetCost(float3,float3,in CaveConfig)" /> times the hashed factor of worm <paramref name="hash" />
        ///     for the start (<paramref name="dx" />, <paramref name="dz" />, <paramref name="j" />) of its 3 × 3 cells.
        /// </summary>
        private static float TargetCost(float3 from, float3 to, uint hash, int dx, int dz, int j, in CaveConfig c){
            float cost = TargetCost(from, to, c);
            if (cost == float.MaxValue) return cost;
            return cost * (1f + TargetSpread * Hash.Hash01((dx + 1) * 3 + dz + 1, j, hash, HashSalts.WormTarget));
        }

        /// <summary>Steps granted to walk from <paramref name="from" /> to <paramref name="to" />.</summary>
        private static int StepsTo(float3 from, float3 to, in CaveConfig c){
            float3 d = to - from;
            return (int)math.ceil(TargetStepsPerBlock * math.max(math.length(d.xz), math.abs(d.y) / Climb(c))) + TargetSlack;
        }

        /// <summary>
        ///     Most steps one slot of a cell walks with its branch: the shaft and a descent from the top of the world,
        ///     the trunk and the way from the end of that descent to the farthest target or the longest rolled length,
        ///     and a branch that walks the way to its own target or half the rest.
        /// </summary>
        public static int MaxSteps(in CaveConfig c){
            float drop = ChunkHeight / Slope(c), climb = ChunkHeight / Climb(c);
            int descent = (int)math.ceil(TargetStepsPerBlock * climb) + TargetSlack;
            int toTarget = (int)math.ceil(TargetStepsPerBlock * math.max(TargetReach * c.WormReach + drop, climb)) + TargetSlack;
            int trunk = TrunkSteps + (int)math.ceil(TargetStepsPerBlock * TrunkSteps);
            int main = c.Cover + (int)math.ceil(c.WormMaxRadius) + 1 + descent + math.max(c.WormLength.y, trunk + toTarget);
            return main + math.max(main / 2, toTarget);
        }

        /// <summary>
        ///     The branch of <paramref name="parent" />, which stands at its <see cref="Worm.BranchStep" />: it turns a
        ///     quarter to one side, takes half the remaining length (or the way to its target, when it has one) and
        ///     walks from its own hash. False when that length is 0.
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
            branch.SteerFrom = 0;
            branch.Target = parent.BranchTarget;
            branch.NextTarget = default;
            branch.TargetsLeft = parent.BranchTargets;
            branch.BranchTarget = default;
            branch.BranchTargets = 0;
            if (branch.TargetsLeft > 0) branch.Length = math.max(branch.Length, StepsTo(parent.Position, branch.Target, c));
            SetRadius(ref branch, 0, c);
            return branch.Length > 0;
        }

        /// <summary>The sphere of <paramref name="worm" /> reaches its current target: the two starts' spheres overlap.</summary>
        public static bool Reached(in Worm worm, in CaveConfig c){
            float r = worm.Radius + c.WormMinRadius;
            return math.lengthsq(worm.Target - worm.Position) <= r * r;
        }

        /// <summary>
        ///     Moves <paramref name="worm" /> one block on: the yaw and pitch rates keep <c>WormTurnDamping</c> of
        ///     their value and gain a jitter, the yaw turns <c>WormSteer</c> of the way towards the target (towards
        ///     <see cref="Worm.DescendTowards" /> while the worm is above <see cref="Worm.DescendAbove" />), the pitch
        ///     relaxes towards the target (towards down while the worm is above <see cref="Worm.DescendAbove" />,
        ///     towards level without a target) and stays within ±<c>WormMaxPitch</c>, and the worm bounces off
        ///     <c>WormMinY</c>. A target the sphere has reached gives way to the next. The targets steer the path only
        ///     after <see cref="Worm.SteerFrom" /> steps, the trunk of a primary worm. An entrance worm above
        ///     <see cref="Worm.ShaftAbove" /> steps straight down instead. False when the path ends: its length is used
        ///     up or the next step would leave <c>WormReach</c> of the start.
        /// </summary>
        public static bool Next(ref Worm worm, in CaveConfig c){
            int step = worm.Step + 1;
            if (step >= worm.Length) return false;
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
            while (worm.TargetsLeft > 0 && Reached(worm, c)){
                worm.TargetsLeft--;
                worm.Target = worm.NextTarget;
            }

            float target = 0f;
            if (worm.Position.y > worm.DescendAbove){
                target = -c.WormMaxPitch;
                Steer(ref worm, worm.DescendTowards - worm.Position.xz, c);
            }
            else if (worm.TargetsLeft > 0 && step > worm.SteerFrom){
                // The descent is over once the worm is below the band top; a target above it may lead back up.
                worm.DescendAbove = int.MaxValue;
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
        ///     within <c>WormPadding</c> of the column, with its branch. The pure reference of the chunk pass, slow.
        /// </summary>
        public static bool Covers(int x, int y, int z, in WorldGenConfig cfg, in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            int3 voxel = new(x, y, z);
            int padding = c.WormPadding;
            int2 first = Cell(new int2(x, z) - padding, cfg), last = Cell(new int2(x, z) + padding, cfg);
            WormAnchors anchors = WormAnchors.Create(first - 2, last + 2, cfg, cache, Allocator.Temp);
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
