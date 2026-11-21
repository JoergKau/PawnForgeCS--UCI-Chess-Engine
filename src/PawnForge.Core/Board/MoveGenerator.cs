using System;
using System.Collections.Generic;

namespace PawnForge.Core.Board
{
    /// <summary>
    /// Pseudo-legal and legal move generation over the bitboards.
    ///
    /// ENTRY POINTS:
    /// GenerateMoves - fully legal: pseudo-legal generation plus
    /// a make/unmake legality filter (used by the
    /// UCI layer, perft and tests)
    /// GeneratePseudoLegalMoves - raw generation with pin-respect: pinned
    /// pieces may only move ALONG the pin ray
    /// (AlongPin filter). Has an optional
    /// CHECK-AWARE mode (useCheckMasks) that
    /// additionally intersects every generator's
    /// targets against a capture/push mask
    /// derived from pos.Checkers, and filters
    /// king moves against a king-danger mask -
    /// see "CHECK-AWARE GENERATION" below.
    /// GenerateLegalMoves - check-aware pseudo-legal generation, i.e.
    /// GeneratePseudoLegalMoves(useCheckMasks:
    /// true) under a clearer name for callers
    /// that want the (much closer to fully
    /// legal) mode without the make/unmake pass.
    /// Still relies on the caller/search to
    /// validate en passant discovered checks
    /// and non-king pin cases via the existing
    /// post-make legality check, exactly as
    /// the reference's own reference implementation
    /// does for en passant (see the design
    /// notes doc referenced below).
    ///
    /// CHECK-AWARE GENERATION (2026-09-06, see
    ///):
    /// the reference's and the design reference's move generators compute, once per node
    /// (BEFORE any move is made): which enemy piece(s) currently check the
    /// king (pos.Checkers, already maintained by Position.UpdateKingThreats
    /// after every make/unmake - no new computation needed for this part),
    /// a captureMask (squares that capture the sole checker) and pushMask
    /// (squares between king and a sliding checker, for blocking), and a
    /// kingDanger bitboard (every square the opponent attacks, computed with
    /// the king REMOVED from occupancy so a slider's ray is not falsely
    /// blocked by the king's own current square). Every piece generator then
    /// intersects its raw attack bitboard against (captureMask | pushMask)
    /// before emitting a move, and king moves are intersected against
    /// ~kingDanger instead of accepting every geometrically reachable
    /// square. This means far fewer candidate moves are ever constructed in
    /// check positions, and ordinary king steps into an attacked square are
    /// never generated at all - both engines rely on this to avoid the
    /// per-move make/unmake legality trial PawnForge's non-check-aware path
    /// still needs. Pin handling is UNCHANGED here: PawnForge's existing
    /// per-move AlongPin check (fed by pos.Pinned, also already
    /// automatically refreshed) is functionally equivalent to the reference's
    /// cached pinRayMasks[64] table - both restrict a pinned piece to moves
    /// along its own pin ray - so it is reused as-is rather than replaced.
    ///
    /// COVERAGE: all piece types, promotions (Q/R/B/N on push and capture),
    /// en passant, castling with empty/attack-checked path (standard chess
    /// geometry only).
    ///
    /// MAGICS: sliding-piece attacks come from MagicBitboards (plain
    /// multiply-shift magics with per-square precomputed tables).
    ///
    /// NOTE: GeneratePawnMoves allocates a small int[] per pawn (hot-path
    /// allocation - a known optimization candidate).
    /// </summary>
    public static class MoveGenerator
    {
        /// <summary>
        /// Generates all FULLY LEGAL moves: pseudo-legal generation followed
        /// by a make/unmake legality filter (a move is kept only if the own
        /// king is not attacked afterwards). Returns the move count.
        /// </summary>
        public static int GenerateMoves(Position pos, Span<Move> buffer)
        {
            // 1. Generate Pseudo-legal in place
            int count = GeneratePseudoLegalMoves(pos, buffer);

            // 2. Filter Legal
            int legalCount = 0;
            int side = pos.SideToMove;

            for (int i = 0; i < count; i++)
            {
                Move m = buffer[i];
                var captured = pos.Mailbox[m.To];

                pos.MakeMoveInternal(m);
                bool legalMove = !pos.IsKingAttacked(side);
                pos.UnmakeMoveInternal(m, captured);

                if (legalMove)
                {
                    if (legalCount != i)
                        buffer[legalCount] = m;
                    legalCount++;
                }
            }
            return legalCount;
        }

