using System.Numerics;
using System.Runtime.CompilerServices;

namespace PawnForge.Core.Board;

/// <summary>
/// Bitboard utility helpers: least-significant-bit extraction, population
/// count and single-bit tests. All hot-path operations, all branch-free
/// where possible (TrailingZeroCount/PopCount compile to single CPU
/// instructions on x64 and ARM64).
///
/// SQUARE CONVENTION (whole engine): A1 = 0, B1 = 1, …, H8 = 63
/// (little-endian rank-file mapping; square = rank * 8 + file).
/// </summary>
public static class Bitboard
{
    public const ulong Full = 0xFFFFFFFFFFFFFFFFUL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(ulong b) => BitOperations.PopCount(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LsbIndex(ulong b)
    {
        if (b == 0) return -1;
        return BitOperations.TrailingZeroCount(b);
    }

    public static int MsbIndex(ulong b)
    {
        if (b == 0) return -1;
        return 63 - BitOperations.LeadingZeroCount(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Lsb(ulong b)
    {
        return b & (ulong)-(long)b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ClearLsb(ulong b)
    {
        return b & (b - 1);
    }

    public static ulong SetSquare(ulong b, int sq) => b | (1UL << sq);
    public static ulong ClearSquare(ulong b, int sq) => b & ~(1UL << sq);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(ulong b, int sq) => ((b >> sq) & 1UL) != 0;
}