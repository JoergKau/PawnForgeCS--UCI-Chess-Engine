namespace PawnForge.Core.Evaluation;

using System.Runtime.CompilerServices;

// ============================================================================
// NnueFeatureSet2.cs — feature scheme 2 (the v5 net format): 768 slots, king-region buckets
// ----------------------------------------------------------------------------
// Ported from the previous generation and adapted to PawnForgeCS's board API
// (Piece enum, Pieces[color][type] bitboards, int SideToMove). This is a faithful
// re-implementation of the trainer's BUILT-IN mirrored-bucket input mapping —
// "no custom InputType" is a deliberate decision: that mapping is a SECOND
// implementation we can diff against, and a bespoke one would be a
// silent-failure surface.
//
// THE INDEX (one slot per POV; the weight block is selected by the region)
// ----------------------------------------------------------------------
// slots = 768 = 2 colours x 6 piece types x 64 squares
// slot = colourOffset + type * 64 + localSq (colourOffset 0 own / 384 opp)
// localSq = sq if this POV is WHITE
// = sq ^ 56 if this POV is BLACK
// mirror = 7 if File(kingInPovFrame) > 3 else 0 (0b111 -> flips the 3 file bits)
// index = region * 768 + (slot ^ mirror) <- the accumulator weight ROW
// region = regionTable[kingInPovFrame] (the 64-byte header table)
//
// ⚠️ THE INDEX DEPENDS ON PIECE COLOUR, NOT ON THE SIDE TO MOVE — and that is
// not a detail. The trainer's record frame normalises every position so the
// mover reads as "white" (when black is to move it flips each square ^= 56 and
// swaps the colour bits, then stores ksq = the mover's king in its normalised
// square and opp_ksq = the opponent's king ^ 56). Read from OUR side the
// conclusion is simply: **the white perspective sees the real board and the
// black perspective sees it rank-flipped.** The side to move does not enter
// into it. Verified against the trainer's own mapping over 12
// asymmetric positions via the parity tooling. Reading it as "flip the side that is NOT to
// move" is wrong on 5 of 12 positions — it is correct for white-to-move and
// INVERTS for black-to-move.
//
// Consequence for the engine (the GOOD one): because the frame is
// stm-independent, the index is stm-independent, so **the scheme-2 accumulator
// CAN be incremental** with lazy own-king refresh — exactly like the reference's
// scheme. The v1.1 plan's "0.35x node rate" fear applies only to the
// stm-keyed misreading (a bug the previous generation shipped); this port carries the CORRECT
// frame from day one, pinned by M2's bit-exactness check.
//
// REGION SEMANTICS — one region id PER PERSPECTIVE, not one per position
// ----------------------------------------------------------------------
// The trainer's mirrored-bucket input computes `get(our_ksq)` and `get(opp_ksq)`:
// each PERSPECTIVE gets its own region and its own mirror. So our conditioning
// matches the trainer's two-ids-per-side exactly. (The "single id per position"
// loss of conditioning described in the old notes does not exist — it was a
// misreading, reported rather than averaged.)
//
// THE REGION TABLE — ONE declaration, TWO renderings
// --------------------------------------------------
// The table is 64 bytes in the net header AND an 8x4 mirrored table in the
// trainer graph. The trainer's bucket constructor takes [usize; 32] and
// expands it with the fixed file-mirror [0,1,2,3,3,2,1,0]. These must not be
// authored twice: `MirrorTable32` derives the trainer form from the 64-byte
// form, and a unit test asserts the expansion is byte-identical (both ways).
// ============================================================================

/// <summary>Common surface the accumulator/evaluator need from a feature set,
/// so the evaluator seam can carry either scheme.</summary>
public interface INnueFeatureSet
{
    /// <summary>Feature-scheme id stored in the net file.</summary>
    int SchemeId { get; }

    /// <summary>Total weight rows (one per feature index).</summary>
    int InputDimensions { get; }

    /// <summary>Full-refresh enumeration of every active feature index of one POV
    /// (one per board piece, both colours — 32 max active features).
    /// Returns the number of indices written.</summary>
    int GetActiveFeatures(Position pos, int pov, Span<int> features);

