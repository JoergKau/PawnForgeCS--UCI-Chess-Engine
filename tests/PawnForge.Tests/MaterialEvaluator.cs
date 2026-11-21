namespace PawnForge.Tests;

using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;

/// <summary>
/// Deterministic MATERIAL-ONLY evaluator for search/quiescence WIRING tests that
/// must not depend on the embedded net.
///
/// WHY (2026-11-02): two review guards asserted on the NNUE's exact eval —
/// a fail-high threshold at <c>stand-pat + 1</c> and a differential node count.
/// Promoting the new net moved the first by 1 cp (516 → 515) and collapsed the
/// second to exactly zero (23346 == 23346), a fragility class. A wiring
/// test must prove that a switch reaches the code path, NOT that the current net
/// happens to make one hand-picked position behave a certain way. Material values
/// never change, so these probes become net-independent.
///
/// This is a TEST DOUBLE, not a third evaluator arm (D1 stands: the engine has
/// exactly one evaluator, the NNUE). It implements the same seam
/// <see cref="IPositionEvaluator"/> that the engine's search consumes.
/// </summary>
internal sealed class MaterialEvaluator : IPositionEvaluator
{
    /// <summary>P, N, B, R, Q, K — indexed by <see cref="PieceExtensions.ToIndex"/>.
    /// The classic values, deliberately not tuned: the tests only need "a rook is
    /// worth more than a bishop, and a won queen is worth more than standing pat".</summary>
    private static readonly int[] Values = { 100, 320, 330, 500, 900, 0 };

    private Position _position = null!;

    /// <inheritdoc />
    public void SetPosition(Position position) => _position = position;

    /// <summary>The BOARD is authoritative: this evaluator keeps no incremental state,
    /// so a make/unmake pair needs no bookkeeping at all.</summary>
    public void MakeMove(Position position, Move move, Piece movingPiece, Piece captured)
    {
    }

    /// <inheritdoc />
    public void UnmakeMove()
    {
    }

    /// <inheritdoc />
    public void ClearHistory()
    {
    }

    /// <summary>Centipawns, side-to-move relative (the seam contract).</summary>
    public int Evaluate()
    {
        int white = 0;
        int black = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            Piece piece = _position.GetPieceAt(sq);
            if (piece == Piece.Empty)
            {
                continue;
            }

            int value = Values[piece.ToIndex()];
            if (piece.IsWhite())
            {
                white += value;
            }
            else
            {
                black += value;
            }
        }

        int diff = white - black;
        return _position.SideToMove == 0 ? diff : -diff;
    }
}
