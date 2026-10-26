using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Data;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Turned voxel variants: base ids stay as they were, every orientation gets its own id after all bases, the
    ///     lookups round-trip, variants copy their base's values, and the turned shapes and textures are correct.
    /// </summary>
    public class VoxelVariantTests{
        private const string ShapePath = "VoxelDataPackages/Standard/Shapes/";
        private RegistryTestFixtures _fixtures;
        private readonly List<IDisposable> _disposables = new();
        private VoxelDataPackage _package;
        private Texture2D _tex;

        [SetUp]
        public void SetUp(){
            _fixtures = new RegistryTestFixtures();
            _package = _fixtures.CreatePackage(RegistryTestFixtures.Prefix);
            _tex = _fixtures.CreateTexture("Tex", new Color32(10, 20, 30, 255));
        }

        [TearDown]
        public void TearDown(){
            foreach (IDisposable disposable in _disposables) disposable.Dispose();
            _disposables.Clear();
            _fixtures.DestroyAll();
        }

        private static VoxelShape Shape(string path){
            VoxelShape shape = Resources.Load<VoxelShape>(ShapePath + path);
            Assert.IsNotNull(shape, path);
            return shape;
        }

        private Voxel CreateVoxel(string name, VoxelShape shape, VoxelFacing facing, bool topBottom){
            Voxel voxel = _fixtures.CreateVoxel(name, _package, shape, _tex);
            voxel.facing = facing;
            voxel.topBottom = topBottom;
            return voxel;
        }

        private VoxelRegistry Registry(params Voxel[] voxels){
            VoxelRegistry registry = _fixtures.CreateVoxelRegistry(voxels);
            _disposables.Add(registry);
            return registry;
        }

        /// <summary>A rotatable voxel of every mode and toggle combination, then a plain one, on the cube shape.</summary>
        private (VoxelRegistry registry, Voxel[] voxels) EveryMode(VoxelShape shape){
            List<Voxel> voxels = new();
            foreach (VoxelFacing facing in Enum.GetValues(typeof(VoxelFacing)))
            foreach (bool topBottom in new[]{ false, true })
                voxels.Add(CreateVoxel($"{facing}{(topBottom ? "TB" : "")}", shape, facing, topBottom));
            voxels.Add(CreateVoxel("Plain", shape, VoxelFacing.None, false));
            return (Registry(voxels.ToArray()), voxels.ToArray());
        }

        private static ushort Id(VoxelRegistry registry, Voxel voxel){
            return registry.GetIdOrThrow(voxel.GetFullName());
        }

        /// <summary>Every orientation byte a voxel has, base first, in variant order.</summary>
        private static IEnumerable<byte> Orientations(Voxel voxel){
            int count = VoxelOrientation.Count(voxel.facing, voxel.topBottom);
            for (int index = 0; index < count; index++) yield return VoxelOrientation.AtIndex(voxel.topBottom, index);
        }

        /// <summary>Quads and texture ids of one face range of a render definition.</summary>
        private static List<(QuadDefinition.QuadDef quad, ushort tex)> Quads(VoxelRegistry registry, uint2 range){
            List<(QuadDefinition.QuadDef, ushort)> quads = new();
            for (uint i = range.x; i < range.x + range.y; i++){
                uint pair = registry.QuadTexPairs[(int)i];
                quads.Add((registry.QuadArray[pair & 0xFFFF], (ushort)(pair >> 16)));
            }

            return quads;
        }

        private static IEnumerable<(QuadDrawCondition condition, uint2 range)> Ranges(Voxel.VoxelDef def){
            yield return (QuadDrawCondition.Always, def.Always);
            yield return (QuadDrawCondition.Right, def.Right);
            yield return (QuadDrawCondition.Left, def.Left);
            yield return (QuadDrawCondition.Up, def.Up);
            yield return (QuadDrawCondition.Down, def.Down);
            yield return (QuadDrawCondition.Forward, def.Front);
            yield return (QuadDrawCondition.Backward, def.Back);
        }

        private static float3[] Corners(QuadDefinition.QuadDef quad){
            return new[]{ quad.position00, quad.position01, quad.position02, quad.position03 };
        }

        #region Registry

        /// <summary>Name → id of every Standard voxel, read from the package before turned variants existed.</summary>
        private static readonly string[] StandardOrder = {
            "Stone", "Dirt", "Grass", "Water", "GrassF", "CottonBlue", "CottonGreen", "CottonRed", "CottonTan",
            "DirtGravel", "DirtSandy", "DirtSnowy", "BrickGrey", "BrickRed", "Cactus", "Glass", "GrassFDead",
            "GrassFDry", "GreystoneRubyOre", "Ice", "Lava", "Leaves", "LeavesOrange", "LogBirch", "LogOak",
            "MushroomBrown", "MushroomRed", "MushroomTan", "Oven", "Planks", "PlanksRed", "Rock", "RockMossy", "Sand",
            "SandGrey", "SandRed", "SandStoneRed", "SandStoneRedEmeraldOre", "SandStoneRedSandy", "StoneCoalOre",
            "StoneDiamondOre", "StoneGoldOre", "StoneGrassy", "StoneGravel", "StoneGrey", "StoneGreySandy",
            "StoneIronBrownOre", "StoneIronGreenOre", "StoneSandy", "StoneSilverOre", "StoneSnowy", "WheatStage1",
            "WheatStage2", "WheatStage3", "WheatStage4", "Workbench", "Flowers", "Snow", "StoneSlab",
            "LeavesSpruce", "LogSpruce", "DeadBush"
        };

        [Test]
        public void StandardPackage_BaseIdsMatchSnapshot(){
            VoxelRegistry registry = StandardRegistry(out Voxel[] voxels);
            Assert.AreEqual(0, registry.GetIdOrThrow("std:air"));
            for (int i = 0; i < StandardOrder.Length; i++)
                Assert.AreEqual(i + 1, registry.GetIdOrThrow("std:" + StandardOrder[i]), StandardOrder[i]);
            // Voxels appended to the package later take the next base ids, before every variant
            Assert.AreEqual(voxels.Length + 1, registry.BaseCount);
            AssertLookupsRoundTrip(registry);
        }

        private VoxelRegistry StandardRegistry(out Voxel[] voxels){
            VoxelDataPackage package = Resources.Load<VoxelDataPackage>("VoxelDataPackages/Standard/Standart");
            Assert.IsNotNull(package);
            voxels = package.voxel.Where(v => v).ToArray();
            VoxelRegistry registry = new();
            _disposables.Add(registry);
            registry.Initialize(Resources.Load<Texture2D>("Artwork/TexError"), Resources.Load<Texture2D>("Artwork/TexErrorT"),
                Resources.Load<Texture2D>("Artwork/TexErrorT"));
            foreach (Voxel voxel in voxels) registry.Register(package.packagePrefix, voxel);
            registry.FinalizeRegistry();
            return registry;
        }

        [Test]
        public void EveryMode_HasFacingsTimesHalvesIds_AfterAllBases(){
            (VoxelRegistry registry, Voxel[] voxels) = EveryMode(Shape("Cube/Cube"));
            int expected = 1 + voxels.Length;
            Assert.AreEqual(expected, registry.BaseCount, "air + one base per voxel");

            int[] facings = { 1, 3, 4, 6 };
            int next = registry.BaseCount;
            foreach (Voxel voxel in voxels){
                ushort baseId = Id(registry, voxel);
                Assert.Less(baseId, registry.BaseCount);
                int count = facings[(int)voxel.facing] * (voxel.topBottom ? 2 : 1);
                Assert.AreEqual(count, VoxelOrientation.Count(voxel.facing, voxel.topBottom), voxel.name);

                List<byte> orientations = Orientations(voxel).ToList();
                Assert.AreEqual(count, orientations.Distinct().Count(), voxel.name);
                Assert.AreEqual(baseId, registry.VariantOf(baseId, 0), voxel.name);
                // Variants follow in base order, then facing index, then top/bottom
                foreach (byte orientation in orientations.Skip(1))
                    Assert.AreEqual(next++, registry.VariantOf(baseId, orientation), $"{voxel.name} {orientation}");
                expected += count - 1;
            }

            Assert.AreEqual(expected, registry.GetAllEntries().Count);
            Assert.AreEqual(expected, registry.GetVoxelGenData().VoxelRenderDefs.Length);
            Assert.AreEqual(expected, registry.GetLightTable().Count);
            AssertLookupsRoundTrip(registry);
        }

        private static void AssertLookupsRoundTrip(VoxelRegistry registry){
            int count = registry.GetAllEntries().Count;
            VoxelVariantTable table = registry.GetVariantTable();
            Assert.AreEqual(count, table.Count);
            for (ushort id = 0; id < count; id++){
                ushort baseId = registry.BaseOf(id);
                byte orientation = registry.OrientationOf(id);
                Assert.AreEqual(id, registry.VariantOf(baseId, orientation), $"id {id}");
                Assert.AreEqual(baseId, table.BaseOf(id));
                Assert.AreEqual(orientation, table.OrientationOf(id));
                if (id < registry.BaseCount){
                    Assert.AreEqual(id, baseId, $"base {id}");
                    Assert.AreEqual(0, orientation, $"base {id}");
                }
                else Assert.Less(baseId, registry.BaseCount, $"variant {id}");
            }
        }

        [Test]
        public void VariantOf_UnknownOrientation_GivesTheBase(){
            Voxel plain = CreateVoxel("Plain", Shape("Cube/Cube"), VoxelFacing.None, false);
            Voxel log = CreateVoxel("Log", Shape("Cube/Cube"), VoxelFacing.Axis, false);
            VoxelRegistry registry = Registry(plain, log);
            ushort plainId = Id(registry, plain), logId = Id(registry, log);
            Assert.AreEqual(plainId, registry.VariantOf(plainId, VoxelOrientation.AxisX));
            Assert.AreEqual(logId, registry.VariantOf(logId, VoxelOrientation.UpsideDown), "no top/bottom");
            Assert.AreEqual(logId, registry.VariantOf(logId, 5), "Axis has three facings");
            Assert.AreNotEqual(logId, registry.VariantOf(logId, VoxelOrientation.AxisX));
        }

        [Test]
        public void Variants_ShareTheirBaseValues_AndResolveToTheBaseAsset(){
            Voxel lamp = CreateVoxel("Lamp", Shape("Cube/Cube"), VoxelFacing.Full, true);
            lamp.lightAttenuation = 3;
            lamp.lightEmission = new Vector3Int(15, 9, 3);
            lamp.glow = 77;
            lamp.collision = false;
            Voxel glass = _fixtures.CreateVoxel("Pane", _package, Shape("Slab/Half"), _tex, MeshLayer.Transparent);
            glass.facing = VoxelFacing.Horizontal;
            glass.topBottom = true;
            glass.surfaceAlpha = 0.5f;
            glass.depthFadeDistance = 4f;
            VoxelRegistry registry = Registry(lamp, glass);

            VoxelLightTable light = registry.GetLightTable();
            foreach (Voxel voxel in new[]{ lamp, glass }){
                ushort baseId = Id(registry, voxel);
                Voxel.VoxelDef baseDef = registry.GetVoxelGenData().GetRenderDef(baseId);
                foreach (byte orientation in Orientations(voxel).Skip(1)){
                    ushort id = registry.VariantOf(baseId, orientation);
                    Voxel.VoxelDef def = registry.GetVoxelGenData().GetRenderDef(id);
                    string label = $"{voxel.name} {orientation}";
                    Assert.AreEqual(id, def.Id, label);
                    Assert.AreEqual(baseDef.MeshLayer, def.MeshLayer, label);
                    Assert.AreEqual(baseDef.Collision, def.Collision, label);
                    Assert.AreEqual(baseDef.LightAttenuation, def.LightAttenuation, label);
                    Assert.AreEqual(baseDef.LightEmission, def.LightEmission, label);
                    Assert.AreEqual(baseDef.Glow, def.Glow, label);
                    Assert.AreEqual(baseDef.SurfaceAlpha, def.SurfaceAlpha, label);
                    Assert.AreEqual(baseDef.VolumeFog, def.VolumeFog, label + " (depth fade)");
                    Assert.AreEqual(baseDef.AlwaysRenderAllFaces, def.AlwaysRenderAllFaces, label);
                    Assert.AreEqual(baseDef.HidesNeighbourFaces, def.HidesNeighbourFaces, label);
                    Assert.AreEqual(light.Attenuation[baseId], light.Attenuation[id], label);
                    Assert.AreEqual(light.Emission[baseId], light.Emission[id], label);

                    Assert.IsTrue(registry.TryGet(id, out Voxel so), label);
                    Assert.AreSame(voxel, so, label);
                    Assert.IsTrue(registry.TryGetName(id, out FixedString32Bytes name), label);
                    StringAssert.StartsWith(voxel.GetFullName() + "#", name.ToString(), label);
                }
            }

            Assert.AreNotEqual(0, registry.GetVoxelGenData().GetRenderDef(Id(registry, glass)).VolumeFog);
            // The voxel map holds the assets' names only; variants resolve to their base
            Assert.AreEqual(registry.BaseCount, registry.VoxelMap.Count);
            CollectionAssert.AllItemsAreUnique(registry.GetAllEntries().Select(e => e.Value.ToString()).ToList());
        }

        [Test]
        public void VariantNames_CarryTheOrientationSuffix(){
            Voxel log = CreateVoxel("Log", Shape("Cube/Cube"), VoxelFacing.Axis, false);
            Voxel oven = CreateVoxel("Oven", Shape("Cube/Cube"), VoxelFacing.Full, true);
            VoxelRegistry registry = Registry(log, oven);
            Assert.AreEqual(registry.VariantOf(Id(registry, log), VoxelOrientation.AxisX), registry.GetIdOrThrow("std:Log#x"));
            Assert.AreEqual(registry.VariantOf(Id(registry, log), VoxelOrientation.AxisZ), registry.GetIdOrThrow("std:Log#z"));
            ushort oven0 = Id(registry, oven);
            Assert.AreEqual(registry.VariantOf(oven0, VoxelOrientation.UpsideDown), registry.GetIdOrThrow("std:Oven#u"));
            Assert.AreEqual(registry.VariantOf(oven0, VoxelOrientation.FrontNegX), registry.GetIdOrThrow("std:Oven#-x"));
            Assert.AreEqual(registry.VariantOf(oven0, VoxelOrientation.Make(VoxelOrientation.FrontPosY, true)),
                registry.GetIdOrThrow("std:Oven#+yu"));
        }

        #endregion

        #region Geometry

        [Test]
        public void RotatedQuads_StayInTheUnitCube_AndNormalsMatchTheirConditions(
            [Values("Cube/Cube", "Slab/Half", "Cross/Cross")] string shapePath){
            (VoxelRegistry registry, Voxel[] voxels) = EveryMode(Shape(shapePath));
            foreach (Voxel voxel in voxels)
            foreach (byte orientation in Orientations(voxel)){
                ushort id = registry.VariantOf(Id(registry, voxel), orientation);
                Voxel.VoxelDef def = registry.GetVoxelGenData().GetRenderDef(id);
                int quadCount = 0;
                foreach ((QuadDrawCondition condition, uint2 range) in Ranges(def))
                foreach ((QuadDefinition.QuadDef quad, _) in Quads(registry, range)){
                    string label = $"{voxel.name} {orientation} {condition}";
                    quadCount++;
                    foreach (float3 corner in Corners(quad))
                        Assert.IsTrue(math.all(corner >= -1e-5f) && math.all(corner <= 1f + 1e-5f),
                            $"{label}: corner {corner}");
                    if (condition != QuadDrawCondition.Always)
                        Assert.IsTrue(math.all(math.abs(quad.normal - VoxelOrientation.Direction(condition)) < 1e-5f),
                            $"{label}: normal {quad.normal}");
                    // Integer basis: the ambient occlusion steps one voxel along up and right
                    Assert.AreEqual(0f, math.dot(quad.up, quad.right), 1e-5f, label);
                }

                Assert.AreEqual(Shape(shapePath).quads.Length, quadCount, $"{voxel.name} {orientation}");
            }
        }

        [Test]
        public void RotatedFullCube_CullsLikeItsBase(){
            (VoxelRegistry registry, Voxel[] voxels) = EveryMode(Shape("Cube/Cube"));
            foreach (Voxel voxel in voxels){
                Voxel.VoxelDef baseDef = registry.GetVoxelGenData().GetRenderDef(Id(registry, voxel));
                Assert.IsTrue(baseDef.HidesNeighbourFaces, voxel.name);
                foreach (byte orientation in Orientations(voxel)){
                    Voxel.VoxelDef def =
                        registry.GetVoxelGenData().GetRenderDef(registry.VariantOf(Id(registry, voxel), orientation));
                    Assert.IsTrue(def.HidesNeighbourFaces);
                    Assert.AreEqual(baseDef.MeshLayer, def.MeshLayer);
                    Assert.AreEqual(baseDef.AlwaysRenderAllFaces, def.AlwaysRenderAllFaces);
                    foreach ((QuadDrawCondition condition, uint2 range) in Ranges(def)){
                        List<(QuadDefinition.QuadDef quad, ushort tex)> quads = Quads(registry, range);
                        if (condition == QuadDrawCondition.Always){
                            Assert.AreEqual(0, quads.Count);
                            continue;
                        }

                        // One quad per face, lying on that face of the cube
                        Assert.AreEqual(1, quads.Count, $"{voxel.name} {orientation} {condition}");
                        int3 direction = VoxelOrientation.Direction(condition);
                        float plane = math.csum(math.max(direction, 0));
                        foreach (float3 corner in Corners(quads[0].quad))
                            Assert.AreEqual(plane, math.dot(corner, math.abs(direction)), 1e-5f,
                                $"{voxel.name} {orientation} {condition}");
                    }
                }
            }
        }

        [Test]
        public void PartialShapes_DoNotHideNeighbourFaces(){
            Voxel cube = CreateVoxel("Cube", Shape("Cube/Cube"), VoxelFacing.None, false);
            Voxel slab = CreateVoxel("Slab", Shape("Slab/Half"), VoxelFacing.None, true);
            Voxel plant = _fixtures.CreateVoxel("Plant", _package, Shape("Cross/Cross"), _tex, MeshLayer.Foliage);
            VoxelRegistry registry = Registry(cube, slab, plant);
            VoxelEngineRenderGenDataAssert(registry, cube, true);
            VoxelEngineRenderGenDataAssert(registry, slab, false);
            VoxelEngineRenderGenDataAssert(registry, plant, false);
            Assert.IsFalse(registry.GetVoxelGenData().GetRenderDef(
                registry.VariantOf(Id(registry, slab), VoxelOrientation.UpsideDown)).HidesNeighbourFaces);
            Assert.IsFalse(registry.GetVoxelGenData().GetRenderDef(0).HidesNeighbourFaces, "air");
        }

        private static void VoxelEngineRenderGenDataAssert(VoxelRegistry registry, Voxel voxel, bool hides){
            Assert.AreEqual(hides, registry.GetVoxelGenData().GetRenderDef(Id(registry, voxel)).HidesNeighbourFaces,
                voxel.name);
        }

        [Test]
        public void FourTurnsAboutY_GiveTheIdentity(){
            int3x3 quarter = VoxelOrientation.Rotation(VoxelFacing.Horizontal, VoxelOrientation.FrontPosX);
            Assert.AreEqual(int3x3.identity, math.mul(math.mul(quarter, quarter), math.mul(quarter, quarter)));
            Assert.AreEqual(math.mul(quarter, quarter),
                VoxelOrientation.Rotation(VoxelFacing.Horizontal, VoxelOrientation.FrontNegZ));
            Assert.AreEqual(math.mul(quarter, math.mul(quarter, quarter)),
                VoxelOrientation.Rotation(VoxelFacing.Full, VoxelOrientation.FrontNegX));

            foreach (VoxelQuad voxelQuad in Shape("Slab/Half").quads){
                QuadDefinition.QuadDef quad = voxelQuad.quadDef.ToStruct();
                QuadDefinition.QuadDef turned = quad;
                for (int i = 0; i < 4; i++) turned = QuadRegistry.Rotate(turned, quarter);
                Assert.AreEqual(quad.position00, turned.position00);
                Assert.AreEqual(quad.position01, turned.position01);
                Assert.AreEqual(quad.position02, turned.position02);
                Assert.AreEqual(quad.position03, turned.position03);
                Assert.AreEqual(quad.normal, turned.normal);
                Assert.AreEqual(quad.up, turned.up);
                Assert.AreEqual(quad.right, turned.right);
                Assert.AreEqual(quad.uv00, turned.uv00);
                Assert.AreEqual(quad.uv03, turned.uv03);
            }
        }

        [Test]
        public void Rotations_TurnTheAuthoredAxesAsSpecified(){
            int3 up = new(0, 1, 0), front = new(0, 0, 1);
            Assert.AreEqual(up, math.mul(VoxelOrientation.Rotation(VoxelFacing.Axis, VoxelOrientation.AxisY), up));
            Assert.AreEqual(0, math.mul(VoxelOrientation.Rotation(VoxelFacing.Axis, VoxelOrientation.AxisX), up).y);
            Assert.AreEqual(1, math.abs(math.mul(VoxelOrientation.Rotation(VoxelFacing.Axis, VoxelOrientation.AxisX), up).x));
            Assert.AreEqual(1, math.abs(math.mul(VoxelOrientation.Rotation(VoxelFacing.Axis, VoxelOrientation.AxisZ), up).z));

            (byte facing, int3 direction)[] fronts = {
                (VoxelOrientation.FrontPosZ, new int3(0, 0, 1)), (VoxelOrientation.FrontPosX, new int3(1, 0, 0)),
                (VoxelOrientation.FrontNegZ, new int3(0, 0, -1)), (VoxelOrientation.FrontNegX, new int3(-1, 0, 0)),
                (VoxelOrientation.FrontPosY, new int3(0, 1, 0)), (VoxelOrientation.FrontNegY, new int3(0, -1, 0))
            };
            foreach ((byte facing, int3 direction) in fronts){
                Assert.AreEqual(direction, math.mul(VoxelOrientation.Rotation(VoxelFacing.Full, facing), front), $"{facing}");
                // The upper half keeps the front and mirrors the profile vertically
                int3x3 upper = VoxelOrientation.Rotation(VoxelFacing.Full, VoxelOrientation.Make(facing, true));
                Assert.AreEqual(direction, math.mul(upper, front), $"{facing} upper");
                Assert.AreEqual(-math.mul(VoxelOrientation.Rotation(VoxelFacing.Full, facing), up), math.mul(upper, up),
                    $"{facing} upper");
                if (facing < 4)
                    Assert.AreEqual(VoxelOrientation.Rotation(VoxelFacing.Full, facing),
                        VoxelOrientation.Rotation(VoxelFacing.Horizontal, facing));
            }
        }

        [Test]
        public void LogAlongX_ShowsTheTopTextureOnItsXFaces_AndSideUvsRunAlongX(){
            Voxel log = CreateVoxel("Log", Shape("Cube/Cube"), VoxelFacing.Axis, false);
            log.TextureMode = Voxel.VoxelTexMode.TopBottomSides;
            log.top = _fixtures.CreateTexture("Top", new Color32(200, 0, 0, 255));
            log.bottom = _fixtures.CreateTexture("Bottom", new Color32(0, 200, 0, 255));
            log.side = _fixtures.CreateTexture("Side", new Color32(0, 0, 200, 255));
            VoxelRegistry registry = Registry(log);

            Voxel.VoxelDef baseDef = registry.GetVoxelGenData().GetRenderDef(Id(registry, log));
            ushort top = Quads(registry, baseDef.Up).Single().tex;
            ushort bottom = Quads(registry, baseDef.Down).Single().tex;
            ushort side = Quads(registry, baseDef.Front).Single().tex;
            Assert.AreNotEqual(top, side);
            Assert.AreNotEqual(bottom, side);
            AssertGrain(registry, baseDef.Front, new int3(0, 1, 0), "base");

            Voxel.VoxelDef x = registry.GetVoxelGenData().GetRenderDef(registry.VariantOf(Id(registry, log),
                VoxelOrientation.AxisX));
            CollectionAssert.AreEquivalent(new[]{ top, bottom },
                new[]{ Quads(registry, x.Right).Single().tex, Quads(registry, x.Left).Single().tex });
            foreach (uint2 range in new[]{ x.Up, x.Down, x.Front, x.Back }){
                Assert.AreEqual(side, Quads(registry, range).Single().tex);
                AssertGrain(registry, range, new int3(1, 0, 0), "x");
            }
        }

        /// <summary>The texture's V runs along the axis on the single quad of a face range.</summary>
        private static void AssertGrain(VoxelRegistry registry, uint2 range, int3 axis, string label){
            QuadDefinition.QuadDef quad = Quads(registry, range).Single().quad;
            float3[] corners = Corners(quad);
            float2[] uvs = { quad.uv00, quad.uv01, quad.uv02, quad.uv03 };
            bool found = false;
            for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++){
                if (uvs[i].x != uvs[j].x || uvs[i].y == uvs[j].y) continue;
                float3 along = math.normalize(corners[j] - corners[i]);
                Assert.AreEqual(1f, math.abs(math.dot(along, axis)), 1e-5f, $"{label}: V runs along {along}");
                found = true;
            }

            Assert.IsTrue(found, label);
        }

        [Test]
        public void UpperSlab_OccupiesTheUpperHalf(){
            Voxel slab = CreateVoxel("Slab", Shape("Slab/Half"), VoxelFacing.None, true);
            VoxelRegistry registry = Registry(slab);
            AssertHeight(registry, Id(registry, slab), 0f, 0.5f);
            AssertHeight(registry, registry.VariantOf(Id(registry, slab), VoxelOrientation.UpsideDown), 0.5f, 1f);
        }

        private static void AssertHeight(VoxelRegistry registry, ushort id, float min, float max){
            Voxel.VoxelDef def = registry.GetVoxelGenData().GetRenderDef(id);
            float low = float.MaxValue, high = float.MinValue;
            foreach ((_, uint2 range) in Ranges(def))
            foreach ((QuadDefinition.QuadDef quad, _) in Quads(registry, range))
            foreach (float3 corner in Corners(quad)){
                low = math.min(low, corner.y);
                high = math.max(high, corner.y);
            }

            Assert.AreEqual(min, low, 1e-5f, $"id {id}");
            Assert.AreEqual(max, high, 1e-5f, $"id {id}");
        }

        #endregion
    }
}
