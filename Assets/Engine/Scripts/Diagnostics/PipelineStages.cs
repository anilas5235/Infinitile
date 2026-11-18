using System;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Render;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using Unity.Mathematics;

namespace Engine.Scripts.Diagnostics{
    /// <summary>
    ///     The four stages of the chunk pipeline (data, light, mesh, collider bake) as a measurement reads them, and
    ///     when the world around the focus is built: every stage sleeps with its job collected after the stages
    ///     before it, every chunk of the load window is loaded, and no partition waits for its GPU build. Sample it
    ///     once per frame. Reads the engine without the profiler, so it works in a release player.
    /// </summary>
    internal sealed class PipelineStages{
        internal const int Data = 0;
        internal const int Light = 1;
        internal const int Mesh = 2;
        internal const int Bake = 3;
        internal const int Count = 4;
        internal static readonly string[] Names = { "data", "light", "mesh", "bake" };

        private readonly VoxelWorld _world;

        /// <summary>Per stage: the handler sleeps (nothing queued, nothing to rescan).</summary>
        internal readonly Func<bool>[] Sleeping = new Func<bool>[Count];

        /// <summary>Per stage: no batch is running or waiting to be collected.</summary>
        internal readonly Func<bool>[] JobReady = new Func<bool>[Count];

        /// <summary>Per stage: the running batch has finished (it may still wait to be collected).</summary>
        internal readonly Func<bool>[] JobComplete = new Func<bool>[Count];

        private readonly float[] _doneSeconds = { -1f, -1f, -1f, -1f };
        private readonly int[] _doneFrames = new int[Count];
        private float _gpuDoneSeconds = -1f;
        private int _gpuDoneFrame;

        internal PipelineStages(VoxelWorld world){
            _world = world;
            VoxelEngineScheduler scheduler = world.Scheduler;
            Sleeping[Data] = () => scheduler.DataHandler.Sleeping;
            Sleeping[Light] = () => scheduler.LightHandler.Sleeping;
            Sleeping[Mesh] = () => scheduler.MeshHandler.Sleeping;
            Sleeping[Bake] = () => scheduler.ColliderHandler.Sleeping;
            JobReady[Data] = () => scheduler.DataJobs.IsReady;
            JobReady[Light] = () => scheduler.LightJobs.IsReady;
            JobReady[Mesh] = () => scheduler.MeshJobs.IsReady;
            JobReady[Bake] = () => scheduler.BakeJobs.IsReady;
            JobComplete[Data] = () => scheduler.DataJobs.IsComplete;
            JobComplete[Light] = () => scheduler.LightJobs.IsComplete;
            JobComplete[Mesh] = () => scheduler.MeshJobs.IsComplete;
            JobComplete[Bake] = () => scheduler.BakeJobs.IsComplete;
        }

        /// <summary>Partitions that wait for their GPU build in the renderer.</summary>
        internal static int PendingBuilds{
            get{
                VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
                return renderer ? renderer.PendingBuildCount : 0;
            }
        }

        /// <summary>Seconds of the stage's completion since the last restart; -1 while it did not happen.</summary>
        internal float DoneSeconds(int stage){
            return _doneSeconds[stage];
        }

        internal int DoneFrame(int stage){
            return _doneFrames[stage];
        }

        /// <summary>When the last GPU build finished after the mesh stage was done; -1 while that did not happen.</summary>
        internal float GpuDoneSeconds => _gpuDoneSeconds;

        internal int GpuDoneFrame => _gpuDoneFrame;

        /// <summary>Seconds until every stage slept with its upstream stages done; -1 while that did not happen.</summary>
        internal float BuiltSeconds => _doneSeconds[Bake];

        internal int BuiltFrame => _doneFrames[Bake];

        /// <summary>True once every stage slept after its upstream stages and no GPU build is pending.</summary>
        internal bool Built => _doneSeconds[Bake] >= 0f && _gpuDoneSeconds >= 0f;

        /// <summary>True when the data, mesh and bake handlers sleep, i.e. no stage has work left to look for.</summary>
        internal bool Idle => Sleeping[Data]() && Sleeping[Mesh]() && Sleeping[Bake]();

        /// <summary>
        ///     Records the completion of every stage that is done now, at <paramref name="seconds" /> and frame
        ///     <paramref name="frame" /> of the caller's clock. Call once per frame.
        /// </summary>
        internal void SampleCompletion(float seconds, int frame){
            bool upstreamDone = _world.Stats.LoadedChunks >= _world.Settings.Chunk.LoadDistance.SquareSize();
            for (int i = 0; i < Count; i++){
                upstreamDone &= Sleeping[i]() && JobReady[i]();
                if (!upstreamDone || _doneSeconds[i] >= 0f) continue;
                _doneSeconds[i] = seconds;
                _doneFrames[i] = frame;
            }

            if (_doneSeconds[Mesh] >= 0f && _gpuDoneSeconds < 0f && PendingBuilds == 0){
                _gpuDoneSeconds = seconds;
                _gpuDoneFrame = frame;
            }
        }

        /// <summary>Forgets the completion times, so a second phase (the world settling after a flight) is timed anew.</summary>
        internal void RestartCompletion(){
            for (int i = 0; i < Count; i++) _doneSeconds[i] = -1f;
            _gpuDoneSeconds = -1f;
        }

        /// <summary>
        ///     Share of the partitions in the columns within <paramref name="radius" /> chunks of the focus that are
        ///     active (meshed, or without a voxel and so needing no mesh).
        /// </summary>
        internal float Coverage(int radius){
            int3 focus = _world.Focus.Pos;
            ChunkPool pool = _world.Pool;
            int meshed = 0, total = 0;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++){
                total++;
                if (pool.IsPartitionActive(new int3(focus.x + x, y, focus.z + z))) meshed++;
            }

            return meshed / (float)total;
        }

        /// <summary>
        ///     Share of the partitions in the columns within <paramref name="radius" /> chunks of the focus that are
        ///     collidable (collider baked, or known to need none).
        /// </summary>
        internal float CollidableShare(int radius){
            int3 focus = _world.Focus.Pos;
            ChunkPool pool = _world.Pool;
            int collidable = 0, total = 0;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++){
                total++;
                if (pool.IsCollidable(new int3(focus.x + x, y, focus.z + z))) collidable++;
            }

            return collidable / (float)total;
        }
    }
}
