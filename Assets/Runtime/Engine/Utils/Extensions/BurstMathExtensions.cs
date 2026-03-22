using System;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Runtime.Engine.Utils.Extensions
{
    /// <summary>
    /// Burst-kompatible mathematische Erweiterungen für int / int2 / int3 sowie bool3 und Vektor-Konvertierungen.
    /// Fokus auf Flatten/Größenoperationen für Chunk- und Voxel-Berechnung.
    /// </summary>
    [GenerateTestsForBurstCompatibility]
    public static class BurstMathExtensions
    {
        /// <summary>
        /// Liefert (2·r+1)^3 – Anzahl Zellen in einem kubischen Bereich mit Radius r.
        /// </summary>
        [BurstCompile]
        public static int CubedSize(this int r) => (2 * r + 1) * (2 * r + 1) * (2 * r + 1);

        /// <summary>
        /// Liefert (2·r+1)^2 – Anzahl Zellen in einem quadratischen Bereich mit Radius r.
        /// </summary>
        [BurstCompile]
        public static int SquareSize(this int r) => (2 * r + 1) * (2 * r + 1);

        /// <summary>
        /// Liefert (2·r.x+1)·(2·r.y+1)·(2·r.z+1) für anisotropen Radius.
        /// </summary>
        [BurstCompile]
        public static int CubedSize(this int3 r) => (2 * r.x + 1) * (2 * r.y + 1) * (2 * r.z + 1);

        /// <summary>
        /// Flacht 2D Koordinaten (x, y) auf eindimensionalen Index basierend auf Größe (vec.x, vec.y) ab.
        /// </summary>
        [BurstCompile]
        public static int Flatten(this int2 vec, int x, int y) =>
            x * vec.y +
            y;

        /// <summary>
        /// Flacht 3D Koordinaten (x, y, z) auf eindimensionalen Index ab (x·Y·Z + z·Y + y).
        /// </summary>
        [BurstCompile]
        public static int Flatten(this int3 vec, int x, int y, int z) =>
            x * vec.y * vec.z +
            z * vec.y +
            y;

        /// <summary>
        /// Flacht 3D Position ab unter Verwendung eines int3 Position-Structs.
        /// </summary>
        [BurstCompile]
        public static int Flatten(this int3 vec, in int3 pos) => Flatten(vec, pos.x, pos.y, pos.z);

        /// <summary>
        /// Reduziert bool3 mit OR Verknüpfung.
        /// </summary>
        [BurstCompile]
        public static bool OrReduce(this bool3 val) => val.x || val.y || val.z;

        /// <summary>
        /// Reduziert bool3 mit AND Verknüpfung.
        /// </summary>
        [BurstCompile]
        public static bool AndReduce(this bool3 val) => val is { x: true, y: true, z: true };

        /// <summary>
        /// Volumen eines int3 (x·y·z).
        /// </summary>
        [BurstCompile]
        public static int Size(this int3 vec) => vec.x * vec.y * vec.z;

        /// <summary>
        /// Konvertiert int3 nach Vector3Int.
        /// </summary>
        [BurstCompile]
        public static Vector3Int GetVector3Int(this int3 vec) => new(vec.x, vec.y, vec.z);

        /// <summary>
        /// Konvertiert int3 nach Vector3.
        /// </summary>
        [BurstCompile]
        public static Vector3 GetVector3(this int3 vec) => new(vec.x, vec.y, vec.z);

        /// <summary>
        /// Konvertiert int2 nach Vector2.
        /// </summary>
        [BurstCompile]
        public static Vector3 GetVector2(this int2 vec) => new(vec.x, vec.y);
    }

    /// <summary>
    /// Nicht-Burst spezifische mathematische Erweiterungen für Distanz und komponentenweises Multiplizieren.
    /// </summary>
    public static class MathExtension
    {
        /// <summary>
        /// Quadratische Länge eines int3 Vektors.
        /// </summary>
        public static int SqrMagnitude(this int3 vec) => vec.x * vec.x + vec.y * vec.y + vec.z * vec.z;

        /// <summary>
        /// Quadratische Länge eines int3 Vektors.
        /// </summary>
        public static int SqrMagnitude(this int2 vec) => vec.x * vec.x + vec.y * vec.y;

        /// <summary>
        /// Komponentenweise Multiplikation zweier int3.
        /// </summary>
        public static int3 MemberMultiply(this int3 a, int3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);

        /// <summary>
        /// Komponentenweise Multiplikation mit Einzelwerten.
        /// </summary>
        public static int3 MemberMultiply(this int3 a, int x, int y, int z) => new(a.x * x, a.y * y, a.z * z);
        
        public static int3 MemberDivide(this int3 a, int3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);


        public static half Max(half a, half b) => a > b ? a : b;
    }

    public static class VectorConstants
    {
        /// <summary> (0,0,0) Vektor. </summary>
        public static readonly int3 Int3Zero = new(0, 0, 0);

        /// <summary> (1,1,1) Vektor. </summary>
        public static readonly int3 Int3One = new(1, 1, 1);

        /// <summary> (0,1,0) Vektor. </summary>
        public static readonly int3 Int3Up = new(0, 1, 0);

        /// <summary> (0,-1,0) Vektor. </summary>
        public static readonly int3 Int3Down = new(0, -1, 0);

        /// <summary> (1,0,0) Vektor. </summary>
        public static readonly int3 Int3Right = new(1, 0, 0); 

        /// <summary> (-1,0,0) Vektor. </summary>
        public static readonly int3 Int3Left = new(-1, 0, 0);

        /// <summary> (0,0,1) Vektor. </summary>
        public static readonly int3 Int3Forward = new(0, 0, 1);

        /// <summary> (0,0,-1) Vektor. </summary>
        public static readonly int3 Int3Backward = new(0, 0, -1);

        public static readonly int3[] Int3Directions = new[]
        {
            Int3Forward,
            Int3Backward,
            Int3Right,
            Int3Left,
            Int3Up,
            Int3Down,
        };

        /// <summary> (0,0,0) Vektor. </summary>
        public static readonly float3 Float3Zero = new(0, 0, 0);

        /// <summary> (1,1,1) Vektor. </summary>
        public static readonly float3 Float3One = new(1, 1, 1);

        /// <summary> (0,1,0) Vektor. </summary>
        public static readonly float3 Float3Up = new(0, 1, 0);

        /// <summary> (0,-1,0) Vektor. </summary>
        public static readonly float3 Float3Down = new(0, -1, 0);

        /// <summary> (1,0,0) Vektor. </summary>
        public static readonly float3 Float3Right = new(1, 0, 0);

        /// <summary> (-1,0,0) Vektor. </summary>
        public static readonly float3 Float3Left = new(-1, 0, 0);

        /// <summary> (0,0,1) Vektor. </summary>
        public static readonly float3 Float3Forward = new(0, 0, 1);

        /// <summary> (0,0,-1) Vektor. </summary>
        public static readonly float3 Float3Backward = new(0, 0, -1);
    }

    /// <summary>
    /// Erweiterungen für Vektor Konvertierung, Normalisierung und Richtungsermittlung.
    /// </summary>
    public static class VectorExtension
    {
        /// <summary>
        /// Konvertiert Vector3Int zu int3.
        /// </summary>
        public static int3 Int3(this Vector3Int vec) => new(vec.x, vec.y, vec.z);

        /// <summary>
        /// Konvertiert Vector3 (floored) zu int3.
        /// </summary>
        public static int3 Int3(this Vector3 vec) => Vector3Int.FloorToInt(vec).Int3();

        public static float3 Float3(this Vector3 vec) => new(vec.x, vec.y, vec.z);

        /// <summary>
        /// Konvertiert Vector3 (floored) zu Vector3Int.
        /// </summary>
        public static Vector3Int V3Int(this Vector3 vec) => Vector3Int.FloorToInt(vec);

        /// <summary>
        /// Konvertiert int2 zu float2.
        /// </summary>
        public static float2 Float2(this int2 vec) => new(vec.x, vec.y);
        
        public static float2 Float2(this Vector2 vec) => new(vec.x, vec.y);

        /// <summary>
        /// Normalisiert einen float3.
        /// </summary>
        public static float3 Normalized(this float3 vec) => math.normalize(vec);
    }
}