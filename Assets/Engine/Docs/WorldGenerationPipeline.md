# World Generation (Overview)

# Version 1.6 (Preview tooling)

The preview window is the comparison tool for world generation. It switches architecture, blend method and strength,
compares two configurations side by side with a difference view, lists the boundary lengths of every biome pair with
implausible pairs flagged, and draws every spline level as a 1D plot. In game, a debug key regenerates the loaded
world with another configuration, and camera bookmarks return to the same spots. Every tool reads
`WorldGenApi.SampleRegion` or the Burst spline evaluator, so the preview shows what the chunk job builds.

## Preview and comparison data flow

```mermaid
flowchart LR
    S[WorldGenSettings asset] --> A[configuration A
the asset itself
edits with Undo]
    S --> B[configuration B
PreviewConfiguration overrides
on a transient clone]
    A --> PA[PreviewPass A
ToConfig, SampleRegion,
ViewJob, boundary CountJob]
    B --> PB[PreviewPass B
same region and seed]
    PA --> V[views and statistics
clamped share, boundaries,
flora, max weight]
    PB --> V
    PA --> D[PreviewDiff
Signed / Mismatch / Pixels]
    PB --> D
    S --> SP[SplinePlots
Flatten + PlotJob per node]
    S --> G[WorldGenDebugKeys
transient variant] --> R[VoxelWorld.Regenerate]
```

### Description

- **Configurations** (`Editor/WorldGen/PreviewConfiguration.cs`): the knobs the preview switches.
  - The knobs are architecture, the method of each stage (`MaterialMethod`, `HeightMethod`), the strengths
    (B_mat, σ_mat, B_hgt, σ_hgt) and the warp of DistortedBoundary (A, λ_d, octaves).
  - Configuration A is the asset. Its controls `Capture` the knobs, draw them and `ApplyTo` the asset with Undo.
  - Configuration B is preview-only:
    - `BuildConfig` instantiates a hidden clone of the asset, applies the knobs and calls `ToConfig`, then destroys the
      clone. The asset is never written.
    - "Copy A → B" and "Apply B to asset" are the only bridges between the two.
    - B shares biomes, splines and climate with the asset.
  - `Describe()` lists only the strengths the selected methods use. Terrain-first has no height stage.
- **Passes** (`Editor/WorldGen/PreviewPass.cs`): each configuration owns one `PreviewPass` with persistent region
  buffers and textures.
  - `Schedule` builds the config, calls `SampleRegion`, then chains the `ViewJob` (all colour views, hillshade on
    the height view) and the boundary `CountJob`.
  - `Finish` uploads the textures and computes:
    - clamped share;
    - mean maximum material weight;
    - flora counts per species;
    - the boundary list.
  - Views (`PreviewView`):
    - height (with hillshade: light from the NW at 45°, adjustable exaggeration);
    - hard biome, dominant material biome, maximum material weight, surface source and flora;
    - climate, erosion and peaks/valleys;
    - in Terrain-first, also the spline outputs Offset, Amp and J.
- **Comparison** (`Editor/WorldGen/PreviewDiff.cs`): with "Compare A/B" on, the window shows A | B | Diff for
  the selected view. Both passes use the same region and seed. The difference reads sampled buffers only.
  - Height: signed B − A. Red means B is higher, blue means B is lower, with square-root scaling to the largest
    difference. Statistics: mean |Δ|, max |Δ|, mean Δ and the share of columns with |Δ| ≥ 1 block.
  - Categorical views (hard, dominant, surface, flora): a mismatch mask (magenta) over the A view dimmed to 30 %,
    plus the share of mismatching columns.
  - Any other view: the per-channel pixel difference, amplified ×4.
- **Boundary lengths** (`Editor/WorldGen/BiomeBoundaryStats.cs`):
  - A Burst job counts the 4-neighbour edges between different dominant material biomes per unordered pair.
  - Length = edges × sample stride, in blocks, sorted longest first, with each pair's share of all boundaries.
  - `BiomeSet.ImplausibleNeighbours` lists pairs that should not touch (default Desert–Snow). Those pairs are shown
    in red. The list is preview-only and is not part of the canonical JSON.
- **Spline plots** (`Editor/WorldGen/SplinePlots.cs`):
  - `TerrainSplineSet.Flatten` can record every node in pre-order (`FlatSpline`: tree, source point, depth, subtree).
    A subtree is contiguous, so `SplineRoot{NodeStart, NodeCount}` evaluates it on its own.
  - A Burst `PlotJob` per node samples the curve over its axis range, the value at every control point (a nested
    child counts as a point), and the value at the probe (C, E, PV) from the Distribution sliders.
  - The window draws each level as a 1D plot with the probe marked.
