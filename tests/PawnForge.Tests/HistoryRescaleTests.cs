using PawnForge.Core.Board;
using PawnForge.Core.Search;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// HIST1 unit tests: gravity-bounded
/// history updates, linear bonuses/maluses, continuous clamped LMR history term.
/// </summary>
public class HistoryRescaleTests
{
    // ─────────────────────────────────────────────────────────────
    // 1. Gravity bounds: any state within (−MAX, MAX) stays there
    // under arbitrary legal update sequences (property test)
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(8033, 207, 1202, 200, 1255)]   // butterfly
    [InlineData(8003, 204, 1229, 205, 1067)]   // continuation
    [InlineData(8488, 192, 1250, 211, 1185)]   // capture
    public void Gravity_BoundsHold_UnderRandomUpdateSequences(int max, int bScale, int bMax, int mScale, int mMax)
    {
        var rng = new Random(42);
        int h = 0;
        for (int i = 0; i < 10_000; i++)
        {
            int depth = rng.Next(1, 30);
            bool positive = rng.Next(2) == 1;
            int u = positive
                ? Math.Min(bScale * depth, bMax)
                : -Math.Min(mScale * depth, mMax);

            // Seed with extreme states periodically to stress the boundary
            if (i % 1000 == 0) h = (i / 1000) % 2 == 0 ? max - 1 : -max + 1;

            h = SearchHeuristics.Gravity(h, u, max);
            // Closed bound: integer truncation lets the iteration REACH exactly ±max
            // (from max, h·u/max = u exactly → new = max), never exceed it.
            Assert.InRange(h, -max, max);
        }
    }

    [Fact]
    public void Gravity_SignMirror_AtZero()
    {
        // At h = 0, bonus +u and malus −u must produce exact mirrors
        Assert.Equal(SearchHeuristics.Gravity(0, 1000, 8033), -SearchHeuristics.Gravity(0, -1000, 8033));
        Assert.Equal(1000, SearchHeuristics.Gravity(0, 1000, 8033));
    }

    [Fact]
    public void Gravity_RepeatedBonuses_SaturateAtMax_NeverExceed()
    {
        // The gravity fixed point under constant positive updates IS the cap:
        // h·u/MAX = u ⟺ h = MAX. Saturates at (not below!) MAX, never beyond.
        int h = 0;
        for (int i = 0; i < 200; i++)
            h = SearchHeuristics.Gravity(h, SearchHeuristics.ButterflyBonusMax, SearchHeuristics.ButterflyMax);

        Assert.InRange(h, 7000, SearchHeuristics.ButterflyMax);
        Assert.True(Math.Abs(h) <= SearchHeuristics.ButterflyMax);
    }

    // ─────────────────────────────────────────────────────────────
    // 2. Update helpers on a real ThreadData
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateHistory_BonusAndMalus_StayBounded()
    {
        var td = new ThreadData(0);
        var m = new Move(12, 30); // a2->e4-ish encoding, only From/To matter here
        var bad = new Move(50, 51);

        for (int i = 0; i < 5_000; i++)
        {
            SearchHeuristics.UpdateHistory(td, side: 0, m, depth: 30, badMoves: new[] { bad });
            Assert.InRange(td.ButterflyHistory[0][m.From][m.To], -SearchHeuristics.ButterflyMax, SearchHeuristics.ButterflyMax);
            Assert.InRange(td.ButterflyHistory[0][bad.From][bad.To], -SearchHeuristics.ButterflyMax, SearchHeuristics.ButterflyMax);
        }

        // The cutoff cell must saturate high, the punished cell low
        Assert.True(td.ButterflyHistory[0][m.From][m.To] > 1000, $"cutoff cell saturated low: {td.ButterflyHistory[0][m.From][m.To]}");
        Assert.True(td.ButterflyHistory[0][bad.From][bad.To] < -1000, $"punished cell stayed high: {td.ButterflyHistory[0][bad.From][bad.To]}");
    }

    [Fact]
    public void UpdateContHistory_Malus_NowExists()
    {
        int[][] table = new int[64][];
        for (int i = 0; i < 64; i++) table[i] = new int[64];
        var m = new Move(8, 24);

        SearchHeuristics.UpdateContHistory(table, m, depth: 10, bonus: true);
        int afterBonus = table[m.From][m.To];
        Assert.True(afterBonus > 0);

        SearchHeuristics.UpdateContHistory(table, m, depth: 10, bonus: false);
        int afterMalus = table[m.From][m.To];
        Assert.True(afterMalus < afterBonus, "malus must subtract (bonus-only scheme is gone)");
        Assert.InRange(afterMalus, -SearchHeuristics.ContMax, SearchHeuristics.ContMax);
    }

    [Fact]
    public void UpdateCaptureHistory_GravityBounded()
    {
        var td = new ThreadData(0);
        for (int i = 0; i < 5_000; i++)
            SearchHeuristics.UpdateCaptureHistory(td, pieceIdx: 1, to: 40, victimIdx: 3, depth: 25);

        Assert.InRange(td.CaptureHistory[1][40][3], 0, SearchHeuristics.CaptureMax);
    }

    // ─────────────────────────────────────────────────────────────
    // 3. LMR history term: continuous, clamped to ±2 plies
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void LmrHistoryTerm_ClampedToBounds()
    {
        // Old-scale garbage input (±200,000) must clamp — this is the whole point
        Assert.Equal(2048, SearchHeuristics.LmrHistoryTerm1024(200_000, quiet: true));
        Assert.Equal(-2048, SearchHeuristics.LmrHistoryTerm1024(-200_000, quiet: true));

        // Exact divisions
        Assert.Equal(1024, SearchHeuristics.LmrHistoryTerm1024(3419, quiet: true));      // 1 ply
        Assert.Equal(-1024, SearchHeuristics.LmrHistoryTerm1024(-3419, quiet: true));
        Assert.Equal(0, SearchHeuristics.LmrHistoryTerm1024(3418, quiet: true));          // sub-ply truncation

        // In-range combined scores: |term| ≤ 2 plies always
        Assert.InRange(SearchHeuristics.LmrHistoryTerm1024(8033, quiet: true), -2048, 2048);
        Assert.InRange(SearchHeuristics.LmrHistoryTerm1024(-8033, quiet: true), -2048, 2048);
    }

    [Fact]
    public void LmrHistoryTerm_ModerateMagnitudes_AreSubPly()
    {
        // Moderately hot cells (a handful of updates) sit in the hundreds-to-low-thousands
        // range: the term is sub-ply to 1 ply, CONTINUOUS — the old whole-ply
        // `history/8192` term returned 0 for all of these magnitudes
        Assert.Equal(0, SearchHeuristics.LmrHistoryTerm1024(1500, quiet: true));
        Assert.Equal(1024, SearchHeuristics.LmrHistoryTerm1024(5000, quiet: true));
        Assert.Equal(-1024, SearchHeuristics.LmrHistoryTerm1024(-4000, quiet: true));
    }
}
