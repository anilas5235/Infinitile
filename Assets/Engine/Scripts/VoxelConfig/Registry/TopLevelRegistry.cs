using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Unity.Collections;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public abstract class TopLevelRegistry<T> : IDisposable where T : ScriptableObject
    {
        protected readonly Registry<FixedString32Bytes> NameRegistry = new(100);
        protected readonly Registry<T> SoRegistry = new(100);

        protected bool Initialized { get; private set; }
        protected bool Finalized { get; private set; }

        protected void InternalInitialize()
        {
            if (Initialized) throw new InvalidOperationException("Registry is already initialized.");
            Initialized = true;
        }

        protected void InternalFinalize()
        {
            if (!Initialized) throw new InvalidOperationException("Registry is not initialized.");
            if (Finalized) throw new InvalidOperationException("Registry is already finalized.");
            Finalized = true;
        }

        public void Register(FixedString32Bytes packagePrefix, T so)
        {
            if (!Initialized) throw new InvalidOperationException("VoxelRegistry is not initialized.");
            if (Finalized) throw new InvalidOperationException("VoxelRegistry has been finalized.");
            if (!so)
            {
                VoxelEngineLogger.Error<TopLevelRegistry<T>>(
                    $"Cannot register a null {typeof(T).Name} definition in package {packagePrefix}.");
                return;
            }

            FixedString32Bytes fullName;
            try
            {
                fullName = new FixedString32Bytes(packagePrefix + ":" + so.name);
            }
            catch (ArgumentException e)
            {
                VoxelEngineLogger.Error<TopLevelRegistry<T>>(
                    $"Name '{so.name}' exceeds the maximum length of {FixedString32Bytes.UTF8MaxLengthInBytes} bytes. Registration skipped.({e.Message})");
                return;
            }

            if (TryGetId(fullName, out ushort existingId))
            {
                VoxelEngineLogger.Warn<TopLevelRegistry<T>>(
                    $"A {typeof(T).Name} with the name '{fullName}' is already registered with ID {existingId}. Registration skipped.");
                return;
            }

            ushort id = NameRegistry.Register(fullName);
            SoRegistry.Register(id, so);

            SubRegister(id, fullName, so);
            
            VoxelEngineLogger.Info<TopLevelRegistry<T>>($"Registered {typeof(T).Name} '{fullName}' with ID {id}");
        }

        protected virtual void SubRegister(ushort id, FixedString32Bytes fullName, T so)
        {
        }

        public bool TryGetId(FixedString32Bytes name, out ushort id) => NameRegistry.TryGetId(name, out id);

        public ushort GetIdOrThrow(FixedString32Bytes name)
        {
            return TryGetId(name, out ushort id)
                ? id
                : throw new KeyNotFoundException($"{name} was not found in the registry.");
        }

        public bool TryGetName(ushort id, out FixedString32Bytes name) => NameRegistry.TryGet(id, out name);

        public bool TryGet(ushort id, out T so) => SoRegistry.TryGet(id, out so);

        public abstract void Dispose();
    }
}