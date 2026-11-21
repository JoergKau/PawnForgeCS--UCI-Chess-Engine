namespace PawnForge.Tests;

using System.Collections.Generic;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// The runtime king-region counter's gate (region-analysis pass —
/// the diagnostic that measures the REAL-GAME region distribution the sampled
/// pools only estimate).
///
/// What must be pinned:
/// 1. OPT-IN: counting is OFF by default — a normal search (and every perf
/// measurement run) must run with the hook as a
/// single predictable branch and ZERO counted events, or the perf baseline
/// silently stops describing the shipped engine.
/// 2. ONE EVENT PER POV REFRESH: a cold start (<c>SetPosition</c>) refreshes
/// BOTH POVs, and each refresh must land exactly 1 event on the region its
/// own king's header-table entry names — the same definition the training-side
/// counters use for pool POV events, so the two
/// histograms are directly comparable.
/// 3. DISABLE STOPS THE STREAM: after <c>Disable</c>, further refreshes count
/// nothing (a measurement that cannot be turned off would contaminate every
/// later bench).
///
/// Expected regions are read from the feature set's own
/// <see cref="NnueFeatureSet2.Region"/> — the same lookup the counter records
/// — over the mechanically oracle-verified FEN list <see cref="NnueTestFens"/>.
/// The counter therefore adds no new FEN constants (RULE HARDENING) and pins
/// that the counted region is THE region the accumulator actually refreshes.
/// </summary>
public class NnueRegionCountersTests
{
    /// <summary>Every test starts and ends with counting off and the counters
    /// clear, so a failed assertion cannot leak state into the next test
    /// (xunit runs the methods of one class sequentially, but the class must
    /// still be hermetic against its own failures).</summary>
    public NnueRegionCountersTests()
    {
        NnueRegionCounters.Disable();
        NnueRegionCounters.Reset();
    }

    private static NnueNetV5 FillerNet() => NnueNetV5.FromFiller(128);

    [Fact]
    public void Counting_IsOffByDefault_AndSetupCountsNothing()
    {
        var net = FillerNet();
        var acc = new NnueAccumulatorV5(net);
        Assert.False(NnueRegionCounters.Enabled, "counting must be opt-in, not a default");

        foreach (string fen in NnueTestFens.All)
        {
            var pos = Position.FromFEN(fen);
            acc.SetPosition(pos);
            acc.MakeNullMove(); // exercise the push path too — still no events
            acc.UnmakeNullMove();
            var (counts, total) = NnueRegionCounters.Snapshot(net.RegionCount);
            Assert.Equal(0, total);
            Assert.All(counts, c => Assert.Equal(0L, c));
        }
    }

    [Fact]
    public void Reset_ThenEnable_CountsExactlyOneEventPerPovRefresh()
    {
        var net = FillerNet();
        var features = net.Features;
        var acc = new NnueAccumulatorV5(net);

        foreach (string fen in NnueTestFens.All)
        {
            var pos = Position.FromFEN(fen);
            NnueRegionCounters.Reset();
            NnueRegionCounters.Enable();
            acc.SetPosition(pos);

            var (counts, total) = NnueRegionCounters.Snapshot(net.RegionCount);

            // A cold start = exactly two refreshes (white POV + black POV), each
            // landing on ITS OWN king's region. The histogram is per-REGION only
            // (the POV is not recorded), so both POVs landing in one region sum
            // into a single cell — assert the SUM, and that each POV's region
            // got at least its own event.
            Assert.Equal(2, total);
            if (features.Region(pos, NnueFeatureSet2.WhitePov)
                == features.Region(pos, NnueFeatureSet2.BlackPov))
            {
                // Both POVs land in ONE region — the single cell carries both.
                Assert.Equal(2, counts[features.Region(pos, NnueFeatureSet2.WhitePov)]);
            }
            else
            {
                Assert.Equal(1, counts[features.Region(pos, NnueFeatureSet2.WhitePov)]);
                Assert.Equal(1, counts[features.Region(pos, NnueFeatureSet2.BlackPov)]);
            }
        }
    }

