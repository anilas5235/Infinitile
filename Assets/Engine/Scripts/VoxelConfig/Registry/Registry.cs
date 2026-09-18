using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Utils.Logger;

namespace Engine.Scripts.VoxelConfig.Registry{
    public class Registry<T> : IRegistry<T>{
        protected readonly Dictionary<T, ushort> ForwardMap;
        private readonly Dictionary<ushort, T> _backwardMap;
        private bool _isFull;

        public Registry(int initCapacity){
            ForwardMap = new Dictionary<T, ushort>(initCapacity);
            _backwardMap = new Dictionary<ushort, T>(initCapacity);
        }

        public int Count => ForwardMap.Count;

        public virtual ushort Register(T item){
            if (_isFull)
                throw new InvalidOperationException("Registry is full (65535). Cannot register more items.");

            if (ForwardMap.TryGetValue(item, out ushort id)) return id;

            id = (ushort)Count;
            ForwardMap.Add(item, id);
            _backwardMap.Add(id, item);
            if (id == ushort.MaxValue) _isFull = true;
            PostRegister(id, item);
            return id;
        }

        public bool TryGetId(T item, out ushort id){
            return ForwardMap.TryGetValue(item, out id);
        }

        public bool TryGet(ushort id, out T item){
            return _backwardMap.TryGetValue(id, out item);
        }

        protected virtual void PostRegister(ushort id, T item){ }

        public bool Register(ushort id, T item){
            if (ForwardMap.TryGetValue(item, out ushort existingId)){
                VoxelEngineLogger.Warn<Registry<T>>($"Register: ID {id} already exists. Ignoring.");
                return false;
            }

            ForwardMap.Add(item, id);
            _backwardMap.Add(id, item);
            return true;
        }

        public List<KeyValuePair<ushort, T>> GetAllEntries(){
            return _backwardMap.ToList();
        }
    }

    public interface IRegistry<T>{
        ushort Register(T item);
        bool TryGetId(T item, out ushort id);
        bool TryGet(ushort id, out T item);
    }

    public interface IResourceRegistry<T> : IRegistry<T>{
        void PrepareArray();
    }
}
