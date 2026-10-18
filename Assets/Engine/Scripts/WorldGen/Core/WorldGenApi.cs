using System;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>A region request (GEN-17): settings snapshot with seed, origin and size in columns.</summary>
    public struct WorldGenRequest{
        /// <summary>Built by <c>WorldGenSettings.ToConfig</c>; must stay alive until the returned handle completes.</summary>
        public WorldGenConfig Config;

        /// <summary>World column (x, z) of element 0.</summary>
        public int2 Origin;

        /// <summary>Samples along x and z. Element [j × Size.x + i] = column (Origin.x + i·Stride, Origin.y + j·Stride) (EXP-01).</summary>
        public int2 Size;

        /// <summary>Column step between samples; 0 or 1 = every column (EXP-01). Larger values give a zoomed-out preview.</summary>
        public int Stride;

        public int ColumnCount => Size.x * Size.y;
    }

    /// <summary>
    ///     Caller-allocated outputs for <see cref="WorldGenApi.SampleRegion" /> (GEN-17). Any subset may be created;
    ///     the others are left default and skipped. Every created array must hold at least
    ///     <see cref="WorldGenRequest.ColumnCount" /> elements. The caller owns and disposes them.
    /// </summary>
    public struct RegionBuffers{
        /// <summary>T, F, C, E, W, PV per column.</summary>
        public NativeArray<WorldFields> Fields;

        /// <summary>Clamped H per column.</summary>
        public NativeArray<float> Height;

        /// <summary>floor(H) per column.</summary>
        public NativeArray<int> ColumnHeight;

        /// <summary>Hard classification argmin_b d_b.</summary>
        public NativeArray<int> HardBiome;

        /// <summary>argmax of the material weights.</summary>
        public NativeArray<int> DominantMaterialBiome;

        public NativeArray<BlendWeights> MaterialWeights;
        public NativeArray<BlendWeights> HeightWeights;

        /// <summary>Biome supplying the top/under blocks.</summary>
        public NativeArray<int> SurfaceSource;

        /// <summary>Flora species id, 0 = none.</summary>
        public NativeArray<ushort> FloraSpecies;

        /// <summary>Biome whose rule produced the flora, -1 = none.</summary>
        public NativeArray<int> FloraSource;

        /// <summary>1 where the height clamp engaged (GEN-12), else 0.</summary>
        public NativeArray<byte> Clamped;
    }

    /// <summary>
    ///     Public region API of the world generator (GEN-17). Fills the requested <see cref="RegionBuffers" /> by
    ///     running <see cref="WorldSampler.SampleColumn" /> per column in a Burst job. Generic engine functionality,
    ///     used by the preview window and later by Part II; it contains no research code (R4).
    /// </summary>
    public static class WorldGenApi{
        /// <summary>
        ///     Schedules the sampling of <paramref name="request" /> into the created arrays of
        ///     <paramref name="buffers" />. Returns the job handle; the caller completes it before reading.
        /// </summary>
        /// <exception cref="ArgumentException">A created buffer is shorter than the request, or the size is not positive.</exception>
        public static JobHandle SampleRegion(in WorldGenRequest request, ref RegionBuffers buffers, JobHandle dependsOn = default){
            if (request.Size.x <= 0 || request.Size.y <= 0)
                throw new ArgumentException($"Region size must be positive, got {request.Size}.", nameof(request));
            if (!request.Config.IsCreated)
                throw new ArgumentException("WorldGenRequest.Config is not created (build it with WorldGenSettings.ToConfig).", nameof(request));

            int count = request.ColumnCount;
            // Validate every buffer before the first dummy is allocated, so a rejected request leaks nothing.
            Validate(buffers.Fields, count, nameof(buffers.Fields));
            Validate(buffers.Height, count, nameof(buffers.Height));
            Validate(buffers.ColumnHeight, count, nameof(buffers.ColumnHeight));
            Validate(buffers.HardBiome, count, nameof(buffers.HardBiome));
            Validate(buffers.DominantMaterialBiome, count, nameof(buffers.DominantMaterialBiome));
            Validate(buffers.MaterialWeights, count, nameof(buffers.MaterialWeights));
            Validate(buffers.HeightWeights, count, nameof(buffers.HeightWeights));
            Validate(buffers.SurfaceSource, count, nameof(buffers.SurfaceSource));
            Validate(buffers.FloraSpecies, count, nameof(buffers.FloraSpecies));
            Validate(buffers.FloraSource, count, nameof(buffers.FloraSource));
            Validate(buffers.Clamped, count, nameof(buffers.Clamped));

            SampleRegionJob job = new(){
                Config = request.Config,
                Origin = request.Origin,
                Width = request.Size.x,
                Stride = math.max(1, request.Stride)
            };

            // Unrequested buffers are replaced by 1-element dummies (one per field: the safety system rejects two
            // writeable fields sharing a container) so the job struct holds only valid containers; the Write* flags
            // keep the job from touching them. The dummies die with the handle.
            job.WriteFields = Bind(ref buffers.Fields, out job.Fields, out NativeArray<WorldFields> dFields);
            job.WriteHeight = Bind(ref buffers.Height, out job.Height, out NativeArray<float> dHeight);
            job.WriteColumnHeight = Bind(ref buffers.ColumnHeight, out job.ColumnHeight, out NativeArray<int> dColumnHeight);
            job.WriteHardBiome = Bind(ref buffers.HardBiome, out job.HardBiome, out NativeArray<int> dHardBiome);
            job.WriteDominantMaterialBiome = Bind(ref buffers.DominantMaterialBiome, out job.DominantMaterialBiome, out NativeArray<int> dDominant);
            job.WriteMaterialWeights = Bind(ref buffers.MaterialWeights, out job.MaterialWeights, out NativeArray<BlendWeights> dMaterial);
            job.WriteHeightWeights = Bind(ref buffers.HeightWeights, out job.HeightWeights, out NativeArray<BlendWeights> dHeightWeights);
            job.WriteSurfaceSource = Bind(ref buffers.SurfaceSource, out job.SurfaceSource, out NativeArray<int> dSurface);
            job.WriteFloraSpecies = Bind(ref buffers.FloraSpecies, out job.FloraSpecies, out NativeArray<ushort> dSpecies);
            job.WriteFloraSource = Bind(ref buffers.FloraSource, out job.FloraSource, out NativeArray<int> dFloraSource);
            job.WriteClamped = Bind(ref buffers.Clamped, out job.Clamped, out NativeArray<byte> dClamped);

            JobHandle handle = job.Schedule(count, 64, dependsOn);

            handle = DisposeDummy(dFields, handle);
            handle = DisposeDummy(dHeight, handle);
            handle = DisposeDummy(dColumnHeight, handle);
            handle = DisposeDummy(dHardBiome, handle);
            handle = DisposeDummy(dDominant, handle);
            handle = DisposeDummy(dMaterial, handle);
            handle = DisposeDummy(dHeightWeights, handle);
            handle = DisposeDummy(dSurface, handle);
            handle = DisposeDummy(dSpecies, handle);
            handle = DisposeDummy(dFloraSource, handle);
            handle = DisposeDummy(dClamped, handle);
            return handle;
        }

        /// <summary>Throws when a requested (created) buffer is shorter than the request.</summary>
        private static void Validate<T>(NativeArray<T> requested, int count, string name) where T : unmanaged{
            if (requested.IsCreated && requested.Length < count)
                throw new ArgumentException($"RegionBuffers.{name} holds {requested.Length} elements, the request needs {count}.");
        }

        /// <summary>Binds a requested buffer or creates a 1-element dummy in its place. Returns whether to write it.</summary>
        private static bool Bind<T>(ref NativeArray<T> requested, out NativeArray<T> target, out NativeArray<T> dummy) where T : unmanaged{
            if (requested.IsCreated){
                target = requested;
                dummy = default;
                return true;
            }

            dummy = new NativeArray<T>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            target = dummy;
            return false;
        }

        private static JobHandle DisposeDummy<T>(NativeArray<T> dummy, JobHandle handle) where T : unmanaged{
            return dummy.IsCreated ? dummy.Dispose(handle) : handle;
        }

        /// <summary>One column per index; GEN-16: synchronous compile, strict float mode.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct SampleRegionJob : IJobParallelFor{
            [ReadOnly] public WorldGenConfig Config;
            public int2 Origin;
            public int Width;
            public int Stride;

            public bool WriteFields;
            public bool WriteHeight;
            public bool WriteColumnHeight;
            public bool WriteHardBiome;
            public bool WriteDominantMaterialBiome;
            public bool WriteMaterialWeights;
            public bool WriteHeightWeights;
            public bool WriteSurfaceSource;
            public bool WriteFloraSpecies;
            public bool WriteFloraSource;
            public bool WriteClamped;

            // Dummies are length 1, so the per-index restriction is lifted; the flags guarantee they are never written.
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<WorldFields> Fields;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float> Height;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> ColumnHeight;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> HardBiome;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> DominantMaterialBiome;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<BlendWeights> MaterialWeights;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<BlendWeights> HeightWeights;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> SurfaceSource;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<ushort> FloraSpecies;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> FloraSource;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> Clamped;

            public void Execute(int index){
                int i = index % Width;
                int j = index / Width;
                ColumnSample s = WorldSampler.SampleColumn(Origin.x + i * Stride, Origin.y + j * Stride, Config);

                if (WriteFields) Fields[index] = s.Fields;
                if (WriteHeight) Height[index] = s.Height;
                if (WriteColumnHeight) ColumnHeight[index] = s.ColumnHeight;
                if (WriteHardBiome) HardBiome[index] = s.HardBiome;
                if (WriteDominantMaterialBiome) DominantMaterialBiome[index] = s.DominantMaterialBiome;
                if (WriteMaterialWeights) MaterialWeights[index] = s.MaterialWeights;
                if (WriteHeightWeights) HeightWeights[index] = s.HeightWeights;
                if (WriteSurfaceSource) SurfaceSource[index] = s.SurfaceSource;
                if (WriteFloraSpecies) FloraSpecies[index] = s.FloraSpecies;
                if (WriteFloraSource) FloraSource[index] = s.FloraSource;
                if (WriteClamped) Clamped[index] = s.Clamped ? (byte)1 : (byte)0;
            }
        }
    }
}
