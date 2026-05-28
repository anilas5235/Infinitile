namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Holds voxel IDs and global parameters used by chunk generation jobs to create terrain,
    ///     caves, ores, vegetation and structures.
    /// </summary>
    public struct GeneratorConfig
    {
        /// <summary>
        ///     Vertical world Y level that represents the global water surface used for oceans, lakes and rivers.
        /// </summary>
        public int WaterLevel;

        /// <summary>
        ///     Global deterministic seed passed to generation jobs to keep world generation reproducible.
        /// </summary>
        public int GlobalSeed;

        /// <summary>Voxel ID for generic grey brick blocks.</summary>
        public ushort BrickGrey;

        /// <summary>Voxel ID for generic red brick blocks.</summary>
        public ushort BrickRed;

        /// <summary>Voxel ID for cactus blocks used in desert biomes.</summary>
        public ushort Cactus;

        /// <summary>Voxel ID for blue cotton decorative blocks.</summary>
        public ushort CottonBlue;

        /// <summary>Voxel ID for green cotton decorative blocks.</summary>
        public ushort CottonGreen;

        /// <summary>Voxel ID for red cotton decorative blocks.</summary>
        public ushort CottonRed;

        /// <summary>Voxel ID for tan cotton decorative blocks.</summary>
        public ushort CottonTan;

        /// <summary>Voxel ID for generic dirt blocks.</summary>
        public ushort Dirt;

        /// <summary>Voxel ID for dirt mixed with gravel.</summary>
        public ushort DirtGravel;

        /// <summary>Voxel ID for sandy dirt blocks.</summary>
        public ushort DirtSandy;

        /// <summary>Voxel ID for snow-covered dirt blocks.</summary>
        public ushort DirtSnowy;

        /// <summary>Voxel ID for generic flower blocks.</summary>
        public ushort Flowers;

        /// <summary>Voxel ID for glass blocks.</summary>
        public ushort Glass;

        /// <summary>Voxel ID for grass-covered dirt blocks.</summary>
        public ushort Grass;

        /// <summary>Voxel ID for tall grass blocks.</summary>
        public ushort GrassF;

        /// <summary>Voxel ID for dead grass blocks.</summary>
        public ushort GrassFDead;

        /// <summary>Voxel ID for dry grass blocks.</summary>
        public ushort GrassFDry;

        /// <summary>Voxel ID for greystone blocks containing ruby ore.</summary>
        public ushort GreystoneRubyOre;

        /// <summary>Voxel ID for ice blocks.</summary>
        public ushort Ice;

        /// <summary>Voxel ID for lava blocks used below lava level.</summary>
        public ushort Lava;

        /// <summary>Voxel ID for generic leaf blocks.</summary>
        public ushort Leaves;

        /// <summary>Voxel ID for orange-tinted leaf blocks (e.g. birch autumn leaves).</summary>
        public ushort LeavesOrange;

        /// <summary>Voxel ID for birch log blocks.</summary>
        public ushort LogBirch;

        /// <summary>Voxel ID for oak log blocks.</summary>
        public ushort LogOak;

        /// <summary>Voxel ID for brown mushroom blocks.</summary>
        public ushort MushroomBrown;

        /// <summary>Voxel ID for red mushroom blocks.</summary>
        public ushort MushroomRed;

        /// <summary>Voxel ID for tan mushroom blocks.</summary>
        public ushort MushroomTan;

        /// <summary>Voxel ID for oven or furnace blocks.</summary>
        public ushort Oven;

        /// <summary>Voxel ID for generic wooden plank blocks.</summary>
        public ushort Planks;

        /// <summary>Voxel ID for red wooden plank blocks.</summary>
        public ushort PlanksRed;

        /// <summary>Voxel ID for generic rock blocks.</summary>
        public ushort Rock;

        /// <summary>Voxel ID for mossy rock blocks.</summary>
        public ushort RockMossy;

        /// <summary>Voxel ID for sand blocks.</summary>
        public ushort Sand;

        /// <summary>Voxel ID for grey sand blocks.</summary>
        public ushort SandGrey;

        /// <summary>Voxel ID for red sand blocks.</summary>
        public ushort SandRed;

        /// <summary>Voxel ID for red sandstone blocks.</summary>
        public ushort SandStoneRed;

        /// <summary>Voxel ID for elm-colored red sandstone blocks.</summary>
        public ushort SandStoneRedElm;

        /// <summary>Voxel ID for sandy red sandstone blocks.</summary>
        public ushort SandStoneRedSandy;

        /// <summary>Voxel ID for generic stone blocks.</summary>
        public ushort Stone;

        /// <summary>Voxel ID for stone blocks containing coal ore.</summary>
        public ushort StoneCoalOre;

        /// <summary>Voxel ID for stone blocks containing diamond ore.</summary>
        public ushort StoneDiamondOre;

        /// <summary>Voxel ID for stone blocks containing gold ore.</summary>
        public ushort StoneGoldOre;

        /// <summary>Voxel ID for stone blocks with grassy top.</summary>
        public ushort StoneGrassy;

        /// <summary>Voxel ID for stone blocks mixed with gravel.</summary>
        public ushort StoneGravel;

        /// <summary>Voxel ID for grey stone blocks.</summary>
        public ushort StoneGrey;

        /// <summary>Voxel ID for grey sandy stone blocks.</summary>
        public ushort StoneGreySandy;

        /// <summary>Voxel ID for stone blocks containing brown iron ore.</summary>
        public ushort StoneIronBrownOre;

        /// <summary>Voxel ID for stone blocks containing green iron ore.</summary>
        public ushort StoneIronGreenOre;

        /// <summary>Voxel ID for sandy stone blocks.</summary>
        public ushort StoneSandy;

        /// <summary>Voxel ID for stone blocks containing silver ore.</summary>
        public ushort StoneSilverOre;

        /// <summary>Voxel ID for snow-covered stone blocks.</summary>
        public ushort StoneSnowy;

        /// <summary>Voxel ID for snow blocks.</summary>
        public ushort Snow;

        /// <summary>Voxel ID for water blocks.</summary>
        public ushort Water;

        /// <summary>Voxel ID for wheat crop in growth stage 1.</summary>
        public ushort WheatStage1;

        /// <summary>Voxel ID for wheat crop in growth stage 2.</summary>
        public ushort WheatStage2;

        /// <summary>Voxel ID for wheat crop in growth stage 3.</summary>
        public ushort WheatStage3;

        /// <summary>Voxel ID for wheat crop in growth stage 4 (mature).</summary>
        public ushort WheatStage4;

        /// <summary>Voxel ID for crafting workbench blocks.</summary>
        public ushort Workbench;
    }
}