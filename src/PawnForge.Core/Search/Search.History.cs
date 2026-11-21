// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · killer/counter/history update helpers (delegate to SearchHeuristics)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Tb;
using TTable = PawnForge.Core.TranspositionTable.TTable;

namespace PawnForge.Core.Search;

public partial class Search
{
    private void UpdateKillers(Move m, int ply)
    {
        SearchHeuristics.UpdateKillers(_killers, m, ply);
    }

    private void UpdateHistory(int side, Move m, int depth, ReadOnlySpan<Move> badMoves)
    {
        SearchHeuristics.UpdateHistory(_td, side, m, depth, badMoves);
    }

    private void UpdateContHistory(int[][] table, Move m, int depth, bool bonus = true)
    {
        SearchHeuristics.UpdateContHistory(table, m, depth, bonus);
    }

    private void UpdateCaptureHistory(int pieceIdx, int to, int victimIdx, int depth)
    {
        SearchHeuristics.UpdateCaptureHistory(_td, pieceIdx, to, victimIdx, depth);
    }

    private int PieceToIndex(Piece p)
    {
        return SearchHeuristics.PieceToIndex(p);
    }
}
