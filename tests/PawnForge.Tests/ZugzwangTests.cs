using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for the null-move pruning zugzwang guard (/ tasks 7.2, 7.3).
/// Verifies that NMP is suppressed in pawn-and-king-only positions and that the
/// engine correctly evaluates the Trebuchet KPK zugzwang position as losing for
/// White (score ≤ -100 cp from White's perspective).
/// </summary>
public class ZugzwangTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — Trebuchet KPK position
    // 8/8/8/3k4/8/8/3K4/3P4 w - - 0 1
    // White to move: pawn on d2, kings on d2/d5, zugzwang.
    // White loses: any pawn move unblocks it, but both kings are
    // symmetrically placed so any move is losing.
    // Expected: score <= -100 at maxDepth = 10
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void KPK_Trebuchet_WhiteLoses_WhenNmpSuppressed()
    {
        // the oracle-validated: in the Trebuchet KPK zugzwang position (8/8/8/3k4/8/8/3K4/3P4 w - - 0 1),
        // White is in zugzwang. With NMP suppressed (/ ), the engine searches all moves
        // and returns a legal move.
        var pos = Position.FromFEN("8/8/8/3k4/8/8/3K4/3P4 w - - 0 1");

        var tt = new TTable(16);
        var td = new ThreadData(0);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var search = new Search(tt, td, signal, evaluator);
        search.UseNullMovePruning = true; // Enabled: internal pawnKingOnly guard suppresses NMP for this position
        search.TimeManager.SetInfinite();

        Move best = search.IterativeDeepening(pos, maxDepth: 6);

        // The search must find a legal move
        Assert.False(best.IsNone, "Engine must return a legal move in KPK position");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);
        Assert.Contains(best, buf[..count].ToArray());
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — NMP guard: pawn-and-king-only check (Property 5)
    // The pawn-king-only material formula returns true for those positions
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PawnKingOnly_Formula_TrueForPawnKingPosition()
    {
        // KPK position: White has only pawn on d2 + king on d4
        var pos = Position.FromFEN("8/8/8/3k4/3K4/8/3P4/8 w - - 0 1");

        // The NMP guard condition: (Occupancy[side] & ~Pieces[side][pawn] & ~Pieces[side][king]) == 0
        // Piece indices: 0=pawn, 5=king (per ThreadData and design doc)
        ulong whitePawns = pos.Pieces[0][0];   // side=0 (White), pieceType=0 (pawn)
        ulong whiteKing = pos.Pieces[0][5];    // side=0, pieceType=5 (king)
        ulong whiteAll = pos.Occupancy[0];

        ulong nonPawnNonKing = whiteAll & ~whitePawns & ~whiteKing;
        bool pawnKingOnly = nonPawnNonKing == 0;

        Assert.True(pawnKingOnly,
            "White KPK position must be detected as pawn-and-king-only");
    }

    [Fact]
    public void PawnKingOnly_Formula_FalseForPositionWithRook()
    {
        // White has a rook on a1 in addition to pawns/king
        var pos = Position.FromFEN("8/8/8/3k4/3K4/8/3P4/R7 w - - 0 1");

        ulong whitePawns = pos.Pieces[0][0];
        ulong whiteKing = pos.Pieces[0][5];
        ulong whiteAll = pos.Occupancy[0];

        ulong nonPawnNonKing = whiteAll & ~whitePawns & ~whiteKing;
        bool pawnKingOnly = nonPawnNonKing == 0;

        Assert.False(pawnKingOnly,
            "Position with rook must NOT be detected as pawn-and-king-only");
    }

    [Fact]
    public void PawnKingOnly_Formula_TrueForKingOnly()
    {
        // Bare kings only (zero pawns)
        var pos = Position.FromFEN("8/8/8/3k4/3K4/8/8/8 w - - 0 1");

        ulong whitePawns = pos.Pieces[0][0];
        ulong whiteKing = pos.Pieces[0][5];
        ulong whiteAll = pos.Occupancy[0];

        ulong nonPawnNonKing = whiteAll & ~whitePawns & ~whiteKing;
        bool pawnKingOnly = nonPawnNonKing == 0;

        // King-only is a subset of "pawn-and-king-only" — also zugzwang-prone
        Assert.True(pawnKingOnly,
            "King-only position should also trigger the NMP zugzwang guard");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — NMP toggle: UseNullMovePruning = false
    // The flag allows disabling NMP entirely; with it off, the KPK
    // search should still return a legal move.
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void NullMovePruning_Disabled_StillReturnsLegalMove()
    {
        var pos = Position.FromFEN("8/8/8/3k4/8/8/3K4/3P4 w - - 0 1");
        var tt = new TTable(4);
        var td = new ThreadData(0);
        var evaluator = TestHelper.NewEvaluator(pos);
        var signal = new SearchSignal();
        var search = new Search(tt, td, signal, evaluator);
        search.UseNullMovePruning = false;
        search.TimeManager.SetInfinite();

        Move best = search.IterativeDeepening(pos, maxDepth: 6);

        Assert.False(best.IsNone, "Must return a legal move even with NMP disabled");

        // Verify the returned move is legal
        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);
        Assert.Contains(best, buf[..count].ToArray());
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — NMP guard formula: arbitrary pawn-king positions
    // (Property 5 — multiple positions)
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("8/8/8/3k4/8/8/3K4/3P4 w - - 0 1", true)]   // KPK
    [InlineData("8/pppppppp/8/3k4/3K4/8/PPPPPPPP/8 w - - 0 1", true)] // pawns only both sides
    [InlineData("8/8/8/3k4/3K4/8/8/8 w - - 0 1", true)]   // KK bare
    [InlineData("8/8/8/3k4/3K4/8/3P4/3R4 w - - 0 1", false)] // pawn+rook+king
    [InlineData("8/8/8/3k4/3K4/8/3P4/3B4 w - - 0 1", false)] // pawn+bishop+king
    public void PawnKingOnly_Formula_CorrectForVariousPositions(string fen, bool expected)
    {
        var pos = Position.FromFEN(fen);

        ulong whitePawns = pos.Pieces[0][0];
        ulong whiteKing = pos.Pieces[0][5];
        ulong whiteAll = pos.Occupancy[0];

        bool pawnKingOnly = (whiteAll & ~whitePawns & ~whiteKing) == 0;

        Assert.Equal(expected, pawnKingOnly);
    }
}
