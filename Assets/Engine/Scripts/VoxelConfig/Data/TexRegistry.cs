using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Utils.Logger;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Registers textures for voxel definitions and builds a shared <see cref="Texture2DArray" /> atlas.
    /// </summary>
    internal class TexRegistry
    {
        private readonly Dictionary<Texture2D, ushort> _textureToId = new();
        private static int TextureSize => VoxelRegistry.TextureSize;

        /// <summary>
        ///     Gets the resulting texture array after <see cref="PrepareArray" /> has been called.
        /// </summary>
        public Texture2DArray TextureArray { get; private set; }

        /// <summary>
        ///     Registers a texture and assigns an index ID if its size matches the expected atlas size.
        ///     Returns the index or -1 on failure.
        /// </summary>
        /// <param name="tex">Texture to register.</param>
        /// <returns>Assigned texture index, or -1 if registration failed.</returns>
        public ushort Register(Texture2D tex)
        {
            ushort textureId = 0;
            if (!tex) return textureId;
            if (tex.width != TextureSize || tex.height != TextureSize)
            {
                VoxelEngineLogger.Warn<VoxelRegistry>(
                    $"Texture {tex.name} size is {tex.width}x{tex.height}, expected {TextureSize}x{TextureSize}. It will be ignored.");
                return textureId;
            }

            if (_textureToId.TryGetValue(tex, out textureId)) return textureId;

            textureId = (ushort)_textureToId.Count;
            _textureToId[tex] = textureId;

            return textureId;
        }

        /// <summary>
        ///     Builds a <see cref="Texture2DArray" /> from all registered textures using point filtering and repeat wrapping.
        /// </summary>
        internal void PrepareArray()
        {
            if (_textureToId.Count == 0) return;

            Texture2DArray textureArray = new(
                TextureSize,
                TextureSize,
                _textureToId.Count,
                _textureToId.First().Key.format,
                false
            )
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            // Copy each texture into the texture array
            int index = 0;
            foreach (KeyValuePair<Texture2D, ushort> kvp in _textureToId)
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