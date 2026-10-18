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
