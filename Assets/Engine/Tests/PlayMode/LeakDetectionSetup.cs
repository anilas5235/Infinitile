using NUnit.Framework;
using Unity.Collections;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     TST-20: native leak detection with stack traces is on for the whole PlayMode run so a container that a
    ///     test forgets to dispose is reported with the allocation site. The previous mode is restored afterwards.
    /// </summary>
    [SetUpFixture]
    public class LeakDetectionSetup{
        private NativeLeakDetectionMode _previousMode;

        [OneTimeSetUp]
        public void EnableLeakDetection(){
            _previousMode = NativeLeakDetection.Mode;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        }

        [OneTimeTearDown]
        public void RestoreLeakDetection(){
            NativeLeakDetection.Mode = _previousMode;
        }
    }
}
