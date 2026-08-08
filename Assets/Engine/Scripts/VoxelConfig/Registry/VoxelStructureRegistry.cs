using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;

namespace Engine.Scripts.VoxelConfig.Registry
{
    public class VoxelStructureRegistry : IDisposable
    {
        private readonly Registry<FixedString32Bytes> _nameRegistry = new(16);
        private readonly Registry<VoxelStructure> _structureRegistry = new(16);

        public NativeArray<VoxelStructure.VoxelStructureDef> StructureArray { get; private set; }

        public void Register(FixedString32Bytes packagePrefix, VoxelStructure structure)
        {
            if (!structure)
                throw new ArgumentNullException(nameof(structure), "Cannot register a null structure definition.");
            FixedString32Bytes structureName;

            try
            {
                structureName = new FixedString32Bytes(packagePrefix + ":" + structure.name);
            }
            catch (ArgumentException e)
            {
                VoxelEngineLogger.Error<VoxelRegistry>(
                    $"Voxel name '{structure.name}' exceeds the maximum length of {FixedString32Bytes.UTF8MaxLengthInBytes} bytes. Registration skipped.");
                return;
            }

            _nameRegistry.Register(structureName);
            _structureRegistry.Register(structure);
        }

        public void PrepareArray(VoxelRegistry voxelRegistry)
        {
            if (_nameRegistry.Count == 0)
            {
                StructureArray = new NativeArray<VoxelStructure.VoxelStructureDef>(0, Allocator.Domain);
                return;
            }

            VoxelStructure.VoxelStructureDef[] tempArray = new VoxelStructure.VoxelStructureDef[_nameRegistry.Count];
            int index = 0;
            foreach (KeyValuePair<ushort, VoxelStructure> kvp in _structureRegistry.GetAllEntries())
            {
                VoxelEngineLogger.Info<VoxelStructureRegistry>($"copy structure {kvp.Value.name} to Structure array");
                VoxelStructure.VoxelStructureDef def;
                try
                {
                    def = kvp.Value.ToStruct(voxelRegistry);
                }
                catch (Exception e)
                {
                    VoxelEngineLogger.Error<VoxelStructureRegistry>(
                        $"Failed to convert structure {kvp.Value.name} to struct: {e.Message}. Skipping this structure.");
                    continue;
                }
                tempArray[index] = def;
                index++;
            }

            StructureArray = new NativeArray<VoxelStructure.VoxelStructureDef>(tempArray, Allocator.Domain);
        }

        public void Dispose()
        {
            foreach (VoxelStructure.VoxelStructureDef def in StructureArray) def.Dispose();
            StructureArray.Dispose();
        }
    }
}