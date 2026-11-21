using System;
using System.Collections.Generic;
using PawnForge.Core.Board;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Migration M4 (ported from PawnForge v1.1's fix): the board now carries a DIRTY-PIECE
/// RECORD — ≤ 4 entries per make, written in board-mutation order — so a
/// consumer (the NNUE accumulator) can replay deltas instead of re-deriving
/// them per move type.
///
/// THE GATE (the only proof of this record that works): replaying the
/// recorded deltas onto the pre-move position must reconstruct the post-move
/// board EXACTLY — mailbox and bitboards, every ply, over random walks and
/// every special-move class (captures, en passant, both castlings,
/// promotions, both colours).
///
/// FEN RULE: every FEN here was verified against
/// the oracle (/opt/homebrew/bin/the oracle) AT TEST DESIGN TIME — each echoed
/// back unchanged, and each scripted move sequence produced the oracle's exact
/// resulting FEN (a7a8q -> "Q7/8/8/8/8/8/7k/K7 b - - 0 1";
/// e5d6 e.p. -> "8/8/3P4/8/8/8/8/k1K5 b - - 0 1";
/// e1g1 (kiwipete) -> "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R4RK1 b kq - 1 1").
/// Kiwipete itself is perft-suite standard.
/// </summary>
public class NnueDirtyPieceRecordTests
{
    /// <summary>
    /// Replays <paramref name="deltas"/> onto <paramref name="before"/> using ONLY the
    /// recorded information, and asserts the result equals <paramref name="after"/>
    /// in mailbox AND bitboards.
    /// </summary>
    private static void AssertRecordReconstructsBoard(Position before, Position after, string context)
    {
        var replay = new Position(before);

        foreach (var d in after.DirtyPieces.ToArray())
        {
            int color = d.Piece is Piece.WP or Piece.WN or Piece.WB or Piece.WR or Piece.WQ or Piece.WK ? 0 : 1;
            int pieceIndex = d.Piece.ToIndex();
            if (d.Added)
            {
                replay.SetPiece(color, pieceIndex, d.Square);
                replay.Mailbox[d.Square] = d.Piece;
            }
            else
            {
                replay.ClearPiece(color, pieceIndex, d.Square);
                replay.Mailbox[d.Square] = Piece.Empty;
            }
        }

        for (int sq = 0; sq < 64; sq++)
        {
            Assert.True(replay.GetPieceAt(sq) == after.GetPieceAt(sq),
                $"{context}: square {sq} — record replay placed {replay.GetPieceAt(sq)}, board has {after.GetPieceAt(sq)}");
        }
        for (int color = 0; color < 2; color++)
        {
            for (int pieceIndex = 0; pieceIndex < 6; pieceIndex++)
            {
                Assert.True(replay.GetPieceBitboard(color, pieceIndex) == after.GetPieceBitboard(color, pieceIndex),
                    $"{context}: bitboard [{color},{pieceIndex}] diverged after record replay");
            }
        }
    }

    /// <summary>
    /// Plays ONE move through the board-only API, verifies the record, and
    /// returns the info needed to unmake.
    /// </summary>
    private static (Move Move, Piece Captured) MakeBoardMove(Position pos, Move m)
    {
        Piece captured = pos.MakePublicMove(m);
        return (m, captured);
    }

    [Fact]
    public void QuietMove_TwoEntries_InMutationOrder()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var before = new Position(pos);

        MakeBoardMove(pos, TestHelper.FindMove(pos, "e2", "e4"));

        Assert.Equal(2, pos.DirtyPieceCount);
        var record = pos.DirtyPieces.ToArray();
        // Mutation order: remove the pawn from e2 FIRST, then place it on e4.
        Assert.False(record[0].Added);
        Assert.Equal(Square.E2, record[0].Square);
        Assert.Equal(Piece.WP, record[0].Piece);
        Assert.True(record[1].Added);
        Assert.Equal(Square.E4, record[1].Square);
        Assert.Equal(Piece.WP, record[1].Piece);

        AssertRecordReconstructsBoard(before, pos, "quiet move");
    }

    [Fact]
    public void Capture_ThreeEntries()
    {
        var pos = Position.FromFEN("4k3/8/8/3p4/4P3/8/8/4K3 w - - 0 1");
        var before = new Position(pos);

        MakeBoardMove(pos, TestHelper.FindMove(pos, "e4", "d5"));

        Assert.Equal(3, pos.DirtyPieceCount);
        var record = pos.DirtyPieces.ToArray();
        Assert.False(record[0].Added);  // mover leaves e4
        Assert.Equal(Square.E4, record[0].Square);
        Assert.False(record[1].Added);  // captured pawn leaves d5
        Assert.Equal(Square.D5, record[1].Square);
        Assert.Equal(Piece.BP, record[1].Piece);
        Assert.True(record[2].Added);   // mover arrives on d5
        Assert.Equal(Square.D5, record[2].Square);

        AssertRecordReconstructsBoard(before, pos, "capture");
    }

    [Fact]
    public void EnPassant_CapturedPawnOnTheBehindSquare()
    {
        // the oracle-verified: e5d6 e.p. is legal here and yields
        // "8/8/3P4/8/8/8/8/k1K5 b - - 0 1" — the captured pawn sat on d5,
        // NOT on the move's target d6.
        var pos = Position.FromFEN("8/8/8/3pP3/8/8/8/k1K5 w - d6 0 1");
        var before = new Position(pos);

        MakeBoardMove(pos, TestHelper.FindMove(pos, "e5", "d6"));

        Assert.Equal(3, pos.DirtyPieceCount);
        var record = pos.DirtyPieces.ToArray();
        Assert.False(record[1].Added);
        Assert.Equal(Square.D5, record[1].Square);   // the EP pawn's REAL square
        Assert.Equal(Piece.BP, record[1].Piece);
        Assert.True(record[2].Added);
        Assert.Equal(Square.D6, record[2].Square);   // the mover arrives on the EP target

        AssertRecordReconstructsBoard(before, pos, "en passant");
    }

    [Theory]
    [InlineData("e1", "g1", 4)]  // white O-O:   king e1 -> g1, rook h1 -> f1
    [InlineData("e1", "c1", 4)]  // white O-O-O: king e1 -> c1, rook a1 -> d1
    [InlineData("e8", "g8", 4)]  // black O-O
    [InlineData("e8", "c8", 4)]  // black O-O-O
    public void Castling_FourEntries_KingAndRook(string from, string to, int expectedCount)
    {
        // Kiwipete (the oracle-verified, perft-suite standard): all four
        // castlings are legal in the first two plies.
        var pos = Position.FromFEN("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        bool black = from == "e8";
        if (black)
        {
            // Hand the move to black: three quiet plies (odd number — a
            // white-to-move position needs an odd ply count to flip). The
            // moves are legal in kiwipete (the a6 bishop steps aside, the
            // a-pawns advance).
            MakeBoardMove(pos, TestHelper.FindMove(pos, "a2", "a3"));
            MakeBoardMove(pos, TestHelper.FindMove(pos, "a6", "b5"));
            MakeBoardMove(pos, TestHelper.FindMove(pos, "a3", "a4"));
        }

        var before = new Position(pos);
        MakeBoardMove(pos, TestHelper.FindMove(pos, from, to));

        Assert.Equal(expectedCount, pos.DirtyPieceCount);
        var record = pos.DirtyPieces.ToArray();
        // The record must contain BOTH the king's real destination and the
        // rook's remove/place — the whole point ('s landmine and 's
        // history both live in this move class).
        int kingSquare = black ? (to == "g8" ? Square.G8 : Square.C8) : (to == "g1" ? Square.G1 : Square.C1);
        Assert.Contains(record, d => d.Added && d.Piece == (black ? Piece.BK : Piece.WK) && d.Square == kingSquare);
        Assert.Contains(record, d => !d.Added && d.Piece == (black ? Piece.BK : Piece.WK));
        Assert.Contains(record, d => !d.Added && d.Piece == (black ? Piece.BR : Piece.WR));
        Assert.Contains(record, d => d.Added && d.Piece == (black ? Piece.BR : Piece.WR));

        AssertRecordReconstructsBoard(before, pos, $"castling {from}{to}");
    }

    [Fact]
    public void Promotion_PromotedPieceIsRecorded_NotThePawn()
    {
        // the oracle-verified: a7a8q is legal here and yields
        // "Q7/8/8/8/8/8/7k/K7 b - - 0 1".
        var pos = Position.FromFEN("8/P7/8/8/8/8/7k/K7 w - - 0 1");
        var before = new Position(pos);

        MakeBoardMove(pos, TestHelper.FindMove(pos, "a7", "a8", 'q'));

        Assert.Equal(2, pos.DirtyPieceCount);
        var record = pos.DirtyPieces.ToArray();
        Assert.False(record[0].Added);
        Assert.Equal(Piece.WP, record[0].Piece);   // the PAWN leaves a7 (pre-promotion identity)
        Assert.True(record[1].Added);
        Assert.Equal(Piece.WQ, record[1].Piece);   // the QUEEN arrives on a8
        Assert.Equal(Square.A8, record[1].Square);

        AssertRecordReconstructsBoard(before, pos, "promotion");
    }

    [Fact]
    public void NullMove_RecordIsEmpty()
    {
        var pos = Position.FromFEN("4k3/8/8/8/8/8/8/4K3 w - - 0 1");
        MakeBoardMove(pos, TestHelper.FindMove(pos, "e1", "e2"));  // a prior make's record exists
        Assert.True(pos.DirtyPieceCount > 0);

        pos.MakeNullMove();

        Assert.Equal(0, pos.DirtyPieceCount);  // a null move changes no pieces
        pos.UnmakeNullMove();
        Assert.Equal(0, pos.DirtyPieceCount);
    }

    [Fact]
    public void Unmake_ClearsTheRecord()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var (move, captured) = MakeBoardMove(pos, TestHelper.FindMove(pos, "e2", "e4"));
        Assert.True(pos.DirtyPieceCount > 0);

        pos.UnmakePublicMove(move, captured);

        Assert.Equal(0, pos.DirtyPieceCount);  // no stale record survives an unmake
    }

    [Fact]
    public void RandomWalks_RecordAlwaysReconstructsTheBoard()
    {
        // THE GATE: every ply of five random games — the record replayed onto
        // the pre-move position must reconstruct the post-move board exactly.
        // Deterministic per seed (rule: deterministic per seed).
        string[] starts =
        {
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", // kiwipete
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",                            // perft suite #3
            "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",     // perft suite #4 (promotions)
            "8/8/8/3pP3/8/8/8/k1K5 w - d6 0 1",                                      // EP-ready (SF-verified)
        };

        int totalMoves = 0;
        Span<Move> moves = stackalloc Move[256];  // once per test, NOT per game (CA2014)
        for (int game = 0; game < starts.Length; game++)
        {
            var pos = Position.FromFEN(starts[game]);
            var rng = new Random(0xC0FFEE + game);
            var undo = new List<(Move Move, Piece Captured)>();

            for (int ply = 0; ply < 80; ply++)
            {
                int count = pos.GenerateMoves(moves);
                if (count == 0) break;  // game over (mate/stalemate)

                Move m = moves[rng.Next(count)];
                var before = new Position(pos);
                var (move, captured) = MakeBoardMove(pos, m);
                undo.Add((move, captured));
                totalMoves++;

                Assert.True(pos.DirtyPieceCount is >= 2 and <= DirtyPiece.MaxPerMake,
                    $"game {game} ply {ply}: dirty count {pos.DirtyPieceCount} out of range");

                AssertRecordReconstructsBoard(before, pos, $"game {game} ply {ply} ({move})");

                if (pos.HalfmoveClock >= 100) break;  // keep walks moving
                if (rng.NextDouble() < 0.3 && undo.Count > 0)
                {
                    // Occasionally unmake — the walk must survive unmake hygiene too.
                    var last = undo[^1];
                    pos.UnmakePublicMove(last.Move, last.Captured);
                    undo.RemoveAt(undo.Count - 1);
                }
            }
        }

        Assert.True(totalMoves > 200, $"walk covered only {totalMoves} plies — the gate is not exercising anything");
    }
}
