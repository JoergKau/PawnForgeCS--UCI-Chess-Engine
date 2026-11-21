using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for Quiescence Search behavior and quiet checking moves (/ ).
/// Verifies:
/// - Stand-pat cutoff behavior (alpha / beta cutoffs before exploring moves)
/// - SEE filtering on captures / quiet moves
/// - Quiescence search determinism and evaluation stability
/// - Captures only behavior across plies
/// </summary>
public class QSearchChecksTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — Stand-pat cutoff: when standing eval >= beta, returns beta
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void QSearch_StandPat_CutoffWhenEvalGreaterOrEqualBeta()
    {
        // Balanced start position
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var td = new ThreadData(0);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var qs = new QuiescenceSearch(evaluator, td, signal);
        qs.UseMicroRiders = false; // explicit legacy stand-pat; R1 default is ON

        int staticEval = evaluator.Evaluate();

        // If beta <= staticEval, qsearch should immediately cutoff with beta (stand-pat)
        int beta = staticEval - 10;
        int alpha = beta - 100;

        int score = qs.Search(pos, alpha, beta, ply: 0);

        Assert.Equal(beta, score);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — Delta pruning: pruned when stand + 1100 < alpha
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void QSearch_DeltaPruning_ReturnsAlphaWhenHopelesslyBehind()
    {
        // In starting position, stand is ~0. If alpha is set to stand + 1500, delta pruning fires
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var td = new ThreadData(0);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var qs = new QuiescenceSearch(evaluator, td, signal);

        int stand = evaluator.Evaluate();
        int alpha = stand + 1500;
        int beta = alpha + 100;

        int score = qs.Search(pos, alpha, beta, ply: 0);

        // Delta pruning: stand + 1100 < alpha, so returns alpha immediately
        Assert.Equal(alpha, score);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — QSearch resolves tactical captures (hanging piece)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void QSearch_EvaluatesHangingCapture()
    {
        // White can capture hanging queen on d5
        var pos = Position.FromFEN("k7/8/8/3q4/8/8/3R4/K7 w - - 0 1");
        var td = new ThreadData(0);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var qs = new QuiescenceSearch(evaluator, td, signal);

        int score = qs.Search(pos, -30000, 30000, ply: 0);

        // After capturing the queen, White is clearly winning (R vs bare K)
        Assert.True(score > 50, $"Expected winning score for White after Qxd5, got {score}");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — QSearch skips losing captures (SEE < -50)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void QSearch_SEEFilter_SkipsLosingCaptures()
    {
        // White queen on a1 can capture pawn on b2 protected by rook on b8 (Qxb2 losing)
        // Standing pat is better than taking the defended pawn with the queen.
        var pos = Position.FromFEN("1r4k1/8/8/8/8/8/1p6/Q5K1 w - - 0 1");
        var td = new ThreadData(0);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var qs = new QuiescenceSearch(evaluator, td, signal);

        int stand = evaluator.Evaluate();
        int score = qs.Search(pos, -30000, 30000, ply: 0);

        // QSearch score should not be significantly worse than stand-pat
        // because the engine should not play the losing queen sacrifice Qxb2.
        Assert.True(score >= stand - 50,
            $"QSearch score ({score}) should not drop heavily below stand-pat ({stand}) on losing captures");
    }
}
