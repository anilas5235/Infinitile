using System.Runtime.InteropServices;
using Engine.Scripts.Render;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     GPU round trip of the CopyPoints kernel as <see cref="CopyPointsHandler" /> dispatches it: every page of a
    ///     layer lands on its target page in the render buffer, only its first count points are written, and the
    ///     rest of the buffer stays untouched.
    /// </summary>
    public class CopyPointsTests{
        private const int TargetPages = 16;
        private const uint Untouched = 0xFFFFFFFFu;

        /// <summary>Same size and layout as <c>PointData</c> in VoxelCommon.hlsl (float3 + uint4).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct TestPoint{
            public uint A, B, C, D, E, F, G;

            public static TestPoint Of(uint v){
                return new TestPoint{ A = v, B = v + 1, C = v + 2, D = v + 3, E = v + 4, F = v + 5, G = v + 6 };
            }
        }

        [Test]
        public void CopyPoints_WritesEachPageToItsTarget_AndNothingElse(){
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Engine/Shaders/Urp/ComputeShaders/CopyPoints.compute");
            Assert.IsNotNull(shader, "CopyPoints.compute not found");
            Assert.AreEqual(28, Marshal.SizeOf<TestPoint>());

            // (target page, point count) per source page. Solid spans more than one thread group and has an
            // empty page and a count above the page size (clamped); foliage has no pages at all.
            uint2[][] layers ={
                new uint2[]{
                    new(9, 256), new(2, 256), new(14, 0), new(0, 256), new(7, 1),
                    new(11, 255), new(4, 300), new(15, 256), new(1, 256), new(12, 77)
                },
                new uint2[]{ new(5, 256), new(3, 17) },
                new uint2[0]
            };
            int[] pointsIn ={ SolidPointsInNameID, TransparentPointsInNameID, FoliagePointsInNameID };
            int[] pointsOut ={ SolidPointsCopyOutNameID, TransparentPointsCopyOutNameID, FoliagePointsCopyOutNameID };
            int[] pagesIds ={ SolidPagesNameID, TransparentPagesNameID, FoliagePagesNameID };

            int kernel = shader.FindKernel("CopyPoints");
            int stride = Marshal.SizeOf<TestPoint>();
            GraphicsBuffer[] inputs = new GraphicsBuffer[3], outputs = new GraphicsBuffer[3], pages = new GraphicsBuffer[3];
            TestPoint[][] sources = new TestPoint[3][];
            using GraphicsBuffer pageCounts = new(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            try{
                uint[] counts = new uint[3];
                int maxPageCount = 0;
                for (int layer = 0; layer < 3; layer++){
                    uint2[] table = layers[layer];
                    counts[layer] = (uint)table.Length;
                    maxPageCount = math.max(maxPageCount, table.Length);

                    sources[layer] = new TestPoint[math.max(1, table.Length) * PointsPerPage];
                    for (int i = 0; i < sources[layer].Length; i++) sources[layer][i] = TestPoint.Of((uint)((layer << 24) + i * 8));
                    inputs[layer] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sources[layer].Length, stride);
                    inputs[layer].SetData(sources[layer]);

                    TestPoint[] blank = new TestPoint[TargetPages * PointsPerPage];
                    for (int i = 0; i < blank.Length; i++) blank[i] = TestPoint.Of(Untouched - 6);
                    outputs[layer] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, blank.Length, stride);
                    outputs[layer].SetData(blank);

                    pages[layer] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, table.Length),
                        Marshal.SizeOf<uint2>());
                    if (table.Length > 0) pages[layer].SetData(table);

                    shader.SetBuffer(kernel, pointsIn[layer], inputs[layer]);
                    shader.SetBuffer(kernel, pointsOut[layer], outputs[layer]);
                    shader.SetBuffer(kernel, pagesIds[layer], pages[layer]);
                }

                pageCounts.SetData(counts);
                shader.SetBuffer(kernel, PageCountsNameID, pageCounts);
                shader.SetInt(PointsPerPageNameID, PointsPerPage);
                CopyPointsHandler.Dispatch(shader, kernel, maxPageCount);

                for (int layer = 0; layer < 3; layer++){
                    TestPoint[] expected = new TestPoint[TargetPages * PointsPerPage];
                    for (int i = 0; i < expected.Length; i++) expected[i] = TestPoint.Of(Untouched - 6);
                    for (int page = 0; page < layers[layer].Length; page++){
                        uint2 entry = layers[layer][page];
                        int copy = math.min((int)entry.y, PointsPerPage);
                        for (int i = 0; i < copy; i++)
                            expected[entry.x * PointsPerPage + i] = sources[layer][page * PointsPerPage + i];
                    }

                    TestPoint[] actual = new TestPoint[expected.Length];
                    outputs[layer].GetData(actual);
                    for (int i = 0; i < expected.Length; i++)
                        if (!expected[i].Equals(actual[i]))
                            Assert.Fail($"layer {layer}, target page {i / PointsPerPage}, point {i % PointsPerPage}: " +
                                        $"expected {expected[i].A:X8} but was {actual[i].A:X8}");
                }
            }
            finally{
                for (int layer = 0; layer < 3; layer++){
                    inputs[layer]?.Dispose();
                    outputs[layer]?.Dispose();
                    pages[layer]?.Dispose();
                }
            }
        }
    }
}