    /// <summary>One feature index (a weight ROW) for a single piece. The
    /// incremental path's primitive; kept for interface parity with scheme 1.</summary>
    int MakeIndex(Position pos, int pov, int pieceSquare, Piece piece);
}

/// <summary>Feature scheme 2: 768 slots per POV, one weight block per king
/// region.</summary>
public sealed class NnueFeatureSet2 : INnueFeatureSet
{
    /// <summary>Feature-scheme id stored in the net file (2 = this design).</summary>
    public const int SchemeId = 2;

    /// <summary>Slots per region: 2 colours x 6 piece types x 64 squares.</summary>
    public const int SlotsPerRegion = 768;

    /// <summary>POV index constants (0 = white POV, 1 = black POV — identical to
    /// the board's color indexing so every call site stays arithmetic).</summary>
    public const int WhitePov = 0;
    public const int BlackPov = 1;

    /// <summary>Legal region counts (power of two, 1..32) — mirrors the header
    /// bound so the loader and the constructor cannot disagree.</summary>
    public const int MinRegions = 1, MaxRegions = 32;

    private readonly byte[] _regionTable;

    /// <summary>Region count K (number of weight blocks).</summary>
    public int RegionCount { get; }

    /// <summary>The 64-byte king-region table, one byte per square (a1 = 0).</summary>
    public ReadOnlySpan<byte> RegionTable => _regionTable;

    int INnueFeatureSet.SchemeId => SchemeId;

    /// <summary>768 x K — one row per (region, slot).</summary>
    public int InputDimensions => SlotsPerRegion * RegionCount;

    /// <summary>Creates the feature set over a 64-byte region table; the table is
    /// validated here so an invalid one can never reach the accumulator.</summary>
    public NnueFeatureSet2(ReadOnlySpan<byte> regionTable)
    {
        if (regionTable.Length != 64)
            throw new ArgumentException($"king-region table must be 64 bytes, got {regionTable.Length}", nameof(regionTable));

        // K = max + 1, and it must be a power of two within the header bounds.
        int max = 0;
        for (int i = 0; i < 64; i++)
            if (regionTable[i] > max)
                max = regionTable[i];
        int regions = max + 1;
        if (regions < MinRegions || regions > MaxRegions || (regions & (regions - 1)) != 0)
            throw new ArgumentException($"king-region table implies K = {regions}; must be a power of two in {MinRegions}..{MaxRegions}", nameof(regionTable));

        // Every region must be non-empty: an unreachable region is dead weight in
        // the file AND a sign the table was written by hand and got it wrong.
        Span<bool> used = stackalloc bool[MaxRegions];
        foreach (byte r in regionTable)
            used[r] = true;
        for (int r = 0; r < regions; r++)
            if (!used[r])
                throw new ArgumentException($"king-region table: region {r} is never used (every region must be non-empty)", nameof(regionTable));

        _regionTable = regionTable.ToArray();
        RegionCount = regions;
    }

    // ---- the index -------------------------------------------------------------

    /// <summary>The frame flip of one POV: <b>0 for the white POV, 56 for the
    /// black POV — INDEPENDENT of the side to move.</b> See the file header for
    /// why (the trainer's mover-normalised record frame, read back into engine
    /// terms). <paramref name="pos"/> is deliberately unused: the frame is a
    /// property of the PERSPECTIVE alone, and keeping the parameter makes the
    /// stm-independence visible at every call site.</summary>
    public static int FrameFlip(Position pos, int pov) => (pov == WhitePov) ? 0 : 56;

    /// <summary>Whether <paramref name="pov"/> is the side to move. Used by the
    /// tests and for readability; note that the SQUARE FRAME deliberately does
    /// not depend on it (see <see cref="FrameFlip"/>).</summary>
    public static bool IsPovSideToMove(Position pos, int pov)
        => pov == pos.SideToMove;

