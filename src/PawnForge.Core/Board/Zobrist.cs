using System;

namespace PawnForge.Core.Board;

/// <summary>
/// Zobrist hashing tables: random 64-bit keys XORed incrementally into the
/// position hash on every make/unmake.
///
/// KEY COMPONENTS (all XOR-composable):
/// PieceSquare[13, 64] piece (colored WP..BK, index 1-12) on square
/// Castling[4] the four castling rights (WK/WQ/BK/BQ)
/// EnPassantFile[8] an available en-passant FILE (not square)
/// SideToMove XORed when black is to move
///
/// GENERATION: deterministic XorShift64 PRNG seeded with
/// 0x243F6A8885A308D3 (the first hex digits of pi) - identical keys on every
/// machine and run, which keeps test node counts reproducible.
///
/// NOTE: the EP key is XORed after EVERY double push, even when no enemy
/// pawn can actually capture en passant - a deliberate KnightByte-parity
/// choice that makes repetition detection slightly stricter than FIDE.
/// </summary>
public static class Zobrist
{
    // deterministic random generator for reproducibility
    private const ulong Seed = 0x243F6A8885A308D3UL; // arbitrary
    public static readonly ulong[,] PieceSquare = new ulong[13,64]; // index by Piece enum
    public static readonly ulong[] EnPassantFile = new ulong[8];
    public static readonly ulong[] Castling = new ulong[4]; // K,Q,k,q
    public static ulong Side;

    static Zobrist()
    {
        Initialize(Seed);
    }

    public static void Initialize(ulong seed)
    {
        var rnd = new XorShift64(seed);
        for (int p = 0; p < 13; p++)
            for (int s = 0; s < 64; s++)
                PieceSquare[p,s] = rnd.NextULong();
        for (int i = 0; i < 8; i++) EnPassantFile[i] = rnd.NextULong();
        for (int i = 0; i < 4; i++) Castling[i] = rnd.NextULong();
        Side = rnd.NextULong();
    }

    private class XorShift64
    {
        private ulong state;
        public XorShift64(ulong seed) => state = seed ^ 0x9E3779B97F4A7C15UL;
        public ulong NextULong()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return state;
        }
    }
}