- **Window structure** (`Editor/WorldGen/`):
  - `WorldGenerationPreviewWindow.cs` holds the layout: toolbar, statistics, distribution plane, spline plots,
    settings and biome panels, the view grid and panning.
  - `.Compare.cs` holds the A/B configuration panel, the compare views and the difference.
  - `.Calibration.cs` holds the height calibration panel.
  - `PreviewBuilder` does the build scheduling:
    - it waits for a pause in the change requests (`RebuildDebounceSeconds`);
    - it owns passes A and B, the distribution plane job and the spline plots;
    - it raises `Finished` when a build has been applied.
- **In game** (`Scripts/UI/WorldGenDebugKeys.cs`):
  - F10 regenerates the loaded world with the next configuration and Shift+F10 with the previous one. The list holds
    the asset as assigned, Biome-first × 4 methods, and Terrain-first with None and with DistanceBlend. Strengths are
    the asset's.
  - Each variant is a transient copy of the asset, destroyed right after `VoxelWorld.Regenerate`.
  - F1–F4 restore a camera bookmark (position, yaw, pitch) and Shift+F1–F4 store one. Bookmarks persist in
    PlayerPrefs.
  - `PlayerManager.HoldUntilGrounded` keeps the player in place until the ground has colliders again, and lifts the
    player when the new terrain would bury them.
- **World reset** (`World/VoxelWorld.cs`): `Regenerate(WorldGenSettings = null)` runs in this order:
  1. Builds the new `ChunkScheduler` (the provider takes the override), so invalid settings throw while the world is
     still intact.
  2. Completes and applies the running batches (`VoxelEngineScheduler.CompleteRunning`).
  3. Disposes the schedulers.
  4. Evicts everything through the normal events: `ChunkPool.ReclaimAll` for the pool chunks, partitions and colliders,
     then `ChunkManager.UnloadAll` for the load ring. The renderer releases its buffers.
  5. Rebuilds the schedulers and resets the focus, so loading restarts around the player. World edits are lost.
- **Shutdown**: `DataImporter.OnDestroy` waits for the world's jobs (`VoxelWorld.WaitForJobs`) before it disposes the
  registries those jobs read, because the scene's destruction order is undefined.
- **Tests**:
  - `PreviewCompareTests`: knob round trip; B leaves the asset's canonical JSON unchanged; `Describe`; the three
    difference kinds.
  - `BiomeBoundaryStatsTests`: edges × stride; unordered pairs sorted longest first; out-of-range ids ignored;
    implausible pairs in either order but never inactive or empty biomes; the default asset flags Desert–Snow.
  - `SplinePlotsTests`: the flatten order covers every node with contiguous subtrees; plots hit constant knots and
    children agree with their parents; an invalid set reports an error.
  - PlayMode `WorldRegenerationTests`: everything is evicted; the other architecture changes the terrain;
    regenerating with the asset reproduces it.
- **Performance**: the tooling does not touch the chunk job; see `Tests/Performance/Baselines/README.md`. The
  blend configurations above the 3 ms/chunk budget (Version 1.4) remain to be revisited.

### Ownership of native memory

- `PreviewPass` owns its persistent region buffers, pixel arrays, boundary edge counts and its `WorldGenConfig`. The
  config is replaced at the start of the next build and released in `Dispose`.
