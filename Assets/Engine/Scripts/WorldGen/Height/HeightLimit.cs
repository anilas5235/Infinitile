using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Height{
    /// <summary>
    ///     Keeps H inside [Hmin, Hmax]. With a soft band B &gt; 0, H above the knee K = Hmax − B bends towards Hmax as
    ///     K + B · x / (1 + x) for x = (H − K) / B: slope 1 at the knee, so the terrain below stays as it is, and the
    ///     peaks round off instead of being cut into flat tops at Hmax, which they never reach. B = 0 is the hard clamp.
    /// </summary>
    public static class HeightLimit{
        /// <summary>H limited to [Hmin, Hmax] as described on the class.</summary>
        public static float Apply(float h, in WorldGenConfig cfg){
            float band = cfg.HeightSoftBand;
            if (band > 0f){
                float knee = cfg.HeightMax - band;
                if (h > knee){
                    float x = (h - knee) / band;
                    h = knee + band * (x / (1f + x));
                }
            }

            return math.clamp(h, cfg.HeightMin, cfg.HeightMax);
        }

        /// <summary>True when the unlimited H lies outside [Hmin, Hmax] (<c>ColumnSample.Clamped</c>).</summary>
        public static bool Exceeds(float unclamped, in WorldGenConfig cfg){
            return unclamped < cfg.HeightMin || unclamped > cfg.HeightMax;
        }
    }
}