    [Fact]
    public void Disable_StopsCounting_WhileRefreshesContinue()
    {
        var net = FillerNet();
        var acc = new NnueAccumulatorV5(net);
        var pos = Position.FromFEN(NnueTestFens.StartPos);

        NnueRegionCounters.Reset();
        NnueRegionCounters.Enable();
        acc.SetPosition(pos);
        long afterEnable = NnueRegionCounters.Snapshot(net.RegionCount).Total;
        Assert.Equal(2, afterEnable);

        NnueRegionCounters.Disable();
        acc.ForceRefresh(pos, NnueFeatureSet2.WhitePov);
        acc.ForceRefresh(pos, NnueFeatureSet2.BlackPov);
        long afterDisable = NnueRegionCounters.Snapshot(net.RegionCount).Total;

        Assert.Equal(afterEnable, afterDisable); // nothing new after Disable
    }

    [Fact]
    public void RandomWalk_RegionsFollowTheKing()
    {
        // A short random walk that crosses king moves: every king move makes
        // one POV stale, so the lazy refresh at the next read records exactly
        // the region of the NEW king square. The identity that makes this a
        // real assertion: sum of events == number of cold starts + number of
        // lazy refreshes — measured here against the walk's own refresh calls
        // by comparing the total against an independently driven reference.
        var net = FillerNet();
        var features = net.Features;
        var acc = new NnueAccumulatorV5(net);
        var random = new System.Random(7);
        Span<Move> moves = stackalloc Move[256];

        var expected = new Dictionary<(int pov, int region), int>();
        var increment = (int pov, int region) =>
        {
            expected.TryGetValue((pov, region), out int n);
            expected[(pov, region)] = n + 1;
        };

        var pos = Position.FromFEN(NnueTestFens.StartPos);
        NnueRegionCounters.Reset();
        NnueRegionCounters.Enable();

        // Reference driver: replicate what a real search's READS cause the
        // accumulator to do — the refresh is LAZY (on GetAccumulator), so the
        // reference must model the same reads, not the makes. Cold start = 2
        // events; a read while stale = 1 event for that POV.
        bool[] stale = { false, false };
        void Read(Position p)
        {
            for (int pov = 0; pov < 2; pov++)
            {
                // The read itself: exactly what NnueEvaluator.Evaluate does —
                // and exactly what clears the accumulator's lazy-stale flag,
                // so the reference must clear its own flag in the same breath.
                acc.GetAccumulator(p, pov);
                if (stale[pov])
                {
                    stale[pov] = false;
                    increment(pov, features.Region(p, pov));
                }
            }
        }

        acc.SetPosition(pos); // the cold start: the counter sees both refreshes
        increment(0, features.Region(pos, 0)); // the reference sees them too
        increment(1, features.Region(pos, 1));

        for (int step = 0; step < 60; step++)
        {
            int count = pos.GenerateLegalMoves(moves);
            if (count == 0)
                break;
            Move move = moves[random.Next(count)];

            // Stale flags BEFORE the make (the moving king makes its POV stale;
            // the seam contract needs the board make BEFORE acc.MakeMove).
            if (pos.Mailbox[move.From] is Piece.WK)
                stale[0] = true;
            if (pos.Mailbox[move.From] is Piece.BK)
                stale[1] = true;

            pos.MakePublicMove(move);
            acc.MakeMove(pos);
            Read(pos); // the reads inside Evaluate — the lazy refresh trigger
        }

        var (counts, total) = NnueRegionCounters.Snapshot(net.RegionCount);

        // The histogram is per-REGION only (the POV is not recorded), so the
        // reference must be aggregated the same way — both POVs landing in one
        // region sum into a single cell.
        var byRegion = new Dictionary<int, int>();
        int expectedTotal = 0;
        foreach (var ((pov, region), n) in expected)
        {
            byRegion.TryGetValue(region, out int have);
            byRegion[region] = have + n;
            expectedTotal += n;
        }
        foreach (var (region, n) in byRegion)
            Assert.Equal(n, counts[region]);
        // And the TOTAL must match the reference exactly.
        Assert.Equal(expectedTotal, total);
    }
}
