using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Engine.Scripts.Jobs.ColliderBake{
    /// <summary>
    ///     Parallel job baking physics mesh colliders for previously generated meshes.
    ///     Uses Physics.BakeMesh with optimization options.
    /// </summary>
    [BurstCompile]
    internal struct ColliderBuildJob : IJobParallelFor{
        /// <summary>
        ///     Options of the bake; the partition colliders use the same, so Unity reuses the baked data.
        /// </summary>
        internal const MeshColliderCookingOptions CookingOptions =
            MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.CookForFasterSimulation;

        [ReadOnly] public NativeList<EntityId> MeshIDs;

        /// <summary>
        ///     Executes bake for each mesh.
        /// </summary>
        public void Execute(int index){
            Physics.BakeMesh(MeshIDs[index], false, CookingOptions);
        }
    }
}
