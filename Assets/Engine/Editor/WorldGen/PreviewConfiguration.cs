using System;
using Engine.Scripts.WorldGen.Settings;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     INT-20/21: the knobs the preview switches — architecture, the per-stage blend methods and their strengths.
    ///     Configuration A is these fields read from and written to the <see cref="WorldGenSettings" /> asset;
    ///     configuration B is a preview-only set layered on the same asset (biomes, splines and climate stay shared),
    ///     so B never touches the asset unless the user applies it (decision 2026-10-20).
    /// </summary>
    [Serializable]
    public class PreviewConfiguration{
        public GenArchitecture Architecture = GenArchitecture.BiomeFirst;
        public BlendMethod MaterialMethod = BlendMethod.None;
        public float MaterialBlendWidth = 32f;
        public float MaterialSigma = 0.04f;
        public BlendMethod HeightMethod = BlendMethod.None;
        public float HeightBlendWidth = 32f;
        public float HeightSigma = 0.04f;
        public float WarpAmplitude = 24f;
        public float WarpWavelength = 96f;
        public int WarpOctaves = 2;

        /// <summary>Reads the knobs of <paramref name="settings" />.</summary>
        public static PreviewConfiguration Capture(WorldGenSettings settings){
            PreviewConfiguration c = new();
            c.CopyFrom(settings);
            return c;
        }

        public void CopyFrom(WorldGenSettings s){
            Architecture = s.Architecture;
            MaterialMethod = s.MaterialMethod;
            MaterialBlendWidth = s.MaterialBlendWidth;
            MaterialSigma = s.MaterialSigma;
            HeightMethod = s.HeightMethod;
            HeightBlendWidth = s.HeightBlendWidth;
            HeightSigma = s.HeightSigma;
            WarpAmplitude = s.WarpAmplitude;
            WarpWavelength = s.WarpWavelength;
            WarpOctaves = s.WarpOctaves;
        }

        public PreviewConfiguration Clone(){
            return (PreviewConfiguration)MemberwiseClone();
        }

        /// <summary>Writes the knobs into <paramref name="s" />; the caller records Undo and marks it dirty.</summary>
        public void ApplyTo(WorldGenSettings s){
            s.Architecture = Architecture;
            s.MaterialMethod = MaterialMethod;
            s.MaterialBlendWidth = MaterialBlendWidth;
            s.MaterialSigma = MaterialSigma;
            s.HeightMethod = HeightMethod;
            s.HeightBlendWidth = HeightBlendWidth;
            s.HeightSigma = HeightSigma;
            s.WarpAmplitude = WarpAmplitude;
            s.WarpWavelength = WarpWavelength;
            s.WarpOctaves = WarpOctaves;
        }

        /// <summary>True when applying this configuration would leave <paramref name="s" /> unchanged.</summary>
        public bool Matches(WorldGenSettings s){
            return Architecture == s.Architecture && MaterialMethod == s.MaterialMethod &&
                   MaterialBlendWidth.Equals(s.MaterialBlendWidth) && MaterialSigma.Equals(s.MaterialSigma) &&
                   HeightMethod == s.HeightMethod && HeightBlendWidth.Equals(s.HeightBlendWidth) &&
                   HeightSigma.Equals(s.HeightSigma) && WarpAmplitude.Equals(s.WarpAmplitude) &&
                   WarpWavelength.Equals(s.WarpWavelength) && WarpOctaves == s.WarpOctaves;
        }

        /// <summary>
        ///     Builds the Burst config of <paramref name="baseSettings" /> with these knobs, through a transient clone,
        ///     so the asset stays untouched. The caller owns and disposes the result (D-05). Throws like
        ///     <see cref="WorldGenSettings.ToConfig" /> on invalid settings.
        /// </summary>
        public WorldGenConfig BuildConfig(WorldGenSettings baseSettings, int seed){
            WorldGenSettings clone = Object.Instantiate(baseSettings);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.name = baseSettings.name + " (preview B)";
            try{
                ApplyTo(clone);
                return clone.ToConfig(seed);
            }
            finally{
                Object.DestroyImmediate(clone);
            }
        }

        /// <summary>
        ///     Short label, e.g. "Biome-first · material DistanceBlend B=32 · height None". Only the strengths the
        ///     selected methods use are shown; Terrain-first has no height stage (TRN-21).
        /// </summary>
        public string Describe(){
            bool biomeFirst = Architecture == GenArchitecture.BiomeFirst;
            string text = $"{(biomeFirst ? "Biome-first" : "Terrain-first")} · material {Stage(MaterialMethod, MaterialBlendWidth, MaterialSigma, true)}";
            if (biomeFirst) text += $" · height {Stage(HeightMethod, HeightBlendWidth, HeightSigma, false)}";
            return text;
        }

        private string Stage(BlendMethod method, float width, float sigma, bool material){
            return method switch{
                BlendMethod.DistanceBlend => $"DistanceBlend B={width:0.##}",
                BlendMethod.ParameterSpaceWeighting => $"ParameterSpace σ={sigma:0.###}",
                // Material-stage DistortedBoundary is a one-hot warp; the height stage also blends over B (§9.7).
                BlendMethod.DistortedBoundary => material
                    ? $"Distorted A={WarpAmplitude:0.##} λd={WarpWavelength:0.##}"
                    : $"Distorted A={WarpAmplitude:0.##} λd={WarpWavelength:0.##} B={width:0.##}",
                _ => "None"
            };
        }
    }
}
