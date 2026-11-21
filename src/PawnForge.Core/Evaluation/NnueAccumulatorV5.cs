namespace PawnForge.Core.Evaluation;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ============================================================================
// NnueAccumulatorV5.cs — scheme-2 accumulator pair (M5), INCREMENTAL
// ----------------------------------------------------------------------------
// Ported from the previous generation's accumulator (the same codebase
// lineage; its incremental design). Same seam contract as the classic NNUE
// accumulator (set position / make after the board's make / unmake before the
// board's unmake / null moves push too), and the SAME update strategy: fully
// incremental with a lazy own-king refresh.
//
// WHY INCREMENTAL IS POSSIBLE AT ALL (the frame-keying finding, carried by our
// NnueFeatureSet2 from day one and pinned by M2's bit-exactness check):
//
// * FrameFlip is stm-INDEPENDENT: 0 for the white POV, 56 for the black POV,
// never a function of pos.SideToMove.
// * The region and the mirror of a POV depend only on that POV's OWN king
// square — also stm-independent.
// * Therefore the feature index of a piece is stm-independent, and a make
// that does not move a POV's own king can be consumed incrementally: one
// add or subtract per dirty piece.
//
// THE COST THIS REMOVES (measured in the previous generation): an always-rebuild
// design re-adds 2 x up-to-32 feature rows x L1 vector ops at every
// evaluation, where the incremental path does 2-4 dirty pieces x L1 vector
// ops — a 16-32x reduction on the update path. A king move still pays the
// full refresh, but LAZILY (only the moved king's POV, only at the next
// evaluation), and only when the move crosses a region/mirror boundary — a king
// step inside the same frame changes no row index and stays incremental. A node
// that makes several moves before evaluating still rebuilds once, not once per
// move.
//
// PER-PLY STATE — WHY THERE IS NO PUSH/POP COPY
// ----------------------------------------------------------------------------
// The state lives in one accumulator pair per search ply (`_state[ply]`), not
// in "one live pair plus a saved copy". A make writes `_state[ply + 1]`
// directly from `_state[ply]`; an unmake is `ply--`. That removes the 2 KB
// copy on every push and on every pop, and it lets the make fuse the copy and
// the row updates into a single load-modify-store pass:
//
//     child = parent + sum(added rows) - sum(subtracted rows)
//
// i16 wrapping arithmetic is associative, so the fused result is
// bit-identical to copy-then-apply. The three shapes a make can produce are
// compile-time constants of the move type — quiet (1 add / 1 subtract),
// capture or promotion (1 add / 2 subtract), castling (2 add / 2 subtract) —
// and each has its own unrolled loop. A shape that is not one of those takes
// the generic loop.
//
// THE GATE: the identity that protects this class is INCREMENTAL == COLD-START
// — after an arbitrary random walk, the incrementally updated accumulator must
// equal a from-scratch one, bit for bit. NnueAccumulatorV5Tests pins that,
// including walks that cross king moves (region changes), captures,
// promotions, castling, en passant and null moves, at every legal L1 width.
// ============================================================================

/// <summary>Scheme-2 (the v5 net format) per-POV i16 accumulator pair with INCREMENTAL
/// updates, lazy own-king refresh and per-ply state instead of a saved-copy stack.</summary>
public sealed class NnueAccumulatorV5
{
    private const int StackCapacity = 2048; // matches the board history depth

    private readonly NnueNetV5 _net;

    /// <summary>One state per ply: [pov 0's L1 lanes | pov 1's L1 lanes]. The
    /// current ply is <see cref="_ply"/>; a make writes the next slot and an
    /// unmake steps back, so no state is ever copied on push/pop.</summary>
    private readonly short[][] _state = new short[StackCapacity][];

    /// <summary>Per-ply stale flags: a POV whose feature frame changed is rebuilt
    /// at the next read; until then its lanes hold the parent's (or an older
    /// branch's) values and must not be consumed.</summary>
    private readonly bool[][] _stateStale = new bool[StackCapacity][];

    /// <summary>Per-ply feature frame (region + mirror) of each POV. A king move
    /// that stays inside the same frame does not change any row index, so it can
    /// be applied incrementally instead of forcing a rebuild.</summary>
    private readonly byte[][] _stateFrame = new byte[StackCapacity][];

    /// <summary>Current search ply (0 = setup position).</summary>
    private int _ply;

    /// <summary>The net this accumulator was built over — exposed so the
    /// incremental==cold-start gate can construct an independent reference
    /// accumulator over the SAME weights instead of comparing against itself.</summary>
    public NnueNetV5 Net { get; }

    public NnueAccumulatorV5(NnueNetV5 net)
    {
        _net = net;
        Net = net;
        for (int i = 0; i < StackCapacity; i++)
        {
            _state[i] = new short[net.L1 * 2];
            _stateStale[i] = new bool[2];
            _stateFrame[i] = new byte[2];
        }
    }

