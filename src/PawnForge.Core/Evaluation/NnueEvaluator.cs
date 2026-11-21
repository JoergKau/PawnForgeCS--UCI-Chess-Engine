namespace PawnForge.Core.Evaluation;

// ============================================================================
// NnueEvaluator.cs — the scheme-2 (v5 net format) evaluator seam (M6)
// ----------------------------------------------------------------------------
// Adapted from the previous generation's evaluator (MIT, ours) to PawnForgeCS's SEAM
// SIGNATURES: the search's 12 call sites (Search.Negamax ×10, QuiescenceSearch
// ×2) call
//
// SetPosition(pos) // FEN / search start
// MakeMove(pos, move, moving, captured) // AFTER the board's make (null moves arrive as Move.None)
// UnmakeMove // BEFORE the board's unmake
// MakeNullMove / UnmakeNullMove // the null-move pair
// Evaluate // centipawns, SIDE-TO-MOVE relative
// ClearHistory // ucinewgame hygiene (no-op here)
//
// — the same surface the previous evaluator exposes (its MakeMove is a documented no-op
// waiting for exactly this class). Position is MUTATED IN PLACE by the board,
// so the evaluator holds the position reference and Evaluate reads whatever
// the board currently is — the same trick the previous evaluator used.
//
// EVAL SEMANTICS (the eval-semantics rule): the v5 head is MOVER-relative — it is fed
// [mover POV, opponent POV] and trained on mover-relative targets — so
// Evaluate returns stm-relative cp with NO sign flip. A v4-style flip here
// would invert every black-to-move node (a failure the previous generation hit).
//
// THE HEAD INPUT ORDER (the head-order rule): the two accumulators go to the head in
// SIDE-TO-MOVE order, not white-first — for every black-to-move node,
// white-first runs a different linear function than the one trained
// (the previous generation hit exactly this; its parity tooling was wrong the same way and the parity
// gate stayed green — parity tooling can share the bug, which is why the
// polarity gates exist at M9).
// ============================================================================

/// <summary>The v5 evaluator: seam glue + the scheme-2 forward pass.</summary>
public sealed class NnueEvaluator : IPositionEvaluator
{
    private readonly NnueNetV5 _net;
    private readonly NnueAccumulatorV5 _accumulator;
    private Position _position = null!;

    /// <summary>The loaded v5 net (immutable after load — shared read-only across
    /// per-thread evaluator instances; only the accumulator is per-thread).</summary>
    public NnueNetV5 Net => _net;

    public NnueEvaluator(NnueNetV5 net)
    {
        _net = net;
        _accumulator = new NnueAccumulatorV5(net);
    }

    /// <summary>A fresh evaluator over the SAME net — one per Lazy SMP helper
    /// thread (the net is immutable and shared; only the accumulator is per-thread).</summary>
    public NnueEvaluator Clone() => new(_net);

    /// <summary>Full setup (FEN load / search start).</summary>
    public void SetPosition(Position pos)
    {
        _position = pos;
        _accumulator.SetPosition(pos);
    }

    /// <summary>Call AFTER the board's make (null moves arrive as
    /// <see cref="Move.None"/> — the accumulator then only pushes, since the
    /// board's dirty record is empty and no piece changed). The move/captured
    /// arguments are part of the seam signature and are deliberately unused:
    /// the v5 accumulator consumes the board's dirty-piece record.</summary>
    public void MakeMove(Position pos, Move move, Piece movingPiece, Piece captured)
    {
        _position = pos;
        _accumulator.MakeMove(pos);
    }

    /// <summary>Call BEFORE the board's unmake.</summary>
    public void UnmakeMove() => _accumulator.UnmakeMove();

    /// <summary>Drops nothing — the net has no caches. Present so the evaluator
    /// is drop-in for the seam's ucinewgame hygiene call.</summary>
    public void ClearHistory() { }

    /// <summary>Forward pass: centipawn eval from the SIDE TO MOVE's perspective
    /// (the negamax convention — identical to the classic evaluator seam), with NO
    /// sign flip: the v5 head is already mover-relative (see the eval-semantics note in the
    /// file header).</summary>
    public int Evaluate()
    {
        return Evaluate(_position);
    }

    /// <summary>Static-eval form for explicit positions (tests/tools): the same
    /// computation for an arbitrary position, ignoring the stored one.</summary>
    public int Evaluate(Position pos)
    {
        bool stmIsWhite = pos.SideToMove == 0;
        ReadOnlySpan<short> stm = _accumulator.GetAccumulator(pos, stmIsWhite ? NnueFeatureSet2.WhitePov : NnueFeatureSet2.BlackPov);
        ReadOnlySpan<short> ntm = _accumulator.GetAccumulator(pos, stmIsWhite ? NnueFeatureSet2.BlackPov : NnueFeatureSet2.WhitePov);
        return NnueQuantV5.ToCp(_net.Forward(stm, ntm));
    }
}
