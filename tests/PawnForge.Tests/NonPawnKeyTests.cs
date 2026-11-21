using PawnForge.Core.Board;
using Xunit;

namespace PawnForge.Tests;
/// <summary>
/// Incremental per-color non-pawn material keys (Position.NonPawnKeyWhite/Black) — the
/// the reference <c>BoardState.nonPawnKeys[0/1]</c> 1:1 port added for the 4-term correction
/// history. The incremental XORs (move out / capture / place /
/// castling rook) must match the from-scratch recompute exactly after every move, and
/// unmake must restore them wholesale — a drift here would silently poison the non-pawn
/// correction terms.
/// </summary>
public class NonPawnKeyTests
{
    [Fact]
    public void RandomGame_IncrementalNonPawnKeys_MatchFromScratch()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var rng = new Random(7);
        Span<Core.Board.Move> buffer = stackalloc Core.Board.Move[256];

        for (int ply = 0; ply < 160; ply++)
        {
            int count = pos.GenerateMoves(buffer);
            if (count == 0) break;

            Core.Board.Move m = buffer[rng.Next(count)];
            pos.MakePublicMove(m);

            var fresh = Position.FromFEN(pos.ToFEN());
            Assert.True(pos.NonPawnKeyWhite == fresh.NonPawnKeyWhite,
                $"White non-pawn key drift at ply {ply}: incremental=0x{pos.NonPawnKeyWhite:X16} fresh=0x{fresh.NonPawnKeyWhite:X16} (FEN {pos.ToFEN()})");
            Assert.True(pos.NonPawnKeyBlack == fresh.NonPawnKeyBlack,
                $"Black non-pawn key drift at ply {ply}: incremental=0x{pos.NonPawnKeyBlack:X16} fresh=0x{fresh.NonPawnKeyBlack:X16} (FEN {pos.ToFEN()})");
            Assert.True(pos.NonPawnKey(0) == pos.NonPawnKeyWhite && pos.NonPawnKey(1) == pos.NonPawnKeyBlack);
        }
    }

    [Fact]
    public void Unmake_RestoresNonPawnKeys()
    {
        var pos = Position.FromFEN("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1");
        ulong whiteBefore = pos.NonPawnKeyWhite;
        ulong blackBefore = pos.NonPawnKeyBlack;

        Span<Core.Board.Move> buffer = stackalloc Core.Board.Move[256];
        // Play castling + captures + promotions through several random plies, then
        // unmake everything — keys must return to the starting values exactly.
        var played = new List<(Core.Board.Move m, Piece captured)>();
        var rng = new Random(11);
        for (int i = 0; i < 12; i++)
        {
            int count = pos.GenerateMoves(buffer);
            if (count == 0) break;
            Core.Board.Move m = buffer[rng.Next(count)];
            Piece captured = pos.MakePublicMove(m);
            played.Add((m, captured));
        }
        for (int i = played.Count - 1; i >= 0; i--)
            pos.UnmakePublicMove(played[i].m, played[i].captured);

        Assert.Equal(whiteBefore, pos.NonPawnKeyWhite);
        Assert.Equal(blackBefore, pos.NonPawnKeyBlack);
    }

    [Fact]
    public void Pawns_DoNotTouchNonPawnKeys()
    {
        var pos = Position.FromFEN("8/8/8/8/8/8/P7/8 w - - 0 1"); // pawn-only position
        ulong w = pos.NonPawnKeyWhite, b = pos.NonPawnKeyBlack;
        Assert.Equal(0UL, w);
        Assert.Equal(0UL, b);

        Span<Core.Board.Move> buffer = stackalloc Core.Board.Move[256];
        int count = pos.GenerateMoves(buffer);
        for (int i = 0; i < count; i++)
        {
            if (!buffer[i].IsEnPassant && buffer[i].Promoted == 0)
            {
                pos.MakePublicMove(buffer[i]);
                Assert.Equal(0UL, pos.NonPawnKeyWhite);
                Assert.Equal(0UL, pos.NonPawnKeyBlack);
                pos.UnmakePublicMove(buffer[i], Piece.Empty);
                break;
            }
        }
    }
}
