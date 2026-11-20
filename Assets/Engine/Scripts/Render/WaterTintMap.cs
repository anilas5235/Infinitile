using System;
using Engine.Scripts.Data;
using Engine.Scripts.Utils;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     The biome water colour around the player for biome-tinted liquids (TransparentVoxel.shader): one texel
    ///     per block column, by world x/z, repeating every <see cref="SizeChunks" /> chunks. A chunk owns the slot
    ///     its position falls on; the map holds the drawn square (2 × draw distance + 1 chunks) with
    ///     <see cref="SlackChunks" /> to spare, so two drawn chunks never share a slot. A chunk's colours are written
    ///     before its partitions are built (<see cref="Ensure" />), from its column biomes
    ///     (<see cref="ChunkVoxelData.Biomes" />), through one 32 × 32 staging texture copied into the map.
    /// </summary>
    internal sealed class WaterTintMap : IDisposable{
        /// <summary>Chunks the map holds beyond the drawn square, so a chunk leaving it frees its slot late.</summary>
        public const int SlackChunks = 3;

        /// <summary>Chunks along one side of the map.</summary>
        public int SizeChunks{ get; }

        /// <summary>Texels (block columns) along one side of the map.</summary>
        public int SizeTexels => SizeChunks * ChunkWidth;

        /// <summary>The tint texture, sRGB, bilinear, repeating.</summary>
        public RenderTexture Texture{ get; }

        /// <summary>Bytes of the tint texture and its staging texture.</summary>
        public long GpuBytes => 4L * (SizeTexels * (long)SizeTexels + ChunkWidth * ChunkDepth);

        private readonly int2[] _owners;
        private readonly bool[] _owned;
        private readonly Color32[] _pixels = new Color32[ChunkWidth * ChunkDepth];
        private readonly Color32 _defaultColor;
        private Texture2D _staging;
        private int _colorsVersion = int.MinValue;

        /// <summary>Chunks along one side of the map for a world drawn <paramref name="drawDistance" /> chunks out.</summary>
        public static int SizeChunksAt(int drawDistance){
            return 2 * math.max(drawDistance, 0) + 1 + SlackChunks;
        }

        /// <summary>Slot of chunk coordinate <paramref name="c" /> along one side of a map <paramref name="size" /> chunks wide.</summary>
        public static int Slot(int c, int size){
            return (c % size + size) % size;
        }

        /// <param name="drawDistance">Draw distance in chunks; the map covers the drawn square and a margin.</param>
        /// <param name="defaultColor">Colour of the slots no chunk has written yet (sRGB).</param>
        public WaterTintMap(int drawDistance, Color32 defaultColor){
            SizeChunks = SizeChunksAt(drawDistance);
            _defaultColor = defaultColor;
            _owners = new int2[SizeChunks * SizeChunks];
            _owned = new bool[SizeChunks * SizeChunks];

            Texture = new RenderTexture(new RenderTextureDescriptor(SizeTexels, SizeTexels,
                GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.None){ useMipMap = false, autoGenerateMips = false }){
                name = "WaterTintMap",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture.Create();
            Clear();

            _staging = new Texture2D(ChunkWidth, ChunkDepth, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None){
                name = "WaterTintStaging",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        /// <summary>Slot index of <paramref name="chunk" /> in the owner arrays.</summary>
        public int SlotIndex(int2 chunk){
            return Slot(chunk.x, SizeChunks) + Slot(chunk.y, SizeChunks) * SizeChunks;
        }

        /// <summary>Whether <paramref name="chunk" />'s colours are in the map now.</summary>
        public bool Owns(int2 chunk){
            int slot = SlotIndex(chunk);
            return _owned[slot] && _owners[slot].Equals(chunk);
        }

        /// <summary>
        ///     Writes <paramref name="chunk" />'s colours unless the map holds them already. A new
        ///     <paramref name="colorsVersion" /> (other biome colours) forgets every chunk first.
        /// </summary>
        /// <returns>True when the chunk's colours were written now.</returns>
        public bool Ensure(int2 chunk, in ChunkVoxelData data, Color32[] colors, int colorsVersion){
            if (colorsVersion != _colorsVersion){
                Array.Clear(_owned, 0, _owned.Length);
                _colorsVersion = colorsVersion;
            }

            if (Owns(chunk)) return false;
            Write(chunk, data, colors);
            return true;
        }

        /// <summary>Forgets <paramref name="chunk" /> (evicted); its slot is written again by the next chunk on it.</summary>
        public void Forget(int2 chunk){
            int slot = SlotIndex(chunk);
            if (_owners[slot].Equals(chunk)) _owned[slot] = false;
        }

        /// <summary>Sets the tint texture and its scale as the shader globals.</summary>
        public void SetGlobals(){
            Shader.SetGlobalTexture(VoxelRenderConstants.VoxelWaterTintNameID, Texture);
            Shader.SetGlobalVector(VoxelRenderConstants.VoxelWaterTintParamsNameID,
                new Vector4(1f / SizeTexels, SizeTexels, 0f, 0f));
        }

        /// <summary>
        ///     The colours of one chunk's columns: pixel x + z × 32 is column (x, z); a column without a biome, or
        ///     with one out of <paramref name="colors" />, gets <paramref name="defaultColor" />.
        /// </summary>
        internal static void FillPixels(in ChunkVoxelData data, Color32[] colors, Color32 defaultColor, Color32[] pixels){
            bool hasBiomes = data.HasBiomes;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                Color32 color = defaultColor;
                if (hasBiomes && colors != null){
                    int biome = data.GetBiome(x, z);
                    if (biome < colors.Length) color = colors[biome];
                }

                pixels[x + z * ChunkWidth] = color;
            }
        }

        private void Write(int2 chunk, in ChunkVoxelData data, Color32[] colors){
            FillPixels(data, colors, _defaultColor, _pixels);
            _staging.SetPixelData(_pixels, 0);
            _staging.Apply(false, false);
            Graphics.CopyTexture(_staging, 0, 0, 0, 0, ChunkWidth, ChunkDepth, Texture, 0, 0,
                Slot(chunk.x, SizeChunks) * ChunkWidth, Slot(chunk.y, SizeChunks) * ChunkDepth);

            int slot = SlotIndex(chunk);
            _owners[slot] = chunk;
            _owned[slot] = true;
        }

        private void Clear(){
            CommandBuffer cmd = new(){ name = "WaterTintMap Clear" };
            cmd.SetRenderTarget(Texture);
            Color clear = _defaultColor; // an sRGB target encodes the linear clear colour
            cmd.ClearRenderTarget(false, true, QualitySettings.activeColorSpace == ColorSpace.Linear ? clear.linear : clear);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        public void Dispose(){
            if (Texture){
                Texture.Release();
                DestroyObject(Texture);
            }

            if (_staging) DestroyObject(_staging);
            _staging = null;
        }

        private static void DestroyObject(UnityEngine.Object obj){
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
