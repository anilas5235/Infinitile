using Engine.Scripts.WorldGen.Graph;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Density{
    /// <summary>
    ///     The terrain outputs of a compiled noise graph as the density passes read them (§5.1): the lattice slots of
    ///     preliminary_surface and factor, the terrain marker with its cell height and corner program, A (the bound
    ///     of the 3D noise term) and the y where the top slide starts.
    /// </summary>
    public struct DensityTerrainGraph{
        public GraphData Graph;

        /// <summary>Lattice slots of preliminary_surface and factor.</summary>
        public int PreliminarySurface, Factor;

        /// <summary>Marker slot of terrain_density and its cell height (4 or 8).</summary>
        public int Marker, CellHeight;

        /// <summary>The corner program of the marker's cell height.</summary>
        public int CornerProgram;

        /// <summary>A: the most the 3D noise term adds to or takes from the density.</summary>
        public float NoiseBound;

        /// <summary>Below this y the top slide leaves the density unchanged.</summary>
        public float SlideStart;

        /// <summary>Corner layers per corner column: y 0 to the world height in steps of the cell height.</summary>
        public int Layers => GraphLimits.WorldHeight / CellHeight + 1;

        /// <summary>
        ///     The terrain of <paramref name="graph" />, or false with the reason when the graph has no terrain outputs
        ///     (the compiler has checked them when it has).
        /// </summary>
        public static bool TryCreate(CompiledNoiseGraph graph, out DensityTerrainGraph terrain, out string error){
            terrain = default;
            GraphOutputInfo density = graph.Output(GraphOutputs.TerrainDensity);
            GraphOutputInfo surface = graph.Output(GraphOutputs.PreliminarySurface);
            GraphOutputInfo factor = graph.Output(GraphOutputs.Factor);
            GraphOutputInfo noise = graph.Output(GraphOutputs.TerrainNoise);
            GraphOutputInfo slide = graph.Output(GraphOutputs.TopSlideStart);
            if (density == null || surface == null || factor == null || noise == null || slide == null){
                error = $"The graph has no terrain: it needs the outputs {string.Join(", ", GraphOutputs.Terrain)}.";
                return false;
            }

            if (surface.LatticeValue < 0 || factor.LatticeValue < 0 || density.Marker < 0){
                error = $"The graph's {GraphOutputs.PreliminarySurface} and {GraphOutputs.Factor} must be lattice values and {GraphOutputs.TerrainDensity} a marker.";
                return false;
            }

            GraphData d = graph.Data;
            int cellHeight = d.MarkerCellHeights[density.Marker];
            terrain = new DensityTerrainGraph{
                Graph = d,
                PreliminarySurface = surface.LatticeValue,
                Factor = factor.LatticeValue,
                Marker = density.Marker,
                CellHeight = cellHeight,
                CornerProgram = cellHeight == 4 ? d.CornerProgram4 : d.CornerProgram8,
                NoiseBound = math.max(math.abs(noise.Bounds.Min), math.abs(noise.Bounds.Max)),
                SlideStart = slide.Bounds.Min
            };
            error = null;
            return true;
        }
    }

    /// <summary>Work of the density passes, summed over chunks (W2-TST-05): the graph's own counters and the cell counts.</summary>
    public struct DensityWorkCounters{
        public GraphWorkCounters Graph;

        public long Chunks;

        /// <summary>Corner columns the corner program ran on, and the corners it computed.</summary>
        public long CornerColumns, Corners;

        /// <summary>
        ///     Voxel blocks (4 × 8 × 4) filled from the corner-column ranges alone, filled because their computed corners
        ///     agree in sign, and interpolated per voxel.
        /// </summary>
        public long BlocksByRange, BlocksBySign, BlocksMixed;

        /// <summary>Voxels interpolated in mixed blocks.</summary>
        public long InterpolatedVoxels;

        /// <summary>Terrain voxels.</summary>
        public long SolidVoxels;

        public void Add(in DensityWorkCounters o){
            Graph.Add(o.Graph);
            Chunks += o.Chunks;
            CornerColumns += o.CornerColumns;
            Corners += o.Corners;
            BlocksByRange += o.BlocksByRange;
            BlocksBySign += o.BlocksBySign;
            BlocksMixed += o.BlocksMixed;
            InterpolatedVoxels += o.InterpolatedVoxels;
            SolidVoxels += o.SolidVoxels;
        }

        public override string ToString(){
            return $"{Chunks} chunks: corner columns {CornerColumns}, corners {Corners}, blocks by range {BlocksByRange} / by sign {BlocksBySign} / mixed {BlocksMixed}, " +
                   $"interpolated {InterpolatedVoxels}, solid {SolidVoxels}; graph: {Graph}";
        }
    }
}
