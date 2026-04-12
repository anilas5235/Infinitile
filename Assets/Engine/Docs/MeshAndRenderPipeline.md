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
    A[Voxel Registry 
    Data] --> B[Build Texture2D
Array]
B --> C[Bind Array +
Material Properties]

T[Vertex] --> U[Extract Texture
Index & Sample 
Texture Array]
U --> V[Apply AO
...]
V --> W[Output Color]
```

### Description

In version 1.0, rendering uses classic mesh rendering per chunk with a Texture2D Array bound to a ShaderGraph material.
Each voxel face stores texture indices, and the shader samples the correct layer from the texture array.

Advantages:

- Simple authoring workflow in ShaderGraph.
- Good visual flexibility without changing mesh topology.

Trade-offs:

- Rendering is still tied to fully buildt meshes.

# Version 1.1 (Partitions)

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
    A[Voxel Registry 
    Data] --> B[Build Texture2D
Array]
B --> C[Bind Array +
Material Properties]

A--> D[Build Quad 
Buffer]
D --> E[Bind Buffer +
Material Properties]

L[Point] --> M[Extract Quad
Index & Sample 
Quad Buffer]
M --> O[Expand to Quads
Geometry Shader]
O -->U[Extract Texture
Index & Sample
Texture Array]
U --> V[Apply AO 
& Lighting
...]
V --> W[Output Color]
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

```mermaid
flowchart LR
    D[Request Partition] --> E[Mesh Queue]
    E --> F[Build Collider Mesh 
    CPU Job]
F --> G[Render Mesh Applied]
G --> H[Bake Collider Queue]
H --> I[Bake Collider
CPU Job]
I --> J[Collider Applied]

E --> B[Compute Shader Queue]
B --> C[Build Vertex Buffer
Shader]
C --> K[Resreve Global Buffer Space
CPU]
K --> L[Copy Vertex to Global
& ReBuild Indices
Shader]   
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
    A[Partition Render Request] --> B[PointBuilder Compute]
    B --> C[Reserve/Compact Global Buffer Range]
    C --> D[CopyPoints Compute]
    D --> E[ReBuildIndexAndArgs Compute]
    E --> F[Global Vertex + Index + Args Buffers]
    F --> G[DrawProceduralIndirect]
    G --> H[Vertex Pulling Shader]
    H --> I[Frame Output]
```

### Description

In version 1.2, rendering becomes meshless for the visual path.
Compute shaders build and pack point/vertex data into global GPU buffers, then indirect draw arguments are rebuilt
before issuing `DrawProceduralIndirect`.

Advantages:

- Strong reduction of CPU render setup work.
- Much better scaling for large numbers of visible partitions.
- Lower draw call overhead through batched indirect rendering.

Trade-offs:

- Higher implementation complexity (compute pipeline, synchronization, buffer lifetime).
- Harder debugging and profiling compared to classic mesh rendering.

