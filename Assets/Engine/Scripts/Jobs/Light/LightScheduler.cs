using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Light{
    /// <summary>
    ///     Schedules <see cref="LightJob" /> batches for chunks whose 3 × 3 neighbourhood has voxel data and
    ///     applies the results to the chunks (<see cref="Data.Chunk.LightReady" />). Runs the incremental
    ///     <see cref="LightEditJob" /> synchronously after each voxel edit.
    /// </summary>
    internal sealed class LightScheduler : JobScheduler{
        private readonly ChunkManager _chunkManager;
        private readonly VoxelRegistry _voxelRegistry;
        private readonly VoxelWorld _world;

        /// <summary>Indicates whether the scheduler is ready to start a new batch.</summary>
        internal bool IsReady = true;

        private JobHandle _handle;
        private NativeList<int2> _jobs;
        private NativeParallelHashMap<int2, ChunkLightData> _results;

        // Reused per edit
        private NativeArray<ChunkVoxelData> _editVoxels;
        private NativeArray<ChunkLightData> _editLights;
        private NativeArray<byte> _editChangedPartitions;
        private readonly Data.Chunk[] _editChunks = new Data.Chunk[LightEditJob.ChunkCount];

        internal LightScheduler(VoxelEngineSettings settings, ChunkManager chunkManager, VoxelRegistry voxelRegistry,
            VoxelWorld world){
            _chunkManager = chunkManager;
            _voxelRegistry = voxelRegistry;
            _world = world;
            int batch = math.max(1, settings.Scheduler.lightBatchSize);
            _jobs = new NativeList<int2>(batch, Allocator.Domain);
            _results = new NativeParallelHashMap<int2, ChunkLightData>(batch, Allocator.Domain);
            _editVoxels = new NativeArray<ChunkVoxelData>(LightEditJob.ChunkCount, Allocator.Domain);
            _editLights = new NativeArray<ChunkLightData>(LightEditJob.ChunkCount, Allocator.Domain);
            _editChangedPartitions = new NativeArray<byte>(LightEditJob.ChunkCount * PartitionsPerChunk, Allocator.Domain);

            // The job reads the voxel data of the batch's 3 × 3 neighbourhoods
            _chunkManager.LightJobReading += CompleteIfRunning;
            _chunkManager.VoxelEdited += OnVoxelEdited;
        }

        /// <summary>Gets a value indicating whether the scheduled batch has completed.</summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>Starts a Burst light job for the given chunks (all 3 × 3 neighbourhoods must be loaded).</summary>
        internal void Start(HashSet<int2> jobs){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.LightSchedule.Auto();
            StartRecord();
            IsReady = false;
            foreach (int2 j in jobs) _jobs.Add(j);
            if (_results.Capacity < _jobs.Length) _results.Capacity = _jobs.Length;

            LightJob job = new(){
                Jobs = _jobs,
                Chunks = _chunkManager.GetLightJobChunks(jobs),
                Table = _voxelRegistry.GetLightTable(),
                Results = _results.AsParallelWriter()
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        ///     Waits for the batch and hands the light to the chunks (a chunk unloaded meanwhile drops its result).
        ///     Applying only assigns light data and raises <see cref="VoxelWorld.ChunkLightChanged" />, so it is safe
        ///     to run from the <see cref="CompleteIfRunning" /> callback as well.
        /// </summary>
        internal void Complete(){
            if (IsReady) return;
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.LightComplete.Auto();
            _handle.Complete();
            IsReady = true; // before applying: listeners may trigger CompleteIfRunning again

            for (int i = 0; i < _jobs.Length; i++){
                int2 pos = _jobs[i];
                if (!_results.TryGetValue(pos, out ChunkLightData light)) continue;
                if (_chunkManager.ApplyLight(pos, light, out Data.Chunk chunk)) _world.RaiseChunkLightChanged(chunk);
            }

            _jobs.Clear();
            _results.Clear();
            StopRecord();
        }

        /// <summary>
        ///     Completes and applies the running batch before the chunk manager writes or disposes voxel data
        ///     the job reads. Applying here keeps light and voxel data consistent: an edit right after this sees
        ///     the light of the pre-edit data and updates it incrementally.
        /// </summary>
        internal void CompleteIfRunning(){
            Complete();
        }

        /// <summary>
        ///     Incremental update after <see cref="ChunkManager.SetVoxel" /> changed a voxel. Needs the 3 × 3
        ///     chunks around the edit lit; otherwise the lit ones are invalidated and re-lit by the light job.
        /// </summary>
        private void OnVoxelEdited(int3 position, ushort oldVoxel){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.LightEdit.Auto();
            int2 center = VoxelUtils.GetChunkCoords(position);
            bool allLit = true;

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int k = LightEditJob.ChunkIndex(new int2(dx, dz));
                _chunkManager.TryGetChunk(center + new int2(dx, dz), out _editChunks[k]);
                allLit &= _editChunks[k] != null && _editChunks[k].LightReady;
            }

            if (!allLit){
                for (int k = 0; k < LightEditJob.ChunkCount; k++)
                    if (_editChunks[k] != null)
                        _chunkManager.InvalidateLight(_editChunks[k].Position);
                System.Array.Clear(_editChunks, 0, _editChunks.Length);
                return;
            }

            for (int k = 0; k < LightEditJob.ChunkCount; k++){
                _chunkManager.CompleteReadersOf(_editChunks[k].Position); // light lists may be replaced
                _editVoxels[k] = _editChunks[k].VoxelData;
                _editLights[k] = _editChunks[k].LightData;
            }

            new LightEditJob{
                Table = _voxelRegistry.GetLightTable(),
                Voxels = _editVoxels,
                Lights = _editLights,
                CenterChunk = center,
                Position = position,
                OldVoxel = oldVoxel,
                ChangedPartitions = _editChangedPartitions
            }.Run();

            for (int k = 0; k < LightEditJob.ChunkCount; k++){
                Data.Chunk chunk = _editChunks[k];
                chunk.LightData = _editLights[k];
                bool changed = false;
                for (int y = 0; y < PartitionsPerChunk; y++){
                    if (_editChangedPartitions[k * PartitionsPerChunk + y] == 0) continue;
                    _chunkManager.FlagReMesh(new int3(chunk.Position.x, y, chunk.Position.y));
                    changed = true;
                }

                if (changed) _world.RaiseChunkLightChanged(chunk);
                _editChunks[k] = null;
            }
        }

        /// <summary>Waits for the running job without applying its results (registries are about to be released).</summary>
        internal void CompleteJob(){
            _handle.Complete();
        }

        /// <summary>Disposes all native containers; results of a batch that was never applied are released.</summary>
        internal void Dispose(){
            _chunkManager.LightJobReading -= CompleteIfRunning;
            _chunkManager.VoxelEdited -= OnVoxelEdited;
            _handle.Complete();

            foreach (KeyValue<int2, ChunkLightData> r in _results) r.Value.Dispose();
            _jobs.Dispose();
            _results.Dispose();
            _editVoxels.Dispose();
            _editLights.Dispose();
            _editChangedPartitions.Dispose();
        }
    }
}
