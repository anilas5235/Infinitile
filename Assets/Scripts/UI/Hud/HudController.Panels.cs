using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Render;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Player;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UIElements;
using static System.FormattableString;

namespace UI{
    /// <summary>
    ///     The F3 debug panels. Left: the player, the world under them and the block they look at (the same ray and
    ///     reach as breaking). Right: frame, streaming, the engine's GPU memory and the culling pages per layer.
    /// </summary>
    public partial class HudController{
        private static readonly string[] ClimateNames = { "T", "F", "C", "E", "W" };
        private static readonly string[] QueueNames = { "data", "light", "mesh", "bake" };

        private readonly List<CullingLayerStats> _cullingStats = new();
        private readonly List<CullingRow> _cullingRows = new();
        private readonly Label[] _climate = new Label[5];
        private readonly Label[] _queues = new Label[4];

        private Label _position, _facing, _chunk, _blockInChunk, _playerMode, _seed;
        private Label _biome, _ground, _waterLevel, _aboveGround;
        private Label _fps, _frameMs, _frameAverage, _frameCap, _budget, _chunks, _drawDistance;
        private Label _gpuTotal, _gpuStore, _gpuUsage, _gpuGrows, _gpuRender, _gpuSlots, _gpuOther, _vram;
        private VisualElement _gpuBarFill, _gpuBarPeak, _vramRow, _culling;

        private Label _lookDistance, _lookName, _lookId, _lookBlock, _lookFace, _lookChunk, _lookLayer, _lookFacing;
        private Label _lookLight, _lookAttenuation, _lookEmits, _lookBiome, _lookEmpty;
        private VisualElement _lookBody, _lookFacingRow, _lookAttenuationRow, _lookEmitsRow, _lookFlags;
        private BlockIcon _lookSwatch;
        private ushort _lookShownId = ushort.MaxValue;

        private float _frameTimeSum;
        private int _frames;
        private ProfilerRecorder _gfxMemory;
        private float _nextRefresh;

        private void BindPanels(){
            _playerMode = Root.Q<Label>("player-mode");
            _position = Root.Q<Label>("position");
            _facing = Root.Q<Label>("facing");
            _chunk = Root.Q<Label>("chunk");
            _blockInChunk = Root.Q<Label>("block-in-chunk");
            _seed = Root.Q<Label>("seed");
            _biome = Root.Q<Label>("biome");
            _ground = Root.Q<Label>("ground");
            _waterLevel = Root.Q<Label>("water-level");
            _aboveGround = Root.Q<Label>("above-ground");
            FillCells(Root.Q("climate"), ClimateNames, _climate);

            _lookDistance = Root.Q<Label>("look-distance");
            _lookBody = Root.Q("look-body");
            _lookEmpty = Root.Q<Label>("look-empty");
            _lookSwatch = new BlockIcon();
            _lookSwatch.style.flexGrow = 1;
            Root.Q("look-swatch").Add(_lookSwatch);
            _lookName = Root.Q<Label>("look-name");
            _lookId = Root.Q<Label>("look-id");
            _lookBlock = Root.Q<Label>("look-block");
            _lookFace = Root.Q<Label>("look-face");
            _lookChunk = Root.Q<Label>("look-chunk");
            _lookLayer = Root.Q<Label>("look-layer");
            _lookFacingRow = Root.Q("look-facing-row");
            _lookFacing = Root.Q<Label>("look-facing");
            _lookFlags = Root.Q("look-flags");
            _lookLight = Root.Q<Label>("look-light");
            _lookAttenuationRow = Root.Q("look-attenuation-row");
            _lookAttenuation = Root.Q<Label>("look-attenuation");
            _lookEmitsRow = Root.Q("look-emits-row");
            _lookEmits = Root.Q<Label>("look-emits");
            _lookBiome = Root.Q<Label>("look-biome");

            _fps = Root.Q<Label>("fps");
            _frameMs = Root.Q<Label>("frame-ms");
            _frameAverage = Root.Q<Label>("frame-average");
            _frameCap = Root.Q<Label>("frame-cap");
            _budget = Root.Q<Label>("budget");
            _chunks = Root.Q<Label>("chunks");
            _drawDistance = Root.Q<Label>("draw-distance");
            FillCells(Root.Q("queues"), QueueNames, _queues);

            _gpuTotal = Root.Q<Label>("gpu-total");
            _gpuStore = Root.Q<Label>("gpu-store");
            _gpuBarFill = Root.Q("gpu-bar-fill");
            _gpuBarPeak = Root.Q("gpu-bar-peak");
            _gpuUsage = Root.Q<Label>("gpu-usage");
            _gpuGrows = Root.Q<Label>("gpu-grows");
            _gpuRender = Root.Q<Label>("gpu-render");
            _gpuSlots = Root.Q<Label>("gpu-slots");
            _gpuOther = Root.Q<Label>("gpu-other");
            _vramRow = Root.Q("vram-row");
            _vram = Root.Q<Label>("vram");
            _culling = Root.Q("culling");
        }

