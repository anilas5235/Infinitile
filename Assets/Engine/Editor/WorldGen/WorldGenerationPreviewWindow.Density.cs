using System;
using System.Linq;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     The density cross-section of graph mode (§5, V2): when the graph has the terrain outputs, the density
    ///     generator runs on the chunks under an x–y slice through the centre of the view (at z = the view centre) and
    ///     the slice shows terrain, water (under the water level of the WorldGenSettings) and air, one pixel per block.
    ///     Yellow marks the preliminary surface, the faint line the start of the top slide. The title counts the
    ///     overhang voxels (air under the top terrain voxel of its column, above the water) and the floating ones
    ///     (terrain with air right under it, above the water).
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        private Texture2D _densityTexture;
        private string _densityTitle;

        private void BuildDensityView(CompiledNoiseGraph graph, uint seed, int2 origin, int extent){
            _densityTitle = null;
            if (!DensityTerrainGraph.TryCreate(graph, out DensityTerrainGraph terrain, out string error)){
                if (error != null && graph.Outputs.Any(o => o.Name == GraphOutputs.TerrainDensity)) _densityTitle = "Density cross-section: " + error;
                return;
            }

            WorldGenSettings settings = WorldGenSettings;
            if (!settings){
                _densityTitle = "Density cross-section: assign a WorldGenSettings asset (it gives the water level).";
                return;
            }

            int width = Mathf.Min(extent, ViewResolution) & ~3;
            int startX = (origin.x + (extent - width) / 2) & ~3;
            int z = _worldOffset.y;
            int chunkZ = (int)math.floor(z / 32f) * 32, localZ = z - chunkZ;
            int firstChunk = (int)math.floor(startX / 32f) * 32;
            const int height = DensityLayout.Height;

            WorldGenConfig config = default;
            try{
                config = settings.ToConfig(Seed, null, Allocator.TempJob);
                int waterLevel = config.WaterLevel;
                Color32[] pixels = new Color32[width * height];
                Color32 stone = new(128, 120, 110, 255), water = new(45, 95, 200, 255), air = new(22, 22, 26, 255);
                Color32 overhangAir = new(70, 40, 60, 255), slide = new(80, 80, 40, 255), prelim = new(255, 220, 40, 255);
                int overhangs = 0, floating = 0, top = -1;
                DensityWorkCounters counters = default;
                for (int cx = firstChunk; cx < startX + width; cx += 32){
                    using DensityProbeResult chunk = DensityProbe.Run(graph, config, new int2(cx, chunkZ), DensityChunk.PassTerrain);
                    counters.Add(chunk.Counters);
                    for (int lx = 0; lx < 32; lx++){
                        int i = cx + lx - startX;
                        if (i < 0 || i >= width) continue;
                        int columnTop = chunk.Heightmap[lx * 32 + localZ];
                        top = math.max(top, columnTop);
                        for (int y = 0; y < height; y++){
                            bool solid = chunk.IsSolid(lx, y, localZ);
                            Color32 c;
                            if (solid){
                                c = stone;
                                if (y > waterLevel && y > 0 && !chunk.IsSolid(lx, y - 1, localZ)) floating++;
                            }
                            else if (y < waterLevel) c = water;
                            else if (y < columnTop){
                                c = overhangAir;
                                overhangs++;
                            }
                            else c = y == (int)terrain.SlideStart ? slide : air;

                            pixels[y * width + i] = c;
                        }
                    }
                }

                // The preliminary surface along the slice.
                float2[] points = new float2[width];
                for (int i = 0; i < width; i++) points[i] = new float2(startX + i, z);
                float[] lattice = GraphEvaluator.SampleLattice(graph, seed, points, out _);
                for (int i = 0; i < width; i++){
                    int y = (int)math.floor(lattice[terrain.PreliminarySurface * width + i]);
                    if (y >= 0 && y < height) pixels[y * width + i] = prelim;
                }

                if (_densityTexture) DestroyImmediate(_densityTexture);
                _densityTexture = new Texture2D(width, height, TextureFormat.RGBA32, false){
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _densityTexture.SetPixels32(pixels);
                _densityTexture.Apply(false);
                _densityTitle = $"Density cross-section · x {startX}..{startX + width - 1} at z {z} · cell 4×{terrain.CellHeight}×4 · " +
                                $"top y {top} · overhang air {overhangs} · floating {floating} · water < {waterLevel} · yellow: preliminary surface";
                _graphCounters.Add(counters.Graph);
            }
            catch (Exception e){
                _densityTitle = "Density cross-section failed: " + e.Message;
            }
            finally{
                if (config.IsCreated) config.Dispose();
            }
        }

        private void DrawDensityView(){
            if (_densityTitle == null) return;
            EditorGUILayoutLabel(_densityTitle);
            if (!_densityTexture) return;
            float width = Mathf.Min(position.width - 40f, _densityTexture.width * 2f);
            Rect rect = GUILayoutUtility.GetRect(width, width * _densityTexture.height / _densityTexture.width, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, _densityTexture, ScaleMode.StretchToFill, false);
        }

        private static void EditorGUILayoutLabel(string text){
            UnityEditor.EditorGUILayout.LabelField(text, UnityEditor.EditorStyles.boldLabel);
        }

        private void DestroyDensityTexture(){
            if (_densityTexture) DestroyImmediate(_densityTexture);
            _densityTexture = null;
            _densityTitle = null;
        }
    }
}
