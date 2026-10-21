using System;
using Engine.Editor.WorldGen;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[assembly: RegisterGenericJobType(typeof(PreviewDiff.MismatchJob<int>))]
[assembly: RegisterGenericJobType(typeof(PreviewDiff.MismatchJob<ushort>))]

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Difference views of two configurations sampled over the same region and seed. Height views show
    ///     the signed difference B − A (red = B higher, blue = B lower, scaled to the largest difference); categorical
    ///     views (biome classes, surface source, flora) show a mismatch mask over the dimmed A view; any other view
    ///     falls back to the per-channel pixel difference. Reads sampled buffers only; no sampler call.
    /// </summary>
    public static class PreviewDiff{
        /// <summary>Colour of a mismatching sample in the mask.</summary>
        public static Color32 MismatchColor => new(255, 40, 200, 255);

        /// <summary>Summary of a difference view.</summary>
        public struct Stats{
            public int Count;

            /// <summary>Samples that differ (height: by at least <see cref="HeightThreshold" /> blocks).</summary>
            public int Differing;

            /// <summary>Signed views only: mean and maximum |B − A| and the mean B − A, in blocks.</summary>
            public float MeanAbs;

            public float MaxAbs;
            public float Mean;

            public float Share => Count > 0 ? Differing / (float)Count : 0f;
        }

        /// <summary>Height difference from which a column counts as differing (one block).</summary>
        public const float HeightThreshold = 1f;

        /// <summary>Signed difference B − A, coloured, with statistics.</summary>
        public static Stats Signed(NativeArray<float> a, NativeArray<float> b, NativeArray<Color32> output){
            CheckLengths(a.Length, b.Length, output.Length);
            using NativeArray<float> stats = new(4, Allocator.TempJob);
            new SignedJob{ A = a, B = b, Output = output, Stats = stats }.Run();
            return new Stats{
                Count = a.Length, Differing = (int)stats[0], MeanAbs = stats[1], MaxAbs = stats[2], Mean = stats[3]
            };
        }

        /// <summary>Mismatch mask of two categorical maps over the dimmed <paramref name="baseView" />.</summary>
        public static Stats Mismatch<T>(NativeArray<T> a, NativeArray<T> b, NativeArray<Color32> baseView, NativeArray<Color32> output)
            where T : unmanaged, IEquatable<T>{
            CheckLengths(a.Length, b.Length, output.Length);
            CheckLengths(a.Length, baseView.Length, output.Length);
            using NativeArray<int> differing = new(1, Allocator.TempJob);
            new MismatchJob<T>{ A = a, B = b, Base = baseView, Output = output, Differing = differing }.Run();
            return new Stats{ Count = a.Length, Differing = differing[0] };
        }

        /// <summary>Per-channel pixel difference |B − A| (amplified ×4); a sample differs when any channel does.</summary>
        public static Stats Pixels(NativeArray<Color32> a, NativeArray<Color32> b, NativeArray<Color32> output){
            CheckLengths(a.Length, b.Length, output.Length);
            using NativeArray<int> differing = new(1, Allocator.TempJob);
            new PixelJob{ A = a, B = b, Output = output, Differing = differing }.Run();
            return new Stats{ Count = a.Length, Differing = differing[0] };
        }

        private static void CheckLengths(int a, int b, int output){
            if (a != b || a != output) throw new ArgumentException($"Difference inputs differ in length ({a}, {b}, {output}).");
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct SignedJob : IJob{
            [ReadOnly] public NativeArray<float> A;
            [ReadOnly] public NativeArray<float> B;
            [WriteOnly] public NativeArray<Color32> Output;

            /// <summary>[differing, mean |d|, max |d|, mean d].</summary>
            public NativeArray<float> Stats;

            public void Execute(){
                int differing = 0;
                double sumAbs = 0, sum = 0;
                float maxAbs = 0f;
                for (int i = 0; i < A.Length; i++){
                    float d = B[i] - A[i];
                    float abs = math.abs(d);
                    sumAbs += abs;
                    sum += d;
                    maxAbs = math.max(maxAbs, abs);
                    if (abs >= HeightThreshold) differing++;
                }

                float scale = math.max(maxAbs, HeightThreshold);
                for (int i = 0; i < A.Length; i++){
                    float d = B[i] - A[i];
                    // Square root so small differences stay visible next to a large maximum.
                    float t = math.sqrt(math.saturate(math.abs(d) / scale));
                    float3 target = d > 0f ? new float3(1f, 0.15f, 0.1f) : new float3(0.15f, 0.35f, 1f);
                    float3 c = math.lerp(new float3(0.08f), target, t);
                    Output[i] = new Color32((byte)(c.x * 255f), (byte)(c.y * 255f), (byte)(c.z * 255f), 255);
                }

                int n = math.max(1, A.Length);
                Stats[0] = differing;
                Stats[1] = (float)(sumAbs / n);
                Stats[2] = maxAbs;
                Stats[3] = (float)(sum / n);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        public struct MismatchJob<T> : IJob where T : unmanaged, IEquatable<T>{
            [ReadOnly] public NativeArray<T> A;
            [ReadOnly] public NativeArray<T> B;
            [ReadOnly] public NativeArray<Color32> Base;
            [WriteOnly] public NativeArray<Color32> Output;
            public NativeArray<int> Differing;

            public void Execute(){
                int differing = 0;
                for (int i = 0; i < A.Length; i++){
                    if (!A[i].Equals(B[i])){
                        differing++;
                        Output[i] = new Color32(255, 40, 200, 255); // MismatchColor
                        continue;
                    }

                    Color32 c = Base[i];
                    Output[i] = new Color32((byte)(c.r * 0.3f), (byte)(c.g * 0.3f), (byte)(c.b * 0.3f), 255);
                }

                Differing[0] = differing;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct PixelJob : IJob{
            [ReadOnly] public NativeArray<Color32> A;
            [ReadOnly] public NativeArray<Color32> B;
            [WriteOnly] public NativeArray<Color32> Output;
            public NativeArray<int> Differing;

            public void Execute(){
                int differing = 0;
                for (int i = 0; i < A.Length; i++){
                    Color32 a = A[i], b = B[i];
                    int dr = math.abs(a.r - b.r), dg = math.abs(a.g - b.g), db = math.abs(a.b - b.b);
                    if (dr + dg + db > 0) differing++;
                    Output[i] = new Color32((byte)math.min(255, dr * 4), (byte)math.min(255, dg * 4), (byte)math.min(255, db * 4), 255);
                }

                Differing[0] = differing;
            }
        }
    }
}