    /// <summary>Full setup (FEN load / search start): rebuild both POVs at ply 0.
    /// Deeper plies are left untouched — they are invalid until a make writes
    /// them again, and every make writes its child slot completely.</summary>
    public void SetPosition(Position pos)
    {
        _ply = 0;
        RefreshPov(pos, 0, NnueFeatureSet2.WhitePov);
        RefreshPov(pos, 0, NnueFeatureSet2.BlackPov);
        _stateStale[0][0] = false;
        _stateStale[0][1] = false;
    }

    /// <summary>Consumes the board's make: writes the next ply's state from the
    /// current one and applies the board's dirty-piece record incrementally.
    /// Call AFTER the board's MakeMove (the dirty list and the post-make king
    /// squares are then valid — the same discipline the classic scheme uses,
    /// for the same reason: a non-king update must be indexed with the king
    /// square it will be CONSUMED under).</summary>
    public void MakeMove(Position pos)
    {
        int child = _ply + 1;
        if (child >= StackCapacity)
            throw new InvalidOperationException(
                $"NNUE accumulator ply OVERFLOW at {child} (capacity {StackCapacity}): " +
                "the search made a move without a matching unmake.");

        _ply = child;
        ApplyDirty(pos, child);
    }

    /// <summary>Null move: no piece changes and no POV's own king moves, so the
    /// child state is an exact copy of the parent's values and stale flags —
    /// only the ply advances.</summary>
    public void MakeNullMove()
    {
        int child = _ply + 1;
        if (child >= StackCapacity)
            throw new InvalidOperationException(
                $"NNUE accumulator ply OVERFLOW at {child} (capacity {StackCapacity}): " +
                "the search made a null move without a matching unmake.");

        Array.Copy(_state[_ply], _state[child], _state[child].Length);
        _stateStale[child][0] = _stateStale[_ply][0];
        _stateStale[child][1] = _stateStale[_ply][1];
        _stateFrame[child][0] = _stateFrame[_ply][0];
        _stateFrame[child][1] = _stateFrame[_ply][1];
        _ply = child;
    }

    /// <summary>Steps back one ply (call BEFORE board unmake). The parent state
    /// was never overwritten, so no restore copy is needed.</summary>
    public void UnmakeMove()
    {
        if (_ply <= 0)
            throw new InvalidOperationException(
                $"NNUE accumulator ply UNDERFLOW (depth {_ply}): the search called " +
                "UnmakeMove more often than MakeMove.");
        _ply--;
    }

    /// <summary>Alias for the null-move pop.</summary>
    public void UnmakeNullMove() => UnmakeMove();

    /// <summary>The POV's accumulator at the current ply, refreshed lazily when
    /// its own king has moved since it was last built.</summary>
    public ReadOnlySpan<short> GetAccumulator(Position pos, int pov)
    {
        if (_stateStale[_ply][pov])
            RefreshPov(pos, _ply, pov);
        int l1 = _net.L1;
        return new ReadOnlySpan<short>(_state[_ply], pov * l1, l1);
    }

    /// <summary>Whether the POV needs a rebuild at the current ply (test visibility).</summary>
    public bool IsStale(int pov) => _stateStale[_ply][pov];

    /// <summary>Stack depth (test visibility).</summary>
    public int StackPly => _ply;

    /// <summary>Forces a rebuild of one POV at the current ply (the
    /// incremental==full-refresh identity's reference path — the tests use it
    /// as the oracle).</summary>
    public void ForceRefresh(Position pos, int pov) => RefreshPov(pos, _ply, pov);

    // ---- internals ------------------------------------------------------------

    /// <summary>Fused full refresh: one pass over the L1 lanes. Each vector chunk
    /// starts from the bias and adds every active feature row's chunk in a
    /// register, then stores once — instead of one full load-modify-store pass
    /// per active row. Wrapping i16 addition is exact and order-free, so the
    /// result is bit-identical to the row-at-a-time form.</summary>
    private void RefreshPov(Position pos, int ply, int pov)
    {
        // The runtime region counter: a full refresh of one POV happens exactly
        // when that POV's own king reselects its region/mirror frame (or on cold
        // start), so this is the one place that measures the REAL-GAME
        // king-region distribution. Opt-in: when disabled (every normal search
        // and every perf measurement) this is one predictable branch.
        NnueRegionCounters.Record(_net.Features.Region(pos, pov));

        Span<int> features = stackalloc int[32]; // at most 32 pieces
        int count = _net.Features.GetActiveFeatures(pos, pov, features);

        int l1 = _net.L1;
        Span<short> acc = new Span<short>(_state[ply], pov * l1, l1);
        ReadOnlySpan<short> bias = _net.FeatureBiases;
        ReadOnlySpan<short> weights = _net.FeatureWeights;

        ref short a = ref MemoryMarshal.GetReference(acc);
        ref short b = ref MemoryMarshal.GetReference(bias);
        ref short w = ref MemoryMarshal.GetReference(weights);
        NnueVectorOps.Refresh(ref a, ref b, ref w, features, count, l1);

        _stateStale[ply][pov] = false;
        _stateFrame[ply][pov] = (byte)_net.Features.Frame(pos, pov);
    }

