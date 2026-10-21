using Engine.Scripts.Jobs.Light;
using NUnit.Framework;

namespace Engine.Tests.EditMode{
    /// <summary>The byte-lane BFS step in <see cref="LightRegion.Propagate" /> equals the scalar reference.</summary>
    public partial class LightPropagationTests{
        [Test]
        public void Lanes_SpreadPack_RoundTripsEveryValue(){
            for (int v = 0; v <= ushort.MaxValue; v++){
                uint lanes = LightRegion.Spread((ushort)v);
                uint expected = 0;
                for (int c = 0; c < 4; c++) expected |= (uint)((v >> (4 * c)) & 0xF) << (8 * c);
                if (lanes != expected) Assert.Fail($"{v:X4}: spread {lanes:X8}, expected {expected:X8}");
                if (LightRegion.Pack(lanes) != v) Assert.Fail($"{v:X4}: packed back to {LightRegion.Pack(lanes):X4}");
            }
        }

        [Test]
        public void Lanes_SubtractAndCompare_MatchPerLaneArithmetic(){
            for (int a = 0; a < 16; a++)
            for (int b = 0; b < 16; b++){
                // Every lane holds a different pair so a borrow across lanes would show up.
                ushort va = (ushort)(a | (b << 4) | (((a + 7) & 0xF) << 8) | (((15 - b) & 0xF) << 12));
                ushort vb = (ushort)(b | (a << 4) | (((b + 3) & 0xF) << 8) | (((15 - a) & 0xF) << 12));
                uint la = LightRegion.Spread(va), lb = LightRegion.Spread(vb);
                uint greater = LightRegion.GreaterLanes(la, lb);
                for (int c = 0; c < 4; c++){
                    int x = (int)(la >> (8 * c)) & 0xF, y = (int)(lb >> (8 * c)) & 0xF;
                    Assert.AreEqual(x > y ? 0xF : 0, (int)(greater >> (8 * c)) & 0xFF, $"{x} > {y} in lane {c}");
                }

                for (int loss = 0; loss <= 16; loss++){
                    uint d = LightRegion.SubtractSaturated(la, loss);
                    for (int c = 0; c < 4; c++){
                        int x = (int)(la >> (8 * c)) & 0xF;
                        Assert.AreEqual(System.Math.Max(0, x - loss), (int)(d >> (8 * c)) & 0xFF, $"{x} − {loss} in lane {c}");
                    }
                }
            }
        }

        [Test]
        public void Lanes_Step_EqualsScalarCombine(){
            var random = new Unity.Mathematics.Random(0x20_02u);
            ushort[] edges = { 0, 0x0001, 0x1111, 0xF000, 0xFFFF, 0x0FFF, 0xEEEE, 0x2222, 0xF00F, 0x1F1F };
            for (int i = 0; i < 100_000; i++){
                ushort current = i < edges.Length * edges.Length ? edges[i % edges.Length] : (ushort)random.NextUInt(0x10000);
                ushort from = i < edges.Length * edges.Length ? edges[i / edges.Length] : (ushort)random.NextUInt(0x10000);
                for (int att = 0; att < LightRegion.Opaque; att++)
                for (int downward = 0; downward <= 1; downward++){
                    ushort expected = LightRegion.Combine(current, from, att, downward);
                    ushort actual = Step(current, from, att, downward);
                    if (actual != expected)
                        Assert.Fail($"current {current:X4} from {from:X4} att {att} downward {downward}: " +
                                    $"expected {expected:X4} but was {actual:X4}");
                }
            }
        }

        /// <summary>The byte-lane step as <see cref="LightRegion.Propagate" /> composes it.</summary>
        private static ushort Step(ushort current, ushort from, int att, int downward){
            uint lanes = LightRegion.Spread(from);
            uint clear = LightRegion.SubtractSaturated(lanes, 1);
            const uint sky = 0x0Fu << 24;
            if (downward != 0 && (lanes & sky) == sky) clear |= sky;
            uint candidate = att == 0 ? clear : LightRegion.SubtractSaturated(lanes, 1 + att);
            uint now = LightRegion.Spread(current);
            uint higher = LightRegion.GreaterLanes(candidate, now);
            return LightRegion.Pack(now ^ ((candidate ^ now) & higher));
        }
    }
}
