using System.Numerics;
using System.Runtime.CompilerServices;

namespace PawnForge.Core.Board;

/// <summary>
/// <c>GivesCheck</c> — a MAKE-FREE predicate that answers "does this (pseudo-legal) move
/// give check to the opponent?" WITHOUT playing the move on the board.
///
/// WHY THIS EXISTS
/// ---------------
/// Quiescence quiet-check generation (following forcing checking sequences past the horizon)
/// is a real selective-depth lever, but the previous attempt (UseQuiescenceChecks) was
/// net-negative because it MADE every quiet move just to test, via IsKingAttacked, whether it
/// gave check — at every quiescence node. That collapsed NPS ~60%. The fix is to decide
/// "gives check?" cheaply, from attack tables, so only the actual checking quiets are ever made.
///
/// CORRECTNESS MODEL
/// -----------------
/// A move gives check iff, in the resulting position, the side-to-move's king is attacked by
/// the mover. There are exactly two independent ways that can happen:
///
/// 1. DIRECT check — the moved piece, sitting on its destination square (with its post-move
/// type, i.e. the promoted piece for a promotion), attacks the enemy king. Slider attacks
/// are computed against the occupancy AFTER the move (the from-square vacated, the
/// to-square filled), so a piece that slides onto a line to the king is detected.
///
/// 2. DISCOVERED check — vacating the from-square uncovers a DIFFERENT friendly slider
/// (bishop / rook / queen) whose ray to the enemy king passed through from-square. This is
/// detected by: from-square lies on the ray between the enemy king and a friendly slider,
/// AND the destination square does not stay on that same ray (if the mover stays on the
/// ray it still blocks it — no discovery).
///
/// Special moves add wrinkles handled explicitly below:
/// • Promotion — the destination piece is the promoted type, not a pawn.
/// • En passant — TWO pawns leave the board (the mover's from-square AND the captured
/// pawn's square), so discovered checks along either vacated line are possible;
/// we recompute the enemy-king attack against the exact post-move occupancy.
/// • Castling — the KING is not giving check (kings cannot), but the ROOK lands on a new
/// square from which it may check; we test the rook's destination.
///
/// This predicate assumes <paramref name="m"/> is at least pseudo-legal for the side to move
/// (the caller — the move picker — only ever passes such moves). It does NOT validate legality
/// (that the mover's own king is left safe); the caller handles that separately.
/// </summary>
public partial class Position
{
    /// <summary>
    /// Returns true if the pseudo-legal move <paramref name="m"/> gives check to the opponent,
    /// computed WITHOUT making the move. See the class-level doc for the correctness model.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GivesCheck(Move m)
    {
        int us = SideToMove;
        int them = 1 - us;

        // Enemy king square. If (pathologically) there is no enemy king, nothing can check it.
        ulong enemyKingBb = Pieces[them][5];
        if (enemyKingBb == 0) return false;
        int kingSq = BitOperations.TrailingZeroCount(enemyKingBb);

        int from = m.From;
        int to = m.To;

        // The moving piece and its post-move TYPE index (0=P,1=N,2=B,3=R,4=Q,5=K).
        // For a promotion the piece that lands on `to` is the promoted type, not a pawn.
        Piece moverPiece = board[from];
        int moverType = m.PromotionPiece != Piece.Empty
            ? m.PromotionPiece.ToIndex()
            : moverPiece.ToIndex();

        // ── Occupancy AFTER the move ─────────────────────────────────────────────────────────
        // Start from the current full occupancy, clear the from-square, set the to-square.
        // Also clear any captured piece's square (normally == to, so already handled; en passant
        // is the exception — the captured pawn is NOT on `to`).
        ulong occAfter = allOccupancy;
        occAfter &= ~(1UL << from);
        occAfter |= (1UL << to);

        int epCapturedSq = -1;
        if (m.IsEnPassant)
        {
            // The captured pawn sits on the square with the mover's destination FILE and the
            // mover's origin RANK (the pawn that just double-pushed). Remove it from occupancy.
            epCapturedSq = (from / 8) * 8 + (to % 8);
            occAfter &= ~(1UL << epCapturedSq);
        }

        // ── Castling: the ROOK gives check, not the king ────────────────────────────────────
        if (m.IsCastle)
        {
            // Standard chess castling rook destinations (king to g1/c1/g8/c8 -> rook to f1/d1/f8/d8).
            // `to` is the king's destination square; derive the rook's landing square from it.
            int rookTo = to switch
            {
                6  => 5,   // white O-O:  Kg1 -> Rf1
                2  => 3,   // white O-O-O:Kc1 -> Rd1
                62 => 61,  // black O-O:  Kg8 -> Rf8
                58 => 59,  // black O-O-O:Kc8 -> Rd8
                _  => -1
            };
            if (rookTo >= 0)
            {
                // Occupancy after castling: both king and rook have moved. `occAfter` already
                // moved the king (from->to). Also move the rook from its origin to rookTo.
                int rookFrom = rookTo switch
                {
                    5  => 7,   // Rf1 came from h1
                    3  => 0,   // Rd1 came from a1
                    61 => 63,  // Rf8 came from h8
                    59 => 56,  // Rd8 came from a8
                    _  => -1
                };
                ulong occCastle = occAfter;
                if (rookFrom >= 0)
                {
                    occCastle &= ~(1UL << rookFrom);
                    occCastle |= (1UL << rookTo);
                }
                // Rook (orthogonal) attack from its new square against the enemy king.
                if ((AttackDetection.GetRookAttacks(rookTo, occCastle) & enemyKingBb) != 0)
                    return true;
            }
            // A castling king move itself can never check; fall through to discovered check
            // (extremely rare, but the king vacating its origin could in principle uncover a
            // slider — handled by the generic discovered-check test below using occAfter).
        }
        else
        {
            // ── DIRECT check by the moved piece from its destination square ───────────────────
            switch (moverType)
            {
                case 0: // Pawn: does a pawn of OUR color on `to` attack the enemy king?
                    // GetPawnAttacks(sq, color) returns the squares a pawn of `color` attacks
                    // FROM `sq`. We want the squares OUR pawn on `to` attacks, so pass `us`
                    // (NOT `them` — that was inverted and missed/mis-flagged pawn checks).
                    if ((AttackDetection.GetPawnAttacks(to, us) & enemyKingBb) != 0)
                        return true;
                    break;
                case 1: // Knight
                    if ((AttackDetection.GetKnightAttacks(to) & enemyKingBb) != 0)
                        return true;
                    break;
                case 2: // Bishop
                    if ((AttackDetection.GetBishopAttacks(to, occAfter) & enemyKingBb) != 0)
                        return true;
                    break;
                case 3: // Rook
                    if ((AttackDetection.GetRookAttacks(to, occAfter) & enemyKingBb) != 0)
                        return true;
                    break;
                case 4: // Queen
                    if ((AttackDetection.GetQueenAttacks(to, occAfter) & enemyKingBb) != 0)
                        return true;
                    break;
                case 5: // King — a king move can never give check directly.
                    break;
            }
        }

        // ── DISCOVERED check ─────────────────────────────────────────────────────────────────
        // Vacating `from` (and, for en passant, also `epCapturedSq`) can uncover a friendly
        // slider whose ray to the enemy king ran through the vacated square. We test every
        // friendly bishop/rook/queen: compute its slider attack against the POST-MOVE occupancy;
        // if it now hits the king AND it did not before (i.e. the mover is no longer blocking),
        // it is a discovered check. Using occAfter already reflects the vacated square(s), so a
        // single attack test against occAfter suffices — but we must exclude the case where the
        // moved piece is itself the slider now giving the (direct) check, already handled above.
        //
        // Efficient form: only sliders that are ALIGNED with the king and whose blocking piece
        // was on a vacated square matter. We compute the friendly diagonal and orthogonal slider
        // sets and test their post-move attacks against the king bit.
        ulong diagSliders = Pieces[us][2] | Pieces[us][4]; // bishops + queens
        ulong orthoSliders = Pieces[us][3] | Pieces[us][4]; // rooks + queens

        // Remove the moved piece from the slider sets: if it is itself a slider it was handled by
        // the direct-check test, and after the move it sits on `to` (its direct attack, not a
        // discovery). We only want OTHER sliders here.
        ulong movedBit = 1UL << from;
        diagSliders &= ~movedBit;
        orthoSliders &= ~movedBit;

        // Diagonal discovered checks.
        ulong db = diagSliders;
        while (db != 0)
        {
            int sq = BitOperations.TrailingZeroCount(db);
            db &= db - 1;
            if ((AttackDetection.GetBishopAttacks(sq, occAfter) & enemyKingBb) != 0)
            {
                // It attacks the king now. Confirm it is a DISCOVERY (it did not attack before,
                // i.e. the vacated square lay between this slider and the king). If the mover
                // ended on the same ray (still blocking) this won't fire because occAfter still
                // has `to` occupied on that ray.
                if ((AttackDetection.GetBishopAttacks(sq, allOccupancy) & enemyKingBb) == 0)
                    return true;
            }
        }

        // Orthogonal discovered checks.
        ulong ob = orthoSliders;
        while (ob != 0)
        {
            int sq = BitOperations.TrailingZeroCount(ob);
            ob &= ob - 1;
            if ((AttackDetection.GetRookAttacks(sq, occAfter) & enemyKingBb) != 0)
            {
                if ((AttackDetection.GetRookAttacks(sq, allOccupancy) & enemyKingBb) == 0)
                    return true;
            }
        }

        return false;
    }
}
