using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Regression tests for predecessor bugs 147-149 (2026-11-18), all found through the
/// KnightByte migration and reclassified from "quirk" to "bug" (see the predecessor
/// bug log):
///
/// - Bug 147: the 16-bit TT move packing drops IsEnPassant/IsCastle, so a stored
///   castling or en-passant best move compared unequal to every generated move and was
///   silently dropped as a TT hint. Fix: flags are rebuilt from board context at probe
///   time (TTable.ReStampMoveFlags) when the caller passes the position.
/// - Bug 148: quiescence classified an en-passant capture as quiet (its target square is
///   empty), so the horizon search never examined an en-passant capture. Fix: the quiet
///   test honours Move.IsEnPassant.
/// - Bug 149: the correction-history quiet gate tested the MOVER'S OWN occupancy at the
///   destination (never set for a legal move), so ordinary captures counted as quiet and
///   polluted the correction tables. Fix: the gate tests the opponent occupancy
///   (Search.IsQuietStoreMove).
/// </summary>
public class PredecessorBugs147to149Tests
{
    /// <summary>Castling rights for both sides, white to move (e1g1 / e1c1 available).</summary>
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    /// <summary>
    /// White pawn e5, black pawn d5 (last move d7-d5), en-passant target d6, white to
    /// move. exd6 e.p. is the ONLY capture white has (the e5 pawn's other diagonal, f6,
    /// is empty, and no other white piece can capture). Black is up a full rook, so the
    /// en-passant capture is the one move that improves on stand-pat in quiescence.
    /// </summary>
    private const string EnPassantRookDown = "r3k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1";

    /// <summary>After 1.e4 d5 2.Nf3: black to move, d5xe4 is an ordinary pawn capture.</summary>
    private const string OrdinaryCapture = "rnbqkbnr/ppp1pppp/8/3p4/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 3";

    /// <summary>White pawn a7 ready to promote (a7-a8q), no captures anywhere.</summary>
    private const string PromotionAvailable = "8/P7/8/8/8/8/8/K1k5 w - - 0 1";

