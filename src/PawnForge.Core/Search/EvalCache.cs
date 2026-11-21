// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · per-thread static-eval cache
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace PawnForge.Core.Search;

/// <summary>
/// Per-thread second-level static-evaluation cache (2026-10-24).
///
/// WHY: the transposition table already caches the RAW static eval inside each entry, so a
/// TT hit never pays for the NNUE forward pass. What it cannot serve is everything the TT
/// misses: positions evicted by bucket replacement, depth-0 quiescence entries competing in
/// the same bucket, and any node whose entry carries no cached <c>StaticEval</c> (in-check
/// stores, out-of-int16 values). This cache catches exactly those repeats — a hit skips an
/// entire NNUE forward pass — and is a pure NPS→depth lever: the cached value is
/// bit-identical to a fresh <c>Evaluate</c>, so a fixed-depth node count must NOT move.
///
/// DESIGN (model: the design reference's second-level cache — 8-byte item, upper-half Zobrist
/// tag, side-to-move byte, int16 score, aligned unmanaged storage, SSE prefetch; note that
/// the design reference's own search never actually probes its cache, so the integration is ours):
/// entry (8 bytes) = uint Tag | short Score | byte Stm | pad
/// index = ZobristKey &amp; (entryCount - 1) (power-of-two, TT-style)
/// tag = upper 32 bits of the Zobrist key
/// sentinel = Score == short.MinValue means "empty" (the same NoStaticEval
/// convention the TT uses; a real NNUE static eval never reaches it)
///
/// The tag + index verify ~52–56 bits of the key depending on size — the same collision
/// model as the reference (a false hit needs a stale entry with the same tag at the same
/// index). It is per-thread and never shared, so no atomics or locks are needed.
///
/// LIFETIME: one instance per <see cref="ThreadData"/> (i.e. per search thread, recreated
/// for every `go` in the UCI orchestrator), allocated lazily on the first cached eval so a
/// run with the toggle OFF never pays the memory. Disposed with the thread state.
/// </summary>
public sealed unsafe class EvalCache : IDisposable
{
    /// <summary>Bytes per cache entry (the layout is asserted in the constructor).</summary>
    public const int EntrySize = 8;

    /// <summary>Score sentinel for an unused slot (matches <c>TTable.NoStaticEval</c>).</summary>
    private const short EmptyScore = short.MinValue;

    /// <summary>64-byte alignment so a prefetched line never straddles two cache lines.</summary>
    private const int CacheLine = 64;

    /// <summary>One cache slot: upper-half key tag, int16 score, side to move.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Item
    {
        public uint Tag;
        public short Score;
        public byte Stm;
    }

    private Item* _items;
    private long _mask;
    private readonly int _sizeMb;

    /// <summary>
    /// Allocates a cache of <paramref name="sizeMb"/> MB (clamped 8..1024, the audit's
    /// `clamp(hash/2, 8, 1024)` is applied by the caller), rounded DOWN to a power-of-two
    /// entry count so indexing is a mask. Every slot starts empty (sentinel score).
    /// </summary>
    public EvalCache(int sizeMb)
    {
        if (sizeof(Item) != EntrySize)
            throw new InvalidOperationException(
                $"EvalCache.Item is {sizeof(Item)} bytes; the {EntrySize}-byte layout is required");

        _sizeMb = ClampSizeMb(sizeMb);
        long bytes = (long)_sizeMb * 1024 * 1024;
        long entries = Math.Max(1, bytes / EntrySize);
        long power = 1;
        while (power * 2 <= entries) power *= 2;
        _mask = power - 1;

        _items = (Item*)NativeMemory.AlignedAlloc((nuint)(power * EntrySize), CacheLine);
        Clear();
    }

    /// <summary>
    /// The audit's cache-size rule, isolated so it is testable without allocating:
    /// `clamp(sizeMb, 8, 1024)` ("sized clamp(hash/2, 8, 1024) MB").
    /// </summary>
    public static int ClampSizeMb(int sizeMb) => Math.Clamp(sizeMb, 8, 1024);

    /// <summary>Actual allocated size in MB (after the 8..1024 clamp and power-of-two rounding).</summary>
    public int SizeMb => _sizeMb;

    /// <summary>Zeroes every slot (a slot with the empty sentinel can never match).</summary>
    public void Clear() => NativeMemory.Clear(_items, (nuint)((_mask + 1) * EntrySize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Index(ulong key) => (long)(key & (ulong)_mask);

    /// <summary>
    /// Probes the cache for a position. A hit returns the cached static eval — bit-identical
    /// to a fresh <c>Evaluate</c> by construction (the caller only stores in-range values).
    /// </summary>
    /// <param name="zobristKey">The position's Zobrist key (includes side to move).</param>
    /// <param name="stm">Side to move (0 = white, 1 = black), stored as an extra guard.</param>
    /// <param name="score">The cached static eval on a hit; 0 on a miss.</param>
    /// <returns>True on a verified hit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryProbe(ulong zobristKey, int stm, out int score)
    {
        ref Item item = ref _items[Index(zobristKey)];
        if (item.Score != EmptyScore
            && item.Tag == (uint)(zobristKey >> 32)
            && item.Stm == (byte)stm)
        {
            score = item.Score;
            return true;
        }
        score = 0;
        return false;
    }

    /// <summary>
    /// Stores a static eval. Values outside the int16 range are NOT cached (the probe can
    /// then never return a value a fresh eval would not produce), and the empty sentinel is
    /// never used as a real score.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong zobristKey, int stm, int score)
    {
        if (score <= EmptyScore || score > short.MaxValue) return;
        ref Item item = ref _items[Index(zobristKey)];
        item.Tag = (uint)(zobristKey >> 32);
        item.Score = (short)score;
        item.Stm = (byte)stm;
    }

    /// <summary>
    /// Prefetches the cache line for a child position into L1 (called right after a make,
    /// next to the TT prefetch). JIT-eliminated on non-x86 targets, like <c>TTable.Prefetch</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ulong zobristKey)
    {
        if (Sse.IsSupported)
            Sse.Prefetch0(_items + Index(zobristKey));
    }

    /// <summary>Frees the unmanaged table (idempotent).</summary>
    public void Dispose()
    {
        if (_items != null)
        {
            NativeMemory.AlignedFree(_items);
            _items = null;
        }
        GC.SuppressFinalize(this);
    }

    ~EvalCache() => Dispose();
}
