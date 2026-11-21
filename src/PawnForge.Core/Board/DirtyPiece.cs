namespace PawnForge.Core.Board;

/// <summary>
/// One piece-square delta of the most recent make: a piece that was removed
/// from a square (<c>Added == false</c>) or placed on one (<c>Added == true</c>).
/// </summary>
/// <param name="Square">The square the piece was removed from / placed on (A1 = 0).</param>
/// <param name="Piece">The piece involved — the MOVED piece identity for removals
/// (the pre-promotion pawn for a promotion), the FINAL piece for additions.</param>
/// <param name="Added">True = placed on the square, false = removed from it.</param>
/// <remarks>
/// <para>
/// Migration M4 (ported from PawnForge v1.1's, 2026-10-07): the
/// board now records what a make changed, so consumers (the NNUE feature
/// accumulator) replay ≤ 4 deltas instead of re-deriving them per move type.
/// The record is written by <see cref="MoveExecution.MakeMove"/> in
/// board-mutation order, reset at the start of every make and on every
/// unmake/null move — it always describes EXACTLY the most recent make and
/// nothing else.
/// </para>
/// <para>
/// Maximum entries per make: 4 (castling = king remove + king place + rook
/// remove + rook place). Capture = 3, en passant = 3, promotion = 3, quiet = 2.
/// </para>
/// </remarks>
public readonly record struct DirtyPiece(int Square, Piece Piece, bool Added)
{
    /// <summary>Maximum dirty entries a single make can produce (castling).</summary>
    public const int MaxPerMake = 4;
}
