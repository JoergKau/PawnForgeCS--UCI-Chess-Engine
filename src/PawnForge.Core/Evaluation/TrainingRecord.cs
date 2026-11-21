namespace PawnForge.Core.Evaluation;

using PawnForge.Core.Board;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

// ============================================================================
// TrainingRecord.cs — the packed 72-byte training record
// ----------------------------------------------------------------------------
// Ported from the retired previous-generation engine (Stage 2.1 format), unchanged in
// LAYOUT — the parity tooling and every existing
// datagen shard depend on these exact bytes:
//
// offset size field
// 0 64 squares — piece code PER SQUARE: 0 = empty, 1..6 = WP..WK,
// 7..12 = BP..BK (EXACTLY the C# Piece enum values)
// 64 1 us — side to move (0 white, 1 black)
// 65 1 ep — en-passant FILE + 1, 0 = none (file only: the
// target square's rank is implied by `us`)
// 66 2 score — i16, side-to-move-relative centipawns (teacher)
// 68 2 wdl — u16 game result: 0 loss, 1 draw, 2 win (stm-rel.)
// 70 1 castling — rights bitfield in the 1=WK 2=WQ 4=BK 8=BQ order
// (PawnForge's Position.CastlingRights bit order is
// already this order); lets the trainer/datagen
// reconstruct the FULL position — the feature
// extractor does NOT consume it
// 71 1 padding — versioning headroom
//
// Little-endian throughout (the net file and every consumer are LE).
// Adaptations for PawnForge: side to move is the int 0/1 (no Color enum) and
// castling rights the Position.CastlingRights int bitmask.
// ============================================================================

/// <summary>
/// Packed 72-byte NNUE training record writer, shared with the parity tooling
/// (the contract — keep BOTH sides in lockstep). The datagen console
/// (<c>src/PawnForge.Datagen</c>) is the C# writer; `
/// is the reader twin.
/// </summary>
public static class TrainingRecord
{
    /// <summary>Total record size in bytes.</summary>
    public const int Size = 72;

    /// <summary>Game-result values (side-to-move-relative),.</summary>
    public const ushort WdlLoss = 0;
    public const ushort WdlDraw = 1;
    public const ushort WdlWin = 2;

    /// <summary>
    /// Writes one record. <paramref name="sideToMove"/> is 0 white / 1 black;
    /// <paramref name="epSquare"/> is −1 for none; <paramref name="score"/> must
    /// fit i16; <paramref name="wdl"/> must be 0..2; <paramref name="rights"/>
    /// is the CastlingRights bitmask (WK=1, WQ=2, BK=4, BQ=8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> dst, ReadOnlySpan<Piece> mailbox, int sideToMove,
                             int epSquare, int score, ushort wdl, int rights)
    {
        if (dst.Length < Size)
            throw new ArgumentException($"destination needs {Size} bytes", nameof(dst));
        if (sideToMove is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(sideToMove), $"{sideToMove} is not a side");
        if (score < short.MinValue || score > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(score), $"{score} does not fit i16");
        if (wdl > WdlWin)
            throw new ArgumentOutOfRangeException(nameof(wdl), $"{wdl} is not a WDL value");

        dst.Clear();
        for (int sq = 0; sq < 64; sq++)
            dst[sq] = (byte)mailbox[sq]; // Piece enum values == record piece codes
        dst[64] = sideToMove == 0 ? (byte)0 : (byte)1;
        dst[65] = epSquare >= 0 ? (byte)((epSquare & 7) + 1) : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(dst[66..], (short)score);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[68..], wdl);
        dst[70] = (byte)(rights & 0b1111);
        // byte 71 stays zero (versioning headroom)
    }
}
