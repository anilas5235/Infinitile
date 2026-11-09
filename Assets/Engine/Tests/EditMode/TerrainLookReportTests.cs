using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     What the terrain of a configuration looks like over 4096² columns around the origin (seed 1337, the game's):
    ///     the climate field percentiles, the spline outputs, the ground height distribution, the relief (slopes and
    ///     the height range of 64-block windows), the biome area shares, the mini-biome share and patch counts, the
    ///     land biomes under water and the implausible neighbour pairs. It also writes three maps per configuration
    ///     to <c>Temp/tuning-captures</c>: height with hillshade, hard biome, and surface source with the columns under
    ///     water darkened. It prints values and sets no limits.
    ///     <para>
    ///         Every configuration is a transient copy of the shipped settings (biomes and splines copied as well), so
    ///         the assets are never written. <see cref="Candidates" /> holds the tuning candidates of 2026-11-09.
    ///     </para>
    /// </summary>
    [Category(TestTiers.Report)]
    [Explicit(TestTiers.ReportReason)]
    public class TerrainLookReportTests{
        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";
        private const int Size = 1024;
        private const int Stride = 4;

        /// <summary>Patches below 64 × 64 blocks count as mini-biomes.</summary>
        private const int SmallPatchCells = 64 * 64 / (Stride * Stride);

        /// <summary>Patches below 256 × 256 blocks count as small (the isolated spots seen in game).</summary>
        private const int MediumPatchCells = 256 * 256 / (Stride * Stride);

        /// <summary>Relief windows of 64 × 64 blocks.</summary>
        private const int WindowCells = 64 / Stride;

        private static string OutDir => Path.Combine(Directory.GetCurrentDirectory(), "Temp", "tuning-captures");

        // --- Candidates ---------------------------------------------------------------------------------------------

        /// <summary>The configurations by name; "A" is the shipped asset unchanged.</summary>
        private static readonly Dictionary<string, Action<WorldGenSettings>> Candidates = new(){
            ["A"] = _ => { },
            ["B"] = s => {
                ReliefB(s);
                OceanFix(s);
                ClimateTargetsB(s);
            },
            ["C"] = s => {
                ReliefC(s);
                OceanFix(s);
                ClimateTargetsB(s);
            },
            ["D"] = s => {
                ReliefB(s);
                OceanFix(s);
            }
        };

        /// <summary>Splines with their knots inside the field ranges actually reached (E p5–p95 0.27–0.74, C p95 0.68).</summary>
        internal const string SplinesB = @"{
  ""Notes"": ""Tuning 2026-11-09 (candidate B): knots inside the reached field ranges, coast at C 0.36, relief and ridges inland."",
  ""Offset"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.05, ""Value"": 78},
    {""X"": 0.2, ""Value"": 100},
    {""X"": 0.3, ""Value"": 114},
    {""X"": 0.36, ""Value"": 123},
    {""X"": 0.42, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 124}, {""X"": 0, ""Value"": 133}, {""X"": 1, ""Value"": 146}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 125}, {""X"": 0, ""Value"": 130}, {""X"": 1, ""Value"": 137}]}},
      {""X"": 0.7, ""Value"": 129}]}},
    {""X"": 0.55, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 127}, {""X"": 0, ""Value"": 148}, {""X"": 1, ""Value"": 178}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 127}, {""X"": 0, ""Value"": 139}, {""X"": 1, ""Value"": 156}]}},
      {""X"": 0.7, ""Value"": 136}]}},
    {""X"": 0.7, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 134}, {""X"": 0, ""Value"": 168}, {""X"": 1, ""Value"": 200}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 134}, {""X"": 0, ""Value"": 152}, {""X"": 1, ""Value"": 176}]}},
      {""X"": 0.7, ""Value"": 145}]}}]},
  ""Amp"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.1, ""Value"": 8},
    {""X"": 0.33, ""Value"": 3},
    {""X"": 0.42, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.3, ""Value"": 14}, {""X"": 0.5, ""Value"": 9}, {""X"": 0.7, ""Value"": 5}]}},
    {""X"": 0.6, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.3, ""Value"": 24}, {""X"": 0.5, ""Value"": 14}, {""X"": 0.7, ""Value"": 7}]}},
    {""X"": 0.75, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.3, ""Value"": 30}, {""X"": 0.7, ""Value"": 10}]}}]},
  ""Jaggedness"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.45, ""Value"": 0},
    {""X"": 0.55, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": 0, ""Value"": 0}, {""X"": 1, ""Value"": 30}]}},
      {""X"": 0.5, ""Value"": 0}]}},
    {""X"": 0.7, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -0.5, ""Value"": 5}, {""X"": 1, ""Value"": 55}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": 0, ""Value"": 0}, {""X"": 1, ""Value"": 20}]}},
      {""X"": 0.65, ""Value"": 0}]}}]}
}";

        /// <summary>Candidate C: higher and sharper mountains, deeper sea, larger continents and erosion zones.</summary>
        internal const string SplinesC = @"{
  ""Notes"": ""Tuning 2026-11-09 (candidate C): dramatic relief, deep sea, mountains to about 240."",
  ""Offset"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.05, ""Value"": 66},
    {""X"": 0.2, ""Value"": 92},
    {""X"": 0.3, ""Value"": 112},
    {""X"": 0.36, ""Value"": 123},
    {""X"": 0.42, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 124}, {""X"": 0, ""Value"": 136}, {""X"": 1, ""Value"": 155}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 125}, {""X"": 0, ""Value"": 131}, {""X"": 1, ""Value"": 140}]}},
      {""X"": 0.7, ""Value"": 129}]}},
    {""X"": 0.55, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 126}, {""X"": 0, ""Value"": 158}, {""X"": 1, ""Value"": 200}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 127}, {""X"": 0, ""Value"": 143}, {""X"": 1, ""Value"": 165}]}},
      {""X"": 0.7, ""Value"": 138}]}},
    {""X"": 0.7, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 135}, {""X"": 0, ""Value"": 178}, {""X"": 1, ""Value"": 205}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 135}, {""X"": 0, ""Value"": 160}, {""X"": 1, ""Value"": 190}]}},
      {""X"": 0.7, ""Value"": 150}]}}]},
  ""Amp"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.1, ""Value"": 12},
    {""X"": 0.33, ""Value"": 3},
    {""X"": 0.42, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.3, ""Value"": 18}, {""X"": 0.5, ""Value"": 10}, {""X"": 0.7, ""Value"": 5}]}},
    {""X"": 0.6, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.3, ""Value"": 30}, {""X"": 0.5, ""Value"": 16}, {""X"": 0.7, ""Value"": 8}]}},
    {""X"": 0.75, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.3, ""Value"": 34}, {""X"": 0.7, ""Value"": 12}]}}]},
  ""Jaggedness"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.42, ""Value"": 0},
    {""X"": 0.55, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -0.2, ""Value"": 0}, {""X"": 1, ""Value"": 45}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": 0.2, ""Value"": 0}, {""X"": 1, ""Value"": 15}]}}]}},
    {""X"": 0.7, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.3, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -0.5, ""Value"": 10}, {""X"": 1, ""Value"": 60}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": 0, ""Value"": 0}, {""X"": 1, ""Value"": 30}]}},
      {""X"": 0.65, ""Value"": 0}]}}]}
}";

        /// <summary>Relief of B: the splines above, rougher detail octaves and wider ridges.</summary>
        private static void ReliefB(WorldGenSettings s){
            s.Splines.FromJson(SplinesB);
            s.GlobalOctaveWeights = new[]{ 1f, 0.55f, 0.3f, 0.17f, 0.1f, 0.05f };
            s.PeakWavelength = 256f;
        }

        /// <summary>Relief of C: dramatic splines, larger continents and erosion zones, ridges as in B.</summary>
        private static void ReliefC(WorldGenSettings s){
            s.Splines.FromJson(SplinesC);
            s.GlobalOctaveWeights = new[]{ 1f, 0.55f, 0.3f, 0.17f, 0.1f, 0.05f };
            s.PeakWavelength = 288f;
            s.continentalLayer.baseNoise.scale = 3200f;
            s.erosionProfile.scale = 1600f;
        }

        /// <summary>
        ///     Ocean wins where the ground is under water: height decides (λ_h 1 → 4) and Ocean's climate targets move to
        ///     the field medians, so no climate dimension keeps it out of the sea.
        /// </summary>
        private static void OceanFix(WorldGenSettings s){
            s.LambdaHeight = 4f;
            Biome ocean = BiomeOf(s, "Ocean");
            ocean.TargetHumidity = 0.35f;
            ocean.TargetContinental = 0.28f;
            ocean.TargetHeight = 0.3f;
        }

        /// <summary>
        ///     Climate targets inside the reached field ranges (T p5–p95 0.22–0.79, F 0.06–0.60), mountains by height,
        ///     and climate zones about 1.6 times larger.
        /// </summary>
        private static void ClimateTargetsB(WorldGenSettings s){
            s.temperatureProfile.scale = 1400f;
            s.humidityProfile.scale = 1100f;
            Biome plains = BiomeOf(s, "Plains");
            plains.TargetHumidity = 0.3f;
            plains.TargetErosion = 0.6f;
            plains.TargetHeight = 0.45f;
            Biome forest = BiomeOf(s, "Forest");
            forest.TargetHumidity = 0.5f;
            forest.TargetErosion = 0.5f;
            forest.TargetHeight = 0.45f;
            Biome desert = BiomeOf(s, "Desert");
            desert.TargetTemperature = 0.72f;
            desert.TargetHumidity = 0.12f;
            desert.TargetErosion = 0.65f;
            desert.TargetPV = -0.2f;
            desert.TargetHeight = 0.45f;
            Biome mountain = BiomeOf(s, "Mountain");
            mountain.TargetHumidity = 0.3f;
            mountain.TargetContinental = 0.62f;
            mountain.TargetErosion = 0.3f;
            mountain.TargetPV = 0.5f;
            mountain.TargetHeight = 0.7f;
            Biome snow = BiomeOf(s, "Snow");
            snow.TargetTemperature = 0.12f;
            snow.TargetHumidity = 0.3f;
            snow.TargetContinental = 0.55f;
            snow.TargetErosion = 0.5f;
            snow.TargetPV = 0f;
            snow.TargetHeight = 0.45f;
        }

        private static IEnumerable<string> CandidateNames => Candidates.Keys;

        [TestCaseSource(nameof(CandidateNames))]
        public void TerrainLook_Report(string candidate){
            List<Object> owned = new();
            WorldGenSettings settings = CloneShipped(owned);
            try{
                Candidates[candidate](settings);
                Report(candidate, settings);
            }
            finally{
                foreach (Object o in owned) Object.DestroyImmediate(o);
            }
        }

        /// <summary>A copy of the shipped settings with copies of its biome set, biomes and splines.</summary>
        internal static WorldGenSettings CloneShipped(List<Object> owned){
            WorldGenSettings shipped = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            Assert.IsNotNull(shipped, ShippedSettingsPath);
            WorldGenSettings settings = Object.Instantiate(shipped);
            settings.name = shipped.name;
            owned.Add(settings);

            BiomeSet set = Object.Instantiate(shipped.Biomes);
            set.name = shipped.Biomes.name;
            owned.Add(set);
            Dictionary<Biome, Biome> map = new();
            set.Biomes = shipped.Biomes.Biomes.Select(b => {
                Biome copy = Object.Instantiate(b);
                copy.name = b.name;
                owned.Add(copy);
                map[b] = copy;
                return copy;
            }).ToList();
            set.ImplausibleNeighbours = shipped.Biomes.ImplausibleNeighbours
                .Select(p => new BiomePair{ First = map[p.First], Second = map[p.Second] }).ToList();
            settings.Biomes = set;

            TerrainSplineSet splines = Object.Instantiate(shipped.Splines);
            splines.name = shipped.Splines.name;
            owned.Add(splines);
            settings.Splines = splines;
            return settings;
        }

        internal static Biome BiomeOf(WorldGenSettings settings, string name){
            Biome biome = settings.Biomes.Biomes.Find(b => b.name == name);
            Assert.IsNotNull(biome, name);
            return biome;
        }

        // --- Report -------------------------------------------------------------------------------------------------

        private static void Report(string candidate, WorldGenSettings settings){
            string[] names = settings.Biomes.Names();
            int biomeCount = names.Length;
            WorldGenConfig cfg = settings.ToConfig(TestFixtures.Seed);
            int n = Size * Size;
            RegionBuffers buffers = new(){
                Fields = new NativeArray<WorldFields>(n, Allocator.TempJob),
                Height = new NativeArray<float>(n, Allocator.TempJob),
                HardBiome = new NativeArray<int>(n, Allocator.TempJob),
                DominantMaterialBiome = new NativeArray<int>(n, Allocator.TempJob),
                SurfaceSource = new NativeArray<int>(n, Allocator.TempJob),
                Clamped = new NativeArray<byte>(n, Allocator.TempJob),
                TerrainParams = new NativeArray<float3>(n, Allocator.TempJob)
            };
            try{
                WorldGenRequest request = new(){
                    Config = cfg, Origin = new int2(-Size * Stride / 2, -Size * Stride / 2), Size = new int2(Size, Size),
                    Stride = Stride
                };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                WorldFields[] fields = buffers.Fields.ToArray();
                float[] height = buffers.Height.ToArray();
                int[] hard = buffers.HardBiome.ToArray();
                int[] dominant = buffers.DominantMaterialBiome.ToArray();
                int[] surface = buffers.SurfaceSource.ToArray();
                byte[] clamped = buffers.Clamped.ToArray();
                float3[] terrain = buffers.TerrainParams.ToArray();
                float water = cfg.WaterLevel;
                int ocean = Array.IndexOf(names, "Ocean");

                StringBuilder sb = new();
                sb.AppendLine($"=== {candidate}: 4096² blocks around the origin, stride {Stride}, seed {TestFixtures.Seed}, water {water}");
                sb.AppendLine("fields  " + Pct("T", fields.Select(f => f.T)) + " | " + Pct("F", fields.Select(f => f.F)));
                sb.AppendLine("fields  " + Pct("C", fields.Select(f => f.C)) + " | " + Pct("E", fields.Select(f => f.E)));
                sb.AppendLine("fields  " + Pct("PV", fields.Select(f => f.PV)));
                sb.AppendLine("spline  " + Pct("Offset", terrain.Select(t => t.x)) + " | " + Pct("Amp", terrain.Select(t => t.y)));
                sb.AppendLine("spline  " + Pct("J", terrain.Select(t => t.z)));
                sb.AppendLine("height  " + Pct("H", height) + $" mean {height.Average():F1}");
                float[] land = height.Where(h => h >= water).ToArray();
                float[] sea = height.Where(h => h < water).ToArray();
                sb.AppendLine($"land    share {100.0 * land.Length / n:F1} %  " +
                              (land.Length > 0 ? Pct("H land", land) + $" mean {land.Average():F1}" : ""));
                sb.AppendLine($"sea     " + (sea.Length > 0 ? Pct("depth", sea.Select(h => water - h)) : "none"));
                sb.AppendLine($"clamped {100.0 * clamped.Count(c => c != 0) / n:F2} %");

                // Relief: slope in blocks per block between neighbouring samples, and the height range per window.
                List<float> slopes = new();
                for (int z = 0; z < Size - 1; z++)
                for (int x = 0; x < Size - 1; x++){
                    int i = z * Size + x;
                    if (height[i] < water) continue;
                    float gx = (height[i + 1] - height[i]) / Stride;
                    float gz = (height[i + Size] - height[i]) / Stride;
                    slopes.Add(math.sqrt(gx * gx + gz * gz));
                }

                List<float> ranges = new();
                for (int wz = 0; wz + WindowCells <= Size; wz += WindowCells)
                for (int wx = 0; wx + WindowCells <= Size; wx += WindowCells){
                    float lo = float.MaxValue, hi = float.MinValue;
                    int landCells = 0;
                    for (int z = wz; z < wz + WindowCells; z++)
                    for (int x = wx; x < wx + WindowCells; x++){
                        float h = height[z * Size + x];
                        if (h >= water) landCells++;
                        lo = math.min(lo, h);
                        hi = math.max(hi, h);
                    }

                    if (landCells * 2 >= WindowCells * WindowCells) ranges.Add(hi - lo);
                }

                if (slopes.Count > 0)
                    sb.AppendLine("relief  " + Pct("slope land", slopes) +
                                  $" steep>1 {100.0 * slopes.Count(s => s > 1f) / slopes.Count:F1} % >2 {100.0 * slopes.Count(s => s > 2f) / slopes.Count:F1} %");
                if (ranges.Count > 0) sb.AppendLine("relief  " + Pct("range64 land", ranges));

                // Biome shares.
                sb.AppendLine("hard    " + Shares(hard, names));
                sb.AppendLine("surface " + Shares(surface, names));
                int landBiomeUnder = 0, oceanAbove = 0, underwater = 0;
                for (int i = 0; i < n; i++){
                    if (height[i] < water){
                        underwater++;
                        if (surface[i] != ocean) landBiomeUnder++;
                    }
                    else if (surface[i] == ocean) oceanAbove++;
                }

                sb.AppendLine($"water   land-biome surface under water {100.0 * landBiomeUnder / n:F1} % of all " +
                              $"({100.0 * landBiomeUnder / math.max(1, underwater):F1} % of the water columns); " +
                              $"Ocean surface above water {100.0 * oceanAbove / n:F2} %");
                int[] deepLand = new int[biomeCount];
                for (int i = 0; i < n; i++)
                    if (height[i] < water - 10 && surface[i] >= 0 && surface[i] != ocean) deepLand[surface[i]]++;
                int shallowLand = 0;
                for (int i = 0; i < n; i++)
                    if (height[i] < water && height[i] >= water - 3 && surface[i] != ocean) shallowLand++;
                sb.AppendLine($"water   land-biome surface 0–3 below water {100.0 * shallowLand / n:F1} % of all");
                sb.AppendLine("water   land biomes >10 below water: " + string.Join(", ",
                    Enumerable.Range(0, biomeCount).Where(b => deepLand[b] > 0)
                        .Select(b => $"{names[b]} {100.0 * deepLand[b] / n:F1} %")));

                // Patches of the hard class (blending only dithers the surface along the borders).
                Patches(hard, biomeCount, out int[] patchCount, out int[] smallCells, out int[] cells, out int[] largest,
                    out int[] mediumCells);
                sb.AppendLine($"patches mini (< 64×64) {100.0 * smallCells.Sum() / n:F2} %, small (< 256×256) {100.0 * mediumCells.Sum() / n:F2} % of all columns; per biome: " +
                              string.Join(", ", Enumerable.Range(0, biomeCount).Where(b => cells[b] > 0).Select(b =>
                                  $"{names[b]} {patchCount[b]} patches, {100.0 * smallCells[b] / cells[b]:F0} % mini, " +
                                  $"{100.0 * mediumCells[b] / cells[b]:F0} % < 256², " +
                                  $"largest {largest[b] * Stride * Stride / 1e6:F2} km²")));

                // Boundaries of the dominant material biome, implausible pairs flagged.
                Dictionary<(int, int), int> edges = BorderEdges(dominant, biomeCount);
                long total = edges.Values.Sum();
                sb.AppendLine("borders " + string.Join(", ", edges.OrderByDescending(kv => kv.Value).Select(kv =>
                    $"{names[kv.Key.Item1]}–{names[kv.Key.Item2]} {100.0 * kv.Value / total:F1} %" +
                    (settings.Biomes.IsImplausible(kv.Key.Item1, kv.Key.Item2) ? " (IMPLAUSIBLE)" : ""))));
                long implausible = edges.Where(kv => settings.Biomes.IsImplausible(kv.Key.Item1, kv.Key.Item2))
                    .Sum(kv => (long)kv.Value);
                sb.AppendLine($"borders implausible {implausible * Stride} blocks");

                TestContext.WriteLine(sb.ToString());
                Directory.CreateDirectory(OutDir);
                File.WriteAllText(Path.Combine(OutDir, $"{candidate}_stats.txt"), sb.ToString());

                Color32[] colors = settings.Biomes.Biomes.Select(b => (Color32)b.RepresentativeColor).ToArray();
                WritePng($"{candidate}_height.png", HeightView(height, water, cfg.HeightMin, cfg.HeightMax));
                WritePng($"{candidate}_hard.png", BiomeView(hard, colors, null, water));
                WritePng($"{candidate}_surface.png", BiomeView(surface, colors, height, water));
            }
            finally{
                buffers.Fields.Dispose();
                buffers.Height.Dispose();
                buffers.HardBiome.Dispose();
                buffers.DominantMaterialBiome.Dispose();
                buffers.SurfaceSource.Dispose();
                buffers.Clamped.Dispose();
                buffers.TerrainParams.Dispose();
                cfg.Dispose();
            }
        }

        private static string Pct(string label, IEnumerable<float> values){
            float[] v = values.ToArray();
            Array.Sort(v);
            if (v.Length == 0) return $"{label}: none";
            float P(double p) => v[math.min(v.Length - 1, (int)(p * v.Length))];
            return $"{label} p5 {P(0.05):F2} p25 {P(0.25):F2} p50 {P(0.5):F2} p75 {P(0.75):F2} p95 {P(0.95):F2} max {v[^1]:F2}";
        }

        private static string Shares(int[] biome, string[] names){
            int[] count = new int[names.Length];
            foreach (int b in biome)
                if (b >= 0 && b < names.Length) count[b]++;
            return string.Join(", ", Enumerable.Range(0, names.Length)
                .Select(b => $"{names[b]} {100.0 * count[b] / biome.Length:F1} %"));
        }

        /// <summary>4-connected single-biome patches: count, cells in patches below the mini size, cells, largest patch.</summary>
        private static void Patches(int[] biome, int count, out int[] patches, out int[] small, out int[] cells,
            out int[] largest, out int[] medium){
            medium = new int[count];
            patches = new int[count];
            small = new int[count];
            cells = new int[count];
            largest = new int[count];
            bool[] seen = new bool[biome.Length];
            Stack<int> stack = new();
            for (int start = 0; start < biome.Length; start++){
                int b = biome[start];
                if (seen[start] || b < 0 || b >= count) continue;
                int size = 0;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0){
                    int i = stack.Pop();
                    size++;
                    int x = i % Size, z = i / Size;
                    if (x > 0) Visit(i - 1);
                    if (x < Size - 1) Visit(i + 1);
                    if (z > 0) Visit(i - Size);
                    if (z < Size - 1) Visit(i + Size);
                }

                patches[b]++;
                cells[b] += size;
                largest[b] = math.max(largest[b], size);
                if (size < SmallPatchCells) small[b] += size;
                if (size < MediumPatchCells) medium[b] += size;
                continue;

                void Visit(int j){
                    if (seen[j] || biome[j] != b) return;
                    seen[j] = true;
                    stack.Push(j);
                }
            }
        }

        private static Dictionary<(int, int), int> BorderEdges(int[] biome, int count){
            Dictionary<(int, int), int> edges = new();
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++){
                int i = z * Size + x;
                if (x + 1 < Size) Add(biome[i], biome[i + 1]);
                if (z + 1 < Size) Add(biome[i], biome[i + Size]);
            }

            return edges;

            void Add(int a, int b){
                if (a == b || a < 0 || b < 0 || a >= count || b >= count) return;
                (int, int) key = (math.min(a, b), math.max(a, b));
                edges.TryGetValue(key, out int c);
                edges[key] = c + 1;
            }
        }

        // --- Images (row 0 is the south edge, as in the preview: +z up, +x right) ---------------------------------

        private static Color32[] HeightView(float[] height, float water, float hMin, float hMax){
            Color32[] px = new Color32[height.Length];
            float3 light = math.normalize(new float3(-1f, 1.41421f, 1f)); // from the NW at 45°
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++){
                int i = z * Size + x;
                float h = height[i];
                float hx = height[z * Size + math.min(x + 1, Size - 1)] - height[z * Size + math.max(x - 1, 0)];
                float hz = height[math.min(z + 1, Size - 1) * Size + x] - height[math.max(z - 1, 0) * Size + x];
                float3 normal = math.normalize(new float3(-hx, 2f * Stride, -hz));
                float shade = 0.35f + 0.65f * math.saturate(math.dot(normal, light));
                float3 c;
                if (h < water){
                    float depth = math.saturate((water - h) / 60f);
                    c = math.lerp(new float3(0.45f, 0.7f, 0.85f), new float3(0.05f, 0.12f, 0.35f), depth);
                    shade = 0.7f + 0.3f * shade;
                }
                else{
                    float t = math.saturate((h - water) / math.max(1f, hMax - water));
                    c = t < 0.15f ? math.lerp(new float3(0.33f, 0.55f, 0.25f), new float3(0.55f, 0.6f, 0.3f), t / 0.15f)
                        : t < 0.45f ? math.lerp(new float3(0.55f, 0.6f, 0.3f), new float3(0.55f, 0.45f, 0.32f), (t - 0.15f) / 0.3f)
                        : t < 0.75f ? math.lerp(new float3(0.55f, 0.45f, 0.32f), new float3(0.6f, 0.6f, 0.6f), (t - 0.45f) / 0.3f)
                        : math.lerp(new float3(0.6f, 0.6f, 0.6f), new float3(1f, 1f, 1f), (t - 0.75f) / 0.25f);
                }

                c *= shade;
                px[i] = new Color32((byte)(math.saturate(c.x) * 255), (byte)(math.saturate(c.y) * 255),
                    (byte)(math.saturate(c.z) * 255), 255);
            }

            return px;
        }

        private static Color32[] BiomeView(int[] biome, Color32[] colors, float[] height, float water){
            Color32[] px = new Color32[biome.Length];
            for (int i = 0; i < biome.Length; i++){
                int b = biome[i];
                Color32 c = b >= 0 && b < colors.Length ? colors[b] : new Color32(0, 0, 0, 255);
                if (height != null && height[i] < water){
                    // Under water: darkened towards blue, so land materials on the sea floor stay visible.
                    c = new Color32((byte)(c.r * 0.45f), (byte)(c.g * 0.45f), (byte)(c.b * 0.45f + 60), 255);
                }

                px[i] = c;
            }

            return px;
        }

        private static void WritePng(string file, Color32[] pixels){
            Texture2D tex = new(Size, Size, TextureFormat.RGBA32, false);
            try{
                tex.SetPixels32(pixels);
                tex.Apply(false);
                File.WriteAllBytes(Path.Combine(OutDir, file), tex.EncodeToPNG());
            }
            finally{
                Object.DestroyImmediate(tex);
            }
        }
    }
}
