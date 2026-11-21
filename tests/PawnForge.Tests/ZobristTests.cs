using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Zobrist hash integrity: the incremental key (XORed during make) must
/// always equal the key recomputed from scratch, and make/unmake must
/// restore the key exactly. A broken hash silently poisons the TT.
/// </summary>
public class ZobristTests
{
    [Fact]
    public void MakeUnmake_RestoresZobristKey()
    {
        var pos = Position.FromFEN("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        ulong beforeZobrist = pos.ZobristKey;
        ulong beforePawn = pos.PawnKey;

        Span<Move> buffer = stackalloc Move[256];
        int count = pos.GenerateMoves(buffer);

        for (int i = 0; i < count; i++)
        {
            Move m = buffer[i];
            Piece captured = pos.MakePublicMove(m);
            pos.UnmakePublicMove(m, captured);
            Assert.Equal(beforeZobrist, pos.ZobristKey);
            Assert.Equal(beforePawn, pos.PawnKey);
        }
    }

    [Fact]
    public void IncrementalKey_MatchesRecomputedKey_AfterSpecialMoves()
    {
        // Covers: capture, en passant, castling both sides, promotions,
        // king moves (bucket/mirror crossings). Plays each sequence forward,
        // verifying the incremental key against a from-scratch key at every
        // ply, then unmakes everything and verifies the start key returns.
        var cases = new (string name, string fen, string[] moves)[]
        {
            ("captures", "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
             new[] { "e2e4", "d7d5", "e4d5", "g8f6", "g1f3" }),
            ("en passant", "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
             new[] { "e5f6", "e7e5" }),
            ("castling both sides", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
             new[] { "e1g1", "e8c8" }),
            ("promotions", "n1n5/PPPk4/8/8/8/8/4Kppp/5N1N w - - 0 1",
             new[] { "b7a8q", "c8b6", "c7c8q", "d7d6" }),
            ("king moves", "4k3/8/8/8/8/8/8/4K3 w - - 0 1",
             new[] { "e1e2", "e8e7", "e2f3", "e7f6" }),
        };

        foreach (var (name, fen, moves) in cases)
        {
            var pos = Position.FromFEN(fen);
            ulong startKey = pos.ZobristKey;
            ulong startPawnKey = pos.PawnKey;

            // Play the sequence, keeping the captured pieces for the unmakes.
            var played = new List<(Move Move, Piece Captured)>();
            foreach (string uci in moves)
            {
                Move m = TestHelper.FindMove(pos, uci[..2], uci[2..4], uci.Length > 4 ? uci[4] : null);
                Piece captured = pos.MakePublicMove(m);
                played.Add((m, captured));

                var fresh = Position.FromFEN(pos.ToFEN());
                Assert.True(pos.ZobristKey == fresh.ZobristKey,
                    $"[{name}] Zobrist mismatch after {uci}: incremental=0x{pos.ZobristKey:X16} fresh=0x{fresh.ZobristKey:X16}");
                Assert.True(pos.PawnKey == fresh.PawnKey,
                    $"[{name}] PawnKey mismatch after {uci}: incremental=0x{pos.PawnKey:X16} fresh=0x{fresh.PawnKey:X16}");
            }

            // Unmake the whole sequence - the start key must return exactly.
            for (int i = played.Count - 1; i >= 0; i--)
            {
                pos.UnmakePublicMove(played[i].Move, played[i].Captured);
            }
            Assert.True(pos.ZobristKey == startKey, $"[{name}] key not restored after unmakes");
            Assert.True(pos.PawnKey == startPawnKey, $"[{name}] pawn key not restored after unmakes");
        }
    }

    [Fact]
    public void RandomGame_IncrementalKey_StaysConsistent()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var evaluator = TestHelper.NewEvaluator(pos);

        var rng = new Random(42);
        Span<Move> buffer = stackalloc Move[256];

        for (int ply = 0; ply < 120; ply++)
        {
            int count = pos.GenerateMoves(buffer);
            if (count == 0) break; // game over

            Move m = buffer[rng.Next(count)];
            Piece moving = pos.GetPieceAt(m.From);
            Piece captured = pos.MakePublicMove(m);
            evaluator.MakeMove(pos, m, moving, captured);

            var fresh = Position.FromFEN(pos.ToFEN());
            Assert.True(pos.ZobristKey == fresh.ZobristKey,
                $"Zobrist drift at ply {ply}: incremental=0x{pos.ZobristKey:X16} fresh=0x{fresh.ZobristKey:X16}");
            Assert.True(pos.PawnKey == fresh.PawnKey,
                $"PawnKey drift at ply {ply}: incremental=0x{pos.PawnKey:X16} fresh=0x{fresh.PawnKey:X16}");
        }
    }
}
