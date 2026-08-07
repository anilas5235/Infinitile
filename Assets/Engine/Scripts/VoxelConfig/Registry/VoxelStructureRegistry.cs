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

        public VoxelStructure.VoxelStructureDef[] StructureArray { get; private set; }

        public void Register(VoxelStructure structure)
        {
            if (!structure)
                throw new ArgumentNullException(nameof(structure), "Cannot register a null structure definition.");
            FixedString32Bytes structureName;

            try
            {
                structureName = new FixedString32Bytes(structure.name);
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

        public void PrepareArray()
        {
            if (_nameRegistry.Count == 0)
            {
                StructureArray = Array.Empty<VoxelStructure.VoxelStructureDef>();
                return;
            }

            StructureArray = new VoxelStructure.VoxelStructureDef[_nameRegistry.Count];
            int index = 0;
            foreach (KeyValuePair<ushort, VoxelStructure> kvp in _structureRegistry.GetAllEntries())
            {
                VoxelEngineLogger.Info<VoxelStructureRegistry>($"copy structure {kvp.Value.name} to Structure array");
                StructureArray[index] = kvp.Value.ToStruct();
                index++;
            }
        }

        public void Dispose()
        {
        }
    }
}