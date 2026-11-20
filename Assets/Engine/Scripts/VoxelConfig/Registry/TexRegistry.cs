using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Utils.Logger;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.VoxelConfig.Registry{
    /// <summary>
    ///     Registers textures for voxel definitions and builds a shared <see cref="Texture2DArray" /> atlas.
    /// </summary>
    internal class TexRegistry : Registry<Texture2D>, IResourceRegistry<Texture2D>{
        private readonly List<uint> _tintWords;
        private TextureFormat _texFormat;

        public TexRegistry(int initCapacity) : base(initCapacity){
            _tintWords = new List<uint>(initCapacity);
        }

        /// <summary>
        ///     The biome tint word of every registered texture by index (<c>_VoxelTexTint</c>, DEC-120): bits 0-1 the
        ///     tint map layer (0 = none, see <see cref="Data.Voxel.VoxelBiomeTint" />), bit 2 set when the tint goes
        ///     through a mask, bits 16-31 the mask's slice in the mask array.
        /// </summary>
        public IReadOnlyList<uint> TintWords => _tintWords;

        private static int TextureSize => VoxelRegistry.TextureSize;

        /// <summary>
        ///     Gets the resulting texture array after <see cref="PrepareArray" /> has been called.
        /// </summary>
        public Texture2DArray TextureArray{ get; private set; }

        /// <summary>
        ///     Registers a texture and assigns an index ID. Throws if its size or format does not match the atlas.
        /// </summary>
        /// <param name="tex">Texture to register.</param>
        /// <returns>Assigned texture index.</returns>
        public override ushort Register(Texture2D tex){
            return Register(tex, 0u);
        }

        /// <summary>
        ///     Registers a texture with its biome tint word (<see cref="TintWords" />). A texture registered again
        ///     with another word keeps its first one and logs an error: a texture is tinted one way wherever it is
        ///     drawn.
        /// </summary>
        public ushort Register(Texture2D tex, uint tintWord){
            if (tex && TryGetId(tex, out ushort known)){
                if (_tintWords[known] != tintWord){
                    VoxelEngineLogger.Error<TexRegistry>(
                        $"Texture '{tex.name}' is used with two biome tints (0x{_tintWords[known]:X} and " +
                        $"0x{tintWord:X}); it keeps the first. Give the other voxel its own texture.");
                }

                return known;
            }

            ushort id = RegisterNew(tex);
            _tintWords.Add(tintWord);
            return id;
        }

        private ushort RegisterNew(Texture2D tex){
            if (!tex) throw new ArgumentNullException(nameof(tex), "Cannot register a null texture.");

            if (tex.width != TextureSize || tex.height != TextureSize){
                throw new ArgumentException(
                    $"Texture size does not match the expected atlas size, expected {TextureSize}x{TextureSize}.",
                    nameof(tex));
            }

            if (Count == 0) _texFormat = tex.format;
            else if (tex.format != _texFormat){
                throw new ArgumentException(
                    $"Texture format does not match the expected format, expected {_texFormat}.",
                    nameof(tex));
            }

            return base.Register(tex);
        }

        /// <summary>
        ///     Builds a <see cref="Texture2DArray" /> from all registered textures using point filtering and repeat
        ///     wrapping. It keeps the textures' mipmaps (as many levels as every texture has), so distant blocks do not
        ///     shimmer; the importer's mipmaps keep the alpha coverage of leaves and plants where that is switched on.
        ///     A tile with fewer levels than the others cuts the levels of the whole array; that logs a warning naming
        ///     the tile (usually its import settings have mipmaps off).
        /// </summary>
        public void PrepareArray(){
            if (Count == 0) return;

            int mipCount = int.MaxValue, maxMipCount = 0;
            foreach (Texture2D tex in ForwardMap.Keys){
                mipCount = Math.Min(mipCount, tex.mipmapCount);
                maxMipCount = Math.Max(maxMipCount, tex.mipmapCount);
            }

            if (mipCount < maxMipCount){
                string[] few = ForwardMap.Keys.Where(t => t.mipmapCount < maxMipCount)
                    .Select(t => $"'{t.name}' ({t.mipmapCount})").ToArray();
                VoxelEngineLogger.Warn<TexRegistry>(
                    $"{few.Length} tile(s) have fewer mip levels than the other tiles ({maxMipCount}), so the whole " +
                    $"texture array keeps only {mipCount}: {string.Join(", ", few)}. Switch on Generate Mipmaps in " +
                    "their import settings.");
            }

            Texture2DArray textureArray = new(
                TextureSize,
                TextureSize,
                Count,
                ForwardMap.First().Key.format,
                mipCount,
                false
            ){
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            // Copy each texture with its mip levels into the texture array
            int index = 0;
            foreach (KeyValuePair<Texture2D, ushort> kvp in ForwardMap){
                for (int mip = 0; mip < mipCount; mip++) Graphics.CopyTexture(kvp.Key, 0, mip, textureArray, index, mip);
                index++;
            }

            textureArray.Apply(false); // keep the copied mip levels
            TextureArray = textureArray;
        }

        /// <summary>
        ///     Destroys the texture array; the registry owns it.
        /// </summary>
        public void DestroyArray(){
            if (!TextureArray) return;
            CoreUtils.Destroy(TextureArray); // DestroyImmediate outside play mode (EditMode tests)
            TextureArray = null;
        }

        protected override void PostRegister(ushort id, Texture2D item){
            VoxelEngineLogger.Info<TexRegistry>($"Registered texture '{item.name}' with ID {id}");
        }
    }
}
