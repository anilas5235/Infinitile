using Unity.Burst;
using Unity.Mathematics;
#if UNITY_EDITOR
#endif

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Helper utilities to select a biome based on climate parameters and to sample coverage (editor diagnostics).
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Selects a biome given temperature, humidity, elevation, ground height, water level threshold,
        ///     continentality and mountain mask, and returns the resulting biome classification.
        /// </summary>
        [BurstCompile]
        private static Biome SelectBiome(float temp, float hum, float elev, int groundY, int waterThreshold,
            float continentality, float mountainMask)
        {
            // --- 0) Wasser & Strand (harte Extrema) ---
            if (groundY < waterThreshold - 8) return Biome.Ocean;
            if (groundY <= waterThreshold + 2 && temp > 0.35f) return Biome.Beach;

            // --- 1) Höhenextreme / Gebirge ---
            float elevEffective = elev + mountainMask * 0.04f;
            if (elevEffective > 1f) elevEffective = 1f;

            switch (elevEffective)
            {
                case >= 0.90f:
                    return Biome.HighStone;
                case >= 0.84f:
                    return Biome.GreyMountain;
                case >= 0.76f when temp <= 0.12f && hum > 0.45f:
                    return Biome.Ice;
                case >= 0.76f when temp <= 0.18f:
                    return Biome.Snow;
                case >= 0.76f:
                    return Biome.Mountain;
            }

            // --- 2) Klimaextreme (sehr kalt / sehr heiß) ---
            bool nearWater = groundY <= waterThreshold + 10;
            float var = Variation(temp, hum, elev, continentality);

            // 2a) Sehr kalt (temp <= 0.15)
            if (temp <= 0.15f)
                return hum switch
                {
                    > 0.70f => Biome.Ice,
                    > 0.50f => Biome.Snow,
                    < 0.30f => Biome.Tundra,
                    _ => var > 0.5f ? Biome.Snow : Biome.Tundra
                };
            // Übergangsbereich
            // 2b) Sehr heiß (temp >= 0.85)
            if (temp >= 0.8f)
                return hum switch
                {
                    > 0.70f => Biome.Jungle,
                    // sehr trocken & weit im Binnenland -> rote Wüste
                    < 0.2f => Biome.RedDesert,
                    < 0.45f => Biome.Desert,
                    _ => hum >= 0.50f ? Biome.Forest : Biome.Plains
                };

            // --- 3) gemäßigt-kalte Zonen (0.15 < temp < 0.40) ---
            if (temp < 0.40f)
                return hum switch
                {
                    // sehr feucht in Wassernähe -> Sumpf
                    > 0.80f when nearWater => Biome.Swamp,
                    > 0.60f => Biome.Forest,
                    < 0.4f => Biome.Tundra,
                    _ => var > 0.6f ? Biome.Forest : Biome.Plains
                };

            // --- 4) gemäßigt-warme Zonen (0.40 <= temp < 0.70) ---
            if (temp < 0.70f)
                return hum switch
                {
                    > 0.7f when nearWater => Biome.Swamp,
                    > 0.62f => Biome.Forest,
                    // leicht trocken -> Plains mit etwas Forest
                    < 0.2f when temp > .6f => Biome.Desert,
                    _ => var > 0.65f ? Biome.Forest : Biome.Plains
                };

            // --- 5) warme Zonen (0.70 <= temp < 0.8) ---
            {
                if (hum > 0.76f && nearWater) return Biome.Swamp;

                // Trockene, warme Binnenregionen -> Wüste
                if (!(continentality >= 0.40f)) return var > 0.55f ? Biome.Forest : Biome.Plains;

                return hum switch
                {
                    < .2f => Biome.RedDesert,
                    < .4f => Biome.Desert,
                    _ => var > 0.55f ? Biome.Forest : Biome.Plains
                };
            }
        }

        /// <summary>
        ///     Deterministic variation function (0..1) for additional noise based on inputs.
        /// </summary>
        private static float Variation(float a, float b, float c, float d)
        {
            float t = a * 12.9898f + b * 78.233f + c * 37.719f + d * 11.123f;
            float s = math.sin(t) * 43758.5453f;
            return math.frac(s);
        }
    }
}