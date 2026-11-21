using System;
using PawnForge.Core.Evaluation;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Width-parity gate for the accumulator's vector kernels. The production
/// dispatch picks the widest kernel the host supports; the 512-bit arm must
/// produce the SAME i16 lane values as the portable arm, including at widths
/// where the tail does not fill a whole 512-bit vector (144, 160).
///
/// <para>On a host without AVX-512 the <see cref="System.Runtime.Intrinsics.Vector512{T}"/>
/// operations run the runtime's software fallback, which is exactly what makes
/// this test useful there: the lane math, bounds and tail copy are exercised
/// even though the hardware path cannot run. On an AVX-512 host the same test
/// exercises the real instructions.</para>
/// </summary>
public class NnueVectorOpsTests
{
    private static short[] RandomShorts(Random random, int length)
    {
        var data = new short[length];
        for (int i = 0; i < length; i++)
            data[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        return data;
    }

    private static void AssertSame(short[] expected, short[] actual, string context)
    {
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{context}: 512-bit kernel differs from the portable kernel");
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(144)]
    [InlineData(160)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void EveryShape_MatchesThePortableKernel(int l1)
    {
        var random = new Random(l1);
        const int rows = 8;
        short[] parent = RandomShorts(random, l1);
        short[] weights = RandomShorts(random, rows * l1);
        short[] v = new short[l1];
        short[] w512 = new short[l1];

        // quiet: 1 add / 1 subtract
        NnueVectorOps.Fused1Add1SubV(ref parent[0], ref v[0], ref weights[0], 1 * l1, 2 * l1, l1);
        NnueVectorOps.Fused1Add1Sub512(ref parent[0], ref w512[0], ref weights[0], 1 * l1, 2 * l1, l1);
        AssertSame(v, w512, $"1/1 at L1={l1}");

        // capture / promotion: 1 add / 2 subtracts
        Array.Clear(v);
        Array.Clear(w512);
        NnueVectorOps.Fused1Add2SubV(ref parent[0], ref v[0], ref weights[0],
            1 * l1, 2 * l1, 3 * l1, l1);
        NnueVectorOps.Fused1Add2Sub512(ref parent[0], ref w512[0], ref weights[0],
            1 * l1, 2 * l1, 3 * l1, l1);
        AssertSame(v, w512, $"1/2 at L1={l1}");

        // castling: 2 adds / 2 subtracts
        Array.Clear(v);
        Array.Clear(w512);
        NnueVectorOps.Fused2Add2SubV(ref parent[0], ref v[0], ref weights[0],
            1 * l1, 2 * l1, 3 * l1, 4 * l1, l1);
        NnueVectorOps.Fused2Add2Sub512(ref parent[0], ref w512[0], ref weights[0],
            1 * l1, 2 * l1, 3 * l1, 4 * l1, l1);
        AssertSame(v, w512, $"2/2 at L1={l1}");

        // generic row list
        ReadOnlySpan<int> adds = stackalloc int[] { 1, 2 };
        ReadOnlySpan<int> subs = stackalloc int[] { 3, 4, 5 };
        Array.Clear(v);
        Array.Clear(w512);
        NnueVectorOps.FusedGenericV(ref parent[0], ref v[0], ref weights[0], adds, subs, l1);
        NnueVectorOps.FusedGeneric512(ref parent[0], ref w512[0], ref weights[0], adds, subs, l1);
        AssertSame(v, w512, $"generic at L1={l1}");
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(144)]
    [InlineData(160)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void Refresh_MatchesThePortableKernel(int l1)
    {
        var random = new Random(l1 ^ 0x5A5A);
        const int rows = 8;
        short[] bias = RandomShorts(random, l1);
        short[] weights = RandomShorts(random, rows * l1);
        ReadOnlySpan<int> features = stackalloc int[] { 1, 3, 5, 7 };
        short[] v = new short[l1];
        short[] w512 = new short[l1];

        NnueVectorOps.RefreshV(ref v[0], ref bias[0], ref weights[0], features, features.Length, l1);
        NnueVectorOps.Refresh512(ref w512[0], ref bias[0], ref weights[0], features, features.Length, l1);
        AssertSame(v, w512, $"refresh at L1={l1}");
    }

    /// <summary>A scalar oracle independent of both vector arms: the fused
    /// result really is parent + add - sub, lane by lane.</summary>
    [Theory]
    [InlineData(128)]
    [InlineData(144)]
    [InlineData(512)]
    public void FusedQuiet_EqualsTheScalarOracle(int l1)
    {
        var random = new Random(l1);
        short[] parent = RandomShorts(random, l1);
        short[] weights = RandomShorts(random, 4 * l1);
        short[] child = new short[l1];
        const int addRow = 1, subRow = 2;

        NnueVectorOps.Fused1Add1Sub512(ref parent[0], ref child[0], ref weights[0],
            addRow * l1, subRow * l1, l1);

        for (int i = 0; i < l1; i++)
        {
            short expected = unchecked((short)(parent[i] + weights[addRow * l1 + i] - weights[subRow * l1 + i]));
            Assert.Equal(expected, child[i]);
        }
    }
}
