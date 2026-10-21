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
    }
}
