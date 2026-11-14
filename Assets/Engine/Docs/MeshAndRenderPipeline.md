# Mesh & Render Pipeline (Overview)

# Version 1.0 (Chunks)

## Mesh Pipeline

```mermaid
flowchart LR
    D[Request Chunk] --> E[Mesh Queue]
    E --> F[Build Chunk Render 
    & Collider Mesh
CPU Job]
F --> G[Render Mesh Applied]
G --> H[Bake Collider Queue]
H --> I[Bake Collider
CPU Job]
I --> J[Collider Applied]
```

### Description

In version 1.0, the mesh pipeline is built around whole chunks.
Each chunk generates its own render mesh and collider mesh on the CPU, and the pipeline applies them in sequence.

Advantages:

- Very simple data flow and easy to reason about.
- Low draw call count because each chunk uses a single render mesh.

Trade-offs:

- Any small change can force a full chunk rebuild.
- Updates become slower and less responsive as the world grows.

## Render Pipeline (Texture Array, ShaderGraph)

```mermaid
flowchart LR
subgraph CPU["CPU (one-time setup)"]
    direction LR
    A[Voxel Registry 
    Data] --> B[Build Texture2D
    Array]
    B --> C[Bind Array +
    Material Properties]
end

subgraph GPU
    direction LR
    T[Vertex] --> U[Extract Texture
    Index & Sample
    Texture Array]
    U --> V[Apply AO
    ...]
    V --> W[Output Color]
end
```

### Description

In version 1.0, rendering uses classic mesh rendering per chunk with a Texture2D Array bound to a ShaderGraph material.
Each voxel face stores texture indices, and the shader samples the correct layer from the texture array.

Advantages:

- Simple authoring workflow in ShaderGraph.
- Good visual flexibility without changing mesh topology.

Trade-offs:

- Rendering is still tied to fully built meshes.

# Version 1.1 (Partitions)
## Mesh Pipeline
```mermaid
flowchart LR
    D[Request Partition] --> E[Mesh Queue]
    E --> F[Build Partition Render 
    & Collider Mesh
CPU Job]
F --> G[Render Mesh Applied]
G --> H[Bake Collider Queue]
H --> I[Bake Collider
CPU Job]
I --> J[Collider Applied]
```

### Description

In version 1.1, the pipeline switches from chunks to partitions.
This solves the main v1.0 problem: small edits no longer force a full chunk rebuild, because only the affected
partitions need new mesh data.

Advantages:

- Faster and more responsive updates after edits.
- Less CPU work per change because only local partitions are rebuilt.

Trade-offs:

- Each partition adds render overhead and more draw calls.
- Many visible partitions can quickly become expensive, especially with multiple materials.

## Render Pipeline (Vertex Pulling, Point Mesh)

```mermaid
flowchart LR
subgraph CPU["CPU (one-time setup)"]
    direction LR
    A[Voxel Registry 
    Data] --> B[Build Texture2D
    Array]
    B --> C[Bind Array +
    Material Properties]

    A --> D[Build Quad
    Buffer]
    D --> E[Bind Buffer +
    Material Properties]
end

subgraph GPU
    direction LR
    L[Point] --> M[Extract Quad
    Index & Sample
    Quad Buffer]
    M --> O[Expand to Quads
    Geometry Shader]
    O --> U[Extract Texture
    Index & Sample
    Texture Array]
    U --> V[Apply AO
    & Lighting
    ...]
    V --> W[Output Color]
end
```

### Description

In version 1.1, rendering shifts toward vertex pulling, where shaders fetch packed vertex data from buffers instead of
relying only on classic mesh attributes.
The geometry stage expands primitives for rendering and keeps CPU-side mesh binding lighter.

Advantages:

- Lower CPU mesh management overhead compared to fully traditional mesh binding.
- Smaller Meshes
- Better control over data layout in GPU buffers.

Trade-offs:

