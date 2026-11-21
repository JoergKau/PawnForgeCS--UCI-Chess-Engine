// ═══════════════════════════════════════════════════════════════════════════════
// BOARD · static exchange evaluation (SEE)
// ═══════════════════════════════════════════════════════════════════════════════

using System;

namespace PawnForge.Core.Board;

public partial class Position
{
    // Static Exchange Evaluation (SEE)
    // Implements recursive capture simulation (ported from Rust reference implementation)
    public bool See(Move mv, int threshold)
    {
        // Castling always passes
        if (mv.IsCastle) return true;

        int moveVal = MoveValue(mv);
        int balance = moveVal - threshold;
        if (balance < 0) return false;

        // subtract our moving piece's base value (worst case we lose it)
        var fromPiece = board[mv.From];
        balance -= PieceValues[PieceToIndex(fromPiece)];

        if (mv.Promoted != 0)
        {
            // promotion penalty (we 'spent' a pawn and gained a promotion piece)
            // mv.Promoted encodes a white piece constant; map it to piece index
            int promoIndex = PieceToIndex((Piece)mv.Promoted);
            balance -= PieceValues[promoIndex];
        }

        if (balance >= 0) return true;

        // simulate occupancy after the capture
        ulong occ = allOccupancy;
        occ &= ~(1UL << mv.From);
        occ |= (1UL << mv.To);
        if (mv.IsEnPassant)
        {
            // captured pawn sits behind target
            occ &= ~(1UL << (mv.To ^ 8));
        }

        // attackers to the target square
        ulong attackers = AttackersTo(mv.To, occ) & occ;
        int stm = 1 - SideToMove; // side to move after our capture

        ulong diagonal = (pieces[0][2] | pieces[0][4] | pieces[1][2] | pieces[1][4]);
        ulong orthogonal = (pieces[0][3] | pieces[0][4] | pieces[1][3] | pieces[1][4]);

        int wKing = Bitboard.LsbIndex(pieces[0][5]);
        int bKing = Bitboard.LsbIndex(pieces[1][5]);
        ulong whitePins = pinned[0] & ~AttackDetection.BetweenTable[wKing, mv.To];
        ulong blackPins = pinned[1] & ~AttackDetection.BetweenTable[bKing, mv.To];
        ulong allowed = ~(whitePins | blackPins);

        while (true)
        {
            ulong ourAttackers = attackers & allowed & occupancy[stm];
            if (ourAttackers == 0UL) break;

            int attackerType = LeastValuableAttacker(ourAttackers);

            // king capture rules: if king would capture and the other side still has attackers, break
            if (attackerType == 5 && (attackers & occupancy[1 - stm]) != 0UL)
                break;

            // remove our attacker from occupancy
            // find square of attacker of that type
            ulong candidates = pieces[stm][ attackerType] & ourAttackers;
            int attSq = Bitboard.LsbIndex(candidates);
            occ &= ~(1UL << attSq);

            // switch side
            stm = 1 - stm;

            // assume our piece gets captured in reply
            balance = -balance - 1 - PieceValues[attackerType];
            if (balance >= 0) break;

            // revealing attacks from sliding pieces
            if (attackerType == 0 || attackerType == 2 || attackerType == 4)
            {
                // pawn/bishop/queen can reveal diagonals
                attackers |= AttackDetection.GetBishopAttacks(mv.To, occ) & diagonal;
            }
            if (attackerType == 3 || attackerType == 4)
            {
                // rook/queen reveal orthogonal
                attackers |= AttackDetection.GetRookAttacks(mv.To, occ) & orthogonal;
            }
            attackers &= occ;
        }

        // last side to move has failed to capture back -> losing
        return stm != SideToMove;
    }

    public bool SeeGe(Move mv, int threshold) => See(mv, threshold);

