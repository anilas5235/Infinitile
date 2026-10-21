using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Drives the sky brightness and tint of the voxel shaders and the rotation of the URP main
    ///     directional light from a time of day. Day and night only change shader globals; the propagated light
    ///     levels are never recomputed.
    /// </summary>
    [ExecuteAlways]
    public class DayNightCycle : MonoBehaviour{
        [Tooltip("Time of day in hours (0 = midnight, 6 = sunrise, 12 = noon, 18 = sunset).")]
        [Range(0f, 24f)] [SerializeField] private float timeOfDay = 10f;

        [Tooltip("In-game hours per real second while playing; 0 stops the clock. 0.02 is a 20-minute day.")]
        [Min(0f)] [SerializeField] private float speed = 0.02f;

        [Tooltip("The main directional light. Uses RenderSettings.sun when empty.")]
        [SerializeField] private Light sun;

        [Tooltip("Compass direction of the sun's path in degrees.")]
        [SerializeField] private float sunYaw = 30f;

        [Tooltip("Sky brightness at night; the day is 1.")]
        [Range(0f, 1f)] [SerializeField] private float nightBrightness = 0.12f;

        [SerializeField] private Color dayTint = Color.white;
        [SerializeField] private Color duskTint = new(1f, 0.62f, 0.42f);
        [SerializeField] private Color nightTint = new(0.45f, 0.55f, 1f);

        [Tooltip("Minimum light of every voxel face (caves without block light).")]
        [Range(0f, 0.5f)] [SerializeField] private float ambient = VoxelLightingGlobals.DefaultAmbient;

        /// <summary>Time of day in hours, 0–24.</summary>
        public float TimeOfDay{ get => timeOfDay; set => timeOfDay = Mathf.Repeat(value, 24f); }

        /// <summary>In-game hours per real second while playing.</summary>
        public float Speed{ get => speed; set => speed = math.max(0f, value); }

        private void OnEnable(){
            Apply();
        }

        private void OnDisable(){
            VoxelLightingGlobals.SetDefaults();
        }

        private void OnValidate(){
            Apply();
        }

        private void Update(){
            if (Application.isPlaying && speed > 0f) TimeOfDay = timeOfDay + speed * Time.deltaTime;
            Apply();
        }

        private void Apply(){
            float elevation = SunElevation(timeOfDay);
            VoxelLightingGlobals.Set(SkyBrightness(elevation, nightBrightness),
                SkyTint(elevation, dayTint, duskTint, nightTint), ambient);

            Light target = sun != null ? sun : RenderSettings.sun;
            if (target != null) target.transform.rotation = SunRotation(timeOfDay, sunYaw);
        }

        /// <summary>Sine of the sun's elevation: −1 at midnight, 0 at 6 and 18 o'clock, 1 at noon.</summary>
        internal static float SunElevation(float timeOfDay){
            return math.sin((timeOfDay / 24f - 0.25f) * 2f * math.PI);
        }

        /// <summary>Sky brightness: night level below the horizon, full day once the sun is a little above it.</summary>
        internal static float SkyBrightness(float elevation, float nightBrightness){
            return math.lerp(nightBrightness, 1f, math.smoothstep(-0.1f, 0.25f, elevation));
        }

        /// <summary>Sky tint: dusk at the horizon, blending to day above and to night below it.</summary>
        internal static Color SkyTint(float elevation, Color day, Color dusk, Color night){
            return elevation >= 0f
                ? Color.Lerp(dusk, day, math.smoothstep(0f, 0.3f, elevation))
                : Color.Lerp(dusk, night, math.smoothstep(0f, 0.2f, -elevation));
        }

        /// <summary>Directional light rotation: pointing straight down at noon, along the horizon at 6 and 18.</summary>
        internal static Quaternion SunRotation(float timeOfDay, float yaw){
            return Quaternion.Euler(timeOfDay / 24f * 360f - 90f, yaw, 0f);
        }
    }
}
