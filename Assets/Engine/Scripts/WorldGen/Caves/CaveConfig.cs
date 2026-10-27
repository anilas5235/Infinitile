using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     Burst-side mirror of <c>CaveSettings</c>, part of <c>WorldGenConfig</c>. Built by the config builder; the
    ///     ore rules are owned and released by <see cref="Dispose" />.
    /// </summary>
    public struct CaveConfig : IDisposable{
        /// <summary>Run the cave, lava, ore and bedrock passes.</summary>
        [MarshalAs(UnmanagedType.U1)] public bool Enabled;

        // --- Cover --------------------------------------------------------------------------------------------------
        public int Cover;

        // --- Caverns ------------------------------------------------------------------------------------------------
        /// <summary>Wavelengths of the cavern noise in blocks: (horizontal, vertical).</summary>
        public float2 CavernWavelength;

        public float CavernThreshold;
        public float CavernEdgeRise;
        public float CavernFloorRise;
        public int CavernBandMin;
        public int CavernBandMax;
        public float CavernMaskWavelength;
        public float CavernMaskThreshold;

        /// <summary>Wavelengths of the pillar noise in blocks: (horizontal, vertical).</summary>
        public float2 PillarWavelength;

        public float PillarThreshold;

        // --- Worms --------------------------------------------------------------------------------------------------
        public int WormCellSize;
        public float WormsPerCell;
        public int WormStartMinY;
        public int2 WormLength;
        public float WormMinRadius;
        public float WormMaxRadius;
        public int WormRadiusPeriod;
        public int WormReach;

        /// <summary>Largest pitch in radians.</summary>
        public float WormMaxPitch;

        /// <summary>Yaw and pitch jitter per step in radians.</summary>
        public float WormYawJitter;

        public float WormPitchJitter;
        public float WormTurnDamping;
        public float WormBranchChance;
        public int WormMinY;

        /// <summary>Share of the turn towards its target a connector worm takes per step, 0…1.</summary>
        public float WormSteer;

        /// <summary>Every random worm sends a connector worm to the nearest other random worm.</summary>
        [MarshalAs(UnmanagedType.U1)] public bool WormLinks;

        /// <summary>Every random worm sends a connector worm to the nearest region of the cavern mask.</summary>
        [MarshalAs(UnmanagedType.U1)] public bool WormHallLinks;

        // --- Entrances ----------------------------------------------------------------------------------------------
        public float EntranceWavelength;
        public float EntranceThreshold;
        public int EntranceStartDepth;

        // --- Lava ---------------------------------------------------------------------------------------------------
        public ushort LavaVoxel;
        public int LavaLevel;
        public float LavaMaskWavelength;
        public float LavaMaskThreshold;

        /// <summary>Wavelengths of the lava pocket noise in blocks: (horizontal, vertical).</summary>
        public float2 LavaPocketWavelength;

        public float LavaPocketThreshold;
        public float LavaPocketEdgeRise;
        public int LavaPocketBandMin;
        public int LavaPocketBandMax;

        // --- Ores and bedrock ---------------------------------------------------------------------------------------
        /// <summary>Ore rules in settings order; rules without an ore or host voxel are left out. Owned.</summary>
        [ReadOnly] public NativeArray<OreRuleDef> Ores;

        public ushort BedrockVoxel;
        public float BedrockChance1;
        public float BedrockChance2;

        /// <summary>
        ///     Columns beyond a block a worm can carve into it from: the reach of its path plus its largest radius.
        ///     A chunk enumerates the worm start cells within this distance of its border.
        /// </summary>
        public int WormPadding => WormReach + (int)math.ceil(WormMaxRadius);

        public void Dispose(){
            if (Ores.IsCreated) Ores.Dispose();
        }
    }

    /// <summary>Burst mirror of one ore rule.</summary>
    public struct OreRuleDef{
        public ushort Ore;
        public ushort Host;
        public int MinY;
        public int MaxY;
        public float VeinsPerCell;
        public int2 VeinSize;
    }
}