        /// <summary>A row of small cells (a name over a value); the value labels go into <paramref name="values" />.</summary>
        private static void FillCells(VisualElement parent, string[] names, Label[] values){
            parent.Clear();
            for (int i = 0; i < names.Length; i++){
                VisualElement cell = new(){ pickingMode = PickingMode.Ignore };
                cell.AddToClassList("cell");
                Label key = new(names[i]);
                key.AddToClassList("cell__k");
                values[i] = new Label("-");
                values[i].AddToClassList("cell__v");
                cell.Add(key);
                cell.Add(values[i]);
                parent.Add(cell);
            }
        }

        // ── Player and world ─────────────────────────────────────────────────

        private void RefreshPlayerAndWorld(){
            Camera cam = Camera.main;
            VoxelWorld world = VoxelWorld.Instance;
            if (!cam || !world) return;

            Vector3 pos = cam.transform.position;
            int3 block = (int3)math.floor((float3)pos);
            _playerMode.text = _movement ? _movement.Mode.ToString().ToLowerInvariant() : "";
            _position.text = Invariant($"{pos.x:F1}  {pos.y:F1}  {pos.z:F1}");
            _facing.text = Facing(cam.transform.forward);
            int2 chunk = VoxelConstants.WorldToChunkPos(block);
            _chunk.text = $"{chunk.x}  {chunk.y}";
            _blockInChunk.text = $"{block.x - chunk.x * VoxelConstants.ChunkWidth}  {block.y}  " +
                                 $"{block.z - chunk.y * VoxelConstants.ChunkDepth}";
            _seed.text = $"seed {world.Settings.Seed}";

            WorldGenSettings worldGen = world.Settings.WorldGen;
            _waterLevel.text = worldGen ? $"{worldGen.WaterLevel}" : "-";
            if (world.TrySampleColumn(block.x, block.z, out ColumnSample sample)){
                string surface = world.BiomeName(sample.SurfaceSource) ?? "?";
                string hard = world.BiomeName(sample.HardBiome) ?? "?";
                _biome.text = surface == hard ? surface : $"{surface} (hard {hard})";
                _ground.text = $"{sample.ColumnHeight}";
                _aboveGround.text = $"{block.y - sample.ColumnHeight}";
                WorldFields f = sample.Fields;
                _climate[0].text = Invariant($"{f.T:F2}");
                _climate[1].text = Invariant($"{f.F:F2}");
                _climate[2].text = Invariant($"{f.C:F2}");
                _climate[3].text = Invariant($"{f.E:F2}");
                _climate[4].text = Invariant($"{f.W:F2}");
            }
            else{
                _biome.text = _ground.text = _aboveGround.text = "-";
                foreach (Label cell in _climate) cell.text = "-";
            }
        }

        private static string Facing(Vector3 forward){
            if (math.abs(forward.x) > math.abs(forward.z)) return forward.x > 0f ? "+x" : "-x";
            return forward.z > 0f ? "+z" : "-z";
        }

        // ── Looked-at block ──────────────────────────────────────────────────

        private void RefreshLookedAt(){
            VoxelWorld world = VoxelWorld.Instance;
            VoxelRegistry registry = Registry();
            bool hit = voxelEditor && world && registry != null &&
                       voxelEditor.TryGetLookedAt(out Vector3Int position, out RaycastHit ray) &&
                       ShowLookedAt(world, registry, position, ray);
            Show(_lookBody, hit);
            Show(_lookEmpty, !hit);
            if (!hit) _lookDistance.text = Invariant($"reach {VoxelEditor.Reach:0.#} m");
        }

