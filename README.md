# Infinitile

A high-performance voxel engine built in Unity with procedurally generated terrain, in-game voxel editing, and GPU-driven rendering using compute shaders.

## Project Overview

This is a modern voxel engine that demonstrates scalable world generation and rendering architecture. The engine generates infinite procedural terrain with caves, ores, and structures, allows real-time voxel placement and destruction, and renders the world using a hybrid approach combining CPU-side world management with GPU-driven point rendering and global buffers.

### Key Features

- **Procedural World Generation** — Noise-based terrain generation with caves, ores, and vegetation
- **In-Game Voxel Editing** — Place and destroy voxels in real-time with responsive chunk updates
- **GPU-Driven Rendering** — Compute shader-based point mesh generation and meshless global buffer rendering
- **Optimized Job System** — Parallel chunk generation and mesh building using Unity Burst jobs
- **Scalable Architecture** — Three-layer design (World / Jobs / Render) for separation of concerns and easy extension

### Technology Stack

- **Engine**: Unity 6000+ with Universal Render Pipeline (URP)
- **Rendering**: Compute shaders, vertex pulling, global buffers, indirect rendering
- **Optimization**: Burst-compiled job system, native collections, minimal GC allocations
- **Input**: New Input System

## Architecture

The engine is organized around three core layers:

### 1. World Layer

Owns chunk data, partition state, and world events. Provides high-level APIs for voxel queries and edits without exposing job or render complexity.

**Key Classes**: `VoxelWorld`, `ChunkManager`, `ChunkPool`

### 2. Job Layer

Turns generation requests and edit operations into background work. Schedules and manages parallel jobs for chunk data generation, lighting, collider mesh building, and collider baking.

**Key Classes**: `VoxelEngineScheduler`, `ChunkScheduler`, `LightScheduler`, `CMeshBuildScheduler`, `ColliderBakeScheduler`

### 3. Render Layer

Manages GPU-side render data, global buffers, and draw submission. Converts partition data into points and indices for compute shader-driven rendering.

**Key Classes**: `VoxelWorldRenderer`, `RenderBufferManager`, `PointBuilderHandler`, `CopyPointsHandler`

## Project Structure

```
Assets/
├── Engine/                    # Core engine code (production)
│   ├── Docs/                  # Pipeline documentation (see below)
│   ├── Editor/                # Editor tooling (inspectors, world generation preview, render debugging)
│   ├── Scripts/
│   │   ├── Behaviour/         # Pooled chunk and partition GameObject components
│   │   ├── Data/              # Chunk voxel and light data, chunk accessor
│   │   ├── Jobs/              # Schedulers and job definitions
│   │   ├── Render/            # GPU rendering pipeline (Post/: underwater fog, post-processing)
│   │   ├── Settings/          # Engine settings and configuration
│   │   ├── ThirdParty/        # Third-party libraries and utilities
│   │   ├── Utils/             # Math, logging, and shared helpers
│   │   ├── VoxelConfig/       # Voxel definitions and registries
│   │   ├── World/             # VoxelWorld, chunk store (ChunkManager) and pool (ChunkPool)
│   │   └── WorldGen/          # World generator: sampler, noise, biomes, height, blending, flora
│   ├── Prefabs/               # Engine prefabs (world manager, renderer, etc.)
│   ├── Resources/             # Voxel data packages and runtime assets
│   ├── Shaders/               # Compute shaders and render shaders
│   └── Tests/                 # EditMode, PlayMode and performance tests
├── EngineSettings/            # Configurable engine settings (world size, generation params)
├── Scenes/                    # Game scenes
├── Scripts/                   # Game-specific scripts (not part of the engine)
│   ├── Player/                # Player controller and voxel editor
│   └── UI/                    # HUD and in-game UI
├── Settings/                  # Project settings
├── TextMesh Pro/              # TextMesh Pro assets and resources
└── InputSystem_Actions.inputactions  # Input bindings

ProjectSettings/              # Unity project configuration
Packages/                     # Unity package dependencies
```

**Note**: Code in `Assets/Scripts/` is game-specific and not part of the core engine. The engine itself is located entirely within `Assets/Engine/`.

## Data Flow

The engine follows a consistent pipeline for world updates:

1. **World Generation Phase**
   - Chunk Scheduler queues new or dirty chunks
   - Parallel chunk jobs generate voxel data using noise and feature passes
   - Chunk data is registered in the world

2. **Light Phase**
   - Light jobs compute the light of chunks whose 3 x 3 neighbourhood has voxel data
   - Voxel edits update the light incrementally

3. **Mesh Generation Phase**
   - Collider mesh jobs build greedy collider meshes on the CPU; bake jobs cook them for physics
   - World publishes partition build requests for the GPU

4. **Rendering Phase**
   - A compute shader builds each partition's points from the uploaded chunk voxel and light data
   - The points are copied into paged global render buffers
   - Per-camera page culling feeds indirect draw calls that pull the vertices from the buffers

This decoupled design means each layer can scale independently without tight coupling.

## Documentation

For detailed information on how the engine works, read the pipeline documentation:

1. **[System Overview](Assets/Engine/Docs/SystemOverview.md)** — High-level architecture, folder layout, and core systems
2. **[World Generation Pipeline](Assets/Engine/Docs/WorldGenerationPipeline.md)** — Procedural terrain generation stages
3. **[Chunk Pipeline](Assets/Engine/Docs/ChunkPipeline.md)** — Chunk lifecycle and edit flow
4. **[Mesh & Render Pipeline](Assets/Engine/Docs/MeshAndRenderPipeline.md)** — Mesh generation, collider baking, and GPU rendering (versions 1.0–1.2)

The world generation specification is [Docs/WorldGenSpec.html](Docs/WorldGenSpec.html): world generation (Part I),
engine rules (Part E), the engine cleanup and implemented ideas (Part II, §15–16) and the research tooling (Part III). It
lives outside `Assets` so Unity does not import it.

Each document includes visual diagrams showing data flow and component interactions.

## License

MIT License — See [LICENSE](LICENSE) file for details.

**Provided as-is with no support.** Do what you want with it, but please provide attribution.