- Geometry stage overhead can limit scalability.
- Geometry Stage is not supported on all platforms.
- Draw call pressure remains high with many visible partitions. (Fixed by Unity's SRP batcher)

# Version 1.2 (Compute Shaders)
## Mesh Pipeline
```mermaid
flowchart LR
subgraph CPU["CPU"]
    direction LR
    D[Request Partition] --> E[Mesh Queue]
    E --> F[Build Collider Mesh 
    CPU Job]
    F --> H[Bake Collider Queue]
    H --> I[Bake Collider
    CPU Job]
    I --> J[Collider Applied]

    K[Reserve Global Buffer Space]
    B[Compute Shader Queue]
end

subgraph GPU
    direction LR
    E --> B
    B --> C[Build Vertex Buffer]
    C --> K
    K --> L[Copy Vertex to Global
    & ReBuild Indices]
end   
```

### Description

In version 1.2, compute shaders take over the render-data generation path.
This solves the main v1.1 problem: the partition approach still caused too many draw calls and too much CPU-side mesh
overhead, so the pipeline moves the heavy work to the GPU and global buffers.

Advantages:

- Much lower CPU cost for generating render data.
- Better scalability when many partitions are visible at once.
- Fewer draw calls thanks to global buffers and indirect-style rendering.

Trade-offs:

- More complex shader, buffer, and synchronization logic.
- Debugging becomes harder than with a classic mesh pipeline.

## Render Pipeline (Meshless Draw, Global Buffers, Index Vertex Pulling)

```mermaid
flowchart LR
subgraph CPU["CPU"]
    direction LR
    
    A[Voxel Registry 
    Data] --> B[Build Texture2D
    Array]
    B --> C[Bind Array +
    Material Properties]

    A --> D[Build Quad
    Buffer]
    D --> E[Bind Buffer +
    Material Properties]

    A --> F[Build Shape
    Buffer]
    F --> G[Bind Buffer +
    Material Properties]

    H[VoxelWorldRenderer] --> I[Prepares & administers
    Global Buffers]
    I --> J[Issue Indirect Draw
    - every frame]
end

subgraph GPU
    direction LR
    K[IndexID
    6 per Point
    -Vertex Pulling] --> L[Get Point 
    from Buffer over
    IndexBuffer]
    L --> M[Extract Quad
    Index & Sample
    Quad Buffer]
    M --> O[Pick correct Vertex
    from Quad Data]
    O --> U[Extract Texture
    Index & Sample
    Texture Array]
    U --> V[Apply AO
    & Lighting
    ...]
    V --> W[Output Color]
end
```

### Description

In version 1.2, rendering becomes fully meshless and GPU-driven.
Instead of building and binding traditional meshes per partition, compute shaders process voxel data directly and build
compact vertex/index buffers in global GPU memory.
The render pipeline then issues a single indirect draw call, with shaders performing vertex pulling to fetch and expand
quad data for rasterization.

Advantages:

- Strong reduction of CPU render setup work—no mesh binding overhead per partition.
- Much better scaling for large numbers of visible partitions through global buffer management.
- Lower draw call overhead through batched indirect rendering instead of per-partition draws.
- Unified GPU-driven path simplifies CPU-side rendering logic.
- No Geometry Shader needed.

Trade-offs:

- Higher implementation complexity (compute pipeline, buffer synchronization, indirect args management).
- Harder debugging and profiling compared to traditional mesh-based rendering.
- Requires support for compute shaders and indirect rendering on target platforms.

# Version 1.3 (GPU Page Culling)

## Render Pipeline (Per-Camera Frustum Culling of Pages)

```mermaid
flowchart LR
subgraph CPU["CPU (VoxelWorldRenderer, per camera that renders)"]
    direction LR
    A[Partition allocated
    in RenderBuffer] --> B[PageAllocator stores
    count + AABB per page]
    B --> C[RebuildBuffers uploads
    _CountsPerPage + _PageBounds
    when dirty]
    D[beginCameraRendering] --> E[GpuCuller.BeginCamera
    frustum planes per camera
    kept while frozen]
    E --> F[Per RenderBuffer:
    Cull + DrawProceduralIndirect
    main list, ShadowCastingMode.Off]
    F --> G[Shadows on: second Cull with
    extruded AABB + ShadowsOnly draw]
end

subgraph GPU
    direction LR
    H[CullPages kernel
    1 thread per page] --> I[Skip pages with
    0 points]
    I --> J[AABB vs 6 planes
    optional light extrusion
    or _CullingEnabled = 0]
    J --> K[Append page index
    to _VisiblePages]
    K --> L[CopyCount into
    args 4]
    L --> M[WriteArgs kernel
    vertex count = pages x 256 x 6]
    M --> N[Vertex shader pulls
    page = _VisiblePages slot / 256
    degenerate triangle past count]
end
```

### Description

Version 1.3 replaces the old `Culling.compute` / `ReBuildBuffers.compute` pair (one index per point, rebuilt on
every page change) with per-page frustum culling that runs once per camera and frame.

- **Page bounds.** `PageAllocator` keeps, next to the point count, the world-space AABB of the partition
  that owns each page (`PageBounds`, 24 bytes). `RenderBuffer.RebuildBuffers` uploads both arrays only when a page
  was allocated or released.
- **Cull kernel.** `PageCulling.compute` tests every page with points against the six camera planes
  (positive-vertex test, conservative at edges) and appends the surviving page indices to an append buffer. The
  counter is copied into slot 4 of the indirect args and a one-thread kernel writes the vertex count, so the CPU
  never reads anything back for the draw.
- **Vertex pulling.** The vertex shader maps `vertexID / 6` to a slot, the slot to a page through
  `_VisiblePages`, and emits a degenerate triangle for slots past the page's point count. Visible pages are drawn
  densely, empty slots cost nothing on the raster side.
- **Per-camera state.** `GpuCuller` owns a `CameraCullState` per camera (planes plus a `CullList` with
  append buffer, args and property block per render buffer). States are created on first sight and released when
  the camera is destroyed or has not rendered for 300 frames.
- **Shadows.** With `RendererSettings.shadows` not `Off` and a main light, every buffer is culled a second
  time with the AABB extruded along the light direction by the shadow distance and drawn `ShadowsOnly`; the main
  pass stops casting but still receives shadows. The mechanics are in place; the voxel shaders have no
  `ShadowCaster` pass yet, so this path does not produce shadows until one is added.
- **Debugging.** `RendererSettings.gpuCulling` (default on) and `VoxelWorldRenderer.GpuCulling` fall back
  to "every page with points is visible". `FreezeCulling` keeps the game camera's last unfrozen frustum and culls
  *every* camera with it, so the Scene view (or a detached game camera) shows exactly the pages the game camera
  kept; the frozen frustum is drawn as a yellow gizmo. `ShowCullingOverlay` shows visible / allocated pages per
  layer and buffer (read back asynchronously with one frame of latency). All three are Inspector check-boxes on
  `VoxelWorldRenderer`; in the game `CullingDebugKeys` maps them to F7 (overlay), F8 (culling), F9 (freeze).
  The Input System only sees the keyboard while the Game view has focus, so the Editor-only
  `CullingSceneViewKeys` handles F7/F8/F9 with the Scene view focused too and shows the switches there. Without
  freeze the Scene view camera culls with its own frustum, so nothing ever looks missing from it. To watch
  culling in the Editor: enter Play, let the Game view render once, press F9 (or tick *Freeze Culling*), then
  orbit the Scene view around the yellow frustum; F9 again picks up the current game frustum.
- **Tests.** `FrustumCulling` is the C# reference of the GPU test (EditMode `FrustumCullingTests`); the
  PlayMode `GpuCullingTests` read the GPU set back for 50 random camera poses and compare it page by page, check
  the shadow list, the fallback, the camera life-cycle, freeze, the allocation-free draw path and log culling
  on/off frame times at a fixed viewpoint. `FrustumCullingBenchmark` records the CPU reference cost.

Advantages:

- Draw work scales with what the camera sees instead of with everything loaded.
- No per-page-change rebuild of an index buffer; the per-frame cost is one small dispatch per buffer and camera.
- Scene, preview and reflection cameras each get a correct visible set without CPU bookkeeping.

Trade-offs:

- Page bounds are partition bounds, so a page is as coarse as its partition; sparse partitions are drawn whole.
- Each camera holds its own append buffer and args per render buffer (a few KB each).
- The conservative plane test keeps boxes that straddle a frustum corner; Hi-Z occlusion culling is future work.

# Version 1.4 (Lighting)

## Light Pipeline (Propagated Voxel Light, Smooth Corners, Day/Night)

```mermaid
flowchart LR
subgraph CPU["CPU (LightScheduler, Burst)"]
    direction LR
    A[Chunk + 8 neighbours
    have voxel data] --> B[LightJob per chunk
    box chunk ± 14
    sky scan + frontier + BFS]
    B --> C[ChunkLightData
    interval list, sky/R/G/B]
    C --> D[LightReady: mesh once
    3 x 3 chunks are lit]
    E[SetVoxel] --> F[LightEditJob.Run
    two-queue update
    edit ± 16]
    F --> C
    C --> G[ChunkLightChanged
    upload light buffer]
end

subgraph GPU
    direction LR
    G --> H[BuildPrep: id
    BuildPrepLight: id or light << 16
    Version 1.6: BuildPrepBox, one pass]
    H --> I[BuildPoints: per face corner
    average of front, sides, diagonal
    skipping opaque]
    I --> J[PointData.packed.y = sky or R << 16
    packed.w = G or B << 16]
    J --> K[Fragment: interpolate corners,
    curve, sky x brightness x tint
    vs block RGB, ambient, x AO]
end
```

### Description

Version 1.4 adds propagated voxel light. Light is computed on the CPU once per chunk and on edits. On the GPU it is
only unpacked and interpolated, so day and night never re-propagate anything.

- **Data.** Every voxel has 16 bits of light: one 4-bit level each for sky, red, green and blue
  (`VoxelLight`, channel c at bits 4c, sky is c = 3). `ChunkLightData` stores them as an interval list in the same
  Flatten order as the voxel data. Each voxel definition has `lightAttenuation` (0 clear … 15 opaque, the default)
  and `lightEmission` (RGB). `VoxelRegistry` turns them into a `VoxelLightTable` for the jobs, and the attenuation
  also goes into `GPUVoxelDef` (80 bytes).
- **Initial light.** `LightJob` lights one chunk from the voxel data of its 3 × 3
  neighbourhood. It runs over a box of chunk ± 14 columns at full height (`LightRegion`), surrounded by an opaque
  sentinel ring. Light loses at least one level per step, so a source 15 columns away contributes 0: the result is
  exact and every chunk can be lit independently and in parallel.
  - Sky without BFS: every voxel above a column's highest non-clear voxel is 15.
  - BFS seeds:
    - the first non-clear voxel under the sky part;
    - sky voxels next to a taller neighbour column that has non-opaque voxels at that height;
    - emitters.
  - BFS: each channel of a neighbour becomes max(current, source − 1 − attenuation). Sky 15 moves down into a clear
    voxel without loss.
- **Scheduling.** `LightScheduler` batches chunks whose 3 × 3 neighbourhood has voxel data. The load
  distance is the draw distance + 3, so the outermost drawn chunks can be lit. A partition is meshed only when its
  chunk and the 8 neighbours are lit. Unloading a chunk invalidates the lit neighbours that read it.
- **Edits.** `SetVoxel` runs `LightEditJob` synchronously, after any job that reads those chunks has
  completed. Per channel it uses the two-queue method in a box of edit ± 16:
  - a removal BFS clears the light that may have come through the edited voxel;
  - light is then re-propagated from the border, the edited voxel's neighbours and the cleared voxels' own sources.

  Partitions whose light changed are remeshed. The result equals a full recomputation (tested over 500 random
  edits).
- **GPU light.** The renderer uploads each chunk's light intervals into the shared interval store, like the
  voxel data (two lists per chunk, see Version 1.5). `PointBuilder.compute` decompresses the voxel ids (`BuildPrep`), then
  ORs the light into the upper 16 bits of the same uint (`BuildPrepLight`). A neighbour lookup therefore fetches
  id and light at once (Version 1.6: `BuildPrepBox` writes both in one pass, see below). A build without a light
  list for every chunk is invalid.
- **Smooth light.** For each face corner, `BuildPoints` averages every channel over the front voxel,
  the two side neighbours and the diagonal, with rounding.
  - It skips opaque voxels (attenuation 15), and the diagonal too when both sides are opaque, so light never leaks
    through a closed corner.
  - If every voxel is opaque, the corner gets the front voxel's light.
  - Inner and foliage quads use the voxel's own light; for an opaque voxel, the per-channel max of its six
    neighbours.
  - Above the world counts as air with sky 15.
  - The 16 corner nibbles go into `PointData.packed.y` (sky | R << 16) and `.w` (G | B << 16), each channel as the
    four corners up-left, up-right, down-right, down-left (UV space, like AO). The point size is unchanged.
  - `PointLightPacking` is the C# port of the accessors and the corner average, used by the round-trip tests.
- **Shading.** The solid and transparent shaders (foliage uses the transparent one) compute the light per
  fragment. They interpolate each channel's corner levels bilinearly and apply the curve 0.85^(15 − level).

  ```
  light = max(curve(sky) × _VoxelSkyBrightness × _VoxelSkyTint, curve(RGB))
  light = max(light, _VoxelAmbient) × AO      (× glow in the transparent shader)
  ```

  `DayNightCycle` (on the main directional light in `SampleScene`) drives the three globals and the light's
  rotation from `timeOfDay` and `speed`. The default speed is a 20-minute day. Without the component, the
  renderer sets full daylight (`VoxelLightingGlobals.SetDefaults`).
- **Tests and benchmarks.**
  - EditMode `LightPropagationTests`: emitter falloff, walls, media, channels, sky columns, frontier vs
    exhaustive seeding, 3 × 3 vs 5 × 5 border exactness, 500 random edits vs a full recomputation.
  - `PointLightPackingTests` and `DayNightCycleTests`.
  - `LightJobBenchmark`: 0.73 ms per chunk on natural terrain, 9.0 ms in the emitter-cavity worst case, with the
    BFS computing all four channels at once in byte lanes (see `Tests/Performance/Baselines/README.md`).

Advantages:

- Coloured block light and sky light with soft corners at no extra point size.
- Day/night is three shader globals; nothing is recomputed when the time changes.
- Chunk light is exact across borders and can be computed in parallel; edits stay local and synchronous.

Trade-offs:

- Each light job processes about 3.5 times its chunk's column count (the ± 14 margin) and uses about 3 MB of
  temporary memory.
- A chunk can be meshed only after its neighbours are lit, so the load distance is three chunks beyond the draw
  distance.
- Light changes need a remesh of the affected partitions, because the corner values are baked into the points.

# Version 1.5 (Shared Interval Store)

## Chunk Data on the GPU (One Paged Buffer Instead of One Buffer per Chunk)

```mermaid
flowchart LR
subgraph CPU["CPU (VoxelWorldRenderer)"]
    direction LR
    A[ChunkDataChanged /
    ChunkLightChanged] --> B[ChunkIntervalStore.Upload
    resize the list's pages]
    B --> C[IntervalPages
    free pages, page lists]
    D[Partition build] --> E[Page tables of the
    3 x 3 chunks' 18 lists]
end

subgraph GPU
    direction LR
    B --> F[Interval store
    one uint2 buffer,
    pages of 512]
    E --> G[BuildPrep / BuildPrepLight
    Version 1.6: BuildPrepBox
    interval k = page k >> 9]
    F --> G
end
```

### Description

Until version 1.4, the renderer kept one `GraphicsBuffer` per chunk list (voxel ids and light), taken from a pool
created at start-up. A list longer than the pool's default length (8 intervals per column) created a new buffer at
runtime, and with it a managed allocation. Every pooled buffer was as large as that default, whatever its chunk
needed.

- **Store.** `ChunkIntervalStore` holds every list in one structured `uint2` buffer of 512-element pages (4 KB).
  A list is the header (interval count, last end) followed by its (value, exclusive end) nodes, as before.
- **Pages.** `IntervalPages` hands out the pages; a list's pages need not be adjacent.
  - An update keeps the pages the list still needs and adds or frees pages only at its end.
  - New pages are handed out lowest first, so a fresh list usually gets one run of pages and one `SetData`.
  - The renderer keeps an `IntervalRange` (slot and length) per chunk list.
- **Kernels.** Per partition build, `PointBuilderHandler` writes the page tables of the 18 lists back to back into a
  small buffer. `BuildPrep` and `BuildPrepLight` read interval k at
  `store[_CompPages[_CompPagesOffset + (k >> 9)] * 512 + (k & 511)]` (Version 1.6: `BuildPrepBox`, through a
  header of table offsets).
- **Growth.** If the free pages do not suffice, the store allocates a larger buffer, copies the old one on the GPU
  (`CopyIntervals`) and logs it. It starts with 17 pages per list, two lists per chunk the `ChunkManager` can hold, the
  same memory as the former pool.
- **Measured** (PlayMode allocation flight test in the game scene): 6935 of 17986 pages used at the peak (39 %). No growth, 0/300
  allocating frames. Upload about 71 µs per chunk list as before. The prep dispatch takes about 22 µs per build
  instead of 20 µs (page-table upload).

Advantages:

- No managed object per chunk upload, whatever the list length.
- Memory follows the actual list lengths; a long list just takes more pages.
- Free pages are shared by all chunks; there is no fragmentation by buffer size.

Trade-offs:

- One more indirection per interval read in the prep kernels, and a page-table upload per build.
- Growth copies the whole store on the GPU and allocates once.

# Version 1.6 (Partition Box Prep)

## Decompressing Only What BuildPoints Reads

```mermaid
flowchart LR
subgraph CPU["CPU (PointBuilderHandler.PrepareBox)"]
    direction LR
    A[Partition build] --> B[Header: start of each
    list's page table
    + the 18 page tables]
end

subgraph GPU
    direction LR
    B --> C[BuildPrepBox
    one thread per box column:
    binary search, then walk the runs]
    D[Interval store] --> C
    C --> E[34 x 34 x 34 box
    id or light << 16]
    E --> F[BuildPoints]
end
```

### Description

Until version 1.5, every partition build decompressed all 9 chunks of the 3 × 3 neighbourhood into 9 buffers of
32 × 256 × 32 uints: `BuildPrep` wrote the ids, `BuildPrepLight` then read and rewrote every uint to add the light.
`BuildPoints` reads only the partition and a 1-voxel border of it (a face corner or AO lookup is at most one voxel
away along each axis).

- **Box.** `BuildPrepBox` writes just that border box, 34 × 34 × 34 uints (`BOX_SIZE`), id and light in one pass.
  Box position = partition-local position + 1, index `(bx * 34 + bz) * 34 + by`; `_BoxOriginY` is the world y of
  box layer 0 (the partition's base − 1).
- **Kernel.** One thread per box column (34 × 34). The column's chunk follows from its x and z: the partition spans
  its chunk in x and z, so the border columns lie in the neighbours. Per list (ids, light) the thread finds the
  interval of its first voxel by a binary search, then walks the runs up the column. Layers below the world read
  as air, layers above it as air with sky 15.
- **Page tables.** `_CompPages` starts with 18 entries, the start of each list's page table in the same buffer
  (voxel lists, then light lists, in `PartitionBuildRequest` order), followed by the tables.
- **Lookup.** `get_voxel_safe` maps a position to the box; anything outside reads as air (never reached).
- **Memory.** One 154 KB buffer per handler instead of 9 × 1 MiB.
- **Measured** (EditMode probe, 3 runs; GPU times are 200 back-to-back builds until the GPU is done):
  - issuing the prep on the CPU: 10.1–10.6 µs → 2.9–3.4 µs;
  - prep on the GPU: about 3.86 ms → 23–26 µs per build;
  - whole build until its counts are read back: 4.23 ms → 0.42 ms.
- **Tests.** `PointBuildTests` hashes the points of three partitions (world bottom, surface with every voxel type,
  world top), recorded with the old kernels; they pass unchanged. `IntervalStoreTests` checks every box voxel of
  all 8 partitions of a chunk read through scattered pages and after a growth step.

# Version 1.7 (Rotated voxels)

## Turned Variants as Voxel Ids of Their Own

```mermaid
flowchart LR
subgraph Registry["VoxelRegistry.FinalizeRegistry"]
    direction LR
    A[Base voxels
    ids unchanged] --> B[RegisterVariants
    per base: facing index,
    then top/bottom]
    B --> C[Variant VoxelDef
    base values,
    turned quads]
    B --> D[VoxelVariantTable
    BaseOf / OrientationOf / VariantOf]
end
C --> E[GPUVoxelDef buffer
+ quad buffer]
D --> F[Jobs, flora bake,
placement]
```

### Description

A voxel asset can turn: `facing` is None, Axis (3 orientations, like a log), Horizontal (4 fronts) or Full (6
fronts), and `topBottom` adds an upper half (a slab). Each turned orientation is a voxel id of its own, so the chunk
data, the interval lists, the light and the point builder stay one `ushort` per voxel and need no extra bits.

- **Ids.** `RegisterVariants` runs after all base voxels are registered and appends the variants: base order, then
  facing index, then top/bottom. Base ids never change. Variant ids depend on the package, so nothing stores them:
  the flora bake, the placement and the tests resolve them through the `VoxelVariantTable` (Burst-friendly native
  arrays: `BaseOf`, `OrientationOf`, `VariantOf`). Data that is ever persisted stores the base id plus the
  orientation. An orientation the base does not have gives the base id.
- **Names.** A variant's name is its base name plus a suffix (for example `std:LogOak#x`, `#-z`, `#u` for an upper half);
  if that does not fit into 32 bytes or is taken, it falls back to `#<base>.<orientation>` and logs a warning.
- **Orientation byte.** Bits 0–2 are the facing index (axis Y 0, X 1, Z 2; fronts +Z 0, +X 1, −Z 2, −X 3, +Y 4,
  −Y 5), bit 3 is the upper half (`VoxelOrientation`). `Rotation` gives the integer rotation of the authored shape:
  the upper half is 180° about Z, applied before the facing.
- **Geometry.** A variant copies its base's `VoxelDef` (mesh layer, collision, light, glow, surface alpha, fog) and
  gets the base quads turned about the voxel centre: `QuadRegistry.RegisterRotated` adds each (quad, rotation) pair
  once, after all authored quads, and the identity returns the quad itself. Positions, normal, up and right turn,
  the UVs stay with their corners, so the texture, the AO basis and the corner light turn with the quad. Each quad
  moves to the draw condition its turned normal points to (`VoxelOrientation.Remap`), so the face culling works on
  the turned shape.
- **Face culling.** A neighbour hid a face whenever it was in the same or a lower mesh layer. That is wrong for a
  slab, which covers only half a face. `GPUVoxelDef` (now 88 bytes) carries `HidesNeighbourFaces`, set for full
  cubes only; a partial shape (slab, plant) leaves its neighbours' faces drawn. Turning a full cube keeps it a full
  cube, so every variant has its base's flag.
- **Placement.** `VoxelPlacement.Resolve` picks the variant for a click like Minecraft: Axis follows the clicked
  face, a front faces the player, top/bottom takes the clicked half. A variant in the hotbar is placed as its base
  would be; picking a voxel returns its base.
- **Generation.** The flora bake resolves the lying logs once (`TreeVariantDef.TrunkVoxelX/Z`); see
  `WorldGenerationPipeline.md`, version 1.10.

Trade-offs:

- The registry, the voxel def buffer and the quad buffer grow by the variants; nothing per chunk grows.
- Adding or removing a rotatable voxel shifts every later variant id, which is why ids are never stored.

# Version 1.8 (Paced Partition Builds)

## The Mesh Step Follows the GPU Build Slots

```mermaid
flowchart LR
    Q[Mesh queue
    nearest first,
    inside the draw window] -->|batch, only while
    pending <= 2 x slots| J[CMeshBuildJob
    collider mesh]
    J --> P[Renderer pending list
    first in, first out]
    P --> S[GPU build slots
    partitionBuildBatchSize]
    S -->|read-back, a few frames| R[Pages + draw]
    P -. pending count .-> Q
```

### Description

A meshed partition takes two things: the collider mesh job on the CPU and the point build on the GPU. The renderer
builds one partition per slot (`SchedulerSettings.partitionBuildBatchSize` slots, each with its own counter
buffers of about 8 MB) and a slot is busy until its read-back returns, a few frames later. The mesh step used to
hand over its batches without looking at the renderer.

- **Pacing.** The renderer reports the number of partitions that wait for a slot
  (`VoxelWorld.PendingPartitionBuilds`). The mesh step dispatches its next batch only while at most two rounds of
  slots wait (`CMeshBuildScheduler.RendererBehind`). Two rounds keep the slots busy between two mesh batches; the
  rest of the backlog stays in the mesh queue, which is re-sorted around the focus and pruned to the draw window
  on every focus update. The renderer's list is first in, first out and cannot follow the focus.
- **Throughput.** With the scheduler of `ChunkPipeline.md` ("Scheduling") the mesh step is no longer bound to one
  batch per twelve frames. The GPU build slots are the limit now: a slot finishes a partition about every second
  frame. So the shipped settings went from a mesh batch of 8 with 4 slots to 16 with 8, a mesh batch being two
  rounds of slots. Fresh world at draw distance 12 and 60 frames per second (2026-11-14):

  | Scheduler | Mesh batch / slots | Frames | Seconds | Partitions per frame |
  | --- | --- | --- | --- | --- |
  | old (one sub-step every fourth frame) | 8 / 4 | 7553 | 125.9 | 0.66 |
  | work-conserving | 8 / 4 | 2532 | 42.8 | 1.98 |
  | work-conserving | 16 / 8 (shipped) | 1345 | 22.4 | 3.72 |
  | work-conserving | 32 / 16 | 1037 | 17.3 | 4.82 |

  With 32 / 16 the collider bake becomes the limit (its queue peaks at 2802 in a flight) and applying a mesh
  batch of 32 takes up to 21 ms of main-thread time in one frame, against 12 ms with 16.
- **Without a renderer** (EditMode tests, a disabled renderer) the count is 0 and the mesh step never waits.

Trade-offs:

- The collider mesh of a partition is produced by the same batch as its GPU build request, so the pacing also
  delays collider meshes behind the GPU builds. Near partitions come first in both, so the focus column is still
  collidable first.
- More slots raise the throughput at about 8 MB of GPU memory per slot: 66 MB for the 8 shipped slots, 33 MB
  before.

# Version 1.9 (Sizes From the Draw Distance, Partitions Without Voxels)

## Mesh Batch and Build Slots Follow the Draw Distance

### Description

The mesh batch and the GPU build slots only have to be as large as the draw distance needs. Flying across a chunk
border exposes one row of the draw window, `2 x DrawDistance + 1` chunks, so that side length is the work the mesh
step has to keep up with.

- **Rule.** `SchedulerSettings.meshingBatchSize` and `partitionBuildBatchSize` are given for
  `referenceDrawDistance` (shipped: 16 and 8 at 12). `MeshBatchSizeAt(drawDistance)` and `BuildSlotsAt(drawDistance)`
  scale them with the side of the draw window, rounded to the nearest, at least 1:

  | Draw distance | 2 | 4 | 6 | 8 | 12 | 16 | 24 | 40 |
  | --- | --- | --- | --- | --- | --- | --- | --- | --- |
  | Mesh batch | 3 | 6 | 8 | 11 | 16 | 21 | 31 | 32 |
  | GPU build slots | 2 | 3 | 4 | 5 | 8 | 11 | 16 | 16 |
  | GPU memory of the slots (MB) | 17 | 25 | 33 | 41 | 66 | 91 | 132 | 132 |

- **Caps.** Scaling up stops at a mesh batch of 32 and 16 slots (`MaxScaledMeshBatchSize`, `MaxScaledBuildSlots`):
  beyond that the collider bake and the main-thread time of applying one batch are the limit (version 1.8). A value
  the asset sets above a cap is used as it is.
- **When.** Both are read once, when the world is built: `CMeshBuildScheduler` sizes its result map with the batch
  and takes its pacing limit (two rounds of slots) from the slots, `VoxelWorldRenderer` creates one point builder
  with its GPU buffers per slot (`VoxelWorldRenderer.BuildSlots`). A draw distance changed in a running world needs
  a new world, as it did before.
- **No scaling.** `referenceDrawDistance = 0` uses the two values as they are; the test rigs do that.
- **Other batch sizes stay fixed.** Data, light and collider bake did not limit a fresh world or a flight at draw
  distance 6 or 12, so they are not scaled.
- **Why these sizes.** They are the smallest that keep up with a sprint flight (80 units per second) and have the
  first build over well inside 20 s. Measured in the Editor without a frame cap (2026-11-14; a player build is
  faster): the world is built while the player stands, then the flight runs for 30 s.

  | Draw distance | Mesh batch / slots | World built after | Mesh queue in flight: peak / at the end | Meshed share of the draw window in flight | Flight frame ms p95 / p99 |
  | --- | --- | --- | --- | --- | --- |
  | 12 | 8 / 4 | 10.7 s | 3070 / 2643 | 0.58 | 13.9 / 16.3 |
  | 12 | 12 / 6 | 7.3 s | 1644 / 1195 | 0.80 | 15.0 / 17.1 |
  | 12 | 16 / 8 (derived) | 5.6 s | 336 / 156 | 0.97 | 14.1 / 16.4 |
  | 6 | 4 / 2 | 3.6 s | 236 / 82 | 0.93 | 7.7 / 10.2 |
  | 6 | 8 / 4 (derived) | 2.5 s | 131 / 0 | 0.98 | 7.4 / 10.1 |
  | 6 | 16 / 8 | 1.4 s | 88 / 0 | 0.99 | 7.8 / 10.5 |

  With smaller sizes the mesh queue grows during the flight and the edge of the draw window stays unbuilt. The
  frame time of the flight does not depend on the sizes: a larger batch is not what makes a frame long.
- **Frame time (goal: 8.33 ms at the 95th percentile, stable from 20 s after the world start).** Standing, 20 s to
  40 s after the start: p95 5.1 ms, p99 6.7 ms, 13 of 5082 frames over 8.33 ms at draw distance 12; p95 4.6 ms and
  no frame over 8.33 ms at 6. In the sprint flight the goal is met at 6 (p95 7.4 ms, 3 % of the frames over
  8.33 ms) and missed at 12 (p95 14.1 ms, 42 % over 8.33 ms, 0.8 % over 16.67 ms). The long frames come from
  main-thread work that is done in one piece: applying a data batch (2.4 ms on average, up to 8.7 ms) and applying
  a mesh batch (2.4 ms, up to 5.9 ms). Bounding that work per frame by time is a later step.

## Partitions Without Voxels Are Not Built

```mermaid
flowchart LR
    Q[Mesh queue] --> V{Voxel in the
    partition?}
    V -->|no: bit clear| E[Active at once
    empty mesh, collidable]
    V -->|yes| J[CMeshBuildJob
    + GPU build request]
    J --> C{Collider mesh
    has vertices?}
    C -->|no| N[Collidable
    without bake]
    C -->|yes| B[Collider bake]
    S[SetVoxel places a block] -->|sets the bit,
    flags the partition| Q
```

### Description

At draw distance 12 almost half of the partitions of the draw window hold only air (the sky above the terrain).
They used to go through the whole mesh step: a mesh job, a GPU build slot with its read-back, and a place in the
collider bake queue, all for nothing to draw and nothing to collide with.

- **Occupancy bit.** `ChunkVoxelData.OccupiedPartitions` has one bit per partition of the chunk. `AddVoxels`
  (generation) and `SetVoxel` (edits) are the only writers of voxel data; both set the bit of every partition a
  voxel that is not air goes into. The bit is never cleared: a partition the player dug empty is still built, to an
  empty mesh.
- **Why air alone decides.** Every face belongs to the voxel it is a face of, in the point build and in the
  collider mesh. A partition without a voxel therefore has no geometry and no collider, whatever its neighbours
  hold.
- **What is skipped, per stage:**

  | Partition | Mesh job | GPU build | Collider bake |
  | --- | --- | --- | --- |
  | no voxel (bit clear) | skipped | skipped (not requested) | skipped |
  | voxels, collider mesh without a vertex (water, plants, no open solid face) | runs | runs | skipped |
  | voxels with an open solid face | runs | runs | runs |

- **Mesh step.** `MeshJobStateHandler` sorts the partitions it takes from the queue: those with voxels fill the
  batch, those without are claimed in the pool and are active at once (`CMeshBuildScheduler.ApplyEmpty`). They do
  not count against the batch; one turn takes at most four batches of them, so a turn stays bounded.
- **Collider state.** A partition without a collider is marked collidable right away
  (`MarkCollidableWithoutCollider`), so `VoxelWorld.IsCollidable` means "the collider is ready: baked, or known to
  be empty" and nothing waits for a bake that never comes. A meshed partition whose last solid face went loses its
  old collider the same way, without a bake.
- **Renderer.** No GPU build is requested for a partition without a voxel, so the renderer has no pending entry
  and no slot for it; it never waits for one.
- **Wake-up.** `SetVoxel` sets the bit and always flags the edited partition, so the first block placed in an air
  partition meshes it. `ChunkManager.FlagReMesh` (light changes) and the flags for the neighbours of an edit skip a
  partition without a voxel: it has no face that could change.
- **Pool.** A skipped partition is claimed like a meshed one, so the pool's count, its priority threshold and the
  eviction are unchanged, and "world built" (every partition of the draw window active) is reached as before.
- **Measured** (Editor, draw distance 12, 60 frames per second, 2026-11-14): of the 5000 partitions of a fresh
  world 2398 (48 %) hold no voxel and take no mesh job and no GPU build; 495 more need no bake.

  | | Before the skip | With the skip |
  | --- | --- | --- |
  | Fresh world | 1346 frames / 22.5 s | 703 frames / 11.7 s |
  | Fresh world: mesh queue peak / bake queue peak | 3128 / 1134 | 1661 / 483 |
  | Sprint flight through a building world: mesh queue peak | 3503 | 2366 |
  | Sprint flight: meshed share of the columns within 4 of the focus | 0.68 | 0.93 |
  | World complete after the flight stops | 15.7 s | 3.6 s |

Trade-offs:

- The bit is conservative: it is set for a partition that holds only water or plants, and it stays set after the
  last block is removed. Such partitions take the mesh job and the GPU build; only the bake is skipped.
- A claim in a full pool can reclaim a pooled chunk and unload its data, so the mesh step drops the partitions of
  the batch it is filling that lost a chunk of their neighbourhood; the next scan picks them up again.
