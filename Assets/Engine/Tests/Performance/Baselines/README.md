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

Not yet benchmarked because the code does not exist yet: light job per chunk (Phase LGT) and the
CPU frustum reference (Phase C). "Per architecture x method" currently has one entry because the
engine has a single generation method.

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
