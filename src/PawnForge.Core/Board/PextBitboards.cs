// ═══════════════════════════════════════════════════════════════════════════════
// BOARD · PEXT sliding attacks + runtime magic/PEXT selection
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace PawnForge.Core.Board;

/// <summary>
/// PEXT (BMI2 `pext`) sliding-attack tables plus the
/// runtime micro-benchmark that chooses between them and the magic tables.
///
/// WHY: on CPUs where `pext` is fast (Intel Haswell+), a PEXT lookup replaces the
/// multiply-shift magic index with one instruction; on CPUs where it is slow (AMD Zen 1/2) or
/// unavailable (Apple Silicon, pre-BMI2 x86), the magic path stays. The design reference's
/// PEXT table is the model: build one table per square indexed by the extracted relevant-occupancy
/// bits, then benchmark both implementations and keep the faster.
///
/// CORRECTNESS: the tables are FILLED from <see cref="MagicBitboards"/> itself — every entry is
/// the magic result for the same (square, occupancy subset) — so PEXT and magic are
/// bit-identical by construction. The choice therefore changes only NPS, never the search tree
/// (the fixed-depth identity gate is unaffected).
///
/// DISPATCH: <see cref="AttackDetection"/> reads <see cref="UsePext"/>, which is set once at
/// type init by <see cref="SelectBest"/>. `PAWNFORGE_DISABLE_PEXT=1` forces magic and
/// `PAWNFORGE_FORCE_PEXT=1` forces PEXT (both for A/B diagnostics; PEXT still requires BMI2).
/// </summary>
public static class PextBitboards
{
    private static readonly ulong[] RookMasks = new ulong[64];
    private static readonly ulong[] BishopMasks = new ulong[64];
    private static readonly int[] RookOffsets = new int[64];
    private static readonly int[] BishopOffsets = new int[64];
    private static ulong[] _rookTable = Array.Empty<ulong>();
    private static ulong[] _bishopTable = Array.Empty<ulong>();

    /// <summary>True when the CPU exposes 64-bit BMI2 PEXT and the tables were built.</summary>
    public static bool Ready { get; }

    /// <summary>True when the runtime benchmark selected PEXT over magic.</summary>
    public static bool UsePext { get; private set; }

    static PextBitboards()
    {
        if (!Bmi2.X64.IsSupported) return;

        string? force = Environment.GetEnvironmentVariable("PAWNFORGE_FORCE_PEXT");
        string? disable = Environment.GetEnvironmentVariable("PAWNFORGE_DISABLE_PEXT");

        try
        {
            Build();
        }
        catch
        {
            // A table build failure must degrade to magic, never break the engine.
            return;
        }

        Ready = true;
        UsePext = disable != "1" && (force == "1" || SelectBest());
    }

    /// <summary>Builds the rook/bishop PEXT tables from the magic implementation.</summary>
    private static void Build()
    {
        // Pass 1: sizes/offsets.
        int rookTotal = 0, bishopTotal = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            RookMasks[sq] = GenerateRookMask(sq);
            BishopMasks[sq] = GenerateBishopMask(sq);
            RookOffsets[sq] = rookTotal;
            BishopOffsets[sq] = bishopTotal;
            rookTotal += 1 << BitOperations.PopCount(RookMasks[sq]);
            bishopTotal += 1 << BitOperations.PopCount(BishopMasks[sq]);
        }

        _rookTable = new ulong[rookTotal];
        _bishopTable = new ulong[bishopTotal];

