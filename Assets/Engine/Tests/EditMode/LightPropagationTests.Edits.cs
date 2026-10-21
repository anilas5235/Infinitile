using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Light;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Tests.EditMode.LightTestWorld;

namespace Engine.Tests.EditMode{
    /// <summary>Incremental light updates after edits.</summary>
    public partial class LightPropagationTests{
        /// <summary>500 random edits with incremental updates equal a full recomputation.</summary>
        [Test]
        public void RandomEdits_Incremental_EqualsFullRecompute(){
            using LightTestWorld world = new(2, RandomTerrain(7));
            List<int2> lit = new();
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                lit.Add(new int2(x, z));
            NativeParallelHashMap<int2, ChunkLightData> lights = world.LightChunks(lit);

            System.Random random = new(11);
            for (int i = 1; i <= 500; i++){
                int3 p = new(random.Next(0, ChunkWidth), random.Next(95, 160), random.Next(0, ChunkDepth));
                ushort id = EditPalette[random.Next(0, EditPalette.Length)];
                world.EditAndRelight(p, id, lights).Dispose();

                if (i % 50 != 0) continue;
                NativeParallelHashMap<int2, ChunkLightData> full = world.LightChunks(lit);
                foreach (int2 c in lit) AssertEqual(full[c], lights[c], $"after {i} edits, chunk {c}");
                foreach (int2 c in lit) full[c].Dispose();
                full.Dispose();
            }

            foreach (int2 c in lit) lights[c].Dispose();
            lights.Dispose();
        }

        [Test]
        public void Edit_PlacingBlockOverSkyColumn_DarkensColumnAndFlagsPartitions(){
            using LightTestWorld world = new(2, p => p.y <= 100 ? Stone : Air);
            List<int2> lit = new();
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                lit.Add(new int2(x, z));
            NativeParallelHashMap<int2, ChunkLightData> lights = world.LightChunks(lit);

            // A 3 × 3 roof at y 200 over (16, 16): the column under the centre gets side light only.
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                world.EditAndRelight(new int3(16 + dx, 200, 16 + dz), Stone, lights).Dispose();

            Assert.AreEqual(13, VoxelLight.GetSky(LightAt(lights[Origin], new int3(16, 150, 16))));
            NativeArray<byte> changed = world.EditAndRelight(new int3(16, 200, 16), Air, lights);
            Assert.AreEqual(15, VoxelLight.GetSky(LightAt(lights[Origin], new int3(16, 150, 16))));
            int center = LightEditJob.ChunkIndex(int2.zero) * PartitionsPerChunk;
            Assert.AreEqual(1, changed[center + 150 / PartitionHeight], "the column's partition is flagged");
            Assert.AreEqual(0, changed[center + 1], "partition 1 (y 32-63) is under the ground and unchanged");
            changed.Dispose();

            foreach (int2 c in lit) lights[c].Dispose();
            lights.Dispose();
        }
    }
}
