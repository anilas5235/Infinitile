using Unity.Profiling;

namespace Engine.Scripts.Utils{
    /// <summary>
    ///     One <see cref="ProfilerMarker" /> per pipeline stage. All names start with "Voxel." so the
    ///     stages can be filtered in the Profiler.
    /// </summary>
    internal static class VoxelProfilerMarkers{
        // Scheduler ticks (VoxelEngineScheduler round robin)
        internal static readonly ProfilerMarker SchedulerData = new("Voxel.Scheduler.Data");
        internal static readonly ProfilerMarker SchedulerLight = new("Voxel.Scheduler.Light");
        internal static readonly ProfilerMarker SchedulerMesh = new("Voxel.Scheduler.Mesh");
        internal static readonly ProfilerMarker SchedulerCollider = new("Voxel.Scheduler.Collider");

        // Data generation (ChunkScheduler / ChunkJob)
        internal static readonly ProfilerMarker DataGenSchedule = new("Voxel.DataGen.Schedule");
        internal static readonly ProfilerMarker DataGenComplete = new("Voxel.DataGen.Complete");

        // The items of the data collect, inside DataGenComplete: per chunk of the batch the wait for the jobs that
        // read the chunk the store evicts next, its eviction, and the add with the upload to the renderer
        internal static readonly ProfilerMarker DataGenEvictWait = new("Voxel.DataGen.EvictWait");
        internal static readonly ProfilerMarker DataGenEvict = new("Voxel.DataGen.Evict");
        internal static readonly ProfilerMarker DataGenAdd = new("Voxel.DataGen.Add");

        // Light (LightScheduler / LightJob / LightEditJob)
        internal static readonly ProfilerMarker LightSchedule = new("Voxel.Light.Schedule");
        internal static readonly ProfilerMarker LightComplete = new("Voxel.Light.Complete");
        internal static readonly ProfilerMarker LightEdit = new("Voxel.Light.Edit");

        // Collider mesh build (CMeshBuildScheduler / CMeshBuildJob)
        internal static readonly ProfilerMarker MeshBuildSchedule = new("Voxel.MeshBuild.Schedule");
        internal static readonly ProfilerMarker MeshBuildComplete = new("Voxel.MeshBuild.Complete");

        // Collider bake (ColliderBakeScheduler / ColliderBuildJob)
        internal static readonly ProfilerMarker ColliderBakeSchedule = new("Voxel.ColliderBake.Schedule");
        internal static readonly ProfilerMarker ColliderBakeComplete = new("Voxel.ColliderBake.Complete");

        // GPU side (VoxelWorldRenderer and handlers)
        internal static readonly ProfilerMarker GpuUploadChunk = new("Voxel.GpuUpload.ChunkData");
        internal static readonly ProfilerMarker PointBuildDispatch = new("Voxel.PointBuild.Dispatch");
        internal static readonly ProfilerMarker PointBuildCopy = new("Voxel.PointBuild.Copy");
        internal static readonly ProfilerMarker RenderRebuildBuffers = new("Voxel.Render.RebuildBuffers");
        internal static readonly ProfilerMarker RenderCulling = new("Voxel.Render.Culling");
        internal static readonly ProfilerMarker RenderDraw = new("Voxel.Render.Draw");
        internal static readonly ProfilerMarker PointBuildComplete = new("Voxel.PointBuild.Complete");
        internal static readonly ProfilerMarker RenderRelease = new("Voxel.Render.Release");
        internal static readonly ProfilerMarker BiomeTintUpload = new("Voxel.GpuUpload.BiomeTint");

        // The three steps of a stage's turn (JobStateHandler.Update), whatever the stage
        internal static readonly ProfilerMarker SchedulerCollect = new("Voxel.Scheduler.Collect");
        internal static readonly ProfilerMarker SchedulerScan = new("Voxel.Scheduler.Scan");
        internal static readonly ProfilerMarker SchedulerDispatch = new("Voxel.Scheduler.Dispatch");

        // Focus update (VoxelEngineScheduler.FocusUpdate, every 0.5 s while the focus moves)
        internal static readonly ProfilerMarker FocusUpdate = new("Voxel.Focus.Update");
        internal static readonly ProfilerMarker FocusData = new("Voxel.Focus.Data");
        internal static readonly ProfilerMarker FocusLight = new("Voxel.Focus.Light");
        internal static readonly ProfilerMarker FocusMesh = new("Voxel.Focus.Mesh");
        internal static readonly ProfilerMarker FocusCollider = new("Voxel.Focus.Collider");
        internal static readonly ProfilerMarker FocusChunkStore = new("Voxel.Focus.ChunkStore");
        internal static readonly ProfilerMarker FocusPool = new("Voxel.Focus.Pool");

        // The main thread waits for a job that still runs, because data the job reads is about to change
        internal static readonly ProfilerMarker WaitColliderBake = new("Voxel.Wait.ColliderBake");
        internal static readonly ProfilerMarker WaitMeshBuild = new("Voxel.Wait.MeshBuild");
        internal static readonly ProfilerMarker WaitLight = new("Voxel.Wait.Light");

        // Eviction and reuse (ChunkManager, ChunkPool)
        internal static readonly ProfilerMarker ChunkUnload = new("Voxel.Chunk.Unload");
        internal static readonly ProfilerMarker PoolClaimPartition = new("Voxel.Pool.ClaimPartition");
        internal static readonly ProfilerMarker PoolReclaimChunk = new("Voxel.Pool.ReclaimChunk");
        internal static readonly ProfilerMarker PoolActivateChunk = new("Voxel.Pool.ActivateChunk");
        internal static readonly ProfilerMarker PoolDeactivateChunk = new("Voxel.Pool.DeactivateChunk");
        internal static readonly ProfilerMarker PartitionClear = new("Voxel.Partition.Clear");

        // Parts of the mesh batch apply, and the collider assignment of a partition
        internal static readonly ProfilerMarker MeshBuildClaim = new("Voxel.MeshBuild.Claim");
        internal static readonly ProfilerMarker MeshBuildApply = new("Voxel.MeshBuild.Apply");

        // The start of every mesh collect (also of a resumed one): Complete of the mesh job and the wait for a
        // running collider bake, before the first claim. The same bake wait inside a pool claim is not in here
        internal static readonly ProfilerMarker MeshBuildWaitBake = new("Voxel.MeshBuild.WaitBake");

        // The dispatch of the mesh stage taking its partitions from the queue, before the claims of the empty
        // partitions and the start of the mesh job
        internal static readonly ProfilerMarker MeshBuildDequeue = new("Voxel.MeshBuild.Dequeue");
        internal static readonly ProfilerMarker ColliderAssign = new("Voxel.Collider.Assign");
    }
}
