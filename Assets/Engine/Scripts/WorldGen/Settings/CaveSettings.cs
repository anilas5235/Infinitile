using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using UnityEngine;
using Voxel = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Caves, lava, ores and the bedrock floor: 3D passes that run after the terrain fill and carve or replace the
    ///     stone below the surface. The surface itself (height, biome, top blocks, flora pick) does not change. The same
    ///     values apply everywhere; there is no per-biome factor.
    /// </summary>
    [Serializable]
    public class CaveSettings{
        [Tooltip("Run the cave, lava, ore and bedrock passes.")]
        public bool Enabled = true;

        [Header("Cover")]
        [Tooltip("Caverns, and worms outside an entrance, carve nothing closer than this many blocks below the surface.")]
        [Min(0)]
        public int Cover = 8;

        [Tooltip("Next to submerged columns, carving stays this many blocks below the lowest surface of the column's 3×3 "
                 + "neighbourhood, so no carved voxel touches water.")]
        [Min(1)]
        public int WaterCover = 6;

        [Tooltip("No entrance opens within this many blocks of a submerged column; probed on a lattice every 4 blocks.")]
        [Min(0)]
        public int EntranceWaterDistance = 16;

        [Header("Caverns")]
        [Tooltip("Horizontal wavelength of the cavern noise in blocks.")]
        public float CavernWavelength = 96f;

        [Tooltip("Vertical wavelength of the cavern noise in blocks.")]
        public float CavernVerticalWavelength = 48f;

        [Tooltip("Cavern noise value (−1…1) above which a hall is carved, in the middle of the band.")]
        [Range(-1f, 1f)]
        public float CavernThreshold = .45f;

        [Tooltip("How far the threshold rises towards the top and bottom of the band, so halls close off there.")]
        [Min(0f)]
        public float CavernEdgeRise = .6f;

        [Tooltip("Lowest y a hall may carve.")]
        public int CavernBandMin = 20;

        [Tooltip("Highest y a hall may carve.")]
        public int CavernBandMax = 90;

        [Tooltip("Wavelength of the 2D cavern mask in blocks.")]
        public float CavernMaskWavelength = 384f;

        [Tooltip("Cavern mask value (−1…1) above which halls may form; tune it towards about 15 % of the area.")]
        [Range(-1f, 1f)]
        public float CavernMaskThreshold = .35f;

        [Tooltip("Horizontal wavelength of the pillar noise in blocks.")]
        public float PillarWavelength = 12f;

        [Tooltip("Vertical wavelength of the pillar noise in blocks; much longer than the horizontal one, so pillars stand upright.")]
        public float PillarVerticalWavelength = 96f;

        [Tooltip("Pillar noise value (−1…1) above which stone stays standing inside a hall.")]
        [Range(-1f, 1f)]
        public float PillarThreshold = .55f;

        [Header("Worms")]
        [Tooltip("Edge of a worm start cell in blocks.")]
        [Min(1)]
        public int WormCellSize = 64;

        [Tooltip("Mean number of worms per start cell (0…2): each cell holds 0, 1 or 2 worms.")]
        [Range(0f, 2f)]
        public float WormsPerCell = .6f;

        [Tooltip("Lowest start y of a worm away from an entrance.")]
        public int WormStartMinY = 20;

        [Tooltip("Steps of a worm, 1 block each.")]
        public IntRange WormLength = new(96, 224);

        [Tooltip("Smallest radius of a worm in blocks.")]
        [Min(.5f)]
        public float WormMinRadius = 1.5f;

        [Tooltip("Largest radius of a worm in blocks.")]
        [Min(.5f)]
        public float WormMaxRadius = 4f;

        [Tooltip("Steps over which the radius changes from one value to the next.")]
        [Min(1)]
        public int WormRadiusPeriod = 24;

        [Tooltip("Largest horizontal distance in blocks a worm and its branch walk from the worm's start.")]
        [Min(1)]
        public int WormReach = 128;

        [Tooltip("Largest pitch of a worm in degrees, up or down.")]
        [Range(0f, 89f)]
        public float WormMaxPitch = 35f;

        [Tooltip("Random turn added to the yaw rate per step, in degrees.")]
        [Min(0f)]
        public float WormYawJitter = 6f;

        [Tooltip("Random change added to the pitch rate per step, in degrees.")]
        [Min(0f)]
        public float WormPitchJitter = 4f;

        [Tooltip("Share of the yaw and pitch rates kept from one step to the next (0…1); higher values give longer bends.")]
        [Range(0f, 1f)]
        public float WormTurnDamping = .8f;

        [Tooltip("Chance that a worm branches once; the branch gets half the remaining length.")]
        [Range(0f, 1f)]
        public float WormBranchChance = .25f;

        [Tooltip("Lowest y a worm may carve; the floor below it stays solid.")]
        [Min(3)]
        public int WormMinY = 3;

        [Header("Entrances")]
        [Tooltip("Wavelength of the 2D entrance mask in blocks.")]
        public float EntranceWavelength = 96f;

        [Tooltip("Entrance mask value (−1…1) above which worms may break the surface; tune it towards about 3 % of the land.")]
        [Range(-1f, 1f)]
        public float EntranceThreshold = .7f;

        [Tooltip("Worms that start inside the entrance mask start this many blocks below the surface at most.")]
        [Min(0)]
        public int EntranceStartDepth = 4;

        [Header("Lava")]
        [Tooltip("Voxel of the lava pockets and of the cave floor below the lava level.")]
        public Voxel LavaVoxel;

        [Tooltip("Carved voxels at or below this y become lava inside the lava mask.")]
        public int LavaLevel = 5;

        [Tooltip("Wavelength of the 2D lava mask in blocks.")]
        public float LavaMaskWavelength = 256f;

        [Tooltip("Lava mask value (−1…1) above which lava forms; tune it towards about 40 % of the area.")]
        [Range(-1f, 1f)]
        public float LavaMaskThreshold = -.15f;

        [Tooltip("Horizontal wavelength of the lava pocket noise in blocks.")]
        public float LavaPocketWavelength = 32f;

        [Tooltip("Vertical wavelength of the lava pocket noise in blocks; much shorter than the horizontal one, so pockets are flat.")]
        public float LavaPocketVerticalWavelength = 8f;

        [Tooltip("Pocket noise value (−1…1) above which a pocket is carved, in the middle of the band.")]
        [Range(-1f, 1f)]
        public float LavaPocketThreshold = .35f;

        [Tooltip("How far the pocket threshold rises towards the top and bottom of the band.")]
        [Min(0f)]
        public float LavaPocketEdgeRise = .6f;

        [Tooltip("Lowest y a lava pocket may carve.")]
        [Min(3)]
        public int LavaPocketBandMin = 3;

        [Tooltip("Highest y a lava pocket may carve.")]
        public int LavaPocketBandMax = 10;

        [Header("Ores")]
        [Tooltip("Ore veins: each rule replaces only its host voxel, within its y range.")]
        public List<OreRule> Ores = new();

        [Header("Bedrock")]
        [Tooltip("Voxel of the bottom layer: all of y 0 and part of y 1 and y 2.")]
        public Voxel BedrockVoxel;

        [Tooltip("Share of y 1 that is bedrock.")]
        [Range(0f, 1f)]
        public float BedrockChance1 = .5f;

        [Tooltip("Share of y 2 that is bedrock.")]
        [Range(0f, 1f)]
        public float BedrockChance2 = .25f;

        /// <summary>
        ///     Every wavelength (in blocks) the cave passes divide by that is not positive and finite. Reported rather
        ///     than clamped, like <c>WorldGenSettings.InvalidScales</c>.
        /// </summary>
        internal IEnumerable<(string field, float value)> InvalidScales(){
            foreach ((string field, float value) in Wavelengths())
                if (!(value > 0f) || float.IsPositiveInfinity(value))
                    yield return (field, value);
        }

        private IEnumerable<(string field, float value)> Wavelengths(){
            yield return (nameof(CavernWavelength), CavernWavelength);
            yield return (nameof(CavernVerticalWavelength), CavernVerticalWavelength);
            yield return (nameof(CavernMaskWavelength), CavernMaskWavelength);
            yield return (nameof(PillarWavelength), PillarWavelength);
            yield return (nameof(PillarVerticalWavelength), PillarVerticalWavelength);
            yield return (nameof(EntranceWavelength), EntranceWavelength);
            yield return (nameof(LavaMaskWavelength), LavaMaskWavelength);
            yield return (nameof(LavaPocketWavelength), LavaPocketWavelength);
            yield return (nameof(LavaPocketVerticalWavelength), LavaPocketVerticalWavelength);
        }

        /// <summary>Values that break the passes (empty ranges, a radius range upside down), as (field, problem).</summary>
        internal IEnumerable<(string field, string problem)> InvalidRanges(){
            if (WormCellSize < 1) yield return (nameof(WormCellSize), $"must be at least 1, got {WormCellSize}");
            if (WormLength.Min < 1 || WormLength.Max < WormLength.Min)
                yield return (nameof(WormLength), $"must be 1 ≤ min ≤ max, got {WormLength.Min}…{WormLength.Max}");
            if (!(WormMinRadius > 0f) || WormMaxRadius < WormMinRadius)
                yield return (nameof(WormMinRadius), $"must be 0 < min ≤ max, got {WormMinRadius}…{WormMaxRadius}");
            if (WormRadiusPeriod < 1) yield return (nameof(WormRadiusPeriod), $"must be at least 1, got {WormRadiusPeriod}");
            if (WormReach < 1) yield return (nameof(WormReach), $"must be at least 1, got {WormReach}");
            if (Ores == null) yield break;
            for (int i = 0; i < Ores.Count; i++){
                OreRule rule = Ores[i];
                if (rule == null) yield return ($"{nameof(Ores)}[{i}]", "is empty");
                else if (rule.VeinSize.Min < 1 || rule.VeinSize.Max < rule.VeinSize.Min)
                    yield return ($"{nameof(Ores)}[{i}].{nameof(OreRule.VeinSize)}",
                        $"must be 1 ≤ min ≤ max, got {rule.VeinSize.Min}…{rule.VeinSize.Max}");
            }
        }
    }

    /// <summary>One ore: its veins replace the host voxel within the y range.</summary>
    [Serializable]
    public class OreRule{
        [Tooltip("Voxel of the ore.")]
        public Voxel Ore;

        [Tooltip("The only voxel a vein replaces.")]
        public Voxel Host;

        [Tooltip("Lowest y of an ore voxel.")]
        public int MinY;

        [Tooltip("Highest y of an ore voxel.")]
        public int MaxY = 255;

        [Tooltip("Mean number of veins per 16³ cell; a fraction gives a vein in that share of the cells.")]
        [Min(0f)]
        public float VeinsPerCell = 1f;

        [Tooltip("Voxels per vein.")]
        public IntRange VeinSize = new(4, 8);
    }
}