- `PreviewBuilder` owns both passes and the distribution plane arrays of a running build. Its `Dispose` (from the
  window's `OnDisable`) completes a running build, then disposes both passes and the distribution texture.
- `PreviewConfiguration.BuildConfig` returns a config the caller disposes. The transient clone is destroyed inside
  the call.
- `PreviewDiff` and `SplinePlots` allocate only TempJob scratch, which is released before they return.
- `VoxelWorld.Regenerate`: the new `GeneratorConfig.WorldGen` belongs to the new `ChunkScheduler`, like the original.
  The old one is released when the old scheduler is disposed. The `WorldGenSettings` passed in stays the caller's.

# Version 1.5 (Flora)

Flora placement is a pure function of (x, z, seed, settings) on the material-stage result: the same decision comes
out of `WorldSampler.SampleColumn`, `WorldGenApi.SampleRegion` and the chunk job, so plants and trees have no chunk
seams. Each biome carries a list of `FloraRule`s; the standard biomes have untuned placeholder rules.

## Flora in the column pipeline

```mermaid
flowchart LR
    C[WorldSampler.SampleCore
height, weights, surface source] --> R[FloraPlacement.RollRule]
    R --> FB[flora biome
SurfacePick salt FloraBiome]
    FB --> SP[species
Hash01 FloraSpecies vs
cumulative probabilities]
    SP --> G[ground checks
same surface, above water,
not clamped at Hmax, solid top]
    G --> MB{multi-block?}
    MB -- no --> P[plant at h+1]
    MB -- yes --> A[anchor if no candidate
within r_tree outranks it
Hash32 TreePriority]
    A --> S[stamp structure / stack
in-chunk voxels only]
```

### Description

- **Rules** (`WorldGen/Flora/FloraRules.cs`): a `FloraRule` names a species (one voxel or one
  `VoxelStructure`), a per-column probability, `RequiresSurfaceFromSameBiome` (default false) and
  `MinHeightAboveWater` (default 1).
  - `MinStackHeight` / `MaxStackHeight` (default 1) stack a single voxel, e.g. a cactus of height 1–3. A stack above 1
    counts as multi-block.
- **Config** (`WorldGen/Flora/FloraConfigBuilder.cs`): `WorldGenSettings.ToConfig` flattens the rules of the active biomes into
  `WorldGenConfig.FloraRules`; each `BiomeDef` addresses its range.
  - Species ids are 1-based, in first-use order (`WorldGenSettings.FloraSpeciesNames()`, [0] = "none").
  - Structures are expanded to voxel offsets once (`FloraStructures`, `FloraVoxels`):
    - offset (0, 0, 0) is the first voxel above the top block;
    - Line is inclusive;
    - Box is centred on its origin;
    - Circle is a horizontal disc (a ring when not filled);
    - Cylinder stacks circles.
  - Other tables:
    - `FloraMultiBlockIntervals`: the cumulative ranges of multi-block rules, used as a sample-free pre-filter.
    - `FloraReach`: the largest horizontal structure extent.
    - `FloraPadding`: reach + r_tree, or 0 without multi-block rules.
    - `VoxelClasses`: the replace class per voxel id.
  - A rule with neither or both species fields is skipped with a warning; a biome whose probabilities sum past 1 is
    reported.
  - `TreeSpacingRadius` (r_tree, default 3, must be ≥ 0) lives in `WorldGenSettings`.
- **Decision** (`WorldGen/Flora/FloraPlacement.cs`), per column:
  1. The flora biome is a dithered pick from w_mat with salt FloraBiome, independent of the surface pick. With
     material method None it is the hard biome.
  2. The species is the rule whose cumulative interval holds Hash01(FloraSpecies); none means no plant.
  3. The ground must allow it:
     - the surface biome's top block is solid;
     - the column is not clamped at Hmax;
     - h_int + 1 − WaterLevel ≥ max(minHeightAboveWater, 0), so a plant is never below the water surface.
  4. A multi-block candidate becomes an anchor only if no other candidate in its r_tree disc outranks it:
     - the higher Hash32(TreePriority) wins;
     - equal hashes go to the lower (x, z), a strict order, so two anchors are never within r_tree.

  `ColumnSample.FloraSpecies` / `FloraSource` (and the region buffers of the same name) hold the result at single
  plants and anchors, (0, −1) elsewhere.
- **Chunk job** (`Jobs/Chunk/ChunkGenerationTerrain.cs`, `ChunkGenerationVegetation.cs`):
  - `PrepareChunkMaps` fills a `FloraGrid` over chunk ± `FloraPadding`:
    - the core sample and rule roll of every chunk column;
    - for ring columns that pass the pre-filter, the same sample and roll, keeping only multi-block rules;
    - the anchor flag for chunk ± reach.
  - The `BlendCache` covers the ring too, and `WorldGenApi` extends its tile caches by `FloraPadding` in the same way.
  - `PlaceVegetation` then writes:
    1. single-block plants at h + 1;
    2. every anchor in world (x, then z) order: its structure, or its stack, with height from salt FloraStackHeight.

    Voxels outside the chunk are clipped.
  - **Overwrite rule:** a flora voxel replaces only a voxel of a lower class: air < foliage < leaves (transparent with
    collision) < solid. Ids not in the table (terrain, water) count as solid. Logs therefore beat leaves, and leaves
    beat grass tufts. Because the order is the world order, every voxel ends up as in a whole-world stamp, whichever
    chunk holds it.
- **Renderer**: flora adds voxel runs per column (measured in flight: median 4.6, maximum 6 intervals per column).
  All chunk lists share one paged interval store, so a longer list just takes more pages
  (`MeshAndRenderPipeline.md`, Version 1.5).
- **Burst**: `WorldSampler.SampleCore` is a Burst direct-call entry like `SampleColumn`. Run managed (Mono), the
  core's Terrain-first heights differ from the jobs by 1 ulp.
- **Tests**:
  - `Tests/EditMode/FloraChunkTests.cs`:
    - `PrepareChunkMaps` columns and chunk-job voxels over 4×4 chunks equal `SampleRegion` and a test-side
      whole-world stamp, in all 8 configurations.
    - Border-crossing trees are complete.
    - Repeat runs are identical.
  - `Tests/EditMode/FloraPlacementTests.cs`: flora biome, species, ground checks and anchor rules, structure
    expansion and config tables.
  - `DataImporterTests`: the shipped settings resolve every flora id through the registry, and every land biome
    carries flora.
  - Fixture: `Tests/EditMode/FloraFixture.cs`.
- **Performance** (`Tests/Performance/ChunkGenerationBenchmark.cs`): new rows
  `ChunkJob.Run <architecture> <method> flora ms/chunk`. They use the same 16 chunks as the per-method rows, with
  the placeholder rules. See `Tests/Performance/Baselines/README.md`.

### Ownership of native memory

- `WorldGenConfig` owns the flora tables:
  - `FloraRules`
  - `FloraStructures`
  - `FloraVoxels`
  - `FloraMultiBlockIntervals`
  - `VoxelClasses`

  They are always created by `ToConfig` (possibly empty), and `WorldGenConfig.Dispose` releases them.
- The `FloraGrid` lives in `Allocator.Temp` inside one chunk's `Execute` and is disposed there.

# Version 1.4 (Blending methods)

Each stage has four blend methods: the material stage in both architectures, the height stage in Biome-first
(Terrain-first height has no biome input, so it is the same for every material method). That gives 8
configurations: Terrain-first × 4 material methods and Biome-first × 4 methods. Both stages take the method and
strength from `WorldGenSettings` (`MaterialMethod`, `MaterialBlendWidth` B_mat, `MaterialSigma`; `HeightMethod`,
`HeightBlendWidth` B_hgt, `HeightSigma`; `WarpAmplitude` A, `WarpWavelength`, `WarpOctaves`), and they can be switched
in the preview and in the game. The default values are untuned placeholders.

## Blend stages in the column pipeline

```mermaid
flowchart LR
    F[ClimateFields
T F C E W PV] --> D[BiomeClassifier.Distances
d_b squared + hard class
once per column]
    D --> M[BlendMethods.Compute
material stage]
    D --> HS[BlendMethods.Compute
height stage, Biome-first]
    L[BlendCache
per chunk / 64x64 tile
nn and warped nn lattices] --> M
    L --> HS
    M --> MW[MaterialWeights] --> SP[SurfacePick
dithered] --> SS[SurfaceSource]
    MW --> DM[DominantMaterialBiome
argmax]
    HS --> HW[HeightWeights] --> BH[BiomeFirstHeight
blended parameters]
    SS --> J[ChunkJob.FillTerrain
surface blocks]
```

### Description

- **Methods** (`WorldGen/Blending/BlendMethods.cs`): one enum switch per stage, every result normalised, Σ w = 1.
  - **1 None**: one-hot on the hard class.
  - **2 DistanceBlend**: the compact kernel k(r) = (1 − r²/R²)² over the hard classes nn(q) of a world-aligned lattice.
  - **3 DistortedBoundary**: the hard class at the warped position q + A·Ω(q), where Ω is a two-octave simplex warp
    (`WorldGen/Noise/WarpField.cs`, salts BlendWarpX/Y).
    - Material stage: one-hot on the warped class.
    - Height stage: warp plus the kernel of method 2 over the warped classes with B_hgt.
    - A = 0 reduces it to the unwarped method.
  - **4 ParameterSpaceWeighting**: the shifted Gaussian w_b ∝ exp(−(d_b² − d_min²)/(2σ²)) over the parameter-space
    distances.
    - The hard class always gets the largest weight, and σ → 0 gives method 1.
    - It needs no neighbourhood.
- **Kernel taps** (`WorldGen/Blending/KernelTaps.cs`): per-phase tap lists on a lattice of stride
  clamp(floor(R/4), 1, 2), in a fixed row-major order. They are built once in `WorldGenSettings.ToConfig`
  and owned by `WorldGenConfig` (`MaterialTaps`, `HeightTaps`). The phase uses floor modulo, so negative columns get
  the same taps as positive ones.
- **Lattice cache** (`WorldGen/Blending/BlendLattice.cs`): `ChunkJob.PrepareChunkMaps` builds a `BlendCache`
  per chunk, and `WorldGenApi.SampleRegion` builds one per 64×64 tile (the region job runs one index per tile).
  - The cache holds a padded lattice of nn(q) for DistanceBlend and one of the warped classes for the
    DistortedBoundary height stage. Both are classified by the same Burst entry points the pure sampler calls per
    tap (`WorldSampler.ClassifyAt` / `ClassifyWarpedAt`).
  - A cached column is therefore bit-identical to the pure `SampleColumn(x, z, cfg)`.
  - A summed-area table of boundary points lets a kernel window with one class return one-hot weights without
    summing.
  - A zoomed-out preview, where the columns are further apart than the taps, skips the lattice and evaluates the
    taps directly, with the same result.
- **Shared work**:
  - The squared parameter-space distances and the hard class are computed once per column and shared by
    classification and method 4.
  - In Biome-first, the height stage reuses the material weights when both stages use the same method and strength
    (`WorldGenConfig.StagesShareWeights`; never for DistortedBoundary, whose stages differ).
  - Method 1 sets the dominant biome and the surface source to the hard class directly.
- **Surface source** (`WorldGen/Blending/SurfacePick.cs`): a white-noise dithered pick from the material
  weights, using hash salt SurfacePick.
  - If rounding leaves the cumulative sum just below the threshold, it falls back to the last biome with non-zero
    weight, so it never returns "none" for non-empty weights.
  - One-hot weights return their own biome.
  - `FillTerrain` takes the surface blocks from this biome.
- **Validation** (`WorldGenSettings`): a strength the selected method uses and cannot work with is reported in
  `OnValidate` and rejected by `ToConfig` with an `InvalidOperationException`:
  - B ≤ 0
  - σ ≤ 0
  - A < 0 or non-finite
  - fewer than one warp octave

  Unused strengths are not checked, and nothing is clamped.
- **Preview** (`Editor/WorldGen/WorldGenerationPreviewWindow.cs`): the "Surface source" view shows the dithered
  pick next to the hard-class biome view, so the material methods are visible. In Biome-first the height view shows
  the height methods. Method switching and comparison are described in Version 1.6.
- **Tests** (`Tests/EditMode/BlendingTests.cs`):
  - cached columns are bit-identical to the pure sampler, and columns outside the cache fall back to it
  - Terrain-first height is bitwise identical for every material method; Biome-first blended height is the weighted
    sum of the biome heights
  - weights are normalised for every method and stage
  - method 4 is the shifted Gaussian, its argmax is the hard class, and its hard limit is method 1
  - the surface pick never returns "none" for weights just below one, and its frequencies follow the weights
  - kernel taps, stride and floor-modulo phases
  - the warp field, and DistortedBoundary at zero amplitude
  - each method against its formula
  - validation and shared stage weights
  - a Burst guard for the blend entry points

  The chunk-job tests check the spatial methods against the sampler in both architectures.
- **Performance** (`Tests/Performance/ChunkGenerationBenchmark.cs`): rows `ChunkJob.Run <architecture> <method>
  ms/chunk`, each the mean over 16 fixed chunks (numbers in `Tests/Performance/Baselines/README.md`):
  - Method 1 costs about 5 % more than the unblended pipeline did.
  - Terrain-first DistanceBlend and DistortedBoundary and Biome-first DistortedBoundary are above the 3 ms/chunk
    budget.

### Ownership of native memory

- `WorldGenConfig` owns `MaterialTaps` and `HeightTaps` (only created when a method uses them); `WorldGenConfig.Dispose`
  releases them with the rest.
- A `BlendCache` lives in `Allocator.Temp` inside the job that builds it (one chunk or one region tile) and is disposed
  there.
- A default `BlendCache` means "no cache": the sampler then evaluates every tap itself.

# Version 1.3 (Biome-first)

`GenArchitecture` has two values, `TerrainFirst` and `BiomeFirst`; every chunk goes through
`WorldSampler.SampleColumn`. The shipped `Assets/EngineSettings/WorldGenSettings.asset` selects Terrain-first (the
class default for new assets is Biome-first). Without a blend method, Biome-first shows a height step at every biome
boundary.

## Biome-first column pipeline

```mermaid
flowchart LR
    S[WorldGenSettings SO\nWaterLevel, octave tables] -->|ToConfig| C[WorldGenConfig\nOctaveCoefficients a_b,i]
    B[BiomeSet SO\nOffset_b, HeightAmplitude_b,\nOctaveWeights_b, Jaggedness_b] --> S
    C --> F[ClimateFields\nT F C E W PV]
    F --> K[BiomeClassifier 5D\nno height dimension]
    K --> W[BlendWeights\nheight stage]
    W --> H[BiomeFirstHeight\nH = Offset + sum a_i N_i + J P\nfrom the blended parameters]
    H --> O[ColumnSample\nHeight, NormalizedHeight]
    O --> J[ChunkJob.PrepareChunkMaps]
    J --> V[FillTerrain]
    O --> R[SampleRegion] --> P[Preview views]
    T[Terrain-first reference] -.->|height calibration| B
```

### Description

- **Classification first** (`WorldSampler.SampleColumn`, Biome-first branch): the biome is the 5D argmin over
  (T, F, C, E, PV) without a height dimension; the material and height weights come from the blend methods
  (Version 1.4).
- **Biome-first height** (`WorldGen/Height/BiomeFirstHeight.cs`): the height-stage weights blend the per-biome
  parameters, Offset = Σ w_b Offset_b, a_i = Σ w_b a_{b,i} with a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i}
  (precomputed in `WorldGenConfig.OctaveCoefficients`), J = Σ w_b J_b, and H is computed once from them. All biomes
  sample the same N_i and P; because every biome parameter enters H linearly, blending the parameters equals blending
  the per-biome heights. The clamp to [Hmin, Hmax] is recorded.