        /// <summary>
        /// Generates PSEUDO-legal moves for the side to move (may include
        /// moves that leave the own king in check - the caller validates).
        /// Pinned pieces are restricted to moves along the pin ray unless
        /// <paramref name="respectPin"/> is false.
        /// </summary>
        public static int GeneratePseudoLegalMoves(Position pos, Span<Move> moves, bool respectPin = true)
        {
            return GeneratePseudoLegalMoves(pos, moves, pos.SideToMove, respectPin);
        }

        /// <summary>Pseudo-legal generation for an explicit side (0 = white, 1 = black).</summary>
        public static int GeneratePseudoLegalMoves(Position pos, Span<Move> moves, int side, bool respectPin = true)
        {
            return GeneratePseudoLegalMoves(pos, moves, side, respectPin, useCheckMasks: false);
        }

        /// <summary>
        /// Check-aware pseudo-legal generation: additionally intersects every generator's
        /// candidate squares against a capture/push mask derived from the current checkers,
        /// and filters king moves against a king-danger mask. See "CHECK-AWARE GENERATION"
        /// in the class remarks above. When not in check, this is IDENTICAL to the plain
        /// pseudo-legal path except for king moves, which always get the king-danger filter.
        /// Pin handling (AlongPin) is unchanged and still applies on top of these masks.
        /// </summary>
        public static int GenerateLegalMoves(Position pos, Span<Move> moves, bool respectPin = true, bool capturesOnly = false)
        {
            return GeneratePseudoLegalMoves(pos, moves, pos.SideToMove, respectPin, useCheckMasks: true, capturesOnly);
        }

        /// <summary>Pseudo-legal generation for an explicit side, with optional check-aware masking.</summary>
        public static int GeneratePseudoLegalMoves(Position pos, Span<Move> moves, int side, bool respectPin, bool useCheckMasks)
        {
            return GeneratePseudoLegalMoves(pos, moves, side, respectPin, useCheckMasks, capturesOnly: false);
        }

        /// <summary>
        /// CAPTURES-ONLY generation. When
        /// <paramref name="capturesOnly"/> is true the generators never construct a quiet move:
        /// pawn pushes/double pushes (including quiet promotions) are skipped, and the slider /
        /// knight target sets are intersected with the enemy occupancy. Castling and quiet king
        /// steps are skipped too. The emitted set is EXACTLY the set the full generator produces
        /// that <c>MovePicker</c> would keep in its <c>onlyCaptures</c> mode (captures, en
        /// passant, capture-promotions) — so a toggle-gated search is bit-identical.
        /// The check masks still apply (a capturing evasion/block is kept; captures-only is
        /// only used at non-check nodes by the callers).
        /// </summary>
        public static int GeneratePseudoLegalMoves(Position pos, Span<Move> moves, int side, bool respectPin, bool useCheckMasks, bool capturesOnly)
        {
            int moveCount = 0;
            ulong bb;
            ulong[][] pieces = pos.Pieces;

            // ─────────────────────────────────────────────────────────────────────────────
            // CHECK-AWARE MASKS (only computed when requested - see class remarks above).
            // captureMask/pushMask default to "all squares" (no restriction) when not in
            // check, or when useCheckMasks is false; kingDanger defaults to 0 (no square
            // excluded) when useCheckMasks is false, so the AND-with-mask code below is a
            // no-op and behaves exactly like the original unconditional generation.
            // ─────────────────────────────────────────────────────────────────────────────
            ulong captureMask = ulong.MaxValue;
            ulong pushMask = ulong.MaxValue;
            ulong kingDanger = 0UL;
            int checkerCount = 0;

            if (useCheckMasks)
            {
                ulong checkersBb = pos.Checkers[side];
                checkerCount = Bitboard.PopCount(checkersBb);
                int kingSqForMasks = Bitboard.LsbIndex(pieces[side][5]);

                if (checkerCount == 1)
                {
                    int checkerSq = Bitboard.LsbIndex(checkersBb);
                    captureMask = checkersBb; // must capture the sole checker...
                    // ...or, if it's a slider, block a square between it and the king
                    Piece checkerPiece = GetPieceAt(pos, checkerSq);
                    bool checkerIsSlider = checkerPiece == Piece.WB || checkerPiece == Piece.BB
                        || checkerPiece == Piece.WR || checkerPiece == Piece.BR
                        || checkerPiece == Piece.WQ || checkerPiece == Piece.BQ;
                    pushMask = checkerIsSlider ? AttackDetection.BetweenTable[kingSqForMasks, checkerSq] : 0UL;
                }
                else if (checkerCount >= 2)
                {
                    // Double (or more) check: no capture or push resolves it - only king moves are legal.
                    captureMask = 0UL;
                    pushMask = 0UL;
                }

                kingDanger = ComputeKingDanger(pos, side);
            }

            // iterate pawns
            bb = pieces[side][0];
            while (bb != 0UL)
            {
                int sq = Bitboard.LsbIndex(bb);
                if (checkerCount < 2)
                    GeneratePawnMoves(pos, sq, side, moves, ref moveCount, respectPin, captureMask, pushMask, capturesOnly);
                bb = Bitboard.ClearLsb(bb);
            }
            // knights
            bb = pieces[side][1];
            while (bb != 0UL)
            {
                int sq = Bitboard.LsbIndex(bb);
                if (checkerCount < 2)
                    GenerateKnightMoves(pos, sq, side, moves, ref moveCount, respectPin, captureMask, pushMask, capturesOnly);
                bb = Bitboard.ClearLsb(bb);
            }
            // bishops
            bb = pieces[side][2];
            while (bb != 0UL)
            {
                int sq = Bitboard.LsbIndex(bb);
                if (checkerCount < 2)
                    GenerateBishopMoves(pos, sq, side, moves, ref moveCount, respectPin, captureMask, pushMask, capturesOnly);
                bb = Bitboard.ClearLsb(bb);
            }
            // rooks
            bb = pieces[side][3];
            while (bb != 0UL)
            {
                int sq = Bitboard.LsbIndex(bb);
                if (checkerCount < 2)
                    GenerateRookMoves(pos, sq, side, moves, ref moveCount, respectPin, captureMask, pushMask, capturesOnly);
                bb = Bitboard.ClearLsb(bb);
            }
            // queens
            bb = pieces[side][4];
            while (bb != 0UL)
            {
                int sq = Bitboard.LsbIndex(bb);
                if (checkerCount < 2)
                    GenerateQueenMoves(pos, sq, side, moves, ref moveCount, respectPin, captureMask, pushMask, capturesOnly);
                bb = Bitboard.ClearLsb(bb);
            }
            // king (always generated - the only legal piece in double check)
            bb = pieces[side][5];
            while (bb != 0UL)
            {
                int sq = Bitboard.LsbIndex(bb);
                GenerateKingMoves(pos, sq, side, moves, ref moveCount, respectPin, useCheckMasks, kingDanger, checkerCount, capturesOnly);
                bb = Bitboard.ClearLsb(bb);
            }
            return moveCount;
        }