    /// <summary>This POV's king square expressed in THIS POV's own frame — the
    /// trainer's `our_ksq` for the mover's perspective and `opp_ksq` for the
    /// other. The king bitboard always has exactly one bit in a legal position.</summary>
    public static int KingSquareInPovFrame(Position pos, int pov)
    {
        ulong kings = pos.Pieces[pov][5];
        // The king bitboard must hold exactly one bit in any legal position. When
        // it is empty, LsbIndex has nothing to point at and every region/index
        // derived from it is garbage — which used to surface as a bare
        // IndexOutOfRangeException from the caller's table lookup, hiding the
        // real cause (an illegal position reached mid-search). Name it here.
        if (kings == 0)
            throw new InvalidOperationException(
                $"no king on side {pov} in {pos.ToFEN()} — the position is not legal, " +
                "so no POV frame exists");
        return Bitboard.LsbIndex(kings) ^ FrameFlip(pos, pov);
    }

    /// <summary>Square expressed in this POV's own frame (rank-flipped for the
    /// black POV: ^56 flips the three rank bits).</summary>
    public static int SquareInPovFrame(Position pos, int pov, int square)
        => square ^ FrameFlip(pos, pov);

    /// <summary>Region id for a POV: a table lookup at that POV's own-frame king
    /// square.</summary>
    public int Region(Position pos, int pov) => _regionTable[KingSquareInPovFrame(pos, pov)];

    /// <summary>
    /// The POV's feature frame as one small integer: region id in the high bits
    /// and the horizontal-mirror bit in the low bit. Every feature index of that
    /// POV is a function of this frame, so two positions with the same frame have
    /// the same row indices even when the king moved. The incremental
    /// accumulator uses that equality to avoid a full refresh on an intra-frame
    /// king move; a frame change is the only case that must rebuild.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Frame(Position pos, int pov)
    {
        int ownFrameKing = KingSquareInPovFrame(pos, pov);
        return (_regionTable[ownFrameKing] << 3) | ((ownFrameKing & 7) > 3 ? 1 : 0);
    }

    /// <summary>Horizontal mirror mask for a POV: 0b111 when the POV's king stands
    /// on files e-h in its own frame, else 0. XOR-ing flips the 3 file bits — the
    /// colourOffset and type components have zero low bits, so only the file
    /// moves (the "hm" half-kAv2 trick: a position and its file mirror share rows).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mirror(Position pos, int pov)
    {
        int kingSquare = KingSquareInPovFrame(pos, pov);
        return (kingSquare & 7) > 3 ? 7 : 0;
    }

    /// <summary>The 768-slot index of one piece in one POV, BEFORE the mirror XOR
    /// and before the region offset. colourOffset 0 = this POV's own colour,
    /// 384 = the opponent's; type = the piece-type index 0..5; localSq = the
    /// square in this POV's frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MakeSlot(Position pos, int pov, int pieceSquare, Piece piece)
    {
        bool pieceIsOwn = (pov == WhitePov) == piece.IsWhite();
        int colourOffset = pieceIsOwn ? 0 : 384;
        int type = piece.ToIndex();
        return colourOffset + type * 64 + SquareInPovFrame(pos, pov, pieceSquare);
    }

    /// <summary>One feature index (weight ROW) = region * 768 + mirrored slot.
    /// This single expression is the entire hot-loop change from scheme 1's
    /// <c>index * L1</c> over a 1536*K index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int MakeIndex(Position pos, int pov, int pieceSquare, Piece piece)
        => Region(pos, pov) * SlotsPerRegion + (MakeSlot(pos, pov, pieceSquare, piece) ^ Mirror(pos, pov));

    int INnueFeatureSet.MakeIndex(Position pos, int pov, int pieceSquare, Piece piece)
        => MakeIndex(pos, pov, pieceSquare, piece);