        private bool ShowLookedAt(VoxelWorld world, VoxelRegistry registry, Vector3Int position, RaycastHit ray){
            int3 p = new(position.x, position.y, position.z);
            ushort id = world.GetVoxel(p);
            if (id == 0 || !registry.TryGet(id, out Voxel voxel)) return false;
            ushort baseId = registry.BaseOf(id);

            _lookDistance.text = Invariant($"{ray.distance:F1} m");
            if (id != _lookShownId){
                _lookShownId = id;
                SetIcon(_lookSwatch, voxel);
                _lookName.text = registry.TryGetName(baseId, out var name) ? name.ToString() : voxel.name;
                _lookId.text = baseId == id ? $"id {id}" : $"id {id} · base {baseId}";
                _lookLayer.text = $"{voxel.meshLayer} · {(voxel.shape ? voxel.shape.name : "-")}";
                FillFlags(voxel);
                _lookAttenuation.text = $"{voxel.lightAttenuation}";
                Show(_lookAttenuationRow, voxel.lightAttenuation < VoxelLightLimits.MaxLevel);
                Vector3Int e = voxel.lightEmission;
                _lookEmits.text = $"{e.x}  {e.y}  {e.z}";
                Show(_lookEmitsRow, e != Vector3Int.zero);
                byte orientation = registry.OrientationOf(id);
                _lookFacing.text = OrientationText(voxel.facing, orientation);
                Show(_lookFacingRow, voxel.facing != VoxelFacing.None || voxel.topBottom);
            }

            _lookBlock.text = $"{p.x}  {p.y}  {p.z}";
            _lookFace.text = FaceOf(ray.normal);
            int2 chunk = VoxelConstants.WorldToChunkPos(p);
            _lookChunk.text = $"{chunk.x} {chunk.y} · {p.x - chunk.x * VoxelConstants.ChunkWidth} {p.y} " +
                              $"{p.z - chunk.y * VoxelConstants.ChunkDepth}";
            // the light the face is lit with: the air in front of it
            Vector3 front = ray.point + ray.normal * 0.5f;
            int3 lit = (int3)math.floor((float3)front);
            _lookLight.text = world.TryGetLight(lit, out ushort light) ? LightText(light) : "-";
            _lookBiome.text = world.TrySampleColumn(p.x, p.z, out ColumnSample sample)
                ? world.BiomeName(sample.SurfaceSource) ?? "?"
                : "-";
            return true;
        }

        private void FillFlags(Voxel voxel){
            _lookFlags.Clear();
            AddTag(voxel.collision ? "collision" : "no collision", false);
            if (voxel.unbreakable) AddTag("unbreakable", true);
        }

        private void AddTag(string text, bool warn){
            Label tag = new(text);
            tag.AddToClassList("tag");
            if (warn) tag.AddToClassList("tag--warn");
            _lookFlags.Add(tag);
        }

        private static string LightText(ushort light){
            int r = VoxelLight.GetRed(light), g = VoxelLight.GetGreen(light), b = VoxelLight.GetBlue(light);
            string block = r == g && g == b ? $"{r}" : $"{r} {g} {b}";
            return $"sky {VoxelLight.GetSky(light)} · block {block}";
        }

        private static string FaceOf(Vector3 normal){
            if (normal.y > 0.5f) return "top";
            if (normal.y < -0.5f) return "bottom";
            if (math.abs(normal.x) > math.abs(normal.z)) return normal.x > 0f ? "+x" : "-x";
            return normal.z > 0f ? "+z" : "-z";
        }

        /// <summary>The way a turned voxel faces: its axis, or where its front points, and the upper half.</summary>
        private static string OrientationText(VoxelFacing facing, byte orientation){
            int index = orientation & VoxelOrientation.FacingMask;
            string text = facing switch{
                VoxelFacing.Axis => index switch{
                    VoxelOrientation.AxisX => "axis x",
                    VoxelOrientation.AxisZ => "axis z",
                    _ => "axis y"
                },
                VoxelFacing.Horizontal or VoxelFacing.Full => index switch{
                    VoxelOrientation.FrontPosX => "front +x",
                    VoxelOrientation.FrontNegZ => "front -z",
                    VoxelOrientation.FrontNegX => "front -x",
                    VoxelOrientation.FrontPosY => "front up",
                    VoxelOrientation.FrontNegY => "front down",
                    _ => "front +z"
                },
                _ => "-"
            };
            if ((orientation & VoxelOrientation.UpsideDown) != 0) text += " · upper";
            return text;
        }

        // ── Frame, streaming, GPU memory, culling ────────────────────────────