        /// <summary>
        /// Cheap test for "the side to move has exactly one pseudo-legal move".
        ///
        /// <para>WHY: the single-response search extension only needs the ANSWER, not the
        /// move list. Building the full list at every eligible node costs more than the
        /// extension saves when the answer is "no" (the normal case). This method counts
        /// knight, slider and king targets directly from their attack sets and returns
        /// <c>false</c> as soon as a second move is seen; only when fewer than two such
        /// moves exist does it build the complete pseudo-legal list, which is rare.</para>
        ///
        /// <para>EXACTNESS: the fast count is a LOWER BOUND on
        /// <see cref="GeneratePseudoLegalMoves(Position, Span{Move}, bool)"/> with pins
        /// respected. A pinned knight is skipped (its attack pattern never lies on the pin
        /// ray); a pinned slider is counted only on its pin ray through
        /// <see cref="AlongPin"/>; and the king count matches the plain (non-check-aware)
        /// generator used by the search at this site. Pawns and castling are not counted
        /// fast — they are left to the complete list, which is generated only when the
        /// fast count is 0 or 1. The answer is therefore identical to
        /// <c>GeneratePseudoLegalMoves(...) == 1</c>, and the unit tests compare the two
        /// directly over walks and hand-made one-move positions.</para>
        /// </summary>
        public static bool HasExactlyOnePseudoLegalMove(Position pos)
        {
            int side = pos.SideToMove;
            ulong own = pos.Occupancy[side];
            ulong pinned = pos.Pinned[side];
            ulong[][] pieces = pos.Pieces;
            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            int count = 0;

            // Knights. A pinned knight has no move: a knight hop never runs along a
            // straight pin ray, so the whole piece is skipped instead of testing each hop.
            ulong bb = pieces[side][1];
            while (bb != 0UL)
            {
                int from = Bitboard.LsbIndex(bb);
                bb = Bitboard.ClearLsb(bb);
                if (Bitboard.IsSet(pinned, from))
                    continue;
                count += Bitboard.PopCount(AttackDetection.GetKnightAttacks(from) & ~own);
                if (count >= 2)
                    return false;
            }

            // Sliders. The non-pinned case is a plain popcount of the attack set.
            // A pinned slider may only move along its pin ray, which is not a simple
            // bitboard mask here; the rare pinned case filters target by target with the
            // same geometric test the full generator uses.
            ulong diagOccupancy = pos.AllOccupancy;
            for (int type = 2; type <= 4; type++)
            {
                bb = pieces[side][type];
                while (bb != 0UL)
                {
                    int from = Bitboard.LsbIndex(bb);
                    bb = Bitboard.ClearLsb(bb);
                    ulong targets = type switch
                    {
                        2 => AttackDetection.GetBishopAttacks(from, diagOccupancy),
                        3 => AttackDetection.GetRookAttacks(from, diagOccupancy),
                        _ => AttackDetection.GetQueenAttacks(from, diagOccupancy),
                    };
                    targets &= ~own;
                    if (Bitboard.IsSet(pinned, from))
                        targets = FilterAlongPin(kingSq, from, targets);
                    count += Bitboard.PopCount(targets);
                    if (count >= 2)
                        return false;
                }
            }

            // King (never pinned). The plain generator does not apply the check-aware
            // king-danger mask, so every geometrically reachable non-own square counts.
            count += Bitboard.PopCount(AttackDetection.GetKingAttacks(kingSq) & ~own);
            if (count >= 2)
                return false;

            // Fewer than two piece moves: pawns and castling decide the answer. Generate
            // the complete list (the same call the extension used before this fast path)
            // and let its count be the truth. This branch is rare by construction.
            Span<Move> buffer = stackalloc Move[256];
            return GeneratePseudoLegalMoves(pos, buffer) == 1;
        }

