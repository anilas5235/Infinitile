# Performance baselines

Benchmarks live in `Assets/Engine/Tests/Performance` (assembly `Engine.Tests.Performance`,
EditMode, Unity Performance Testing 3.2.0). Each benchmark warms up 3 times and takes
20 measurements on a single worker (`job.Run()`), so the numbers are ms per unit of work,
not throughput across all cores.

Each entry is checked against two rules:
- **Regression check:** no benchmark is more than 10 % slower than in the previous baseline.
- **Budgets** (initial targets per chunk and worker, to be confirmed on the target machine): generation 1.5 ms
  without blending (`None`) and 3 ms with the spatial blend methods; light 1 ms on natural terrain.

The tables below keep the numbers of every baseline. Only the three latest result files stay in this folder;
the older ones were removed on 2026-10-21 and are in the git history
(`git log --diff-filter=D -- Assets/Engine/Tests/Performance/Baselines`).

## 2026-10-17 — first baseline (`2026-10-17-editmode-baseline.json`)

Machine: AMD Ryzen 7 5800X (16 threads), 32 GB, Windows 11, Unity 6000.3.11f1, Mono, D3D11.
Burst enabled, Editor not in batch mode, native leak detection at its default level.

| Benchmark | Sample group | Median | Min | Max |
|---|---|---|---|---|
| ChunkJob.Run (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.07 ms | 1.03 ms | 1.22 ms |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 9.02 ms | 8.61 ms | 10.07 ms |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 8.94 ms | 8.62 ms | 10.55 ms |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 11.20 ms | 10.88 ms | 13.78 ms |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.47 ms | 0.46 ms | 0.65 ms |

At this point the engine had a single generation method and no light job.

## 2026-10-18 — GPU page culling (`2026-10-18-editmode-baseline.json`)

Same machine and settings as above. The change adds the CPU frustum reference and stores an AABB per page in
`PageAllocator`, which is the only change to code an existing benchmark touches.

| Benchmark | Sample group | Median | Min | Max | vs previous |
|---|---|---|---|---|---|
| ChunkJob.Run (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.08 ms | 1.07 ms | 1.21 ms | +1 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 6.55 ms | 6.38 ms | 6.93 ms | -27 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 6.60 ms | 6.38 ms | 6.96 ms | -26 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 10.39 ms | 10.24 ms | 10.60 ms | -7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.47 ms | 0.43 ms | 0.55 ms | +2 % |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample (managed C#, no Burst) | FrustumCulling.IsVisible 1024 pages us | 2248 us | 2216 us | 3160 us | new |

Regression check: passed (the page allocator pays about 2 % for writing the bounds per page). The collider mesh
and interval list numbers improved; this change does not touch that code, so the difference is Editor state (a
warmed-up Editor, no test-runner domain reload in between), not a code change.

The frustum reference is deliberately plain managed C# over `Unity.Mathematics` (it is the oracle the GPU
test is compared with), so its cost says what a CPU fallback would cost without Burst. Each sample covers 10 passes over the buffer (`IterationsPerMeasurement(10)`; the Performance
Testing package records the total, not the mean), so one buffer costs about 225 us, about 0.22 us per page in
Mono with safety checks on. The GPU kernel tests the same 1024 pages
in one dispatch of 8 thread groups.

## 2026-10-18 — seeded simplex (`2026-10-18-phase0-editmode-baseline.json`)

Same machine and settings as above. The change replaces the noise basis of `NoiseProfile`/`WarpedNoiseLayer` with the seeded
simplex, so `ChunkJob.Run` is the benchmark whose code changed; `GeneratorConfig` also grew the `WorldGen` mirror,
which the job carries but does not read on the legacy path.

| Benchmark | Sample group | Median | Min | Max | vs previous |
|---|---|---|---|---|---|
| ChunkJob.Run (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.10 ms | 1.09 ms | 1.14 ms | +1.5 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 6.75 ms | 6.50 ms | 7.05 ms | +3 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 6.71 ms | 6.54 ms | 6.85 ms | +2 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 10.75 ms | 10.63 ms | 11.36 ms | +3 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.47 ms | 0.46 ms | 0.55 ms | +1 % |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2273 us | 2243 us | 2876 us | +1 % |

Regression check: passed. The seeded simplex costs about the same per sample as the old basis; the 1–3 % spread
across benchmarks whose code did not change is Editor state.

Lesson for future runs: the first runs of this entry showed `ChunkJob.Run` at 30.5 ms and `CMeshBuildJob.Run`
at 167 ms (about 25x). The cause was not the new code but Burst direct-call entry points in `WorldSampler` that
returned structs and carried `bool` fields in their signatures. Burst reports such errors only in `Editor.log`
(`Burst error BC1063/BC1064`) at domain reload and then runs **every** job in the Editor managed, including jobs in
unrelated code. A 10x or larger slowdown of all Burst benchmarks while managed benchmarks stay put is that
symptom; `WorldSamplerTests.SampleColumn_CompilesWithBurst` now turns it into a test failure.

## 2026-10-18 — Terrain-first generator (`2026-10-18-phase1-editmode-baseline.json`)

Same machine and settings as above, **but not a quiet machine**: a game (about 3.6 cores) and a browser (about
1.4 cores) were running during all three attempts of this run; the table shows the best attempt. The change
routes `ChunkJob.PrepareChunkMaps` through `WorldSampler.SampleColumn` when the config selects a non-legacy
architecture and adds four fields to `ChunkColumn`; the legacy path only gained those field writes. No other
benchmarked code changed.

| Benchmark | Sample group | Median | Min | Max | vs previous |
|---|---|---|---|---|---|
| ChunkJob.Run, legacy (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.11 ms | 1.06 ms | 1.66 ms | +1 % |
| ChunkJob.Run, Terrain-first (default splines, six default biomes, 6D classification + RLE write) | ChunkJob.Run TerrainFirst ms/chunk | 2.26 ms | 2.16 ms | 3.75 ms | new |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 7.20 ms | 6.75 ms | 9.53 ms | +7 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 7.27 ms | 6.81 ms | 8.94 ms | +8 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 11.53 ms | 11.07 ms | 15.49 ms | +7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 1.04 ms | 0.59 ms | 3.19 ms | +121 % (load noise, see below) |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2454 us | 2366 us | 2656 us | +8 % |

Regression check: the one benchmark whose code changed (`ChunkJob.Run` on the legacy path) is within 1 % of the
previous run. The collider mesh, interval list and frustum numbers moved 7–8 % although their code is untouched,
and the page allocator (a 0.5 ms benchmark) doubled with a 0.59 ms minimum and a 3.19 ms maximum; both are the
foreground load, not the change. The two discarded attempts put the same untouched benchmarks 20–40 % above the
previous run with the minima near its medians. **Re-run this baseline on a quiet machine before reading
anything but the `ChunkJob.Run` rows from it.**

Terrain-first costs about 2x the legacy path per chunk: every column evaluates three nested splines, the shared
detail octaves and the 6D classification over six biomes (`WorldSampler.SampleColumn`), where the legacy path
samples two noise layers. **This is above the 1.5 ms budget** without blending; the busy machine inflates the
number, so re-measure on a quiet machine before deciding whether Terrain-first needs optimising.

## 2026-10-18 — Biome-first generator, legacy generator removed (`2026-10-18-phase2-editmode-baseline.json`)

Same machine and settings as above, quiet machine (no game or browser load). The change adds the
`BiomeFirstHeight` module and routes every chunk through `WorldSampler.SampleColumn`; the legacy generator is
gone, so the `ChunkJob.Run TerrainRle ms/chunk` row ends here. The collider mesh benchmark fixture now builds its
chunk from the world sampler (one Plains biome, surface near y = 100 instead of the legacy noise surface), so its
partition content changed and its rows are not comparable with the previous run.

| Benchmark | Sample group | Median | Min | Max | vs previous |
|---|---|---|---|---|---|
| ChunkJob.Run, Biome-first (six default biomes, 5D classification, per-biome Offset/a_i/J + RLE write) | ChunkJob.Run BiomeFirst ms/chunk | 1.51 ms | 1.42 ms | 2.52 ms | new |
| ChunkJob.Run, Terrain-first (default splines, six default biomes, 6D classification + RLE write) | ChunkJob.Run TerrainFirst ms/chunk | 2.16 ms | 2.12 ms | 2.53 ms | -4 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 3.78 ms | 3.66 ms | 4.40 ms | fixture changed |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 6.86 ms | 6.50 ms | 7.22 ms | fixture changed (-6 %) |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 10.76 ms | 10.52 ms | 14.14 ms | -7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.49 ms | 0.48 ms | 0.58 ms | -53 % (+4 % vs seeded simplex) |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2344 us | 2293 us | 3191 us | -4 % |

Regression check: no benchmark is slower than in the previous run; the untouched benchmarks sit 4–7 % below it,
which confirms that run's foreground load. The page allocator is back at its seeded-simplex level (+4 %).

Biome-first costs 1.51 ms per chunk, about 70 % of Terrain-first: the same climate fields, detail octaves and
peak noise, but the 5D classification picks the biome first and the height is Offset_b + Σ a_{b,i} N_i + J_b P
from that biome's constants, so no nested splines are evaluated. It sits at the 1.5 ms budget without blending.
Terrain-first measures 2.16 ms on the quiet machine (2.26 ms in the previous run under load), so it remains above
that budget; whether the splines need optimising stays open, since the blend methods add cost on both paths. The
surface-partition collider row halved because the new fixture's surface is flatter than the legacy noise surface,
not because the mesher changed.

## 2026-10-18 — biome blend methods (`2026-10-18-phase3-editmode-baseline.json`)

Same machine and settings as above, **not a quiet machine**: a game and a browser were running during the whole
session, and benchmarks whose code this change does not touch sit 1–14 % above the previous run (collider mesh
+8/+11 %, frustum +14 %). The change routes every column through the blend stages and the dithered surface pick
and gives `ChunkJob.PrepareChunkMaps` a padded lattice cache per chunk. The new rows run one architecture x method
configuration each (Biome-first: the method on both stages, default strengths B = 32, σ = 0.04, A = 24) over 16
fixed scattered chunks and record the mean per chunk, because the cost of the spatial methods depends on how many
columns lie near a boundary; the single-chunk rows are unchanged.

| Benchmark | Sample group | Median | Min | Max | vs previous |
|---|---|---|---|---|---|
| ChunkJob.Run, Biome-first, None (single chunk) | ChunkJob.Run BiomeFirst ms/chunk | 1.58 ms | 1.54 ms | 2.10 ms | +5 % |
| ChunkJob.Run, Terrain-first, None (single chunk) | ChunkJob.Run TerrainFirst ms/chunk | 2.42 ms | 2.33 ms | 3.12 ms | +12 % (load, see below) |
| ChunkJob.Run, Terrain-first, None (16 chunks) | ChunkJob.Run TerrainFirst None ms/chunk | 3.39 ms | 2.58 ms | 6.26 ms | new |
| ChunkJob.Run, Terrain-first, DistanceBlend (16 chunks) | ChunkJob.Run TerrainFirst DistanceBlend ms/chunk | 4.89 ms | 4.54 ms | 10.10 ms | new |
| ChunkJob.Run, Terrain-first, DistortedBoundary (16 chunks) | ChunkJob.Run TerrainFirst DistortedBoundary ms/chunk | 4.35 ms | 4.16 ms | 4.91 ms | new |
| ChunkJob.Run, Terrain-first, ParameterSpaceWeighting (16 chunks) | ChunkJob.Run TerrainFirst ParameterSpaceWeighting ms/chunk | 2.65 ms | 2.54 ms | 2.83 ms | new |
| ChunkJob.Run, Biome-first, None (16 chunks) | ChunkJob.Run BiomeFirst None ms/chunk | 1.85 ms | 1.73 ms | 6.91 ms | new |
| ChunkJob.Run, Biome-first, DistanceBlend (16 chunks) | ChunkJob.Run BiomeFirst DistanceBlend ms/chunk | 2.87 ms | 2.77 ms | 3.07 ms | new |
| ChunkJob.Run, Biome-first, DistortedBoundary (16 chunks) | ChunkJob.Run BiomeFirst DistortedBoundary ms/chunk | 3.53 ms | 3.41 ms | 4.06 ms | new |
| ChunkJob.Run, Biome-first, ParameterSpaceWeighting (16 chunks) | ChunkJob.Run BiomeFirst ParameterSpaceWeighting ms/chunk | 2.02 ms | 1.86 ms | 2.42 ms | new |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 4.07 ms | 3.77 ms | 4.88 ms | +8 % (untouched) |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 7.63 ms | 7.12 ms | 9.32 ms | +11 % (untouched) |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 11.58 ms | 11.21 ms | 15.15 ms | +8 % (untouched) |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.50 ms | 0.48 ms | 0.54 ms | +1 % (untouched) |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2675 us | 2394 us | 3559 us | +14 % (untouched) |

Regression check: because the load moved untouched benchmarks by up to 14 %, the two existing chunk rows were also
measured A/B under the same load, the previous code (`master`) and this change back to back, comparing minima (the
least load-sensitive statistic here):

| Row | Before: minima (3 runs) | After: minima (4 runs) | Difference |
|---|---|---|---|
| ChunkJob.Run BiomeFirst ms/chunk | 1.46, 1.48 ms (one run at 2.79 ms discarded) | 1.54–1.57 ms | about +5 % |
| ChunkJob.Run TerrainFirst ms/chunk | 2.20, 2.20, 2.27 ms | 2.27–2.33 ms | about +4 % |

So `None` costs about 5 % more than before, within the 10 % limit: the per-column distance vector, the blend-stage
dispatch and the (empty) per-chunk cache. `None` sets the dominant biome and the surface source to the hard class
directly instead of running argmax and the dithered pick (identical result).

Budget (3 ms with the spatial methods): Biome-first meets it with DistanceBlend (2.9 ms) and ParameterSpaceWeighting
(2.0 ms) and misses it with DistortedBoundary (3.5 ms: one warped classification per column for the material stage
plus a warped lattice for the height kernel). Terrain-first misses it with DistanceBlend (4.9 ms) and
DistortedBoundary (4.4 ms), as it already misses the 1.5 ms budget without blending: a Terrain-first lattice point
costs a full spline height, because h enters the 6D classification, and a chunk with B = 32 (stride 2) needs
32 x 32 lattice points on top of its 1024 columns, so the spatial methods roughly double the `None` cost. Options if
the budget is to hold: reuse the hard class of the columns that are lattice points (a quarter of the chunk's lattice
at stride 2), optimise the Terrain-first spline evaluation itself, or confirm a higher budget for Terrain-first. The
uniform-window early-out already skips the kernel sum away from boundaries.

## 2026-10-20 — flora (`2026-10-20-phase4-editmode-baseline.json`)

Same machine as above (Ryzen 7 5800X, RX 6900 XT, Windows 11). The suite ran three times back to back. The first run
was disturbed by load (medians of untouched benchmarks up to +30 %, minima unchanged) and is not used; the recorded
file is the second run. The third agrees with it within 3 % on every minimum, except the single-chunk Biome-first
row (a load spike in run 3).

The change re-enables `PlaceVegetation` and gives `PrepareChunkMaps` a flora grid over the chunk ± `FloraPadding`
(reach + r_tree) whose ring is sampled only for multi-block candidates; the `BlendCache` lattice is padded by the
same amount. The existing rows run without flora rules (`FloraPadding` 0, no ring), so their code path is
unchanged. The new `... flora ms/chunk` rows run the same 16 chunks per configuration with placeholder rules (a
tree like the shipped one: reach 1, r_tree 3, `FloraPadding` 4); "+x % (minima)" compares their minimum with the
minimum of the matching per-method row over runs 2 and 3.

| Benchmark | Sample group | Median | Min | Max | vs previous |
|---|---|---|---|---|---|
| ChunkJob.Run, Biome-first, None (single chunk) | ChunkJob.Run BiomeFirst ms/chunk | 1.54 ms | 1.46 ms | 1.68 ms | -3 % |
| ChunkJob.Run, Terrain-first, None (single chunk) | ChunkJob.Run TerrainFirst ms/chunk | 2.36 ms | 2.31 ms | 2.70 ms | -2 % |
| ChunkJob.Run, Terrain-first, None (16 chunks) | ChunkJob.Run TerrainFirst None ms/chunk | 2.30 ms | 2.26 ms | 2.43 ms | -32 % |
| ChunkJob.Run, Terrain-first, DistanceBlend (16 chunks) | ChunkJob.Run TerrainFirst DistanceBlend ms/chunk | 4.36 ms | 4.29 ms | 9.56 ms | -11 % |
| ChunkJob.Run, Terrain-first, DistortedBoundary (16 chunks) | ChunkJob.Run TerrainFirst DistortedBoundary ms/chunk | 3.99 ms | 3.94 ms | 4.29 ms | -8 % |
| ChunkJob.Run, Terrain-first, ParameterSpaceWeighting (16 chunks) | ChunkJob.Run TerrainFirst ParameterSpaceWeighting ms/chunk | 2.43 ms | 2.38 ms | 3.43 ms | -8 % |
| ChunkJob.Run, Biome-first, None (16 chunks) | ChunkJob.Run BiomeFirst None ms/chunk | 1.77 ms | 1.53 ms | 2.36 ms | -4 % |
| ChunkJob.Run, Biome-first, DistanceBlend (16 chunks) | ChunkJob.Run BiomeFirst DistanceBlend ms/chunk | 2.59 ms | 2.55 ms | 3.31 ms | -10 % |
| ChunkJob.Run, Biome-first, DistortedBoundary (16 chunks) | ChunkJob.Run BiomeFirst DistortedBoundary ms/chunk | 3.24 ms | 3.20 ms | 4.71 ms | -8 % |
| ChunkJob.Run, Biome-first, ParameterSpaceWeighting (16 chunks) | ChunkJob.Run BiomeFirst ParameterSpaceWeighting ms/chunk | 1.77 ms | 1.74 ms | 2.01 ms | -12 % |
| ChunkJob.Run, Terrain-first, None + flora (16 chunks) | ChunkJob.Run TerrainFirst None flora ms/chunk | 2.38 ms | 2.35 ms | 2.77 ms | new; +4 % (minima) |
| ChunkJob.Run, Terrain-first, DistanceBlend + flora (16 chunks) | ChunkJob.Run TerrainFirst DistanceBlend flora ms/chunk | 4.89 ms | 4.85 ms | 5.18 ms | new; +13 % (minima) |
| ChunkJob.Run, Terrain-first, DistortedBoundary + flora (16 chunks) | ChunkJob.Run TerrainFirst DistortedBoundary flora ms/chunk | 4.17 ms | 4.11 ms | 9.90 ms | new; +4 % (minima) |
| ChunkJob.Run, Terrain-first, ParameterSpaceWeighting + flora (16 chunks) | ChunkJob.Run TerrainFirst ParameterSpaceWeighting flora ms/chunk | 2.50 ms | 2.48 ms | 2.81 ms | new; +4 % (minima) |
| ChunkJob.Run, Biome-first, None + flora (16 chunks) | ChunkJob.Run BiomeFirst None flora ms/chunk | 1.62 ms | 1.60 ms | 1.68 ms | new; +4 % (minima) |
| ChunkJob.Run, Biome-first, DistanceBlend + flora (16 chunks) | ChunkJob.Run BiomeFirst DistanceBlend flora ms/chunk | 2.87 ms | 2.83 ms | 3.16 ms | new; +11 % (minima) |
| ChunkJob.Run, Biome-first, DistortedBoundary + flora (16 chunks) | ChunkJob.Run BiomeFirst DistortedBoundary flora ms/chunk | 3.55 ms | 3.51 ms | 3.71 ms | new; +9 % (minima) |
| ChunkJob.Run, Biome-first, ParameterSpaceWeighting + flora (16 chunks) | ChunkJob.Run BiomeFirst ParameterSpaceWeighting flora ms/chunk | 1.88 ms | 1.84 ms | 2.10 ms | new; +6 % (minima) |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 7.21 ms | 6.81 ms | 8.31 ms | -6 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 4.01 ms | 3.86 ms | 4.22 ms | -2 % |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2478 us | 2399 us | 3028 us | -7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.50 ms | 0.47 ms | 0.59 ms | -0 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 11.06 ms | 10.89 ms | 14.11 ms | -4 % |

Regression check: no existing row is slower than in the previous run (all minima −12 % … +3 %; the −32 % of the
Terrain-first None median only shows that the previous median was load-inflated, its minimum moved −12 %).

Flora cost: +4…6 % with None and ParameterSpaceWeighting, +4…9 % with DistortedBoundary and +11…13 % with
DistanceBlend. The DistanceBlend share is most likely the padded `BlendCache`: with B = 32 the lattice around a
32 x 32 chunk grows by 4 columns on every side, and every lattice point is a full classification (a full spline
height in Terrain-first). Options if this is to come down: evaluate the ring columns directly instead of padding the lattice
(they are few after the multi-block pre-filter), or pad only on the sides where candidates exist.

**Accepted on 2026-10-20:** the +11…13 % flora cost with DistanceBlend stays as it is.

Budget: Biome-first DistanceBlend stays within 3 ms with flora (2.87 ms); the configurations that already missed
the budget in the previous run still miss it.

## 2026-10-20 — preview tooling (`2026-10-20-phase5-editmode-baseline.json`)

Same machine and settings as above. The preview tooling and in-game regeneration change no code that a benchmark
runs. The only engine runtime changes are the world-reset path (`VoxelWorld.Regenerate`, `ChunkPool.ReclaimAll`,
`ChunkManager.UnloadAll`) and the shutdown wait in `DataImporter`. Neither is on a per-chunk path.

| Benchmark | Sample group | Median | Min | Max | vs previous (median / min) |
|---|---|---|---|---|---|
| ChunkJob.Run, Terrain-first, None (single chunk) | ChunkJob.Run TerrainFirst ms/chunk | 2.14 ms | 2.10 ms | 2.26 ms | -10 % / -9 % |
| ChunkJob.Run, Biome-first, None (single chunk) | ChunkJob.Run BiomeFirst ms/chunk | 1.41 ms | 1.39 ms | 1.53 ms | -8 % / -4 % |
| ChunkJob.Run, Terrain-first, None | ChunkJob.Run TerrainFirst None ms/chunk | 2.14 ms | 2.12 ms | 2.19 ms | -7 % / -7 % |
| ChunkJob.Run, Terrain-first, DistanceBlend | ChunkJob.Run TerrainFirst DistanceBlend ms/chunk | 4.05 ms | 3.97 ms | 4.17 ms | -7 % / -7 % |
| ChunkJob.Run, Terrain-first, ParameterSpaceWeighting | ChunkJob.Run TerrainFirst ParameterSpaceWeighting ms/chunk | 2.24 ms | 2.20 ms | 2.42 ms | -8 % / -8 % |
| ChunkJob.Run, Terrain-first, DistortedBoundary | ChunkJob.Run TerrainFirst DistortedBoundary ms/chunk | 3.74 ms | 3.68 ms | 3.96 ms | -6 % / -7 % |
| ChunkJob.Run, Biome-first, None | ChunkJob.Run BiomeFirst None ms/chunk | 1.45 ms | 1.43 ms | 1.49 ms | -18 % / -6 % |
| ChunkJob.Run, Biome-first, DistanceBlend | ChunkJob.Run BiomeFirst DistanceBlend ms/chunk | 2.40 ms | 2.36 ms | 2.47 ms | -7 % / -8 % |
| ChunkJob.Run, Biome-first, ParameterSpaceWeighting | ChunkJob.Run BiomeFirst ParameterSpaceWeighting ms/chunk | 1.62 ms | 1.59 ms | 1.65 ms | -9 % / -9 % |
| ChunkJob.Run, Biome-first, DistortedBoundary | ChunkJob.Run BiomeFirst DistortedBoundary ms/chunk | 3.01 ms | 2.93 ms | 3.11 ms | -7 % / -8 % |
| ChunkJob.Run, Terrain-first, None + flora | ChunkJob.Run TerrainFirst None flora ms/chunk | 2.22 ms | 2.20 ms | 2.71 ms | -7 % / -6 % |
| ChunkJob.Run, Terrain-first, DistanceBlend + flora | ChunkJob.Run TerrainFirst DistanceBlend flora ms/chunk | 4.53 ms | 4.48 ms | 4.58 ms | -7 % / -8 % |
| ChunkJob.Run, Terrain-first, ParameterSpaceWeighting + flora | ChunkJob.Run TerrainFirst ParameterSpaceWeighting flora ms/chunk | 2.32 ms | 2.29 ms | 2.40 ms | -7 % / -8 % |
| ChunkJob.Run, Terrain-first, DistortedBoundary + flora | ChunkJob.Run TerrainFirst DistortedBoundary flora ms/chunk | 3.87 ms | 3.82 ms | 4.02 ms | -7 % / -7 % |
| ChunkJob.Run, Biome-first, None + flora | ChunkJob.Run BiomeFirst None flora ms/chunk | 1.49 ms | 1.46 ms | 1.55 ms | -8 % / -9 % |
| ChunkJob.Run, Biome-first, DistanceBlend + flora | ChunkJob.Run BiomeFirst DistanceBlend flora ms/chunk | 2.65 ms | 2.59 ms | 2.90 ms | -8 % / -9 % |
| ChunkJob.Run, Biome-first, ParameterSpaceWeighting + flora | ChunkJob.Run BiomeFirst ParameterSpaceWeighting flora ms/chunk | 1.72 ms | 1.69 ms | 1.75 ms | -8 % / -8 % |
| ChunkJob.Run, Biome-first, DistortedBoundary + flora | ChunkJob.Run BiomeFirst DistortedBoundary flora ms/chunk | 3.33 ms | 3.27 ms | 3.68 ms | -6 % / -7 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 6.65 ms | 6.44 ms | 9.31 ms | -8 % / -5 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 3.72 ms | 3.60 ms | 5.60 ms | -7 % / -7 % |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2268 us | 2233 us | 2579 us | -8 % / -7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.42 ms | 0.41 ms | 0.53 ms | -16 % / -12 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 10.55 ms | 10.27 ms | 11.07 ms | -5 % / -6 % |

Regression check: every row is faster than in the previous run (minima −4 % … −12 %). The code is unchanged, so the
uniform shift is the machine state (a quieter machine), not a speed-up.

Budget re-measured on this run, the quietest so far:
- Without flora: Terrain-first DistanceBlend 4.05 ms, Terrain-first DistortedBoundary 3.74 ms and Biome-first
  DistortedBoundary 3.01 ms still miss the 3 ms target.
- With flora: 4.53 ms, 3.87 ms and 3.33 ms.
- Every other configuration is within the budget.
- Candidate optimisations are in the blend-methods notes above: reuse the column hard classes for lattice points
  inside the chunk, and a faster Terrain-first spline evaluation.

Neither is a simple change:
- With B = 32 the lattice has stride 2 and covers the chunk ± (reach 15 + `FloraPadding`), about 37 x 37 ≈ 1370
  points. Only 16 x 16 = 256 of them (≈ 19 %) coincide with chunk columns, so reuse would save about 0.35 ms
  (Terrain-first DistanceBlend ≈ 3.7 ms), still over budget. It would also need `SampleCore` split into two
  passes while keeping the cached and pure samplers bit-identical.
- DistortedBoundary has no lattice to share: every column classifies at its own warped position.

**Accepted on 2026-10-20 (for now):** the three configurations stay over the 3 ms target.

## 2026-10-21 — lighting (`2026-10-21-phaseL-editmode-baseline.json`)

Same machine and settings as above. Lighting adds the light job benchmarks. No code that an existing benchmark runs
was changed; the renderer changes (light buffers, `PointBuilder.compute`) are on the GPU path.

New rows (`LightJobBenchmark`, `LightJob.Run` on a single worker, ms per lit chunk). The JSON holds the re-run
with the light box reduced from chunk ± 15 to ± 14 (see below); the first column pair is that run, the last the
± 15 run it replaced:

| Benchmark | Sample group | Median | Min | Max | ± 15 median / min |
|---|---|---|---|---|---|
| Natural terrain: Terrain-first, None + flora, 8 scattered chunks with their 3 x 3 rings | LightJob.Run natural terrain ms/chunk | 0.82 ms | 0.78 ms | 1.00 ms | 0.86 / 0.81 ms (−5 % / −3 %) |
| Worst case: air cavity y 20…235 with a Lava emitter every 8 voxels, leaf roof (box filled by BFS on all 4 channels) | LightJob.Run worst case ms/chunk | 15.27 ms | 14.94 ms | 16.42 ms | 15.73 / 15.57 ms (−3 % / −4 %) |

The light budget (1 ms/chunk on natural terrain) is met. The first run of the benchmark measured 3.69 ms (worst
case 25.9 ms). A stage breakdown showed that the time went into the bulk loops, not the BFS (about 11 k dequeues
per chunk on natural terrain):
- `FillVoxels`: 1.75 ms;
- sky fill in `SeedSky`: 1.15 ms;
- BFS: 0.3 ms;
- `WriteChunk`: 0.2 ms.

Burst keeps safety checks on in the Editor, and the checked `NativeArray` indexers kept those loops from vectorising.
`LightRegion` now uses `MemSet` and raw pointers there. `FillVoxels` walks the interval nodes across consecutive
columns instead of a binary search per column. The initial-light BFS uses a ring buffer (`LightQueue`) instead
of `NativeQueue`.

The light box is the chunk ± 14 (60 x 60 x 256 voxels plus the sentinel ring), so each chunk does about 3.5 times
its own column count of work. That is the price of lighting every chunk independently and in parallel.
The margin was 15 in the first runs: light at horizontal distance d is at most 15 − d, so a source 15 columns away
contributes 0 and the ± 14 box is exact (the 3 x 3 vs 5 x 5 border test still passes). The re-run with ± 14 moved
every other row by −2 % … +4 % (median), within the noise of the earlier re-runs.

Regression check, existing rows vs the previous run (median / min):
- Every `ChunkJob`, `CMeshBuildJob`, `FrustumCulling` and `UnsafeIntervalList` row is between −3 % and +1 %.
- `PageAllocator fill+release` measured 0.46 ms (+11 % / +7 %); two re-runs gave 0.47 / 0.46 ms.
  - `PageAllocator` is unchanged.
  - 0.46–0.47 ms is the 2026-10-17 value (0.47 ms); the previous run (0.42 ms) was the low outlier.

## 2026-10-21 — four-channel light BFS (`2026-10-21-s20.2-editmode-baseline.json`)

Same machine and settings as above. `LightRegion.Propagate` combines the four channels of a BFS step at once. The 16-bit light value is spread to one channel per byte lane; a saturating
subtract and a lane-wise max replace the loop over channels in `Combine`, which stays as the scalar reference
(`LightPropagationTests.Lanes`). The candidate for a clear neighbour is computed once per dequeued voxel.

| Sample group | Median | Min | Max | Previous median / min | Change (median / min) |
|---|---|---|---|---|---|
| LightJob.Run natural terrain ms/chunk | 0.73 ms | 0.68 ms | 0.98 ms | 0.82 / 0.78 ms | −11 % / −13 % |
| LightJob.Run worst case ms/chunk | 9.02 ms | 8.71 ms | 11.87 ms | 15.27 / 14.94 ms | −41 % / −42 % |

The target for this change (worst case ≤ 10 ms/chunk) is met.

Tried and not kept:
- **Queued bit:** a spare attenuation bit marks queued voxels, so a voxel is not queued
  again before it is processed. Both rows measured the same with and without it (natural 0.76 / 0.75 ms,
  worst 8.9 / 9.0 ms). In the worst case every emitter has the same light and the FIFO visits voxels in distance
  order, so a voxel is raised only once anyway. Without a measurable gain it was not worth the extra state in `Att`
  (which `LightEditJob` reads).
- **Branch-free relax** (always store, always write the queue slot, advance the count only on a change):
  worst case 17.3 ms, much slower than the branch it replaces.

Regression check, existing rows vs the previous run (median / min). No code they run was changed; three full runs:
- Recorded run: every row between −3 % and +5 % (median) and −4 % and +3 % (min); the highest is
  `PageAllocator fill+release` (+5 % / +3 %).
- The other two runs: between −3 % and +8 % (median) and −2 % and +3 % (min); the +8 % is
  `BiomeFirst DistortedBoundary` (min +2 %), +1 % in the recorded run.
- Each run had one `ChunkJob` row with a median spike and an almost flat min (+43 % / +5 %, +22 % / −1 %,
  +48 % / +1 %), each time a different row: a background hiccup during that row, not counted above. The JSON is
  the run with the smallest spike (`TerrainFirst None flora`, 2.71 / 2.16 ms vs 2.23 / 2.19 ms).

## 2026-10-21 — collider mask (`2026-10-21-s20.3-editmode-baseline.json`)

Same machine and settings as above. `CMeshBuildJob` fills a 34 × 34 × 34 byte collision mask (partition plus border)
once per partition by walking the interval runs of each column, and builds the face masks from it; a partition
whose interior has no collision skips the neighbours and the meshing. `CVertex` drops its normal (8 instead of
16 bytes). New row: an all-air partition (y = 7).

| Sample group | Median | Min | Max | Previous median / min | Change (median / min) |
|---|---|---|---|---|---|
| CMeshBuildJob.Run solid ms/partition | 0.32 ms | 0.32 ms | 0.37 ms | 6.49 / 6.23 ms | −95 % / −95 % |
| CMeshBuildJob.Run surface ms/partition | 0.50 ms | 0.49 ms | 0.70 ms | 3.73 / 3.66 ms | −87 % / −87 % |
| CMeshBuildJob.Run air ms/partition | 0.018 ms | 0.017 ms | 0.025 ms | new (old mesher 0.75 / 0.43 ms) | |

`ChunkJob`: the run-count pass added on 2026-10-21 (sizing the chunk lists, PR #39) read all 262,144 voxels a
second time and cost 0.15–0.25 ms per chunk (+5 % to +18 %, never measured then). `WriteToChunkData` now writes the
runs into a Temp list and copies them into a list of their exact size, in one pass over the voxels. Back to back
in the same session (two rounds each), this is 3 % to 12 % faster than the counting pass. A branch-free linear
counting pass was tried and was slower than the original (+5 % to +14 %).

Regression check, existing rows vs the previous run (median / min): every row between −6 % and +5 % (median) and
−6 % and +6 % (min). `TerrainFirst None flora` is −16 % / +4 % because the previous JSON holds that row's median
spike.

## Re-running

With the project open in the Editor (the CLI `unity test` refuses to run while the Editor has the
project open):

```
unity command run_tests --mode EditMode --filter Engine.Tests.Performance --timeout 600 --result-only
```

With the Editor closed:

```
unity test --mode EditMode --filter Engine.Tests.Performance --output perf-results.xml
```

The package writes the full results (samples, hardware, package versions) to
`%USERPROFILE%\AppData\LocalLow\DefaultCompany\voxel-game\PerformanceTestResults.json`; copy that file
here with a date prefix to record a new baseline.
