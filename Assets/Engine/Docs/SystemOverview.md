# Engine System Overview

This document is a quick orientation guide for future AI agents working on the voxel engine.
It summarizes the project structure, the main runtime systems, and the recommended order for reading and editing code.

## Read this first

If you need to understand the full runtime flow, read these docs in order:

1. [World Generation Pipeline](./WorldGenerationPipeline.md)
2. [Chunk Pipeline](./ChunkPipeline.md)
3. [Mesh & Render Pipeline](./MeshAndRenderPipeline.md)

These documents describe the major execution pipelines in more detail.
This file is the high-level map of the codebase.

## High-level architecture

The project is organized around a voxel world that is split into chunks and partitions.
The runtime flow is roughly:

1. World and scheduler state are initialized.
2. Chunk data is generated or updated.
3. Mesh and collider jobs are queued and processed.
4. Render buffers are filled with GPU-friendly point data.
5. The renderer draws the world through compute-shader-driven global buffers.

The important idea is that the systems are decoupled:
- `VoxelWorld` owns world state and events.
- `VoxelEngineScheduler` coordinates work between data, mesh, and collider jobs.
- `VoxelWorldRenderer` owns GPU-side render data and draw submission.
- `VoxelDataImporter` loads voxel definitions and builds registries used by both world generation and rendering.

## Main folders and responsibilities

### `World`
Core world state, chunk management, partition ownership, and world events.

Typical entry points:
- `VoxelWorld`
- `ChunkManager`
- `ChunkPool`

### `Jobs`
Async and scheduled work for generation, meshing, and collider baking.

Important subareas:
- `Jobs/Core` — scheduler state machines and orchestration
- `Jobs/Chunk` — chunk data generation jobs
- `Jobs/Meshing` — partition mesh generation
- `Jobs/ColliderBake` — collider baking jobs

### `Render`
GPU-side rendering pipeline, render buffers, and partition-to-buffer copying.

Important entry points:
- `VoxelWorldRenderer`
- `RenderBufferManager`
- `RenderBuffer`
- `PointBuilderHandler`
- `CopyPointsHandler`

### `VoxelConfig`
Voxel definition assets, registries, shapes, and editor tooling for authoring voxel content.

Important entry points:
- `VoxelDataImporter`
- `VoxelRegistry`
- `VoxelDefinition`
- `VoxelShape`
- `QuadDefinition`

### `Data`
Project data containers, settings assets, and imported configuration used by runtime systems.

### `Settings`
Global engine and renderer settings used by scheduling, rendering, and pipeline configuration.

### `Utils`
Shared constants, math helpers, singleton/provider helpers, logging, and collections.

### `Components`
Unity-facing runtime components that connect the world, scheduler, and renderer.

### `Noise`
Procedural generation utilities used by chunk data jobs and world generation.

## Core systems and what they do

### `VoxelWorld`
The central runtime world object.
It exposes events such as chunk changes, chunk data readiness, partition eviction, and partition build requests.
Most systems listen to these events instead of polling the world directly.

### `VoxelEngineScheduler`
Coordinates the major job pipelines in a round-robin cycle:
- data generation
- mesh building
- collider baking

It also tracks queue counts and average timings for diagnostics.

### `ChunkScheduler`
Dispatches chunk data generation jobs.
It is used by the data job state handler.

### `MeshBuildScheduler`
Dispatches partition mesh generation jobs.
It is used by the mesh job state handler.

### `ColliderBakeScheduler`
Dispatches collider baking jobs.
It is used by the collider job state handler.

### `VoxelDataImporter`
Loads voxel data packages from `Resources/VoxelDataPackages`, registers voxel definitions, builds texture and quad registries, and exposes the resulting runtime render data.
This is the main setup point for voxel content.

### `VoxelRegistry`
Stores voxel render definitions, voxel IDs, texture arrays, quad arrays, and GPU buffers.
If you need voxel-to-render mapping or ID lookup, this is usually the place.

### `VoxelWorldRenderer`
Owns the meshless GPU rendering path.
It manages render buffers for solid, transparent, and foliage data, copies point data into global buffers, and issues indirect draws.

### `RenderBufferManager`
Allocates and tracks global render buffers per material group.
It is responsible for page allocation, release, and buffer rebuilds.

### `PointBuilderHandler`
Builds point data for a partition from voxel data using compute shaders.
It prepares neighboring chunks, runs the point builder shader, and reads back point counts.

### `CopyPointsHandler`
Copies built point data into the target render buffers and prepares page metadata for shader consumption.

## Important runtime entry points

If you are changing behavior, these are the places to inspect first:

- World startup and initialization:
  - `VoxelWorld`
  - `VoxelDataImporter`
  - `VoxelEngineScheduler`

- Chunk generation flow:
  - `ChunkScheduler`
  - `DataJobStateHandler`
  - `ChunkManager`

- Mesh generation flow:
  - `MeshBuildScheduler`
  - `MeshJobStateHandler`
  - `PartitionBuildRequest`

- Collider flow:
  - `ColliderBakeScheduler`
  - `ColliderJobStateHandler`

- Rendering flow:
  - `VoxelWorldRenderer`
  - `PointBuilderHandler`
  - `CopyPointsHandler`
  - `RenderBufferManager`
  - `RenderBuffer`

- Voxel authoring / content setup:
  - `VoxelDefinition`
  - `VoxelShape`
  - `QuadDefinition`
  - `VoxelRegistry`

## Agent working rules

### Prefer the existing pipelines
Before changing code, read the relevant pipeline docs and follow the current architecture.
Do not introduce a new flow if an existing one already handles the task.

### Keep responsibilities separated
- World state should stay in world and component classes.
- Scheduling logic should stay in `Jobs/Core`.
- GPU render data should stay in `Render`.
- Content authoring data should stay in `VoxelConfig`.

### Respect event-driven flow
A lot of the codebase is driven by events such as chunk changes, chunk readiness, and partition requests.
When adding new behavior, hook into the existing events instead of polling where possible.

### Preserve GPU and job lifetimes
Many systems own native arrays, graphics buffers, or compute-shader resources.
When editing these systems, check disposal paths carefully.

### Prefer minimal changes
The project already has a layered architecture.
When fixing or extending behavior, make the smallest change that fits the existing design.

## Quick mental model

Think of the project as three connected layers:

1. **World layer** — owns chunk data and requests work.
2. **Job layer** — turns requests into generated data, meshes, and collider results.
3. **Render layer** — turns partition output into GPU buffers and draws the final world.

If you keep that separation in mind, most changes become easier to place correctly.

## Useful files to open next

- `Assets/Engine/Scripts/World/VoxelWorld.cs`
- `Assets/Engine/Scripts/Render/VoxelWorldRenderer.cs`
- `Assets/Engine/Scripts/Jobs/Core/VoxelEngineScheduler.cs`
- `Assets/Engine/Scripts/VoxelConfig/Data/VoxelDataImporter.cs`
- `Assets/Engine/Scripts/VoxelConfig/Data/VoxelRegistry.cs`
- `Assets/Engine/Scripts/Settings/VoxelEngineSettings.cs`

## Notes for future agents

- Existing docs are intentionally pipeline-focused; keep new documentation consistent with that style.
- When adding new systems, document both the data flow and the ownership/lifetime of native resources.
- If a task touches rendering, check both the render pipeline docs and the `Render` folder code.
- If a task touches world generation or chunk lifecycle, check the scheduler and pipeline docs before editing.

