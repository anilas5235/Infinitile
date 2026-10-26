using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using BlendWeights = Engine.Scripts.WorldGen.Blending.BlendWeights;
using Object = UnityEngine.Object;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     The views of the preview. Offset, Amp and Jaggedness exist in Terrain-first only; the cave volume is
    ///     sampled only on request and at one sample per block.
    /// </summary>
    internal enum PreviewView{
        Height,
        HardBiome,
        DominantBiome,
        MaxMaterialWeight,
        SurfaceSource,
        Flora,
        Climate,
        Erosion,
        PeaksValleys,
        Offset,
        Amp,
        Jaggedness,
        CaveVolume,
        CavernMask,
        EntranceMask,
        LavaMask
    }

    /// <summary>Display options shared by every pass.</summary>
    internal struct PreviewViewOptions{
        public bool ShowTemperature;
        public bool ShowContinental;
        public bool ShowHumidity;
        public bool Hillshade;

        /// <summary>Vertical exaggeration of the hillshade (1 = true slope in blocks).</summary>
        public float Exaggeration;

        /// <summary>Density noise (<see cref="FloraDensity" />) the flora view shades its ground with; -1 = none.</summary>
        public int DensityOverlay;

        /// <summary>
        ///     Sample the carved voxels per column (a full voxel generation of the region without flora); taken only
        ///     at one sample per block.
        /// </summary>
        public bool CaveVolume;
    }

    /// <summary>
    ///     One configuration of the preview: its Burst config, the sampled region buffers, the view pixels and
    ///     textures and the per-view statistics (clamped share, biome-pair boundaries, flora counts). Every view is
    ///     filled from <see cref="WorldGenApi.SampleRegion" />, so it shows exactly what <c>WorldSampler</c>
    ///     computes. The buffers persist between builds so the A/B difference can be
    ///     recomputed for another view without sampling again; they are reallocated only when the view size changes.
    /// </summary>
    internal sealed class PreviewPass : IDisposable{
        public static readonly int ViewCount = Enum.GetValues(typeof(PreviewView)).Length;

        public readonly string Label;
        private WorldGenConfig _config; // owned; rebuilt per build
        private readonly LocalVoxelIds _voxelIds = new(); // the cave volume tells blocks from air by id

        // --- Sampled buffers (Persistent, Size² each) ----------------------------------------------------------------
        public NativeArray<WorldFields> Fields;
        public NativeArray<float> Height;
        public NativeArray<int> HardBiome;
        public NativeArray<int> DominantBiome;
        public NativeArray<BlendWeights> MaterialWeights;
        public NativeArray<int> SurfaceSource;
        public NativeArray<ushort> FloraSpecies;
        public NativeArray<byte> Clamped;
        public NativeArray<float3> TerrainParams;
        public NativeArray<float> MaxWeight;
        public NativeArray<float> CavernMask;
        public NativeArray<float> EntranceMask;
        public NativeArray<float> LavaMask;
        public NativeArray<int> CaveVolume;
        private NativeArray<int> _edges;
        private NativeArray<Color32> _biomeColors;
        private readonly NativeArray<Color32>[] _pixels = new NativeArray<Color32>[ViewCount];
        private readonly Texture2D[] _textures = new Texture2D[ViewCount];

        public PreviewPass(string label){
            Label = label;
        }

        // --- Result of the last finished build ------------------------------------------------------------------------
        public string Error { get; private set; }
        public string Description { get; private set; } = "";
        public bool TerrainFirst { get; private set; }
        public int Size { get; private set; }
        public int Stride { get; private set; }
        public bool HasResult { get; private set; }
        public float ClampedShare { get; private set; }
        public float MeanMaxWeight { get; private set; }
        public float2 AmpRange { get; private set; }
        public float2 JaggednessRange { get; private set; }

        /// <summary>The last build sampled the cave volume.</summary>
        public bool CaveVolumeSampled { get; private set; }

        public int MaxCaveVolume { get; private set; }
        public long TotalCaveVolume { get; private set; }

        /// <summary>Share of the columns inside each cave mask: cavern, entrance, lava.</summary>
        public float3 MaskShares { get; private set; }
        public List<BiomeBoundaryStats.PairLength> Boundaries { get; private set; } = new();

        /// <summary>Flora species id → name of the last build (element 0 = "none").</summary>
        public string[] FloraNames { get; private set; } = Array.Empty<string>();

        /// <summary>Flora species id → columns, sorted by count (id 0 = none excluded).</summary>
        public List<(ushort species, int count)> FloraCounts { get; private set; } = new();

        /// <summary>The config of the running or last build; valid until the next <see cref="Schedule" />.</summary>
        public WorldGenConfig Config => _config;

        public Texture2D Texture(PreviewView view){
            return _textures[(int)view];
        }

        public NativeArray<Color32> Pixels(PreviewView view){
            return _pixels[(int)view];
        }

        /// <summary>
        ///     Builds the config (from the asset, or the asset with <paramref name="overrides" />) and schedules the
        ///     sampling, the view conversion and the boundary count. Returns false with <see cref="Error" /> set when
        ///     the config cannot be built; nothing is scheduled then. The previous config is disposed here, so no
        ///     job of the previous build may still run.
        /// </summary>
        public bool Schedule(WorldGenSettings asset, PreviewConfiguration overrides, int seed, int2 origin, int size,
            int stride, PreviewViewOptions options, out JobHandle handle){
            handle = default;
            DisposeConfig();
            try{
                _config = overrides != null
                    ? overrides.BuildConfig(asset, seed, _voxelIds.Id)
                    : asset.ToConfigWithVoxelIds(seed, _voxelIds.Id);
                Error = null;
            }
            catch (Exception e){
                Error = e.Message;
                HasResult = false;
                return false;
            }

            Description = (overrides ?? PreviewConfiguration.Capture(asset)).Describe();
            TerrainFirst = _config.Architecture == GenArchitecture.TerrainFirst;
            Size = size;
            Stride = stride;
            EnsureBuffers(size * size);

            int biomeCount = _config.BiomeCount;
            for (int i = 0; i < _biomeColors.Length; i++){
                Biome biome = i < biomeCount ? asset.Biomes.Biomes[i] : null;
                _biomeColors[i] = biome ? (Color32)biome.RepresentativeColor : new Color32(0, 0, 0, 255);
            }

            WorldGenRequest request = new(){ Config = _config, Origin = origin, Size = new int2(size, size), Stride = stride };
            RegionBuffers buffers = new(){
                Fields = Fields,
                Height = Height,
                HardBiome = HardBiome,
                DominantMaterialBiome = DominantBiome,
                MaterialWeights = MaterialWeights,
                SurfaceSource = SurfaceSource,
                FloraSpecies = FloraSpecies,
                Clamped = Clamped,
                TerrainParams = TerrainParams, // zero in Biome-first
                CavernMask = CavernMask,
                EntranceMask = EntranceMask,
                LavaMask = LavaMask
            };
            CaveVolumeSampled = options.CaveVolume && stride == 1;
            if (CaveVolumeSampled) buffers.CaveVolume = CaveVolume;
            JobHandle sampled = WorldGenApi.SampleRegion(request, ref buffers);

            int overlay = math.clamp(options.DensityOverlay, -1, 3);
            JobHandle views = new ViewJob{
                Size = size,
                Stride = stride,
                Origin = origin,
                Seed = _config.Seed,
                DensityOverlay = overlay,
                DensityWavelength = overlay >= 0 ? _config.FloraDensityWavelength[overlay] : 1f,
                DensityContrast = overlay >= 0 ? _config.FloraDensityContrast[overlay] : 0f,
                Fields = Fields,
                Height = Height,
                HardBiome = HardBiome,
                DominantBiome = DominantBiome,
                MaterialWeights = MaterialWeights,
                SurfaceSource = SurfaceSource,
                FloraSpecies = FloraSpecies,
                Clamped = Clamped,
                BiomeColors = _biomeColors,
                HeightMin = _config.HeightMin,
                HeightMax = _config.HeightMax,
                WaterLevel = asset.WaterLevel,
                ShowTemperature = options.ShowTemperature ? (byte)1 : (byte)0,
                ShowContinental = options.ShowContinental ? (byte)1 : (byte)0,
                ShowHumidity = options.ShowHumidity ? (byte)1 : (byte)0,
                Hillshade = options.Hillshade ? (byte)1 : (byte)0,
                Exaggeration = options.Exaggeration,
                MaxWeight = MaxWeight,
                HeightView = _pixels[(int)PreviewView.Height],
                HardView = _pixels[(int)PreviewView.HardBiome],
                DominantView = _pixels[(int)PreviewView.DominantBiome],
                MaxWeightView = _pixels[(int)PreviewView.MaxMaterialWeight],
                SurfaceView = _pixels[(int)PreviewView.SurfaceSource],
                FloraView = _pixels[(int)PreviewView.Flora],
                ClimateView = _pixels[(int)PreviewView.Climate],
                ErosionView = _pixels[(int)PreviewView.Erosion],
                PvView = _pixels[(int)PreviewView.PeaksValleys],
                CavernMask = CavernMask,
                EntranceMask = EntranceMask,
                LavaMask = LavaMask,
                MaskThresholds = MaskThresholds(),
                CavernView = _pixels[(int)PreviewView.CavernMask],
                EntranceView = _pixels[(int)PreviewView.EntranceMask],
                LavaView = _pixels[(int)PreviewView.LavaMask]
            }.Schedule(size * size, 64, sampled);

            // Boundaries of the dominant material biome, the class the surface blocks follow.
            JobHandle boundaries = BiomeBoundaryStats.Schedule(DominantBiome, size, _edges, sampled);
            handle = JobHandle.CombineDependencies(views, boundaries);
            return true;
        }

        /// <summary>After the scheduled handle completed: uploads the textures and computes the statistics.</summary>
        public void Finish(WorldGenSettings asset){
            FillTerrainParamViews(asset.WaterLevel);
            FillCaveVolumeView();
            for (int v = 0; v < ViewCount; v++){
                EnsureTexture(ref _textures[v], Size);
                _textures[v].SetPixelData(_pixels[v], 0);
                _textures[v].Apply(false);
            }

            int count = Size * Size;
            int clamped = 0;
            double weight = 0;
            int3 masked = int3.zero;
            float3 thresholds = MaskThresholds();
            Dictionary<ushort, int> flora = new();
            for (int i = 0; i < count; i++){
                clamped += Clamped[i];
                weight += MaxWeight[i];
                masked += math.select(int3.zero, 1, new float3(CavernMask[i], EntranceMask[i], LavaMask[i]) > thresholds);
                ushort species = FloraSpecies[i];
                if (species == 0) continue;
                flora.TryGetValue(species, out int n);
                flora[species] = n + 1;
            }

            ClampedShare = count > 0 ? clamped / (float)count : 0f;
            MeanMaxWeight = count > 0 ? (float)(weight / count) : 0f;
            MaskShares = count > 0 ? (float3)masked / count : float3.zero;
            FloraCounts = new List<(ushort, int)>();
            foreach (KeyValuePair<ushort, int> pair in flora) FloraCounts.Add((pair.Key, pair.Value));
            FloraCounts.Sort((a, b) => b.count != a.count ? b.count.CompareTo(a.count) : a.species.CompareTo(b.species));
            Boundaries = BiomeBoundaryStats.Summarize(_edges, Stride, asset.Biomes);
            try{
                FloraNames = asset.FloraSpeciesNames();
            }
            catch (InvalidOperationException){
                FloraNames = Array.Empty<string>(); // an empty biome slot; ToConfig reported it already
            }

            HasResult = true;
        }

        /// <summary>
        ///     Terrain-parameter views: Offset on the height scale (blue below the water level), Amp and J normalised to the range seen
        ///     in the view. Black in Biome-first. Main-thread conversion of at most 512² pixels.
        /// </summary>
        private void FillTerrainParamViews(int waterLevel){
            NativeArray<Color32> offsetPixels = _pixels[(int)PreviewView.Offset];
            NativeArray<Color32> ampPixels = _pixels[(int)PreviewView.Amp];
            NativeArray<Color32> jagPixels = _pixels[(int)PreviewView.Jaggedness];
            int count = Size * Size;
            if (!TerrainFirst){
                Color32 black = new(0, 0, 0, 255);
                for (int i = 0; i < count; i++) offsetPixels[i] = ampPixels[i] = jagPixels[i] = black;
                AmpRange = JaggednessRange = float2.zero;
                return;
            }

            float2 amp = new(float.MaxValue, float.MinValue), jag = new(float.MaxValue, float.MinValue);
            for (int i = 0; i < count; i++){
                float3 t = TerrainParams[i];
                amp = new float2(math.min(amp.x, t.y), math.max(amp.y, t.y));
                jag = new float2(math.min(jag.x, t.z), math.max(jag.y, t.z));
            }

            if (count == 0) amp = jag = float2.zero;
            AmpRange = amp;
            JaggednessRange = jag;
            float heightSpan = math.max(1f, _config.HeightMax - _config.HeightMin);
            float ampSpan = math.max(1e-6f, amp.y - amp.x);
            float jagSpan = math.max(1e-6f, jag.y - jag.x);
            for (int i = 0; i < count; i++){
                float3 t = TerrainParams[i];
                byte o = (byte)math.round(math.saturate((t.x - _config.HeightMin) / heightSpan) * 255f);
                offsetPixels[i] = t.x < waterLevel
                    ? new Color32((byte)(o / 2), (byte)(o / 2), (byte)math.min(255, o + 80), 255)
                    : new Color32(o, o, o, 255);
                byte a = (byte)math.round(math.saturate((t.y - amp.x) / ampSpan) * 255f);
                ampPixels[i] = new Color32(a, a, a, 255);
                byte j = (byte)math.round(math.saturate((t.z - jag.x) / jagSpan) * 255f);
                jagPixels[i] = new Color32(j, j, j, 255);
            }
        }

        /// <summary>Thresholds of the cavern, entrance and lava masks.</summary>
        private float3 MaskThresholds(){
            return new float3(_config.Caves.CavernMaskThreshold, _config.Caves.EntranceThreshold, _config.Caves.LavaMaskThreshold);
        }

        /// <summary>
        ///     Carved voxels per column on a black-to-yellow scale up to the largest column in the view; dark blue
        ///     where the volume was not sampled. Main-thread conversion of at most 512² pixels.
        /// </summary>
        private void FillCaveVolumeView(){
            NativeArray<Color32> pixels = _pixels[(int)PreviewView.CaveVolume];
            int count = Size * Size;
            MaxCaveVolume = 0;
            TotalCaveVolume = 0;
            if (!CaveVolumeSampled){
                Color32 none = new(20, 24, 48, 255);
                for (int i = 0; i < count; i++) pixels[i] = none;
                return;
            }

            for (int i = 0; i < count; i++){
                MaxCaveVolume = math.max(MaxCaveVolume, CaveVolume[i]);
                TotalCaveVolume += CaveVolume[i];
            }

            float scale = 1f / math.max(1, MaxCaveVolume);
            for (int i = 0; i < count; i++){
                float v = math.sqrt(CaveVolume[i] * scale); // square root, so thin tunnels still show
                pixels[i] = CaveVolume[i] == 0
                    ? new Color32(0, 0, 0, 255)
                    : new Color32((byte)math.round(math.lerp(60f, 255f, v)), (byte)math.round(math.lerp(20f, 230f, v)),
                        (byte)math.round(math.lerp(20f, 60f, v)), 255);
            }
        }

        /// <summary>Stable colour of a flora species id (also used for the legend).</summary>
        public static Color32 SpeciesColor(ushort species){
            return ViewJob.SpeciesColor(species);
        }

        private void EnsureBuffers(int count){
            if (Height.IsCreated && Height.Length == count) return;
            DisposeBuffers();
            Fields = new NativeArray<WorldFields>(count, Allocator.Persistent);
            Height = new NativeArray<float>(count, Allocator.Persistent);
            HardBiome = new NativeArray<int>(count, Allocator.Persistent);
            DominantBiome = new NativeArray<int>(count, Allocator.Persistent);
            MaterialWeights = new NativeArray<BlendWeights>(count, Allocator.Persistent);
            SurfaceSource = new NativeArray<int>(count, Allocator.Persistent);
            FloraSpecies = new NativeArray<ushort>(count, Allocator.Persistent);
            Clamped = new NativeArray<byte>(count, Allocator.Persistent);
            TerrainParams = new NativeArray<float3>(count, Allocator.Persistent);
            MaxWeight = new NativeArray<float>(count, Allocator.Persistent);
            CavernMask = new NativeArray<float>(count, Allocator.Persistent);
            EntranceMask = new NativeArray<float>(count, Allocator.Persistent);
            LavaMask = new NativeArray<float>(count, Allocator.Persistent);
            CaveVolume = new NativeArray<int>(count, Allocator.Persistent);
            _edges = new NativeArray<int>(BiomeBoundaryStats.Side * BiomeBoundaryStats.Side, Allocator.Persistent);
            _biomeColors = new NativeArray<Color32>(BiomeSet.MaxBiomes, Allocator.Persistent);
            for (int v = 0; v < ViewCount; v++) _pixels[v] = new NativeArray<Color32>(count, Allocator.Persistent);
            HasResult = false;
        }

        private void DisposeBuffers(){
            if (Fields.IsCreated) Fields.Dispose();
            if (Height.IsCreated) Height.Dispose();
            if (HardBiome.IsCreated) HardBiome.Dispose();
            if (DominantBiome.IsCreated) DominantBiome.Dispose();
            if (MaterialWeights.IsCreated) MaterialWeights.Dispose();
            if (SurfaceSource.IsCreated) SurfaceSource.Dispose();
            if (FloraSpecies.IsCreated) FloraSpecies.Dispose();
            if (Clamped.IsCreated) Clamped.Dispose();
            if (TerrainParams.IsCreated) TerrainParams.Dispose();
            if (MaxWeight.IsCreated) MaxWeight.Dispose();
            if (CavernMask.IsCreated) CavernMask.Dispose();
            if (EntranceMask.IsCreated) EntranceMask.Dispose();
            if (LavaMask.IsCreated) LavaMask.Dispose();
            if (CaveVolume.IsCreated) CaveVolume.Dispose();
            if (_edges.IsCreated) _edges.Dispose();
            if (_biomeColors.IsCreated) _biomeColors.Dispose();
            for (int v = 0; v < ViewCount; v++)
                if (_pixels[v].IsCreated)
                    _pixels[v].Dispose();
        }

        private void DisposeConfig(){
            _config.Dispose(); // safe on a default instance
            _config = default;
        }

        /// <summary>Releases everything; no job of this pass may still run.</summary>
        public void Dispose(){
            DisposeConfig();
            DisposeBuffers();
            for (int v = 0; v < ViewCount; v++) DestroyTexture(ref _textures[v]);
            HasResult = false;
        }

        internal static void EnsureTexture(ref Texture2D texture, int size){
            if (texture && texture.width == size && texture.height == size) return;
            DestroyTexture(ref texture);
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false){
                name = "WorldGenPreviewTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        internal static void DestroyTexture(ref Texture2D texture){
            if (!texture) return;
            Object.DestroyImmediate(texture);
            texture = null;
        }

        /// <summary>
        ///     Converts the sampled buffers to the view pixels. Reads only; no sampler call, so the sampler's strict
        ///     float mode is not needed, but it compiles synchronously like every other preview job: the first build
        ///     after a script reload would otherwise run managed over the whole view.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        private struct ViewJob : IJobParallelFor{
            public int Size;
            public int Stride;
            public int2 Origin;
            public uint Seed;
            public int DensityOverlay;
            public float DensityWavelength;
            public float DensityContrast;
            [ReadOnly] public NativeArray<WorldFields> Fields;
            [ReadOnly] public NativeArray<float> Height;
            [ReadOnly] public NativeArray<int> HardBiome;
            [ReadOnly] public NativeArray<int> DominantBiome;
            [ReadOnly] public NativeArray<BlendWeights> MaterialWeights;
            [ReadOnly] public NativeArray<int> SurfaceSource;
            [ReadOnly] public NativeArray<ushort> FloraSpecies;
            [ReadOnly] public NativeArray<byte> Clamped;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            public float HeightMin;
            public float HeightMax;
            public int WaterLevel;
            public byte ShowTemperature;
            public byte ShowContinental;
            public byte ShowHumidity;
            public byte Hillshade;
            public float Exaggeration;
            [WriteOnly] public NativeArray<float> MaxWeight;
            [WriteOnly] public NativeArray<Color32> HeightView;
            [WriteOnly] public NativeArray<Color32> HardView;
            [WriteOnly] public NativeArray<Color32> DominantView;
            [WriteOnly] public NativeArray<Color32> MaxWeightView;
            [WriteOnly] public NativeArray<Color32> SurfaceView;
            [WriteOnly] public NativeArray<Color32> FloraView;
            [WriteOnly] public NativeArray<Color32> ClimateView;
            [WriteOnly] public NativeArray<Color32> ErosionView;
            [WriteOnly] public NativeArray<Color32> PvView;
            [ReadOnly] public NativeArray<float> CavernMask;
            [ReadOnly] public NativeArray<float> EntranceMask;
            [ReadOnly] public NativeArray<float> LavaMask;
            public float3 MaskThresholds; // cavern, entrance, lava
            [WriteOnly] public NativeArray<Color32> CavernView;
            [WriteOnly] public NativeArray<Color32> EntranceView;
            [WriteOnly] public NativeArray<Color32> LavaView;

            public void Execute(int index){
                HardView[index] = BiomeColor(HardBiome[index]);
                DominantView[index] = BiomeColor(DominantBiome[index]);
                Color32 surface = BiomeColor(SurfaceSource[index]);
                SurfaceView[index] = surface;

                float weight = MaterialWeights[index].Max();
                MaxWeight[index] = weight;
                byte w = (byte)math.round(math.saturate(weight) * 255f);
                MaxWeightView[index] = new Color32(w, w, w, 255);

                // Flora species over the darkened surface source; with a density overlay the ground is dark in
                // clearings (factor 0) and bright in groves (factor 2).
                ushort species = FloraSpecies[index];
                float ground = 0.4f;
                if (DensityOverlay >= 0){
                    int x = Origin.x + index % Size * Stride, z = Origin.y + index / Size * Stride;
                    ground = 0.45f * FloraPlacement.DensityFactor(x, z, Seed, DensityOverlay, DensityWavelength, DensityContrast);
                }

                FloraView[index] = species != 0
                    ? SpeciesColor(species)
                    : new Color32((byte)(surface.r * ground), (byte)(surface.g * ground), (byte)(surface.b * ground), 255);

                HeightView[index] = HeightColor(index);

                WorldFields f = Fields[index];
                byte t = ShowTemperature == 1 ? (byte)math.round(math.saturate(f.T) * 255f) : (byte)0;
                byte c = ShowContinental == 1 ? (byte)math.round(math.saturate(f.C) * 255f) : (byte)0;
                byte hum = ShowHumidity == 1 ? (byte)math.round(math.saturate(f.F) * 255f) : (byte)0;
                ClimateView[index] = new Color32(t, c, hum, 255);
                byte e = (byte)math.round(math.saturate(f.E) * 255f);
                ErosionView[index] = new Color32(e, e, e, 255);
                byte pv = (byte)math.round(math.saturate((f.PV + 1f) * 0.5f) * 255f);
                PvView[index] = new Color32(pv, pv, pv, 255);

                CavernView[index] = MaskColor(CavernMask[index], MaskThresholds.x, new float3(1f, .6f, .15f));
                EntranceView[index] = MaskColor(EntranceMask[index], MaskThresholds.y, new float3(.3f, 1f, .35f));
                LavaView[index] = MaskColor(LavaMask[index], MaskThresholds.z, new float3(1f, .25f, .1f));
            }

            /// <summary>Grey of the mask value (−1…1); above the threshold tinted with <paramref name="tint" />.</summary>
            private static Color32 MaskColor(float value, float threshold, float3 tint){
                float g = math.saturate((value + 1f) * 0.5f);
                float3 rgb = value > threshold ? tint * (0.45f + 0.55f * g) : new float3(g * 0.6f);
                return new Color32((byte)math.round(rgb.x * 255f), (byte)math.round(rgb.y * 255f), (byte)math.round(rgb.z * 255f), 255);
            }

            /// <summary>
            ///     Height on the HeightMin..HeightMax scale, blue tint below water, red where clamped. With the
            ///     hillshade, land is lit by a Lambert light from the north-west at 45° over the slope of
            ///     the neighbouring samples (central differences, <c>2·Stride</c> blocks apart).
            /// </summary>
            private Color32 HeightColor(int index){
                if (Clamped[index] != 0) return new Color32(255, 0, 0, 255);
                float h = Height[index];
                float v = math.saturate((h - HeightMin) / math.max(1f, HeightMax - HeightMin));
                if (h < WaterLevel) return new Color32((byte)(v * 127.5f), (byte)(v * 127.5f), (byte)math.min(255f, v * 255f + 80f), 255);
                if (Hillshade == 0){
                    byte g = (byte)math.round(v * 255f);
                    return new Color32(g, g, g, 255);
                }

                int x = index % Size, y = index / Size;
                float hx0 = Height[y * Size + math.max(0, x - 1)], hx1 = Height[y * Size + math.min(Size - 1, x + 1)];
                float hy0 = Height[math.max(0, y - 1) * Size + x], hy1 = Height[math.min(Size - 1, y + 1) * Size + x];
                float dx = (math.min(Size - 1, x + 1) - math.max(0, x - 1)) * (float)Stride;
                float dy = (math.min(Size - 1, y + 1) - math.max(0, y - 1)) * (float)Stride;
                float gx = dx > 0f ? (hx1 - hx0) / dx * Exaggeration : 0f;
                float gy = dy > 0f ? (hy1 - hy0) / dy * Exaggeration : 0f;
                float3 normal = math.normalize(new float3(-gx, -gy, 1f));
                float3 light = math.normalize(new float3(-1f, 1f, math.SQRT2)); // azimuth NW (−x, +z), altitude 45°
                float shade = math.saturate(math.dot(normal, light)) / light.z; // 1 on flat ground
                float lit = math.saturate(v * (0.35f + 0.65f * shade) + 0.08f);
                byte l = (byte)math.round(lit * 255f);
                return new Color32(l, l, (byte)math.round(math.saturate(lit * 0.92f) * 255f), 255);
            }

            private Color32 BiomeColor(int biome){
                return biome >= 0 && biome < BiomeColors.Length ? BiomeColors[biome] : new Color32(0, 0, 0, 255);
            }

            /// <summary>A saturated colour per species id: golden-ratio hue steps, so neighbouring ids differ.</summary>
            public static Color32 SpeciesColor(ushort species){
                float hue = math.frac(species * 0.618034f + 0.11f);
                float3 rgb = math.saturate(math.abs(math.frac(hue + new float3(1f, 2f / 3f, 1f / 3f)) * 6f - 3f) - 1f);
                rgb = math.lerp(new float3(1f), rgb, 0.85f) * 0.95f;
                return new Color32((byte)(rgb.x * 255f), (byte)(rgb.y * 255f), (byte)(rgb.z * 255f), 255);
            }
        }
    }
}
