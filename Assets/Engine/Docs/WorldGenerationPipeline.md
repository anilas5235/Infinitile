# World Generation (Overview)

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
