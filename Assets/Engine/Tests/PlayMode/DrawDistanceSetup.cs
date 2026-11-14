using NUnit.Framework;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     The PlayMode suite changes the draw distance of the settings asset object in memory (see
    ///     <see cref="PlayModeFixtures.LoadGameScene" />). After the run the shipped value is put back, so Play mode
    ///     in the Editor does not keep the suite's draw distance.
    /// </summary>
    [SetUpFixture]
    public class DrawDistanceSetup{
        [OneTimeTearDown]
        public void RestoreShippedDrawDistance(){
            PlayModeFixtures.SetAssetDrawDistance(PlayModeFixtures.ShippedDrawDistance);
        }
    }
}
