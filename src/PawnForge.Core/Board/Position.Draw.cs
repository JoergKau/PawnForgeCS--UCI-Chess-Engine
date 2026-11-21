// ═══════════════════════════════════════════════════════════════════════════════
// BOARD · draw / repetition detection
// ═══════════════════════════════════════════════════════════════════════════════

using System;

namespace PawnForge.Core.Board;

public partial class Position
{
    public bool IsFiftyMoveDraw()
    {
        return HalfmoveClock >= 100;
    }

    /// <summary>
    /// Get the number of times the current position has occurred in the game history.
    /// Returns 0 if this is the first occurrence, 1 if seen once before (2 total), etc.
    /// Only counts positions since the last irreversible move (pawn move or capture).
    /// This is used to prevent TT cutoffs when a position has appeared before,
    /// as the cached score may not account for the impending threefold repetition.
    /// </summary>
    public int GetRepetitionCount()
    {
        // Current position counts as one occurrence
        int count = 1;

        // Search backward through history
        int end = Math.Max(0, historyPly - HalfmoveClock);

        for (int i = historyPly - 2; i >= end; i -= 2)
        {
            if (historyStack[i].ZobristKey == ZobristKey)
            {
                count++;
            }
        }

        // Return how many times we've seen this position BEFORE now
        // (subtract 1 because count includes current position)
        return count - 1;
    }

    /// <summary>
    /// Check if the current position has occurred at least once before (twofold repetition,
    /// 2 total occurrences). Used by the search when <c>UseTwofoldRepetitionDraw</c> is on:
    /// the standard "twofold inside the search tree" draw convention (the reference/leading mainstream engines) —
    /// threefold remains the game-claim rule, which is what <see cref="IsDraw"/> implements.
    /// Only counts positions since the last irreversible move (pawn move or capture).
    /// </summary>
    public bool IsTwofoldRepetition()
    {
        return GetRepetitionCount() >= 1; // Seen once before = 2 total
    }

    /// <summary>
    /// Check if the current position is a threefold repetition.
    /// Searches through the position history for repetitions of the current Zobrist key.
    /// Returns true if the current position has occurred at least twice before (3 times total).
    /// Only counts positions since the last irreversible move (pawn move or capture).
    /// </summary>
    public bool IsThreefoldRepetition()
    {
        return GetRepetitionCount() >= 2; // Seen 2 or more times before = 3+ total
    }

    /// <summary>
    /// Check if the current position has insufficient material for either side to deliver checkmate.
    /// Returns true for the following endgames:
    /// - King vs King
    /// - King + Knight vs King
    /// - King + Bishop vs King
    /// - King + Bishop vs King + Bishop (bishops on same color squares)
    /// </summary>
    public bool IsInsufficientMaterial()
    {
        // Count material for both sides
        int whitePawns = Bitboard.PopCount(pieces[0][ 0]);
        int blackPawns = Bitboard.PopCount(pieces[1][ 0]);
        int whiteKnights = Bitboard.PopCount(pieces[0][ 1]);
        int blackKnights = Bitboard.PopCount(pieces[1][ 1]);
        int whiteBishops = Bitboard.PopCount(pieces[0][ 2]);
        int blackBishops = Bitboard.PopCount(pieces[1][ 2]);
        int whiteRooks = Bitboard.PopCount(pieces[0][ 3]);
        int blackRooks = Bitboard.PopCount(pieces[1][ 3]);
        int whiteQueens = Bitboard.PopCount(pieces[0][ 4]);
        int blackQueens = Bitboard.PopCount(pieces[1][ 4]);

        // If there are any pawns, rooks, or queens, there's sufficient material
        if (whitePawns > 0 || blackPawns > 0 ||
            whiteRooks > 0 || blackRooks > 0 ||
            whiteQueens > 0 || blackQueens > 0)
        {
            return false;
        }

        // King vs King
        if (whiteKnights == 0 && blackKnights == 0 &&
            whiteBishops == 0 && blackBishops == 0)
        {
            return true;
        }

        // King + Knight vs King
        if ((whiteKnights == 1 && whiteBishops == 0 && blackKnights == 0 && blackBishops == 0) ||
            (blackKnights == 1 && blackBishops == 0 && whiteKnights == 0 && whiteBishops == 0))
        {
            return true;
        }

        // King + Bishop vs King
        if ((whiteBishops == 1 && whiteKnights == 0 && blackKnights == 0 && blackBishops == 0) ||
            (blackBishops == 1 && blackKnights == 0 && whiteKnights == 0 && whiteBishops == 0))
        {
            return true;
        }

        // King + Bishop vs King + Bishop (bishops on same color)
        if (whiteKnights == 0 && blackKnights == 0 &&
            whiteBishops == 1 && blackBishops == 1)
        {
            // Check if bishops are on the same color squares
            int whiteBishopSquare = Bitboard.LsbIndex(pieces[0][ 2]);
            int blackBishopSquare = Bitboard.LsbIndex(pieces[1][ 2]);

            // Square color: (rank + file) % 2 == 0 for dark squares, == 1 for light squares
            int whiteBishopFile = whiteBishopSquare % 8;
            int whiteBishopRank = whiteBishopSquare / 8;
            int blackBishopFile = blackBishopSquare % 8;
            int blackBishopRank = blackBishopSquare / 8;

            int whiteBishopColor = (whiteBishopRank + whiteBishopFile) % 2;
            int blackBishopColor = (blackBishopRank + blackBishopFile) % 2;

            if (whiteBishopColor == blackBishopColor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Comprehensive draw detection. Returns true if the position is drawn by:
    /// - Fifty-move rule (halfmove clock >= 100)
    /// - Threefold repetition
    /// - Insufficient material
    /// </summary>
    public bool IsDraw()
    {
        return IsFiftyMoveDraw() || IsThreefoldRepetition() || IsInsufficientMaterial();
    }

    /// <summary>
    /// Draw detection for SEARCH nodes (game-claim rules are <see cref="IsDraw"/>).
    /// Identical to <see cref="IsDraw"/> except that the repetition threshold is
    /// selectable: with <paramref name="twofoldRepetition"/> the SECOND occurrence of a
    /// position inside the search tree already scores as a draw — the "twofold in search,
    /// threefold in game" convention the reference uses (<c>Score.isDoubleRepetition</c>) and the
    /// endgame-strength lever diagnosed in(offense: never walks
    /// into a repetition in won endings; defense: finds the drawing repetition one ply
    /// earlier in worse endings). With false, the legacy threefold-only behavior is
    /// reproduced exactly.
    /// </summary>
    public bool IsDrawForSearch(bool twofoldRepetition)
    {
        return IsFiftyMoveDraw()
            || (twofoldRepetition ? IsTwofoldRepetition() : IsThreefoldRepetition())
            || IsInsufficientMaterial();
    }
}
