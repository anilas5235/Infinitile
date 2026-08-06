using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Utils.Logger;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Registry
{
    /// <summary>
    ///     Registers textures for voxel definitions and builds a shared <see cref="Texture2DArray" /> atlas.
    /// </summary>
    internal class TexRegistry : Registry<Texture2D>, IResourceRegistry<Texture2D>
    {
        public TexRegistry(int initCapacity) : base(initCapacity)
        {
        }

        private static int TextureSize => VoxelRegistry.TextureSize;

        /// <summary>
        ///     Gets the resulting texture array after <see cref="PrepareArray" /> has been called.
        /// </summary>
        public Texture2DArray TextureArray { get; private set; }

        private TextureFormat _texFormat;

        /// <summary>
        ///     Registers a texture and assigns an index ID if its size matches the expected atlas size.
        ///     Returns the index or -1 on failure.
        /// </summary>
        /// <param name="tex">Texture to register.</param>
        /// <returns>Assigned texture index.</returns>
        public override ushort Register(Texture2D tex)
        {
            if (!tex) throw new ArgumentNullException(nameof(tex), "Cannot register a null texture.");

            if (tex.width != TextureSize || tex.height != TextureSize)
                throw new ArgumentException(
                    $"Texture size does not match the expected atlas size, expected {TextureSize}x{TextureSize}.",
                    nameof(tex));

            if (Count == 0) _texFormat = tex.format;
            else if (tex.format != _texFormat)
                throw new ArgumentException(
                    $"Texture format does not match the expected format, expected {_texFormat}.",
                    nameof(tex));

            return base.Register(tex);
        }

        /// <summary>
        ///     Builds a <see cref="Texture2DArray" /> from all registered textures using point filtering and repeat wrapping.
        /// </summary>
        public void PrepareArray()
        {
            if (Count == 0) return;

            Texture2DArray textureArray = new(
                TextureSize,
                TextureSize,
                Count,
                ForwardMap.First().Key.format,
                false
            )
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            // Copy each texture into the texture array
            int index = 0;
            foreach (KeyValuePair<Texture2D, ushort> kvp in ForwardMap)
            {
                VoxelEngineLogger.Info<TexRegistry>($"copy texture {kvp.Key.name} to texture array");
                Graphics.CopyTexture(kvp.Key, 0, 0, textureArray, index, 0);
                index++;
            }

            textureArray.Apply();
            TextureArray = textureArray;
        }
    }
}