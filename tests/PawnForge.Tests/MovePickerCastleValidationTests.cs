using PawnForge.Core.Board;
using PawnForge.Core.Search;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// B2-class regression tests:
/// a table-sourced (TT) castle move must pass FULL castling legality before the picker emits
/// it — MakeMove executes castles unconditionally (hardcoded rook teleport), so a stale or
/// collided TT castle used to silently corrupt the board. The wrong-colour branch was already
/// blocked (IsMovePseudoLegal's colour check); these tests pin the geometry/rights/path/check
/// branches that the old "Trust flag for now" line skipped.
/// </summary>
public class MovePickerCastleValidationTests
{
    private static bool TtMoveEmitted(string fen, Move ttMove)
    {
        var pos = Position.FromFEN(fen);
        var picker = new MovePicker(
            pos, ttMove, killers: null, history: null, counterMove: Move.None,
            contHist: null, contHist2: null, contHist4: null, contHist6: null,
            captureHistory: null, onlyCaptures: false,
            genBuf: new Move[256], capsBuf: new ScoredMove[64], quietsBuf: new ScoredMove[128],
            badBuf: new Move[64]);
        var first = picker.Next();
        return !first.IsNone && first.Equals(ttMove);
    }

    [Theory]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", 4, 6, true)]    // full rights, clean path
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", 4, 2, true)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w - - 0 1", 4, 6, false)]      // no rights — the old bug
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w - - 0 1", 4, 2, false)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b kq - 0 1", 60, 62, true)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b - - 0 1", 60, 62, false)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", 4, 7, false)]   // not castle geometry
    public void TtCastle_Validity(string fen, int from, int to, bool expected)
    {
        bool emitted = TtMoveEmitted(fen, new Move(from, to, IsCastle: true));
        Assert.Equal(expected, emitted);
    }

    [Fact]
    public void TtCastle_PathBlocked_QueenSide_Rejected()
    {
        // b1 occupied → queenside transit not empty (a1 rook still home, rights granted)
        Assert.False(TtMoveEmitted("r3k2r/8/8/8/8/8/8/RNP1K2R w KQkq - 0 1", new Move(4, 2, IsCastle: true)));
    }

    [Fact]
    public void TtCastle_RookMissing_Rejected()
    {
        // h1 rook gone (captured earlier in the subtree), right flag still set (stale)
        Assert.False(TtMoveEmitted("r3k2r/8/8/8/8/8/8/R3K3 w KQkq - 0 1", new Move(4, 6, IsCastle: true)));
        // queenside still legal
        Assert.True(TtMoveEmitted("r3k2r/8/8/8/8/8/8/R3K3 w KQkq - 0 1", new Move(4, 2, IsCastle: true)));
    }

    [Fact]
    public void TtCastle_KingInCheck_Rejected()
    {
        // Black rook e8... no: rook on e-file attacking e1 directly
        Assert.False(TtMoveEmitted("4r3/8/8/8/8/8/8/R3K2R w KQkq - 0 1", new Move(4, 6, IsCastle: true)));
    }

    [Fact]
    public void TtCastle_TransitAttacked_Rejected()
    {
        // Black rook f8 → f-file: f1 attacked → king would cross an attacked square
        Assert.False(TtMoveEmitted("5r2/8/8/8/8/8/8/R3K2R w KQkq - 0 1", new Move(4, 6, IsCastle: true)));
        // queenside unaffected → still emitted
        Assert.True(TtMoveEmitted("5r2/8/8/8/8/8/8/R3K2R w KQkq - 0 1", new Move(4, 2, IsCastle: true)));
    }
}
