using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry{
    /// <summary>
    ///     Every biome of the loaded packages, by id. Generation does not read this array: it converts the biomes of
    ///     the active <see cref="WorldGen.Settings.BiomeSet" /> itself and rejects a broken one there. A package may
    ///     keep inactive biomes (e.g. Jungle, Swamp), so a biome that fails to convert is logged and skipped instead
    ///     of failing the whole import.
    /// </summary>
    public class BiomeRegistry : TopLevelRegistry<Biome>{
        private readonly HashSet<ushort> _broken = new();

        public NativeArray<Biome.BiomeDef> BiomeArray{ get; private set; }

        /// <summary>True if the biome at <paramref name="id" /> failed to convert; its array entry is empty.</summary>
        public bool IsBroken(ushort id){
            return _broken.Contains(id);
        }

        public void Initialize(){
            InternalInitialize();
        }

        public void FinalizeRegistry(VoxelRegistry voxelRegistry){
            InternalFinalize();

            if (NameRegistry.Count == 0){
                BiomeArray = new NativeArray<Biome.BiomeDef>(0, Allocator.Domain);
                return;
            }

            Biome.BiomeDef[] tempArray = new Biome.BiomeDef[NameRegistry.Count];
            foreach (KeyValuePair<ushort, Biome> kvp in SoRegistry.GetAllEntries()){
                VoxelEngineLogger.Info<BiomeRegistry>($"copy biome {kvp.Value.name} to Biome array");
                try{
                    tempArray[kvp.Key] = kvp.Value.ToStruct(voxelRegistry); // place by id, not by enumeration order
                }
                catch (Exception e){
                    // The slot stays empty and the ids of the other biomes stay as they are.
                    _broken.Add(kvp.Key);
                    VoxelEngineLogger.Error<BiomeRegistry>(
                        $"Skipping biome {kvp.Value.name}, it failed to convert: {e.Message}. " +
                        "World generation rejects it if the active BiomeSet uses it.");
                }
            }

            BiomeArray = new NativeArray<Biome.BiomeDef>(tempArray, Allocator.Domain);
        }

        public override void Dispose(){
            _broken.Clear();
            if (!BiomeArray.IsCreated) return; // never finalised
            foreach (Biome.BiomeDef def in BiomeArray) def.Dispose();
            BiomeArray.Dispose();
        }
    }
}
