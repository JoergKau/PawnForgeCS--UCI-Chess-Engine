using PawnForge.Core.Board;

namespace PawnForge.Core.Book;

/// <summary>
/// Computes the ChessBase CTG position signature - the lookup key inside a
/// .ctg opening book. The signature is a Huffman-coded bit stream of the 64
/// board squares followed by castling / en-passant flag bits, stored as a
/// byte sequence whose first byte holds the signature length plus two flag
/// bits (bit 6 = castling present, bit 5 = en-passant present).
///
/// CTG convention: every position is stored in a canonical "white to move"
/// form, so before encoding the board is
/// 1. FLIPPED (ranks mirrored) when black is to move, and
/// 2. MIRRORED (files mirrored) when the side-to-move king stands on the
/// queenside (files a-d) AND neither side has any castling rights.
/// Piece colors are swapped together with the rank flip.
///
/// Bit encoding is LSB-first into a big-endian byte stream (the first code
/// bit lands on the MSB of its byte); the board starts at bit position 8
/// because byte 0 is reserved for the header. Trailing padding is inserted
/// so the flag bits always end flush at a byte boundary.
///
/// Format reference (web pages used to derive this encoding):
/// • https://chessprogramming.org/CTG (format overview)
/// • http://rybkaforum.net/cgi-bin/rybkaforum/topic_show.pl?tid=2319
/// (Sesse's leaked CTG specification, 2007)
/// • http://www.open-aurec.com/wbforum/viewtopic.php?f=2&t=50747
/// (Brutus v8.5 source release - Stephan Vermeire's original
/// reverse-engineering of the bit encoding)
/// The Huffman code table below is a format constant shared by every CTG
/// implementation; the surrounding code is PawnForge's own work.
/// </summary>
public static class CtgSignature
{
    /// <summary>Huffman code + bit length per <see cref="Piece"/> value (index 0 = Empty, 1..12 = WP..BK).</summary>
    private static readonly (int Bits, int Len)[] Huffman =
    {
        (0x0, 1),   // Empty
        (0x3, 3),   // WP   (stream bits 110)
        (0x9, 5),   // WN   (10010)
        (0x5, 5),   // WB   (10100)
        (0xD, 5),   // WR   (10110)
        (0x11, 6),  // WQ   (100010)
        (0x1, 6),   // WK   (100000)
        (0x7, 3),   // BP   (111)
        (0x19, 5),  // BN   (10011)
        (0x15, 5),  // BB   (10101)
        (0x1D, 5),  // BR   (10111)
        (0x31, 6),  // BQ   (100011)
        (0x21, 6),  // BK   (100001)
    };

    /// <summary>
    /// True when the position must be horizontally mirrored before encoding:
    /// the side-to-move king is on files a-d and no castling rights remain.
    /// </summary>
    public static bool IsMirrored(Position pos)
    {
        if (pos.CastlingRights != 0)
            return false;
        Piece king = pos.SideToMove == 0 ? Piece.WK : Piece.BK;
        var board = pos.Mailbox;
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
                if (board[r * 8 + f] == king)
                    return f < 4;
        return false;
    }

