namespace PawnForge.Tests;

using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// M3 gate — the frozen v5 quantization chain (NnueQuantV5).
///
/// Pure arithmetic: no positions, no FENs, no oracle gate needed. The gates
/// are:
/// * SCReLU bounds [0, 254] over the full i16 accumulator range;
/// * the s*s &lt;= 65025 &lt; 2^16 property that makes the SIMD square legal;
/// * the unsigned-reinterpret LOGICAL shift rule (the arithmetic variant
/// would subtract 256 for every activation above 181);
/// * the i64 overflow bound: |out| &lt;= ~1.7e10 and cp &lt;= ~2.6e7 after
/// the &gt;&gt;20 — both past the i32 multiply, so the wide arithmetic is
/// structural, not defensive;
/// * the frozen constants (K1, QB1, OutputGain) match the derivation the
/// exporter uses — these are the values the bit-exactness check re-derives.
/// </summary>
public class NnueQuantV5Tests
{
    [Fact]
    public void Screlu_Bounds_AreZeroTo254_OverFullI16Range()
    {
        for (int a = short.MinValue; a <= short.MaxValue; a++)
        {
            int s = NnueQuantV5.Screlu(a);
            Assert.InRange(s, 0, 254);
        }
    }

    [Fact]
    public void Screlu_ClampIsTheSaturate_MatchesTheDocumentedFormula()
    {
        // clamp(a, 0, 255)^2 >> 8, exactly — and no input above the clamp can
        // change the result (the clamp IS the saturate, step 1 of the derivation).
        for (int a = short.MinValue; a <= short.MaxValue; a++)
        {
            int clamped = a < 0 ? 0 : (a > 255 ? 255 : a);
            Assert.Equal((clamped * clamped) >> 8, NnueQuantV5.Screlu(a));
        }
        Assert.Equal(NnueQuantV5.Screlu(255), NnueQuantV5.Screlu(32767));
        Assert.Equal(NnueQuantV5.Screlu(-255), NnueQuantV5.Screlu(short.MinValue));
        Assert.Equal(0, NnueQuantV5.Screlu(-1));
        Assert.Equal(254, NnueQuantV5.Screlu(255)); // 65025 >> 8 = 254 (one under GAIN)
    }

    [Fact]
    public void SquaredFitsIn16Bits_TheSimdSquareIsLegal()
    {
        // The vectorised head computes s*s in i16 lanes. That is only exact
        // because s <= QA = 255, so s*s <= 65025 < 2^16 — for EVERY value the
        // clamp can produce. If QA ever changed, this test is what fails first.
        for (int s = 0; s <= NnueQuantV5.QA; s++)
            Assert.True(s * s <= 65025, $"s={s}: s*s = {s * s} overflows 16 bits");
        // And the clamped square's low 16 bits ARE the square itself.
        for (int s = 0; s <= NnueQuantV5.QA; s++)
            Assert.Equal(s * s, (short)(s * s) & 0xFFFF);
    }

    [Fact]
    public void SquaredShift_MustBeLogical_NotArithmetic()
    {
        // For s >= 182 the square has bit 15 set (33124+), so as an i16 it is
        // NEGATIVE. An ARITHMETIC shift right by 8 on that signed value subtracts
        // 256 from every one of them (the F10 class). The correct operation — and
        // what the SIMD path implements via an unsigned reinterpret — is the
        // LOGICAL shift of the UNSIGNED square. This test pins the scalar chain
        // to the value the vector path must reproduce, and asserts the two
        // readings actually differ, so the test cannot rot into a tautology.
        bool differsSomewhere = false;
        for (int s = 0; s <= NnueQuantV5.QA; s++)
        {
            ushort square = (ushort)(s * s);            // s*s IS the u16 square
            int logical = square >> NnueQuantV5.ScreluShift;
            int arithmetic = ((short)square) >> NnueQuantV5.ScreluShift; // the WRONG reading
            Assert.Equal(logical, NnueQuantV5.Screlu(s));
            if (logical != arithmetic)
                differsSomewhere = true;
        }
        Assert.True(differsSomewhere, "the arithmetic shift agreed with the logical one — the guard is vacuous");
        // Concrete instance of the trap: s = 255. (65025 has bit 15 set, so the
        // i16 reinterpretation is -511; the arithmetic shift then gives -2.)
        Assert.Equal(254, NnueQuantV5.Screlu(255));
        Assert.Equal(-511, unchecked((short)unchecked((ushort)65025)));
        Assert.Equal(-2, unchecked((short)unchecked((ushort)65025)) >> 8);
    }

