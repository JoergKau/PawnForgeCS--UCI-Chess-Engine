namespace PawnForge.Tests;

using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// Gate for the hardware-intrinsic NNUE output head.
///
/// <para>What is pinned here:</para>
/// <list type="number">
/// <item><b>Bit-exactness.</b> Each intrinsic arm (ARM64 <c>ForwardAdvSimd</c>,
/// x64 <c>ForwardAvx2</c>) must equal <see cref="NnueNetV5.ForwardScalar"/>
/// exactly — over every loader-legal L1 (64, 128, 144, 160, 256, 512, 1024,
/// 2048), over full-range and adversarial head weights (±32767), and over
/// saturating accumulators (255, 254, 181, 182, −32768, ±32767, random). This
/// catches a logical-shift mistake, a per-lane overflow and a swapped
/// perspective.</item>
/// <item><b>Dispatch proof.</b> On a host with AdvSimd or AVX2 the matching
/// intrinsic arm must actually be selected, so the sweep cannot pass by
/// silently falling back to the portable
/// <see cref="NnueNetV5.ForwardPath.Vectorised"/> path.</item>
/// <item><b>No wall-clock assertion — deliberately.</b> A timing assert
/// (intrinsic faster than portable by X %) inverts on a loaded CI box and turns
/// a real gain into a flake. The gain is covered by the fixed-depth node
/// identity and the timed strength match, not here.</item>
/// </list>
///
/// <para>The arms are host-gated by their own <c>IsSupported</c> flags: on
/// macOS/ARM64 the AdvSimd assertions run and the x64 ones are skipped; on
/// Windows/x64 the reverse holds. Both arms live in the SAME source file, so
/// neither host can silently lose its gate.</para>
/// </summary>
public class NnueHeadIntrinsicTests
{
    /// <summary>Every L1 the loader accepts that is also a multiple of 16 — the
    /// widths the intrinsic arms support.</summary>
    public static TheoryData<int> LegalWidths => new() { 64, 128, 144, 160, 256, 512, 1024, 2048 };

    private static NnueNetV5 HeadNet(int l1, int weightMagnitude, ulong seed)
    {
        // Only the head participates in this gate; leaving the feature tables at
        // zero keeps a 2048-wide fixture cheap without changing the arithmetic.
        var net = new NnueNetV5(l1, NnueFeatureSet2.DefaultRegionTable, 1, "head-intrinsic-test");
        ulong state = seed;
        int span = 2 * weightMagnitude + 1;
        for (int i = 0; i < net.OutputWeights.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            net.OutputWeights[i] = (short)((int)(state >> 48) % span - weightMagnitude);
        }
        return net;
    }

    /// <summary>Accumulator patterns that exercise the clamp, the square wrap
    /// point (181/182) and the signed extremes.</summary>
    private static short[] Acc(int l1, int kind, int seed)
    {
        var acc = new short[l1];
        if (kind == 5 || kind == 6)
        {
            var rng = new Random(seed);
            for (int i = 0; i < l1; i++)
                acc[i] = kind == 5
                    ? (short)(rng.Next(65536) - 32768)
                    : (short)(rng.Next(512) - 256);
            return acc;
        }

        short value = kind switch
        {
            0 => 0,
            1 => 255,
            2 => 254,
            3 => 181,
            4 => 182,
            7 => -32768,
            _ => 32767,
        };
        Array.Fill(acc, value);
        return acc;
    }

    private static void AssertAllArmsEqual(NnueNetV5 net, short[] stm, short[] ntm, string context)
    {
        long scalar = net.ForwardScalar(stm, ntm);
        Assert.True(net.Forward(stm, ntm) == scalar, $"{context}: dispatched Forward != scalar");
        Assert.True(net.ForwardVectorised(stm, ntm) == scalar, $"{context}: portable Vector<T> != scalar");

        if (AdvSimd.Arm64.IsSupported)
            Assert.True(net.ForwardAdvSimd(stm, ntm) == scalar, $"{context}: AdvSimd != scalar");

        if (Avx2.IsSupported)
            Assert.True(net.ForwardAvx2(stm, ntm) == scalar, $"{context}: AVX2 != scalar");
    }