    // ─────────────────────────────────────────────────────────────────
    // The FEN rule: every new FEN constant is Stockfish-verified at test
    // time (echo gate + our perft vs the oracle's perft).
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BugFixtureFens_AreOracleVerified()
    {
        if (!Stockfish.IsAvailable) return;
        foreach (string fen in new[] { EnPassantRookDown, OrdinaryCapture, PromotionAvailable, Kiwipete })
        {
            Stockfish.VerifyFen(fen);
            var pos = Position.FromFEN(fen);
            Assert.Equal(Stockfish.Perft(fen, 3), TestHelper.Perft(pos, 3));
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Bug 147 - TT move flags survive the store/probe round-trip
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bug147_TtRoundTrip_PreservesCastleFlag()
    {
        var pos = Position.FromFEN(Kiwipete);
        Move castle = TestHelper.FindMove(pos, "e1", "g1");
        Assert.True(castle.IsCastle, "fixture: e1g1 must be the generated castling move");

        var tt = new TTable(4);
        tt.Store(pos.ZobristKey, value: 42, depth: 6, NodeType.Exact, castle, currentAge: 1);

        Assert.True(tt.Probe(pos.ZobristKey, out var entry, ply: 0, pos));
        Assert.True(entry.BestMove.IsCastle,
            "a stored castling move must come back with IsCastle set (Bug 147 fix)");
        Assert.Equal(castle, entry.BestMove);
    }

    [Fact]
    public void Bug147_TtRoundTrip_PreservesEnPassantFlag()
    {
        var pos = Position.FromFEN(EnPassantRookDown);
        Move ep = TestHelper.FindMove(pos, "e5", "d6");
        Assert.True(ep.IsEnPassant, "fixture: e5d6 must be the generated en-passant move");

        var tt = new TTable(4);
        tt.Store(pos.ZobristKey, value: 17, depth: 4, NodeType.Exact, ep, currentAge: 1);

        Assert.True(tt.Probe(pos.ZobristKey, out var entry, ply: 0, pos));
        Assert.True(entry.BestMove.IsEnPassant,
            "a stored en-passant move must come back with IsEnPassant set (Bug 147 fix)");
        Assert.Equal(ep, entry.BestMove);
    }

    [Fact]
    public void Bug147_ProbeWithoutPosition_KeepsTheDocumentedFlaglessForm()
    {
        var pos = Position.FromFEN(Kiwipete);
        Move castle = TestHelper.FindMove(pos, "e1", "g1");

        var tt = new TTable(4);
        tt.Store(pos.ZobristKey, value: 42, depth: 6, NodeType.Exact, castle, currentAge: 1);

        // Without a position there is no board context to re-stamp from; the flagless
        // form is the documented packed representation (the squares still round-trip).
        Assert.True(tt.Probe(pos.ZobristKey, out var entry, ply: 0));
        Assert.False(entry.BestMove.IsCastle);
        Assert.Equal((castle.From, castle.To), (entry.BestMove.From, entry.BestMove.To));
    }

    [Fact]
    public void Bug147_ReStamp_NeutralForOrdinaryMoves()
    {
        // Ordinary moves must pass through the re-stamp unchanged, and the re-stamp must
        // never invent a flag from a stale or collided entry (empty source square).
        var pos = Position.FromFEN(Kiwipete);
        Move quiet = TestHelper.FindMove(pos, "a2", "a3");
        Move capture = TestHelper.FindMove(pos, "e5", "c6");

        Assert.Equal(quiet, TTable.ReStampMoveFlags(pos, quiet));
        Assert.Equal(capture, TTable.ReStampMoveFlags(pos, capture));

        var empty = Position.FromFEN("8/8/8/8/8/8/8/K1k5 w - - 0 1");
        var stale = new Move(4, 6); // a king-e1-to-g1 shape on an empty board
        Assert.False(TTable.ReStampMoveFlags(empty, stale).IsCastle);
    }

    // ─────────────────────────────────────────────────────────────────
    // Bug 148 - quiescence searches the en-passant capture
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bug148_Quiescence_ImprovesOnStandPatThroughTheEnPassantCapture()
    {
        bool prevProbing = Syzygy.ProbingEnabled;
        try
        {
            // Guard against a parallel Syzygy test enabling tablebase probing (the
            // fixture has five men and must be evaluated by the NNUE, not a probe).
            Syzygy.ProbingEnabled = false;

            var pos = Position.FromFEN(EnPassantRookDown);
            var evaluator = TestHelper.NewEvaluator(pos);
            var qs = new QuiescenceSearch(evaluator, new ThreadData(0), new SearchSignal());
            qs.UseCheckAwareQuiescence = true;
            qs.UseQuiescenceChecks = false; // the shipped default: captures-only quiescence

            int stand = evaluator.Evaluate();
            int score = qs.Search(pos, -31000, 31000, ply: 0, qDepth: 0);

            // The en-passant capture wins a clean pawn (the d6 pawn cannot be recaptured:
            // the a8 rook does not attack d6 and the e8 king is two steps away), and it is
            // white's ONLY capture. Before the Bug 148 fix quiescence skipped it and
            // returned the stand-pat score unchanged.
            Assert.True(score > stand,
                $"quiescence must search the only (en-passant) capture and improve on stand-pat: stand={stand}, qs={score}");
        }
        finally
        {
            Syzygy.ProbingEnabled = prevProbing;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Bug 149 - the correction-history quiet gate classifies captures
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bug149_QuietGate_RejectsAnOrdinaryCapture()
    {
        // Pre-fix this returned TRUE: the gate tested the mover's own occupancy, which is
        // never set at a legal move's destination, so every ordinary capture passed as
        // quiet and its eval-error diff polluted the correction tables.
        var pos = Position.FromFEN(OrdinaryCapture);
        Move capture = TestHelper.FindMove(pos, "d5", "e4");
        Assert.False(Search.IsQuietStoreMove(pos, capture),
            "an ordinary capture must not count as quiet in the correction-history gate");
    }

    [Fact]
    public void Bug149_QuietGate_RejectsEnPassantAndPromotion()
    {
        var epPos = Position.FromFEN(EnPassantRookDown);
        Move ep = TestHelper.FindMove(epPos, "e5", "d6");
        Assert.False(Search.IsQuietStoreMove(epPos, ep),
            "an en-passant capture must not count as quiet in the correction-history gate");

        var promoPos = Position.FromFEN(PromotionAvailable);
        Move promo = TestHelper.FindMove(promoPos, "a7", "a8", 'q');
        Assert.False(Search.IsQuietStoreMove(promoPos, promo),
            "a promotion must not count as quiet in the correction-history gate");
    }

    [Fact]
    public void Bug149_QuietGate_AcceptsAQuietMoveAndMoveNone()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Move quiet = TestHelper.FindMove(pos, "e2", "e3");
        Assert.True(Search.IsQuietStoreMove(pos, quiet),
            "a plain pawn push must count as quiet in the correction-history gate");
        Assert.True(Search.IsQuietStoreMove(pos, Move.None),
            "no stored move must count as quiet (the gate's 'or none' branch)");
    }
}
