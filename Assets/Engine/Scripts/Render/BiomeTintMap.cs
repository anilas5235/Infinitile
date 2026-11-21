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
    ///     The biome colours around the player (DEC-119, DEC-122): a texture array with one layer per
    ///     <see cref="BiomeTintPalette" /> layer (water, grass, plants), by world x/z, repeating every
    ///     <see cref="SizeChunks" /> chunks. One texel holds the mean colour of <see cref="BlendColumns" /> ×
    ///     <see cref="BlendColumns" /> block columns, and the shader blends four bilinear taps around a vertex
    ///     (biome_tint_color in VoxelShaderCommon.hlsl), so a colour changes over about 16 blocks at a biome border.
    ///     A chunk owns the slot its position falls on; the map holds the drawn square (2 × draw distance + 1 chunks)
    ///     and its ring of neighbours with <see cref="SlackChunks" /> to spare, so two chunks in use never share a
    ///     slot. A chunk's colours are written before its partitions are built (<see cref="Ensure" />), from its
    ///     column biomes (<see cref="ChunkVoxelData.Biomes" />), through one small staging texture copied into the
    ///     three layers.
    /// </summary>
    internal sealed class BiomeTintMap : IDisposable{
        /// <summary>Chunks the map holds beyond the drawn square, so a chunk leaving it frees its slot late.</summary>
        public const int SlackChunks = 3;

        /// <summary>Block columns along one side of a texel: the colour is averaged over 4 × 4 columns.</summary>
        public const int BlendColumns = 4;

        /// <summary>Texels along one side of a chunk.</summary>
        public const int ChunkTexels = ChunkWidth / BlendColumns;

        /// <summary>Chunks along one side of the map.</summary>
        public int SizeChunks{ get; }

        /// <summary>Texels along one side of a layer.</summary>
        public int SizeTexels => SizeChunks * ChunkTexels;

        /// <summary>Block columns along one side of the map (the shader's repeat period).</summary>
        public int SizeBlocks => SizeChunks * ChunkWidth;

        /// <summary>The tint texture array, sRGB, bilinear, repeating, one slice per palette layer, no mips.</summary>
        public RenderTexture Texture{ get; }

        /// <summary>Bytes of the tint texture and of the staging texture.</summary>
        public long GpuBytes => 4L * (SizeTexels * (long)SizeTexels * BiomeTintPalette.LayerCount +
                                      ChunkTexels * ChunkTexels * BiomeTintPalette.LayerCount);

        private readonly int2[] _owners;
        private readonly bool[] _owned;
        private readonly Color32[] _pixels = new Color32[ChunkTexels * ChunkTexels * BiomeTintPalette.LayerCount];
        private Texture2D _staging;
        private int _paletteVersion = int.MinValue;

        /// <summary>Chunks along one side of the map for a world drawn <paramref name="drawDistance" /> chunks out.</summary>
        public static int SizeChunksAt(int drawDistance){
            return 2 * math.max(drawDistance, 0) + 1 + SlackChunks;
        }

        /// <summary>Slot of chunk coordinate <paramref name="c" /> along one side of a map <paramref name="size" /> chunks wide.</summary>
        public static int Slot(int c, int size){
            return (c % size + size) % size;
        }

        /// <param name="drawDistance">Draw distance in chunks; the map covers the drawn square and a margin.</param>
        /// <param name="defaults">Gives each layer's colour where no chunk has written yet.</param>
        public BiomeTintMap(int drawDistance, BiomeTintPalette defaults){
            SizeChunks = SizeChunksAt(drawDistance);
            _owners = new int2[SizeChunks * SizeChunks];
            _owned = new bool[SizeChunks * SizeChunks];

            Texture = new RenderTexture(new RenderTextureDescriptor(SizeTexels, SizeTexels,
                GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.None){
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = BiomeTintPalette.LayerCount,
                    useMipMap = false,
                    autoGenerateMips = false
                }){
                name = "BiomeTintMap",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture.Create();
            Clear(defaults);

            _staging = new Texture2D(ChunkTexels, ChunkTexels * BiomeTintPalette.LayerCount,
                GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None){
                name = "BiomeTintStaging",
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
        ///     Writes <paramref name="chunk" />'s colours unless the map holds them already. A palette of another
        ///     <see cref="BiomeTintPalette.Version" /> (other biome colours) forgets every chunk first.
        /// </summary>
        /// <returns>True when the chunk's colours were written now.</returns>
        public bool Ensure(int2 chunk, in ChunkVoxelData data, BiomeTintPalette palette){
            if (palette.Version != _paletteVersion){
                Array.Clear(_owned, 0, _owned.Length);
                _paletteVersion = palette.Version;
            }

            if (Owns(chunk)) return false;
            Write(chunk, data, palette);
            return true;
        }

        /// <summary>Forgets <paramref name="chunk" /> (evicted); its slot is written again by the next chunk on it.</summary>
        public void Forget(int2 chunk){
            int slot = SlotIndex(chunk);
            if (_owners[slot].Equals(chunk)) _owned[slot] = false;
        }

        /// <summary>Sets the tint texture array and its scale as the shader globals.</summary>
        public void SetGlobals(){
            Shader.SetGlobalTexture(VoxelRenderConstants.VoxelBiomeTintNameID, Texture);
            Shader.SetGlobalVector(VoxelRenderConstants.VoxelBiomeTintParamsNameID,
                new Vector4(1f / SizeBlocks, SizeBlocks, 0f, 0f));
        }

        /// <summary>
        ///     The colours of one chunk's texels: layer <c>l</c>, texel (x, z) is pixel x + (z + l × 8) × 8, the
        ///     linear-light mean of the colours of its 4 × 4 columns, sRGB-encoded. A column without a biome, or with
        ///     one the palette lacks, takes the layer's default colour.
        /// </summary>
        internal static void FillPixels(in ChunkVoxelData data, BiomeTintPalette palette, Color32[] pixels){
            bool hasBiomes = data.HasBiomes;
            const float columns = BlendColumns * BlendColumns;
            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++)
            for (int tx = 0; tx < ChunkTexels; tx++)
            for (int tz = 0; tz < ChunkTexels; tz++){
                float3 sum = float3.zero;
                for (int dx = 0; dx < BlendColumns; dx++)
                for (int dz = 0; dz < BlendColumns; dz++){
                    int biome = hasBiomes ? data.GetBiome(tx * BlendColumns + dx, tz * BlendColumns + dz) : -1;
                    sum += palette.Linear(layer, biome);
                }

                float3 mean = sum / columns;
                pixels[tx + (tz + layer * ChunkTexels) * ChunkTexels] =
                    new Color(mean.x, mean.y, mean.z, 1f).gamma;
            }
        }

        private void Write(int2 chunk, in ChunkVoxelData data, BiomeTintPalette palette){
            FillPixels(data, palette, _pixels);
            _staging.GetPixelData<Color32>(0).CopyFrom(_pixels); // SetPixelData with an array allocates
            _staging.Apply(false, false);
            int dstX = Slot(chunk.x, SizeChunks) * ChunkTexels, dstY = Slot(chunk.y, SizeChunks) * ChunkTexels;
            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++){
                Graphics.CopyTexture(_staging, 0, 0, 0, layer * ChunkTexels, ChunkTexels, ChunkTexels,
                    Texture, layer, 0, dstX, dstY);
            }

            int slot = SlotIndex(chunk);
            _owners[slot] = chunk;
            _owned[slot] = true;
        }

        private void Clear(BiomeTintPalette defaults){
            CommandBuffer cmd = new(){ name = "BiomeTintMap Clear" };
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            for (int layer = 0; layer < BiomeTintPalette.LayerCount; layer++){
                cmd.SetRenderTarget(Texture, 0, CubemapFace.Unknown, layer);
                Color clear = defaults.DefaultColor(layer); // an sRGB target encodes the linear clear colour
                cmd.ClearRenderTarget(false, true, linear ? clear.linear : clear);
            }

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
