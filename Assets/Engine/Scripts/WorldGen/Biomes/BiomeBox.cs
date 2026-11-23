using System;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>
    ///     A parameter box of a biome (WorldGen v2 §6.1, DEC-127): one interval per climate parameter, as in Minecraft's
    ///     biome table, plus the depth interval reserved for cave biomes (v2.0 evaluates depth 0 only). An interval
    ///     holds min ≤ v &lt; max; a min at −1 or below means no lower edge and a max at 1 or above no upper edge, so the
    ///     clamped parameters (W2-NG-07) at −1 and at 1 lie inside. x is min, y is max.
    /// </summary>
    [Serializable]
    public class BiomeBox{
        /// <summary>The five climate parameters a box bounds, in this order.</summary>
        public const int Parameters = 5;

        public const int Temperature = 0, Humidity = 1, Continentalness = 2, Erosion = 3, Weirdness = 4;

        /// <summary>Names of the parameters, as the noise graph's outputs and the biome JSON files call them.</summary>
        public static readonly string[] ParameterNames ={ "temperature", "humidity", "continentalness", "erosion", "weirdness" };

        /// <summary>Short names for tables.</summary>
        public static readonly string[] ShortNames ={ "T", "F", "C", "E", "W" };

        [Tooltip("Temperature interval: min ≤ T < max; −1 and 1 are open ends.")]
        public Vector2 TemperatureRange = Full;

        [Tooltip("Humidity interval (F).")]
        public Vector2 HumidityRange = Full;

        [Tooltip("Continentalness interval (C, after the C curve).")]
        public Vector2 ContinentalnessRange = Full;

        [Tooltip("Erosion interval (E).")]
        public Vector2 ErosionRange = Full;

        [Tooltip("Weirdness interval (W).")]
        public Vector2 WeirdnessRange = Full;

        [Tooltip("Depth interval, reserved for cave biomes: only depth 0 is evaluated, so a box whose depth interval " +
                 "does not hold 0 never places its biome.")]
        public Vector2 DepthRange = Vector2.zero;

        /// <summary>The whole parameter range: no lower and no upper edge.</summary>
        public static readonly Vector2 Full = new(-1f, 1f);

        public BiomeBox(){ }

        public BiomeBox(Vector2 t, Vector2 f, Vector2 c, Vector2 e, Vector2 w){
            TemperatureRange = t;
            HumidityRange = f;
            ContinentalnessRange = c;
            ErosionRange = e;
            WeirdnessRange = w;
        }

        /// <summary>The interval of parameter <paramref name="p" /> (<see cref="Temperature" /> … <see cref="Weirdness" />).</summary>
        public Vector2 Range(int p){
            return p switch{
                Temperature => TemperatureRange,
                Humidity => HumidityRange,
                Continentalness => ContinentalnessRange,
                Erosion => ErosionRange,
                Weirdness => WeirdnessRange,
                _ => throw new ArgumentOutOfRangeException(nameof(p))
            };
        }

        public void SetRange(int p, Vector2 range){
            switch (p){
                case Temperature: TemperatureRange = range; break;
                case Humidity: HumidityRange = range; break;
                case Continentalness: ContinentalnessRange = range; break;
                case Erosion: ErosionRange = range; break;
                case Weirdness: WeirdnessRange = range; break;
                default: throw new ArgumentOutOfRangeException(nameof(p));
            }
        }

        /// <summary>Whether the box places its biome at depth 0, the only depth v2.0 evaluates.</summary>
        public bool AtSurface => DepthRange.x <= 0f && DepthRange.y >= 0f;

        public BiomeBox Clone(){
            return (BiomeBox)MemberwiseClone();
        }

        public override string ToString(){
            System.Text.StringBuilder s = new();
            for (int p = 0; p < Parameters; p++){
                Vector2 r = Range(p);
                if (r.x <= -1f && r.y >= 1f) continue;
                if (s.Length > 0) s.Append(' ');
                s.Append(ShortNames[p]).Append(' ');
                s.Append(r.x <= -1f ? "−∞" : r.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                s.Append('…');
                s.Append(r.y >= 1f ? "∞" : r.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }

            return s.Length == 0 ? "everywhere" : s.ToString();
        }
    }
}