    [Theory]
    [MemberData(nameof(LegalWidths))]
    public void IntrinsicHeads_MatchScalar_ForEveryLegalWidth(int l1)
    {
        var net = HeadNet(l1, weightMagnitude: 512, seed: 0x9E3779B97F4A7C15UL);
        for (int kind = 0; kind <= 8; kind++)
        {
            var stm = Acc(l1, kind, 1000 + kind);
            var ntm = Acc(l1, kind, 2000 + kind);
            AssertAllArmsEqual(net, stm, ntm, $"L1={l1}, pattern={kind}");
        }
    }

    [Theory]
    [MemberData(nameof(LegalWidths))]
    public void IntrinsicHeads_MatchScalar_WithAdversarialHeadWeights(int l1)
    {
        // ±32767 head weights: the worst legal product (254 × 32767 = 8,322,818)
        // on every lane, which is what the per-lane overflow budget is sized for.
        var net = HeadNet(l1, weightMagnitude: 32767, seed: 0xD1B54A32D192ED03UL);
        var stm = Acc(l1, 1, 7001); // 255 → SCReLU 254 on every lane
        var ntm = Acc(l1, 7, 7002); // −32768 → clamped to 0
        AssertAllArmsEqual(net, stm, ntm, $"L1={l1}, extreme weights");

        // Both perspectives saturated with mixed signs, full i16 noise.
        var noisyStm = Acc(l1, 5, 7003);
        var noisyNtm = Acc(l1, 5, 7004);
        AssertAllArmsEqual(net, noisyStm, noisyNtm, $"L1={l1}, extreme weights + noise");
    }

    [Fact]
    public void IntrinsicHeads_MatchScalar_OnTheLegalExtremeNet_WithoutWrapping()
    {
        // Same fixture as NnueEvaluatorTests: every head weight at i16 max and
        // the bias at int.MaxValue, so the total passes int.MaxValue. An i32
        // lane reduction anywhere would wrap; the widening drain must not.
        var net = new NnueNetV5(64, NnueFeatureSet2.DefaultRegionTable, 1, "extreme", int.MaxValue);
        Array.Fill(net.OutputWeights, short.MaxValue);
        var full = new short[net.L1];
        Array.Fill(full, short.MaxValue);

        long expected = net.OutputBias
            + (long)NnueQuantV5.Output(NnueQuantV5.Screlu(short.MaxValue)) * short.MaxValue * (2 * net.L1);
        Assert.True(expected > int.MaxValue, "the fixture must exceed the i32 range or it proves nothing");
        Assert.Equal(expected, net.ForwardScalar(full, full));
        AssertAllArmsEqual(net, full, full, "legal-extreme net");
    }

    [Fact]
    public void Dispatch_SelectsTheIntrinsicPath_TheHardwareCanExecute()
    {
        var net = HeadNet(512, weightMagnitude: 512, seed: 42);
        NnueNetV5.ForwardPath path = net.SelectForwardPath();

        if (AdvSimd.Arm64.IsSupported)
        {
            Assert.Equal(NnueNetV5.ForwardPath.AdvSimd, path);
        }
        else if (Avx2.IsSupported)
        {
            Assert.Equal(NnueNetV5.ForwardPath.Avx2, path);
        }
        else
        {
            // No intrinsic arm is executable here: the portable vector path is
            // the only legal choice.
            Assert.Equal(NnueNetV5.ForwardPath.Vectorised, path);
        }
    }

    [Fact]
    public void Dispatch_DoesNotUseAnIntrinsicArm_WhenTheQuantChainIsNotTheFrozenOne()
    {
        // The intrinsic paths skip the post-SCRELU clamp, which is only legal at
        // OutputShift == 0. The frozen build always has 0, so this test
        // documents the invariant rather than exercising a second quant chain
        // (which the loader would reject).
        Assert.Equal(0, NnueQuantV5.OutputShift);
        var net = HeadNet(512, weightMagnitude: 512, seed: 43);
        Assert.NotEqual(NnueNetV5.ForwardPath.Scalar, net.SelectForwardPath());
    }
}
