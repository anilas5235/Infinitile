using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    ///     land biomes under water and the implausible neighbour pairs, the connected areas of high ground (how many,
    ///     how large) and the Mountain patches by size. It also writes four maps per configuration to
    ///     <c>Temp/tuning-captures</c>: height with hillshade, hard biome, surface source with the columns under water
    ///     darkened, and the high ground with the Mountain biome marked. It prints values and sets no limits.
    ///     <para>
    ///         Every configuration is a transient copy of the shipped settings (biomes and splines copied as well), so
    ///         the assets are never written. <see cref="Candidates" /> lists the configurations.
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

        /// <summary>
        ///     The configurations by name; "Shipped" is the asset unchanged. To compare a tuning idea, add an entry that
        ///     changes the copy (fields, <c>Splines.FromJson</c>, biome targets via <see cref="BiomeOf" />). The
        ///     candidates B, C and D of 2026-11-09 are in the commit that added this test; C is now the shipped asset.
        ///     "HardClamp" is the shipped asset with the hard clamp at HeightMax of before 2026-11-10. The biome–terrain fit
        ///     candidates "Landform" / "LandformStrong" of 2026-11-10 are in commit d8ce664; Landform is in the shipped
        ///     ten-biome set since. The massif candidates A, B and C of 2026-11-12 went through the candidate file
        ///     (<see cref="TerrainLook_ReportCandidateFile" />, splines as JSON); A is the shipped asset since.
        /// </summary>
        private static readonly Dictionary<string, Action<WorldGenSettings>> Candidates = new(){
            ["Shipped"] = _ => { },
            ["HardClamp"] = s => s.HeightSoftBand = 0f
        };

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

        /// <summary>
        ///     Candidates from <c>Temp/tuning-candidates.txt</c>, so a tuning round needs no recompile. One candidate per
        ///     line: <c>Name: Jungle.TargetHeight=0.48; Mountain.TargetPV=0.6; Settings.LambdaHeight=4</c>. The part before
        ///     the dot is a biome name or <c>Settings</c>, the part after it a float, int or bool field, which may be nested
        ///     or an array element (<c>Settings.erosionProfile.scale=2400</c>, <c>Settings.GlobalOctaveWeights[1]=0.4</c>).
        ///     <c>Splines=file.json</c> loads the splines from a file in the JSON form of <see cref="TerrainSplineSet" />
        ///     (relative to <c>Temp</c>). Lines starting with # and empty lines are skipped; without the file the test
        ///     reports nothing.
        /// </summary>
        [Test]
        public void TerrainLook_ReportCandidateFile(){
            string file = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "tuning-candidates.txt");
            if (!File.Exists(file)) Assert.Ignore($"No candidate file at {file}");
            foreach (string raw in File.ReadAllLines(file)){
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int colon = line.IndexOf(':');
                Assert.Greater(colon, 0, line);
                List<Object> owned = new();
                WorldGenSettings settings = CloneShipped(owned);
                try{
                    foreach (string assignment in line[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                        Assign(settings, assignment.Trim());
                    Report(line[..colon].Trim(), settings);
                }
                finally{
                    foreach (Object o in owned) Object.DestroyImmediate(o);
                }
            }
        }

        /// <summary>
        ///     The inputs of the Terrain-first height of every sampled column, for trying spline and target candidates
        ///     outside Unity before they get a report. One dump per line of <c>Temp/tuning-dump.txt</c>:
        ///     <c>Name@stride@seed: assignments</c> with the assignments of <see cref="TerrainLook_ReportCandidateFile" />
        ///     (stride and seed are optional, 4 and the game's seed by default; the area is 1024² samples around the
        ///     origin). Writes <c>Name_fields.bin</c> (float arrays one after another: T, F, C, E, PV, the detail octaves
        ///     N_i unweighted, the peak noise P, the limited height H, the hard biome index) and <c>Name_fields.json</c>
        ///     (what the arrays are, the splines, biome targets, λ and height limits). Without the file nothing is written.
        /// </summary>
        [Test]
        public void TerrainLook_DumpFields(){
            string file = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "tuning-dump.txt");
            if (!File.Exists(file)) Assert.Ignore($"No dump file at {file}");
            foreach (string raw in File.ReadAllLines(file)){
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int colon = line.IndexOf(':');
                Assert.Greater(colon, 0, line);
                string[] head = line[..colon].Trim().Split('@');
                int stride = head.Length > 1 ? int.Parse(head[1], CultureInfo.InvariantCulture) : Stride;
                int seed = head.Length > 2 ? int.Parse(head[2], CultureInfo.InvariantCulture) : TestFixtures.Seed;
                List<Object> owned = new();
                WorldGenSettings settings = CloneShipped(owned);
                try{
                    foreach (string assignment in line[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                        Assign(settings, assignment.Trim());
                    Dump(head[0], settings, stride, seed);
                }
                finally{
                    foreach (Object o in owned) Object.DestroyImmediate(o);
                }
            }
        }

        private static void Dump(string name, WorldGenSettings settings, int stride, int seed){
            WorldGenConfig cfg = settings.ToConfig(seed);
            int n = Size * Size;
            RegionBuffers buffers = new(){
                Fields = new NativeArray<WorldFields>(n, Allocator.TempJob),
                Height = new NativeArray<float>(n, Allocator.TempJob),
                HardBiome = new NativeArray<int>(n, Allocator.TempJob)
            };
            try{
                int2 origin = new(-Size * stride / 2, -Size * stride / 2);
                WorldGenRequest request = new(){ Config = cfg, Origin = origin, Size = new int2(Size, Size), Stride = stride };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                WorldFields[] fields = buffers.Fields.ToArray();
                int octaves = cfg.OctaveCount;
                string[] arrays = new[]{ "T", "F", "C", "E", "PV" }.Concat(Enumerable.Range(0, octaves).Select(i => $"N{i}"))
                    .Concat(new[]{ "P", "H", "Hard" }).ToArray();
                float[] data = new float[arrays.Length * n];
                for (int i = 0; i < n; i++){
                    WorldFields f = fields[i];
                    float2 p = origin + new float2(i % Size, i / Size) * stride;
                    data[i] = f.T;
                    data[n + i] = f.F;
                    data[2 * n + i] = f.C;
                    data[3 * n + i] = f.E;
                    data[4 * n + i] = f.PV;
                    for (int o = 0; o < octaves; o++)
                        data[(5 + o) * n + i] = DetailOctaves.Sample(o, p, cfg.OctaveWavelengths[o], cfg.Seed);
                    data[(5 + octaves) * n + i] = PeakNoise.Sample(p, cfg.PeakWavelength, cfg.Seed);
                    data[(6 + octaves) * n + i] = buffers.Height[i];
                    data[(7 + octaves) * n + i] = buffers.HardBiome[i];
                }

                byte[] bytes = new byte[data.Length * sizeof(float)];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                Directory.CreateDirectory(OutDir);
                File.WriteAllBytes(Path.Combine(OutDir, $"{name}_fields.bin"), bytes);
                Dictionary<string, object> meta = new(){
                    ["Arrays"] = arrays,
                    ["Size"] = Size,
                    ["Stride"] = stride,
                    ["Seed"] = seed,
                    ["Origin"] = new[]{ origin.x, origin.y },
                    ["WaterLevel"] = cfg.WaterLevel,
                    ["HeightMin"] = settings.HeightMin,
                    ["HeightMax"] = settings.HeightMax,
                    ["HeightSoftBand"] = settings.HeightSoftBand,
                    ["GlobalOctaveWeights"] = settings.GlobalOctaveWeights,
                    ["Lambda"] = new[]{
                        settings.LambdaTemperature, settings.LambdaHumidity, settings.LambdaContinental, settings.LambdaErosion,
                        settings.LambdaPV, settings.LambdaHeight
                    },
                    ["Biomes"] = settings.Biomes.Biomes.Select(b => (object)new Dictionary<string, object>{
                        ["Name"] = b.name,
                        ["Target"] = new[]{
                            b.TargetTemperature, b.TargetHumidity, b.TargetContinental, b.TargetErosion, b.TargetPV, b.TargetHeight
                        }
                    }).ToList(),
                    ["Splines"] = settings.Splines.ToTree()
                };
                File.WriteAllText(Path.Combine(OutDir, $"{name}_fields.json"), CanonicalJson.Serialize(meta));
                TestContext.WriteLine($"dump {name}: {Size}² samples, stride {stride}, seed {seed}, {arrays.Length} arrays");
            }
            finally{
                buffers.Fields.Dispose();
                buffers.Height.Dispose();
                buffers.HardBiome.Dispose();
                cfg.Dispose();
            }
        }

        /// <summary>
        ///     The surface material at block resolution: 1024² columns around <see cref="CloseUpCentre" />, once with the
        ///     per-block mix (<c>MaterialBorderWavelength</c> 0) and once as shipped. Writes
        ///     <c>Border_Dither_surface.png</c> and <c>Border_Shipped_surface.png</c> and prints for either one the
        ///     columns whose material differs from the dominant one, the neighbour pairs of different material (the
        ///     border length) and the columns that differ from all four neighbours (speckles).
        /// </summary>
        [Test]
        public void TerrainLook_BorderCloseUp(){
            foreach (string variant in new[]{ "Dither", "Shipped" }){
                List<Object> owned = new();
                WorldGenSettings settings = CloneShipped(owned);
                try{
                    if (variant == "Dither") settings.MaterialBorderWavelength = 0f;
                    CloseUp(variant, settings);
                }
                finally{
                    foreach (Object o in owned) Object.DestroyImmediate(o);
                }
            }
        }

        /// <summary>Centre of the close-up of <see cref="TerrainLook_BorderCloseUp" /> in blocks.</summary>
        private static readonly int2 CloseUpCentre = new(0, 0);

        private static void CloseUp(string variant, WorldGenSettings settings){
            WorldGenConfig cfg = settings.ToConfig(TestFixtures.Seed);
            int n = Size * Size;
            RegionBuffers buffers = new(){
                Height = new NativeArray<float>(n, Allocator.TempJob),
                DominantMaterialBiome = new NativeArray<int>(n, Allocator.TempJob),
                SurfaceSource = new NativeArray<int>(n, Allocator.TempJob)
            };
            try{
                WorldGenRequest request = new(){
                    Config = cfg, Origin = CloseUpCentre - new int2(Size / 2, Size / 2), Size = new int2(Size, Size), Stride = 1
                };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                float[] height = buffers.Height.ToArray();
                int[] dominant = buffers.DominantMaterialBiome.ToArray();
                int[] surface = buffers.SurfaceSource.ToArray();
                int minority = 0, pairs = 0, speckles = 0;
                for (int z = 1; z < Size - 1; z++)
                for (int x = 1; x < Size - 1; x++){
                    int i = z * Size + x, s = surface[i];
                    if (s != dominant[i]) minority++;
                    if (s != surface[i + 1]) pairs++;
                    if (s != surface[i + Size]) pairs++;
                    if (s != surface[i + 1] && s != surface[i - 1] && s != surface[i + Size] && s != surface[i - Size]) speckles++;
                }

                string line = $"=== border close-up {variant}: {Size}² blocks around ({CloseUpCentre.x}, {CloseUpCentre.y}), " +
                              $"border wavelength {settings.MaterialBorderWavelength:F0}: not the dominant material " +
                              $"{100.0 * minority / n:F2} %, neighbour pairs of different material {pairs}, single columns {speckles}";
                TestContext.WriteLine(line);
                Directory.CreateDirectory(OutDir);
                File.WriteAllText(Path.Combine(OutDir, $"Border_{variant}_stats.txt"), line);
                Color32[] colors = settings.Biomes.Biomes.Select(b => (Color32)b.RepresentativeColor).ToArray();
                WritePng($"Border_{variant}_surface.png", BiomeView(surface, colors, height, cfg.WaterLevel));
            }
            finally{
                buffers.Height.Dispose();
                buffers.DominantMaterialBiome.Dispose();
                buffers.SurfaceSource.Dispose();
                cfg.Dispose();
            }
        }

        /// <summary>
        ///     Sets <c>Target.Field=value</c> on a biome of <paramref name="settings" /> or on the settings themselves
        ///     (nested fields and array elements included), or loads the splines for <c>Splines=file</c>.
        /// </summary>
        private static void Assign(WorldGenSettings settings, string assignment){
            if (assignment.Length == 0) return;
            string[] sides = assignment.Split('=');
            Assert.AreEqual(2, sides.Length, assignment);
            string[] path = sides[0].Trim().Split('.');
            string value = sides[1].Trim();
            if (path.Length == 1 && path[0] == "Splines"){
                string file = Path.IsPathRooted(value) ? value : Path.Combine(Directory.GetCurrentDirectory(), "Temp", value);
                settings.Splines.FromJson(File.ReadAllText(file));
                return;
            }

            Assert.GreaterOrEqual(path.Length, 2, assignment);
            object target = path[0] == "Settings" ? settings : BiomeOf(settings, path[0]);
            SetPath(target, path, 1, value, assignment);
        }

        /// <summary>
        ///     Sets the member <paramref name="path" />[<paramref name="at" />…] of <paramref name="target" /> and
        ///     returns the target, so a value type on the way is written back into its owner.
        /// </summary>
        private static object SetPath(object target, string[] path, int at, string value, string assignment){
            string token = path[at];
            int index = -1, bracket = token.IndexOf('[');
            if (bracket >= 0){
                index = int.Parse(token[(bracket + 1)..token.IndexOf(']')], CultureInfo.InvariantCulture);
                token = token[..bracket];
            }

            FieldInfo field = target.GetType().GetField(token, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(field, assignment);
            bool last = at == path.Length - 1;
            if (index >= 0){
                System.Collections.IList list = (System.Collections.IList)field.GetValue(target);
                Assert.IsNotNull(list, assignment);
                list[index] = last ? ParseValue(value, list[index].GetType()) : SetPath(list[index], path, at + 1, value, assignment);
                return target;
            }

            field.SetValue(target,
                last ? ParseValue(value, field.FieldType) : SetPath(field.GetValue(target), path, at + 1, value, assignment));
            return target;
        }

        private static object ParseValue(string value, Type type){
            if (type == typeof(bool)) return value is "1" or "true";
            float number = float.Parse(value, CultureInfo.InvariantCulture);
            return type == typeof(int) ? (int)number : number;
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

                // High ground by height alone: what a biome that takes every column above a line could look like.
                foreach (float line in new[]{ 170f, 180f, 190f, 210f }){
                    int[] high = height.Select(h => h >= line ? 0 : -1).ToArray();
                    sb.AppendLine($"high    H ≥ {line:F0}: " + Areas(PatchSizes(high, 1)[0], n));
                }

                int mountain = Array.IndexOf(names, "Mountain");
                if (mountain >= 0){
                    sb.AppendLine("ranges  Mountain: " + Areas(PatchSizes(hard, biomeCount)[mountain], n));
                    // Mountain and high ground as one mask: the massifs as seen in game (stone or high).
                    int[] massif = new int[n];
                    for (int i = 0; i < n; i++) massif[i] = hard[i] == mountain || height[i] >= 180f ? 0 : -1;
                    sb.AppendLine("ranges  Mountain or H ≥ 180: " + Areas(PatchSizes(massif, 1)[0], n));
                }

                // Boundaries of the dominant material biome, implausible pairs flagged.
                Dictionary<(int, int), int> edges = BorderEdges(dominant, biomeCount);
                long total = edges.Values.Sum();
                sb.AppendLine("borders " + string.Join(", ", edges.OrderByDescending(kv => kv.Value).Select(kv =>
                    $"{names[kv.Key.Item1]}–{names[kv.Key.Item2]} {100.0 * kv.Value / total:F1} %" +
                    (settings.Biomes.IsImplausible(kv.Key.Item1, kv.Key.Item2) ? " (IMPLAUSIBLE)" : ""))));
                long implausible = edges.Where(kv => settings.Biomes.IsImplausible(kv.Key.Item1, kv.Key.Item2))
                    .Sum(kv => (long)kv.Value);
                sb.AppendLine($"borders implausible {implausible * Stride} blocks");
                Fit(sb, settings, cfg, fields, height, hard, surface, clamped, request.Origin);
                foreach ((string name, int4 box) in Windows) Window(sb, name, box, names, height, hard, surface, request.Origin, water);

                TestContext.WriteLine(sb.ToString());
                Directory.CreateDirectory(OutDir);
                File.WriteAllText(Path.Combine(OutDir, $"{candidate}_stats.txt"), sb.ToString());

                Color32[] colors = settings.Biomes.Biomes.Select(b => (Color32)b.RepresentativeColor).ToArray();
                WritePng($"{candidate}_height.png", HeightView(height, water, cfg.HeightMin, cfg.HeightMax));
                WritePng($"{candidate}_hard.png", BiomeView(hard, colors, null, water));
                WritePng($"{candidate}_surface.png", BiomeView(surface, colors, height, water));
                WritePng($"{candidate}_high.png", HighView(height, hard, mountain, water));
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

        /// <summary>
        ///     Biome–terrain fit: per hard biome, the ground height and land slope percentiles and the share of its land
        ///     columns in each relief class (flat slope &lt; .3, hilly &lt; 1, steep) and height band (low &lt; 140,
        ///     mid &lt; 180, high), the snow-covered share of its surface, and the unclamped H of clamped columns.
        /// </summary>
        private static void Fit(StringBuilder sb, WorldGenSettings settings, in WorldGenConfig cfg, WorldFields[] fields, float[] height,
            int[] hard, int[] surface, byte[] clamped, int2 origin){
            string[] names = settings.Biomes.Names();
            float water = cfg.WaterLevel;
            List<float> over = new();
            for (int i = 0; i < height.Length; i++){
                if (clamped[i] == 0 || height[i] < cfg.WaterLevel) continue;
                float2 p = origin + new float2(i % Size, i / Size) * Stride;
                Engine.Scripts.WorldGen.Height.TerrainFirstHeight.Sample(p, fields[i], cfg, out float3 _, out float _,
                    out float unclamped);
                over.Add(unclamped);
            }

            sb.AppendLine("clamp   " + (over.Count > 0 ? Pct("unclamped H above Hmax", over) : "no column above Hmax") +
                          $"; columns at Hmax {100.0 * AtMax(height, cfg.HeightMax) / height.Length:F2} %");
            for (int b = 0; b < names.Length; b++){
                List<float> h = new(), slope = new();
                int[] relief = new int[3], band = new int[3];
                int count = 0, snow = 0, surfaceCount = 0;
                // The test config resolves no voxel ids, so the snow block is read from the asset.
                bool snowy = settings.Biomes.Biomes[b].SnowTopBlock != null;
                for (int z = 0; z < Size - 1; z++)
                for (int x = 0; x < Size - 1; x++){
                    int i = z * Size + x;
                    if (surface[i] == b && height[i] >= water){
                        surfaceCount++;
                        int2 w = origin + new int2(x, z) * Stride;
                        int top = (int)math.floor(height[i]);
                        if (snowy && cfg.EnableClimateOverrides && top + 1 >= cfg.WaterLevel &&
                            top + 1 >= Engine.Scripts.WorldGen.Core.SurfaceCover.SnowLine(w.x, w.y, cfg)) snow++;
                    }

                    if (hard[i] != b) continue;
                    count++;
                    h.Add(height[i]);
                    if (height[i] < water) continue;
                    float gx = (height[i + 1] - height[i]) / Stride, gz = (height[i + Size] - height[i]) / Stride;
                    float s = math.sqrt(gx * gx + gz * gz);
                    slope.Add(s);
                    relief[s < .3f ? 0 : s < 1f ? 1 : 2]++;
                    band[height[i] < 140f ? 0 : height[i] < 180f ? 1 : 2]++;
                }

                if (count == 0) continue;
                int land = math.max(1, slope.Count);
                sb.AppendLine($"fit     {names[b],-8} " + Pct("H", h) + " | " + Pct("slope", slope) +
                              $" | flat/hilly/steep {100 * relief[0] / land}/{100 * relief[1] / land}/{100 * relief[2] / land} %" +
                              $" | low/mid/high {100 * band[0] / land}/{100 * band[1] / land}/{100 * band[2] / land} %" +
                              $" | snow {100.0 * snow / math.max(1, surfaceCount):F1} % of its dry surface");
            }
        }

        /// <summary>
        ///     Places the world tour of 2026-11-11 named (x0, z0, x1, z1 in blocks, both ends included): the area the tour
        ///     flew over, the steep west Jungle, the Savanna that was a shallow sea, and the two Badlands spots with
        ///     Mountain ground on them.
        /// </summary>
        private static readonly (string, int4)[] Windows ={
            ("Tour", new int4(-1280, -1280, 1280, 1280)),
            ("WestJungle", new int4(-1280, -1152, -960, -576)),
            ("SouthWestSavanna", new int4(-768, -704, -640, -256)),
            ("BadlandsMeadow", new int4(896, 640, 1088, 1024)),
            ("BadlandsCap", new int4(1216, -704, 1344, -576))
        };

        /// <summary>
        ///     One window of the sampled area: per hard biome its share, ground height percentiles and the share of its
        ///     columns under water, then the surface source shares and the columns whose surface comes from another
        ///     biome than the hard one (from 0.5 % of the window).
        /// </summary>
        private static void Window(StringBuilder sb, string name, int4 box, string[] names, float[] height, int[] hard, int[] surface,
            int2 origin, float water){
            int count = names.Length, total = 0;
            List<float>[] h = Enumerable.Range(0, count).Select(_ => new List<float>()).ToArray();
            int[] wet = new int[count], surf = new int[count], surfWet = new int[count];
            Dictionary<(int, int), int> cross = new();
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++){
                int2 w = origin + new int2(x, z) * Stride;
                if (w.x < box.x || w.x > box.z || w.y < box.y || w.y > box.w) continue;
                int i = z * Size + x, b = hard[i], s = surface[i];
                if (b < 0 || s < 0) continue;
                total++;
                h[b].Add(height[i]);
                surf[s]++;
                if (height[i] < water){
                    wet[b]++;
                    surfWet[s]++;
                }

                if (s == b) continue;
                cross.TryGetValue((b, s), out int c);
                cross[(b, s)] = c + 1;
            }

            if (total == 0) return;
            sb.AppendLine($"window  {name} x {box.x}…{box.z}, z {box.y}…{box.w}: hard " + string.Join(", ",
                Enumerable.Range(0, count).Where(b => h[b].Count > 0).OrderByDescending(b => h[b].Count).Select(b => {
                    float[] v = h[b].ToArray();
                    Array.Sort(v);
                    float P(double p) => v[math.min(v.Length - 1, (int)(p * v.Length))];
                    return $"{names[b]} {100.0 * v.Length / total:F1} % (H {P(0.05):F0}/{P(0.5):F0}/{P(0.95):F0}, max {v[^1]:F0}, " +
                           $"under water {100.0 * wet[b] / v.Length:F0} %)";
                })));
            sb.AppendLine($"window  {name} surface " + string.Join(", ",
                Enumerable.Range(0, count).Where(b => surf[b] > 0).OrderByDescending(b => surf[b])
                    .Select(b => $"{names[b]} {100.0 * surf[b] / total:F1} % (under water {100.0 * surfWet[b] / surf[b]:F0} %)")) +
                          "; surface of another biome: " + string.Join(", ",
                              cross.Where(kv => kv.Value * 200 >= total).OrderByDescending(kv => kv.Value)
                                  .Select(kv => $"{names[kv.Key.Item2]} on {names[kv.Key.Item1]} {100.0 * kv.Value / total:F1} %")));
        }

        private static int AtMax(float[] height, float max){
            return height.Count(h => h >= max);
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
            List<int>[] sizes = PatchSizes(biome, count);
            patches = sizes.Select(s => s.Count).ToArray();
            cells = sizes.Select(s => s.Sum()).ToArray();
            largest = sizes.Select(s => s.Count > 0 ? s[0] : 0).ToArray();
            small = sizes.Select(s => s.Where(size => size < SmallPatchCells).Sum()).ToArray();
            medium = sizes.Select(s => s.Where(size => size < MediumPatchCells).Sum()).ToArray();
        }

        /// <summary>
        ///     The sizes in cells of the 4-connected patches of every id in [0, <paramref name="count" />), largest
        ///     first. Cells with another value belong to no patch.
        /// </summary>
        private static List<int>[] PatchSizes(int[] biome, int count){
            List<int>[] sizes = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
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

                sizes[b].Add(size);
                continue;

                void Visit(int j){
                    if (seen[j] || biome[j] != b) return;
                    seen[j] = true;
                    stack.Push(j);
                }
            }

            foreach (List<int> list in sizes) list.Sort((a, b) => b.CompareTo(a));
            return sizes;
        }

        /// <summary>Upper bounds in km² of the size classes of <see cref="Areas" />; the last class is open.</summary>
        private static readonly double[] AreaClasses ={ 0.01, 0.0655, 0.25, 1 };

        /// <summary>
        ///     The connected areas of one kind, from their sizes in cells (largest first): share of the
        ///     <paramref name="total" /> cells, how many, the largest five in km², the share the three largest hold, the
        ///     share in areas below 256 × 256 blocks, and per size class (below 0.01 km², below 256², below 0.25 km²,
        ///     below 1 km², from 1 km²) the number of areas and their share of the kind's area.
        /// </summary>
        private static string Areas(List<int> sizes, int total){
            const double km2 = Stride * Stride / 1e6;
            long cells = sizes.Sum(s => (long)s);
            if (cells == 0) return "none";
            int[] classCount = new int[AreaClasses.Length + 1];
            long[] classCells = new long[AreaClasses.Length + 1];
            foreach (int size in sizes){
                int c = 0;
                while (c < AreaClasses.Length && size * km2 >= AreaClasses[c]) c++;
                classCount[c]++;
                classCells[c] += size;
            }

            string[] labels ={ "< 0.01", "< 256²", "< 0.25", "< 1", "≥ 1 km²" };
            return $"{100.0 * cells / total:F1} % of all ({cells * km2:F2} km²), {sizes.Count} areas, largest " +
                   string.Join(" / ", sizes.Take(5).Select(s => (s * km2).ToString("F2", CultureInfo.InvariantCulture))) +
                   $" km², three largest hold {100.0 * sizes.Take(3).Sum(s => (long)s) / cells:F0} %, " +
                   $"{100.0 * sizes.Where(s => s < MediumPatchCells).Sum(s => (long)s) / cells:F0} % < 256²; by size: " +
                   string.Join(", ", Enumerable.Range(0, labels.Length)
                       .Select(c => $"{labels[c]}: {classCount[c]} ({100.0 * classCells[c] / cells:F0} %)"));
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

        /// <summary>
        ///     The high ground on a grey hillshade: Mountain columns red (dark red below 180), other columns from 180 up
        ///     yellow, every column from 210 up (the snow line) white, water blue.
        /// </summary>
        private static Color32[] HighView(float[] height, int[] hard, int mountain, float water){
            Color32[] px = new Color32[height.Length];
            float3 light = math.normalize(new float3(-1f, 1.41421f, 1f));
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++){
                int i = z * Size + x;
                float h = height[i];
                float hx = height[z * Size + math.min(x + 1, Size - 1)] - height[z * Size + math.max(x - 1, 0)];
                float hz = height[math.min(z + 1, Size - 1) * Size + x] - height[math.max(z - 1, 0) * Size + x];
                float shade = 0.45f + 0.55f * math.saturate(math.dot(math.normalize(new float3(-hx, 2f * Stride, -hz)), light));
                float3 c = h < water ? new float3(0.35f, 0.5f, 0.7f)
                    : h >= 210f ? new float3(1f, 1f, 1f)
                    : hard[i] == mountain ? h >= 180f ? new float3(0.9f, 0.25f, 0.2f) : new float3(0.55f, 0.15f, 0.25f)
                    : h >= 180f ? new float3(0.95f, 0.8f, 0.2f)
                    : new float3(0.62f, 0.62f, 0.62f);
                if (h < water) shade = 1f;
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
