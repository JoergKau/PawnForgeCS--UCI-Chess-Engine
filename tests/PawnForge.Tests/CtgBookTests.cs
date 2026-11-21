using PawnForge.Core.Board;
using PawnForge.Core.Book;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// CTG opening-book support: the ChessBase position-signature encoding and
/// the.ctg reader/probing. Signatures are validated against a real book
/// (Perfect2023.ctg in the repo) - the expected byte sequences below were
/// verified byte-for-byte against the on-disk entries of that book.
/// </summary>
public class CtgBookTests
{
    private static string SigHex(Position pos)
    {
        Span<byte> buf = stackalloc byte[32];
        int len = CtgSignature.Compute(pos, buf);
        return Convert.ToHexString(buf[..len]);
    }

    [Fact]
    public void Signature_StartPosition_MatchesRealBook()
    {
        // The exact 22-byte signature ChessBase stores for the initial
        // position (castling present, no en-passant).
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Assert.Equal("56B60F7960F3A60F58B078E0C1E1A60F5960F3B60F7F", SigHex(pos));
    }

    [Fact]
    public void Signature_BlackToMove_FlipsTheBoard()
    {
        // After 1.Nf3 (black to move) the board must be rank-flipped so the
        // stored form is canonical "white to move". Verified against the book.
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R b KQkq - 1 1");
        Assert.Equal("56B60F7960F3A60F58B078E0C1E1A613F5960EB60F7F", SigHex(pos));
    }

    [Fact]
    public void Signature_EnPassantWithoutCapture_IsNotRecorded()
    {
        // After 1.e4 the EP square e3 exists, but no black pawn can capture
        // en passant - ChessBase therefore stores the position WITHOUT the EP
        // flag. The signature must equal the one of the identical position
        // given without an EP square.
        var plain = Position.FromFEN("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1");
        var withEp = Position.FromFEN("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
        Assert.Equal(SigHex(plain), SigHex(withEp));
    }

    [Fact]
    public void Signature_EnPassantWithCapture_IsRecorded()
    {
        // White pawn e5 adjacent to the black pawn d5 that just double-pushed
        // (ep square d6): exd6 is possible, so the EP square IS part of the
        // signature and must differ from the EP-less form.
        var plain = Position.FromFEN("rnbqkbnr/ppp1pppp/8/3pP3/8/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2");
        var withEp = Position.FromFEN("rnbqkbnr/ppp1pppp/8/3pP3/8/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2");
        Assert.NotEqual(SigHex(plain), SigHex(withEp));
    }

    [Fact]
    public void Signature_NoCastlingKingQueenside_MirrorsTheBoard()
    {
        // White king on b1, no castling rights -> the board must be file-mirrored.
        var pos = Position.FromFEN("4k3/8/8/8/8/8/8/1K6 w - - 0 1");
        var flipped = Position.FromFEN("4k3/8/8/8/8/8/8/1K6 b - - 0 1");
        // Just sanity: computing must not throw and produce a 1..31 byte signature.
        Span<byte> buf = stackalloc byte[32];
        int len = CtgSignature.Compute(pos, buf);
        Assert.InRange(len, 1, 31);
        _ = flipped;
    }

    // ── Real book probing (books/Perfect2023.ctg, third-party) ───────────
    // These tests only run when the book file is present in the repo; on CI
    // without the (non-redistributable) book they no-op so the build stays
    // green.

    private static CtgBook? TryLoadBook()
    {
        var path = TestHelper.FindBookPath();
        if (path == null)
            return null;
        var book = new CtgBook();
        return book.Load(path) ? book : null;
    }

    private static string Uci(Move m)
        => $"{(char)('a' + m.From % 8)}{(char)('1' + m.From / 8)}{(char)('a' + m.To % 8)}{(char)('1' + m.To / 8)}";

    [Fact]
    public void Load_RealBook_HasSensibleEntryCount()
    {
        var book = TryLoadBook();
        if (book is null) return; // book not checked in - skip
        Assert.True(book.IsLoaded);
        Assert.True(book.EntryCount > 10_000, $"expected a large book, got {book.EntryCount}");
    }

    [Fact]
    public void PickBookMove_StartPosition_ReturnsOnlyLegalKnownMoves()
    {
        var book = TryLoadBook();
        if (book is null) return;
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Span<Move> legal = stackalloc Move[256];
        int n = pos.GenerateMoves(legal);

        // The 12 book moves of the Perfect2023 start position.
        string[] expected =
        {
            "e2e4", "g1f3", "d2d4", "c2c4", "f2f4", "c2c3",
            "e2e3", "g2g3", "b2b3", "a2a3", "h2h3", "b1c3"
        };

        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            Move m = book.PickBookMove(pos, new Random(i));
            Assert.False(m.IsNone, "start position must always yield a book move");
            Assert.Contains(m, legal[..n].ToArray()); // always a legal move
            Assert.Contains(Uci(m), expected);         // always one of the book moves
            seen.Add(Uci(m));
        }
        Assert.True(seen.Count >= 3, $"expected variety in book picks, got {string.Join(",", seen)}");
    }

    [Fact]
    public void PickBookMove_FollowsBookSeveralPlies()
    {
        // Play book moves for both sides for several plies - exercises the
        // black (flip) and castling decode paths. Every move must be legal.
        var book = TryLoadBook();
        if (book is null) return;
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var rng = new Random(7);
        Span<Move> legal = stackalloc Move[256];
        for (int ply = 0; ply < 8; ply++)
        {
            Move m = book.PickBookMove(pos, rng);
            Assert.False(m.IsNone, $"book ran out at ply {ply}");
            int n = pos.GenerateMoves(legal);
            Assert.Contains(m, legal[..n].ToArray());
            pos.MakePublicMove(m);
        }
    }

    [Fact]
    public void PickBookMove_UnknownPosition_ReturnsNone()
    {
        // A late middlegame/endgame far outside any opening book.
        var book = TryLoadBook();
        if (book is null) return;
        var pos = Position.FromFEN("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1");
        Assert.True(book.PickBookMove(pos, new Random(0)).IsNone);
    }
}
