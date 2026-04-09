using Runtime.Engine.Components;
using Runtime.Engine.Jobs.Chunk;
using Runtime.Engine.Jobs.ColliderBake;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Settings;
using Unity.Mathematics;

namespace Runtime.Engine.Jobs
{
    /// <summary>
    /// Zentraler Scheduler der Daten-, Mesh- und Collider-Jobs als Round-Robin State Machine orchestriert.
    /// Verwaltet separate Prioritäts-Queues und wählt Batches nach Konfiguration (<see cref="SchedulerSettings"/>).
    /// </summary>
    public class VoxelEngineScheduler
    {
        private readonly ChunkScheduler _chunkScheduler;
        private readonly ColliderBakeScheduler _colliderBakeScheduler;
        private readonly MeshBuildScheduler _meshBuildScheduler;

        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;

        private readonly DataJobStateHandler _dataJobHandler;
        private readonly MeshJobStateHandler _meshJobHandler;
        private readonly ColliderJobStateHandler _colliderJobHandler;

        private SchedulerUpdate _currentUpdate;

        /// <summary>
        /// Erstellt neuen Scheduler und initialisiert alle Queues / Sets.
        /// </summary>
        internal VoxelEngineScheduler(VoxelEngineSettings settings,
            MeshBuildScheduler meshBuildScheduler,
            ChunkScheduler chunkScheduler,
            ChunkManager chunkManager,
            ColliderBakeScheduler colliderBakeScheduler,
            ChunkPool chunkPool)
        {
            _meshBuildScheduler = meshBuildScheduler;
            _chunkScheduler = chunkScheduler;
            _colliderBakeScheduler = colliderBakeScheduler;
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;

            _colliderJobHandler = new ColliderJobStateHandler(settings, chunkManager, chunkPool, colliderBakeScheduler);
            _meshJobHandler = new MeshJobStateHandler(settings, chunkManager, chunkPool, meshBuildScheduler,
                _colliderJobHandler);
            _dataJobHandler =
                new DataJobStateHandler(settings, chunkManager, chunkPool, chunkScheduler, _meshJobHandler);

            _currentUpdate = SchedulerUpdate.Data;

            _chunkManager.OnChunkRemeshRequested += OnRemesh;
        }

        /// <summary>
        /// Ausführung eines Scheduler-Ticks. Führt abhängig vom aktuellen Schritt Queue-Updates, Job-Vergaben oder Result-Sammlung aus.
        /// </summary>
        /// <param name="focus">Fokusposition (z.B. Spieler Chunk Root).</param>
        internal void ScheduleUpdate(int3 focus)
        {
            switch (_currentUpdate)
            {
                case SchedulerUpdate.Data:
                    _dataJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Mesh:
                    _meshJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Collider:
                    _colliderJobHandler.Update(focus);
                    break;
            }

            _currentUpdate = (SchedulerUpdate)(((byte)_currentUpdate + 1) % 3);
        }

        private enum SchedulerUpdate : byte
        {
            Data,
            Mesh,
            Collider
        }

        /// <summary>
        /// Aktualisiert Prioritäten aller Queues und delegiert Fokus an Manager/Pool.
        /// </summary>
        internal void FocusUpdate(int3 focus)
        {
            _dataJobHandler.FocusUpdate(focus);
            _meshJobHandler.FocusUpdate(focus);
            _colliderJobHandler.FocusUpdate(focus);
            _chunkManager.FocusUpdate(focus);
            _chunkPool.FocusUpdate(focus);
        }

        private void OnRemesh()
        {
            _meshJobHandler.WakeUp();
        }

        /// <summary>
        /// Räumt Sub-Scheduler Ressourcen auf.
        /// </summary>
        internal void Dispose()
        {
            _chunkScheduler.Dispose();
            _meshBuildScheduler.Dispose();
            _colliderBakeScheduler.Dispose();

            if (_chunkManager != null) _chunkManager.OnChunkRemeshRequested -= OnRemesh;
        }

        #region RuntimeStatsAPI

        /// <summary>
        /// Durchschnittliche Laufzeit von Datenjobs.
        /// </summary>
        public float DataAvgTiming => _chunkScheduler.AvgTime;

        /// <summary>
        /// Durchschnittliche Laufzeit von Meshjobs.
        /// </summary>
        public float MeshAvgTiming => _meshBuildScheduler.AvgTime;

        /// <summary>
        /// Anzahl Chunks in Daten-Queue.
        /// </summary>
        public int DataQueueCount => _dataJobHandler.QueueCount;

        /// <summary>
        /// Anzahl Chunks in Mesh-Queue.
        /// </summary>
        public int MeshQueueCount => _meshJobHandler.QueueCount;

        /// <summary>
        /// Anzahl Chunks in Collider-Queue.
        /// </summary>
        public int BakeQueueCount => _colliderJobHandler.QueueCount;

        #endregion
    }
}