    /// <summary>Full refresh: every active feature index of one POV, one per board
    /// piece (both colours, both kings — the half_ka_v2 trait). Mirrors the trainer's
    /// `Chess768.map_features` + the bucket/mirror wrapper exactly. The region
    /// and mirror are computed ONCE per POV (not per piece) — they depend only
    /// on the king square.</summary>
    public int GetActiveFeatures(Position pos, int pov, Span<int> features)
    {
        int baseIndex = Region(pos, pov) * SlotsPerRegion;
        int mirror = Mirror(pos, pov);
        int count = 0;
        for (int color = 0; color < 2; color++)
        {
            // Piece enum layout: WP..WK = 1..6 (white), BP..BK = 7..12 (black).
            Piece first = color == 0 ? Piece.WP : Piece.BP;
            for (int type = 0; type < 6; type++)
            {
                ulong bb = pos.Pieces[color][type];
                Piece piece = first + type;
                while (bb != 0)
                {
                    int square = Bitboard.LsbIndex(bb);
                    bb = Bitboard.ClearLsb(bb);
                    features[count++] = baseIndex + (MakeSlot(pos, pov, square, piece) ^ mirror);
                }
            }
        }
        return count;
    }

    int INnueFeatureSet.GetActiveFeatures(Position pos, int pov, Span<int> features)
        => GetActiveFeatures(pos, pov, features);

    /// <summary>A compact, deterministic, human-diffable dump of BOTH POVs — the
    /// exact text the cross-language bit-exactness check compares against the other
    /// implementation. Format per POV: region:mirror:slot,slot,…
    /// with slots SORTED and taken mod 768 (the region offset stripped — the set
    /// of slots must agree regardless of which block they landed in, and sorted
    /// so bitboard scan order vs python-chess iteration order is irrelevant).
    /// One line: "&lt;pov0&gt;|&lt;pov1&gt;". Instance method: the dump is over THIS
    /// feature set's region table (the bit-exactness check constructs the default).</summary>
    public string Describe(Position pos)
    {
        Span<int> buffer = stackalloc int[32];
        var parts = new string[2];
        for (int pov = 0; pov < 2; pov++)
        {
            int region = Region(pos, pov);
            int mirror = Mirror(pos, pov);
            int count = GetActiveFeatures(pos, pov, buffer);
            var slots = new int[count];
            for (int i = 0; i < count; i++)
                slots[i] = buffer[i] % SlotsPerRegion;
            System.Array.Sort(slots);
            parts[pov] = $"{region}:{mirror}:" + string.Join(",", slots);
        }
        return string.Join("|", parts);
    }

    /// <summary>Static convenience over the default region table — what the
    /// bit-exactness check and debug tooling use.</summary>
    public static string DescribeDefault(Position pos)
    {
        var features = new NnueFeatureSet2(DefaultRegionTable);
        return features.Describe(pos);
    }

    // ---- the default table ----------------------------------------------------

    /// <summary>The region map (K = 8) in SQUARE-INDEX order (a1 = index 0,
    /// h8 = index 63). <b>The literal is in index order, so rank 1 comes
    /// FIRST</b> — the spec prints the map the other way up (rank 8 at the top,
    /// like a board diagram), and transposing it is the easiest mistake in the
    /// whole table (the known-failure list's F11). `MirrorTable32` also
    /// emits rank 1 first, and the two agreeing is itself part of what the unit
    /// tests pin.</summary>
    ///
    /// <para>GENERATION 2 (2026-10-13, Network-Analysis/ — the r4/r5
    /// merge): the old map split rank 5 into two 4-square regions (r4 flank
    /// files, r5 centre files) that covered 0.79 %/1.30 % of POV events
    /// (3.08 % real-game, NnueRegionCounters bench) — too little gradient, so
    /// 14 feature rows per region decayed to exactly zero (28 genuinely dead
    /// rows in the shipped champion). The merged map folds rank 5 into the
    /// per-rank forward bands: ranks 5/6/7/8 each become ONE all-files region
    /// of 8 squares with 4 live own-king rows — the empirically healthy shape
    /// (the old r6/r7 reported extra = 0). The audit's literal proposal (one
    /// 24-square ranks-5-7 region, K = 7) is not realisable: K must be a power
    /// of two in the frozen v5 format. The engine reads the table from the net
    /// HEADER (old and new nets both load); this default is what the parity
    /// gates and the tests construct — it must move together with
    /// ` and the trainer's `` table.</para>
    public static ReadOnlySpan<byte> DefaultRegionTable => new byte[]
    {
        // rank 1 (squares 0..7, files a..h)
        0, 1, 2, 3, 3, 2, 1, 0,
        // rank 2 (8..15)
        0, 1, 2, 3, 3, 2, 1, 0,
        // rank 3 (16..23)
        0, 1, 2, 3, 3, 2, 1, 0,
        // rank 4 (24..31)
        0, 1, 2, 3, 3, 2, 1, 0,
        // rank 5 (32..39) — merged: was (a,b,g,h)->r4, rest->r5 (the dead rows)
        4, 4, 4, 4, 4, 4, 4, 4,
        // rank 6 (40..47) — was r6
        5, 5, 5, 5, 5, 5, 5, 5,
        // rank 7 (48..55) — was r6
        6, 6, 6, 6, 6, 6, 6, 6,
        // rank 8 (56..63) — was r7
        7, 7, 7, 7, 7, 7, 7, 7,
    };

