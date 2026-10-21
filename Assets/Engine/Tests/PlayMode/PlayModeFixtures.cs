using System;
using System.Collections;
using System.Reflection;
using Engine.Scripts.Data;
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
        ///     Loads the game scene once per play session and waits until the engine singletons exist.
        ///     Later tests find the scene still loaded and return immediately.
        /// </summary>
        public static IEnumerator LoadGameScene(float timeoutSeconds = 60f){
            if (VoxelWorld.Instance == null){
                AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
                Assert.IsNotNull(load, $"scene '{SceneName}' is not in the build settings");
                while (!load.isDone) yield return null;
            }

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
