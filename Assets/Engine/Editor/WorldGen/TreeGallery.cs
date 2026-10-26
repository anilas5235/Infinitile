using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Flora;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;
using VoxelSo = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Species gallery of the preview: stamps every variant of a <see cref="TreeSpecies" /> for a row of seeds
    ///     with <see cref="TreeShape" /> (the code the chunk job runs) and renders a side view and a top view per
    ///     variant. The trees stand on flat ground at the world origin; tree k of a row uses seed + k.
    /// </summary>
    public sealed class TreeGallery : IDisposable{
        /// <summary>Pixels between two trees and around the row.</summary>
        private const int Gap = 1;

        private static readonly Color32 Background = new(26, 26, 26, 255);
        private static readonly Color32 Ground = new(92, 70, 48, 255);
        private static readonly Color32 TrunkColor = new(122, 84, 46, 255);
        private static readonly Color32 LyingTrunkColor = new(176, 128, 78, 255);
        private static readonly Color32 LeafColor = new(64, 140, 48, 255);
        private static readonly Color32 AltLeafColor = new(44, 108, 92, 255);

        /// <summary>One variant: its views and the ranges of what the row rolled.</summary>
        public sealed class Row{
            public string Name;
            public float Share;
            public TreeVariantDef Def;
            public Texture2D Side;
            public Texture2D Top;
            public int2 VoxelCount;
            public int2 Height;
            public int Narrowed;
        }

        private readonly List<Row> _rows = new();

        public IReadOnlyList<Row> Rows => _rows;

        /// <summary>
        ///     Rebuilds the rows. <paramref name="narrow" /> builds every wide or leaning trunk as it comes out on
        ///     uneven ground (one block wide, upright).
        /// </summary>
        public void Build(TreeSpecies species, int seed, int count, bool narrow){
            Clear();
            if (!species || species.Variants == null || species.Variants.Count == 0) return;
            count = math.max(count, 1);

            // Local voxel ids: only the role colours matter here, not the engine's ids.
            Dictionary<VoxelSo, ushort> ids = new();
            ushort next = 0;
            ushort Id(VoxelSo voxel){
                if (!ids.TryGetValue(voxel, out ushort id)){
                    id = ++next;
                    ids[voxel] = id;
                }

                return id;
            }

            // Lying logs get ids of their own, so they show in their own colour.
            Dictionary<(ushort, byte), ushort> variantIds = new();
            ushort VariantOf(ushort id, byte orientation){
                if (!variantIds.TryGetValue((id, orientation), out ushort variant)){
                    variant = ++next;
                    variantIds[(id, orientation)] = variant;
                }

                return variant;
            }

            List<TreeVariantDef> defs = new();
            FloraConfigBuilder.AddTree(species, Id, VariantOf, defs, new Dictionary<ushort, byte>(), species, false);

            NativeList<FloraVoxel> list = new(512, Allocator.Temp);
            try{
                float previousEnd = 0f;
                for (int i = 0; i < defs.Count; i++){
                    TreeVariantDef def = defs[i];
                    Row row = new(){
                        Name = species.Variants[i]?.Name ?? $"#{i}",
                        Share = def.CumulativeEnd - previousEnd,
                        Def = def,
                        VoxelCount = new int2(int.MaxValue, 0),
                        Height = new int2(int.MaxValue, 0)
                    };
                    previousEnd = def.CumulativeEnd;
                    Dictionary<int3, FloraVoxel>[] trees = new Dictionary<int3, FloraVoxel>[count];
                    int top = 0;
                    for (int k = 0; k < count; k++){
                        uint treeSeed = unchecked((uint)(seed + k));
                        TreeRoll roll = TreeShape.Roll(0, 0, treeSeed, i, def);
                        if (narrow && roll.NeedsFlatFootprint){
                            roll.Narrow();
                            row.Narrowed++;
                        }

                        list.Clear();
                        TreeShape.Build(int3.zero, roll, def, treeSeed, ref list);
                        trees[k] = Resolve(list);
                        int height = 0;
                        foreach (int3 p in trees[k].Keys) height = math.max(height, p.y + 1);
                        top = math.max(top, height);
                        row.VoxelCount = new int2(math.min(row.VoxelCount.x, trees[k].Count), math.max(row.VoxelCount.y, trees[k].Count));
                        row.Height = new int2(math.min(row.Height.x, height), math.max(row.Height.y, height));
                    }

                    Dictionary<ushort, Color32> colors = new(){ [0] = Background };
                    if (def.AltLeafVoxel != 0) colors.TryAdd(def.AltLeafVoxel, AltLeafColor);
                    if (def.LeafVoxel != 0) colors[def.LeafVoxel] = LeafColor;
                    if (def.TrunkVoxel != 0){
                        colors[def.TrunkVoxelX] = LyingTrunkColor;
                        colors[def.TrunkVoxelZ] = LyingTrunkColor;
                        colors[def.TrunkVoxel] = TrunkColor;
                    }
                    int reach = math.max(def.Reach, 1);
                    row.Side = RenderSide(trees, reach, top, colors);
                    row.Top = RenderTop(trees, reach, top, colors);
                    _rows.Add(row);
                }
            }
            finally{
                list.Dispose();
            }
        }

        /// <summary>One voxel per position, as stamping resolves duplicates: the higher replace class wins.</summary>
        private static Dictionary<int3, FloraVoxel> Resolve(NativeList<FloraVoxel> list){
            Dictionary<int3, FloraVoxel> voxels = new();
            foreach (FloraVoxel v in list){
                if (v.Voxel == 0 || v.Offset.y < 0) continue; // never stamped / inside the ground
                if (!voxels.TryGetValue(v.Offset, out FloraVoxel old) || old.Class < v.Class) voxels[v.Offset] = v;
            }

            return voxels;
        }

        /// <summary>Side view along +z: the nearest voxel of each (x, y), darker with depth; one ground row below.</summary>
        private static Texture2D RenderSide(Dictionary<int3, FloraVoxel>[] trees, int reach, int top, Dictionary<ushort, Color32> colors){
            int cell = 2 * reach + 1;
            int width = trees.Length * (cell + Gap) + Gap;
            int height = top + 1 + Gap;
            Color32[] pixels = Fill(width * height, Background);
            for (int k = 0; k < trees.Length; k++){
                int left = Gap + k * (cell + Gap);
                for (int x = 0; x < cell; x++) pixels[left + x] = Ground;
                Dictionary<int2, int> nearest = new();
                foreach (int3 p in trees[k].Keys){
                    int2 key = p.xy;
                    if (!nearest.TryGetValue(key, out int z) || p.z < z) nearest[key] = p.z;
                }

                foreach ((int2 key, int z) in nearest){
                    if (math.abs(key.x) > reach) continue; // outside the variant's reach: a builder bug, not drawn
                    FloraVoxel v = trees[k][new int3(key.x, key.y, z)];
                    float shade = math.lerp(1f, .55f, (z + reach) / (float)(2 * reach));
                    pixels[(key.y + 1) * width + left + key.x + reach] = Shade(colors, v.Voxel, shade);
                }
            }

            return Texture(width, height, pixels);
        }

        /// <summary>Top view: the highest voxel of each (x, z), darker further down.</summary>
        private static Texture2D RenderTop(Dictionary<int3, FloraVoxel>[] trees, int reach, int top, Dictionary<ushort, Color32> colors){
            int cell = 2 * reach + 1;
            int width = trees.Length * (cell + Gap) + Gap;
            int height = cell + 2 * Gap;
            Color32[] pixels = Fill(width * height, Background);
            for (int k = 0; k < trees.Length; k++){
                int left = Gap + k * (cell + Gap);
                for (int z = 0; z < cell; z++)
                for (int x = 0; x < cell; x++)
                    pixels[(z + Gap) * width + left + x] = Ground;

                Dictionary<int2, int> highest = new();
                foreach (int3 p in trees[k].Keys){
                    int2 key = p.xz;
                    if (!highest.TryGetValue(key, out int y) || p.y > y) highest[key] = p.y;
                }

                foreach ((int2 key, int y) in highest){
                    if (math.cmax(math.abs(key)) > reach) continue;
                    FloraVoxel v = trees[k][new int3(key.x, y, key.y)];
                    float shade = math.lerp(.5f, 1f, y / (float)math.max(top - 1, 1));
                    pixels[(key.y + reach + Gap) * width + left + key.x + reach] = Shade(colors, v.Voxel, shade);
                }
            }

            return Texture(width, height, pixels);
        }

        private static Color32 Shade(Dictionary<ushort, Color32> colors, ushort voxel, float shade){
            Color32 c = colors.TryGetValue(voxel, out Color32 known) ? known : LeafColor;
            return new Color32((byte)(c.r * shade), (byte)(c.g * shade), (byte)(c.b * shade), 255);
        }

        private static Color32[] Fill(int count, Color32 color){
            Color32[] pixels = new Color32[count];
            Array.Fill(pixels, color);
            return pixels;
        }

        private static Texture2D Texture(int width, int height, Color32[] pixels){
            Texture2D texture = new(width, height, TextureFormat.RGBA32, false){
                name = "TreeGalleryTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private void Clear(){
            foreach (Row row in _rows){
                if (row.Side) Object.DestroyImmediate(row.Side);
                if (row.Top) Object.DestroyImmediate(row.Top);
            }

            _rows.Clear();
        }

        public void Dispose(){
            Clear();
        }
    }
}
