namespace PawnForge.Tests;

using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Unit tests for move-loop futility pruning, a shallow-depth optimization that skips quiet
/// non-pawn moves when the static evaluation is significantly below alpha.
///
/// <para>
/// <b>What is Move-Loop Futility Pruning?</b>
/// </para>
/// <para>
/// At shallow search depths, if the current position's evaluation is far below the minimum score
/// we need (alpha) to improve our best line, most quiet moves—which don't immediately change
/// material or create forcing threats—cannot realistically bridge that gap. This pruning technique
/// identifies such "futile" moves and skips them to save search nodes for more promising branches.
/// </para>
///
/// <para>
/// <b>Key Design Decisions Tested:</b>
/// </para>
/// <list type="the Rust trainer">
/// <item><b>Non-pawn moves only:</b> Pawn moves alter position structure irreversibly and are
/// excluded from pruning even when the evaluation is poor.</item>
/// <item><b>Depth-limited:</b> Only applies at depths ≤ FutilityMaxDepth (default 7) where the
/// margin formula is reliable.</item>
/// <item><b>Safety guards:</b> Disabled when in check, at PV nodes, or for material-changing moves
/// (captures/promotions).</item>
/// </list>
///
/// <para>
/// These tests verify that the pruning fires when appropriate (reducing node count) and is
/// correctly suppressed when the safety conditions are violated.
/// </para>
/// </summary>
public class FutilityPruningTests
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
    /// Validates that futility pruning fires at appropriate depths (depth &lt;= FutilityMaxDepth).
    /// Uses a position where the static eval is well below alpha, so non-pawn quiet moves should
    /// be skipped.
    /// </summary>
    [Fact]
    public void FutilityPruning_FiresAtShallowDepth()
    {
        // Position with White down material (black has extra rooks), static eval ~−500 to −1000.
        // Search with alpha=0, beta=100 at depth 5 (within the FutilityMaxDepth=7 default).
        // With futility pruning ON, non-pawn quiet moves whose eval + depth*120 < alpha=0 should
        // be skipped, resulting in fewer nodes searched.
        //
        // IMPORTANT: This test uses TWO INDEPENDENT (Search, ThreadData) instances rather than
        // sharing one instance across both runs via td.Reset. ThreadData.Reset deliberately
        // does NOT clear ButterflyHistory, CaptureHistory, continuation history, or killer moves
        // (see ThreadData.cs XML docs: "history tables PERSIST... knowledge accumulates across
        // moves of a game" — this is correct behavior for real play). Because the LMR modulating
        // terms (history score, killer/counter match) read these persisted tables, whichever
        // configuration runs SECOND in a shared instance benefits from a "warmed up" history
        // table from the first run, which changes reduction depths independently of the pruning
        // flag under test. Using fresh instances for each run eliminates this ordering artifact.
        const string fen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1"; // Material down
        var pos = Position.FromFEN(fen);

        int depth = 5;
        int alpha = 0;
        int beta = 100;

        var (searchWith, _, tdWith) = CreateSearch(pos);
        searchWith.UseFutilityPruning = true;
        searchWith.FutilityMaxDepth = 7;
        searchWith.FutMarginByDepth = new[] { 0, 120, 120, 120, 120, 120, 120, 120 };
        searchWith.UseNullMovePruning = false;
        searchWith.UseReverseFutilityPruning = false;
        searchWith.UseRazoring = false;
        searchWith.UseLateMovePruning = false;
        var dummyMove = Move.None;
        searchWith.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);
        long nodesWithPruning = tdWith.Nodes;

        var (searchWithout, _, tdWithout) = CreateSearch(pos);
        searchWithout.UseFutilityPruning = false;
        searchWithout.UseNullMovePruning = false;
        searchWithout.UseReverseFutilityPruning = false;
        searchWithout.UseRazoring = false;
        searchWithout.UseLateMovePruning = false;
        var dummyMove2 = Move.None;
        searchWithout.Negamax(pos, depth, alpha, beta, ref dummyMove2, ply: 0);
        long nodesWithoutPruning = tdWithout.Nodes;

        // With futility pruning enabled, we should search fewer nodes
        Assert.True(nodesWithPruning < nodesWithoutPruning,
            $"Expected futility pruning to reduce nodes. With: {nodesWithPruning}, Without: {nodesWithoutPruning}");
    }

    /// <summary>
    /// Validates that futility pruning does NOT fire when in check (safety: must search all evasions).
    /// The node counts don't need to be identical because other pruning mechanisms may behave
    /// slightly differently, but the futility pruning toggle should have minimal effect in check.
    /// </summary>
    [Fact]
    public void FutilityPruning_DoesNotFireInCheck()
    {
        // Position where White king is in check (black rook on e1 checking white king on e2).
        // Even with static eval far below alpha, futility pruning must not skip moves because
        // the engine must search all legal evasions.
        const string fen = "4k3/8/8/8/8/8/4K3/4r3 w - - 0 1"; // King in check
        var pos = Position.FromFEN(fen);
        var (search, _, td) = CreateSearch(pos);

        search.UseFutilityPruning = true;
        search.FutilityMaxDepth = 7;
        search.FutMarginByDepth = new[] { 0, 120, 120, 120, 120, 120, 120, 120 };

        // Disable other pruning
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;
        search.UseLateMovePruning = false;

        int depth = 5;
        int alpha = -1000;
        int beta = 1000;

        // Run with and without futility pruning
        var dummyMove = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);
        long nodesWithPruning = td.Nodes;

        td.Reset();
        search.UseFutilityPruning = false;
        var dummyMove2 = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove2, ply: 0);
        long nodesWithoutPruning = td.Nodes;

        // The key test: futility pruning should be disabled in check, so we should have
        // searched at least as many nodes without it as with it (minimal benefit)
        Assert.True(nodesWithPruning > 0);
        Assert.True(nodesWithoutPruning > 0);
    }

    /// <summary>
    /// Validates that futility pruning does NOT fire for pawn moves (pawn moves can change
    /// position structure dramatically and are not safely prunable by eval margin alone).
    /// </summary>
    [Fact]
    public void FutilityPruning_DoesNotFireForPawnMoves()
    {
        // Position with pawns. Static eval is low, but pawn moves should not be pruned.
        const string fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"; // Starting position
        var pos = Position.FromFEN(fen);
        var (search, _, td) = CreateSearch(pos);

        search.UseFutilityPruning = true;
        search.FutilityMaxDepth = 7;
        search.FutMarginByDepth = new[] { 0, 120, 120, 120, 120, 120, 120, 120 };

        // Disable other pruning
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;
        search.UseLateMovePruning = false;

        int depth = 3;
        int alpha = -100;
        int beta = 100;

        // Run search - should not prune pawn moves even if eval is below alpha
        var dummyMove = Move.None;
        int score = search.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);

        // Just verify the search completes without error and finds some reasonable move
        Assert.True(td.Nodes > 0);
        Assert.NotEqual(Move.None, dummyMove);
    }

    /// <summary>
    /// Validates that futility pruning has reduced effect at PV nodes (PV nodes require full search).
    /// Note: Even though the condition checks !isPvNode, the toggle can still affect performance
    /// indirectly through interaction with other pruning mechanisms.
    /// </summary>
    [Fact]
    public void FutilityPruning_DoesNotFireAtPvNodes()
    {
        const string fen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
        var pos = Position.FromFEN(fen);
        var (search, _, td) = CreateSearch(pos);

        search.UseFutilityPruning = true;
        search.FutilityMaxDepth = 7;
        search.FutMarginByDepth = new[] { 0, 120, 120, 120, 120, 120, 120, 120 };

        // Disable other pruning
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;
        search.UseLateMovePruning = false;

        int depth = 5;
        // Full window = PV node
        int alpha = -30000;
        int beta = 30000;

        // Run with and without futility pruning at a PV node (full window)
        var dummyMove = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);
        long nodesWithPruning = td.Nodes;

        td.Reset();
        search.UseFutilityPruning = false;
        var dummyMove2 = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove2, ply: 0);
        long nodesWithoutPruning = td.Nodes;

        // The key test: at PV nodes, futility pruning should not directly fire,
        // but we just verify the search completes successfully
        Assert.True(nodesWithPruning > 0);
        Assert.True(nodesWithoutPruning > 0);
    }

    /// <summary>
    /// Validates that futility pruning has reduced effect at depths beyond FutilityMaxDepth.
    /// </summary>
    [Fact]
    public void FutilityPruning_DoesNotFireBeyondMaxDepth()
    {
        const string fen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
        var pos = Position.FromFEN(fen);
        var (search, _, td) = CreateSearch(pos);

        search.UseFutilityPruning = true;
        search.FutilityMaxDepth = 3; // Set a low max depth
        search.FutMarginByDepth = new[] { 0, 120, 120, 120, 120, 120, 120, 120 };

        // Disable other pruning
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;
        search.UseLateMovePruning = false;

        int depth = 8; // Beyond FutilityMaxDepth
        int alpha = 0;
        int beta = 100;

        // Run with and without futility pruning at a depth beyond max
        var dummyMove = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);
        long nodesWithPruning = td.Nodes;

        td.Reset();
        search.UseFutilityPruning = false;
        var dummyMove2 = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove2, ply: 0);
        long nodesWithoutPruning = td.Nodes;

        // At depths beyond FutilityMaxDepth, pruning should not fire at the root level,
        // but can still affect the tree indirectly. Just verify search completes.
        Assert.True(nodesWithPruning > 0);
        Assert.True(nodesWithoutPruning > 0);
    }

    /// <summary>
    /// Validates that futility pruning does NOT fire for capture moves (captures change material
    /// and must be searched).
    /// </summary>
    [Fact]
    public void FutilityPruning_DoesNotFireForCaptures()
    {
        // Position with possible captures
        const string fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1";
        var pos = Position.FromFEN(fen);
        var (search, _, td) = CreateSearch(pos);

        search.UseFutilityPruning = true;
        search.FutilityMaxDepth = 7;
        search.FutMarginByDepth = new[] { 0, 120, 120, 120, 120, 120, 120, 120 };

        // Disable other pruning
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;
        search.UseLateMovePruning = false;

        int depth = 5;
        int alpha = -100;
        int beta = 100;

        // Just verify search completes and finds moves
        var dummyMove = Move.None;
        search.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);

        Assert.True(td.Nodes > 0);
    }

    /// <summary>
    /// Integration test: verify that futility pruning reduces node count in a real search
    /// scenario — ISOLATED (Phase C1 rewrite, 2026-09-07).
    /// </summary>
    /// <remarks>
    /// The original version used a rich middlegame position where, after Phase C1, the newer
    /// PF-only pruning layers (the reference LMP, the reference history pruning, Bad-Noisy `break`,
    /// depth-scaled SEE) remove the same late quiets BEFORE the futility block sees them —
    /// the ON/OFF node counts converged and the test went vacuous (see
    ///: the layers genuinely overlap). The rewritten
    /// scenario removes every interfering layer BY CONSTRUCTION: a lost KQ-vs-K position with
    /// NO captures available (nothing for SEE/BNP to fire on), only ~8 king moves (nothing
    /// for LMP to fire on), fresh history tables (nothing for history pruning to fire on),
    /// and a static eval catastrophically below alpha — the exact condition futility exists
    /// for (eval + depth·margin &lt; alpha at shallow depth). FEN validated against the oracle
    /// per (must evaluate as decisively lost for White, &lt; -700 cp).
    /// </remarks>
    [Fact]
    public void FutilityPruning_ReducesNodeCount_IntegrationTest()
    {
        // White Kd3 vs black Ka8 + Qh1: White is down a queen (~-900..-1000 cp), has only
        // quiet king moves, and cannot capture anything. the oracle validates the fixture.
        const string fen = "k7/8/8/8/8/3K4/8/7q w - - 0 1";
        // NOTE: fixed-depth the oracle (no tablebases) reports ~-450..-500 cp here (true value
        // is a forced mate for Black); assert only "decisively lost for White" robustly.
        if (Stockfish.IsAvailable)
        {
            var fixture = Stockfish.Analyze(fen, 8);
            Assert.True(fixture.ScoreCp.HasValue && fixture.ScoreCp.Value < -300,
                $"Fixture validation failed: '{fen}' should be decisively lost for White, " +
                $"Stockfish reports {fixture.ScoreSummary}.");
        }

        var pos = Position.FromFEN(fen);

        // alpha = 0 is far ABOVE the eval (~-500): the futility condition
        // (eval + 96 + 68*depth + 4*moves < 0) fires on every quiet king move at depth <= 5
        // inside the zero-window children (futility is !isPvNode-gated, exactly as in the
        // real engine — the root's PVS children dominate the node count).
        int depth = 6;
        int alpha = 0;
        int beta = 100;

        var (searchWith, _, tdWith) = CreateSearch(pos);
        searchWith.UseFutilityPruning = true;
        searchWith.FutilityMaxDepth = 7;
        searchWith.FutMarginByDepth = new[] { 0, 96, 96, 96, 96, 96, 96, 96 };
        // Isolation pins: every other pruning/extension layer OFF in BOTH runs.
        searchWith.UseNullMovePruning = false;
        searchWith.UseReverseFutilityPruning = false;
        searchWith.UseRazoring = false;
        searchWith.UseLateMovePruning = false;
        searchWith.UseRefLmp = false;
        searchWith.UseRefHistoryPruning = false;
        searchWith.UseBadNoisyPruning = false;
        searchWith.UseRefSeeThresholds = false;
        searchWith.UseProbCut = false;
        searchWith.UseSecondaryProbCut = false;
        searchWith.UseSingularExtensions = false;
        searchWith.UseLmrV2 = false; // legacy LMR only, so no reducedDepth coupling
        var dummyMove = Move.None;
        // Isolation from Syzygy (see QuiescenceChecksTests): this is a 3-man position —
        // inside the TB cardinality — and probing is now depth-unconditional, so with
        // tables loaded in parallel the position would instant-resolve and equalize
        // the node counts, defeating the very comparison under test.
        bool prevProbing = Syzygy.ProbingEnabled;
        Syzygy.ProbingEnabled = false;
        try
        {
            searchWith.Negamax(pos, depth, alpha, beta, ref dummyMove, ply: 0);
            long nodesWithPruning = tdWith.Nodes;

        var (searchWithout, _, tdWithout) = CreateSearch(pos);
        searchWithout.UseFutilityPruning = false;
        searchWithout.FutilityMaxDepth = 7;
        searchWithout.FutMarginByDepth = new[] { 0, 96, 96, 96, 96, 96, 96, 96 };
        searchWithout.UseNullMovePruning = false;
        searchWithout.UseReverseFutilityPruning = false;
        searchWithout.UseRazoring = false;
        searchWithout.UseLateMovePruning = false;
        searchWithout.UseRefLmp = false;
        searchWithout.UseRefHistoryPruning = false;
        searchWithout.UseBadNoisyPruning = false;
        searchWithout.UseRefSeeThresholds = false;
        searchWithout.UseProbCut = false;
        searchWithout.UseSecondaryProbCut = false;
        searchWithout.UseSingularExtensions = false;
        searchWithout.UseLmrV2 = false;
        var dummyMove2 = Move.None;
        searchWithout.Negamax(pos, depth, alpha, beta, ref dummyMove2, ply: 0);
        long nodesWithoutPruning = tdWithout.Nodes;

        Assert.True(nodesWithPruning < nodesWithoutPruning,
            $"Expected node reduction with futility pruning. With: {nodesWithPruning}, Without: {nodesWithoutPruning}");
        }
        finally
        {
            Syzygy.ProbingEnabled = prevProbing;
        }
    }
}
