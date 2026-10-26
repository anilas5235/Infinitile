using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;
using VoxelSo = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Cross-section of the preview: the final voxels of a vertical slice from <see cref="WorldGenApi.SampleSlice" />
    ///     (the passes the chunk job runs), coloured with the average colour of each voxel's side texture. Air above
    ///     the surface before carving is sky, air below it is cave. Block ids come from a local id table, since the
    ///     preview has no voxel registry; the table persists, so ids stay stable between builds.
    /// </summary>
    internal sealed class PreviewSlice : IDisposable{
        private static readonly Color32 Sky = new(58, 66, 84, 255);
        private static readonly Color32 Cave = new(12, 10, 10, 255);
        private static readonly Color32 Unknown = new(255, 0, 255, 255);

        private readonly LocalVoxelIds _ids = new();
        private readonly Dictionary<VoxelSo, Color32> _colors = new();

        private WorldGenConfig _config; // owned; rebuilt per build
        private NativeArray<ushort> _jobVoxels;
        private NativeArray<int> _jobHeights;
        private ushort[] _voxels = Array.Empty<ushort>();
        private int[] _heights = Array.Empty<int>();
        private Texture2D _texture;

        // --- Result of the last finished build ------------------------------------------------------------------------
        public string Error { get; private set; }
        public bool HasResult { get; private set; }
        public SliceAxis Axis { get; private set; }
        public int Coordinate { get; private set; }
        public int Start { get; private set; }
        public int Width { get; private set; }
        public int YMin { get; private set; }
        public int YMax { get; private set; }

        /// <summary>Carved voxels (cave air and lava below the surface before carving) of the slice.</summary>
        public int Carved { get; private set; }

        public Texture2D Texture => _texture;

        /// <summary>
        ///     Builds a config with local block ids from <paramref name="asset" /> and schedules the slice of
        ///     <paramref name="width" /> columns from <paramref name="start" /> along <paramref name="axis" /> and its
        ///     surface heights. Returns false with <see cref="Error" /> set when the config cannot be built. The
        ///     previous config is disposed here, so no job of the previous build may still run.
        /// </summary>
        public bool Schedule(WorldGenSettings asset, int seed, SliceAxis axis, int coordinate, int start, int width,
            int yMin, int yMax, bool flora, out JobHandle handle){
            handle = default;
            DisposeJobData();
            try{
                _config = asset.ToConfigWithVoxelIds(seed, _ids.Id);
                Error = null;
            }
            catch (Exception e){
                Error = e.Message;
                HasResult = false;
                return false;
            }

            Axis = axis;
            Coordinate = coordinate;
            Start = start;
            Width = width;
            YMin = yMin;
            YMax = yMax;

            int2 origin = axis == SliceAxis.X ? new int2(start, coordinate) : new int2(coordinate, start);
            int2 size = axis == SliceAxis.X ? new int2(width, 1) : new int2(1, width);
            WorldGenRequest request = new(){ Config = _config, Origin = origin, Size = size, Stride = 1 };
            _jobVoxels = new NativeArray<ushort>(width * (yMax - yMin + 1), Allocator.Persistent);
            _jobHeights = new NativeArray<int>(width, Allocator.Persistent);
            RegionBuffers buffers = new(){ ColumnHeight = _jobHeights };
            JobHandle heights = WorldGenApi.SampleRegion(request, ref buffers);
            JobHandle voxels = WorldGenApi.SampleSlice(request, axis, coordinate, yMin, yMax, ref _jobVoxels, flora);
            handle = JobHandle.CombineDependencies(heights, voxels);
            return true;
        }

        /// <summary>After the scheduled handle completed: colours the slice and keeps its voxels for <see cref="VoxelAt" />.</summary>
        public void Finish(){
            if (!_jobVoxels.IsCreated) return;
            _voxels = _jobVoxels.ToArray();
            _heights = _jobHeights.ToArray();
            DisposeJobData(false);

            Color32[] palette = new Color32[_ids.Count];
            for (int id = 1; id < palette.Length; id++) palette[id] = ColorOf(_ids.Voxel((ushort)id));
            ushort lava = _config.Caves.LavaVoxel;

            int rows = YMax - YMin + 1;
            Color32[] pixels = new Color32[Width * rows];
            int carved = 0;
            for (int r = 0; r < rows; r++)
            for (int i = 0; i < Width; i++){
                int index = r * Width + i;
                ushort v = _voxels[index];
                bool below = YMin + r <= _heights[i];
                if (below && (v == 0 || lava != 0 && v == lava)) carved++;
                pixels[index] = v == 0 ? below ? Cave : Sky : v < palette.Length ? palette[v] : Unknown;
            }

            Carved = carved;
            if (!_texture || _texture.width != Width || _texture.height != rows){
                PreviewPass.DestroyTexture(ref _texture);
                _texture = new Texture2D(Width, rows, TextureFormat.RGBA32, false){
                    name = "WorldGenPreviewSlice",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(false);
            HasResult = true;
        }

        /// <summary>Voxel asset (null = air) and surface height of sample <paramref name="i" /> at height <paramref name="y" />.</summary>
        public (VoxelSo voxel, int surface) VoxelAt(int i, int y){
            if (!HasResult || i < 0 || i >= Width || y < YMin || y > YMax) return (null, 0);
            ushort id = _voxels[(y - YMin) * Width + i];
            return (_ids.Voxel(id), _heights[i]);
        }

        /// <summary>Average colour of the voxel's side texture, weighted by alpha; cached per voxel.</summary>
        private Color32 ColorOf(VoxelSo voxel){
            if (!voxel) return Unknown;
            if (_colors.TryGetValue(voxel, out Color32 color)) return color;
            Texture2D texture = null;
            try{
                texture = voxel.GetDisplayTexture(QuadDrawCondition.Forward);
            }
            catch (Exception){
                // a mode without a side texture (unique quads not loaded): fall back below
            }

            color = texture ? Average(texture) : Unknown;
            _colors[voxel] = color;
            return color;
        }

        private static Color32 Average(Texture2D texture){
            Color32[] pixels;
            if (texture.isReadable){
                pixels = texture.GetPixels32(Mathf.Min(texture.mipmapCount - 1, 3)); // a small mip is enough
            }
            else{
                // Not readable: draw it into a small render texture and read that back.
                RenderTexture rt = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32);
                RenderTexture previous = RenderTexture.active;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                Texture2D copy = new(16, 16, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                pixels = copy.GetPixels32();
                Object.DestroyImmediate(copy);
            }

            float4 sum = float4.zero;
            foreach (Color32 p in pixels){
                float a = p.a / 255f;
                sum += new float4(p.r * a, p.g * a, p.b * a, a);
            }

            if (sum.w <= 0f) return Unknown;
            float3 rgb = sum.xyz / sum.w;
            return new Color32((byte)rgb.x, (byte)rgb.y, (byte)rgb.z, 255);
        }

        private void DisposeJobData(bool config = true){
            if (_jobVoxels.IsCreated) _jobVoxels.Dispose();
            if (_jobHeights.IsCreated) _jobHeights.Dispose();
            if (!config) return;
            _config.Dispose(); // safe on a default instance
            _config = default;
        }

        /// <summary>Releases everything; no job of this slice may still run.</summary>
        public void Dispose(){
            DisposeJobData();
            PreviewPass.DestroyTexture(ref _texture);
            HasResult = false;
        }
    }
}
