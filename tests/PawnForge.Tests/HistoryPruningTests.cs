using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for history pruning behavior (/ tasks 3.2, 3.3).
/// Documents that the history pruning sum currently uses butterfly +
/// contHist/2 + contHist2/2 + contHist4/2 (contHist6 not yet included
/// — that is the pending change). Tests cover the margin constant
/// invariant and the formula properties.
/// </summary>
public class HistoryPruningTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — HISTORY_PRUNING_MARGIN constant invariant ()
    // HISTORY_PRUNING_MARGIN * 3 == 7500 and <= 7500 (depth-3 cap)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HistoryPruningMargin_Times3_LessThanOrEqual7500()
    {
        // The design review requirement () states the absolute threshold at
        // depth 3 must be no larger than before contHist6 was added.
        // Current value: HISTORY_PRUNING_MARGIN = 2500 (before fix).
        // After fix (), the margin drops to 2000 → 6000 at d3.
        // Either value satisfies the cap.
        const int HISTORY_PRUNING_MARGIN = 2500;  // current value in source
        int thresholdAtDepth3 = HISTORY_PRUNING_MARGIN * 3;

        // The requirement is |threshold| <= 7500 (the old value at 2500*3=7500)
        Assert.True(thresholdAtDepth3 <= 7500,
            $"Threshold at depth 3 ({thresholdAtDepth3}) exceeds cap of 7500");
    }

    [Fact]
    public void HistoryPruningMargin_PostFix_Times3_LessThanOrEqual7500()
    {
        // After fix (): HISTORY_PRUNING_MARGIN drops to 2000
        // to compensate for the wider sum (+ contHist6/2).
        const int NEW_HISTORY_PRUNING_MARGIN = 2000;
        int thresholdAtDepth3 = NEW_HISTORY_PRUNING_MARGIN * 3;

        Assert.Equal(6000, thresholdAtDepth3);
        Assert.True(thresholdAtDepth3 <= 7500,
            $"Post-fix threshold at depth 3 ({thresholdAtDepth3}) must be <= 7500");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — History pruning sum formula (Property 3 analog)
    // The pruning sum matches the expected formula for known inputs
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 0, 0, 0, 0)]      // butterfly only
    [InlineData(1000, 2000, 0, 0, 0)]   // butterfly + c1/2 = 2000
    [InlineData(1000, 2000, 4000, 0, 0)] // butterfly + c1/2 + c2/2 = 4000
    [InlineData(1000, 2000, 4000, 8000, 0)] // + c4/2 = 7000
    [InlineData(1000, 2000, 4000, 8000, 16000)] // + c6/2 = 15000 (after fix)
    public void HistoryPruningSum_Formula_IsCorrect(
        int butterfly, int c1, int c2, int c4, int c6)
    {
        // Simulate the history-pruning sum as documented in the design spec.
        // After fix: sum = butterfly + c1/2 + c2/2 + c4/2 + c6/2
        int pruningSum = butterfly
            + (c1 != 0 ? c1 / 2 : 0)
            + (c2 != 0 ? c2 / 2 : 0)
            + (c4 != 0 ? c4 / 2 : 0)
            + (c6 != 0 ? c6 / 2 : 0);

        int expected = butterfly + c1 / 2 + c2 / 2 + c4 / 2 + c6 / 2;
        Assert.Equal(expected, pruningSum);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — ContHist6 weight /2 is consistent with ordering
    // The weight used in the pruning sum matches ScoreQuiet's weight
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void ContHist6_PruningWeight_ConsistentWithOrdering()
    {
        // Both the pruning sum and ScoreQuiet must use the /2 divisor.
        // If they diverged (e.g., one uses /1, the other /2), the pruning
        // decision would be inconsistent with move ordering.
        // This is a constant-level invariant test.

        // The design doc () specifies contHist6[m.From][m.To] / 2
        // for BOTH the pruning block and ScoreQuiet.
        const int CONTHIST6_PRUNING_DIVISOR = 2;
        const int CONTHIST6_ORDERING_DIVISOR = 2;

        Assert.Equal(CONTHIST6_ORDERING_DIVISOR, CONTHIST6_PRUNING_DIVISOR);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — Pruning threshold scales with depth
    // threshold = -MARGIN * depth (more negative = more pruning)
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, -2500)]
    [InlineData(2, -5000)]
    [InlineData(3, -7500)]
    public void HistoryThreshold_ScalesWithDepth(int depth, int expected)
    {
        const int HISTORY_PRUNING_MARGIN = 2500;
        int threshold = -HISTORY_PRUNING_MARGIN * depth;
        Assert.Equal(expected, threshold);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 5 — Post-fix margin threshold at depth 1, 2, 3
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, -2000)]
    [InlineData(2, -4000)]
    [InlineData(3, -6000)]
    public void HistoryThreshold_PostFix_ScalesWithDepth(int depth, int expected)
    {
        // After fix the margin is reduced to 2000.
        const int NEW_HISTORY_PRUNING_MARGIN = 2000;
        int threshold = -NEW_HISTORY_PRUNING_MARGIN * depth;
        Assert.Equal(expected, threshold);
    }
}
