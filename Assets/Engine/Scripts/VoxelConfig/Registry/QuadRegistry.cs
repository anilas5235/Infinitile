using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Unity.Mathematics;

namespace Engine.Scripts.VoxelConfig.Registry{
    /// <summary>
    ///     Registers quad definitions and builds the runtime quad array used by voxel rendering.
    /// </summary>
    public class QuadRegistry : Registry<QuadDefinition>, IResourceRegistry<QuadDefinition>{
        // Turned copies of registered quads, appended after them; keyed by quad and rotation
        private readonly Dictionary<(QuadDefinition, int3x3), ushort> _rotatedIds = new();
        private readonly List<QuadDefinition.QuadDef> _rotated = new();

        public QuadRegistry(int initCapacity) : base(initCapacity){ }

        /// <summary>Number of quads in the runtime array: the registered ones and their turned copies.</summary>
        public int TotalCount => Count + _rotated.Count;

        /// <summary>
        ///     Gets the registered quad data array after preparation.
        /// </summary>
        public QuadDefinition.QuadDef[] QuadArray{ get; private set; }

        /// <summary>
        ///     Registers a quad definition and returns its assigned ID.
        /// </summary>
        /// <param name="quad">The quad definition to register.</param>
        /// <returns>The assigned quad ID</returns>
        public override ushort Register(QuadDefinition quad){
            if (!quad) throw new ArgumentNullException(nameof(quad), "Cannot register a null quad definition.");
            if (_rotated.Count > 0 && !TryGetId(quad, out _))
                throw new InvalidOperationException("Quads must be registered before any turned copy.");
            return base.Register(quad);
        }

        /// <summary>
        ///     Registers a copy of a quad turned about the voxel centre (0.5, 0.5, 0.5) and returns its id; the
        ///     identity gives the quad itself. Positions, normal, up and right turn; the UVs stay with their corners,
        ///     so the texture turns with the quad. The basis is the quad's own, turned, so ambient occlusion and the
        ///     corner light stay aligned with the texture as on the authored quad.
        /// </summary>
        public ushort RegisterRotated(QuadDefinition quad, int3x3 rotation){
            if (rotation.Equals(int3x3.identity)) return Register(quad);
            if (_rotatedIds.TryGetValue((quad, rotation), out ushort id)) return id;
            if (TotalCount > ushort.MaxValue)
                throw new InvalidOperationException("Quad registry is full (65535).");

            id = (ushort)TotalCount;
            _rotatedIds.Add((quad, rotation), id);
            _rotated.Add(Rotate(quad.ToStruct(), rotation));
            return id;
        }

        /// <summary>A quad turned about the voxel centre; exact for the integer rotations of the orientations.</summary>
        public static QuadDefinition.QuadDef Rotate(QuadDefinition.QuadDef quad, int3x3 rotation){
            float3x3 r = rotation;
            float3 centre = new(0.5f);
            quad.position00 = math.mul(r, quad.position00 - centre) + centre;
            quad.position01 = math.mul(r, quad.position01 - centre) + centre;
            quad.position02 = math.mul(r, quad.position02 - centre) + centre;
            quad.position03 = math.mul(r, quad.position03 - centre) + centre;
            quad.normal = math.mul(r, quad.normal);
            quad.up = math.mul(r, quad.up);
            quad.right = math.mul(r, quad.right);
            return quad;
        }

        /// <summary>
        ///     Builds the runtime quad array from all registered quad definitions.
        /// </summary>
        public void PrepareArray(){
            if (TotalCount == 0){
                QuadArray = Array.Empty<QuadDefinition.QuadDef>();
                return;
            }

            QuadArray = new QuadDefinition.QuadDef[TotalCount];
            int index = 0;
            foreach (KeyValuePair<QuadDefinition, ushort> kvp in ForwardMap){
                VoxelEngineLogger.Info<QuadRegistry>($"copy quad {kvp.Key.name} to Quad array");
                QuadArray[index] = kvp.Key.ToStruct();
                index++;
            }

            foreach (QuadDefinition.QuadDef rotated in _rotated) QuadArray[index++] = rotated;
        }
    }
}