    /// <summary>
    /// Encodes <paramref name="pos"/> into <paramref name="buffer"/> (must be
    /// at least 32 bytes) and returns the signature length in bytes. The
    /// buffer is zeroed first; a signature is at most 31 bytes for any legal
    /// chess position (fits the 5-bit length field of the header byte).
    /// </summary>
    public static int Compute(Position pos, Span<byte> buffer)
    {
        buffer.Clear();
        bool flip = pos.SideToMove == 1;
        bool mirror = IsMirrored(pos);
        var board = pos.Mailbox;

        int bitPos = 8; // byte 0 is reserved for the header
        for (int f = 0; f < 8; f++)
        {
            for (int r = 0; r < 8; r++)
            {
                // canonical square after flip (rank) / mirror (file)
                int cf = mirror ? 7 - f : f;
                int cr = flip ? 7 - r : r;
                Piece piece = board[cr * 8 + cf];
                if (flip && piece != Piece.Empty)
                    piece = (Piece)((int)piece < 7 ? (int)piece + 6 : (int)piece - 6);
                (int bits, int len) = Huffman[(int)piece];
                AppendBits(buffer, ref bitPos, bits, len);
            }
        }

        // Castling / en-passant flags, canonically from the "white" side.
        int castle = 0;
        int castleRights = pos.CastlingRights;
        if (flip)
        {
            // canonical white = black: black castling is white's, and vice versa
            if ((castleRights & 4) != 0) castle += 4;   // BK -> white O-O
            if ((castleRights & 8) != 0) castle += 8;   // BQ -> white O-O-O
            if ((castleRights & 1) != 0) castle += 1;   // WK
            if ((castleRights & 2) != 0) castle += 2;   // WQ
        }
        else
        {
            if ((castleRights & 1) != 0) castle += 4;   // WK
            if ((castleRights & 2) != 0) castle += 8;   // WQ
            if ((castleRights & 4) != 0) castle += 1;   // BK
            if ((castleRights & 8) != 0) castle += 2;   // BQ
        }

        int epFile = -1;
        if (pos.EnPassantSquare >= 0 && EnPassantCapturePossible(pos))
        {
            epFile = pos.EnPassantSquare % 8;
            if (mirror)
                epFile = 7 - epFile;
        }

        int flagBits = castle;
        int flagLen = castle != 0 ? 4 : 0;
        if (epFile >= 0)
        {
            flagBits = (castle << 3) | epFile;
            flagLen += 3;
        }

        // Pad so the flags end flush at the last byte boundary.
        int rem = bitPos % 8;
        if (8 - rem < flagLen)
        {
            int pad = 8 - rem;
            for (int i = 0; i < pad; i++)
                AppendBits(buffer, ref bitPos, 0, 1);
        }
        rem = bitPos % 8;
        int pad2 = (8 - rem - flagLen) % 8;
        for (int i = 0; i < pad2; i++)
            AppendBits(buffer, ref bitPos, 0, 1);

        // Append flag bits LSB-first (castle bits first, then the ep file).
        for (int i = 0; i < flagLen; i++)
            AppendBits(buffer, ref bitPos, (flagBits >> i) & 1, 1);

        int nbytes = (bitPos + 7) / 8;
        byte header = (byte)nbytes;
        if (epFile >= 0) header |= 1 << 5;
        if (castle != 0) header |= 1 << 6;
        buffer[0] = header;
        return nbytes;
    }

    /// <summary>
    /// ChessBase only records the en-passant square when an en-passant capture
    /// is actually possible (a pawn of the side to move stands on an adjacent
    /// file, on the rank of the doubled pawn). PawnForge, like many engines,
    /// keeps the EP square after every double push; canonicalize it to the
    /// book convention here.
    /// </summary>
    private static bool EnPassantCapturePossible(Position pos)
    {
        int ep = pos.EnPassantSquare;
        int epFile = ep % 8;
        int epRank = ep / 8;
        // The doubled pawn is one rank ahead of the EP square; the capturing
        // pawn (of the side to move) stands on that rank, adjacent file.
        int pawnRank = pos.SideToMove == 0 ? epRank - 1 : epRank + 1;
        if (pawnRank < 0 || pawnRank > 7)
            return false;
        Piece capturer = pos.SideToMove == 0 ? Piece.WP : Piece.BP;
        var board = pos.Mailbox;
        for (int df = -1; df <= 1; df += 2)
        {
            int f = epFile + df;
            if (f >= 0 && f < 8 && board[pawnRank * 8 + f] == capturer)
                return true;
        }
        return false;
    }

    /// <summary>Appends <paramref name="numBits"/> bits of <paramref name="bits"/> LSB-first at <paramref name="bitPos"/>.</summary>
    private static void AppendBits(Span<byte> buffer, ref int bitPos, int bits, int numBits)
    {
        for (int i = 0; i < numBits; i++, bitPos++)
        {
            if ((bits & (1 << i)) != 0)
                buffer[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
        }
    }
}
