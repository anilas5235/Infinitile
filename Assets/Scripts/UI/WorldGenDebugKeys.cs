using System;
using System.Collections.Generic;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Settings;
using Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UI{
    /// <summary>
    ///     Debug keys for comparing world-generation configurations in game. F10 regenerates the loaded world
    ///     with the next configuration (Shift+F10: the previous one). The first entry uses the assigned
    ///     <see cref="WorldGenSettings" /> unchanged; the others override architecture and blend methods on a transient
    ///     copy of it, so strengths, biomes and splines stay the asset's and the asset is never written. F1–F4 jump to
    ///     a camera bookmark, Shift+F1–F4 store the current pose; bookmarks persist in PlayerPrefs, so the same spot can
    ///     be compared across configurations and sessions. F11 switches the architecture of the assigned asset. The HUD
    ///     shows <see cref="Message" /> and calls the same actions from its buttons. Game-side only; the engine
    ///     exposes <see cref="VoxelWorld.Regenerate" />.
    /// </summary>
    public class WorldGenDebugKeys : MonoBehaviour{
        private const string BookmarkPrefsKey = "WorldGenDebugKeys.Bookmark";

        [SerializeField] private Key regenerateKey = Key.F10;
        [SerializeField] private Key architectureKey = Key.F11;
        [SerializeField] private Key[] bookmarkKeys = { Key.F1, Key.F2, Key.F3, Key.F4 };

        [Tooltip("Player root that is moved and yawed by a bookmark; found in the scene when empty.")]
        [SerializeField] private PlayerManager player;

        [SerializeField] private float labelSeconds = 3f;

        [SerializeField] private List<Configuration> configurations = new(){
            new Configuration{ Name = "As assigned", AsAssigned = true },
            new Configuration{ Architecture = GenArchitecture.BiomeFirst },
            new Configuration{
                Architecture = GenArchitecture.BiomeFirst, MaterialMethod = BlendMethod.DistanceBlend,
                HeightMethod = BlendMethod.DistanceBlend
            },
            new Configuration{
                Architecture = GenArchitecture.BiomeFirst, MaterialMethod = BlendMethod.ParameterSpaceWeighting,
                HeightMethod = BlendMethod.ParameterSpaceWeighting
            },
            new Configuration{
                Architecture = GenArchitecture.BiomeFirst, MaterialMethod = BlendMethod.DistortedBoundary,
                HeightMethod = BlendMethod.DistortedBoundary
            },
            new Configuration{ Architecture = GenArchitecture.TerrainFirst },
            new Configuration{ Architecture = GenArchitecture.TerrainFirst, MaterialMethod = BlendMethod.DistanceBlend }
        };

        private int _current;
        private float _messageUntil;
        private LookController _look;

        /// <summary>Number of bookmark slots.</summary>
        public int BookmarkCount => bookmarkKeys.Length;

        /// <summary>Label of the configuration the world was last regenerated with by these keys.</summary>
        public string CurrentConfiguration => configurations.Count > 0 ? configurations[_current].Label : "";

        /// <summary>The last action's result, null once it has been shown for a few seconds.</summary>
        public string Message => Time.unscaledTime <= _messageUntil ? _message : null;

        private string _message;

        private void Update(){
            Keyboard keyboard = Keyboard.current;
            VoxelWorld world = VoxelWorld.Instance;
            if (keyboard == null || world == null) return;

            bool shift = keyboard.shiftKey.isPressed;
            if (keyboard[regenerateKey].wasPressedThisFrame) StepConfiguration(shift ? -1 : 1);
            if (keyboard[architectureKey].wasPressedThisFrame) SwitchArchitecture();

            for (int i = 0; i < bookmarkKeys.Length; i++){
                if (!keyboard[bookmarkKeys[i]].wasPressedThisFrame) continue;
                if (shift) StoreBookmark(i);
                else RestoreBookmark(i);
            }
        }

        /// <summary>Regenerates the world with the next (<paramref name="step" /> 1) or previous (−1) configuration.</summary>
        public void StepConfiguration(int step){
            VoxelWorld world = VoxelWorld.Instance;
            if (world == null || configurations.Count == 0) return;
            _current = (_current + step + configurations.Count) % configurations.Count;
            Configuration configuration = configurations[_current];
            Regenerate(world, configuration.AsAssigned ? null : configuration,
                $"World generation {_current + 1}/{configurations.Count}: {configuration.Label}");
        }

        /// <summary>
        ///     Regenerates the world with the assigned asset under the other architecture than the one in use; the
        ///     asset's blend methods stay.
        /// </summary>
        public void SwitchArchitecture(){
            VoxelWorld world = VoxelWorld.Instance;
            if (world == null || !world.TryGetGenerationMethods(out GenArchitecture architecture, out _, out _)) return;
            WorldGenSettings asset = world.Settings.WorldGen;
            Configuration configuration = new(){
                Architecture = architecture == GenArchitecture.BiomeFirst
                    ? GenArchitecture.TerrainFirst
                    : GenArchitecture.BiomeFirst,
                MaterialMethod = asset.MaterialMethod,
                HeightMethod = asset.HeightMethod
            };
            Regenerate(world, configuration, $"World generation: {configuration.Label}");
        }

        /// <summary>Regenerates with <paramref name="configuration" /> applied to a copy of the asset (null = the asset).</summary>
        private void Regenerate(VoxelWorld world, Configuration configuration, string message){
            WorldGenSettings variant = null;
            try{
                if (configuration != null){
                    variant = Instantiate(world.Settings.WorldGen);
                    variant.Architecture = configuration.Architecture;
                    variant.MaterialMethod = configuration.MaterialMethod;
                    variant.HeightMethod = configuration.HeightMethod;
                }

                world.Regenerate(variant);
                Show(message);
                if (FindPlayer()) player.HoldUntilGrounded(); // the colliders under the player are gone until reload
            }
            catch (Exception e){
                Debug.LogException(e);
                Show($"{message}: {e.Message}");
            }
            finally{
                if (variant) Destroy(variant); // the engine copied its values
            }
        }

        /// <summary>True when bookmark <paramref name="index" /> holds a pose.</summary>
        public bool HasBookmark(int index){
            return PlayerPrefs.HasKey(BookmarkPrefsKey + index);
        }

        /// <summary>Stores the player's pose in bookmark <paramref name="index" />.</summary>
        public void StoreBookmark(int index){
            if (!FindPlayer()) return;
            Transform root = player.transform;
            Bookmark bookmark = new(){
                Position = root.position, Yaw = root.eulerAngles.y, Pitch = _look ? _look.Pitch : 0f
            };
            PlayerPrefs.SetString(BookmarkPrefsKey + index, JsonUtility.ToJson(bookmark));
            PlayerPrefs.Save();
            Show($"Bookmark {index + 1} stored at {bookmark.Position:F0}");
        }

        /// <summary>Moves the player to bookmark <paramref name="index" />.</summary>
        public void RestoreBookmark(int index){
            string json = PlayerPrefs.GetString(BookmarkPrefsKey + index, null);
            if (string.IsNullOrEmpty(json)){
                Show($"Bookmark {index + 1} is empty (Shift+{bookmarkKeys[index]} stores one)");
                return;
            }

            if (!FindPlayer()) return;
            Bookmark bookmark = JsonUtility.FromJson<Bookmark>(json);
            Transform root = player.transform;

            // A CharacterController overwrites a moved transform while it is enabled.
            CharacterController controller = root.GetComponent<CharacterController>();
            bool controllerEnabled = controller && controller.enabled;
            if (controller) controller.enabled = false;
            root.SetPositionAndRotation(bookmark.Position, Quaternion.Euler(0f, bookmark.Yaw, 0f));
            if (controller) controller.enabled = controllerEnabled;
            if (_look) _look.SetPitch(bookmark.Pitch);

            player.HoldUntilGrounded(); // the target may not be loaded yet
            Show($"Bookmark {index + 1}: {bookmark.Position:F0}");
        }

        private bool FindPlayer(){
            if (!player) player = FindFirstObjectByType<PlayerManager>();
            if (player && !_look) _look = player.GetComponentInChildren<LookController>();
            return player;
        }

        private void Show(string message){
            _message = message;
            _messageUntil = Time.unscaledTime + labelSeconds;
        }

        [Serializable]
        public class Configuration{
            [Tooltip("Shown when the configuration is selected; built from the fields when empty.")]
            public string Name;

            [Tooltip("Use the assigned WorldGenSettings asset unchanged; the fields below are ignored.")]
            public bool AsAssigned;

            public GenArchitecture Architecture = GenArchitecture.BiomeFirst;
            public BlendMethod MaterialMethod = BlendMethod.None;

            [Tooltip("Biome-first only; Terrain-first has no height stage.")]
            public BlendMethod HeightMethod = BlendMethod.None;

            public string Label{
                get{
                    if (!string.IsNullOrEmpty(Name)) return Name;
                    return Architecture == GenArchitecture.BiomeFirst
                        ? $"Biome-first · material {MaterialMethod} · height {HeightMethod}"
                        : $"Terrain-first · material {MaterialMethod}";
                }
            }
        }

        [Serializable]
        private struct Bookmark{
            public Vector3 Position;
            public float Yaw;
            public float Pitch;
        }
    }
}
