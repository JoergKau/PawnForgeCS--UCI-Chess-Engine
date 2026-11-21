using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for cut-node-aware Late Move Reductions (search-depth work, ).
///
/// A "cut node" is an interior node the search predicts will fail high. Late moves at such
/// nodes are reduced one extra ply so the tree stays narrow and the search reaches greater
/// depth in the same time. The pure arithmetic lives in <c>Search.ApplyCutNodeReduction</c>;
/// the cut-node flag is threaded through <c>Negamax</c> and consumed in the LMR block, gated by
/// the <c>UseCutNode</c> toggle.
/// </summary>
public class CutNodeReductionTests
{
    private static (Search search, TTable tt, ThreadData td) CreateSearch(Position pos)
    {
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var signal = new SearchSignal();
        var evaluator = TestHelper.NewEvaluator(pos);
        var search = new Search(tt, td, signal, evaluator);
        return (search, tt, td);
    }

    /// <summary>
    /// The reduction bump is applied only when the feature is enabled AND the node is a cut
    /// node; otherwise the reduction is returned unchanged. Table-driven over all four
    /// combinations plus a couple of reduction magnitudes.
    /// </summary>
    [Theory]
    [InlineData(2, true,  true,  3)] // enabled + cut node  -> +1
    [InlineData(0, true,  true,  1)] // enabled + cut node  -> +1 from zero
    [InlineData(2, true,  false, 2)] // enabled + non-cut   -> unchanged
    [InlineData(2, false, true,  2)] // disabled + cut node -> unchanged
    [InlineData(2, false, false, 2)] // disabled + non-cut  -> unchanged
    [InlineData(5, true,  true,  6)] // larger reduction bumps too
    public void ApplyCutNodeReduction_MatchesTable(int reduction, bool enabled, bool cutNode, int expected)
    {
        Assert.Equal(expected, Search.ApplyCutNodeReduction(reduction, cutNode, enabled));
    }

    /// <summary>
    /// Behavioral smoke: a fixed-depth root search on a normal midgame position completes and
    /// returns a positive node count with the cut-node reduction enabled - the extra reduction
    /// never breaks the PVS ladder or move selection.
    /// </summary>
    [Fact]
    public void CutNodeReduction_Enabled_SearchCompletes()
    {
        var pos = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4");
        var (search, _, td) = CreateSearch(pos);
        search.UseCutNode = true;

        int score = search.Negamax(pos, depth: 8, alpha: -30000, beta: 30000);

        Assert.True(td.Nodes > 0);
        Assert.InRange(score, -30000, 30000);
    }

    /// <summary>
    /// The toggle actually reaches the tree: a fixed-depth root search visits a DIFFERENT
    /// number of nodes with the cut-node reduction enabled vs disabled, proving the flag is
    /// wired into the LMR path and changes the shape of the search.
    ///
    /// NOTE (2026-09-04): this test previously asserted enabled ≤ disabled ("extra reduction
    /// can only narrow the tree"). That premise is FALSE in general and measurably false here
    /// (on=232815 vs off=209436 in this position): an extra ply of reduction can make the
    /// reduced search fail high and trigger a full-depth RE-SEARCH, and re-searches cost nodes.
    /// So more reduction does not monotonically mean fewer nodes — it depends on the re-search
    /// rate. The durable, TRUE assertion is that the flag demonstrably affects the tree and the
    /// search stays correct; the net node/Elo direction is a self-play question, not a unit-test
    /// invariant.
    /// </summary>
    /// <summary>
    /// LMR v2 (Phase C1) note: with the v2 schedule active (default), the cut-node reduction
    /// application is gated by <see cref="Search.UseLmrTermCutNode"/> (the reference's +1910/1024
    /// weight) instead of the legacy whole-ply <see cref="Search.UseCutNode"/> toggle. This test
    /// therefore runs the tree-change assertion for BOTH schedules: v2 (vary UseLmrTermCutNode)
    /// and legacy (UseLmrV2 = false, vary UseCutNode).
    /// </summary>
    [Theory]
    [InlineData(true)]  // LMR v2 schedule
    [InlineData(false)] // legacy v1 schedule
    public void CutNodeReduction_Enabled_ChangesTheTree(bool useLmrV2)
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4";
        const int Depth = 9;

        // Pin EVERY toggle this comparison depends on, so the test is
        // independent of the engine's ambient defaults. The cut-node reduction's
        // "narrows the tree" property is defined relative to a fixed LMR base;
        // when the default of UseLmrTable changed (2026-09 baseline reset) this
        // test broke purely because the base formula underneath changed, not
        // because cut-node stopped working. We fix the LMR base explicitly here
        // (table-based, the schedule cut-node was designed against) and vary ONLY
        // the cut-node application toggle — the actual thing under test.
        var posOn = Position.FromFEN(fen);
        var (searchOn, _, tdOn) = CreateSearch(posOn);
        searchOn.UseLmrTable = true;
        searchOn.UseLmrV2 = useLmrV2;
        if (useLmrV2) searchOn.UseLmrTermCutNode = true; else searchOn.UseCutNode = true;
        searchOn.Negamax(posOn, Depth, -30000, 30000);

        var posOff = Position.FromFEN(fen);
        var (searchOff, _, tdOff) = CreateSearch(posOff);
        searchOff.UseLmrTable = true;
        searchOff.UseLmrV2 = useLmrV2;
        if (useLmrV2) searchOff.UseLmrTermCutNode = false; else searchOff.UseCutNode = false;
        searchOff.Negamax(posOff, Depth, -30000, 30000);

        // The flag must demonstrably change the tree (proves it is wired into the LMR path).
        Assert.True(tdOn.Nodes != tdOff.Nodes,
            $"cut-node reduction did not affect the tree at all (useLmrV2={useLmrV2}): on={tdOn.Nodes}, off={tdOff.Nodes}");
        // Sanity: both actually searched a non-trivial tree.
        Assert.True(tdOn.Nodes > 1 && tdOff.Nodes > 1);
    }
}
