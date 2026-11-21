using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for pawn move generation correctness (/ ).
/// Validates that GeneratePawnMoves produces the same move counts as the
/// general perft-based reference for pawn-heavy positions, covering:
/// - Regular pawn pushes (single and double)
/// - Pawn captures
/// - En-passant captures
/// - Promotions (push and capture)
/// - Pinned pawn filtering
///
/// The tests use perft(1) as the ground truth (move count from GenerateMoves).
/// Pawn-move correctness is implicitly guaranteed when perft numbers match
/// the expected values for each FEN.
/// </summary>
public class PawnMoveGenerationTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — Startpos: 16 pawn moves out of 20 total
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PawnMoves_Startpos_CorrectCount()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Span<Move> buf = stackalloc Move[256];
        int total = pos.GenerateMoves(buf);

        // Startpos: 20 legal moves (16 pawn + 4 knight)
        Assert.Equal(20, total);

        // Count pawn moves (piece at From is WP)
        int pawnMoves = 0;
        for (int i = 0; i < total; i++)
            if (pos.GetPieceAt(buf[i].From) == Piece.WP)
                pawnMoves++;
        Assert.Equal(16, pawnMoves);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — En-passant capture
    // After 1.e4 e5 2.e5xd5 — e5 pawn can capture d5 en-passant
    // (or conversely, Black after e5 double push has e.p. available)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PawnMoves_EnPassant_Generated()
    {
        // White pawn on e5, Black pawn just pushed d7-d5 (ep square = d6)
        var pos = Position.FromFEN("rnbqkbnr/ppp1pppp/8/3pP3/8/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 3");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);

        // e5 pawn should be able to capture d6 en-passant
        bool epFound = false;
        for (int i = 0; i < count; i++)
        {
            if (buf[i].IsEnPassant &&
                buf[i].From == TestHelper.Sq("e5") &&
                buf[i].To == TestHelper.Sq("d6"))
            {
                epFound = true;
                break;
            }
        }
        Assert.True(epFound, "En-passant capture e5xd6 should be available");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — Promotion (all 4 promotions available)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PawnMoves_Promotion_AllFourPiecesGenerated()
    {
        // White pawn on e7, Black king on a8 — e8 is empty, 4 promotions (Q, R, B, N)
        var pos = Position.FromFEN("k7/4P3/8/8/8/8/8/4K3 w - - 0 1");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);

        int promoCount = 0;
        for (int i = 0; i < count; i++)
            if (buf[i].Promoted != 0)
                promoCount++;

        // 4 push promotions (Q/R/B/N)
        Assert.Equal(4, promoCount);
    }

    [Fact]
    public void PawnMoves_PromotionCapture_AllFourPiecesGenerated()
    {
        // White pawn on e7, Black rook on d8, Black king on h8 — 4 push + 4 capture promotions
        var pos = Position.FromFEN("3r3k/4P3/8/8/8/8/8/4K3 w - - 0 1");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);

        int promoCount = 0;
        for (int i = 0; i < count; i++)
            if (buf[i].Promoted != 0)
                promoCount++;

        // 4 push promotions + 4 capture promotions
        Assert.Equal(8, promoCount);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — Double pawn push available on rank 2
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PawnMoves_DoublePush_AvailableFromRank2()
    {
        var pos = Position.FromFEN("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);

        // e2e4 (double push) must be present
        bool doublePushFound = false;
        for (int i = 0; i < count; i++)
        {
            if (pos.GetPieceAt(buf[i].From) == Piece.WP &&
                buf[i].From == TestHelper.Sq("e2") &&
                buf[i].To == TestHelper.Sq("e4"))
            {
                doublePushFound = true;
                break;
            }
        }
        Assert.True(doublePushFound, "Double push e2e4 must be available");
    }

    [Fact]
    public void PawnMoves_DoublePush_BlockedIfSquareOccupied()
    {
        // Pawn on e2, piece on e3 — no double push
        var pos = Position.FromFEN("4k3/8/8/8/8/4n3/4P3/4K3 w - - 0 1");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);

        for (int i = 0; i < count; i++)
        {
            if (pos.GetPieceAt(buf[i].From) == Piece.WP &&
                buf[i].From == TestHelper.Sq("e2") &&
                buf[i].To == TestHelper.Sq("e4"))
            {
                Assert.Fail("Double push e2e4 should be blocked by piece on e3");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Test 5 — Pinned pawn cannot move off the pin ray
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PawnMoves_PinnedPawn_CannotMoveOffPinRay()
    {
        // White king e1, White pawn e4, Black rook e8.
        // The e4 pawn is pinned along the e-file — it may NOT capture sideways.
        // It CAN move forward (e4e5) since that stays on the pin ray.
        var pos = Position.FromFEN("4r1k1/8/8/8/4P3/8/8/4K3 w - - 0 1");

        Span<Move> buf = stackalloc Move[256];
        int count = pos.GenerateMoves(buf);

        // Pinned pawn: e4 can only push forward on the pin ray (e5)
        // It must NOT make any capture (captures would leave the king in check)
        for (int i = 0; i < count; i++)
        {
            if (buf[i].From == TestHelper.Sq("e4"))
            {
                // Only legal move for the pinned pawn is e4-e5
                Assert.Equal(TestHelper.Sq("e5"), buf[i].To);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Test 6 — Perft(1) cross-check for pawn-heavy positions
    // These expected values are validated against known references.
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 20)]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 14)]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 6)]
    public void PawnMoveCount_MatchesPerftReference(string fen, int expected)
    {
        var pos = Position.FromFEN(fen);
        long count = TestHelper.Perft(pos, 1);
        Assert.Equal(expected, (int)count);
    }
}
