# Performance baselines (PERF-02)

Benchmarks live in `Assets/Engine/Tests/Performance` (assembly `Engine.Tests.Performance`,
EditMode, Unity Performance Testing 3.2.0). Each benchmark warms up 3 times and takes
20 measurements on a single worker (`job.Run()`), so the numbers are ms per unit of work,
not throughput across all cores.

## 2026-10-17 baseline (`2026-10-17-editmode-baseline.json`)

Machine: AMD Ryzen 7 5800X (16 threads), 32 GB, Windows 11, Unity 6000.3.11f1, Mono, D3D11.
Burst enabled, Editor not in batch mode, native leak detection at its default level.

| Benchmark | Sample group | Median | Min | Max |
|---|---|---|---|---|
| ChunkJob.Run (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.07 ms | 1.03 ms | 1.22 ms |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 9.02 ms | 8.61 ms | 10.07 ms |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 8.94 ms | 8.62 ms | 10.55 ms |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 11.20 ms | 10.88 ms | 13.78 ms |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.47 ms | 0.46 ms | 0.65 ms |

Not yet benchmarked because the code does not exist yet: light job per chunk (Phase LGT). "Per
architecture x method" currently has one entry because the engine has a single generation method.

## 2026-10-18 baseline (`2026-10-18-editmode-baseline.json`, Phase C)

Same machine and settings as above. Phase C adds the CPU frustum reference and stores an AABB per page in
`PageAllocator` (CUL-01), which is the only change to code an existing benchmark touches.

| Benchmark | Sample group | Median | Min | Max | vs 2026-10-17 |
|---|---|---|---|---|---|
| ChunkJob.Run (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.08 ms | 1.07 ms | 1.21 ms | +1 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 6.55 ms | 6.38 ms | 6.93 ms | -27 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 6.60 ms | 6.38 ms | 6.96 ms | -26 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 10.39 ms | 10.24 ms | 10.60 ms | -7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.47 ms | 0.43 ms | 0.55 ms | +2 % |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample (managed C#, no Burst) | FrustumCulling.IsVisible 1024 pages us | 2248 us | 2216 us | 3160 us | new |

PERF-03: no benchmark is more than 10 % slower than the 2026-10-17 baseline (the page allocator pays
about 2 % for writing the bounds per page). The collider mesh and interval list numbers improved; nothing in
Phase C touches that code, so the difference is Editor state (a warmed-up Editor, no test-runner domain reload
in between), not a code change.

The frustum reference is deliberately plain managed C# over `Unity.Mathematics` (it is the oracle the GPU
test is compared with, PERF-02 "CPU frustum test reference"), so its cost says what a CPU fallback would cost
without Burst. Each sample covers 10 passes over the buffer (`IterationsPerMeasurement(10)`; the Performance
Testing package records the total, not the mean), so one buffer costs about 225 us, about 0.22 us per page in
Mono with safety checks on. The GPU kernel tests the same 1024 pages
in one dispatch of 8 thread groups.

## 2026-10-18 Phase 0 baseline (`2026-10-18-phase0-editmode-baseline.json`)

Same machine and settings as above. Phase 0 (WorldGenSpec foundations) replaces the noise basis of
`NoiseProfile`/`WarpedNoiseLayer` with the seeded simplex (GEN-05, GEN-15), so `ChunkJob.Run` is the benchmark
whose code changed; `GeneratorConfig` also grew the `WorldGen` mirror, which the job carries but does not read on
the legacy path.

| Benchmark | Sample group | Median | Min | Max | vs 2026-10-18 (Phase C) |
|---|---|---|---|---|---|
| ChunkJob.Run (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.10 ms | 1.09 ms | 1.14 ms | +1.5 % |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 6.75 ms | 6.50 ms | 7.05 ms | +3 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 6.71 ms | 6.54 ms | 6.85 ms | +2 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 10.75 ms | 10.63 ms | 11.36 ms | +3 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 0.47 ms | 0.46 ms | 0.55 ms | +1 % |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2273 us | 2243 us | 2876 us | +1 % |

PERF-03: no benchmark is more than 10 % slower than the previous baseline. The seeded simplex costs about the same
per sample as the old basis; the 1–3 % spread across benchmarks whose code did not change is Editor state.

Lesson recorded for future runs: the first Phase 0 runs showed `ChunkJob.Run` at 30.5 ms and `CMeshBuildJob.Run`
at 167 ms (about 25x). The cause was not the new code but Burst direct-call entry points in `WorldSampler` that
returned structs and carried `bool` fields in their signatures. Burst reports such errors only in `Editor.log`
(`Burst error BC1063/BC1064`) at domain reload and then runs **every** job in the Editor managed, including jobs in
unrelated code. A 10x or larger slowdown of all Burst benchmarks while managed benchmarks stay put is that
symptom; `WorldSamplerTests.SampleColumn_CompilesWithBurst` now turns it into a test failure.

## 2026-10-18 Phase 1 baseline (`2026-10-18-phase1-editmode-baseline.json`)

Same machine and settings as above, **but not a quiet machine**: a game (about 3.6 cores) and a browser (about
1.4 cores) were running during all three attempts of this run; the table shows the best attempt. Phase 1
(Terrain-first) routes `ChunkJob.PrepareChunkMaps` through `WorldSampler.SampleColumn` when the config selects a
non-legacy architecture (INT-10) and adds four fields to `ChunkColumn`; the legacy path only gained those field
writes. No other benchmarked code changed.

| Benchmark | Sample group | Median | Min | Max | vs 2026-10-18 (Phase 0) |
|---|---|---|---|---|---|
| ChunkJob.Run, legacy (noise terrain + RLE write) | ChunkJob.Run TerrainRle ms/chunk | 1.11 ms | 1.06 ms | 1.66 ms | +1 % |
| ChunkJob.Run, Terrain-first (section 19.1 splines, six section 19.2 biomes, 6D classification + RLE write) | ChunkJob.Run TerrainFirst ms/chunk | 2.26 ms | 2.16 ms | 3.75 ms | new |
| CMeshBuildJob.Run, surface partition (y=3) | CMeshBuildJob.Run surface ms/partition | 7.20 ms | 6.75 ms | 9.53 ms | +7 % |
| CMeshBuildJob.Run, fully solid partition (y=1) | CMeshBuildJob.Run solid ms/partition | 7.27 ms | 6.81 ms | 8.94 ms | +8 % |
| UnsafeIntervalList.Set, 10^4 random edits, chunk-sized list | UnsafeIntervalList.Set 10k edits ms | 11.53 ms | 11.07 ms | 15.49 ms | +7 % |
| PageAllocator, fill one buffer with 3-page partitions and release all | PageAllocator fill+release 1 buffer ms | 1.04 ms | 0.59 ms | 3.19 ms | +121 % (load noise, see below) |
| FrustumCulling.IsVisible, 1024 page AABBs vs 6 planes, 10 passes per sample | FrustumCulling.IsVisible 1024 pages us | 2454 us | 2366 us | 2656 us | +8 % |

PERF-03: the one benchmark whose code changed in Phase 1 (`ChunkJob.Run` on the legacy path) is within 1 % of
Phase 0. The collider mesh, interval list and frustum numbers moved 7–8 % although their code is untouched, and
the page allocator (a 0.5 ms benchmark) doubled with a 0.59 ms minimum and a 3.19 ms maximum; both are the
foreground load, not Phase 1. The two discarded attempts put the same untouched benchmarks 20–40 % above
Phase 0 with the minima near the Phase 0 medians. **Re-run this baseline on a quiet machine before reading
anything but the `ChunkJob.Run` rows from it.**

Terrain-first costs about 2x the legacy path per chunk: every column evaluates three nested splines, the shared
detail octaves and the 6D classification over six biomes (`WorldSampler.SampleColumn`), where the legacy path
samples two noise layers. **This is above the PERF-05 initial budget** of 1.5 ms per chunk and worker for method 1
(a SHOULD, to be confirmed against the user's machine); the busy machine inflates the number, so re-measure on a
quiet machine before deciding whether Terrain-first needs optimising. Biome-first (Phase 2) adds a row to this
table, the blend methods (Phase 3) one per method.

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
