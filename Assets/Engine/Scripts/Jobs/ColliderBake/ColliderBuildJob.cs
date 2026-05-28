using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Engine.Scripts.Jobs.ColliderBake
{
    /// <summary>
    ///     Parallel job baking physics mesh colliders for previously generated meshes.
    ///     Uses Physics.BakeMesh with optimization options.
    /// </summary>
    [BurstCompile]
    internal struct ColliderBuildJob : IJobParallelFor
    {
        [ReadOnly] public NativeList<int> MeshIDs;

        /// <summary>
        ///     Executes bake for each mesh.
        /// </summary>
        public void Execute(int index)
        {
            Physics.BakeMesh(MeshIDs[index], false,
                MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.CookForFasterSimulation);
        }
    }
}