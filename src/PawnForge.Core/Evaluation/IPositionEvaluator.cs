namespace PawnForge.Core.Evaluation;

// ============================================================================
// IPositionEvaluator.cs — the evaluator seam the NNUE implements (M7)
// ----------------------------------------------------------------------------
// PawnForgeCS's search consumes evaluation through ONE seam (the 12 call
// sites), and the engine has ONE evaluator arm: the v5 NNUE. This interface
// is the contract, so the search's call sites stay unchanged and the
// orchestrator decides when the evaluator is attached.
//
// CALL DISCIPLINE (the seam contract, identical for both arms):
// SetPosition — FEN / search start
// MakeMove(pos, move, …) — AFTER the board's make (null moves arrive as Move.None)
// UnmakeMove — BEFORE the board's unmake
// Evaluate — centipawns, SIDE-TO-MOVE relative
// ClearHistory — ucinewgame hygiene (may be a no-op)
//
// The move/captured arguments of MakeMove exist for seam compatibility; the
// NNUE ignores them and consumes the board's dirty-piece record instead
// (M4/M5). The seam stays drop-in: a Search instance never knows which
// evaluator implementation it holds.
// ============================================================================

/// <summary>The evaluator seam the NNUE implements.</summary>
public interface IPositionEvaluator
{
    /// <summary>Attach a position (FEN load / search start) — full rebuild of the arm's state.</summary>
    void SetPosition(Position position);

    /// <summary>Consume a board make — call AFTER the board's make. Null moves
    /// arrive as <see cref="Move.None"/>. <paramref name="movingPiece"/> and
    /// <paramref name="captured"/> were the previous evaluator's incremental deltas; the NNUE
    /// consumes the board's dirty-piece record instead.</summary>
    void MakeMove(Position position, Move move, Piece movingPiece, Piece captured);

    /// <summary>Undo the arm's state for one make — call BEFORE the board's unmake.</summary>
    void UnmakeMove();

    /// <summary>Centipawn evaluation of the current position, side-to-move
    /// relative (positive = good for the side to move).</summary>
    int Evaluate();

    /// <summary>ucinewgame hygiene — drop any caches. May be a no-op.</summary>
    void ClearHistory();
}
