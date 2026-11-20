using System;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Classify{
    /// <summary>
    ///     The smoothed height the Terrain-first classification reads instead of the column's own H (DEC-121), so a
    ///     biome follows the broad shape of the land and not every hill, ridge and peak.
    ///     <para>
    ///         Raw nodes on a world-aligned lattice every <see cref="NodeStride" /> blocks hold the macro height
    ///         R = limit(Offset + Amp · Σ_{i &lt; K'} g_i N_i): the spline height with only the first
    ///         <c>ClassHeightOctaves</c> detail octaves and without the peak noise. A smoothed node is the separable
    ///         Gaussian S(n) = Σ_j w_j Σ_i w_i R(n + (i, j)) with the normalised weights
    ///         <see cref="WorldGenConfig.ClassHeightWeights" />, summed in this order (i, then j, both ascending). A
    ///         position reads the bilinear blend of its four surrounding smoothed nodes.
    ///     </para>
    ///     Every value is a pure function of the position: <see cref="ClassHeightGrid" /> only caches smoothed nodes, and
    ///     the pure path builds the same small grid around one position, so cached and pure reads agree bit for bit.
    /// </summary>
    [BurstCompile]
    public static class ClassHeight{
        /// <summary>Lattice spacing of the raw and smoothed nodes in blocks.</summary>
        public const int NodeStride = 32;

        /// <summary>Largest kernel radius in nodes; <see cref="WorldGenConfig.ClassHeightWeights" /> holds 2r + 1 weights.</summary>
        public const int MaxRadius = 7;

        /// <summary>
        ///     Normalised 1D Gaussian weights for standard deviation <paramref name="sigma" /> (blocks), cut at 1.5 σ
        ///     (rounded up to whole nodes); empty when <paramref name="sigma" /> is 0 (smoothing off).
        /// </summary>
        public static FixedList64Bytes<float> Weights(float sigma){
            FixedList64Bytes<float> weights = new();
            if (!(sigma > 0f)) return weights;
            int r = math.min(MaxRadius, (int)math.ceil(1.5f * sigma / NodeStride));
            float sum = 0f;
            for (int k = -r; k <= r; k++){
                float x = k * NodeStride / sigma;
                float w = math.exp(-0.5f * x * x);
                weights.Add(w);
                sum += w;
            }

            for (int k = 0; k < weights.Length; k++) weights[k] /= sum;
            return weights;
        }

        /// <summary>Macro height R at raw node <paramref name="node" /> (world position node · <see cref="NodeStride" />).</summary>
        public static float RawNode(int2 node, in WorldGenConfig cfg){
            float2 p = node * NodeStride;
            WorldFields f = ClimateFields.Sample(p, cfg.Climate);
            float3 terrain = TerrainFirstHeight.SampleParams(f, cfg);
            int octaves = math.min(cfg.ClassHeightOctaves, cfg.OctaveCount);
            float detail = 0f;
            for (int i = 0; i < octaves; i++){
                float g = cfg.GlobalOctaveWeights[i];
                if (g != 0f) detail += g * DetailOctaves.Sample(i, p, cfg.OctaveWavelengths[i], cfg.Seed);
            }

            return HeightLimit.Apply(terrain.x + terrain.y * detail, cfg);
        }

        /// <summary>
        ///     Fills <see cref="ClassHeightGrid.Smoothed" /> of an allocated grid: raw nodes over [lo − r, hi + r]², the
        ///     row sums over x ∈ [lo, hi], z ∈ [lo − r, hi + r], then the column sums. Direct-call entry point, so a
        ///     managed <c>BlendCache.Build</c> gets the jobs' bits.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void Fill(ref ClassHeightGrid grid, in WorldGenConfig cfg){
            int r = cfg.ClassHeightWeights.Length / 2;
            int2 lo = grid.Origin;
            int2 size = grid.Size;
            int2 rawSize = size + 2 * r;
            NativeArray<float> raw = new(rawSize.x * rawSize.y, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int j = 0; j < rawSize.y; j++)
            for (int i = 0; i < rawSize.x; i++)
                raw[j * rawSize.x + i] = RawNode(lo - r + new int2(i, j), cfg);

            NativeArray<float> rows = new(size.x * rawSize.y, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int j = 0; j < rawSize.y; j++)
            for (int i = 0; i < size.x; i++){
                float sum = 0f;
                for (int k = 0; k <= 2 * r; k++) sum += cfg.ClassHeightWeights[k] * raw[j * rawSize.x + i + k];
                rows[j * size.x + i] = sum;
            }

            for (int j = 0; j < size.y; j++)
            for (int i = 0; i < size.x; i++){
                float sum = 0f;
                for (int k = 0; k <= 2 * r; k++) sum += cfg.ClassHeightWeights[k] * rows[(j + k) * size.x + i];
                grid.Smoothed[j * size.x + i] = sum;
            }

            raw.Dispose();
            rows.Dispose();
        }

        /// <summary>
        ///     Smoothed height at world position <paramref name="p" /> from <paramref name="grid" /> when it covers the
        ///     four nodes, else from a grid built for this position alone (the pure path, same bits).
        /// </summary>
        public static float Sample(float2 p, in WorldGenConfig cfg, in ClassHeightGrid grid){
            int2 n0 = (int2)math.floor(p / NodeStride);
            if (grid.Covers(n0)) return grid.Interpolate(p, n0);
            int2 column = (int2)math.floor(p);
            ClassHeightGrid own = ClassHeightGrid.Build(column, column, cfg, Allocator.Temp);
            float h = own.Interpolate(p, n0);
            own.Dispose();
            return h;
        }
    }

    /// <summary>
    ///     Cache of smoothed <see cref="ClassHeight" /> nodes over a block of positions: built once per chunk or region
    ///     tile, read by every classification in it. A default instance caches nothing.
    /// </summary>
    public struct ClassHeightGrid : IDisposable{
        /// <summary>Node index of element (0, 0).</summary>
        public int2 Origin;

        /// <summary>Nodes along x and z.</summary>
        public int2 Size;

        /// <summary>Smoothed node values, element [j × Size.x + i].</summary>
        public NativeArray<float> Smoothed;

        public bool IsCreated => Smoothed.IsCreated;

        /// <summary>
        ///     Smoothed nodes for every position in [<paramref name="minPosition" />, <paramref name="maxPosition" />]
        ///     (world blocks, inclusive): the nodes around each position for the bilinear read.
        /// </summary>
        public static ClassHeightGrid Build(int2 minPosition, int2 maxPosition, in WorldGenConfig cfg, Allocator allocator){
            int2 lo = FloorDiv(minPosition, ClassHeight.NodeStride);
            int2 hi = FloorDiv(maxPosition, ClassHeight.NodeStride) + 1;
            ClassHeightGrid grid = new(){ Origin = lo, Size = hi - lo + 1 };

            grid.Smoothed = new NativeArray<float>(grid.Size.x * grid.Size.y, allocator, NativeArrayOptions.UninitializedMemory);
            ClassHeight.Fill(ref grid, cfg);
            return grid;
        }

        /// <summary>True when the four nodes n0 … n0 + (1, 1) are stored.</summary>
        public bool Covers(int2 n0){
            if (!Smoothed.IsCreated) return false;
            int2 local = n0 - Origin;
            return math.all(local >= 0) && math.all(local + 1 < Size);
        }

        /// <summary>True when every position in [p - reach, p + reach] reads covered nodes.</summary>
        public bool CoversWindow(int2 p, int reach){
            if (!Smoothed.IsCreated) return false;
            int2 lo = FloorDiv(p - reach, ClassHeight.NodeStride) - Origin;
            int2 hi = FloorDiv(p + reach, ClassHeight.NodeStride) + 1 - Origin;
            return math.all(lo >= 0) && math.all(hi < Size);
        }

        /// <summary>Bilinear blend of the four smoothed nodes around <paramref name="p" /> (n0 = floor(p / stride), covered).</summary>
        public float Interpolate(float2 p, int2 n0){
            float2 t = p / ClassHeight.NodeStride - n0;
            int2 local = n0 - Origin;
            int at = local.y * Size.x + local.x;
            float s00 = Smoothed[at], s10 = Smoothed[at + 1];
            float s01 = Smoothed[at + Size.x], s11 = Smoothed[at + Size.x + 1];
            return math.lerp(math.lerp(s00, s10, t.x), math.lerp(s01, s11, t.x), t.y);
        }

        public void Dispose(){
            if (Smoothed.IsCreated) Smoothed.Dispose();
        }

        private static int2 FloorDiv(int2 value, int divisor){
            return new int2(FloorDiv(value.x, divisor), FloorDiv(value.y, divisor));
        }

        private static int FloorDiv(int value, int divisor){
            int q = value / divisor;
            return q * divisor > value ? q - 1 : q;
        }
    }
}
