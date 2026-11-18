using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     The data collect under a budget that cuts it (<see cref="ChunkScheduler.Complete" />): every eviction
    ///     and every add with its upload is an item of work, and a cut call continues where it stopped. The rig
    ///     has its own chunk store and data scheduler and generates one real batch; a budget that counts items
    ///     instead of time cuts it after every item. The light collect (<see cref="LightScheduler.Complete" />)
    ///     is cut the same way, one item per chunk's light.
    /// </summary>
    public class DataCollectCutTests{
        private const int LoadDistance = 2;
        private const int StoreSize = (LoadDistance + 2) * 2 + 1; // a side of the store, which holds StoreSize²
        private const int BatchSize = 8;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        /// <summary>A chunk store, a data scheduler and an inactive world as their event hub.</summary>
        private sealed class DataRig : System.IDisposable{
            public readonly ChunkManager Manager;
            public readonly ChunkScheduler Data;

            /// <summary>Every chunk handed to the renderer (the upload), in order.</summary>
            public readonly List<int2> Uploaded = new();

            /// <summary>Every chunk the store unloaded, in order.</summary>
            public readonly List<int2> Unloaded = new();

            /// <summary>Every chunk whose light a light scheduler applied, in order.</summary>
            public readonly List<int2> Lit = new();

            /// <summary>The batch: a row next to the focus, so the store never evicts its chunks.</summary>
            public readonly List<int2> Batch = new();

            private readonly GameObject _worldObject;
            private readonly VoxelWorld _world;
            private readonly VoxelEngineSettings _settings;
            private bool _dataDisposed;

            public DataRig(){
                _settings = PlayModeFixtures.CreateSettings(1, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
                _settings.Chunk.LoadDistanceOverride = LoadDistance;
                _worldObject = new GameObject("DataCollectCutTests.World");
                _worldObject.SetActive(false); // only the event hub is used; Awake must not run
                _world = _worldObject.AddComponent<VoxelWorld>();
                _world.ChunkDataReady += OnDataReady;
                _world.ChunkLightChanged += OnLightChanged;

                Manager = new ChunkManager(_settings);
                Manager.OnChunkUnloaded += Unloaded.Add;
                Manager.FocusUpdate(new PriorityUtil.Focus(int3.zero, new float3(0, 0, 1)));
                Data = VoxelEngineProvider.Current.ChunkDataScheduler(Manager, DataImporter.Instance.CreateConfig(),
                    _world);
                for (int i = 0; i < BatchSize; i++) Batch.Add(new int2(i - BatchSize / 2, 0));
            }

            private void OnDataReady(Chunk chunk){
                Uploaded.Add(chunk.Position);
            }

            private void OnLightChanged(Chunk chunk){
                Lit.Add(chunk.Position);
            }

            /// <summary>A light scheduler on the rig's store; the caller disposes it before the rig.</summary>
            public LightScheduler CreateLight(){
                return VoxelEngineProvider.Current.LightScheduler(Manager, DataImporter.Instance.VoxelRegistry, _world);
            }

            /// <summary>Loads air chunks from <paramref name="min" /> to <paramref name="max" /> (inclusive).</summary>
            public void LoadAir(int2 min, int2 max){
                NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(64, Allocator.Temp);
                for (int x = min.x; x <= max.x; x++)
                for (int z = min.y; z <= max.y; z++)
                    chunks.Add(new int2(x, z), PlayModeFixtures.CreateUniformChunk(PlayModeFixtures.Air));
                Manager.AddChunks(chunks); // the manager owns the chunk data from here on
                chunks.Dispose();
            }

            /// <summary>Fills the store with air chunks far from the focus, so every chunk of the batch evicts one.</summary>
            public void FillStore(){
                int half = StoreSize / 2;
                NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(StoreSize * StoreSize, Allocator.Temp);
                for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                    chunks.Add(new int2(x, 30 + z), PlayModeFixtures.CreateUniformChunk(PlayModeFixtures.Air));
                Manager.AddChunks(chunks); // the manager owns the chunk data from here on
                chunks.Dispose();
                Assert.IsTrue(Manager.StoreFull, "the store must be full before the batch");
            }

            /// <summary>Starts the batch and waits for its job.</summary>
            public IEnumerator Generate(){
                Data.Start(new HashSet<int2>(Batch));
                float deadline = Time.realtimeSinceStartup + 60f;
                while (!Data.IsComplete){
                    if (Time.realtimeSinceStartup > deadline) Assert.Fail("the data job did not finish in time");
                    yield return null;
                }
            }

            /// <summary>One frame of the collect under <paramref name="budget" />; true when the batch is in.</summary>
            public bool Collect(FrameBudget budget){
                budget.BeginFrame();
                return Data.Complete(budget);
            }

            public int LoadedOfBatch(){
                int loaded = 0;
                foreach (int2 position in Batch)
                    if (Manager.IsChunkLoaded(position)) loaded++;
                return loaded;
            }

            /// <summary>Releases the scheduler with the chunks of the batch it still holds, before the store.</summary>
            public void DisposeData(){
                if (_dataDisposed) return;
                _dataDisposed = true;
                Data.Dispose();
            }

            public void Dispose(){
                _world.ChunkDataReady -= OnDataReady;
                _world.ChunkLightChanged -= OnLightChanged;
                DisposeData();
                Manager.Dispose();
                Object.Destroy(_worldObject);
                Object.Destroy(_settings);
            }
        }

        /// <inheritdoc cref="EmptyPartitionTests" />
        private static FrameBudget ItemsPerFrame(int items){
            long asked = 0;
            return new FrameBudget(items, () => asked++, 1d);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ADataCollectTheBudgetCuts_AddsOneChunkPerItem(){
            // The store has room: every chunk is one item (the add with its upload). One item per frame adds one
            // chunk per call; the batch is in after one call per chunk, every chunk added and uploaded once.
            using DataRig rig = new();
            yield return rig.Generate();
            FrameBudget budget = ItemsPerFrame(1);

            for (int call = 1; call <= BatchSize; call++){
                bool done = rig.Collect(budget);
                Assert.AreEqual(call, rig.LoadedOfBatch(), $"chunks of the batch in the store after call {call}");
                Assert.AreEqual(call, rig.Uploaded.Count, $"uploads after call {call}");
                Assert.AreEqual(call == BatchSize, done, $"call {call} ends the batch");
                Assert.AreEqual(done, rig.Data.IsReady);
            }

            CollectionAssert.AreEquivalent(rig.Batch, rig.Uploaded, "every chunk of the batch is uploaded once");
            Assert.AreEqual(0, rig.Unloaded.Count, "a store with room evicts nothing");
            Assert.AreEqual(BatchSize, rig.Manager.ChunkCount());
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ADataCollectCutInAnEviction_EvictsOncePerChunk(){
            // The store is full: every chunk is three items (the wait for the readers of the chunk to evict, the
            // eviction, the add). One item per frame stops between every two of them; the store never holds more
            // than it may, and every chunk of the batch evicts exactly one chunk.
            using DataRig rig = new();
            rig.FillStore();
            int capacity = rig.Manager.ChunkCount();
            yield return rig.Generate();
            FrameBudget budget = ItemsPerFrame(1);

            for (int chunk = 0; chunk < BatchSize; chunk++){
                Assert.IsFalse(rig.Collect(budget), $"chunk {chunk}: the wait for the readers is an item");
                Assert.AreEqual(chunk, rig.Unloaded.Count, $"chunk {chunk}: nothing evicted in the wait");
                Assert.AreEqual(chunk, rig.LoadedOfBatch());

                Assert.IsFalse(rig.Collect(budget), $"chunk {chunk}: the eviction is an item");
                Assert.AreEqual(chunk + 1, rig.Unloaded.Count, $"chunk {chunk}: one chunk evicted");
                Assert.AreEqual(chunk, rig.LoadedOfBatch(), $"chunk {chunk}: not added in the eviction's item");
                Assert.AreEqual(capacity - 1, rig.Manager.ChunkCount());

                bool done = rig.Collect(budget);
                Assert.AreEqual(chunk == BatchSize - 1, done, $"chunk {chunk}: the add ends the batch only at the last");
                Assert.AreEqual(chunk + 1, rig.Unloaded.Count, $"chunk {chunk}: the resumed call evicts no second chunk");
                Assert.AreEqual(chunk + 1, rig.LoadedOfBatch());
                Assert.AreEqual(capacity, rig.Manager.ChunkCount(), "the store stays at its size");
            }

            foreach (int2 position in rig.Unloaded)
                CollectionAssert.DoesNotContain(rig.Batch, position, "the store evicted a chunk of the batch");
            CollectionAssert.AreEquivalent(rig.Batch, rig.Uploaded);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AReaderOfTheNextEviction_EndsTheTurnWithoutAnItem(){
            // While a job that reads the chunk to evict still runs, the collect does not wait for it: the call
            // ends without an item (no frame is cut, nothing evicted or added) and asks again in the next frame.
            using DataRig rig = new();
            rig.FillStore();
            yield return rig.Generate();
            bool running = true;
            List<int2> asked = new();
            rig.Data.EvictionReadersRunning = position => {
                asked.Add(position);
                return running;
            };
            FrameBudget budget = ItemsPerFrame(1);

            for (int frame = 0; frame < 3; frame++){
                Assert.IsFalse(rig.Collect(budget), $"frame {frame}: a running reader ends the call");
                Assert.AreEqual(0, budget.FramesCut, $"frame {frame}: the call used no item");
                Assert.AreEqual(0, rig.Unloaded.Count, $"frame {frame}: nothing evicted");
                Assert.AreEqual(0, rig.LoadedOfBatch(), $"frame {frame}: nothing added");
            }

            Assert.AreEqual(3, asked.Count, "the collect asks once per frame");
            Assert.IsTrue(rig.Manager.TryGetNextEviction(out int2 next));
            Assert.AreEqual(next, asked[0], "it asks for the chunk the store evicts next");

            running = false;
            Assert.IsFalse(rig.Collect(budget), "the wait for the readers is an item again");
            Assert.AreEqual(1, budget.FramesCut, "the eviction after it is cut");
            Assert.IsFalse(rig.Collect(budget), "the eviction");
            Assert.AreEqual(next, rig.Unloaded[0], "the chunk asked for is evicted");

            int calls = 0;
            while (!rig.Collect(budget)) Assert.Less(++calls, 4 * BatchSize, "the collect does not end");
            Assert.AreEqual(BatchSize, rig.LoadedOfBatch());
            CollectionAssert.AreEquivalent(rig.Batch, rig.Uploaded);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AnUnloadDuringADataCollectCut_SkipsTheEviction(){
            // A chunk the store unloads between two calls (the pool reclaims one) makes room: the resumed call
            // adds without evicting, even when the wait for the readers of an eviction already ran.
            using DataRig rig = new();
            rig.FillStore();
            int capacity = rig.Manager.ChunkCount();
            yield return rig.Generate();
            FrameBudget budget = ItemsPerFrame(1);

            Assert.IsFalse(rig.Collect(budget), "the wait for the readers of the first eviction");
            Assert.IsTrue(rig.Manager.UnloadChunk(new int2(0, 30)), "the unload between the calls");
            Assert.IsFalse(rig.Collect(budget), "the add of the first chunk, without an eviction");
            Assert.AreEqual(1, rig.Unloaded.Count, "only the unload from outside");
            Assert.AreEqual(1, rig.LoadedOfBatch());

            int calls = 2;
            while (!rig.Collect(budget)){
                calls++;
                Assert.Less(calls, 4 * BatchSize, "the collect does not end");
                Assert.LessOrEqual(rig.Manager.ChunkCount(), capacity);
            }

            Assert.AreEqual(BatchSize, rig.Unloaded.Count, "seven evictions and the unload from outside");
            Assert.AreEqual(BatchSize, rig.LoadedOfBatch());
            Assert.AreEqual(capacity, rig.Manager.ChunkCount());
            CollectionAssert.AreEquivalent(rig.Batch, rig.Uploaded);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AnEditDuringADataCollectCut_KeepsTheAddedChunksEdit(){
            // Between two calls the player edits: a chunk the cut collect already added takes the edit and keeps
            // it, one it did not add yet refuses it like any chunk that is not loaded.
            using DataRig rig = new();
            yield return rig.Generate();
            FrameBudget budget = ItemsPerFrame(1);

            Assert.IsFalse(rig.Collect(budget));
            Assert.IsFalse(rig.Collect(budget));
            int2 added = default, pending = default;
            bool hasAdded = false, hasPending = false;
            foreach (int2 position in rig.Batch){
                if (rig.Manager.IsChunkLoaded(position)){
                    added = position;
                    hasAdded = true;
                }
                else{
                    pending = position;
                    hasPending = true;
                }
            }

            Assert.IsTrue(hasAdded && hasPending, "the cut leaves added and pending chunks");
            int3 inAdded = new(added.x * ChunkWidth + 3, ChunkHeight - 2, added.y * ChunkWidth + 5);
            int3 inPending = new(pending.x * ChunkWidth + 3, ChunkHeight - 2, pending.y * ChunkWidth + 5);
            ushort stone = (ushort)(rig.Manager.GetVoxel(inAdded) == 1 ? 2 : 1);
            Assert.IsTrue(rig.Manager.SetVoxel(stone, inAdded), "an edit in an added chunk is taken");
            Assert.IsFalse(rig.Manager.SetVoxel(stone, inPending), "an edit in a pending chunk is refused");

            while (!rig.Collect(budget)){ }

            Assert.AreEqual(stone, rig.Manager.GetVoxel(inAdded), "the rest of the batch keeps the edit");
            Assert.AreEqual(BatchSize, rig.LoadedOfBatch());
            CollectionAssert.AreEquivalent(rig.Batch, rig.Uploaded);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ADataCollectCut_FinishesUnlimitedOrIsDisposed(){
            // The world finishes a cut collect without a limit before it is rebuilt (CompleteRunning), and
            // disposes its scheduler during one when it shuts down: the first adds the rest, the second releases
            // only the chunks the store does not own (a double release would throw in the store's dispose).
            using (DataRig rig = new()){
                yield return rig.Generate();
                FrameBudget budget = ItemsPerFrame(1);
                Assert.IsFalse(rig.Collect(budget));
                Assert.IsFalse(rig.Collect(budget));
                Assert.IsTrue(rig.Data.Complete(), "a collect without a limit ends the batch");
                Assert.AreEqual(BatchSize, rig.LoadedOfBatch());
                CollectionAssert.AreEquivalent(rig.Batch, rig.Uploaded);
            }

            using (DataRig rig = new()){
                rig.FillStore();
                yield return rig.Generate();
                FrameBudget budget = ItemsPerFrame(1);
                for (int call = 0; call < 5; call++) Assert.IsFalse(rig.Collect(budget));
                Assert.AreEqual(1, rig.LoadedOfBatch(), "one chunk added, one evicted for the second");
                rig.DisposeData(); // the store's dispose (end of the using) releases the chunks it owns
            }
        }

        [Test]
        public void ALightCollectTheBudgetCuts_AppliesOneChunkPerItem(){
            // Every chunk's light is one item: one item per frame applies one chunk per call, and the batch is
            // applied after one call per chunk, every chunk lit once.
            using DataRig rig = new();
            rig.LoadAir(new int2(-1, -1), new int2(2, 2));
            HashSet<int2> jobs = new(){ new int2(0, 0), new int2(1, 0), new int2(0, 1), new int2(1, 1) };
            LightScheduler light = rig.CreateLight();
            try{
                light.Start(jobs);
                light.CompleteJob(); // the job has finished, as when the handler collects
                FrameBudget budget = ItemsPerFrame(1);

                for (int call = 1; call <= jobs.Count; call++){
                    budget.BeginFrame();
                    bool done = light.Complete(budget);
                    Assert.AreEqual(call, rig.Lit.Count, $"chunks lit after call {call}");
                    Assert.AreEqual(call == jobs.Count, done, $"call {call} ends the batch");
                    Assert.AreEqual(done, light.IsReady);
                }

                CollectionAssert.AreEquivalent(jobs, rig.Lit, "every chunk of the batch is lit once");
                foreach (int2 position in jobs)
                    Assert.IsTrue(rig.Manager.TryGetChunk(position, out Chunk chunk) && chunk.LightReady,
                        $"chunk {position} is lit");
            }
            finally{
                light.Dispose();
            }
        }

        [Test]
        public void AnEditDuringALightCollectCut_AppliesTheRestFirst(){
            // An edit in a chunk the batch read, between two calls of a cut light collect, applies the rest of
            // the batch before the voxel changes; the handler's next call finds the batch applied. A scheduler
            // disposed during a cut releases only the light it did not hand out.
            using DataRig rig = new();
            rig.LoadAir(new int2(-1, -1), new int2(2, 2));
            HashSet<int2> jobs = new(){ new int2(0, 0), new int2(1, 0), new int2(0, 1), new int2(1, 1) };
            LightScheduler light = rig.CreateLight();
            try{
                light.Start(jobs);
                light.CompleteJob();
                FrameBudget budget = ItemsPerFrame(1);
                budget.BeginFrame();
                Assert.IsFalse(light.Complete(budget));
                budget.BeginFrame();
                Assert.IsFalse(light.Complete(budget));
                Assert.AreEqual(2, rig.Lit.Count);

                int3 voxel = new(3, ChunkHeight - 2, 5); // in chunk (0, 0)
                Assert.IsTrue(rig.Manager.SetVoxel(PlayModeFixtures.Grass, voxel), "the edit is taken");
                Assert.IsTrue(light.IsReady, "the edit applied the rest of the batch first");
                CollectionAssert.AreEquivalent(jobs, rig.Lit, "every chunk of the batch is lit once");
                budget.BeginFrame();
                Assert.IsTrue(light.Complete(budget), "the handler's next call finds the batch applied");
            }
            finally{
                light.Dispose();
            }

            rig.Lit.Clear();
            HashSet<int2> second = new(){ new int2(1, 1), new int2(1, 0) };
            light = rig.CreateLight();
            light.Start(second);
            light.CompleteJob();
            FrameBudget cut = ItemsPerFrame(1);
            cut.BeginFrame();
            Assert.IsFalse(light.Complete(cut));
            Assert.AreEqual(1, rig.Lit.Count);
            Assert.DoesNotThrow(light.Dispose, "a dispose during a cut releases the rest once");
        }
    }
}
