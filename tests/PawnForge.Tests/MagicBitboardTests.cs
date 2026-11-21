using PawnForge.Core;
using PawnForge.Core.Board;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Magic bitboard validation: for many random occupancy patterns, the magic
/// lookup must EXACTLY match a slow ray-walk reference. Empty-board sweeps
/// do NOT catch occupancy-dependent hash collisions in plain magics — those
/// were the root cause of "king walks into attack" legality bugs.
/// </summary>
public class MagicBitboardTests
{
    private static ulong RookReference(int sq, ulong occ)
    {
        ulong attacks = 0UL;
        int r = sq / 8, f = sq % 8;
        int[] dr = { 1, -1, 0, 0 }, df = { 0, 0, 1, -1 };
        for (int d = 0; d < 4; d++)
        {
            int rr = r + dr[d], ff = f + df[d];
            while (rr is >= 0 and < 8 && ff is >= 0 and < 8)
            {
                int t = rr * 8 + ff;
                attacks |= 1UL << t;
                if ((occ & (1UL << t)) != 0) break;
                rr += dr[d]; ff += df[d];
            }
        }
        return attacks;
    }

    private static ulong BishopReference(int sq, ulong occ)
    {
        ulong attacks = 0UL;
        int r = sq / 8, f = sq % 8;
        int[] dr = { 1, 1, -1, -1 }, df = { 1, -1, 1, -1 };
        for (int d = 0; d < 4; d++)
        {
            int rr = r + dr[d], ff = f + df[d];
            while (rr is >= 0 and < 8 && ff is >= 0 and < 8)
            {
                int t = rr * 8 + ff;
                attacks |= 1UL << t;
                if ((occ & (1UL << t)) != 0) break;
                rr += dr[d]; ff += df[d];
            }
        }
        return attacks;
    }

    [Fact]
    public void RookMagics_MatchReference_UnderRandomOccupancies()
    {
        var rng = new Random(2026);
        int failures = 0;
        for (int trial = 0; trial < 1000 && failures < 5; trial++)
        {
            ulong occ = 0UL;
            int pieces = rng.Next(2, 14);
            for (int p = 0; p < pieces; p++) occ |= 1UL << rng.Next(64);

            for (int sq = 0; sq < 64; sq++)
            {
                ulong expected = RookReference(sq, occ & ~(1UL << sq));
                ulong actual = MagicBitboards.RookAttacks(sq, occ & ~(1UL << sq));
                if (expected != actual)
                {
                    lock (TestHelper.MagicFailures) TestHelper.MagicFailures.Add($"ROOK sq={sq} occ=0x{occ:X16}: expected 0x{expected:X16}, got 0x{actual:X16}");
                    failures++;
                    break;
                }
            }
        }
        Assert.Equal(0, failures);
    }

    [Fact]
    public void BishopMagics_MatchReference_UnderRandomOccupancies()
    {
        var rng = new Random(2026);
        int failures = 0;
        for (int trial = 0; trial < 1000 && failures < 5; trial++)
        {
            ulong occ = 0UL;
            int pieces = rng.Next(2, 14);
            for (int p = 0; p < pieces; p++) occ |= 1UL << rng.Next(64);

            for (int sq = 0; sq < 64; sq++)
            {
                ulong expected = BishopReference(sq, occ & ~(1UL << sq));
                ulong actual = MagicBitboards.BishopAttacks(sq, occ & ~(1UL << sq));
                if (expected != actual)
                {
                    lock (TestHelper.MagicFailures) TestHelper.MagicFailures.Add($"BISHOP sq={sq} occ=0x{occ:X16}: expected 0x{expected:X16}, got 0x{actual:X16}");
                    failures++;
                    break;
                }
            }
        }
        Assert.Equal(0, failures);
    }
}