- **Configuration**: `GeneratorConfig` holds `Voxels` and `WorldGen` only; the seed and the water level are read from
  `WorldGen` (`WorldGenConfig`). `WaterLevel` lives on `WorldGenSettings` (default 124) and is part of the canonical JSON. `VoxelEngineSettings` requires a
  `WorldGenSettings`; the chunk scheduler throws without one. `NoiseProfileSettings` and `WarpedNoiseLayerSettings`
  live in `WorldGen/Settings/`.
- **Preview** (`Editor/WorldGen/WorldGenerationPreviewWindow.cs`, menu Infinitile → World Generation Preview):
  built on `WorldGenApi.SampleRegion` for both architectures; the settings and biome inspectors sit next to the
  views, and every edit rebuilds the preview (Undo included). It is the tool for iterating generation parameters.
- **Height calibration** (`Editor/WorldGen/BiomeFirstCalibration.cs`, panel "Height calibration" in the
  preview): samples the Terrain-first pipeline of the same settings over the framed area, groups the columns by
  their Terrain-first biome and suggests Jaggedness_b = mean J, Offset_b = mean H − Jaggedness_b · mean P and
  HeightAmplitude_b = std(detail residual) / std(Σ OctaveWeights_{b,i} N_i), so both architectures produce similar
  height distributions. The J·mean P correction keeps the non-negative ridge term from lifting jagged biomes
  (Mountain, Snow) above their Terrain-first mean height. Suggestions appear next to the current values; a biome is
  written only on its Apply click.