        /// <summary>Keeps the targets that lie on the pin ray through
        /// <paramref name="king"/> and <paramref name="from"/>. Only used by
        /// <see cref="HasExactlyOnePseudoLegalMove"/> for the rare pinned slider.</summary>
        private static ulong FilterAlongPin(int king, int from, ulong targets)
        {
            ulong allowed = 0UL;
            while (targets != 0UL)
            {
                int to = Bitboard.LsbIndex(targets);
                targets = Bitboard.ClearLsb(targets);
                if (AlongPin(king, from, to))
                    allowed |= 1UL << to;
            }
            return allowed;
        }

        /// <summary>
        /// Whole-board "which squares does the opponent attack" bitboard, used to filter
        /// king moves at generation time (CHECK-AWARE GENERATION, see class remarks).
        /// CRITICAL: the king's own square is temporarily removed from occupancy before
        /// computing slider attacks, so a rook/bishop/queen's ray through the king's
        /// current square is not falsely blocked by the king itself - matching the reference's
        /// removeKing/addKing and the design reference's "Bitboard blockers = All ^ kingMask" technique
        /// (see and the
        /// the design reference refinement note in the same section). Pawn/knight/king attacks do not
        /// depend on occupancy at all, so only the slider (bishop/rook/queen) computation
        /// needs the king-removed occupancy; the non-slider terms use the real occupancy.
        /// </summary>
        private static ulong ComputeKingDanger(Position pos, int side)
        {
            int opp = 1 - side;
            ulong[][] pieces = pos.Pieces;
            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            ulong occWithoutOurKing = kingSq >= 0 ? pos.AllOccupancy & ~(1UL << kingSq) : pos.AllOccupancy;

            ulong danger = 0UL;

            // Pawns: GetPawnAttacks(sq, color) returns the squares attacked BY a pawn of
            // `color` sitting AT `sq` (it is literally PawnAttacks[color, sq] - an attacks-
            // FROM table, not the attacks-TO/defender-symmetry table used elsewhere in this
            // file for single-square queries). Since `sq` here is an OPPONENT pawn's square,
            // the second argument must be `opp` (the opponent's own color), NOT `side` (ours)
            // - passing `side` computed "where would MY pawn attack from the OPPONENT's pawn
            // square", which is nonsensical and was caught by CheckAwareGenerationTests
            // (missing legal pawn double-push moves in positions with an opponent pawn
            // nearby, because the wrong danger squares leaked into unrelated calculations
            // downstream... actually caught directly as wrong kingDanger reducing legal king
            // moves incorrectly - see the differential test failures this fixed).
            ulong oppPawns = pieces[opp][0];
            while (oppPawns != 0UL)
            {
                int sq = Bitboard.LsbIndex(oppPawns);
                danger |= AttackDetection.GetPawnAttacks(sq, opp);
                oppPawns = Bitboard.ClearLsb(oppPawns);
            }

            // Knights
            ulong oppKnights = pieces[opp][1];
            while (oppKnights != 0UL)
            {
                int sq = Bitboard.LsbIndex(oppKnights);
                danger |= AttackDetection.GetKnightAttacks(sq);
                oppKnights = Bitboard.ClearLsb(oppKnights);
            }

            // Bishops + diagonal queen moves (king removed from occupancy - see remarks)
            ulong oppDiag = pieces[opp][2] | pieces[opp][4];
            while (oppDiag != 0UL)
            {
                int sq = Bitboard.LsbIndex(oppDiag);
                danger |= AttackDetection.GetBishopAttacks(sq, occWithoutOurKing);
                oppDiag = Bitboard.ClearLsb(oppDiag);
            }

            // Rooks + orthogonal queen moves (king removed from occupancy - see remarks)
            ulong oppOrtho = pieces[opp][3] | pieces[opp][4];
            while (oppOrtho != 0UL)
            {
                int sq = Bitboard.LsbIndex(oppOrtho);
                danger |= AttackDetection.GetRookAttacks(sq, occWithoutOurKing);
                oppOrtho = Bitboard.ClearLsb(oppOrtho);
            }

            // Opponent king (kings can never actually give "check" to each other under
            // legal play, but adjacent squares to the opponent king are still off-limits -
            // two kings may never stand adjacent).
            ulong oppKingBb = pieces[opp][5];
            if (oppKingBb != 0UL)
            {
                danger |= AttackDetection.GetKingAttacks(Bitboard.LsbIndex(oppKingBb));
            }

            return danger;
        }