        private void RefreshPerformance(){
            float ms = _frames > 0 ? _frameTimeSum / _frames * 1000f : 0f;
            _fps.text = ms > 0f ? Invariant($"{1000f / ms:F0} FPS") : "- FPS";
            _frameMs.text = ms > 0f ? Invariant($"{ms:F1} ms") : "";
            _frameMs.EnableInClassList("frame__ms--slow", ms > 1000f / 120f);
            _frameAverage.text = Invariant($"avg {refreshInterval:0.##} s");
            _frameCap.text = QualitySettings.vSyncCount > 0 ? "VSync"
                : Application.targetFrameRate > 0 ? $"{Application.targetFrameRate} fps"
                : "none";

            VoxelWorld world = VoxelWorld.Instance;
            if (world){
                float budget = world.MainThreadBudgetMs;
                _budget.text = budget > 0f ? Invariant($"{budget:0.##} ms") : "off";
                WorldStats stats = world.Stats;
                _chunks.text = $"{stats.LoadedChunks}";
                _drawDistance.text = $"draw {world.DrawDistance}";
                _queues[0].text = $"{stats.DataQueue}";
                _queues[1].text = $"{stats.LightQueue}";
                _queues[2].text = $"{stats.MeshQueue}";
                _queues[3].text = $"{stats.BakeQueue}";
            }

            RefreshGpuMemory();
            RefreshCulling();
        }

        /// <summary>
        ///     The engine's own GPU buffers (every build) and, where Unity counts it (Editor, development players),
        ///     Unity's graphics memory.
        /// </summary>
        private void RefreshGpuMemory(){
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            GpuMemory gpu = renderer ? renderer.GpuMemory : default;
            _gpuTotal.text = Invariant($"{Mb(gpu.TotalBytes):F0} MB");
            _gpuStore.text = Invariant($"{Mb(gpu.IntervalStoreBytes):F0} MB");
            _gpuBarFill.style.width = Length.Percent(math.saturate(gpu.IntervalStoreUsed) * 100f);
            _gpuBarPeak.style.left = Length.Percent(math.saturate(gpu.IntervalStorePeak) * 100f);
            _gpuUsage.text = Invariant($"{gpu.IntervalStoreUsed * 100f:F0} % used · peak {gpu.IntervalStorePeak * 100f:F0} %");
            _gpuGrows.text = $"{gpu.IntervalStoreGrowCount} grow{(gpu.IntervalStoreGrowCount == 1 ? "" : "s")}";
            _gpuRender.text = $"{gpu.SolidBuffers} / {gpu.TransparentBuffers} / {gpu.FoliageBuffers} · " +
                              Invariant($"{Mb(gpu.RenderBufferBytes):F0} MB");
            _gpuSlots.text = gpu.BuildSlots > 0
                ? Invariant($"{gpu.BuildSlots} × {Mb(gpu.BuildSlotBytes) / gpu.BuildSlots:F1} MB")
                : "-";
            _gpuOther.text = Invariant($"{Mb(gpu.OtherBytes):F0} MB");

            Show(_vramRow, _gfxMemory.Valid);
            if (_gfxMemory.Valid)
                _vram.text = $"{_gfxMemory.LastValue / (1024 * 1024)} / {SystemInfo.graphicsMemorySize} MB";
        }

        private void RefreshCulling(){
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            if (renderer){
                renderer.CollectCullingStats = true;
                renderer.GetCullingStats(_cullingStats);
            }
            else{
                _cullingStats.Clear();
            }

            while (_cullingRows.Count < _cullingStats.Count) _cullingRows.Add(new CullingRow(_culling));
            for (int i = 0; i < _cullingRows.Count; i++){
                bool used = i < _cullingStats.Count;
                Show(_cullingRows[i].Row, used);
                if (used) _cullingRows[i].Set(_cullingStats[i]);
            }
        }

        private static float Mb(long bytes){
            return bytes / (1024f * 1024f);
        }

        /// <summary>One layer of the culling table: name, visible, shadow and allocated pages.</summary>
        private class CullingRow{
            public readonly VisualElement Row;
            private readonly Label _name, _visible, _shadow, _allocated;

            public CullingRow(VisualElement table){
                Row = new VisualElement{ pickingMode = PickingMode.Ignore };
                Row.AddToClassList("tr");
                _name = Cell("tr__name");
                _visible = Cell("tr__n");
                _shadow = Cell("tr__n");
                _allocated = Cell("tr__n");
                table.Add(Row);
            }

            private Label Cell(string className){
                Label label = new(){ pickingMode = PickingMode.Ignore };
                label.AddToClassList(className);
                Row.Add(label);
                return label;
            }

            public void Set(CullingLayerStats stats){
                _name.text = stats.Buffers == 1 ? stats.Layer : $"{stats.Layer} ({stats.Buffers})";
                _visible.text = $"{stats.VisiblePages}";
                _shadow.text = $"{stats.ShadowPages}";
                _allocated.text = $"{stats.AllocatedPages}";
            }
        }
    }
}