- **Performance** (`Tests/Performance/ChunkGenerationBenchmark.cs`): row `ChunkJob.Run BiomeFirst ms/chunk`; numbers
  in `Tests/Performance/Baselines/README.md`.

### Ownership of native memory

`GeneratorConfig.Dispose` releases `WorldGen` only; the voxel map belongs to the `VoxelRegistry`. A
`WorldGenSettings` is always the source of the config.

# Version 1.2 (Terrain-first)

The game runs the Terrain-first architecture: `Assets/EngineSettings/WorldGenSettings.asset` selects
`GenArchitecture.TerrainFirst` with `TerrainSplineSet_Default.asset` (placeholder splines), and the chunk job takes
its columns from `WorldSampler.SampleColumn`.

## Terrain-first column pipeline

```mermaid
flowchart LR
    T[TerrainSplineSet SO\nnested splines] -->|Flatten| C[WorldGenConfig\nSplineNodes / SplinePoints / roots]
    S[WorldGenSettings SO] -->|ToConfig| C
    C --> F[ClimateFields\nT F C E W PV]
    F --> N[NestedSpline.Evaluate\nOffset, Amp, J over C E PV]
    N --> H[H = Offset + Amp * sum g_i N_i + J P\nclamp to Hmin..Hmax]
    H --> K[BiomeClassifier 6D\nh = saturate of H]
    K --> O[ColumnSample\nHeight, NormalizedHeight, TerrainParams]
    O --> J[ChunkJob.PrepareChunkMaps]
    J --> V[FillTerrain\nblocks from SurfaceSource]
    O --> R[SampleRegion] --> P[Preview: biome 6D, height,\nE, PV, Offset, Amp, J]
```

