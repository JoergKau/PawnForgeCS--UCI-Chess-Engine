using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace PawnForge.Core.TranspositionTable
{
    /// <summary>Result classification of a TT entry: exact value, a fail-high lower bound, or a fail-low upper bound.</summary>
    public enum NodeType { Exact, LowerBound, UpperBound }

    /// <summary>Decoded transposition-table entry (the raw storage is packed into 16 bytes; see TTable).</summary>
    public struct TTEntry
    {
        /// <summary>The full Zobrist key this entry was stored under (XOR-verified against the probe key).</summary>
        public ulong Key;
        /// <summary>Remaining search depth when the value was stored (deeper = more reliable).</summary>
        public int Depth;
        /// <summary>The search score in centipawns, mate distances already adjusted for the probe ply.</summary>
        public int Value;
        /// <summary>Node classification: Exact, LowerBound (fail-high) or UpperBound (fail-low).</summary>
        public NodeType Flag;
        /// <summary>The best move found in this position (packed From/To/Promoted; may be None).</summary>
        public Move BestMove;
        /// <summary>The TT generation (search counter) when the entry was stored.</summary>
        public int Age;
        /// <summary>
        /// The RAW (uncorrected) static evaluation of this position, cached so a later visit
        /// can skip the expensive evaluation (Phase 5a — the reference's design: the static
        /// eval rides inside the existing TT entry rather than in a separate cache). Equals
        /// <see cref="TTable.NoStaticEval"/> when the entry carries no cached eval (e.g. it was
        /// stored by a path that never computed one, or the eval fell outside the 16-bit range).
        /// </summary>
        public int StaticEval;
        /// <summary>
        /// True when the entry was stored by (or on behalf of) a PV node — a full-window node, or
        /// any node that probed a TT entry whose own PV flag was set (the reference's `ttPv` propagation).
        /// The search uses this as the "TT-proven PV" signal for the LMR v2
        /// reduce-less term — it identifies lines that the search has repeatedly
        /// confirmed as principal variation, which deserve full-depth treatment.
        /// </summary>
        public bool IsPv;
    }

    /// <summary>
    /// The transposition table: a LOCKLESS, cache-line-aligned hash of previously
    /// searched positions, used for cutoffs, move ordering and PV extraction.
    ///
    /// LAYOUT (per 16-byte raw entry) — the lockless XOR scheme (Phase 5b):
    /// Data (8 bytes) the bit-packed payload:
    /// bits 0-15 move (u16)
    /// bits 16-31 score (i16)
    /// bits 32-47 staticEval (i16), populated since Phase 5a
    /// bits 48-55 depth (u8)
    /// bits 56-57 flag (2 bits)
    /// bits 58-62 age (5 bits, generation mod 32)
    /// bit 63 pv (1 bit) — the reference's ttPv
    /// Lock (8 bytes) = Data XOR key (the verification word)
    /// 4 entries per bucket = exactly one 64-byte cache line.
    ///
    /// LOCKLESS VERIFICATION (Phase 5b): the key is NOT stored raw; it is recovered
    /// as `Data XOR Lock`. A probe accepts an entry only when `(Data XOR Lock) == probeKey`.
    /// If a concurrent writer tears the two 8-byte words (a reader sees a fresh Data
    /// with a stale Lock, or vice-versa), the XOR reconstruction yields a value that
    /// (with overwhelming probability) does not equal the probe key, so the torn entry
    /// manifests as a MISS — never as a garbage "hit". This is the standard lockless trick
    /// both generations rely on: the design reference (explicit XOR-verify) and
    /// the reference port (one shared table, zero synchronization). No
    /// locks, no atomics, no memory barriers on the hot path — 8-byte aligned word reads
    /// are already atomic on x64/arm64, and the XOR check is ordering-independent.
    /// NOTE: the previous scheme stored the RAW key in word 1 and only compared THAT to
    /// the probe key, so a torn word-2 read could have returned garbage — it was only
    /// safe because it was lock-guarded. Going lockless REQUIRED moving to this scheme.
    ///
    /// INDEXING: bucket = key &amp; (numBuckets - 1), numBuckets a power of two.
    ///
    /// REPLACEMENT: same key always refreshes; otherwise the victim is the
    /// entry with the lowest effective depth (stored depth - 2 per generation
    /// of age) - depth-preferred with age blending. Replacement races only ever
    /// cost entry quality (a lost/overwritten entry), never correctness.
    ///
    /// MEMORY: one unmanaged allocation (AllocHGlobal) with manual 64-byte
    /// alignment. The ORIGINAL handle is kept separately and is the only
    /// pointer ever freed - the aligned table start is an interior pointer and
    /// must never be passed to FreeHGlobal.
    /// </summary>
    public unsafe class TTable
    {
        private const int ENTRIES_PER_BUCKET = 4;
        private const int ENTRY_SIZE_BYTES = 16; // 8 (data) + 8 (lock = data ^ key)
        private const int BUCKET_SIZE = ENTRY_SIZE_BYTES * ENTRIES_PER_BUCKET; // 64 bytes

        // Raw 16-byte entry: the packed payload plus its XOR-verification word.
        // Empty slot == both words zero (a real payload always has nonzero flag bits,
        // so Data == 0 uniquely identifies an unused slot).
        private unsafe struct RawEntry
        {
            public ulong Data; // packed payload (move/score/staticEval/depth/flag/age)
            public ulong Lock; // = Data ^ key (probe recovers key = Data ^ Lock)
        }

        private unsafe RawEntry* _tablePtr; // pointer to raw memory for atomic operations
        private IntPtr _tableHandle; // ALIGNED address (table start) - must never be freed
        private IntPtr _originalHandle; // ORIGINAL AllocHGlobal handle - the ONLY pointer we may free
        public static TTable Shared = new TTable(256); // CCRL-ready default (engines of this class ship Hash 128/256 MB)
        private long _bucketMask; // power-of-two buckets - 1 (Fix #5: use long)
        private int _generation; // byte-range generation
        private const int CACHE_LINE_SIZE = 64; // Fix #1: cache-line alignment

        private const int MATE_SCORE = 31000;
        private const int MATE_PLY_THRESHOLD = 1000;  // Reverting back - 100 made it worse!

        /// <summary>
        /// Sentinel meaning "this entry has no cached static eval" (Phase 5a /). Stored in the
        /// 16-bit static-eval field. Chosen as short.MinValue (-32768) because a genuine static
        /// eval never reaches that magnitude (it is a ~±40-pawn quantity, far inside the 16-bit range),
        /// so the sentinel can never collide with a real cached value. A probe returning this value
        /// tells the caller to fall back to a fresh <c>Evaluate</c>.
        /// </summary>
        public const int NoStaticEval = -32768;

        // Bit layout of the 64-bit XOR-scrambled payload word (see the class-level LAYOUT comment).
        private const int STATIC_EVAL_SHIFT = 32; // bits 32-47 (i16)
        private const int DEPTH_SHIFT       = 48; // bits 48-55 (u8)
        private const int FLAG_SHIFT        = 56; // bits 56-57 (2 bits)
        private const int AGE_SHIFT         = 58; // bits 58-62 (5 bits)
        private const int AGE_MASK          = 0x1F; // 5-bit generation (wraps at 32)
        private const ulong PV_BIT          = 1UL << 63; // ttPv-style propagation flag (the reference's design)

        /// <summary>
        /// Allocates a table of the given size in MB (rounded down to a power
        /// of two buckets). The table is lockless (Phase 5b) — no lock array.
        /// </summary>
        public TTable(int sizeMb = 16)
        {
            // Bug 150: validate before allocating — the same unchecked
            // allocation-size cast as in Resize lived here, so an out-of-range
            // constructor argument corrupted the heap the same way.
            if (sizeMb < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizeMb), sizeMb, "The table size must be at least 1 MB.");
            }

            // calculate buckets: sizeMB -> numBuckets = sizeBytes / BUCKET_SIZE
            var bytes = (long)sizeMb * 1024 * 1024;
            var buckets = Math.Max(1L, bytes / BUCKET_SIZE);
            // round down to power of two
            long power = 1; while (power * 2 <= buckets) power *= 2;
            var numBuckets = power;
            _bucketMask = numBuckets - 1;

            // Allocate unmanaged memory for the table with cache-line alignment (Fix #1)
            var totalSize = (long)numBuckets * ENTRIES_PER_BUCKET * sizeof(RawEntry);
            var alignedSize = totalSize + CACHE_LINE_SIZE;
            if (alignedSize > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizeMb), sizeMb,
                    "The requested table size exceeds the largest allocation addressable through IntPtr.");
            }
            _originalHandle = Marshal.AllocHGlobal((int)alignedSize);
            long unalignedAddr = _originalHandle.ToInt64();
            long alignedAddr = ((unalignedAddr + CACHE_LINE_SIZE - 1) / CACHE_LINE_SIZE) * CACHE_LINE_SIZE;
            _tableHandle = new IntPtr(alignedAddr);
            _tablePtr = (RawEntry*)_tableHandle.ToPointer();

            // Zero out the memory
            for (long i = 0; i < numBuckets * ENTRIES_PER_BUCKET; i++)
            {
                _tablePtr[i].Data = 0;
                _tablePtr[i].Lock = 0;
            }

            _generation = 1;
        }

        ~TTable()
        {
            FreeTableMemory();
        }

        /// <summary>
        /// Frees the table memory. IMPORTANT: we must free the ORIGINAL
        /// AllocHGlobal handle, not the aligned (interior) address that
        /// _tableHandle points to - freeing an interior pointer is
        /// undefined behavior.
        /// </summary>
        private void FreeTableMemory()
        {
            if (_originalHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_originalHandle);
                _originalHandle = IntPtr.Zero;
            }
            _tableHandle = IntPtr.Zero;
            _tablePtr = null;
        }

        /// <summary>
        /// Frees and reallocates the table at the new size. ALL entries are
        /// lost (the keys would re-index anyway at the new bucket count).
        /// </summary>
        public void Resize(int sizeMb)
        {
            // Bug 150: validate FIRST, before any state change. The UCI Hash
            // option used to call this with the raw parsed value, and a size far
            // above the declared maximum overflowed the `(int)alignedSize` cast
            // below — a small buffer was allocated and the zeroing loop then
            // wrote far past its end (heap corruption, delayed process death).
            if (sizeMb < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizeMb), sizeMb, "The table size must be at least 1 MB.");
            }

            // calculate buckets: sizeMB -> numBuckets = sizeBytes / BUCKET_SIZE
            var bytes = (long)sizeMb * 1024 * 1024;
            var buckets = Math.Max(1L, bytes / BUCKET_SIZE);
            // round down to power of two
            long power = 1; while (power * 2 <= buckets) power *= 2;
            var numBuckets = power;

            var totalSize = (long)numBuckets * ENTRIES_PER_BUCKET * sizeof(RawEntry);
            var alignedSize = totalSize + CACHE_LINE_SIZE;
            if (alignedSize > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizeMb), sizeMb,
                    "The requested table size exceeds the largest allocation addressable through IntPtr.");
            }

            // Free the old memory only after the new size is fully validated, so
            // a rejected size leaves the current table alive and usable
            // (original handle - see FreeTableMemory).
            FreeTableMemory();

            _bucketMask = numBuckets - 1;

            // Allocate new unmanaged memory for the table with cache-line alignment (Fix #1)
            _originalHandle = Marshal.AllocHGlobal((int)alignedSize);
            long unalignedAddr = _originalHandle.ToInt64();
            long alignedAddr = ((unalignedAddr + CACHE_LINE_SIZE - 1) / CACHE_LINE_SIZE) * CACHE_LINE_SIZE;
            _tableHandle = new IntPtr(alignedAddr);
            _tablePtr = (RawEntry*)_tableHandle.ToPointer();

            // Zero out the memory
            for (long i = 0; i < numBuckets * ENTRIES_PER_BUCKET; i++)
            {
                _tablePtr[i].Data = 0;
                _tablePtr[i].Lock = 0;
            }
        }

        /// <summary>Zeroes every entry and resets the generation counter (UCI `Clear Hash`).</summary>
        public void Clear()
        {
            // Zero out the entire table
            var numEntries = (_bucketMask + 1) * ENTRIES_PER_BUCKET;
            for (int i = 0; i < numEntries; i++)
            {
                _tablePtr[i].Data = 0;
                _tablePtr[i].Lock = 0;
            }
            _generation = 1;
        }

        /// <summary>
        /// Advances the generation counter at the start of a new search:
        /// older entries become replacement victims first (age blending).
        /// Main thread only.
        /// </summary>
        public void NewSearch()
        {
            unchecked { _generation++; if (_generation == 0) _generation = 1; }
        }

        // Fix #5: Use long for bucketIndex to handle large TT
        private long BucketIndex(ulong key) => (long)(key & (ulong)_bucketMask);

        // Packs From (6 bits), To (6 bits), Promoted (4 bits).
        // BUG 147 NOTE: the 16-bit form cannot carry Move.IsEnPassant / Move.IsCastle.
        // The flags are rebuilt from board context at probe time (ReStampMoveFlags)
        // whenever the caller passes the position to Probe; a probe without a position
        // returns the flagless form. A stored special move therefore compares equal to
        // its generated move again instead of matching nothing.
        private static ushort PackMove(Move m)
        {
            // pack From (6 bits), To (6 bits), Promoted (4 bits)
            int from = m.From & 0x3F;
            int to = m.To & 0x3F;
            int prom = m.Promoted & 0xF;
            return (ushort)(from | (to << 6) | (prom << 12));
        }

        private static Move UnpackMove(ushort p)
        {
            int from = p & 0x3F;
            int to = (p >> 6) & 0x3F;
            int prom = (p >> 12) & 0xF;
            return new Move(from, to, prom);
        }

        /// <summary>
        /// Rebuilds the <see cref="Move.IsEnPassant"/> / <see cref="Move.IsCastle"/> flags that the
        /// 16-bit TT packing cannot store (Bug 147). The packed form keeps from/to/promotion only,
        /// so an unpacked castling or en-passant move used to compare unequal to every generated
        /// move (Move equality includes the flags) and was silently dropped as a TT hint: never
        /// tried first, never excluded in the singular-verification search, never extended.
        /// Both reconstructions are unambiguous on the probed position:
        /// - a king move of exactly two files on one rank can only be castling (an ordinary king
        ///   step covers one file, and the king has no other two-file move);
        /// - a pawn move onto the current en-passant target square can only be the en-passant
        ///   capture: while the EP square is set, no pawn push can land on it (the square in front
        ///   of the pushing pawn is occupied by the pawn that just double-pushed); the diagonal
        ///   geometry is additionally checked for belt and braces.
        /// A move that matches neither shape is returned unchanged, so ordinary moves, promotions
        /// and entries probed without a position behave exactly as before the fix.
        /// </summary>
        /// <param name="pos">The position the entry was probed under (the probe key matched, so
        /// the entry was stored for exactly this position - apart from Zobrist collisions, which
        /// the existing exact-key verification treats the same way as before the fix).</param>
        /// <param name="m">The flagless move recovered from the 16-bit payload.</param>
        /// <returns>The move with the flags re-stamped where the board context determines them.</returns>
        internal static Move ReStampMoveFlags(Position pos, Move m)
        {
            if (m.From == m.To) return m; // Move.None or malformed - nothing to re-stamp

            Piece moving = pos.GetPieceAt(m.From);
            if (moving == Piece.Empty) return m;

            if (moving == Piece.WK || moving == Piece.BK)
            {
                // Castling is the only king move that crosses exactly two files on one rank.
                if (m.To / 8 == m.From / 8 && Math.Abs((m.To % 8) - (m.From % 8)) == 2)
                    return new Move(m.From, m.To, m.Promoted, IsEnPassant: false, IsCastle: true);
                return m;
            }

            if (moving == Piece.WP || moving == Piece.BP)
            {
                // A pawn landing on the EP target square with diagonal geometry is the
                // en-passant capture; no pawn push can reach the EP square while it is set.
                if (pos.EnPassantSquare >= 0 && m.To == pos.EnPassantSquare
                    && Math.Abs((m.To % 8) - (m.From % 8)) == 1)
                    return new Move(m.From, m.To, m.Promoted, IsEnPassant: true, IsCastle: false);
                return m;
            }

            return m;
        }

        private static int AdjustMateForStorage(int score, int ply)
        {
            if (Math.Abs(score) >= MATE_SCORE - MATE_PLY_THRESHOLD)
            {
                return score > 0 ? score + ply : score - ply;
            }
            return score;
        }

        private static int AdjustMateForRetrieval(int score, int ply)
        {
            if (Math.Abs(score) >= MATE_SCORE - MATE_PLY_THRESHOLD)
            {
                return score > 0 ? score - ply : score + ply;
            }
            return score;
        }

        /// <summary>Writes a (payload, key) pair into a slot as the lockless Data/Lock word pair.</summary>
        private void WriteSlot(long idx, ulong payload, ulong key)
        {
            // Write Data first, then the XOR-verification word. Order is not required for
            // correctness (a torn read is caught by the XOR check either way), but writing the
            // verification word last is the conventional "commit".
            _tablePtr[idx].Data = payload;
            _tablePtr[idx].Lock = payload ^ key;
        }

        /// <summary>
        /// Stores an entry under the given Zobrist key. Mate scores are
        /// adjusted to root-relative form before storage (ply parameter).
        /// Replacement policy: same key always refreshes (if deeper or from an
        /// older generation); otherwise the lowest effective-depth entry in the
        /// bucket is replaced. Lockless (Phase 5b): concurrent writers can only
        /// cost entry quality, never correctness (readers verify via XOR).
        /// </summary>
        /// <param name="key">Zobrist key of the position.</param>
        /// <param name="value">Score from the searching side's perspective.</param>
        /// <param name="depth">Remaining depth of the search that produced the value.</param>
        /// <param name="flag">Exact / LowerBound / UpperBound classification.</param>
        /// <param name="best">Best move found (packed to 16 bits; may be None).</param>
        /// <param name="currentAge">The current TT generation (for age blending).</param>
        /// <param name="ply">Distance from the root - mate scores are converted to root-relative before storage.</param>
        /// <param name="staticEval">
        /// RAW (uncorrected) static eval to cache in the entry for later reuse (Phase 5a /).
        /// Defaults to <see cref="NoStaticEval"/> for callers that have no eval to record; values
        /// outside the 16-bit range are also stored as the sentinel so a reused value is always
        /// bit-identical to a freshly computed one.
        /// </param>
        /// <param name="pv">
        /// (the reference's `ttPv`): marks the entry as belonging to a PV-proven line. The flag
        /// lives in payload bit 63 (the age field shrank from 6 to 5 bits — generation still wraps
        /// harmlessly, now at 32 searches instead of 64). Read back on probe as
        /// <see cref="TTEntry.IsPv"/> and consumed by the LMR v2 ttPv term.
        /// </param>
        public void Store(ulong key, int value, int depth, NodeType flag, Move best, int currentAge, int ply = 0, int staticEval = NoStaticEval, bool pv = false)
        {
            long bucket = BucketIndex(key);
            long bucketBase = bucket * ENTRIES_PER_BUCKET; // Index into RawEntry array

            // Build payload
            ushort mv = PackMove(best);
            int flagByte = flag switch { NodeType.Exact => 1, NodeType.LowerBound => 2, NodeType.UpperBound => 3, _ => 0 };
            int adjustedScore = AdjustMateForStorage(value, ply); // adjust mate scores with ply
            // Only cache a static eval that round-trips losslessly through the 16-bit field;
            // anything out of range (or the sentinel itself) is stored as "no eval" so that a
            // future reuse can never differ from a fresh Evaluate call.
            short se = (staticEval > NoStaticEval && staticEval <= short.MaxValue)
                ? (short)staticEval
                : (short)NoStaticEval;
            ulong payload = (ulong)(ushort)mv;
            payload |= ((ulong)(ushort)adjustedScore) << 16;
            payload |= ((ulong)(ushort)se) << STATIC_EVAL_SHIFT;
            payload |= ((ulong)(byte)depth) << DEPTH_SHIFT;
            payload |= ((ulong)(flagByte & 0x3)) << FLAG_SHIFT;
            payload |= ((ulong)(currentAge & AGE_MASK)) << AGE_SHIFT;
            if (pv) payload |= PV_BIT;

            // Replacement selection (lockless: read each slot's Data/Lock once, recover its key
            // as Data ^ Lock). Reads may occasionally be torn under contention — that only skews
            // the victim choice, never correctness, so no lock is needed.
            long replaceIdx = -1;
            int lowestEffectiveDepth = int.MaxValue;

            for (int i = 0; i < ENTRIES_PER_BUCKET; i++)
            {
                long idx = bucketBase + i;
                ulong storedData = _tablePtr[idx].Data;
                ulong storedLock = _tablePtr[idx].Lock;

                // Empty slot: both words zero (a real payload always has nonzero flag bits).
                if (storedData == 0UL && storedLock == 0UL)
                {
                    WriteSlot(idx, payload, key);
                    return;
                }

                ulong storedKey = storedData ^ storedLock; // recovered Zobrist key
                if (storedKey == key)
                {
                    int storedDepth = (int)((storedData >> DEPTH_SHIFT) & 0xFF);
                    int storedGen = (int)((storedData >> AGE_SHIFT) & AGE_MASK);
                    if (depth >= storedDepth || (currentAge & AGE_MASK) != storedGen)
                    {
                        WriteSlot(idx, payload, key);
                    }
                    return;
                }

                int storedDepth2 = (int)((storedData >> DEPTH_SHIFT) & 0xFF);
                int storedGen2 = (int)((storedData >> AGE_SHIFT) & AGE_MASK);
                int age = (_generation - storedGen2) & AGE_MASK;
                int effectiveDepth = storedDepth2 - (age * 2);
                if (effectiveDepth < lowestEffectiveDepth)
                {
                    lowestEffectiveDepth = effectiveDepth;
                    replaceIdx = idx;
                }
            }

            if (replaceIdx != -1)
            {
                WriteSlot(replaceIdx, payload, key);
            }
        }

        /// <summary>
        /// Prefetches the transposition-table bucket for the given Zobrist key into CPU L1 cache
        /// (Phase 5c, the design reference).
        ///
        /// PLATFORM COMPATIBILITY:
        /// - On x86/x64 (Windows 11 with AVX2): <see cref="Sse.IsSupported"/> is a JIT compile-time
        /// constant true; the JIT eliminates the branch and emits the hardware `prefetcht0`
        /// instruction, pulling the 64-byte bucket into L1 cache ahead of the child node's probe.
        /// - On ARM64 (macOS Apple Silicon): <see cref="Sse.IsSupported"/> is a JIT compile-time
        /// constant false; the JIT eliminates the method body entirely with zero runtime overhead.
        /// </summary>
        /// <param name="key">The Zobrist hash key of the position about to be searched.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Prefetch(ulong key)
        {
            if (Sse.IsSupported)
            {
                long bucket = BucketIndex(key);
                long bucketBase = bucket * ENTRIES_PER_BUCKET;
                Sse.Prefetch0(_tablePtr + bucketBase);
            }
        }

        /// <summary>Probes the table at ply 0 (no mate-distance adjustment).</summary>
        public bool Probe(ulong key, out TTEntry entry)
        {
            return Probe(key, out entry, ply: 0);
        }

        /// <summary>
        /// Probes the table for the given key. Lockless (Phase 5b): an entry is a hit only when
        /// the key recovered from the two words, <c>Data XOR Lock</c>, equals the probe key. A
        /// torn read (a fresh word paired with a stale one) reconstructs a mismatching key and is
        /// rejected as a miss, so a garbage payload can never be returned. Mate scores are
        /// converted from root-relative to node-relative using the ply parameter.
        /// </summary>
        /// <param name="key">Zobrist key of the position to look up.</param>
        /// <param name="entry">The decoded entry on a hit (default on a miss).</param>
        /// <param name="ply">Distance from the root - mate scores are adjusted by it.</param>
        /// <param name="pos">
        /// The position probed under, when the caller holds it. The 16-bit packed move carries no
        /// <c>IsEnPassant</c>/<c>IsCastle</c> flags (Bug 147); when a position is supplied the flags
        /// are rebuilt from board context after unpacking, so a stored castling or en-passant move
        /// compares equal to its generated move again. A probe without a position returns the
        /// documented flagless form.
        /// </param>
        /// <returns>True on a verified hit.</returns>
        public bool Probe(ulong key, out TTEntry entry, int ply, Position? pos = null)
        {
            entry = default;
            long bucket = BucketIndex(key);
            long bucketBase = bucket * ENTRIES_PER_BUCKET; // Index into RawEntry array

            for (int i = 0; i < ENTRIES_PER_BUCKET; i++)
            {
                long idx = bucketBase + i;

                ulong storedData = _tablePtr[idx].Data;
                ulong storedLock = _tablePtr[idx].Lock;

                // XOR-verify: the entry is valid for this key iff (Data ^ Lock) == key.
                if ((storedData ^ storedLock) == key)
                {
                    ulong payload = storedData;

                    ushort mv = (ushort)(payload & 0xFFFF);
                    short score = (short)((payload >> 16) & 0xFFFF);
                    short staticEval = (short)((payload >> STATIC_EVAL_SHIFT) & 0xFFFF);
                    int depth = (int)((payload >> DEPTH_SHIFT) & 0xFF);
                    int flag = (int)((payload >> FLAG_SHIFT) & 0x3);
                    int age = (int)((payload >> AGE_SHIFT) & AGE_MASK);
                    bool isPv = (payload & PV_BIT) != 0;

                    int adjScore = AdjustMateForRetrieval(score, ply);
                    Move move = UnpackMove(mv);
                    // Bug 147 fix: rebuild the IsEnPassant/IsCastle flags the 16-bit packing
                    // cannot store, so the returned move matches its generated move again.
                    if (pos != null) move = ReStampMoveFlags(pos, move);

                    entry = new TTEntry { Key = key, Depth = depth, Value = adjScore, Flag = flag == 1 ? NodeType.Exact : (flag == 2 ? NodeType.LowerBound : NodeType.UpperBound), BestMove = move, Age = age, StaticEval = staticEval, IsPv = isPv };
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Approximates the table usage as a percentage by sampling up to
        /// 65536 buckets (a slot is "used" iff its Data word is nonzero — a real
        /// payload always has nonzero flag bits, so this reliably distinguishes
        /// used from empty slots). Reported as 0-100; the UCI layer multiplies by
        /// 10 for the standard permille `hashfull` unit.
        /// </summary>
        public int GetHashfullPercent()
        {
            long numBuckets = (_bucketMask + 1L);
            if (numBuckets == 0) return 0;

            const long MAX_SAMPLES = 65536;
            long samples = Math.Min(numBuckets, MAX_SAMPLES);
            long used = 0;
            long stride = numBuckets / samples;
            if (stride == 0) stride = 1;

            for (long s = 0; s < samples; s++)
            {
                long b = (s * stride) % numBuckets;
                long bucketBase = b * ENTRIES_PER_BUCKET;
                for (int j = 0; j < ENTRIES_PER_BUCKET; j++)
                {
                    ulong data = Volatile.Read(ref _tablePtr[bucketBase + j].Data);
                    if (data != 0UL) used++;
                }
            }

            long totalSampled = samples * ENTRIES_PER_BUCKET;
            if (totalSampled == 0) return 0;
            int percent = (int)((used * 100) / totalSampled);
            if (used > 0 && percent == 0) percent = 1;
            if (percent > 100) percent = 100;
            return percent;
        }

        public int GetSizeMB()
        {
            long numBuckets = (_bucketMask + 1L);
            long bytes = numBuckets * BUCKET_SIZE;
            return (int)(bytes / (1024L * 1024L));
        }

    }
}