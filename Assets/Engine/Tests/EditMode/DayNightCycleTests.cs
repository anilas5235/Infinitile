using Engine.Scripts.Render;
using NUnit.Framework;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>The day/night curve of the DayNightCycle.</summary>
    public class DayNightCycleTests{
        [TestCase(0f, -1f)]
        [TestCase(6f, 0f)]
        [TestCase(12f, 1f)]
        [TestCase(18f, 0f)]
        public void SunElevation_FollowsTheClock(float hour, float expected){
            Assert.AreEqual(expected, DayNightCycle.SunElevation(hour), 1e-5f);
        }

        [Test]
        public void SkyBrightness_IsFullAtNoonAndNightLevelAtMidnight(){
            Assert.AreEqual(1f, DayNightCycle.SkyBrightness(DayNightCycle.SunElevation(12f), 0.1f), 1e-5f);
            Assert.AreEqual(0.1f, DayNightCycle.SkyBrightness(DayNightCycle.SunElevation(0f), 0.1f), 1e-5f);
            float dawn = DayNightCycle.SkyBrightness(DayNightCycle.SunElevation(6f), 0.1f);
            Assert.That(dawn, Is.GreaterThan(0.1f).And.LessThan(1f));
        }

        [Test]
        public void SkyTint_IsDuskAtTheHorizon(){
            Color day = Color.white, dusk = Color.red, night = Color.blue;
            Assert.AreEqual(dusk, DayNightCycle.SkyTint(0f, day, dusk, night));
            Assert.AreEqual(day, DayNightCycle.SkyTint(1f, day, dusk, night));
            Assert.AreEqual(night, DayNightCycle.SkyTint(-1f, day, dusk, night));
        }

        [Test]
        public void SunRotation_PointsDownAtNoon(){
            Vector3 noon = DayNightCycle.SunRotation(12f, 30f) * Vector3.forward;
            Assert.AreEqual(-1f, noon.y, 1e-4f);
            Vector3 sunrise = DayNightCycle.SunRotation(6f, 30f) * Vector3.forward;
            Assert.AreEqual(0f, sunrise.y, 1e-4f);
        }

        [Test]
        public void FogParams_EncodeTheRangeAndTheSkyGradient(){
            // The shader computes saturate((d - x) * y): 0 at the start, 1 at the end.
            Vector4 p = VoxelLightingGlobals.FogParams(true, 115.2f, 192f, .6f);
            Assert.AreEqual(0f, (115.2f - p.x) * p.y, 1e-5f);
            Assert.AreEqual(1f, (192f - p.x) * p.y, 1e-5f);
            Assert.AreEqual(1f, p.z);
            Assert.AreEqual(.6f, p.w);
            Assert.AreEqual(0f, VoxelLightingGlobals.FogParams(false, 0f, 10f, 1f).z, "off");
            Assert.IsTrue(float.IsFinite(VoxelLightingGlobals.FogParams(true, 50f, 50f, 1f).y), "empty range");
        }

        [Test]
        public void FogCurve_RisesExponentiallyFromZeroToOne(){
            // The shader computes (exp(x * t) - 1) * y over t = 0..1.
            Vector4 c = VoxelLightingGlobals.FogCurve(3f);
            float Fog(float t) => (Mathf.Exp(c.x * t) - 1f) * c.y;
            Assert.AreEqual(0f, Fog(0f), 1e-6f);
            Assert.AreEqual(1f, Fog(1f), 1e-5f);
            Assert.Less(Fog(.5f), .5f, "below the straight ramp");
            Vector4 flat = VoxelLightingGlobals.FogCurve(0f);
            Assert.IsTrue(float.IsFinite(flat.y), "zero steepness");
            Assert.AreEqual(.5f, (Mathf.Exp(flat.x * .5f) - 1f) * flat.y, 1e-3f, "zero steepness is a straight ramp");
        }
    }
}