    [Fact]
    public void Output_AtFrozenZeroShift_IsIdentityClamp()
    {
        // At OutputShift = 0 the shift is a no-op and the clamp cannot bind
        // (s2 <= 254 < OutputCap = 255) — asserted over the full SCReLU range,
        // so the scalar chain and (from M6) the vectorised head consume the
        // same activation.
        for (int a = short.MinValue; a <= short.MaxValue; a++)
            Assert.Equal(NnueQuantV5.Screlu(a), NnueQuantV5.Output(NnueQuantV5.Screlu(a)));
    }

    [Fact]
    public void ToCp_UsesThe64BitMultiply_AtTheOverflowBound()
    {
        // |out| at the legal extreme: OutputCap * i16max * MaxL1 + |bias| =
        // 255 * 32767 * 2048 + 32767 = 1.71713e10 — PAST int.MaxValue (2.15e9),
        // so an int*int multiply in ToCp would wrap. The >>20 brings cp back to
        // ~2.6e7, which fits int — assert the exact expected value, not a range.
        long extreme = 255L * 32767 * 2048 + 32767;
        Assert.True(extreme > int.MaxValue, "the fixture must stay above the i32 range or it proves nothing");
        long expected = (extreme * NnueQuantV5.EvalScaleMul) >> NnueQuantV5.EvalScaleShift;
        Assert.Equal(expected, NnueQuantV5.ToCp(extreme));
        Assert.InRange(Math.Abs(NnueQuantV5.ToCp(extreme)), 25_000_000, 27_000_000);
        // The chain is NOT odd-symmetric: >> on a negative is the ARITHMETIC
        // shift (rounds toward -inf), so ToCp(-x) = ((-x*1600)>>20), not
        // -ToCp(x), whenever the discarded low bits are nonzero. Both sides of
        // the bit-exactness check run the SAME operation, so consistency is what
        // matters — pin the exact value.
        Assert.Equal((-extreme * NnueQuantV5.EvalScaleMul) >> NnueQuantV5.EvalScaleShift,
                     NnueQuantV5.ToCp(-extreme));
        Assert.NotEqual(-expected, NnueQuantV5.ToCp(-extreme));
    }

    [Fact]
    public void ToCp_MatchesTheFormula_OnSignedExtremes()
    {
        long[] cases = { 0, 1, -1, 8_100_000 /* a healthy net's |out| bound: |W1| <= 31.8 */,
                         int.MaxValue, (long)int.MaxValue * 2, -8_100_000 };
        foreach (long fc1 in cases)
            Assert.Equal((int)((fc1 * NnueQuantV5.EvalScaleMul) >> NnueQuantV5.EvalScaleShift),
                         NnueQuantV5.ToCp(fc1));
    }

    [Fact]
    public void FrozenConstants_MatchTheDocumentedDerivation()
    {
        // These are the values the trainer's exporter and the bit-exactness check derive
        // from the SAME formulas — if the engine's copies drift, the formats
        // silently disagree. (Migration doc: the header pins them; 
        // documents the derivation.)
        Assert.Equal(255, NnueQuantV5.QA);
        Assert.Equal(8, NnueQuantV5.ScreluShift);
        Assert.Equal(0, NnueQuantV5.OutputShift);
        Assert.Equal(255, NnueQuantV5.OutputCap);
        Assert.Equal(1600, NnueQuantV5.EvalScaleMul);
        Assert.Equal(20, NnueQuantV5.EvalScaleShift);
        Assert.Equal(400, NnueQuantV5.ScoreScale);

        Assert.Equal(254.00390625, NnueQuantV5.OutputGain, precision: 9);
        Assert.Equal(262144.0, NnueQuantV5.QB1, precision: 6);
        Assert.Equal(1032.047120, NnueQuantV5.K1, precision: 5);
        Assert.Equal(32767.0 / NnueQuantV5.K1, NnueQuantV5.MaxAbsFc1Weight, precision: 9);
        Assert.InRange(NnueQuantV5.MaxAbsFc1Weight, 31.7, 31.8); // the documented 31.75
    }

    [Fact]
    public void HealthyNetBound_StayFarInsideI64()
    {
        // A real net's |W1| <= MaxAbsFc1Weight = 31.75 raw, so the head sum is
        // bounded by 254 * 32767 * L1 * (31.75 / 32767) = 254 * 31.75 * L1 — at
        // L1 2048 that is 1.62e7, comfortably inside i32 EVEN, let alone i64.
        long healthyBound = (long)(NnueQuantV5.OutputCap * NnueQuantV5.MaxAbsFc1Weight) * 2048;
        Assert.True(healthyBound < int.MaxValue);
        Assert.Equal(0, NnueQuantV5.ToCp(0));
    }
}
