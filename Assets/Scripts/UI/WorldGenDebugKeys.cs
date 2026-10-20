using System;
using System.Collections.Generic;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Settings;
using Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UI{
    /// <summary>
    ///     INT-30 debug keys for comparing world-generation configurations in game. F10 regenerates the loaded world
    ///     with the next configuration (Shift+F10: the previous one). The first entry uses the assigned
    ///     <see cref="WorldGenSettings" /> unchanged; the others override architecture and blend methods on a transient
    ///     copy of it, so strengths, biomes and splines stay the asset's and the asset is never written. F1–F4 jump to
    ///     a camera bookmark, Shift+F1–F4 store the current pose; bookmarks persist in PlayerPrefs, so the same spot can
    ///     be compared across configurations and sessions. Game-side only; the engine exposes
    ///     <see cref="VoxelWorld.Regenerate" />.
    /// </summary>
    public class WorldGenDebugKeys : MonoBehaviour{
        private const string BookmarkPrefsKey = "WorldGenDebugKeys.Bookmark";

        [SerializeField] private Key regenerateKey = Key.F10;
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
        private string _label;
        private float _labelUntil;
        private LookController _look;

        private void Update(){
            Keyboard keyboard = Keyboard.current;
            VoxelWorld world = VoxelWorld.Instance;
            if (keyboard == null || world == null) return;

            bool shift = keyboard.shiftKey.isPressed;
            if (keyboard[regenerateKey].wasPressedThisFrame && configurations.Count > 0) Regenerate(world, shift ? -1 : 1);

            for (int i = 0; i < bookmarkKeys.Length; i++){
                if (!keyboard[bookmarkKeys[i]].wasPressedThisFrame) continue;
                if (shift) StoreBookmark(i);
                else RestoreBookmark(i);
            }
        }

        private void OnGUI(){
            if (_label == null || Time.unscaledTime > _labelUntil) return;
            GUI.Label(new Rect(10, 10, 900, 24), _label);
        }

        private void Regenerate(VoxelWorld world, int step){
            _current = (_current + step + configurations.Count) % configurations.Count;
            Configuration configuration = configurations[_current];
            WorldGenSettings variant = null;
            try{
                if (!configuration.AsAssigned){
                    variant = Instantiate(world.Settings.WorldGen);
                    variant.Architecture = configuration.Architecture;
                    variant.MaterialMethod = configuration.MaterialMethod;
                    variant.HeightMethod = configuration.HeightMethod;
                }

                world.Regenerate(variant);
                Show($"World generation {_current + 1}/{configurations.Count}: {configuration.Label}");
                if (FindPlayer()) player.HoldUntilGrounded(); // the colliders under the player are gone until reload
            }
            catch (Exception e){
                Debug.LogException(e);
                Show($"{configuration.Label}: {e.Message}");
            }
            finally{
                if (variant) Destroy(variant); // the engine copied its values (D-05)
            }
        }

        private void StoreBookmark(int index){
            if (!FindPlayer()) return;
            Transform root = player.transform;
            Bookmark bookmark = new(){
                Position = root.position, Yaw = root.eulerAngles.y, Pitch = _look ? _look.Pitch : 0f
            };
            PlayerPrefs.SetString(BookmarkPrefsKey + index, JsonUtility.ToJson(bookmark));
            PlayerPrefs.Save();
            Show($"Bookmark {index + 1} stored at {bookmark.Position:F0}");
        }

        private void RestoreBookmark(int index){
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

        private void Show(string label){
            _label = label;
            _labelUntil = Time.unscaledTime + labelSeconds;
        }

        [Serializable]
        public class Configuration{
            [Tooltip("Shown when the configuration is selected; built from the fields when empty.")]
            public string Name;

            [Tooltip("Use the assigned WorldGenSettings asset unchanged; the fields below are ignored.")]
            public bool AsAssigned;

            public GenArchitecture Architecture = GenArchitecture.BiomeFirst;
            public BlendMethod MaterialMethod = BlendMethod.None;

            [Tooltip("Biome-first only; Terrain-first has no height stage (TRN-21).")]
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
