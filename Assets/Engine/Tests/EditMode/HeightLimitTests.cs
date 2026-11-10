using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The soft height limit (2026-11-10): H above HeightMax − band bends towards HeightMax instead of being cut
    ///     flat there; band 0 is the hard clamp.
    /// </summary>
    public class HeightLimitTests{
        private static WorldGenConfig Cfg(float band){
            return new WorldGenConfig{ HeightMin = 50f, HeightMax = 250f, HeightSoftBand = band };
        }

        [Test]
        public void HardClamp_WithoutBand(){
            WorldGenConfig cfg = Cfg(0f);
            Assert.AreEqual(250f, HeightLimit.Apply(300f, cfg));
            Assert.AreEqual(50f, HeightLimit.Apply(10f, cfg));
            Assert.AreEqual(180f, HeightLimit.Apply(180f, cfg));
        }

        [Test]
        public void SoftBand_KeepsHBelowTheKnee_RisesMonotonically_NeverReachesHmax(){
            WorldGenConfig cfg = Cfg(32f);
            Assert.AreEqual(218f, HeightLimit.Apply(218f, cfg), "knee");
            Assert.AreEqual(150f, HeightLimit.Apply(150f, cfg), "below the knee");
            Assert.AreEqual(234f, HeightLimit.Apply(250f, cfg), 1e-4f, "x = 1 gives half the band");
            float previous = HeightLimit.Apply(218f, cfg);
            for (float h = 218.5f; h <= 400f; h += .5f){
                float limited = HeightLimit.Apply(h, cfg);
                Assert.Greater(limited, previous, $"monotonic at {h}");
                Assert.Less(limited, 250f, $"below HeightMax at {h}");
                Assert.LessOrEqual(limited, h, $"never raised at {h}");
                previous = limited;
            }

            // Slope 1 at the knee: no kink where the band starts.
            Assert.AreEqual(.1f, HeightLimit.Apply(218.1f, cfg) - 218f, 1e-3f);
            // Distinct whole blocks up to the highest peaks of the shipped world (unclamped H about 300).
            Assert.Less((int)HeightLimit.Apply(280f, cfg), (int)HeightLimit.Apply(300f, cfg));
        }

        [Test]
        public void Exceeds_IsTheUnlimitedRangeTest(){
            WorldGenConfig cfg = Cfg(32f);
            Assert.IsFalse(HeightLimit.Exceeds(240f, cfg), "inside the band, still inside [Hmin, Hmax]");
            Assert.IsTrue(HeightLimit.Exceeds(251f, cfg));
            Assert.IsTrue(HeightLimit.Exceeds(49f, cfg));
        }
    }
}
