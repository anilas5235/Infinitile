using Engine.Scripts.Diagnostics;
using Engine.Scripts.World;
using UnityEngine;

namespace UI{
    /// <summary>
    ///     The player's choices from the settings menu (draw distance, frame rate, VSync), kept in PlayerPrefs between
    ///     runs. The saved draw distance is handed to the world before the first scene builds it; the frame cap is
    ///     applied when the HUD starts. A measurement run or a test (<see cref="AutomatedRun" />) ignores both.
    /// </summary>
    public static class GameSettings{
        /// <summary>Draw distances the menu offers, in chunks.</summary>
        public static readonly int[] DrawDistances = { 4, 6, 8, 10, 12, 16 };

        /// <summary>Frame caps the menu offers, in frames per second; 0 is no cap.</summary>
        public static readonly int[] FrameRates = { 60, 90, 120, 300, 0 };

        private const string DrawDistanceKey = "settings.drawDistance";
        private const string FrameRateKey = "settings.frameRate";
        private const string VSyncKey = "settings.vSync";

        /// <summary>The saved draw distance in chunks; 0 when none is saved (the settings asset's applies).</summary>
        public static int DrawDistance{
            get => PlayerPrefs.GetInt(DrawDistanceKey, 0);
            set{
                PlayerPrefs.SetInt(DrawDistanceKey, value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>The saved frame cap in frames per second; 0 is no cap (the default).</summary>
        public static int FrameRate{
            get => Mathf.Max(PlayerPrefs.GetInt(FrameRateKey, 0), 0);
            set{
                PlayerPrefs.SetInt(FrameRateKey, Mathf.Max(value, 0));
                PlayerPrefs.Save();
            }
        }

        /// <summary>VSync on: the display's refresh rate caps the frames and the frame rate choice is not used.</summary>
        public static bool VSync{
            get => PlayerPrefs.GetInt(VSyncKey, 0) != 0;
            set{
                PlayerPrefs.SetInt(VSyncKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Applies the saved frame cap and VSync. The engine's main-thread budget follows the cap by itself.</summary>
        public static void ApplyFrameCap(){
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = VSync || FrameRate <= 0 ? -1 : FrameRate;
        }

        /// <summary>The frame cap at start, unless a measurement run or a test drives the engine.</summary>
        public static void ApplyFrameCapAtStart(){
            if (!AutomatedRun.Active) ApplyFrameCap();
        }

        /// <summary>
        ///     Before the first scene loads: the world builds itself with the saved draw distance. The world itself
        ///     skips it in a measurement run or a test, which may only say so later.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void HandSavedDrawDistanceToWorld(){
            int drawDistance = DrawDistance;
            if (drawDistance >= VoxelWorld.MinDrawDistance && drawDistance <= VoxelWorld.MaxDrawDistance)
                VoxelWorld.RequestedDrawDistance = drawDistance;
        }
    }
}