### Description

- **Nested splines** (`WorldGen/Height/NestedSpline.cs`): a spline is a list of control points over one axis (C, E or
  PV) whose values are constants or child splines over another axis. The asset keeps a flat, pre-ordered
  tree (children have higher node indices), so Burst evaluates a root with one pass from the last node to the first
  into a fixed-size value list. Interpolation is Fritsch–Carlson monotone cubic Hermite (Wikipedia, "Monotone cubic
  interpolation") or linear per spline; outside the control range the end value holds.
- **Terrain-first height** (`WorldGen/Height/TerrainFirstHeight.cs`): Offset, Amp and J are evaluated at the column
  from the three roots, then H = Offset + Amp · Σ g_i N_i + J · P with the global octave weights g_i and the shared
  detail octaves; the clamp to [Hmin, Hmax] is recorded. No biome takes part, so the height is continuous wherever
  the fields are.
- **Classification** (`WorldSampler.SampleColumn`): the normalised height h = saturate((H − Hmin)/(Hmax − Hmin)) is the
  sixth distance dimension with weight λ_h. A `HeightMethod` other than None is ignored with a warning in
  Terrain-first.
- **Spline asset** (`WorldGen/Settings/TerrainSplineSet.cs`): a `ScriptableObject` with named splines, three root
  indices and `Notes`. `Validate` reports missing roots, unsorted or duplicate X, out-of-range X, cycles, nesting
  deeper than 8 and more than 127 nodes per root; `Flatten` throws on any problem (no silent repair). `ToJson`
  / `FromJson` round-trip a human-readable form (`{"Axis","Interpolation","Points":[{"X","Value"}]}`), and
  `ResetToPlaceholders` restores the placeholder trees. `WorldGenSettings.ToCanonicalJson` inlines the tree.
- **Chunk job** (`Jobs/Chunk/ChunkGenerationTerrain.cs`): `PrepareChunkMaps` calls `WorldSampler.SampleColumn` per
  column, independent of the chunk origin, so chunk borders carry no seam. `ChunkColumn` carries `SurfaceSource`,
  `FloraSpecies`, `FloraSource` and `HeightF`; the blocks come from the surface-source biome. `FillTerrain` places the
  top block at h_int even under water, so Ocean floors are sand; its ice override follows `EnableClimateOverrides`.
  `ChunkJob` compiles with `CompileSynchronously` and `FloatMode.Strict` because it calls the sampler.
- **Preview** (`WorldGenerationPreviewWindow`): in Terrain-first the views add Offset, Amp and J per column; the 1D
  spline plots are described in Version 1.6.
- **Performance** (`Tests/Performance/ChunkGenerationBenchmark.cs`): row `ChunkJob.Run TerrainFirst ms/chunk`; numbers
  in `Tests/Performance/Baselines/README.md`.

### Ownership of native memory

`WorldGenConfig` also owns `SplineNodes` and `SplinePoints`; both are released by `WorldGenConfig.Dispose` with the
other arrays.

# Version 1.1 (Sampler foundations)

The foundations of the column sampler: seeded noise, climate fields, settings, the Burst config mirror, the pure
sampler and the region API used by the preview and the tests.

## Sampler pipeline

```mermaid
flowchart LR
    S[WorldGenSettings SO] -->|ToConfig| C[WorldGenConfig\nBurst mirror]
    B[BiomeSet SO] --> S
    C --> W[WorldSampler.SampleColumn]
    W --> F[ClimateFields\nT F C E W PV]
    F --> K[BiomeClassifier\nargmin]
    K --> M[BlendWeights]
    M --> H[Height]
    H --> O[ColumnSample]
    O --> R[WorldGenApi.SampleRegion]
    R --> P[Preview views]
```

### Description

- **Hashing and noise basis** (`WorldGen/Core/Hash.cs`, `WorldGen/Core/HashSalts.cs`, `WorldGen/Noise/SeededSimplex.cs`):
  every noise instance is a seeded 2D simplex with hashed gradients. The seed changes the field itself, not a
  translation of it; each instance gets its own salt and a hashed offset. `NoiseProfile` and `WarpedNoiseLayer` sit
  on this basis.
- **Fields** (`WorldGen/Noise/ClimateFields.cs`): T, F, C, E, W and the derived PV = 1 − |3|W| − 2| per column,
  with `ClimateFrequencyScale` scaling the position and `WGain` on W. `DetailOctaves` (K octaves, wavelengths
  512…16) and `PeakNoise` (P = (1 − |n|)²) supply the height detail.
- **Settings** (`WorldGen/Settings/`): `WorldGenSettings` holds architecture, blend methods and widths, λ weights,
  height range, octave tables, the T/F/C/E/W climate profiles with their couplings and references to a `BiomeSet`
  and a `TerrainSplineSet`, plus `OnValidate`.
  - `ToCanonicalJson` produces a deterministic manifest with the biome set inlined. The tree is built in
    `WorldGenSettingsJson`.
  - `ToConfig` builds the `WorldGenConfig` mirror: flattened NativeArrays, the per-biome octave coefficients
    a_{b,i} = HeightAmplitude × OctaveWeights and the climate instances. The conversion is in
    `WorldGenConfigBuilder`.
- **Biome** carries TargetErosion, TargetPV, HeightOffset, HeightAmplitude, OctaveWeights, Jaggedness and
  FloraRules. Which biomes are active is decided by the `BiomeSet` asset, in index order;
  `Assets/EngineSettings/BiomeSet_Default.asset` lists Plains, Desert, Mountain, Snow, Forest, Ocean.
- **Sampler** (`WorldGen/Core/WorldSampler.cs`): `SampleFields` and `SampleColumn` are pure functions of
  (x, z, seed, config). The entry points are Burst direct-call, so the preview and the tests see the same bits as
  the jobs.
- **Region API** (`WorldGen/Core/WorldGenApi.cs`): `SampleRegion(in WorldGenRequest, ref RegionBuffers)` fills any
  subset of caller-owned buffers in a `CompileSynchronously`/`FloatMode.Strict` job. Unrequested buffers stay
  unallocated. Element [j × Size.x + i] is column (Origin.x + i·Stride, Origin.y + j·Stride); a stride above 1 gives
  zoomed-out previews.

### Ownership of native memory

| Container | Owner | Released by |
|---|---|---|
| `GeneratorConfig.Voxels` | `VoxelRegistry` | the registry |
| `GeneratorConfig.WorldGen` (all arrays and climate profiles) | the `ChunkScheduler` that received the config | `GeneratorConfig.Dispose` → `WorldGenConfig.Dispose` |
| `RegionBuffers` | the caller of `SampleRegion` | the caller |
| Dummy arrays for unrequested buffers | `SampleRegion` | disposed with the returned handle |

`GeneratorConfig` travels inside job structs, so its `WorldGen` field must always be constructed (by
`WorldGenSettings.ToConfig`).

# Version 1.0 (Chunk Jobs)

## Generation Pipeline

```mermaid
flowchart
    C[Chunk Scheduler
    Chunk Data Jobs\nCPU]
    C --> E1[Prepare Chunk Inputs\nSeed, Bounds, Neighbor Context]
    E1 --> E2[Sample Noise Fields\nHeight, Density, Biome Weights]
    E2 --> E3[Base Terrain\nAir, Soil, Stone, Water]
    E3 --> E4[Apply Feature Passes\nOres, Caves, Structures, Vegetation]
    E4 --> E6[Pack Chunk Data\nVoxel Map + Metadata]
    E6 --> F[Chunk Data Ready]
```

### Description

In version 1.0, world generation is fully chunk-based and CPU job driven.
The scheduler collects generation requests, batches chunk jobs, and builds voxel data for each chunk.
Inside the generate step, each chunk job runs a fixed sequence: prepare inputs, sample noise fields, classify base
materials, run feature passes (ores/caves/structures/vegetation), apply border fixups, then pack the final voxel map and
metadata.
When chunk data is finished, it is registered in the chunk manager and forwarded to the mesh pipeline for visual and
collider updates.

Advantages:

- Simple and deterministic pipeline with clear stage boundaries.
- Easy to debug because generation and handoff happen in explicit queue steps.
- Works well for small and medium visible worlds.

Trade-offs:

- Regeneration cost can spike when many chunks are dirty in the same frame.
- CPU-side generation becomes a bottleneck as world size and update frequency grow.
- Local edits can trigger broader recomputation depending on chunk dependencies.
