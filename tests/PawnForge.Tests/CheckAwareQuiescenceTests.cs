using System.Collections.Generic;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for check-aware quiescence (design review, ). When the side to move is
/// in check, the quiescence search must not stand-pat, must search all legal evasions, and must
/// detect checkmate. When not in check it must be byte-for-byte identical to the previous
/// captures-only behavior. The toggle is <c>UseCheckAwareQuiescence</c>.
/// All FEN fixtures are the oracle-validated (binding project rule #1).
/// </summary>
public class CheckAwareQuiescenceTests
{
    private static QuiescenceSearch CreateQSearch(Position pos, bool checkAware)
    {
        var evaluator = TestHelper.NewEvaluator(pos);
        var qs = new QuiescenceSearch(evaluator, new ThreadData(0), new SearchSignal());
        qs.UseCheckAwareQuiescence = checkAware;
        return qs;
    }

    // Fool's mate: white to move is checkmated (Qh4#). the oracle: mate 0.
    private const string FoolsMate = "rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3";

    /// <summary>
    /// With check-aware quiescence ON, a checkmated node is detected INSIDE quiescence and scored
    /// as mate (-31000 at ply 0), rather than being mistaken for a quiet position.
    /// </summary>
    [Fact]
    public void CheckAware_DetectsCheckmateInQuiescence()
    {
        Stockfish.AssertCheckmate(FoolsMate);
        var pos = Position.FromFEN(FoolsMate);
        var qs = CreateQSearch(pos, checkAware: true);

        int score = qs.Search(pos, -31000, 31000, ply: 0, qDepth: 0);

        Assert.Equal(-31000, score); // mated at ply 0
    }

    /// <summary>
    /// Demonstrates the horizon bug the feature fixes: with check-aware OFF (captures-only),
    /// the same checkmated node is NOT recognized as mate - it stands pat and returns a
    /// non-mate static score. This is exactly why the feature exists.
    /// </summary>
    [Fact]
    public void CapturesOnly_MissesCheckmateInQuiescence()
    {
        Stockfish.AssertCheckmate(FoolsMate);
        var pos = Position.FromFEN(FoolsMate);
        var qs = CreateQSearch(pos, checkAware: false);

        int score = qs.Search(pos, -31000, 31000, ply: 0, qDepth: 0);

        Assert.True(score > -30000,
            $"captures-only quiescence should NOT detect the mate (it stands pat); got {score}");
    }

    /// <summary>
    /// When the side to move is NOT in check, check-aware quiescence must be identical to the
    /// captures-only behavior (the in-check branch is the only difference). Verified on a normal
    /// midgame position that is not in check.
    /// </summary>
    [Fact]
    public void NotInCheck_ToggleMakesNoDifference()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4";
        var posOn = Position.FromFEN(fen);
        var posOff = Position.FromFEN(fen);

        int on = CreateQSearch(posOn, checkAware: true).Search(posOn, -31000, 31000, 0, 0);
        int off = CreateQSearch(posOff, checkAware: false).Search(posOff, -31000, 31000, 0, 0);

        Assert.Equal(off, on);
    }
}
