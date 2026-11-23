using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.WorldGen.Surface;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Schedules and manages jobs that generate chunk data, wraps <see cref="ChunkJob" />,
    ///     tracks job lists and result maps, and forwards completed chunks to <see cref="ChunkManager" />.
    /// </summary>
    internal class ChunkScheduler : JobScheduler{
        private readonly ChunkManager _chunkManager;
        private readonly GeneratorConfig _config;
        private readonly VoxelWorld _world;

        // The density generator (WorldGen v2): the shipped noise graph, compiled once per scheduler, and whether the
        // generated code fits it (else the interpreter runs it)
        private readonly CompiledNoiseGraph _graph;
        private readonly DensityTerrainGraph _terrain;
        private readonly bool _generatedCode;

        /// <summary>
        ///     Indicates whether the scheduler is ready to start a new chunk generation batch.
        /// </summary>
        internal bool IsReady = true;

        private JobHandle _handle;
        private NativeList<int2> _jobs;
        private NativeParallelHashMap<int2, ChunkVoxelData> _results;

        // Where a collect the budget cut stands: the next job of the batch to add (and upload), and whether the jobs
        // that read the chunk the store evicts next were completed in an earlier item
        private int _next;
        private bool _evictionReadersDone;

        // BatchAddInOneItem only: the batch's chunks are in the store, the upload continues at _next
        private bool _added;

        /// <summary>
        ///     For measurements only (the comparison with the collect before 2026-11-18): true adds the whole batch to
        ///     the chunk store in one item of work, with every eviction it needs, and uploads the chunks one item
        ///     each after it. False (the default) makes every eviction and every add its own item. Change it only
        ///     between two batches.
        /// </summary>
        internal bool BatchAddInOneItem;

        /// <summary>
        ///     Whether a job that reads the given chunk still runs on the worker threads (the collider mesh job, the
        ///     light job, the collider bake while the pool renders the chunk); set by the
        ///     <see cref="VoxelEngineScheduler" />, null means none. Under a frame budget the collect does not wait
        ///     for such a job before an eviction: it ends the frame's turn and looks again in the next frame.
        /// </summary>
        internal Func<int2, bool> EvictionReadersRunning;

        /// <summary>
        ///     Initializes a new instance of the <see cref="ChunkScheduler" /> class with its own result collection.
        /// </summary>
        /// <param name="settings">Voxel engine settings providing chunk dimensions and load distance.</param>
        /// <param name="chunkManager">Chunk manager that receives finished chunk data.</param>
        /// <param name="config">Generator configuration used by chunk jobs.</param>
        /// <param name="world">Voxel world that raises events when chunk data is ready.</param>
        internal ChunkScheduler(
            VoxelEngineSettings settings,
            ChunkManager chunkManager,
            GeneratorConfig config,
            VoxelWorld world
        ){
            _chunkManager = chunkManager;
            _config = config;
            _world = world;
            if (config.WorldGen.Generator == WorldGenerator.Density){
                _graph = CompileDensityGraph(out _terrain, out _generatedCode);
                try{
                    CheckLayout(settings.WorldGen, config.WorldGen, _graph);
                    if (config.WorldGen.Layout.HasBoxes && settings.WorldGen != null)
                        _config.WorldGen.SurfaceRules = CompileSurfaceRules(settings.WorldGen, config.WorldGen, _graph);
                }
                catch{
                    _graph.Dispose();
                    throw;
                }
            }
            _jobs = new NativeList<int2>(Allocator.Domain);
            _results = new NativeParallelHashMap<int2, ChunkVoxelData>(
                settings.Chunk.LoadDistance.SquareSize(),
                Allocator.Domain
            );
        }


        /// <summary>The shipped noise graph with its terrain outputs; throws when it has errors or no terrain.</summary>
        private static CompiledNoiseGraph CompileDensityGraph(out DensityTerrainGraph terrain, out bool generatedCode){
            GraphCompileResult result = ShippedNoiseGraph.Compile();
            if (!result.Succeeded)
                throw new InvalidOperationException("The noise graph has errors: " + string.Join("; ", result.Errors));
            CompiledNoiseGraph graph = result.Graph;
            if (!DensityTerrainGraph.TryCreate(graph, out terrain, out string error)){
                graph.Dispose();
                throw new InvalidOperationException(error);
            }

            generatedCode = GraphEvaluator.HasGeneratedCode(graph);
            return graph;
        }

        /// <summary>
        ///     The checks of WorldGen v2 §6.6 and §12 before the first chunk: the merged biome layout must have no
        ///     validator errors (with ε and the parameter range from the graph), and the blend kernel must fit in the
        ///     biome padding. Throws with every error.
        /// </summary>
        private static void CheckLayout(WorldGenSettings settings, in WorldGenConfig worldGen, CompiledNoiseGraph graph){
            if (!worldGen.Layout.HasBoxes || settings == null) return;
            LayoutValidation validation = BiomeLayoutValidator.Validate(BiomeLayout.FromBiomeSet(settings.Biomes), graph);
            if (validation.HasErrors) throw new InvalidOperationException("The biome layout has errors:\n" + validation.ErrorText());
            string blendError = BlendWidthError(worldGen.MaterialMethod, worldGen.MaterialKernel.Reach, settings.MaterialBlendWidth);
            if (blendError != null) throw new InvalidOperationException(blendError);
        }

        /// <summary>
        ///     The shipped surface rules (§7) for the BiomeSet's biomes, with block ids from the voxel registry (none in
        ///     tests without one: named blocks then drop their rules). Throws with every validator error.
        /// </summary>
        private static SurfaceRules CompileSurfaceRules(WorldGenSettings settings, in WorldGenConfig worldGen, CompiledNoiseGraph graph){
            VoxelConfig.Registry.VoxelRegistry registry = VoxelConfig.DataImporter.Instance != null ? VoxelConfig.DataImporter.Instance.VoxelRegistry : null;
            Func<string, int> blockByName = registry == null ? null : name => name.Length <= FixedString32Bytes.UTF8MaxLengthInBytes && registry.TryGetId(new FixedString32Bytes(name), out ushort id) ? id : -1;
            using SurfaceRuleCompileResult result = SurfaceRuleCompiler.CompileShipped(BiomeLayout.FromBiomeSet(settings.Biomes), worldGen.Layout.Defs.ToArray(), graph,
                blockByName, Allocator.Persistent);
            if (!result.Succeeded) throw new InvalidOperationException("The surface rules have errors:\n" + result.ErrorText());
            return result.TakeRules();
        }

        /// <summary>
        ///     The refusal of a blend kernel that reaches past the biome padding; null when the kernel fits or the
        ///     method has no kernel.
        /// </summary>
        internal static string BlendWidthError(BlendMethod method, int reach, float width){
            if (method != BlendMethod.DistanceBlend || reach <= DensityLayout.BiomePadding - 1) return null;
            return $"MaterialBlendWidth {width} reaches {reach} columns; the biome padding allows " +
                   $"{DensityLayout.BiomePadding - 1} (a width of at most 32).";
        }

        /// <summary>The world-generation config the chunk jobs read; released with this scheduler.</summary>
        internal WorldGenConfig WorldGen => _config.WorldGen;

        /// <summary>The compiled noise graph of the density generator; null for the other generators.</summary>
        internal CompiledNoiseGraph Graph => _graph;

        /// <summary>
        ///     Gets a value indicating whether the scheduled job has completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        ///     Starts a Burst-compiled chunk generation job for the given list of chunk world positions.
        /// </summary>
        /// <param name="jobs">List of chunk world positions to generate.</param>
        internal void Start(HashSet<int2> jobs){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.DataGenSchedule.Auto();
            StartRecord();
            IsReady = false;
            foreach (int2 j in jobs) _jobs.Add(j);

            if (_graph != null){
                _handle = _generatedCode ? ScheduleDensity<ShippedNoiseGraphCode>() : ScheduleDensity<InterpretedPrograms>();
                return;
            }

            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                Config = _config
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        private JobHandle ScheduleDensity<TPrograms>() where TPrograms : struct, IGraphPrograms{
            DensityChunkJob<TPrograms> job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                Config = _config,
                Terrain = _terrain
            };
            return job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        ///     Waits for the job to finish and moves its chunks into the chunk store, in the order of the batch.
        ///     Items of work for <paramref name="budget" />, per chunk of the batch: while the store is full, the
        ///     wait for the jobs that read the chunk it evicts next (the collider mesh job, the light job with the
        ///     apply of its batch, the collider bake while the pool renders the chunk) and then the eviction itself;
        ///     then adding the chunk with its upload (the world hands its voxel data to the renderer). When the
        ///     budget runs out in between, the next call continues at the item it stopped before. No other stage
        ///     runs until the batch is in (<see cref="StageRing" />), so no job starts that reads a chunk between
        ///     its wait and its eviction. Under a frame budget the wait never blocks: while a reader still runs
        ///     (<see cref="EvictionReadersRunning" />) the call returns false without an item, and the next frame
        ///     looks again.
        /// </summary>
        /// <param name="budget">The frame's budget; null means no limit.</param>
        /// <returns>
        ///     True when the batch is applied; false when the budget cut the call or a reader of the next eviction
        ///     still runs.
        /// </returns>
        internal bool Complete(FrameBudget budget = null){
            budget ??= FrameBudget.Unlimited;
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.DataGenComplete.Auto();
            _handle.Complete();

            if (BatchAddInOneItem ? !AddBatchInOneItem(budget) : !AddChunks(budget)) return false;

            _jobs.Clear();
            _next = 0;
            _evictionReadersDone = false;
            _added = false;
            IsReady = true;
            StopRecord();
            return true;
        }

        /// <summary>
        ///     Adds the batch chunk by chunk, from <see cref="_next" />: an item for the readers of the next eviction,
        ///     one for the eviction and one for the add with the upload. A chunk's data leaves the result map in the
        ///     item that adds it, so <see cref="Dispose" /> releases only the chunks that are not in the store yet.
        /// </summary>
        /// <returns>False when the budget cut the call or a reader of the next eviction still runs.</returns>
        private bool AddChunks(FrameBudget budget){
            for (; _next < _jobs.Length; _next++){
                int2 position = _jobs[_next];
                if (!_results.TryGetValue(position, out ChunkVoxelData data)) continue;

                // Checked again after a cut: an unload in the frames between makes the eviction unnecessary
                if (!_chunkManager.IsChunkLoaded(position) && _chunkManager.StoreFull){
                    if (!_evictionReadersDone){
                        if (budget.Limited && ReaderOfNextEvictionRuns()) return false;
                        if (!budget.Next()) return false;

                        using (VoxelProfilerMarkers.DataGenEvictWait.Auto()) _chunkManager.CompleteReadersOfNextEviction();
                        _evictionReadersDone = true;
                    }

                    if (!budget.Next()) return false;

                    // The focus may have changed the chunk to evict since its readers were completed; the unload
                    // completes the readers of the chunk it takes
                    using (VoxelProfilerMarkers.DataGenEvict.Auto()) _chunkManager.EvictNext();
                }

                _evictionReadersDone = false;
                if (!budget.Next()) return false;

                using (VoxelProfilerMarkers.DataGenAdd.Auto()){
                    _results.Remove(position); // the store owns the chunk data from here on
                    if (_chunkManager.AddChunk(position, data, out Data.Chunk chunk)) _world.RaiseChunkDataReady(chunk);
                }
            }

            _results.Clear();
            return true;
        }

        private bool ReaderOfNextEvictionRuns(){
            return EvictionReadersRunning != null && _chunkManager.TryGetNextEviction(out int2 next) &&
                   EvictionReadersRunning(next);
        }

        /// <summary>
        ///     The collect before 2026-11-18 (see <see cref="BatchAddInOneItem" />): the whole batch with its
        ///     evictions is one item, then the upload of every chunk is one item.
        /// </summary>
        /// <returns>False when the budget cut the call.</returns>
        private bool AddBatchInOneItem(FrameBudget budget){
            if (!_added){
                if (!budget.Next()) return false;

                _chunkManager.AddChunks(_results);
                _results.Clear(); // the store owns the chunk data from here on
                _added = true;
                _next = 0;
            }

            // A chunk that was unloaded again in the frames a cut call waited is skipped
            for (; _next < _jobs.Length; _next++){
                if (!budget.Next()) return false;

                if (_chunkManager.TryGetChunk(_jobs[_next], out Data.Chunk chunk)) _world.RaiseChunkDataReady(chunk);
            }

            return true;
        }

        /// <summary>
        ///     Waits for the running job without applying its results; <see cref="Complete" /> still applies them.
        /// </summary>
        internal void CompleteJob(){
            _handle.Complete();
        }

        /// <summary>
        ///     Disposes all native containers after ensuring that the scheduled job has completed; chunk data of a
        ///     batch that was never completed is released.
        /// </summary>
        internal void Dispose(){
            _handle.Complete();

            foreach (KeyValue<int2, ChunkVoxelData> r in _results) r.Value.Dispose();
            _jobs.Dispose();
            _results.Dispose();
            _config.Dispose(); // Releases the noise parameters created for this scheduler's config
            _graph?.Dispose();
        }
    }
}
