using System;
using PawnForge.Core.Board;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// The single-response extension's fast predicate must answer EXACTLY the
/// question the old full-list call answered: "is the pseudo-legal move count
/// one?". A wrong fast path would silently change the search tree, so the
/// oracle here is the generator itself.
///
/// Two kinds of coverage:
/// * a deterministic random walk over the shared FEN list — the ordinary
///   case, where the fast count bails out at the second move;
/// * hand-made positions in which the side to move has exactly ONE
///   pseudo-legal move. A walk almost never reaches one (the king is walled in
///   by its own pieces), so without these the "one" branch could be wrong and
///   every walk test would still pass.
/// </summary>
public class ForcedReplyCountTests
{
    /// <summary>
    /// White: Ka1, Na2, Nb1, Nb2, Ph6; Black: Ra8, Bc3, Rh1, Kh8.
    ///
    /// Each white knight is pinned by a different black slider (Na2 by Ra8 on
    /// the a-file, Nb1 by Rh1 on rank 1, Nb2 by Bc3 on the a1-h8 diagonal), and
    /// a pinned knight has no move. The king has no square left (a2/b1/b2 are
    /// its own pieces), so the single pawn push h6-h7 is the only pseudo-legal
    /// move. The position is quiet — no side is in check — and the FEN is
    /// Stockfish-verified at test time.
    /// </summary>
    private const string OnePawnMove =
        "r6k/8/7P/8/8/2b5/NN6/KN5r w - - 0 1";

    private static int FullPseudoLegalCount(Position pos)
    {
        Span<Move> buffer = stackalloc Move[256];
        return pos.GeneratePseudoLegalMoves(buffer);
    }

    private static void AssertMatchesFullGenerator(Position pos, string context)
    {
        bool fast = pos.HasExactlyOnePseudoLegalMove();
        int full = FullPseudoLegalCount(pos);
        Assert.True(fast == (full == 1),
            $"fast predicate said {fast} but the generator found {full} pseudo-legal moves ({context})");
    }

    [Theory]
    [InlineData(OnePawnMove)]
    public void HandMadeOneMovePositions_AreCountedAsOne(string fen)
    {
        Stockfish.VerifyFen(fen);
        var pos = Position.FromFEN(fen);
        Assert.Equal(1, FullPseudoLegalCount(pos));
        Assert.True(pos.HasExactlyOnePseudoLegalMove());
    }

    /// <summary>
    /// Every node of a deterministic random walk must agree with the full
    /// generator. The walk is advanced with LEGAL moves, so every FEN stays
    /// playable; the predicate is still compared against the pseudo-legal list
    /// (the extension's contract).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomWalks_MatchTheFullGeneratorAtEveryNode(int seed)
    {
        var random = new Random(seed);
        // Once per test, not per ply (CA2014: the xunit worker stack is small).
        Span<Move> moves = stackalloc Move[256];

        foreach (string fen in NnueTestFens.All)
        {
            var pos = Position.FromFEN(fen);
            AssertMatchesFullGenerator(pos, $"setup of {fen}");

            for (int step = 0; step < 48; step++)
            {
                int count = pos.GenerateLegalMoves(moves);
                if (count == 0)
                    break;

                Move move = moves[random.Next(count)];
                Piece captured = pos.MakePublicMove(move);
                AssertMatchesFullGenerator(pos, $"{fen} step {step} after {move}");
                pos.UnmakePublicMove(move, captured);
            }
        }
    }
}