        /// <summary>Mailbox piece lookup, used only by the check-mask computation above.</summary>
        private static Piece GetPieceAt(Position pos, int sq) => pos.Mailbox[sq];

        private static void GeneratePawnMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin = true)
        {
            GeneratePawnMoves(pos, sq, side, moves, ref count, respectPin, ulong.MaxValue, ulong.MaxValue, capturesOnly: false);
        }

        /// <summary>
        /// <paramref name="captureMask"/>/<paramref name="pushMask"/> restrict which target
        /// squares are legal when the king is in check (see "CHECK-AWARE GENERATION" in the
        /// class remarks). A pawn CAPTURE (including en passant) must land on a captureMask
        /// square; a pawn PUSH (single or double) must land on a pushMask square. Both masks
        /// default to "all squares" (no restriction) when not in check.
        /// </summary>
        private static void GeneratePawnMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin, ulong captureMask, ulong pushMask, bool capturesOnly)
        {
            bool white = side == 0;
            int dir = white ? 8 : -8;
            int startRank = white ? 1 : 6;
            int promoteRank = white ? 7 : 0;
            ulong allOccupancy = pos.AllOccupancy;
            ulong[] pinned = pos.Pinned;
            ulong[][] pieces = pos.Pieces;
            ulong[] occupancy = pos.Occupancy;

            // Captures-only: the push blocks below (including QUIET
            // promotions) are skipped entirely — they are the quiet moves the picker would
            // discard in onlyCaptures mode. Captures and en passant still follow.
            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            if (!capturesOnly)
            {
                // Forward moves
                int to = sq + dir;
                if (to >= 0 && to < 64 && !Bitboard.IsSet(allOccupancy, to) && Bitboard.IsSet(pushMask, to))
                {
                    // Respect pin for forward move only if requested
                    if (!respectPin || !Bitboard.IsSet(pinned[side], sq) || AlongPin(kingSq, sq, to))
                    {
                        if (to / 8 == promoteRank)
                        {
                            // promotions
                            moves[count++] = new Move(sq, to, (int)(white ? Piece.WQ : Piece.BQ));
                            moves[count++] = new Move(sq, to, (int)(white ? Piece.WR : Piece.BR));
                            moves[count++] = new Move(sq, to, (int)(white ? Piece.WB : Piece.BB));
                            moves[count++] = new Move(sq, to, (int)(white ? Piece.WN : Piece.BN));
                        }
                        else
                        {
                            moves[count++] = new Move(sq, to);
                        }
                    }
                }
                // Double push: checked independently of the single push above (a single push
                // blocked-or-off-pushMask must not prevent evaluating the double push square,
                // though both require the intermediate square to be empty regardless of masks).
                if (to >= 0 && to < 64 && !Bitboard.IsSet(allOccupancy, to) && sq / 8 == startRank)
                {
                    int to2 = to + dir;
                    if (!Bitboard.IsSet(allOccupancy, to2) && Bitboard.IsSet(pushMask, to2))
                    {
                        if (!respectPin || !Bitboard.IsSet(pinned[side], sq) || AlongPin(kingSq, sq, to2))
                            moves[count++] = new Move(sq, to2);
                    }
                }
            }

            // captures - the two diagonal push offsets are constants (only the
            // sign depends on the side). Use stackalloc instead of a heap int[]
            // (this method runs once per pawn per node; the old allocation was
            // a hot-path GC pressure source).
            Span<int> caps = white ? stackalloc int[] { 7, 9 } : stackalloc int[] { -7, -9 };
            foreach (var c in caps)
            {
                int t = sq + c;
                if (t < 0 || t >= 64) continue;
                // avoid wrap-around across files
                int df = Math.Abs((sq % 8) - (t % 8));
                if (df != 1) continue;

                // regular captures
                if (Bitboard.IsSet(occupancy[1 - side], t) && Bitboard.IsSet(captureMask, t)) // opponent piece on target square
                {
                    // respect pin for capture only if requested
                    if (!respectPin || !Bitboard.IsSet(pinned[side], sq) || AlongPin(kingSq, sq, t))
                    {
                        if (t / 8 == promoteRank)
                        {
                            moves[count++] = new Move(sq, t, (int)(white ? Piece.WQ : Piece.BQ));
                            moves[count++] = new Move(sq, t, (int)(white ? Piece.WR : Piece.BR));
                            moves[count++] = new Move(sq, t, (int)(white ? Piece.WB : Piece.BB));
                            moves[count++] = new Move(sq, t, (int)(white ? Piece.WN : Piece.BN));
                        }
                        else
                        {
                            moves[count++] = new Move(sq, t);
                        }
                    }
                }

                // en passant. NOTE: an en passant capture removes a pawn from a square
                // (capturedPawnSq = t ^ 8, one rank behind the target) that is NOT the
                // destination square `t` itself. So it resolves a check either by REMOVING
                // the checker (capturedPawnSq is the checker's own square - check against
                // captureMask, which in single-check holds exactly that square) or by moving
                // our pawn onto a square that BLOCKS a sliding check (t is on pushMask).
                // captureMask is never tested against `t` here (unlike a normal capture)
                // because the piece captured by en passant never sits on the destination
                // square, only ordinary captureMask semantics (checker's own square) apply.
                if (t == pos.EnPassantSquare)
                {
                    int capturedPawnSq = t ^ 8;
                    bool resolvesCheck = Bitboard.IsSet(captureMask, capturedPawnSq) || Bitboard.IsSet(pushMask, t);
                    if (resolvesCheck)
                    {
                        // en-passant can expose king; disallow if pawn is pinned and not along pin (respectPin applies)
                        if (!respectPin || !Bitboard.IsSet(pinned[side], sq) || AlongPin(kingSq, sq, t))
                            moves[count++] = new Move(sq, t, 0, true);
                    }
                }
            }
        }

        private static void GenerateKnightMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin = true)
        {
            GenerateKnightMoves(pos, sq, side, moves, ref count, respectPin, ulong.MaxValue, ulong.MaxValue, capturesOnly: false);
        }

        /// <summary>captureMask/pushMask restrict targets when in check - see class remarks.</summary>
        private static void GenerateKnightMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin, ulong captureMask, ulong pushMask, bool capturesOnly)
        {
            ulong[] occupancy = pos.Occupancy;
            ulong[] pinned = pos.Pinned;
            ulong[][] pieces = pos.Pieces;

            // A pinned knight has ZERO legal moves: a knight's attack pattern never lies
            // along a straight ray, so it can never move "along the pin" - AlongPin(kingSq,
            // sq, t) is false for every possible t. The loop below already achieves this
            // implicitly (every candidate fails the AlongPin test and is skipped), so no
            // separate short-circuit is needed - this note documents why that's safe/correct,
            // matching the reference's explicit `knights & ~pinMask` exclusion of the whole piece.

            // Precomputed knight attacks bitboard
            ulong knightAttacks = AttackDetection.GetKnightAttacks(sq);
            ulong targets = side == 0 ? knightAttacks & ~occupancy[0] : knightAttacks & ~occupancy[1]; // Can't capture own pieces
            targets &= (captureMask | pushMask);
            // captures-only: keep only enemy-occupied targets.
            if (capturesOnly) targets &= occupancy[1 - side];

            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            while (targets != 0UL)
            {
                int t = Bitboard.LsbIndex(targets);
                // if piece is pinned and respectPin is requested, only allow moves along the pin ray
                if (respectPin && Bitboard.IsSet(pinned[side], sq) && !AlongPin(kingSq, sq, t)) { targets = Bitboard.ClearLsb(targets); continue; }
                moves[count++] = new Move(sq, t);
                targets = Bitboard.ClearLsb(targets);
            }
        }

        private static void GenerateBishopMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin = true)
        {
            GenerateBishopMoves(pos, sq, side, moves, ref count, respectPin, ulong.MaxValue, ulong.MaxValue, capturesOnly: false);
        }

        /// <summary>captureMask/pushMask restrict targets when in check - see class remarks.</summary>
        private static void GenerateBishopMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin, ulong captureMask, ulong pushMask, bool capturesOnly)
        {
            ulong[] occupancy = pos.Occupancy;
            ulong[] pinned = pos.Pinned;
            ulong[][] pieces = pos.Pieces;
            ulong allOccupancy = pos.AllOccupancy;

            ulong bishopAttacks = AttackDetection.GetBishopAttacks(sq, allOccupancy);
            ulong targets = side == 0 ? bishopAttacks & ~occupancy[0] : bishopAttacks & ~occupancy[1]; // Can't capture own pieces
            targets &= (captureMask | pushMask);
            // captures-only: keep only enemy-occupied targets.
            if (capturesOnly) targets &= occupancy[1 - side];

            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            while (targets != 0UL)
            {
                int t = Bitboard.LsbIndex(targets);
                if (respectPin && Bitboard.IsSet(pinned[side], sq) && !AlongPin(kingSq, sq, t)) { targets = Bitboard.ClearLsb(targets); continue; }
                moves[count++] = new Move(sq, t);
                targets = Bitboard.ClearLsb(targets);
            }
        }

        private static void GenerateRookMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin = true)
        {
            GenerateRookMoves(pos, sq, side, moves, ref count, respectPin, ulong.MaxValue, ulong.MaxValue, capturesOnly: false);
        }

        /// <summary>captureMask/pushMask restrict targets when in check - see class remarks.</summary>
        private static void GenerateRookMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin, ulong captureMask, ulong pushMask, bool capturesOnly)
        {
            ulong[] occupancy = pos.Occupancy;
            ulong[] pinned = pos.Pinned;
            ulong[][] pieces = pos.Pieces;
            ulong allOccupancy = pos.AllOccupancy;

            ulong rookAttacks = AttackDetection.GetRookAttacks(sq, allOccupancy);
            ulong targets = side == 0 ? rookAttacks & ~occupancy[0] : rookAttacks & ~occupancy[1]; // Can't capture own pieces
            targets &= (captureMask | pushMask);
            // captures-only: keep only enemy-occupied targets.
            if (capturesOnly) targets &= occupancy[1 - side];

            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            while (targets != 0UL)
            {
                int t = Bitboard.LsbIndex(targets);
                if (respectPin && Bitboard.IsSet(pinned[side], sq) && !AlongPin(kingSq, sq, t)) { targets = Bitboard.ClearLsb(targets); continue; }
                moves[count++] = new Move(sq, t);
                targets = Bitboard.ClearLsb(targets);
            }
        }

        private static void GenerateQueenMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin = true)
        {
            GenerateQueenMoves(pos, sq, side, moves, ref count, respectPin, ulong.MaxValue, ulong.MaxValue, capturesOnly: false);
        }

        /// <summary>captureMask/pushMask restrict targets when in check - see class remarks.</summary>
        private static void GenerateQueenMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin, ulong captureMask, ulong pushMask, bool capturesOnly)
        {
            ulong[] occupancy = pos.Occupancy;
            ulong[] pinned = pos.Pinned;
            ulong[][] pieces = pos.Pieces;
            ulong allOccupancy = pos.AllOccupancy;

            ulong queenAttacks = AttackDetection.GetQueenAttacks(sq, allOccupancy);
            ulong targets = side == 0 ? queenAttacks & ~occupancy[0] : queenAttacks & ~occupancy[1]; // Can't capture own pieces
            targets &= (captureMask | pushMask);
            // captures-only: keep only enemy-occupied targets.
            if (capturesOnly) targets &= occupancy[1 - side];

            int kingSq = Bitboard.LsbIndex(pieces[side][5]);
            while (targets != 0UL)
            {
                int t = Bitboard.LsbIndex(targets);
                if (respectPin && Bitboard.IsSet(pinned[side], sq) && !AlongPin(kingSq, sq, t)) { targets = Bitboard.ClearLsb(targets); continue; }
                moves[count++] = new Move(sq, t);
                targets = Bitboard.ClearLsb(targets);
            }
        }

        private static void GenerateKingMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin = true)
        {
            GenerateKingMoves(pos, sq, side, moves, ref count, respectPin, useCheckMasks: false, kingDanger: 0UL, checkerCount: 0, capturesOnly: false);
        }

        /// <summary>
        /// When <paramref name="useCheckMasks"/> is true, ordinary (non-castling) king moves
        /// are filtered against <paramref name="kingDanger"/> (every square the opponent
        /// attacks, computed with the king removed from occupancy - see
        /// <see cref="ComputeKingDanger"/> and the class remarks). This closes the gap noted
        /// in the design-difference document: previously every geometrically reachable
        /// square was generated regardless of whether the opponent attacked it, relying
        /// entirely on the post-move IsKingAttacked check downstream to discover illegal
        /// king moves. Castling retains its own existing IsSquareAttackedBy checks
        /// unconditionally (both modes), since those already correctly validate the king's
        /// full travel path.
        /// </summary>
        private static void GenerateKingMoves(Position pos, int sq, int side, Span<Move> moves, ref int count, bool respectPin, bool useCheckMasks, ulong kingDanger, int checkerCount, bool capturesOnly)
        {
            ulong[] occupancy = pos.Occupancy;
            ulong allOccupancy = pos.AllOccupancy;

            ulong kingAttacks = AttackDetection.GetKingAttacks(sq);
            ulong targets = side == 0 ? kingAttacks & ~occupancy[0] : kingAttacks & ~occupancy[1];
            if (useCheckMasks)
            {
                targets &= ~kingDanger;
            }
            // captures-only: keep only enemy-occupied king targets.
            if (capturesOnly) targets &= occupancy[1 - side];

            while (targets != 0UL)
            {
                int t = Bitboard.LsbIndex(targets);
                moves[count++] = new Move(sq, t);
                targets = Bitboard.ClearLsb(targets);
            }

            // castling with basic square-attack checks. NOTE: `IsSquareAttackedBy(sq, 1-side)`
            // below already tests "is the king's OWN current square attacked", which is
            // exactly "is the king in check right now" - this pre-existing check already
            // correctly forbids castling out of check, so no separate checkerCount guard is
            // needed here even in check-aware mode.
            // Castling is a QUIET move: skipped in captures-only mode.
            if (!capturesOnly && !pos.IsSquareAttackedBy(sq, 1 - side))
            {
                int castlingRights = pos.CastlingRights;
                // Standard chess castling
                if (side == 0)
                {
                    if ((castlingRights & 1) != 0 &&
                        !Bitboard.IsSet(allOccupancy, 5) && !Bitboard.IsSet(allOccupancy, 6) &&
                        !pos.IsSquareAttackedBy(4, 1) && !pos.IsSquareAttackedBy(5, 1) && !pos.IsSquareAttackedBy(6, 1))
                    {
                        moves[count++] = new Move(sq, sq + 2, 0, false, true);
                    }
                    if ((castlingRights & 2) != 0 &&
                        !Bitboard.IsSet(allOccupancy, 1) && !Bitboard.IsSet(allOccupancy, 2) && !Bitboard.IsSet(allOccupancy, 3) &&
                        !pos.IsSquareAttackedBy(4, 1) && !pos.IsSquareAttackedBy(3, 1) && !pos.IsSquareAttackedBy(2, 1))
                    {
                        moves[count++] = new Move(sq, sq - 2, 0, false, true);
                    }
                }
                else
                {
                    if ((castlingRights & 4) != 0 &&
                        !Bitboard.IsSet(allOccupancy, 61) && !Bitboard.IsSet(allOccupancy, 62) &&
                        !pos.IsSquareAttackedBy(60, 0) && !pos.IsSquareAttackedBy(61, 0) && !pos.IsSquareAttackedBy(62, 0))
                    {
                        moves[count++] = new Move(sq, sq + 2, 0, false, true);
                    }
                    if ((castlingRights & 8) != 0 &&
                        !Bitboard.IsSet(allOccupancy, 59) && !Bitboard.IsSet(allOccupancy, 58) && !Bitboard.IsSet(allOccupancy, 57) &&
                        !pos.IsSquareAttackedBy(60, 0) && !pos.IsSquareAttackedBy(59, 0) && !pos.IsSquareAttackedBy(58, 0))
                    {
                        moves[count++] = new Move(sq, sq - 2, 0, false, true);
                    }
                }
            }
        }

        private static bool AlongPin(int king, int from, int to)
        {
            // Verify if from and to are on the same ray through king
            // Uses geometry check
            
            int kx = king % 8, ky = king / 8;
            int fx = from % 8, fy = from / 8;
            int tx = to % 8, ty = to / 8;
            
            // Check delta King-From
            int dx = fx - kx;
            int dy = fy - ky;
            
            // Check delta King-To
            int tx_d = tx - kx;
            int ty_d = ty - ky;
            
            // Normalize direction King-From
            int gcd = Gcd(Math.Abs(dx), Math.Abs(dy));
            if (gcd == 0) return false; // Should not happen for distinct squares
            dx /= gcd; dy /= gcd;
            
            // Normalize direction King-To
            int gcd2 = Gcd(Math.Abs(tx_d), Math.Abs(ty_d));
            if (gcd2 == 0) return false; // fast fail
            int dx2 = tx_d / gcd2; int dy2 = ty_d / gcd2;
            
            // Must match direction
            return dx == dx2 && dy == dy2;
        }
        
        private static int Gcd(int a, int b)
        {
            while (b != 0) { int t = b; b = a % b; a = t; }
            return a;
        }
    }
}
