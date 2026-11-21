using PawnForge.Core.Board;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Transposition table correctness - the historical bug hotspot.
/// Covers store/probe roundtrips, replacement policy, mate-score ply
/// adjustment, hashfull accounting, resize/clear, XOR payload integrity
/// under bucket collisions, and multi-threaded stress.
/// </summary>
public class TranspositionTableTests : IDisposable
{
    private readonly TTable _tt = new(4);

    public void Dispose()
    {
        _tt.DisposeIfPossible();
    }

    [Fact]
    public void StoreProbe_Roundtrip()
    {
        var move = new Move(12, 28);
        _tt.Store(0x1234, value: 137, depth: 5, NodeType.Exact, move, currentAge: 1);

        bool found = _tt.Probe(0x1234, out var entry);
        Assert.True(found);
        Assert.Equal(137, entry.Value);
        Assert.Equal(5, entry.Depth);
        Assert.Equal(NodeType.Exact, entry.Flag);
        Assert.Equal(move, entry.BestMove);
    }

    [Fact]
    public void Probe_UnknownKey_ReturnsFalse()
    {
        _tt.Store(0x1234, 100, 3, NodeType.LowerBound, Move.None, 1);
        Assert.False(_tt.Probe(0x9999, out _));
    }

    [Fact]
    public void Probe_DifferentKey_SameBucket_DoesNotDecodeGarbage()
    {
        // Find two distinct keys that map into the SAME bucket (collision),
        // then verify the XOR payload check rejects the wrong key instead of
        // returning corrupted data.
        ulong keyA = 0x0000000000000001;
        ulong keyB = FindKeyInSameBucket(keyA);

        _tt.Store(keyA, 111, 4, NodeType.Exact, Move.None, 1);

        Assert.True(_tt.Probe(keyA, out var entryA));
        Assert.Equal(111, entryA.Value);

        // keyB hashes to the same bucket but must NOT match keyA's entry.
        Assert.False(_tt.Probe(keyB, out _));

        // Overwrite via collision - keyA's data must now be gone or replaced,
        // but never decoded as garbage.
        _tt.Store(keyB, 222, 4, NodeType.Exact, Move.None, 1);
        bool probeA = _tt.Probe(keyA, out var entryAfter);
        Assert.True(!probeA || entryAfter.Key == keyA);
    }

    [Fact]
    public void Replacement_DeeperEntry_Preferred()
    {
        _tt.Store(0xABCD, value: 10, depth: 2, NodeType.Exact, Move.None, currentAge: 1);
        _tt.Store(0xABCD, value: 50, depth: 8, NodeType.Exact, Move.None, currentAge: 1);

        _tt.Probe(0xABCD, out var entry);
        Assert.Equal(50, entry.Value);
        Assert.Equal(8, entry.Depth);
    }

    [Fact]
    public void Replacement_ShallowerEntry_DoesNotOverwrite()
    {
        _tt.Store(0xABCD, value: 50, depth: 8, NodeType.Exact, Move.None, currentAge: 1);
        _tt.Store(0xABCD, value: 10, depth: 2, NodeType.Exact, Move.None, currentAge: 1);

        _tt.Probe(0xABCD, out var entry);
        Assert.Equal(50, entry.Value);
        Assert.Equal(8, entry.Depth);
    }

    [Fact]
    public void MateScore_PlyAdjusted_OnStoreAndProbe()
    {
        // Mate in 5 plies from the node: raw score = MATE(31000) - 5 = 30995.
        // Stored at ply 2 -> stored as 30995 + 2 = 30997 (distance from root).
        // Probed at ply 4 -> returned as 30997 - 4 = 30993 (distance from node).
        const ulong mateKey = 0x000000000000CAFE;
        _tt.Store(mateKey, value: 30995, depth: 6, NodeType.Exact, Move.None, currentAge: 1, ply: 2);

        bool found = _tt.Probe(mateKey, out var entry, ply: 4);
        Assert.True(found);
        Assert.Equal(30993, entry.Value);
    }

    [Fact]
    public void Hashfull_Empty_IsZero()
    {
        Assert.Equal(0, _tt.GetHashfullPercent());
    }

