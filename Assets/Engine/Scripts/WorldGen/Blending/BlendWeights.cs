using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Blending{
    /// <summary>
    ///     Normalised weight vector over the active biomes: up to 16 entries, stored as four float4 so the
    ///     struct is blittable and Burst-friendly without unsafe code. Index = biome index in the BiomeSet.
    ///     Every blend method returns one of these with Σ w = 1 (± 1e−5) and w ≥ 0; method 1 returns
    ///     <see cref="OneHot" />.
    /// </summary>
    public struct BlendWeights{
        /// <summary>Maximum number of active biomes (BiomeSet.MaxBiomes).</summary>
        public const int Capacity = 16;

        private float4 _w0;
        private float4 _w1;
        private float4 _w2;
        private float4 _w3;

        public float this[int index]{
            get{
                switch (index >> 2){
                    case 0: return _w0[index & 3];
                    case 1: return _w1[index & 3];
                    case 2: return _w2[index & 3];
                    default: return _w3[index & 3];
                }
            }
            set{
                switch (index >> 2){
                    case 0:
                        _w0[index & 3] = value;
                        break;
                    case 1:
                        _w1[index & 3] = value;
                        break;
                    case 2:
                        _w2[index & 3] = value;
                        break;
                    default:
                        _w3[index & 3] = value;
                        break;
                }
            }
        }

        /// <summary>Weights of method 1: 1 at <paramref name="index" />, 0 elsewhere.</summary>
        public static BlendWeights OneHot(int index){
            BlendWeights w = default;
            if (index >= 0 && index < Capacity) w[index] = 1f;
            return w;
        }

        /// <summary>Σ w over the first <paramref name="count" /> entries.</summary>
        public float Sum(int count = Capacity){
            float sum = 0f;
            for (int i = 0; i < count; i++) sum += this[i];
            return sum;
        }

        /// <summary>Scales the first <paramref name="count" /> entries so they sum to 1. A zero vector is left unchanged.</summary>
        public void Normalize(int count = Capacity){
            float sum = Sum(count);
            if (sum <= 0f) return;
            float inv = 1f / sum;
            for (int i = 0; i < count; i++) this[i] *= inv;
        }

        /// <summary>Index of the largest weight among the first <paramref name="count" />; ties break to the lower index.</summary>
        public int ArgMax(int count = Capacity){
            int best = 0;
            float bestValue = this[0];
            for (int i = 1; i < count; i++){
                float v = this[i];
                if (v > bestValue){
                    bestValue = v;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Largest weight among the first <paramref name="count" />.</summary>
        public float Max(int count = Capacity){
            return this[ArgMax(count)];
        }
    }
}
