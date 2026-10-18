using System.Collections.Generic;
using Engine.Scripts.Utils.Extensions;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>TST-E04: BurstMathExtensions, MathExtension, VectorExtension.</summary>
    public class MathExtensionsTests{
        [Test]
        public void Flatten_Int3_IsBijectionOntoSize(){
            int3 size = new(4, 5, 6);
            HashSet<int> seen = new();
            for (int x = 0; x < size.x; x++)
            for (int y = 0; y < size.y; y++)
            for (int z = 0; z < size.z; z++){
                int flat = size.Flatten(x, y, z);
                Assert.GreaterOrEqual(flat, 0);
                Assert.Less(flat, size.Size());
                Assert.IsTrue(seen.Add(flat), $"duplicate index {flat} for ({x},{y},{z})");
                Assert.AreEqual(flat, size.Flatten(new int3(x, y, z)), "int3 overload consistency");
            }

            Assert.AreEqual(size.Size(), seen.Count);
        }

        [Test]
        public void Flatten_Int2_IsBijectionAndMatchesInt3WithDepthOne(){
            int2 size = new(7, 5);
            int3 size3 = new(7, 5, 1);
            HashSet<int> seen = new();
            for (int x = 0; x < size.x; x++)
            for (int y = 0; y < size.y; y++){
                int flat = size.Flatten(x, y);
                Assert.GreaterOrEqual(flat, 0);
                Assert.Less(flat, size.x * size.y);
                Assert.IsTrue(seen.Add(flat));
                Assert.AreEqual(size3.Flatten(x, y, 0), flat);
            }

            Assert.AreEqual(size.x * size.y, seen.Count);
        }

        [Test]
        public void Flatten_YIsFastestAxis(){
            int3 size = new(32, 256, 32);
            Assert.AreEqual(1, size.Flatten(0, 1, 0));
            Assert.AreEqual(256, size.Flatten(0, 0, 1));
            Assert.AreEqual(256 * 32, size.Flatten(1, 0, 0));
        }

        [Test]
        public void CubedSize_SquareSize_Size(){
            Assert.AreEqual(1, 0.CubedSize());
            Assert.AreEqual(27, 1.CubedSize());
            Assert.AreEqual(125, 2.CubedSize());
            Assert.AreEqual(1, 0.SquareSize());
            Assert.AreEqual(9, 1.SquareSize());
            Assert.AreEqual(25, 2.SquareSize());
            Assert.AreEqual(3 * 5 * 7, new int3(1, 2, 3).CubedSize());
            Assert.AreEqual(120, new int3(4, 5, 6).Size());
        }

        [Test]
        public void MemberMultiply_MemberDivide(){
            Assert.AreEqual(new int3(10, 18, 28), new int3(2, 3, 4).MemberMultiply(new int3(5, 6, 7)));
            Assert.AreEqual(new int3(10, 18, 28), new int3(2, 3, 4).MemberMultiply(5, 6, 7));
            Assert.AreEqual(new int3(2, 3, 4), new int3(10, 18, 28).MemberDivide(new int3(5, 6, 7)));
            // Integer division truncates towards zero (documented behaviour, see D-08 for the consequence).
            Assert.AreEqual(new int3(-1, 1, -3), new int3(-3, 3, -7).MemberDivide(new int3(2, 2, 2)));
        }

        [Test]
        public void SqrMagnitude(){
            Assert.AreEqual(14, new int3(1, -2, 3).SqrMagnitude());
            Assert.AreEqual(25, new int2(-3, 4).SqrMagnitude());
        }

        [Test]
        public void OrReduce_AndReduce(){
            Assert.IsTrue(new bool3(false, false, true).OrReduce());
            Assert.IsTrue(new bool3(true, false, false).OrReduce());
            Assert.IsFalse(new bool3(false, false, false).OrReduce());
            Assert.IsTrue(new bool3(true, true, true).AndReduce());
            Assert.IsFalse(new bool3(true, true, false).AndReduce());
            Assert.IsFalse(new bool3(false, true, true).AndReduce());
        }

        [Test]
        public void Vector3_Int3_FloorsNegatives(){
            Assert.AreEqual(new int3(-1, 0, -2), new Vector3(-0.5f, 0.5f, -1.0001f).Int3());
            Assert.AreEqual(new int3(-1, -1, -1), new Vector3(-1f, -1f, -1f).Int3());
            Assert.AreEqual(new int3(2, 3, 4), new Vector3(2.9f, 3.1f, 4f).Int3());
            Assert.AreEqual(new Vector3Int(-1, 0, -2), new Vector3(-0.5f, 0.5f, -1.0001f).V3Int());
        }

        [Test]
        public void Vector_Conversions(){
            Assert.AreEqual(new int3(1, -2, 3), new Vector3Int(1, -2, 3).Int3());
            Assert.AreEqual(new float3(1.5f, -2f, 3f), new Vector3(1.5f, -2f, 3f).Float3());
            Assert.AreEqual(new int2(4, -5), new Vector2Int(4, -5).Int2());
            Assert.AreEqual(new float2(4f, -5f), new int2(4, -5).Float2());
            Assert.AreEqual(new float2(1.25f, 2f), new Vector2(1.25f, 2f).Float2());
            Assert.AreEqual(new Vector3Int(1, 2, 3), new int3(1, 2, 3).GetVector3Int());
            Assert.AreEqual(new Vector3(1, 2, 3), new int3(1, 2, 3).GetVector3());
            Assert.AreEqual(new Vector3(1, 2, 0), new int2(1, 2).GetVector2());
            float3 n = new float3(0, 3, 4).Normalized();
            Assert.That(math.length(n), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(n.y, Is.EqualTo(0.6f).Within(1e-6f));
        }

        [Test]
        public void Max_Half(){
            Assert.AreEqual((half)3f, MathExtension.Max((half)3f, (half)2f));
            Assert.AreEqual((half)3f, MathExtension.Max((half)(-1f), (half)3f));
        }

        [Test]
        public void VectorConstants_DirectionsAreUnitAxes(){
            Assert.AreEqual(6, VectorConstants.Int3Directions.Length);
            foreach (int3 dir in VectorConstants.Int3Directions) Assert.AreEqual(1, dir.SqrMagnitude());
            Assert.AreEqual(new int3(0, 1, 0), VectorConstants.Int3Up);
            Assert.AreEqual(new int3(0, 0, 1), VectorConstants.Int3Forward);
            Assert.AreEqual(new int3(1, 0, 0), VectorConstants.Int3Right);
        }
    }
}
