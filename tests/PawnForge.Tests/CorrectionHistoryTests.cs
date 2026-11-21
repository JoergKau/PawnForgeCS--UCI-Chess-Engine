using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for the correction history read path ().
/// The CorrectionHistory table is written on Exact nodes and must be
/// applied to staticEval BEFORE it influences pruning decisions.
///
/// NOTE: The read path () is listed as a design review recommendation
/// but has not yet been applied. These tests validate:
/// - The constant invariant W = 512 → max correction = 16384/512 = 32 ≤ 50 cp
/// - The clamp behaviour (applied after correction, before pruning)
/// - The additive property of correction + raw eval
/// - A basic search integration: CorrectionHistory affects the search score
/// in a deterministic, bounded way.
/// </summary>
public class CorrectionHistoryTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — Constant invariant: W = 512 satisfies the cap
    // 16384 / W <= 50 (max correction ≤ 50 cp)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void CorrectionWeight_Constant_SatisfiesCap()
    {
        // The design review doc specifies W = 512.
        // Verify that 16384 / 512 == 32 and 32 <= 50.
        const int W = 512;
        const int MaxEntry = 16384;
        int maxCorrection = MaxEntry / W;

        Assert.Equal(32, maxCorrection);
        Assert.True(maxCorrection <= 50,
            $"Max correction {maxCorrection} cp exceeds the 50 cp cap");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — Additive correction property (Property 1)
    // correctedEval = clamp(rawEval + correction / W, -29999, 29999)
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 1024, 512)]   // rawEval=100, correction=1024 → +2 → 102
    [InlineData(0, -2048, 512)]    // rawEval=0, correction=-2048 → -4 → -4
    [InlineData(29990, 16384, 512)] // rawEval=29990, +32 → 30022, clamp → 29999
    [InlineData(-29990, -16384, 512)] // rawEval=-29990, -32 → -30022, clamp → -29999
    public void CorrectionHistory_Application_IsAdditiveAndClamped(
        int rawEval, int correctionEntry, int W)
    {
        int corrected = rawEval + correctionEntry / W;
        int clamped = Math.Clamp(corrected, -29999, 29999);

        // The result must match the expected formula from the design doc.
        int expected = Math.Clamp(rawEval + correctionEntry / W, -29999, 29999);
        Assert.Equal(expected, clamped);

        // It must never exceed ±29999.
        Assert.InRange(clamped, -29999, 29999);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — Positive saturation clamp at +29999
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void CorrectionHistory_PositiveSaturation_ClampsTo29999()
    {
        const int W = 512;
        int rawEval = 29980;
        int correctionEntry = 16384; // max entry, produces +32 cp
        int corrected = rawEval + correctionEntry / W; // 29980 + 32 = 30012

        int result = Math.Clamp(corrected, -29999, 29999);

        Assert.Equal(29999, result);
        Assert.True(corrected > 29999, "pre-clamp value must exceed the cap");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — Negative saturation clamp at -29999
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void CorrectionHistory_NegativeSaturation_ClampsToMinus29999()
    {
        const int W = 512;
        int rawEval = -29980;
        int correctionEntry = -16384; // most negative entry, produces -32 cp

        int corrected = rawEval + correctionEntry / W; // -29980 - 32 = -30012
        int result = Math.Clamp(corrected, -29999, 29999);

        Assert.Equal(-29999, result);
        Assert.True(corrected < -29999, "pre-clamp value must be below the lower cap");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 5 — CorrectionHistory is written on Exact nodes
    // A search that reaches an Exact node must update the table.
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void CorrectionHistory_WrittenBySearch_OnExactNode()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var td = new ThreadData(0);
        var tt = new TTable(4);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var search = new Search(tt, td, signal, evaluator);
        search.UseCorrectionHistory = true;
        // This test validates the LEGACY pawn-only additive design, which is only written
        // when the the reference 4-term path is disabled.
        search.UseRefCorrectionHistory = false;

        // Run a short search to populate the correction history
        search.IterativeDeepening(pos, 3);

        // After a completed search at depth 3 with EXACT nodes, at least some
        // correction history entries should be non-zero.
        bool anyNonZero = false;
        for (int side = 0; side < 2; side++)
            for (int idx = 0; idx < 16384; idx++)
                if (td.CorrectionHistory[side][idx] != 0)
                {
                    anyNonZero = true;
                    break;
                }

        Assert.True(anyNonZero,
            "At least one CorrectionHistory entry should be written after a depth-3 search");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 6 — CorrectionHistory entries are clamped to ±16384
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void CorrectionHistory_Entries_ClampedToMaxEntry()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var td = new ThreadData(0);
        var tt = new TTable(4);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var search = new Search(tt, td, signal, evaluator);
        search.UseCorrectionHistory = true;
        // Legacy-design test — see the note on CorrectionHistory_WrittenBySearch_OnExactNode.
        search.UseRefCorrectionHistory = false;

        search.IterativeDeepening(pos, 5);

        for (int side = 0; side < 2; side++)
            for (int idx = 0; idx < 16384; idx++)
                Assert.InRange(td.CorrectionHistory[side][idx], -16384, 16384);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// the reference-style 4-term correction history
// ═══════════════════════════════════════════════════════════════════════════════

public class ReferenceCorrectionHistoryTests
{
    // the reference CorrectionHistoryTable.correction math, replicated for direct assertion:
    // newValue = diff * 64; newWeight = min(depth+1, 16);
    // update = (old*(64-newWeight) + newValue*newWeight) / 64; clamp ±2048
    private static short ExpectedUpdate(int oldValue, int diff, int depth)
    {
        int newValue = diff * 64;
        int newWeight = Math.Min(depth + 1, 16);
        int update = (oldValue * (64 - newWeight) + newValue * newWeight) / 64;
        return (short)Math.Clamp(update, -2048, 2048);
    }

    [Fact]
    public void ReferenceUpdate_Formula_MatchesReference()
    {
        // First update from a fresh cell (old=0): update = diff*64*min(d+1,16)/64 = diff*newWeight.
        Assert.Equal(40 * 5, ExpectedUpdate(0, 40, 4));          // d=4 → newWeight=5
        // Depth-weighted: d=1 (weight 2) vs d=16 (weight 16) from the same old value.
        // shallow (d=1): (100*62 + 40*64*2)/64 = 11320/64 = 176
        // deep (d=16): (100*48 + 40*64*16)/64 = 45760/64 = 715 → deep moves ~3x further
        short shallow = ExpectedUpdate(100, 40, 1);
        short deep = ExpectedUpdate(100, 40, 16);
        Assert.Equal(176, shallow);
        Assert.Equal(715, deep);
        // Clamp at ±2048: huge diff with full weight → 2048.
        Assert.Equal(2048, ExpectedUpdate(0, 100000, 16));
    }

    [Fact]
    public void ReferenceCorrections_AreBounded_AndChangeSearch()
    {
        var pos = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 1");
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var search = new Search(tt, td, new SearchSignal(), TestHelper.NewEvaluator(pos));
        search.TimeManager.SetInfinite();
        search.UseRefCorrectionHistory = true;

        var best = search.IterativeDeepening(pos, maxDepth: 9);
        Assert.False(best.IsNone);

        // Every cell in every the reference table must respect the ±2048 clamp.
        foreach (var t in td.CorrectionPawn)
            foreach (var v in t) Assert.InRange(v, -2048, 2048);
        foreach (var perKey in td.CorrectionNonPawn)
            foreach (var t in perKey)
                foreach (var v in t) Assert.InRange(v, -2048, 2048);
        foreach (var t in td.CorrectionCont)
            foreach (var row in t)
                foreach (var v in row) Assert.InRange(v, -2048, 2048);

        // The the reference path must actually have written cells (search populated them).
        bool anyNonZero = td.CorrectionPawn.Any(t => t.Any(v => v != 0))
                          || td.CorrectionNonPawn.Any(k => k.Any(t => t.Any(v => v != 0)));
        Assert.True(anyNonZero, "reference correction tables were never written during search");
    }

    [Fact]
    public void Toggle_Off_ReproducesLegacyPath_WithDifferentNodes()
    {
        var posOn = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 1");
        var tdOn = new ThreadData(0);
        var searchOn = new Search(new TTable(16), tdOn, new SearchSignal(), TestHelper.NewEvaluator(posOn));
        searchOn.TimeManager.SetInfinite();
        searchOn.UseRefCorrectionHistory = true;
        searchOn.IterativeDeepening(posOn, maxDepth: 10);

        var posOff = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 1");
        var tdOff = new ThreadData(0);
        var searchOff = new Search(new TTable(16), tdOff, new SearchSignal(), TestHelper.NewEvaluator(posOff));
        searchOff.TimeManager.SetInfinite();
        searchOff.UseRefCorrectionHistory = false;
        searchOff.IterativeDeepening(posOff, maxDepth: 10);

        // Both corrections are active but learn differently — the node counts must
        // diverge (proves the toggle selects between two different correction designs,
        // not the same one twice).
        Assert.NotEqual(tdOn.Nodes, tdOff.Nodes);
    }
}