    /// <summary>Consumes the board's dirty-piece record into the child ply.
    /// A change of the POV's feature frame (region + mirror) marks it stale:
    /// every row index of that POV is selected by the frame, so no incremental
    /// update can survive a frame change. A king move that stays inside the
    /// frame changes no index and is applied as an ordinary add/subtract, and
    /// every other dirty piece (including the OPPONENT's king) is likewise one
    /// row add/subtract.</summary>
    private void ApplyDirty(Position pos, int childPly)
    {
        int l1 = _net.L1;
        Span<int> add0 = stackalloc int[4];
        Span<int> sub0 = stackalloc int[4];
        Span<int> add1 = stackalloc int[4];
        Span<int> sub1 = stackalloc int[4];
        int nAdd0 = 0, nSub0 = 0, nAdd1 = 0, nSub1 = 0;
        int parentPly = childPly - 1;

        // The frame is the region plus the mirror bit. It selects every row
        // index of a POV, so an unchanged frame survives ANY piece move —
        // including a king move inside the same region/mirror. Only a frame
        // change (or an already-stale parent) forces a rebuild.
        int frame0 = _net.Features.Frame(pos, NnueFeatureSet2.WhitePov);
        int frame1 = _net.Features.Frame(pos, NnueFeatureSet2.BlackPov);
        bool stale0 = _stateStale[parentPly][0] || frame0 != _stateFrame[parentPly][0];
        bool stale1 = _stateStale[parentPly][1] || frame1 != _stateFrame[parentPly][1];

        foreach (DirtyPiece d in pos.DirtyPieces)
        {
            if (!stale0)
            {
                int index = _net.Features.MakeIndex(pos, NnueFeatureSet2.WhitePov, d.Square, d.Piece);
                if (d.Added) add0[nAdd0++] = index;
                else sub0[nSub0++] = index;
            }

            if (!stale1)
            {
                int index = _net.Features.MakeIndex(pos, NnueFeatureSet2.BlackPov, d.Square, d.Piece);
                if (d.Added) add1[nAdd1++] = index;
                else sub1[nSub1++] = index;
            }
        }

        _stateStale[childPly][0] = stale0;
        _stateStale[childPly][1] = stale1;
        _stateFrame[childPly][0] = (byte)frame0;
        _stateFrame[childPly][1] = (byte)frame1;

        if (!stale0)
            FusedApply(_state[childPly - 1], _state[childPly], 0, l1,
                add0[..nAdd0], sub0[..nSub0]);
        if (!stale1)
            FusedApply(_state[childPly - 1], _state[childPly], l1, l1,
                add1[..nAdd1], sub1[..nSub1]);
    }

    /// <summary>Fused copy + row updates for one POV. The row counts are the
    /// move shape (1/1 quiet, 1/2 capture or promotion, 2/2 castling); the three
    /// common shapes have unrolled loops because a runtime loop over a row list
    /// is not unrolled by the JIT and the unrolling is part of the gain. Any
    /// other shape (or a null move with no rows) takes the generic/copy path.
    /// <para>Wrapping i16 arithmetic is associative, so every path is
    /// bit-identical to copying the parent and applying the rows one at a
    /// time.</para></summary>
    private void FusedApply(short[] parent, short[] child, int povOffset, int l1,
        ReadOnlySpan<int> adds, ReadOnlySpan<int> subs)
    {
        ReadOnlySpan<short> weights = _net.FeatureWeights;
        ref short p = ref MemoryMarshal.GetArrayDataReference(parent);
        ref short c = ref MemoryMarshal.GetArrayDataReference(child);
        ref short w = ref MemoryMarshal.GetReference(weights);
        p = ref Unsafe.Add(ref p, povOffset);
        c = ref Unsafe.Add(ref c, povOffset);

        if (adds.Length == 1 && subs.Length == 1)
            NnueVectorOps.Fused1Add1Sub(ref p, ref c, ref w, adds[0] * l1, subs[0] * l1, l1);
        else if (adds.Length == 1 && subs.Length == 2)
            NnueVectorOps.Fused1Add2Sub(ref p, ref c, ref w,
                adds[0] * l1, subs[0] * l1, subs[1] * l1, l1);
        else if (adds.Length == 2 && subs.Length == 2)
            NnueVectorOps.Fused2Add2Sub(ref p, ref c, ref w,
                adds[0] * l1, adds[1] * l1, subs[0] * l1, subs[1] * l1, l1);
        else
            NnueVectorOps.FusedGeneric(ref p, ref c, ref w, adds, subs, l1);
    }
}

