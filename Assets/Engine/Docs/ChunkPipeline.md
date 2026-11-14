# Chunk Pipeline (Overview)

```mermaid
flowchart LR
    A[Tick] --> B[Data Queue]
    B --> C[Generate Chunk Data
    World Gen]
    C --> D[Chunk Data Ready]
    D --> L[Light Job
    3 x 3 data ready]
    L --> E[Request Mesh Pipeline
    3 x 3 light ready]

    X[SetVoxel Edit] --> Z[Incremental Light Update]
    Z --> Y[Remesh Requested]
    Y --> E
```

## Rings around the focus

`VoxelEngineScheduler` runs the steps round robin: data, light, mesh, collider. Each step needs the one before it
in the 3 x 3 neighbourhood of a chunk, so each step reaches one ring less than the one before:

| Step | Needs | Reaches (Chebyshev chunk distance) |
| --- | --- | --- |
| Data (`ChunkScheduler`) | nothing | `LoadDistance` = `DrawDistance + 3` |
| Light (`LightScheduler`) | 3 x 3 voxel data | `LoadDistance - 1` |
| Mesh (`CMeshBuildScheduler` + GPU build) | 3 x 3 lit chunks | `DrawDistance` |

Strictly, `DrawDistance + 2` would be enough (light up to `DrawDistance + 1`, mesh up to `DrawDistance`); the extra
ring is slack, so lit neighbours are usually ready by the time a chunk enters the draw distance.

## Scheduling

Each step has a `JobStateHandler` with a priority queue (nearest to the focus first) and one batch of jobs in
flight. The scheduler gives one step per frame a turn on the main thread, so a frame never pays for two steps:

- **A turn does everything the step can do now:** it collects the finished batch, refills the queue when it runs
  low (a scan of the step's window, at the latest every fourth turn), and dispatches the next batch. A step whose
  batch still runs has nothing to do and costs no frame.
- **Work-conserving round robin.** The scheduler starts with the step after the one that worked last and takes the
  first step that has work. With every step busy each gets every fourth frame; a step that is alone gets every
  frame. (Until 2026-11 each step got every fourth frame for one of three sub-steps, twelve frames per batch,
  whether the other steps had work or not: at draw distance 12 the mesh step took 7553 frames for a fresh world
  while data and light were done after a fifth of them.)
- **Queues stay inside their windows.** The focus update (every 0.5 s) removes the queued entries that left the
  step's window before it re-sorts the rest, so no queue holds more than its window (mesh: the draw window, 5000
  partitions at draw distance 12). Before, entries the focus had left behind stayed queued until they were
  dequeued and thrown away; in a sprint flight the mesh queue reached 13 000.
- **The mesh step waits for the renderer.** Every meshed partition also needs a GPU build
  (`MeshAndRenderPipeline.md`, version 1.8). While more than two rounds of GPU build slots wait in the renderer,
  the mesh step dispatches nothing: the partitions stay in the priority queue, which follows the focus, instead
  of piling up in the renderer's first-in-first-out list.

`PipelineThroughputTests` (PlayMode) holds the frame budget for a fresh world and the queue limits in a flight;
`PipelineMeasurementTests` (explicit) prints the timings.

## Light

- **Light job.** `LightJob` lights one chunk per index from the voxel data of its 3 x 3 neighbourhood
  over the box chunk +-14 (full height; a source 15 columns away contributes 0) and writes only that chunk's light,
  so no job writes another chunk's light.
  Sky columns are 15 without BFS; the BFS starts only at the frontier (under leaves and water, at overhangs and
  cliffs, at emitters). `ChunkManager.ApplyLight` sets `Chunk.LightReady`; `VoxelWorld.ChunkLightChanged` tells the
  renderer to upload the light.
- **Meshing waits for light.** `MeshJobStateHandler` only meshes a chunk whose 3 x 3 chunks are loaded and
  lit, because faces at chunk borders sample the neighbour's light.
- **The light job reads voxel data.** `ChunkManager.GetLightJobChunks` records the chunks the running batch
  reads. Before `SetVoxel` or `UnloadChunk` writes one of them, `ChunkManager.LightJobReading` makes the light
  scheduler complete *and apply* the batch. Applying only assigns light data, so it cannot re-enter an unload.
- **Edits.** `SetVoxel` raises `ChunkManager.VoxelEdited`; `LightScheduler` runs `LightEditJob`
  synchronously (`Run()`) on the 3 x 3 chunks around the edit (removal BFS, then re-propagation; box edit +-16).
  Partitions whose light changed, or that touch a changed voxel, are flagged for remeshing. If one of the 3 x 3
  chunks is not lit yet, the lit ones are invalidated instead and re-lit by the light job.
- **Unloading invalidates neighbours.** A lit neighbour's light may hold edits of the unloaded chunk that a reload
  regenerates away, so `UnloadChunk` clears `LightReady` of the 8 neighbours. A chunk that was lit before
  (`Chunk.WasLit`) flags its and its neighbours' partitions for remeshing when it is lit again.

## Lifetime rules

- **Eviction is by plain distance.** `ChunkManager` and `ChunkPool` reclaim the chunk or partition
  farthest from the focus. The view direction only orders work, never eviction.
- **Unloading raises the eviction event.** `ChunkManager.UnloadChunk` raises `OnChunkUnloaded`, which
  `VoxelWorld` forwards as `ChunkEvicted`, so the renderer releases the chunk's GPU buffer and render pages.
- **Chunk data is not written while a job reads it.** Before `SetVoxel` or `UnloadChunk` changes or disposes
  a chunk, `ChunkManager.AccessorChunkWriting` makes the collider mesh scheduler complete its running job. Only the
  job is completed there; the results are applied later by `CMeshBuildScheduler.Complete`, because applying them
  can claim partitions, evict chunks and re-enter the unload.
- **A collider bake only marks its own mesh version collidable.** Partition objects are pooled per chunk, so
  each `ChunkPartition` carries a `MeshVersion` that changes on every clear or rewrite of its collider mesh.
  `ColliderBakeScheduler` completes a running bake before any collider mesh is cleared or rewritten
  (`ChunkPool.PartitionMeshWriting`) and skips results whose partition was reclaimed or re-meshed meanwhile.
