using UnityEngine;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     The frame cap in force, as the period of a capped frame: vertical sync (<see cref="QualitySettings.vSyncCount" />
    ///     with the display's refresh rate) or <see cref="Application.targetFrameRate" />. Read every frame by the
    ///     scheduler when its budget follows the cap (<see cref="FrameBudget.FollowsCap" />), so a cap changed at run
    ///     time (a settings menu) is followed from the next frame. Allocates nothing.
    ///     What it cannot see: vertical sync forced by the graphics driver, and the VSync switch of the Editor's
    ///     game view. A frame capped that way counts as uncapped and keeps the plain budget.
    /// </summary>
    internal static class FrameCap{
        /// <summary>The period of the frame cap in force in milliseconds; 0 when the frame rate is not capped.</summary>
        internal static float PeriodMs(){
            int vSyncCount = QualitySettings.vSyncCount;
            double refreshHz = vSyncCount > 0 ? Screen.currentResolution.refreshRateRatio.value : 0d;
            return PeriodMs(vSyncCount, Application.targetFrameRate, refreshHz);
        }

        /// <summary>
        ///     The period of a capped frame in milliseconds; 0 for no cap. Vertical sync wins over a target frame
        ///     rate, as it does on desktop platforms (Unity ignores <see cref="Application.targetFrameRate" /> while
        ///     <see cref="QualitySettings.vSyncCount" /> is above 0). A vertical sync with an unknown refresh rate
        ///     counts as no cap.
        /// </summary>
        /// <param name="vSyncCount">Vertical blanks per frame; 0 for no vertical sync.</param>
        /// <param name="targetFrameRate">Frames per second; 0 or less for no target.</param>
        /// <param name="refreshHz">Refresh rate of the display in Hz.</param>
        internal static float PeriodMs(int vSyncCount, int targetFrameRate, double refreshHz){
            if (vSyncCount > 0) return refreshHz > 0d ? (float)(vSyncCount * 1000d / refreshHz) : 0f;
            return targetFrameRate > 0 ? 1000f / targetFrameRate : 0f;
        }
    }
}
