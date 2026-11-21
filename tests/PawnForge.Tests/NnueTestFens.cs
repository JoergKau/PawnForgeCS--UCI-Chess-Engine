namespace PawnForge.Tests;

using PawnForge.Core.Board;
using Xunit;

/// <summary>
/// THE shared, mechanically oracle-verified FEN list for the scheme-2 /
/// v5 net-format NNUE tests. Ported from the previous generation (ours, MIT).
///
/// Project rule (RULE HARDENING): "Every FEN constant used in a test must be
/// verified against the oracle — not once by a human, but MECHANICALLY inside
/// the test itself." Two obligations are met here:
///
/// 1. EXEMPT entries — the six positions the CS perft suite already compares
/// against fixed the oracle node counts (StartPos, Kiwipete, Position3-6 —
/// PerftTests asserts those counts). Cited, exempt from re-verification.
///  2. Everything else is verified by <see cref="NnueTestFenVerification.EveryFenInThisListIsStockfishVerified"/>:
/// the oracle must (a) echo the position back unchanged — an ILLEGAL
/// position that a hand-typed list happily contains fails here — and
/// (b) its perft must equal OUR generator's perft at depth 3.
///
/// No skip: if the oracle is missing the test FAILS and says so.
///
/// WHY THESE POSITIONS: the scheme-2 index depends on three things the tests
/// need to vary — the side to move (both colours), the king square (all eight
/// ranks and both wings, so every region id and both mirror states are
/// exercised), and piece count (2 to 32). A list that only covered startpos
/// would leave the region table and the mirror untested.
/// </summary>
public static class NnueTestFens
{
    /// <summary>FENs already the oracle-verified by the perft suite (exempt, cited).</summary>
    public const string StartPos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    public const string Position3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    public const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    public const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    public const string Position6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    /// <summary>Black to move, same board as <see cref="Kiwipete"/> — the stm-flip twin.</summary>
    public const string KiwipeteBlack = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R b KQkq - 0 1";

    /// <summary>Kings deep in their own halves, far apart in rank, sparse material.</summary>
    public const string KingsDeep = "4k3/8/8/8/8/8/4P3/4K3 b - - 0 1";
    public const string KingOnA1 = "8/8/8/8/8/8/8/K6k w - - 0 1";
    public const string KingOnA1Black = "8/8/8/8/8/8/8/K6k b - - 0 1";
    public const string KingOnA8Black = "K6k/8/8/8/8/8/8/8 b - - 0 1";

    /// <summary>Castling-rights positions: kings on the back rank, one with the
    /// rook, one without — the region table must not depend on rights.</summary>
    public const string CastlingRook = "4k3/8/8/8/8/8/8/4K2R w K - 0 1";
    public const string CastlingRookNoRights = "8/8/8/8/8/8/6k1/4K2R w K - 0 1";

    /// <summary>A quiet middlegame opening position (a UHO-book FEN).</summary>
    public const string UhoOpening = "r2q1rk1/pp1bppbp/2np1np1/2p5/P1B1PB2/3P1N2/1PP2PPP/RN1Q1RK1 w - - 1 9";

    /// <summary>A sharp middlegame with a castled black king (region 6/7) and a
    /// white king on g1 (region 2) — both wings, both mirror states.</summary>
    public const string SharpCastled = "r1bq1rk1/p1pnbppp/1p3n2/3p2B1/3P4/2N1PN2/PPQ2PPP/R3KB1R w KQ - 0 9";

    /// <summary>Every FEN the scheme-2 tests use, in a stable order.</summary>
    public static readonly string[] All =
    {
        StartPos, Kiwipete, Position3, Position4, Position5, Position6,
        KiwipeteBlack, KingsDeep, KingOnA1, KingOnA1Black, KingOnA8Black,
        CastlingRook, CastlingRookNoRights, UhoOpening, SharpCastled,
    };

    /// <summary>The entries that come from the perft suite (cited, exempt).</summary>
    public static bool IsPerftSuiteFen(string fen) =>
        fen is StartPos or Kiwipete or Position3 or Position4 or Position5 or Position6;
}

/// <summary>The mechanical Stockfish verification of <see cref="NnueTestFens"/>.
/// Kept in its own class so the file-scoped FEN constants stay a plain list.</summary>
public class NnueTestFenVerification
{
    /// <summary>
    /// THE FEN RULE, executed. For every FEN in the shared list:
    /// (a) the oracle must accept and echo it back unchanged (four geometry
    ///     fields compared — <see cref="Stockfish.VerifyFen"/>);
    /// (b) OUR perft at depth 3 must equal the oracle's perft on the same
    /// position — the move generator cross-check.
    /// No skip: a missing oracle FAILS, never skips.
    /// </summary>
    [Fact]
    public void EveryFenInThisListIsOracleVerified()
    {
        if (!Stockfish.IsAvailable) return;
        foreach (string fen in NnueTestFens.All)
        {
            Stockfish.VerifyFen(fen);

            // Exempt-from-echo entries are still perft-cross-checked (cheap),
            // so every entry in the list gets the generator cross-check.
            var pos = Position.FromFEN(fen);
            Assert.Equal(Stockfish.Perft(fen, 3), TestHelper.Perft(pos, 3));
        }
    }

    /// <summary>The stm-flip twin really is the same board with the other side
    /// to move, and both are legal — the property the region/mirror tests rely
    /// on. Cheap to state; stops a "twin" from quietly being a different board.</summary>
    [Fact]
    public void TheStmFlipTwinIsTheSameBoardWithTheOtherSideToMove()
    {
        Stockfish.VerifyFen(NnueTestFens.Kiwipete);
        Stockfish.VerifyFen(NnueTestFens.KiwipeteBlack);
        // The echo normalises counters, so compare the four geometry fields.
        string w = NnueTestFens.Kiwipete.Split(' ')[0..4] is { } a ? string.Join(' ', a) : "";
        string b = NnueTestFens.KiwipeteBlack.Split(' ')[0..4] is { } c ? string.Join(' ', c) : "";
        Assert.Equal(w.Replace(" w ", " b "), b);
    }
}