    // Exact SEE Value for Move Ordering
    public int SeeValue(Move mv)
    {
        // Castling always passes
        if (mv.IsCastle) return 0;
            
        // Per-move score stack is a stack buffer: SeeValue runs once per scored
        // capture, and a 32-int heap array there was pure GC pressure in the
        // hottest ordering path. 32 entries are the maximum SEE sequence length.
        Span<int> scores = stackalloc int[32];
        int scoreIdx = 0;
        
        int val = MoveValue(mv); 
        
        var fromPiece = board[mv.From];
        int attackerValue = PieceValues[PieceToIndex(fromPiece)];
        
        if (mv.Promoted != 0)
        {
            val += PieceValues[PieceToIndex((Piece)mv.Promoted)] - PieceValues[PieceToIndex(Piece.WP)];
            attackerValue = PieceValues[PieceToIndex((Piece)mv.Promoted)];
        }
        
        scores[scoreIdx++] = val;

        ulong occ = allOccupancy;
        occ &= ~(1UL << mv.From);
        occ |= (1UL << mv.To);
        if (mv.IsEnPassant)
        {
            occ &= ~(1UL << (mv.To ^ 8));
            scores[0] = PieceValues[PieceToIndex(Piece.BP)]; 
        }

        ulong attackers = AttackersTo(mv.To, occ) & occ;
        int stm = 1 - SideToMove;
        
        ulong diagonal = (pieces[0][2] | pieces[0][4] | pieces[1][2] | pieces[1][4]);
        ulong orthogonal = (pieces[0][3] | pieces[0][4] | pieces[1][3] | pieces[1][4]);

        // CRITICAL: Handle Pins
        int wKing = Bitboard.LsbIndex(pieces[0][5]);
        int bKing = Bitboard.LsbIndex(pieces[1][5]);
        ulong whitePins = pinned[0] & ~AttackDetection.BetweenTable[wKing, mv.To];
        ulong blackPins = pinned[1] & ~AttackDetection.BetweenTable[bKing, mv.To];
        ulong allowed = ~(whitePins | blackPins);

        while (true)
        {
            ulong ourAttackers = attackers & allowed & occupancy[stm];
            if (ourAttackers == 0UL) break;

            int attackerType = LeastValuableAttacker(ourAttackers);

            if (attackerType == 5 && (attackers & occupancy[1 - stm]) != 0UL)
                break;

            ulong candidates = pieces[stm][ attackerType] & ourAttackers;
            int attSq = Bitboard.LsbIndex(candidates);
            occ &= ~(1UL << attSq);

            scores[scoreIdx] = attackerValue - scores[scoreIdx - 1];
            scoreIdx++;
            
            attackerValue = PieceValues[attackerType];
            stm = 1 - stm;

            if (attackerType == 0 || attackerType == 2 || attackerType == 4)
                attackers |= AttackDetection.GetBishopAttacks(mv.To, occ) & diagonal;
            if (attackerType == 3 || attackerType == 4)
                attackers |= AttackDetection.GetRookAttacks(mv.To, occ) & orthogonal;
            
            attackers &= occ;
        }
        
        while (scoreIdx > 1) {
            scoreIdx--;
            scores[scoreIdx-1] = -Math.Max(-scores[scoreIdx-1], scores[scoreIdx]);
        }
        return scores[0];
    }

    // Fast approximate SEE (cheap heuristic for ordering)
    public int SeeApproximate(Move mv)
    {
        int mvVal = MoveValue(mv);
        var fromP = board[mv.From];
        int fromVal = PieceValues[PieceToIndex(fromP)];
        return mvVal - fromVal;
    }

    // Compute bitboard of attackers to a square given a hypothetical occupancy
    private ulong AttackersTo(int sq, ulong occ)
    {
        ulong attackers = 0UL;
        // White pawns attack if GetPawnAttacks(sq, 1) & pieces[0][0]
        attackers |= AttackDetection.GetPawnAttacks(sq, 1) & pieces[0][0];
        attackers |= AttackDetection.GetPawnAttacks(sq, 0) & pieces[1][0];
        attackers |= AttackDetection.GetKnightAttacks(sq) & (pieces[0][1] | pieces[1][1]);
        attackers |= AttackDetection.GetKingAttacks(sq) & (pieces[0][5] | pieces[1][5]);
        attackers |= AttackDetection.GetBishopAttacks(sq, occ) & (pieces[0][2] | pieces[0][4] | pieces[1][2] | pieces[1][4]);
        attackers |= AttackDetection.GetRookAttacks(sq, occ) & (pieces[0][3] | pieces[0][4] | pieces[1][3] | pieces[1][4]);
        return attackers;
    }

    // Return least valuable attacker type index from attackers bitboard
    private int LeastValuableAttacker(ulong attackers)
    {
        // pawn (0)
        if (((pieces[0][0] | pieces[1][0]) & attackers) != 0UL) return 0;
        // knight (1)
        if (((pieces[0][1] | pieces[1][1]) & attackers) != 0UL) return 1;
        // bishop (2)
        if (((pieces[0][2] | pieces[1][2]) & attackers) != 0UL) return 2;
        // rook (3)
        if (((pieces[0][3] | pieces[1][3]) & attackers) != 0UL) return 3;
        // queen (4)
        if (((pieces[0][4] | pieces[1][4]) & attackers) != 0UL) return 4;
        // king (5)
        return 5;
    }

    private int MoveValue(Move mv)
    {
        if (mv.IsEnPassant) return PieceValues[0];
        var capture = board[mv.To];
        int captureIdx = PieceToIndex(capture);
        if (mv.Promoted != 0)
        {
            int promoIdx = PieceToIndex((Piece)mv.Promoted);
            return PieceValues[captureIdx] + PieceValues[promoIdx] - PieceValues[0];
        }
        return PieceValues[captureIdx];
    }

    // accessors for tests
}