        // Pass 2: fill every subset from the magic tables (bit-identical by construction).
        for (int sq = 0; sq < 64; sq++)
        {
            int rookBits = BitOperations.PopCount(RookMasks[sq]);
            for (int i = 0; i < (1 << rookBits); i++)
            {
                ulong occ = IndexToOccupancy(i, RookMasks[sq]);
                _rookTable[RookOffsets[sq] + i] = MagicBitboards.RookAttacks(sq, occ);
            }

            int bishopBits = BitOperations.PopCount(BishopMasks[sq]);
            for (int i = 0; i < (1 << bishopBits); i++)
            {
                ulong occ = IndexToOccupancy(i, BishopMasks[sq]);
                _bishopTable[BishopOffsets[sq] + i] = MagicBitboards.BishopAttacks(sq, occ);
            }
        }
    }

    /// <summary>Rook attacks via PEXT (caller must check <see cref="Ready"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RookAttacks(int sq, ulong occ)
        => _rookTable[RookOffsets[sq] + (int)Bmi2.X64.ParallelBitExtract(occ, RookMasks[sq])];

    /// <summary>Bishop attacks via PEXT (caller must check <see cref="Ready"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BishopAttacks(int sq, ulong occ)
        => _bishopTable[BishopOffsets[sq] + (int)Bmi2.X64.ParallelBitExtract(occ, BishopMasks[sq])];

    /// <summary>
    /// Runtime micro-benchmark (the design reference's `PextSupported`): runs magic and PEXT over a fixed
    /// set of (square, occupancy) pairs for several rounds, discards the first round (JIT
    /// warm-up), and returns true when PEXT is faster. Deterministic input set, wall-clock
    /// comparison — the result only selects the dispatch.
    /// </summary>
    public static bool SelectBest()
    {
        if (!Ready) return false;

        // Deterministic pseudo-random occupancy set: 64 boards × all squares.
        const int boards = 64;
        ulong seed = 0x9E3779B97F4A7C15UL;
        var occupancies = new ulong[boards];
        for (int b = 0; b < boards; b++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            occupancies[b] = seed;
        }

        long magicTicks = 0, pextTicks = 0;
        var sw = new Stopwatch();
        for (int round = 0; round < 5; round++)
        {
            sw.Restart();
            ulong sink = 0;
            for (int b = 0; b < boards; b++)
                for (int sq = 0; sq < 64; sq++)
                    sink ^= MagicBitboards.RookAttacks(sq, occupancies[b])
                          ^ MagicBitboards.BishopAttacks(sq, occupancies[b]);
            sw.Stop();
            if (round > 0) magicTicks += sw.ElapsedTicks;
            GC.KeepAlive(sink);

            sw.Restart();
            sink = 0;
            for (int b = 0; b < boards; b++)
                for (int sq = 0; sq < 64; sq++)
                    sink ^= RookAttacks(sq, occupancies[b])
                          ^ BishopAttacks(sq, occupancies[b]);
            sw.Stop();
            if (round > 0) pextTicks += sw.ElapsedTicks;
            GC.KeepAlive(sink);
        }

        return pextTicks < magicTicks;
    }

    /// <summary>Edge-masked rook blocker mask (same shape as the magic masks).</summary>
    private static ulong GenerateRookMask(int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong mask = 0UL;
        for (int rr = r + 1; rr <= 6; rr++) mask |= 1UL << (rr * 8 + f);
        for (int rr = r - 1; rr >= 1; rr--) mask |= 1UL << (rr * 8 + f);
        for (int ff = f + 1; ff <= 6; ff++) mask |= 1UL << (r * 8 + ff);
        for (int ff = f - 1; ff >= 1; ff--) mask |= 1UL << (r * 8 + ff);
        return mask;
    }

    /// <summary>Edge-masked bishop blocker mask (same shape as the magic masks).</summary>
    private static ulong GenerateBishopMask(int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong mask = 0UL;
        for (int rr = r + 1, ff = f + 1; rr <= 6 && ff <= 6; rr++, ff++) mask |= 1UL << (rr * 8 + ff);
        for (int rr = r + 1, ff = f - 1; rr <= 6 && ff >= 1; rr++, ff--) mask |= 1UL << (rr * 8 + ff);
        for (int rr = r - 1, ff = f + 1; rr >= 1 && ff <= 6; rr--, ff++) mask |= 1UL << (rr * 8 + ff);
        for (int rr = r - 1, ff = f - 1; rr >= 1 && ff >= 1; rr--, ff--) mask |= 1UL << (rr * 8 + ff);
        return mask;
    }

    /// <summary>
    /// Expands a table index into the occupancy subset: bit i of <paramref name="index"/>
    /// corresponds to the i-th set bit of <paramref name="mask"/> (LSB-first), matching
    /// `pext`'s extraction order.
    /// </summary>
    private static ulong IndexToOccupancy(int index, ulong mask)
    {
        ulong occ = 0UL;
        int bits = BitOperations.PopCount(mask);
        for (int i = 0; i < bits; i++)
        {
            int bitPos = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if ((index & (1 << i)) != 0)
                occ |= 1UL << bitPos;
        }
        return occ;
    }
}
