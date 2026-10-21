using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Build scheduling of the world-generation preview. Collects change requests and, once asked, rebuilds the
    ///     Burst configs (so edits take effect) and schedules pass A, in compare mode pass B over the same region and
    ///     seed, and the distribution plane of A. <see cref="Update" /> applies the results when the jobs are done.
    /// </summary>
    internal sealed class PreviewBuilder : IDisposable{
        internal const int DistributionResolution = 256;
        private const int PlotSamples = 96;
        internal const double RebuildDebounceSeconds = 0.15; // auto rebuild waits for this pause in the changes

        /// <summary>One build: the asset, the optional configuration B, the sampled region and the view options.</summary>
        internal struct Request{
            public WorldGenSettings Asset;
            public PreviewConfiguration ConfigB; // null: pass A only
            public int Seed;
            public int2 Origin;
            public int ViewSize;
            public int Stride;
            public PreviewViewOptions Options;
            public float3 Probe; // C, E, PV of the distribution plane and the spline plots
            public float ProbeHeight; // normalised h of the distribution plane, Terrain-first only
        }

        private bool _stale = true;
        private double _lastChangeTime; // EditorApplication.timeSinceStartup of the last MarkStale
        private bool _inProgress;
        private JobHandle _handle;
        private WorldGenSettings _asset; // the asset of the running build
        private bool _scheduledA;
        private bool _scheduledB;

        private Texture2D _distributionTexture;
        private NativeArray<Color32> _jobDistributionPixels;
        private NativeArray<Color32> _jobDistributionColors;

        internal PreviewPass PassA{ get; } = new("A");
        internal PreviewPass PassB{ get; } = new("B");

        /// <summary>The classifier's T x F plane of A at the probe values.</summary>
        internal Texture2D DistributionTexture => _distributionTexture;

        internal List<SplinePlots.Plot> Plots{ get; private set; } = new();
        internal string PlotsError{ get; private set; }
        internal bool InProgress => _inProgress;

        /// <summary>Stale, idle, and no change has come in for <see cref="RebuildDebounceSeconds" />.</summary>
        internal bool AutoBuildDue => _stale && !_inProgress &&
                                      EditorApplication.timeSinceStartup - _lastChangeTime >= RebuildDebounceSeconds;

        /// <summary>Raised when a build has been applied, or when a stale build had no pass to schedule.</summary>
        internal event Action Finished;

        public void Dispose(){
            Complete(false); // a running build still reads the configs and buffers
            PassA.Dispose();
            PassB.Dispose();
            PreviewPass.DestroyTexture(ref _distributionTexture);
        }

        /// <summary>Marks the views stale and records the time of the change.</summary>
        internal void MarkStale(){
            _stale = true;
            _lastChangeTime = EditorApplication.timeSinceStartup;
        }

        /// <summary>Applies a finished build; call once per editor update.</summary>
        internal void Update(){
            if (_inProgress && _handle.IsCompleted) Complete(true);
        }

        /// <summary>
        ///     Rebuilds the Burst configs and schedules the passes of <paramref name="request" /> if the views are
        ///     stale and no build is running. The spline plots are recomputed synchronously (a few thousand spline
        ///     evaluations).
        /// </summary>
        internal void Schedule(in Request request){
            if (_inProgress || !_stale) return;
            _stale = false;
            WorldGenSettings asset = request.Asset;
            if (!asset) return;

            Plots = SplinePlots.Compute(asset.Splines, request.Probe, PlotSamples, out string plotsError);
            PlotsError = plotsError;

            JobHandle handle = default;
            _scheduledA = PassA.Schedule(asset, null, request.Seed, request.Origin, request.ViewSize, request.Stride,
                request.Options, out JobHandle a);
            if (_scheduledA){
                handle = JobHandle.CombineDependencies(handle, a,
                    ScheduleDistribution(asset, request.Probe, request.ProbeHeight));
            }

            _scheduledB = false;
            if (request.ConfigB != null && PassB.Schedule(asset, request.ConfigB, request.Seed, request.Origin,
                    request.ViewSize, request.Stride, request.Options, out JobHandle b)){
                _scheduledB = true;
                handle = JobHandle.CombineDependencies(handle, b);
            }

            if (!_scheduledA && !_scheduledB){
                Finished?.Invoke();
                return;
            }

            _asset = asset;
            _handle = handle;
            _inProgress = true;
        }

        private JobHandle ScheduleDistribution(WorldGenSettings asset, float3 probe, float probeHeight){
            WorldGenConfig config = PassA.Config;
            _jobDistributionPixels = new NativeArray<Color32>(DistributionResolution * DistributionResolution, Allocator.Persistent);
            _jobDistributionColors = new NativeArray<Color32>(Mathf.Max(1, config.BiomeCount), Allocator.Persistent);
            for (int i = 0; i < config.BiomeCount; i++){
                Biome biome = asset.Biomes.Biomes[i];
                _jobDistributionColors[i] = biome ? (Color32)biome.RepresentativeColor : new Color32(0, 0, 0, 255);
            }

            return new DistributionJob{
                Resolution = DistributionResolution,
                Continental = probe.x,
                Erosion = probe.y,
                PV = probe.z,
                Height = probeHeight,
                TerrainFirst = config.Architecture == GenArchitecture.TerrainFirst ? (byte)1 : (byte)0,
                Config = config,
                BiomeColors = _jobDistributionColors,
                Output = _jobDistributionPixels
            }.Schedule(_jobDistributionPixels.Length, 64);
        }

        private void Complete(bool applyResult){
            if (_inProgress){
                _handle.Complete();
                _inProgress = false;
                if (applyResult && _asset){
                    if (_scheduledA){
                        PassA.Finish(_asset);
                        PreviewPass.EnsureTexture(ref _distributionTexture, DistributionResolution);
                        _distributionTexture.SetPixelData(_jobDistributionPixels, 0);
                        _distributionTexture.Apply(false);
                    }

                    if (_scheduledB) PassB.Finish(_asset);
                }
            }

            if (_jobDistributionPixels.IsCreated) _jobDistributionPixels.Dispose();
            if (_jobDistributionColors.IsCreated) _jobDistributionColors.Dispose();
            if (applyResult) Finished?.Invoke();
        }

        /// <summary>
        ///     The classifier's T x F plane: x → F, y → T at fixed C, E, PV (and h in Terrain-first, 6D). Uses the
        ///     same <see cref="BiomeClassifier" /> as the sampler, so the plane is the hard class the world would get
        ///     at those field values, tie rule included.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct DistributionJob : IJobParallelFor{
            public int Resolution;
            public float Continental;
            public float Erosion;
            public float PV;
            public float Height;
            public byte TerrainFirst;
            [ReadOnly] public WorldGenConfig Config;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index){
                int x = index % Resolution;
                int y = index / Resolution;
                float step = 1f / Resolution;
                WorldFields f = new(){
                    F = (x + 0.5f) * step,
                    T = (y + 0.5f) * step,
                    C = Continental,
                    E = Erosion,
                    W = 0f,
                    PV = PV
                };
                int biome = TerrainFirst == 1
                    ? BiomeClassifier.Classify(f, Height, Config)
                    : BiomeClassifier.Classify(f, Config);
                Output[index] = biome >= 0 && biome < BiomeColors.Length ? BiomeColors[biome] : new Color32(0, 0, 0, 255);
            }
        }
    }
}
