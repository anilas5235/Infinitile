using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.PlayMode{
    /// <summary>Shared helpers for the PlayMode tests: scene loading, settings and reflection access.</summary>
    internal static class PlayModeFixtures{
        public const string SceneName = "SampleScene";
        public const ushort Air = 0;
        public const ushort Grass = 1;

        /// <summary>
        ///     Draw distance of the world the PlayMode suite runs in. The time limits, page counts and timings of the
        ///     suite were written for it, so it stays when the shipped draw distance changes; the tests of
        ///     <see cref="ShippedDrawDistanceTests" /> run at the shipped one.
        /// </summary>
        public const int BaselineDrawDistance = 6;

        private const string SettingsAssetPath = "Assets/EngineSettings/VoxelEngineSettings.asset";
        private static int _shippedDrawDistance;

        /// <summary>
        ///     Draw distance of the shipped settings asset. In the Editor it is read from the file, because the suite
        ///     changes the asset object in memory; in a player it is the distance of the first scene load.
        /// </summary>
        public static int ShippedDrawDistance{
            get{
#if UNITY_EDITOR
                if (_shippedDrawDistance == 0){
                    Match match = Regex.Match(File.ReadAllText(SettingsAssetPath), @"^\s*DrawDistance:\s*(\d+)",
                        RegexOptions.Multiline);
                    Assert.IsTrue(match.Success, $"no DrawDistance in {SettingsAssetPath}");
                    _shippedDrawDistance = int.Parse(match.Groups[1].Value);
                }
#endif
                Assert.Greater(_shippedDrawDistance, 0, "the shipped draw distance is known after the first scene load");
                return _shippedDrawDistance;
            }
        }

        /// <summary>
        ///     Loads the game scene once per play session and waits until the engine singletons exist. Later tests
        ///     find the scene still loaded and return immediately, unless the world has another draw distance than
        ///     <paramref name="drawDistance" />: then the world is reloaded with it.
        /// </summary>
        public static IEnumerator LoadGameScene(float timeoutSeconds = 60f, int drawDistance = BaselineDrawDistance){
            if (VoxelWorld.Instance == null){
                SetAssetDrawDistance(drawDistance);
                AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
                Assert.IsNotNull(load, $"scene '{SceneName}' is not in the build settings");
                while (!load.isDone) yield return null;
            }

            yield return WaitForEngine(timeoutSeconds);
            if (_shippedDrawDistance == 0) _shippedDrawDistance = VoxelWorld.Instance.Settings.Chunk.DrawDistance;
            if (VoxelWorld.Instance.Settings.Chunk.DrawDistance != drawDistance)
                yield return ReloadWorldWithDrawDistance(drawDistance, timeoutSeconds);
        }

        /// <summary>
        ///     Reloads the game scene with another draw distance; the settings asset is shared in memory. Waits for
        ///     an idle scheduler first: unloading the scene while a CMeshBuildJob runs frees the registry's
        ///     VoxelRenderDefs under the job (shutdown order between DataImporter and VoxelWorld).
        /// </summary>
        public static IEnumerator ReloadWorldWithDrawDistance(int drawDistance, float timeoutSeconds = 60f){
            float deadline = Time.realtimeSinceStartup + 300f;
            while (!SchedulerSleeping() && Time.realtimeSinceStartup < deadline) yield return null;
            VoxelWorld.Instance.Settings.Chunk.DrawDistance = drawDistance;
            AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return WaitForEngine(timeoutSeconds);
        }

        /// <summary>True when every scheduler handler sleeps, i.e. the world around the focus is fully built.</summary>
        public static bool SchedulerSleeping(){
            VoxelEngineScheduler scheduler = GetPrivateField<VoxelEngineScheduler>(VoxelWorld.Instance, "_scheduler");
            DataJobStateHandler data = GetPrivateField<DataJobStateHandler>(scheduler, "_dataJobHandler");
            MeshJobStateHandler mesh = GetPrivateField<MeshJobStateHandler>(scheduler, "_meshJobHandler");
            ColliderJobStateHandler collider = GetPrivateField<ColliderJobStateHandler>(scheduler, "_colliderJobHandler");
            return data.Sleeping && mesh.Sleeping && collider.Sleeping;
        }

        /// <summary>
        ///     Editor only: writes the draw distance into the settings asset object in memory (never to the file), so
        ///     the first scene load of a play session already builds the wanted world.
        /// </summary>
        public static void SetAssetDrawDistance(int drawDistance){
#if UNITY_EDITOR
            _ = ShippedDrawDistance; // read from the file before the object changes
            VoxelEngineSettings settings = UnityEditor.AssetDatabase.LoadAssetAtPath<VoxelEngineSettings>(SettingsAssetPath);
            Assert.IsNotNull(settings, SettingsAssetPath);
            settings.Chunk.DrawDistance = drawDistance;
#endif
        }

        private static IEnumerator WaitForEngine(float timeoutSeconds){
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (VoxelWorld.Instance == null || DataImporter.Instance == null || VoxelWorld.Instance.ChunkManager == null){
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("engine did not start in time");
                yield return null;
            }

            yield return null; // let Start() run on everything
        }

        public static VoxelEngineSettings CreateSettings(int drawDistance, GameObject chunkPrefab){
            VoxelEngineSettings settings = ScriptableObject.CreateInstance<VoxelEngineSettings>();
            settings.Seed = 1337;
            settings.Chunk = new ChunkSettings{
                ChunkPrefab = chunkPrefab,
                DrawDistance = drawDistance
            };
            settings.Scheduler = new SchedulerSettings();
            settings.Renderer = new RendererSettings();
            return settings;
        }

        public static ChunkVoxelData CreateUniformChunk(ushort voxelId){
            ChunkVoxelData data = new(16);
            data.AddVoxels(voxelId, VoxelsPerChunk);
            return data;
        }

        public static T GetPrivateField<T>(object target, string name){
            FieldInfo field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{name} not found");
            return (T)field.GetValue(target);
        }

        public static MethodInfo GetPrivateMethod(Type type, string name){
            MethodInfo method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, $"{type.Name}.{name} not found");
            return method;
        }
    }
}
