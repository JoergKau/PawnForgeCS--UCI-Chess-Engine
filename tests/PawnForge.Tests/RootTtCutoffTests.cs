using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for the root TT cutoff blunder (bug condition exploration and preservation).
///
/// Property 1 (Bug Condition): On unfixed code the Exact root TT cutoff fires and returns
/// the poisoned move without executing the move loop. This test MUST FAIL on unfixed code —
/// failure is the proof the bug exists. After Fix 1 is applied the test MUST PASS.
///
/// Property 2 (Preservation): Non-root TT cutoffs and cold-TT searches must behave
/// identically on unfixed and fixed code. These tests PASS on unfixed code (baseline).
/// Exception: UpperBound_TtStore_UsesTtMoveNotFirstTriedMove MUST FAIL on unfixed code
/// (confirms Hypothesis F).
/// </summary>
public class RootTtCutoffTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Helper: create a fresh Search instance (same pattern as SearchSanityTests)
    // ─────────────────────────────────────────────────────────────────────────
    private static (Search search, TTable tt, ThreadData td) CreateSearch(Position pos)
    {
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var signal = new SearchSignal();
        var evaluator = TestHelper.NewEvaluator(pos);
        var search = new Search(tt, td, signal, evaluator);
        return (search, tt, td);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Property 2 — Preservation tests
    //
    // These document the baseline behaviour that must NOT regress after the
    // three fixes are applied.
    //
    // NonRootTtCutoff_Exact_StillFires → PASS on unfixed AND fixed code
    // RootColdTt_SearchesNormally → PASS on unfixed AND fixed code
    // UpperBound_TtStore_… → FAIL on unfixed, PASS on fixed (Hypothesis F)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **Validates: Requirements 3.1, 3.4**
    ///
    /// Property 2a: Preservation — non-root Exact TT cutoff still fires.
    ///
    /// Seed an Exact TT entry at depth 4, value +77 for a position with real
    /// material. Call Negamax at ply=1 (non-root) with depth 4 and a window
    /// that contains the value. Because probe.Depth (4) >= searchDepth (4),
    /// the Exact cutoff fires immediately and returns probe.Value.
    ///
    /// PIECE COUNT (≥6) — TEST-ISOLATION SAFETY:
    /// This test uses a 6-piece position on purpose. The Negamax Syzygy
    /// tablebase probe (guarded by PopCount(occupancy) <= Syzygy.MaxPieces)
    /// can return early — skipping the TT logic this test exercises — if the
    /// position has few enough pieces to fit a loaded tablebase. Other test
    /// classes initialize Syzygy globally and run in parallel with this one,
    /// so a low-piece position (e.g. K+Q vs K = 3 pieces) makes this test
    /// flaky. Using ≥6 pieces keeps the tablebase guard permanently false,
    /// making the test immune to Syzygy interference regardless of what
    /// tablebases another test class happens to have loaded.
    ///
    /// EXPECTED ON UNFIXED CODE: PASS — non-root behaviour is unchanged.
    /// EXPECTED AFTER ALL FIXES: PASS — fix only guards ply == 0.
    /// </summary>
    [Fact]
    public void NonRootTtCutoff_Exact_StillFires()
    {
        // 6-piece position (immune to Syzygy probe): white K on e6, white Q on d1,
        // white pawns on d2/e2/f2, black K on e8. White to move, clearly winning
        // but not an immediate mate and not a draw.
        // the oracle 18 bestmove for this FEN: d2d3.
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);

        // Seed an Exact entry at depth 4, value +77. Use a legal queen move as BestMove
        // (d1->c1 is legal in this position; d1->d2 is now blocked by the pawn on d2).
        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        const int SeedValue = 77;
        tt.Store(pos.ZobristKey, value: SeedValue, depth: 4,
                 NodeType.Exact, anyMove, currentAge: 1, ply: 0);

        // Call Negamax at ply=1 (non-root) — Exact cutoff should fire.
        Move dummy = default;
        int returned = search.Negamax(pos, depth: 4, alpha: -1000, beta: 1000,
                                      pvMove: ref dummy, ply: 1);

        // The TT value must be returned immediately (cutoff fired).
        Assert.Equal(SeedValue, returned);
    }

    /// <summary>
    /// **Validates: Requirements 3.2, 3.4**
    ///
    /// Property 2b: Preservation — cold TT always searches normally.
    ///
    /// With NO prior TT entry for the position, IterativeDeepening must do
    /// real search work: td.Nodes > maxDepth.
    ///
    /// EXPECTED ON UNFIXED CODE: PASS — cold TT never triggers any cutoff.
    /// EXPECTED AFTER ALL FIXES: PASS — behaviour is unchanged.
    /// </summary>
    [Fact]
    public void RootColdTt_SearchesNormally()
    {
        // K vs K — cold TT (no store beforehand).
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/8/8 w - - 0 1");

        var (search, _, td) = CreateSearch(pos);
        // TT is freshly constructed — no entry for this position.

        const int MaxDepth = 4;
        search.IterativeDeepening(pos, maxDepth: MaxDepth);

        // At least one move must have been actually searched.
        Assert.True(td.Nodes > MaxDepth,
            $"Expected nodes > {MaxDepth} (actual search) but got {td.Nodes}.");
    }

    /// <summary>
    /// **Validates: Requirements 3.5**
    ///
    /// Property 2c: UpperBound TT store should use ttMove, not bestMove.
    ///
    /// This test surfaces Hypothesis F via a pseudo-illegal priorTtMove:
    ///
    /// 1. Seed a prior Exact TT entry at depth=1 (SHALLOWER than search
    /// depth=2) with priorTtMove = e6→a1. A king on e6 cannot reach a1
    /// in one step — MovePicker's IsMovePseudoLegal rejects it, so the
    /// move is never tried in the loop.
    /// 2. Call Negamax at ply=1 with alpha=29000 so every legal move fails
    /// low → UpperBound TT store is written at the end of Negamax.
    /// 3. On UNFIXED code: _tt.Store(..., bestMove) where bestMove = first
    /// legal move executed in the loop (NOT priorTtMove, NOT Move.None)
    /// → isAcceptable = false → assertion FAILS ✓ (Hypothesis F confirmed)
    /// 4. On FIXED code: moveToStore = ttMove = priorTtMove (e6→a1)
    /// → isAcceptable = true → assertion PASSES ✓
    ///
    /// PIECE COUNT (≥6) — TEST-ISOLATION SAFETY:
    /// This test uses a 6-piece position on purpose. The Negamax Syzygy
    /// tablebase probe (guarded by PopCount(occupancy) <= Syzygy.MaxPieces)
    /// can return early from Negamax — before the UpperBound TT store this
    /// test relies on is ever written — when the position is small enough to
    /// fit a loaded tablebase. Other test classes initialize Syzygy globally
    /// and run in parallel with this one, so a low-piece position (e.g. K+Q
    /// vs K = 3 pieces) makes this test flaky: the seeded depth-1 Exact entry
    /// survives and the NodeType.UpperBound assertion fails. Using ≥6 pieces
    /// keeps the tablebase guard permanently false, making the test immune to
    /// Syzygy interference regardless of what tablebases are loaded elsewhere.
    ///
    /// EXPECTED ON UNFIXED CODE: FAILS — BestMove is the first-tried legal move,
    /// neither priorTtMove (pseudo-illegal, never tried) nor Move.None.
    /// EXPECTED AFTER FIX 2: PASSES — moveToStore = ttMove = priorTtMove.
    /// </summary>
    [Fact]
    public void UpperBound_TtStore_UsesTtMoveNotFirstTriedMove()
    {
        // 6-piece position (immune to Syzygy probe): white K on e6, white Q on d1,
        // white pawns on d2/e2/f2, black K on e8. White to move.
        // the oracle 18 bestmove for this FEN: d2d3.
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);

        // Build a pseudo-illegal "move" for the king: e6→a1.
        // A king cannot reach a1 from e6 in one step; MovePicker will reject it
        // via IsMovePseudoLegal, so it is never tried in the move loop.
        // The TT probe still sets ttMove = priorTtMove (before MovePicker runs).
        Move priorTtMove = new Move(TestHelper.Sq("e6"), TestHelper.Sq("a1"));

        // Seed a prior Exact TT entry at depth=1 (shallower than search depth=2)
        // so probe.Depth (1) < searchDepth (2) → Exact cutoff does NOT fire.
        tt.Store(pos.ZobristKey, value: 50, depth: 1,
                 NodeType.Exact, priorTtMove, currentAge: 1, ply: 0);

        // Call Negamax at ply=1 with alpha=29000 so every move fails low.
        // ttMove = priorTtMove is extracted from the probe but skipped by MovePicker.
        // The loop executes normal legal moves; all score below alpha.
        // At the end of Negamax an UpperBound entry is written to the TT.
        Move dummy = default;
        search.Negamax(pos, depth: 2, alpha: 29000, beta: 30000,
                       pvMove: ref dummy, ply: 1);

        // Probe the TT — the deeper UpperBound write (depth=2) should replace the
        // shallower Exact entry (depth=1) per the replacement policy.
        bool found = tt.Probe(pos.ZobristKey, out var entry, ply: 0);
        Assert.True(found, "Expected a TT entry to be stored after the fail-low search.");
        Assert.Equal(NodeType.UpperBound, entry.Flag);

        // Assert: BestMove must be priorTtMove (the incoming ttMove) or Move.None.
        //
        // On UNFIXED code this FAILS:
        // bestMove = first legal move tried in the loop (e.g. e6d5, d1d2, …)
        // that move is NOT priorTtMove (e6→a1 is pseudo-illegal, never tried)
        // and is NOT Move.None → isAcceptable = false → FAIL ✓
        //
        // On FIXED code this PASSES:
        // moveToStore = ttMove = priorTtMove (e6→a1) → isAcceptable = true ✓
        bool isAcceptable = entry.BestMove.Equals(priorTtMove) || entry.BestMove.IsNone;
        Assert.True(isAcceptable,
            $"UpperBound BestMove should be the prior ttMove ({priorTtMove}) or Move.None, " +
            $"but got {entry.BestMove}. " +
            $"This confirms Hypothesis F: the engine stores bestMove (first-tried fail-low " +
            $"move) instead of ttMove.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Secondary "small ProbCut" tests (search-depth-wave1 )
    //
    // The secondary ProbCut fires from an already-probed TT LowerBound entry that
    // is up to 4 plies SHALLOWER than the search depth, returning beta + margin
    // (margin = 200) without running the move loop. It is !isRoot / !isPvNode /
    // !inCheck guarded and only fires on LowerBound entries with non-mate scores.
    //
    // All positions below use ≥6 pieces to stay immune to the Negamax Syzygy
    // probe (see the piece-count notes on the tests above) so the secondary
    // ProbCut path is reached deterministically regardless of loaded tablebases.
    //
    // NMP / RFP / Razoring are disabled on the test Search instance to ISOLATE the
    // secondary-ProbCut behaviour (otherwise a static-eval-driven early return
    // could pre-empt it). This mirrors how the RFP/razoring tests isolate their
    // own target behaviour.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Isolate the secondary ProbCut: ensure the feature under test is enabled (its
    /// compiled-in default is OFF) and disable the other early-return prunings so only
    /// the secondary ProbCut can fire. Individual tests may re-disable it to assert the
    /// toggle-off behavior.
    /// </summary>
    private static void IsolateSecondaryProbCut(Search search)
    {
        search.UseSecondaryProbCut = true;
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;
    }

    /// <summary>
    /// **Validates: Requirements 6.1**
    ///
    /// Secondary ProbCut fires on a LowerBound entry seeded shallower (depth-3)
    /// than the search depth, with value >= beta + 200. It returns beta + 200
    /// immediately (probCutBeta) with no move loop executed (td.Nodes == 1).
    /// </summary>
    [Fact]
    public void SecondaryProbCut_FiresOnLowerBoundWithinFourPlies()
    {
        // 6-piece position (immune to Syzygy probe), white to move, not in check, not a draw.
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);
        IsolateSecondaryProbCut(search);

        const int Depth = 8;
        const int Beta = 100;
        const int SeedDepth = Depth - 3;      // 5 (< Depth, so main cutoff path does NOT fire)
        const int SeedValue = Beta + 200;     // 300 == probCutBeta, so the check fires

        // Seed a LowerBound entry shallower than the search depth.
        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        tt.Store(pos.ZobristKey, value: SeedValue, depth: SeedDepth,
                 NodeType.LowerBound, anyMove, currentAge: 1, ply: 0);

        // Non-root (ply=2), zero window (non-PV), not in check.
        Move dummy = default;
        int returned = search.Negamax(pos, depth: Depth, alpha: Beta - 1, beta: Beta,
                                      pvMove: ref dummy, ply: 2);

        Assert.Equal(Beta + 200, returned);
        Assert.Equal(1, td.Nodes); // fired before the move loop; one node increment for this call
    }

    /// <summary>
    /// **Validates: Requirements 6.2**
    ///
    /// The secondary ProbCut must NEVER fire at the root. With the identical
    /// qualifying entry seeded, calling at ply=0 must run the move loop
    /// (td.Nodes > depth), regardless of UseSecondaryProbCut.
    /// </summary>
    [Fact]
    public void SecondaryProbCut_NeverFiresAtRoot()
    {
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);
        IsolateSecondaryProbCut(search);

        const int Depth = 8;
        const int Beta = 100;
        const int SeedDepth = Depth - 3;
        const int SeedValue = Beta + 200;

        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        tt.Store(pos.ZobristKey, value: SeedValue, depth: SeedDepth,
                 NodeType.LowerBound, anyMove, currentAge: 1, ply: 0);

        // Root call (ply=0) — the move loop must run.
        Move dummy = default;
        search.Negamax(pos, depth: Depth, alpha: Beta - 1, beta: Beta,
                       pvMove: ref dummy, ply: 0);

        Assert.True(td.Nodes > Depth,
            $"Expected nodes > depth={Depth} (move loop ran) but got {td.Nodes}. " +
            $"The secondary ProbCut must never short-circuit at the root.");
    }

    /// <summary>
    /// **Validates: Requirements 6.3**
    ///
    /// When UseSecondaryProbCut is false, the check must not fire even with an
    /// otherwise-qualifying entry — the search proceeds into the move loop.
    /// </summary>
    [Fact]
    public void SecondaryProbCut_RespectsToggleOff()
    {
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);
        IsolateSecondaryProbCut(search);
        search.UseSecondaryProbCut = false;

        const int Depth = 8;
        const int Beta = 100;
        const int SeedDepth = Depth - 3;
        const int SeedValue = Beta + 200;

        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        tt.Store(pos.ZobristKey, value: SeedValue, depth: SeedDepth,
                 NodeType.LowerBound, anyMove, currentAge: 1, ply: 0);

        Move dummy = default;
        int returned = search.Negamax(pos, depth: Depth, alpha: Beta - 1, beta: Beta,
                                      pvMove: ref dummy, ply: 2);

        // With the toggle off the check cannot fire: the move loop runs (more than
        // one node) and the returned value is not the probCutBeta shortcut value.
        Assert.True(td.Nodes > 1,
            $"Expected nodes > 1 (move loop ran) but got {td.Nodes}. " +
            $"With UseSecondaryProbCut=false the check must not fire.");
        Assert.NotEqual(Beta + 200, returned);
    }

    /// <summary>
    /// **Validates: Requirements 6.4**
    ///
    /// Only LowerBound entries qualify. Seeding the same value/depth as an Exact
    /// or UpperBound entry must NOT trigger the secondary ProbCut: the move loop
    /// runs and the return value is not the probCutBeta shortcut.
    /// </summary>
    [Theory]
    [InlineData(NodeType.Exact)]
    [InlineData(NodeType.UpperBound)]
    public void SecondaryProbCut_DoesNotFireOnExactOrUpperBound(NodeType flag)
    {
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);
        IsolateSecondaryProbCut(search);

        const int Depth = 8;
        const int Beta = 100;
        const int SeedDepth = Depth - 3;
        const int SeedValue = Beta + 200;

        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        tt.Store(pos.ZobristKey, value: SeedValue, depth: SeedDepth,
                 flag, anyMove, currentAge: 1, ply: 0);

        Move dummy = default;
        int returned = search.Negamax(pos, depth: Depth, alpha: Beta - 1, beta: Beta,
                                      pvMove: ref dummy, ply: 2);

        // Non-LowerBound entries do not qualify: the secondary ProbCut must not fire.
        Assert.True(td.Nodes > 1,
            $"Expected nodes > 1 (move loop ran) for flag {flag} but got {td.Nodes}. " +
            $"Only LowerBound entries may trigger the secondary ProbCut.");
        Assert.NotEqual(Beta + 200, returned);
    }

    /// <summary>
    /// **Validates: Requirements 6.5**
    ///
    /// Mate-score exclusion: the secondary ProbCut must NOT fire when the probed
    /// value is a mate score (Math.Abs(value) >= 30000), even on a qualifying
    /// LowerBound entry within 4 plies. A "beta + margin" comparison is meaningless
    /// near a mate bound, so the check is gated by Math.Abs(probe.Value) < 30000.
    /// With a mate-scored LowerBound seeded, the move loop must run instead.
    /// </summary>
    [Fact]
    public void SecondaryProbCut_ExcludesMateScores()
    {
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");

        var (search, tt, td) = CreateSearch(pos);
        IsolateSecondaryProbCut(search);

        const int Depth = 8;
        const int Beta = 100;
        const int SeedDepth = Depth - 3;
        // Mate-band value: >= 30000 must disqualify the entry from the secondary
        // ProbCut regardless of the beta + margin comparison.
        const int MateValue = 30500;

        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        tt.Store(pos.ZobristKey, value: MateValue, depth: SeedDepth,
                 NodeType.LowerBound, anyMove, currentAge: 1, ply: 0);

        Move dummy = default;
        int returned = search.Negamax(pos, depth: Depth, alpha: Beta - 1, beta: Beta,
                                      pvMove: ref dummy, ply: 2);

        // The mate-score guard blocks the shortcut: the move loop runs and the
        // return value is not the probCutBeta shortcut.
        Assert.True(td.Nodes > 1,
            $"Expected nodes > 1 (move loop ran) but got {td.Nodes}. " +
            $"A mate-scored LowerBound must not trigger the secondary ProbCut.");
        Assert.NotEqual(Beta + 200, returned);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Property 1 — Bug-condition exploration test
    //
    // MUST FAIL on unfixed code (failure = bug confirmed).
    // MUST PASS after Fix 1 is applied.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **Validates: Requirements 1.1, 1.5**
    ///
    /// Property 1: Bug Condition — Root TT Probe Returns Without Search.
    ///
    /// Seed the TT with a poisoned Exact entry (BestMove = d8a5, depth = 8) for the
    /// blunder position. Call IterativeDeepening at maxDepth = 5. Because the probe
    /// depth (8) >= search depth (5), the Exact root cutoff path fires on unfixed code
    /// and returns d8a5 immediately with nodes == depth (zero search work).
    ///
    /// EXPECTED OUTCOME ON UNFIXED CODE: FAILS.
    /// - best == poisonMove (d8a5 returned blindly)
    /// - td.Nodes == 5 (no move loop executed; one node increment per depth iteration)
    ///
    /// EXPECTED OUTCOME AFTER FIX 1: PASSES.
    /// - best != poisonMove (move loop actually ran and found the correct drawing move)
    /// - td.Nodes > 5 (at least one move was searched)
    /// </summary>
    [Fact]
    public void RootTtPoison_ExactEntry_IsNotReturnedBlindly()
    {
        // Drawn KQ vs KQ blunder position (the oracle-validated as a draw):
        // Q2q4/1K6/8/8/8/3k4/8/8 b - - 12 80
        // Black queen on d8, white queen on a8, kings on b7/d3.
        // d8a5 is a diagonal queen move (d8→c7→b6→a5) that loses the queen immediately.
        var pos = Position.FromFEN("Q2q4/1K6/8/8/8/3k4/8/8 b - - 12 80");

        var (search, tt, td) = CreateSearch(pos);

        // Build the poison move: d8 -> a5
        // d8 = rank 7 * 8 + file 3 = 59; a5 = rank 4 * 8 + file 0 = 32
        // Use TestHelper.FindMove so we get the exact Move object with correct flags.
        Move poisonMove = TestHelper.FindMove(pos, "d8", "a5");

        // Seed the TT with a poisoned Exact entry at depth 8, value -480 cp.
        // This simulates what the engine stores during an earlier iteration where
        // d8a5 was ordered first (killer/TT priority) and scored "acceptable".
        // probe.Depth (8) >= searchDepth (5) satisfies the cutoff condition.
        tt.Store(
            pos.ZobristKey,
            value: -480,
            depth: 8,
            NodeType.Exact,
            poisonMove,
            currentAge: 1,
            ply: 0);

        // Run a normal iterative deepening search at depth 5.
        // On unfixed code the Exact TT cutoff fires at the root and returns
        // poisonMove with td.Nodes == 5 (one node per depth iteration, no move loop).
        Move best = search.IterativeDeepening(pos, maxDepth: 5);

        // Assert: the stale TT move must NOT be returned blindly.
        // On unfixed code this assertion fails (best == poisonMove).
        Assert.NotEqual(poisonMove, best);

        // Assert: at least one move was actually searched.
        // On unfixed code td.Nodes == 5 (no move loop executed), so this also fails.
        Assert.True(td.Nodes > 5,
            $"Expected nodes > depth=5 (actual search work) but got nodes={td.Nodes}. " +
            $"This confirms the root Exact TT cutoff fires and returns without searching.");
    }
}