    /// <summary>The fixed file-mirror pattern: the
    /// expansion of a [usize; 32] table is
    /// <c>buckets[(idx / 8) * 4 + [0,1,2,3,3,2,1,0][idx % 8]]</c>.</summary>
    public static ReadOnlySpan<byte> FileMirrorPattern => new byte[] { 0, 1, 2, 3, 3, 2, 1, 0 };

    /// <summary>The SAME table rendered in the form the trainer's bucket
    /// constructor takes: 8 rows x 4 file-pairs, rank 1 first.
    /// <b>This is the array to paste into the trainer graph — deriving it here
    /// is what stops the header and the trainer from being authored twice.</b>
    /// Throws if the 64-byte table is not file-mirror symmetric, because a table
    /// the trainer cannot represent would silently mean something else in training.</summary>
    public static int[] MirrorTable32() => MirrorTable32(DefaultRegionTable);

    /// <summary>Same conversion, from an ARBITRARY 64-byte table. The public
    /// parameterless form is the default table; this one exists so the "trainer
    /// cannot represent it" guard is testable at all.</summary>
    public static int[] MirrorTable32(ReadOnlySpan<byte> table)
    {
        if (table.Length != 64)
            throw new ArgumentException("king-region table must be 64 bytes", nameof(table));
        int[] rows = new int[32];
        // A separate `written` bitmap, not a sentinel in `rows`: region 0 is a
        // perfectly legal value, so "rows[slot] == 0" cannot distinguish "not yet
        // filled" from "filled with region 0" — and a table whose FIRST value in a
        // slot is 0 would silently skip the symmetry check on that pair.
        Span<bool> written = stackalloc bool[32];
        ReadOnlySpan<byte> pattern = FileMirrorPattern;
        for (int sq = 0; sq < 64; sq++)
        {
            byte value = table[sq];
            int slot = (sq / 8) * 4 + pattern[sq % 8];
            if (written[slot] && rows[slot] != value)
                throw new InvalidOperationException(
                    $"king-region table is not file-mirror symmetric at square {sq} " +
                    $"(region {value} where its mirror square has region {rows[slot]}): the trainer cannot represent it");
            rows[slot] = value;
            written[slot] = true;
        }
        return rows;
    }

    /// <summary>The trainer's expansion of a [usize; 32] table — the reference the
    /// 64-byte form must equal (a unit test pins this both ways).</summary>
    public static byte[] ExpandMirrorTable32(ReadOnlySpan<int> rows)
    {
        if (rows.Length != 32)
            throw new ArgumentException("mirrored table must be 8x4 = 32 entries", nameof(rows));
        byte[] expanded = new byte[64];
        ReadOnlySpan<byte> pattern = FileMirrorPattern;
        for (int sq = 0; sq < 64; sq++)
            expanded[sq] = (byte)rows[(sq / 8) * 4 + pattern[sq % 8]];
        return expanded;
    }
}