    [Fact]
    public void Hashfull_GrowsWithEntries()
    {
        for (ulong k = 1; k <= 1000; k++)
        {
            _tt.Store(k * 7919, 1, 1, NodeType.Exact, Move.None, 1);
        }
        int hashfull = _tt.GetHashfullPercent();
        Assert.InRange(hashfull, 1, 100);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _tt.Store(0xABCD, 100, 5, NodeType.Exact, Move.None, 1);
        _tt.Clear();
        Assert.False(_tt.Probe(0xABCD, out _));
        Assert.Equal(0, _tt.GetHashfullPercent());
    }

    [Fact]
    public void Resize_DoesNotCrash_AndTableWorksAfterwards()
    {
        _tt.Store(0xABCD, 100, 5, NodeType.Exact, Move.None, 1);
        _tt.Resize(8);

        // Old entries may be gone (resize reallocates), but the table must
        // accept new stores and probe them back without corruption.
        _tt.Store(0x7777, 250, 3, NodeType.LowerBound, Move.None, 1);
        Assert.True(_tt.Probe(0x7777, out var entry));
        Assert.Equal(250, entry.Value);
    }

    [Fact]
    public void ConcurrentStore_NoLostEntries()
    {
        // 8 threads store 2000 unique keys each into a table with room to
        // spare (4 MB = 262144 entries vs 16000 stored) - afterwards every
        // key must probe back with its exact value.
        // NOTE: TT values are int16 - keep them within ±32000.
        var tt = new TTable(4);
        const int threads = 8, perThread = 2000;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                ulong key = (ulong)(t * 1_000_003 + i * 7919) | 1UL; // unique, nonzero
                tt.Store(key, value: t * 1000 + i, depth: 3, NodeType.Exact, Move.None, currentAge: 1);
            }
        });

        for (int t = 0; t < threads; t++)
        {
            for (int i = 0; i < perThread; i += 97) // spot-check every 97th
            {
                ulong key = (ulong)(t * 1_000_003 + i * 7919) | 1UL;
                Assert.True(tt.Probe(key, out var entry), $"Lost key {key} (thread {t}, i {i})");
                Assert.Equal(t * 1000 + i, entry.Value);
            }
        }
    }

    [Fact]
    public void ConcurrentStoreAndProbe_NoExceptions()
    {
        // Mixed read/write hammering: must never throw or decode garbage.
        var tt = new TTable(2);
        var errors = new List<Exception>();

        Parallel.For(0, 8, t =>
        {
            try
            {
                var rng = new Random(t);
                Span<byte> keyBytes = stackalloc byte[8];
                for (int i = 0; i < 5000; i++)
                {
                    rng.NextBytes(keyBytes);
                    ulong key = BitConverter.ToUInt64(keyBytes) | 1UL;

                    tt.Store(key, value: i, depth: 2, NodeType.Exact, Move.None, currentAge: 1);
                    tt.Probe(key, out _);
                    tt.GetHashfullPercent();
                }
            }
            catch (Exception ex)
            {
                lock (errors) { errors.Add(ex); }
            }
        });

        Assert.Empty(errors);
    }

    // ─── Phase 5a /: cached static-eval field ────────────────────────────────────────

    [Fact]
    public void StaticEval_Roundtrip_Positive()
    {
        _tt.Store(0x51A7, value: 20, depth: 4, NodeType.Exact, Move.None, currentAge: 1,
                  ply: 0, staticEval: 137);
        Assert.True(_tt.Probe(0x51A7, out var entry));
        Assert.Equal(137, entry.StaticEval);
    }

    [Fact]
    public void StaticEval_Roundtrip_Negative()
    {
        _tt.Store(0x51A8, value: -20, depth: 4, NodeType.Exact, Move.None, currentAge: 1,
                  ply: 0, staticEval: -4321);
        Assert.True(_tt.Probe(0x51A8, out var entry));
        Assert.Equal(-4321, entry.StaticEval);
    }

    [Fact]
    public void StaticEval_DefaultsToSentinel_WhenNotSupplied()
    {
        // Callers that have no eval to cache (older signature / in-check nodes) leave the field
        // at the NoStaticEval sentinel so the consumer knows to recompute.
        _tt.Store(0x51A9, value: 5, depth: 2, NodeType.Exact, Move.None, currentAge: 1);
        Assert.True(_tt.Probe(0x51A9, out var entry));
        Assert.Equal(TTable.NoStaticEval, entry.StaticEval);
    }

    [Fact]
    public void StaticEval_OutOfShortRange_StoredAsSentinel()
    {
        // Anything that would not round-trip through the 16-bit field is cached as "no eval" so a
        // reused value can never differ from a freshly computed one.
        _tt.Store(0x51AA, value: 5, depth: 2, NodeType.Exact, Move.None, currentAge: 1,
                  ply: 0, staticEval: 40000);
        Assert.True(_tt.Probe(0x51AA, out var entry));
        Assert.Equal(TTable.NoStaticEval, entry.StaticEval);
    }

    [Fact]
    public void StaticEval_DoesNotCorruptOtherFields()
    {
        // The static eval shares the 64-bit payload word with move/score/depth/flag/age; verify
        // packing a full-range eval leaves every other field intact.
        var move = new Move(10, 26, 5); // from, to, promotion nibble
        _tt.Store(0x51AB, value: -12345, depth: 200, NodeType.UpperBound, move, currentAge: 1,
                  ply: 0, staticEval: 32000);
        Assert.True(_tt.Probe(0x51AB, out var entry));
        Assert.Equal(-12345, entry.Value);
        Assert.Equal(200, entry.Depth);
        Assert.Equal(NodeType.UpperBound, entry.Flag);
        Assert.Equal(move, entry.BestMove);
        Assert.Equal(32000, entry.StaticEval);
    }

    [Fact]
    public void Replacement_OlderGeneration_IsOverwritten()
    {
        // Age was narrowed to 6 bits for the static-eval field; a shallower entry from an OLDER
        // generation must still be replaceable (the `currentAge != storedGen` refresh path).
        _tt.Store(0xBEEF, value: 10, depth: 9, NodeType.Exact, Move.None, currentAge: 1);
        _tt.Store(0xBEEF, value: 77, depth: 3, NodeType.Exact, Move.None, currentAge: 2);
        Assert.True(_tt.Probe(0xBEEF, out var entry));
        Assert.Equal(77, entry.Value);
        Assert.Equal(3, entry.Depth);
    }

    // ─── Phase 5b: lockless XOR-verify (key recovered as Data ^ Lock) ────────────────────────

    [Fact]
    public void LocklessVerify_NoFalseHits_AcrossManySameBucketKeys()
    {
        // Store ONE entry, then probe 500 other keys that all map to the SAME bucket. The lockless
        // scheme recovers the key as (Data ^ Lock) and must reject every one of them: a false hit
        // would mean the XOR verification is not actually validating the key.
        ulong keyA = 0x0000000000000101UL;
        _tt.Store(keyA, value: 4242, depth: 7, NodeType.Exact, new Move(1, 18), currentAge: 1,
                  ply: 0, staticEval: 55);

        for (int i = 1; i <= 500; i++)
        {
            ulong other = keyA + ((ulong)i << 20); // same low bucket bits, different key
            Assert.False(_tt.Probe(other, out _), $"false hit for key {other:X}");
        }

        // keyA itself still verifies and decodes correctly.
        Assert.True(_tt.Probe(keyA, out var e));
        Assert.Equal(4242, e.Value);
        Assert.Equal(55, e.StaticEval);
        Assert.Equal(keyA, e.Key);
    }

    // ─── Phase 5c: prefetch operation ────────────────────────────────────────

    [Fact]
    public void Prefetch_DoesNotThrow_AndTableOperatesNormally()
    {
        // Tests that Prefetch on valid keys, zero keys, and boundary keys executes
        // cleanly without memory fault, and subsequent Store/Probe operations function.
        ulong key = 0xDEADBEEFCAFE1234UL;
        _tt.Prefetch(key);
        _tt.Prefetch(0UL);
        _tt.Prefetch(ulong.MaxValue);

        _tt.Store(key, value: 777, depth: 4, NodeType.Exact, Move.None, currentAge: 1);
        _tt.Prefetch(key);

        Assert.True(_tt.Probe(key, out var entry));
        Assert.Equal(777, entry.Value);
    }

    /// <summary>Returns a different key that maps to the same bucket (same low bucket-mask bits).</summary>
    private static ulong FindKeyInSameBucket(ulong key)
    {
        // 4 MB table = 65536 buckets -> mask 0xFFFF. Adding 1<<20 (a multiple
        // of 65536) keeps the bucket identical while changing the key.
        return key + (1UL << 20);
    }
}

file static class TTableTestExtensions
{
    public static void DisposeIfPossible(this TTable tt)
    {
        // TTable frees native memory via finalizer; nothing to do here.
    }
}
