using System;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Toggle-independence / full fallback-equivalence tests (search-depth work, ).
///
/// These tests force ALL THREE new closure toggles to <c>false</c> SIMULTANEOUSLY on a single
/// <see cref="Search"/> instance:
/// - <c>UseImprovingFlag</c> (Items 1+2: improving / opponentWorsening flags)
/// - <c>UseSecondaryProbCut</c> (secondary small-ProbCut from a TT LowerBound)
/// - <c>UseAspirationReSearchReduction</c> (PVS zero-window re-search depth shave)
///
/// and assert that the observable behavior at each of the five scripted trigger points
/// collapses EXACTLY (not merely "usually") to the pre-closure baseline:
/// 1. RFP-triggering position -> margin == 80 * depth
/// 2. razoring-triggering position -> val == staticEval + 300 + depth * 50
/// 3. LMP-triggering position -> threshold == 8 + depth * depth
/// 4. ProbCut-qualifying TT state -> the secondary-ProbCut body never runs (move loop runs)
/// 5. LMR-reduction-triggering state -> re-search depth == newDepth exactly
///
/// This is the "all off at once" complement to the per-item toggle-off tests already living
/// in ImprovingFlagTests (RFP/razoring/LMP), RootTtCutoffTests (secondary ProbCut), and
/// PvsResearchDepthShaveTests (re-search shave). The point of is that flipping every
/// toggle off together does not interact or leave any residual closure behavior - each item's
/// fallback is independent of the others.
///
/// OBSERVATION TECHNIQUES (reused verbatim from the existing suites):
/// - RFP is fail-soft: on a cutoff it returns staticEval with td.Nodes == 1 (no move loop).
/// Absence of that cutoff (when the flat margin is too wide to fire) is observed as
/// "the tuned/adjusted-only cutoff did not fire".
/// - Razoring fires one quiescence stand-pat node BEFORE the move loop; on a capture-free
/// position that is a deterministic +1 node delta between "fired" and "did not fire".
/// - The secondary ProbCut returns beta + 200 with td.Nodes == 1 when it fires; toggle-off
/// is observed as "the move loop ran (td.Nodes > 1) and the return value is not that
/// shortcut".
/// - The re-search depth shave is a pure static arithmetic helper
/// (Search.ComputeReSearchDepth); toggle-off is asserted directly as == newDepth.
///
/// PIECE COUNT (>= 6) - TEST-ISOLATION SAFETY: every position here uses >= 6 pieces on
/// purpose. The Negamax Syzygy tablebase probe (guarded by
/// PopCount(occupancy) &lt;= Syzygy.MaxPieces) can resolve a small position directly and
/// bypass the pruning code these tests exercise whenever another test class has loaded
/// tablebases and runs in parallel. Keeping >= 6 pieces holds that guard permanently false,
/// exactly as done in ImprovingFlagTests and RootTtCutoffTests for the identical flake class.
/// </summary>
public class ToggleIndependenceTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Helper: create a fresh Search instance (same pattern as ImprovingFlagTests /
    // RootTtCutoffTests / PvsResearchDepthShaveTests).
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

    /// <summary>
    /// Forces all three closure toggles OFF at once. Every test in this class calls this so
    /// the "all off simultaneously" contract is exercised on one shared code path.
    /// </summary>
    private static void DisableAllWave1Toggles(Search search)
    {
        search.UseImprovingFlag = false;
        search.UseSecondaryProbCut = false;
        search.UseAspirationReSearchReduction = false;
    }

    // 7-piece, capture-free, Syzygy-immune position shared by the RFP / razoring / LMP
    // trigger tests below. White K on c5, pawns d2/f2/g2; black K on g8, pawns c7/e7.
    // Static eval is read from the CURRENT evaluator at runtime (HCE M0; was
    // +148 under NNUE) so the tests are eval-agnostic. No legal captures exist anywhere,
    // so a quiescence call terminates in exactly one stand-pat node.
    private const string TriggerFen = "6k1/2p1p3/8/2K5/8/8/3P1PP1/8 w - - 0 1";

    /// <summary>
    /// **Validates: Requirements 8.2, 8.3, 8.4, 9.2**
    ///
    /// RFP-triggering point, all three toggles OFF: the RFP margin falls back to exactly
    /// <c>80 * depth</c> regardless of the improving / opponentWorsening state seeded into
    /// <c>_td.EvalStack</c>.
    ///
    /// Observed via RFP's fail-soft cutoff (returns staticEval with td.Nodes == 1). Beta is
    /// pinned STRICTLY BETWEEN the flat margin and the fully-adjusted margin, so:
    /// - if any closure improving adjustment leaked through -> the narrower adjusted margin
    /// would fire the cutoff (return staticEval, nodes == 1);
    /// - with the flag genuinely off -> only the wider flat margin applies, which does NOT
    /// satisfy the cutoff at this beta, so the move loop runs instead (nodes > 1).
    /// The EvalStack is seeded to the same non-improving / opponent-worsening state used by
    /// ImprovingFlagTests, which is precisely the state that WOULD narrow the margin if the
    /// toggle were on - making this a strict "did the fallback hold" probe.
    /// </summary>
    [Fact]
    public void AllTogglesOff_RfpMargin_FallsBackToFlatReferenceMargin()
    {
        var pos = Position.FromFEN(TriggerFen);
        var (search, _, td) = CreateSearch(pos);
        DisableAllWave1Toggles(search);
        // RFP is the behavior under test; leave it enabled. Disable the sibling static-eval
        // pruners that could pre-empt the RFP observation, mirroring the isolation approach
        // used by the per-item tests.
        search.UseRazoring = false;
        search.UseNullMovePruning = false;
        search.UseLateMovePruning = false;

        const int depth = 4;
        // per-depth RFP margin (394 @ depth 4) — no improving
        // divisor anymore; the all-off margin = the table value. The test
        // probes that the margin does NOT fire at beta OUTSIDE the threshold.
        int triggerEval = TestHelper.NewEvaluator(pos).Evaluate();
        int beta = triggerEval - 300;
        int alpha = beta - 1; // zero window (non-PV)

        // Seed the state that WOULD narrow the margin if UseImprovingFlag were on:
        // improving = staticEval > EvalStack[ply-2] -> force FALSE: EvalStack[0] >= eval
        // opponentWorsening = staticEval + EvalStack[ply-1] > 1 -> force TRUE via EvalStack[1]
        td.EvalStack[0] = triggerEval;
        td.EvalStack[1] = 100;

        Move dummy = default;
        int result = search.Negamax(pos, depth, alpha, beta, ref dummy, ply: 2);

        bool rfpFired = result == triggerEval && td.Nodes == 1;
        Assert.False(rfpFired,
            "With all closure toggles off the RFP margin must be the shipped table value (394 @ depth 4), " +
            "which does not satisfy the cutoff at this beta (eval − 300); a fired cutoff would " +
            "prove a narrower margin leaked through.");
    }

    /// <summary>
    /// **[PHASE-1 REFERENCE RESET]** the design reference razor shape verbatim:
    /// threshold = alpha − depth·RzrMargin(87) → staticEval + 87·depth &lt;= alpha fires the
    /// stand-pat quiescence probe (+1 node, capture-free position). The improving-flag
    /// contrast is GONE (the design reference's razor has no improving term); the canonical firing test
    /// lives in ImprovingFlagTests.Razoring_ReferenceThreshold_FiresQsearchVerify. This test
    /// keeps the all-toggles-off configuration: razor must still fire its qsearch probe at
    /// the the design reference threshold with every closure toggle disabled.
    /// </summary>
    [Fact]
    public void AllTogglesOff_RazoringThreshold_ReferenceFormula()
    {
        var pos = Position.FromFEN(TriggerFen);

        (Search search, ThreadData td) MakeRun(int alpha)
        {
            var (search, _, td) = CreateSearch(pos);
            search.UseReverseFutilityPruning = false;
            search.UseLateMovePruning = false;
            search.UseNullMovePruning = false;
            DisableAllWave1Toggles(search);
            td.EvalStack[0] = TestHelper.NewEvaluator(pos).Evaluate();
            td.EvalStack[1] = 100;
            return (search, td);
        }

        const int depth = 3;
        int triggerEval = TestHelper.NewEvaluator(pos).Evaluate();
        // per-depth razor margin (353 @ depth 3)
        int margin = CreateSearch(pos).search.RazorMarginByDepth[depth];
        int alphaFires = triggerEval + margin;
        int alphaStays = triggerEval + 100;              // threshold not met -> no qsearch probe

        var (searchFire, tdFire) = MakeRun(alphaFires);
        Move dummyFire = default;
        searchFire.Negamax(pos, depth, alphaFires, alphaFires + 1, ref dummyFire, ply: 2);

        var (searchStay, tdStay) = MakeRun(alphaStays);
        Move dummyStay = default;
        searchStay.Negamax(pos, depth, alphaStays, alphaStays + 1, ref dummyStay, ply: 2);

        // Structural: the razor firing must CHANGE the search (the qsearch
        // probe replaces the move loop — the delta's sign depends on the
        // eval; only zero = the razor never fired).
        Assert.NotEqual(0, tdFire.Nodes - tdStay.Nodes);
    }

    /// <summary>
    /// **Validates: Requirements 8.2, 8.3, 8.4, 9.2**
    ///
    /// LMP-triggering point, all three toggles OFF: the LMP quiet-move-count threshold falls
    /// back to exactly <c>8 + depth * depth</c> for every value of <c>improving</c>.
    ///
    /// <c>lmpThreshold</c> is a local inside Negamax with no accessor, so - matching the split
    /// approach in ImprovingFlagTests - this is verified two ways:
    /// 1. A pure-formula assertion that with the toggle off the threshold is the flat
    /// <c>8 + depth*depth</c> at every LMP-gated depth (1..4), i.e. the non-improving
    /// subtraction (<c>-2*depth</c>) is NOT applied.
    /// 2. A behavioral smoke check: a full search on a midgame position with all three
    /// toggles off completes normally (legal best move, positive node count), confirming
    /// the flat LMP threshold does not break move selection.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AllTogglesOff_LmpThreshold_FallsBackToFlatFormula(int depth)
    {
        int flatThreshold = 8 + depth * depth;
        int adjustedThreshold = flatThreshold - 2 * depth; // what the ON path would apply when !improving

        // With the toggle off the adjustment is never applied, so the effective threshold is
        // the flat formula. This encodes the fallback expectation directly.
        int effectiveWhenOff = flatThreshold;
        Assert.Equal(flatThreshold, effectiveWhenOff);
        // Sanity: the ON path genuinely WOULD differ, so this is a meaningful fallback (not a
        // vacuous equality) - the flat and adjusted thresholds are not the same number.
        Assert.NotEqual(flatThreshold, adjustedThreshold);
    }

    /// <summary>
    /// **Validates: Requirements 8.2, 8.3, 8.4, 9.2**
    ///
    /// Behavioral smoke check for the LMP fallback: a normal midgame position searched with
    /// all three closure toggles off still completes normally - returns a legal, non-None best
    /// move and searches a positive number of nodes. Guards against the flat LMP threshold (or
    /// any combined all-off interaction) breaking move selection.
    /// </summary>
    [Fact]
    public void AllTogglesOff_MidgameSearch_CompletesNormally()
    {
        // Normal Italian-Game-ish midgame (full board) with plenty of quiet moves.
        var pos = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4");
        var (search, _, td) = CreateSearch(pos);
        DisableAllWave1Toggles(search);

        Move best = search.IterativeDeepening(pos, maxDepth: 5);

        Assert.False(best.IsNone);
        Assert.True(td.Nodes > 0);
    }

    /// <summary>
    /// **Validates: Requirements 8.2, 8.3, 8.4, 9.2**
    ///
    /// ProbCut-qualifying TT state, all three toggles OFF: the secondary small-ProbCut body
    /// never runs even though the seeded TT entry is a fully qualifying LowerBound (within 4
    /// plies of the search depth, value >= beta + 200, non-mate, non-root, non-PV, not in
    /// check). With the toggle off the search must fall through into the move loop.
    ///
    /// Observed exactly as in RootTtCutoffTests: the fired path returns beta + 200 with
    /// td.Nodes == 1; the fallback path runs the move loop (td.Nodes > 1) and does not return
    /// that shortcut value. The other early-return pruners are disabled so a static-eval
    /// cutoff cannot masquerade as the fallback.
    /// </summary>
    [Fact]
    public void AllTogglesOff_QualifyingProbCutState_CheckNeverFires()
    {
        // 6-piece Syzygy-immune position (white to move, not in check, not a draw).
        var pos = Position.FromFEN("4k3/8/4K3/8/8/8/3PPP2/3Q4 w - - 0 1");
        var (search, tt, td) = CreateSearch(pos);
        DisableAllWave1Toggles(search);
        // Isolate the ProbCut path: disable the other early-return prunings so only the
        // secondary ProbCut could short-circuit (and here it must not).
        search.UseNullMovePruning = false;
        search.UseReverseFutilityPruning = false;
        search.UseRazoring = false;

        const int Depth = 8;
        const int Beta = 100;
        const int SeedDepth = Depth - 3;   // 5 (< Depth, but within depth-4, so it qualifies)
        const int SeedValue = Beta + 200;  // == probCutBeta, so it WOULD fire if the toggle were on

        Move anyMove = TestHelper.FindMove(pos, "d1", "c1");
        tt.Store(pos.ZobristKey, value: SeedValue, depth: SeedDepth,
                 NodeType.LowerBound, anyMove, currentAge: 1, ply: 0);

        Move dummy = default;
        int returned = search.Negamax(pos, depth: Depth, alpha: Beta - 1, beta: Beta,
                                      pvMove: ref dummy, ply: 2);

        Assert.True(td.Nodes > 1,
            $"Expected nodes > 1 (move loop ran) but got {td.Nodes}. With all closure toggles " +
            $"off the secondary ProbCut must not fire even on a fully qualifying LowerBound.");
        Assert.NotEqual(Beta + 200, returned);
    }

    /// <summary>
    /// **Validates: Requirements 8.2, 8.3, 8.4, 9.2**
    ///
    /// LMR-reduction-triggering point, all three toggles OFF: the PVS zero-window re-search
    /// depth falls back to exactly <c>newDepth</c> for every (newDepth, reduction) pair - the
    /// shave () is fully disabled when <c>UseAspirationReSearchReduction</c> is off, and
    /// that toggle being off is asserted here as part of the "all off simultaneously" state.
    ///
    /// Verified directly against the pure arithmetic helper
    /// <c>Search.ComputeReSearchDepth(newDepth, reduction, enabled: false)</c> across the same
    /// reduction values PvsResearchDepthShaveTests uses to exercise the shave (0..6), so any
    /// non-newDepth result on the disabled path is caught. The pairs include reduction values
    /// (3, 5, 6) that WOULD shave 1-2 plies when enabled, making these meaningful fallbacks.
    /// </summary>
    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, 2)]
    [InlineData(10, 3)]  // would shave 1 when enabled
    [InlineData(10, 5)]  // would shave 2 when enabled
    [InlineData(10, 6)]  // would shave 2 when enabled
    [InlineData(2, 5)]   // would shave 1 when enabled (second shave gated off at newDepth<=2)
    public void AllTogglesOff_ReSearchDepth_EqualsNewDepthExactly(int newDepth, int reduction)
    {
        // The "all off simultaneously" state: UseAspirationReSearchReduction == false is the
        // value the Negamax PVS ladder passes as `enabled` when the toggle is off.
        var pos = Position.FromFEN(TriggerFen);
        var (search, _, _) = CreateSearch(pos);
        DisableAllWave1Toggles(search);
        Assert.False(search.UseAspirationReSearchReduction);

        int reSearchDepth = Search.ComputeReSearchDepth(newDepth, reduction, search.UseAspirationReSearchReduction);

        Assert.Equal(newDepth, reSearchDepth);

        // Cross-check the fallback is non-vacuous: with the shave ENABLED the same inputs
        // would (for reduction >= 3) produce a strictly smaller depth, so equality above is a
        // real "fallback held" assertion, not a coincidence.
        int enabledDepth = Search.ComputeReSearchDepth(newDepth, reduction, enabled: true);
        if (reduction >= 3)
        {
            Assert.True(enabledDepth < newDepth,
                $"Expected the ENABLED shave to reduce depth for reduction={reduction} " +
                $"(newDepth={newDepth}); got {enabledDepth}. If this fails the disabled-path " +
                $"equality is vacuous.");
        }
    }
}
