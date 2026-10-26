using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using O = Engine.Scripts.VoxelConfig.Data.Voxel.VoxelOrientation;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Orientation of a placed voxel from the click, for every facing mode and the top/bottom toggle, including
    ///     the ties between components.
    /// </summary>
    public class VoxelPlacementTests{
        private static readonly float3 PosX = new(1, 0, 0), NegX = new(-1, 0, 0);
        private static readonly float3 PosY = new(0, 1, 0), NegY = new(0, -1, 0);
        private static readonly float3 PosZ = new(0, 0, 1), NegZ = new(0, 0, -1);
        private static readonly float3 AnyPoint = new(10.5f, 20.5f, 30.5f);

        private static byte Orientation(VoxelFacing facing, bool topBottom, float3 normal, float3 point, float3 look){
            return VoxelPlacement.Orientation(facing, topBottom, normal, point, math.normalize(look));
        }

        // (clicked face normal, expected axis)
        private static readonly object[] AxisCases = {
            new object[]{ PosX, O.AxisX }, new object[]{ NegX, O.AxisX },
            new object[]{ PosY, O.AxisY }, new object[]{ NegY, O.AxisY },
            new object[]{ PosZ, O.AxisZ }, new object[]{ NegZ, O.AxisZ },
            // Ties: horizontal before vertical, X before Z
            new object[]{ math.normalize(new float3(1, 1, 0)), O.AxisX },
            new object[]{ math.normalize(new float3(0, -1, 1)), O.AxisZ },
            new object[]{ math.normalize(new float3(-1, 0, 1)), O.AxisX },
            new object[]{ math.normalize(new float3(1, 1, 1)), O.AxisX }
        };

        [TestCaseSource(nameof(AxisCases))]
        public void Axis_FollowsTheClickedFace(float3 normal, byte expected){
            // The view direction does not matter
            foreach (float3 look in new[]{ PosZ, new float3(1, -1, 0.2f), NegY })
                Assert.AreEqual(expected, Orientation(VoxelFacing.Axis, false, normal, AnyPoint, look));
        }

        // (view direction, expected front for Horizontal, expected front for Full)
        private static readonly object[] FrontCases = {
            new object[]{ PosZ, O.FrontNegZ, O.FrontNegZ }, new object[]{ NegZ, O.FrontPosZ, O.FrontPosZ },
            new object[]{ PosX, O.FrontNegX, O.FrontNegX }, new object[]{ NegX, O.FrontPosX, O.FrontPosX },
            // Looking down at a slant: the larger horizontal component wins for Horizontal, y for Full
            new object[]{ new float3(0.3f, -0.9f, -0.2f), O.FrontNegX, O.FrontPosY },
            new object[]{ new float3(0.1f, -0.5f, 0.8f), O.FrontNegZ, O.FrontNegZ },
            new object[]{ new float3(-0.2f, 0.9f, 0.3f), O.FrontNegZ, O.FrontNegY },
            new object[]{ NegY, O.FrontPosX, O.FrontPosY }, // straight down: no horizontal component, X wins the tie
            new object[]{ PosY, O.FrontPosX, O.FrontNegY },
            // 45° ties: horizontal before vertical, X before Z
            new object[]{ new float3(1, 0, 1), O.FrontNegX, O.FrontNegX },
            new object[]{ new float3(-1, 0, -1), O.FrontPosX, O.FrontPosX },
            new object[]{ new float3(-1, 0, 1), O.FrontPosX, O.FrontPosX },
            new object[]{ new float3(0, -1, 1), O.FrontNegZ, O.FrontNegZ },
            new object[]{ new float3(1, -1, 0), O.FrontNegX, O.FrontNegX },
            new object[]{ new float3(-1, 1, 1), O.FrontPosX, O.FrontPosX }
        };

        [TestCaseSource(nameof(FrontCases))]
        public void Front_FacesThePlayer(float3 look, byte horizontal, byte full){
            // The clicked face does not matter
            foreach (float3 normal in new[]{ PosY, NegX, PosZ }){
                Assert.AreEqual(horizontal, Orientation(VoxelFacing.Horizontal, false, normal, AnyPoint, look), "Horizontal");
                Assert.AreEqual(full, Orientation(VoxelFacing.Full, false, normal, AnyPoint, look), "Full");
            }
        }

        // (clicked face normal, hit point y, upper half)
        private static readonly object[] HalfCases = {
            new object[]{ PosY, 5f, false }, new object[]{ PosY, 5.9f, false }, // top face: lower half
            new object[]{ NegY, 5f, true }, new object[]{ NegY, 5.1f, true }, // bottom face: upper half
            new object[]{ PosX, 3.7f, true }, new object[]{ NegZ, 3.2f, false }, // side face: the clicked half
            new object[]{ PosZ, 3.5f, false }, new object[]{ NegX, 3.51f, true },
            new object[]{ PosX, -2.25f, true }, new object[]{ PosX, -2.75f, false } // below y 0
        };

        [TestCaseSource(nameof(HalfCases))]
        public void TopBottom_PicksTheClickedHalf(float3 normal, float y, bool upper){
            float3 point = new(4.5f, y, 7.5f);
            Assert.AreEqual(upper ? O.UpsideDown : 0, Orientation(VoxelFacing.None, true, normal, point, PosZ));
            // Without the toggle there is no upper half
            Assert.AreEqual(0, Orientation(VoxelFacing.None, false, normal, point, PosZ));
        }

        [Test]
        public void CombinedModes_ApplyBothRules(){
            float3 side = new(4f, 2.75f, 7.5f);
            Assert.AreEqual(O.Make(O.FrontNegZ, true), Orientation(VoxelFacing.Horizontal, true, PosX, side, PosZ));
            Assert.AreEqual(O.Make(O.FrontPosX, false),
                Orientation(VoxelFacing.Horizontal, true, PosY, new float3(4.5f, 3f, 7.5f), NegX));
            Assert.AreEqual(O.Make(O.FrontPosY, false),
                Orientation(VoxelFacing.Full, true, PosY, new float3(4.5f, 3f, 7.5f), NegY));
            Assert.AreEqual(O.Make(O.FrontNegY, true),
                Orientation(VoxelFacing.Full, true, NegY, new float3(4.5f, 3f, 7.5f), PosY));
            Assert.AreEqual(O.Make(O.AxisZ, true), Orientation(VoxelFacing.Axis, true, PosZ, new float3(4.5f, 9.6f, 7f), PosX));
        }

        #region Resolve

        private RegistryTestFixtures _fixtures;
        private readonly List<IDisposable> _disposables = new();

        [SetUp]
        public void SetUp(){
            _fixtures = new RegistryTestFixtures();
        }

        [TearDown]
        public void TearDown(){
            foreach (IDisposable disposable in _disposables) disposable.Dispose();
            _disposables.Clear();
            _fixtures.DestroyAll();
        }

        [Test]
        public void Resolve_GivesTheVariantForTheClick(){
            VoxelDataPackage package = _fixtures.CreatePackage(RegistryTestFixtures.Prefix);
            VoxelShape cube = Resources.Load<VoxelShape>("VoxelDataPackages/Standard/Shapes/Cube/Cube");
            Texture2D tex = _fixtures.CreateTexture("Tex", new Color32(1, 2, 3, 255));
            Voxel stone = _fixtures.CreateVoxel("Stone", package, cube, tex);
            Voxel log = _fixtures.CreateVoxel("Log", package, cube, tex);
            log.facing = VoxelFacing.Axis;
            Voxel stair = _fixtures.CreateVoxel("Stair", package, cube, tex);
            stair.facing = VoxelFacing.Horizontal;
            stair.topBottom = true;
            VoxelRegistry registry = _fixtures.CreateVoxelRegistry(stone, log, stair);
            _disposables.Add(registry);

            ushort stoneId = registry.GetIdOrThrow("std:Stone");
            ushort logId = registry.GetIdOrThrow("std:Log");
            ushort stairId = registry.GetIdOrThrow("std:Stair");
            float3 side = new(4f, 2.75f, 7.5f);

            Assert.AreEqual(stoneId, VoxelPlacement.Resolve(registry, stoneId, PosX, side, PosZ));
            ushort logX = registry.VariantOf(logId, O.AxisX);
            Assert.AreNotEqual(logId, logX);
            Assert.AreEqual(logX, VoxelPlacement.Resolve(registry, logId, NegX, side, PosZ));
            Assert.AreEqual(logId, VoxelPlacement.Resolve(registry, logId, PosY, side, PosZ));
            // A variant is placed as its base would be
            Assert.AreEqual(logId, VoxelPlacement.Resolve(registry, logX, PosY, side, PosZ));
            Assert.AreEqual(registry.VariantOf(stairId, O.Make(O.FrontNegZ, true)),
                VoxelPlacement.Resolve(registry, stairId, PosX, side, PosZ));
        }

        #endregion
    }
}
