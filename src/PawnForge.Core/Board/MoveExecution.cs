namespace PawnForge.Core.Board;

/// <summary>
/// The make/unmake state machine: applies a move to the position and restores
/// it exactly on unmake. Everything the search relies on is updated here in
/// one pass:
///
/// MAKE (MakeMoveInternal):
/// 1. save full state to the history stack (move, captured piece +
/// square, EP square, castling rights, clocks, Zobrist key)
/// 2. XOR out: old EP file, old castling flags, moving piece
/// 3. handle capture (XOR out captured piece, clear bitboard; for EP
/// the mailbox clear happens separately - the square differs)
/// 4. promotions: translate the color-coded piece, place on target
/// 5. castling: move the rook (bitboards + mailbox + hash)
/// 6. XOR in: placed piece, new castling flags
/// 7. update EP square (ALWAYS after double pushes), clocks, side
/// 8. refresh cached pin/checker bitboards
///
/// UNMAKE: pops the history entry and restores everything wholesale
/// (no reverse-XOR) - the exact inverse of the make steps.
///
/// INVARIANT: after Make+Unmake the position is BIT-IDENTICAL to before
/// (verified by perft and the Zobrist consistency tests). Any divergence
/// here poisons movegen, hash and evaluation simultaneously.
/// </summary>
public static class MoveExecution
{
    /// <summary>
    /// Executes a move on the position, updating all state including bitboards,
    /// zobrist key, castling rights, en passant, and clocks.
    /// </summary>
    /// <param name="pos">The position to modify</param>
    /// <param name="m">The move to make</param>
    public static void MakeMove(Position pos, Move m)
    {
        int origSide = pos.SideToMove;
        int capturedSquare = m.IsEnPassant ? (pos.SideToMove == 0 ? m.To - 8 : m.To + 8) : m.To;
        var captured = pos.Mailbox[capturedSquare];

        // Dirty-piece record (migration M4): this make's deltas are
        // recorded below at each board mutation, in mutation order.
        pos.ResetDirtyPieces();

        // Save history (No Alloc)
        pos.SaveHistory(m, captured, capturedSquare);

        // Remove old en-passant from zobrist if present
        if (pos.EnPassantSquare != -1)
        {
            int file = pos.EnPassantSquare % 8;
            pos.XorZobrist(Zobrist.EnPassantFile[file]);
        }
        
        // Remove old castling flags from zobrist
        if ((pos.CastlingRights & 1) != 0) pos.XorZobrist(Zobrist.Castling[0]);
        if ((pos.CastlingRights & 2) != 0) pos.XorZobrist(Zobrist.Castling[1]);
        if ((pos.CastlingRights & 4) != 0) pos.XorZobrist(Zobrist.Castling[2]);
        if ((pos.CastlingRights & 8) != 0) pos.XorZobrist(Zobrist.Castling[3]);

        // moving piece
        var moving = pos.Mailbox[m.From];
        int movingColor = pos.IsWhite(moving) ? 0 : 1;
        int movingIndex = pos.PieceToIndex(moving);
        
        // xor out moving piece from its from-square
        pos.XorZobrist(Zobrist.PieceSquare[(int)moving, m.From]);
        if (moving == Piece.WP || moving == Piece.BP)
            pos.XorPawnKey(Zobrist.PieceSquare[(int)moving, m.From]);
        else
            pos.XorNonPawnKey(movingColor, Zobrist.PieceSquare[(int)moving, m.From]);
        
        // clear from-bitboards
        pos.ClearPiece(movingColor, movingIndex, m.From);
        pos.RecordDirtyPiece(m.From, moving, added: false);  // dirty record (M4)

        // handle capture
        if (captured != Piece.Empty)
        {
            pos.XorZobrist(Zobrist.PieceSquare[(int)captured, capturedSquare]);
            if (captured == Piece.WP || captured == Piece.BP)
                pos.XorPawnKey(Zobrist.PieceSquare[(int)captured, capturedSquare]);
            else
                pos.XorNonPawnKey(pos.IsWhite(captured) ? 0 : 1, Zobrist.PieceSquare[(int)captured, capturedSquare]);
            int capColor = pos.IsWhite(captured) ? 0 : 1;
            int capIndex = pos.PieceToIndex(captured);
            pos.ClearPiece(capColor, capIndex, capturedSquare);
            pos.RecordDirtyPiece(capturedSquare, captured, added: false);  // dirty record (M4)
        }

        // apply move on board
        pos.Mailbox[m.To] = pos.Mailbox[m.From];
        pos.Mailbox[m.From] = Piece.Empty;
        
        // promotion handling
        Piece placed = pos.Mailbox[m.To];
        if (m.Promoted != 0)
        {
            placed = (Piece)m.Promoted;
            if (origSide == 1) // black to move promoted coded as white pieces - translate
            {
                if (placed == Piece.WQ) placed = Piece.BQ;
                if (placed == Piece.WR) placed = Piece.BR;
                if (placed == Piece.WB) placed = Piece.BB;
                if (placed == Piece.WN) placed = Piece.BN;
            }
            pos.Mailbox[m.To] = placed;
        }

        // en passant capture removal
        if (m.IsEnPassant)
        {
            // NOTE: the generic capture block above ALREADY removed the
            // captured pawn from bitboards and Zobrist (for EP the `captured`
            // piece is non-empty and capturedSquare points at the pawn's
            // actual square). Here we only clear the MAILBOX, because for EP
            // capturedSquare != m.To and the mailbox overwrite at m.To did
            // not touch it. XORing/clearing the bitboard again here would
            // cancel the earlier Zobrist XOR and corrupt the incremental key!
            pos.Mailbox[capturedSquare] = Piece.Empty;
        }

        // update bitboards with placed piece
        int placedColor = pos.IsWhite(placed) ? 0 : 1;
        int placedIndex = pos.PieceToIndex(placed);
        pos.SetPiece(placedColor, placedIndex, m.To);
        pos.RecordDirtyPiece(m.To, placed, added: true);  // dirty record (M4) — the FINAL piece (post-promotion)

        // castling rook move updates
        if (m.IsCastle)
        {
            HandleCastlingRookMove(pos, m);
        }

        // Zobrist: add placed piece at destination
        pos.XorZobrist(Zobrist.PieceSquare[(int)placed, m.To]);
        if (placed == Piece.WP || placed == Piece.BP)
            pos.XorPawnKey(Zobrist.PieceSquare[(int)placed, m.To]);
        else
            pos.XorNonPawnKey(placedColor, Zobrist.PieceSquare[(int)placed, m.To]);

        // update castling rights based on moves/captures
        UpdateCastlingRights(pos, moving, m.From, capturedSquare);

        // XOR in new castling flags
        if ((pos.CastlingRights & 1) != 0) pos.XorZobrist(Zobrist.Castling[0]);
        if ((pos.CastlingRights & 2) != 0) pos.XorZobrist(Zobrist.Castling[1]);
        if ((pos.CastlingRights & 4) != 0) pos.XorZobrist(Zobrist.Castling[2]);
        if ((pos.CastlingRights & 8) != 0) pos.XorZobrist(Zobrist.Castling[3]);

        // update en-passant square — UNCONDITIONALLY after every double push
        UpdateEnPassant(pos, moving, m);

        // update clocks
        if (moving == Piece.WP || moving == Piece.BP || captured != Piece.Empty) 
            pos.SetHalfmoveClock(0); 
        else 
            pos.IncrementHalfmoveClock();
            
        if (origSide == 1) pos.IncrementFullmoveNumber();

        // toggle side and xor side key
        pos.ToggleSide();
        pos.XorZobrist(Zobrist.Side);

        // Update pinned/checkers after move
        pos.UpdateKingThreats();
    }

    /// <summary>
    /// Unmakes a move, restoring the position to its previous state.
    /// </summary>
    /// <param name="pos">The position to modify</param>
    /// <param name="m">The move to unmake</param>
    /// <param name="captured">The captured piece (from MakeMove)</param>
    public static void UnmakeMove(Position pos, Move m, Piece captured)
    {
        var h = pos.PopHistory();

        // Dirty-piece record (M4): the unmade make's deltas are no
        // longer current — clear rather than leave a stale record that a
        // consumer could replay against the wrong position.
        pos.ResetDirtyPieces();

        // Swap side back
        pos.ToggleSide();

        // Restore zobrist key & pawn key & non-pawn keys
        pos.RestoreZobrist(h.ZobristKey);
        pos.RestorePawnKey(h.PawnKey);
        pos.RestoreNonPawnKeys(h.NonPawnKeyWhite, h.NonPawnKeyBlack);

        Piece movedPiece = pos.Mailbox[m.To];
        int color = pos.IsWhite(movedPiece) ? 0 : 1;
        int index = pos.PieceToIndex(movedPiece);

        // 1. Remove piece from 'To' square in bitboards
        pos.ClearPiece(color, index, m.To);

        // 2. Put original piece back at 'From' square (un-promote if necessary)
        Piece pieceToRestore = m.Promoted != 0 ? (pos.SideToMove == 0 ? Piece.WP : Piece.BP) : movedPiece;
        pos.Mailbox[m.From] = pieceToRestore;
        pos.Mailbox[m.To] = Piece.Empty;

        int restoreIndex = pos.PieceToIndex(pieceToRestore);
        pos.SetPiece(color, restoreIndex, m.From);

        // 3. Restore captured piece from the history record
        if (h.Captured != Piece.Empty)
        {
            pos.Mailbox[h.CapturedSquare] = h.Captured;
            int capColor = pos.IsWhite(h.Captured) ? 0 : 1;
            int capIndex = pos.PieceToIndex(h.Captured);
            pos.SetPiece(capColor, capIndex, h.CapturedSquare);
        }
        
        pos.RestoreEnPassant(h.EnPassantSquare);
        pos.RestoreCastlingRights(h.CastlingRights);
        pos.RestoreClocks(h.HalfmoveClock, h.FullmoveNumber);
        
        if (m.IsCastle)
        {
            UnmakeCastlingRookMove(pos, m);
        }

        // Recompute pinned/checkers after unmake
        pos.UpdateKingThreats();
    }

    private static void HandleCastlingRookMove(Position pos, Move m)
    {
        // Standard chess castling
        // White: king at e1 (4)
        if (m.From == 4 && m.To == 6) // white king side e1->g1
        {
            pos.Mailbox[7] = Piece.Empty;
            pos.Mailbox[5] = Piece.WR;
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.WR, 7]);
            pos.XorNonPawnKey(0, Zobrist.PieceSquare[(int)Piece.WR, 7]);
            pos.ClearPiece(0, 3, 7); // Clear rook from h1
            pos.RecordDirtyPiece(7, Piece.WR, false); // dirty record (M4)
            pos.SetPiece(0, 3, 5);   // Set rook to f1
            pos.RecordDirtyPiece(5, Piece.WR, true); // dirty record (M4)
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.WR, 5]);
            pos.XorNonPawnKey(0, Zobrist.PieceSquare[(int)Piece.WR, 5]);
        }
        else if (m.From == 4 && m.To == 2) // white queen side e1->c1
        {
            pos.Mailbox[0] = Piece.Empty;
            pos.Mailbox[3] = Piece.WR;
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.WR, 0]);
            pos.XorNonPawnKey(0, Zobrist.PieceSquare[(int)Piece.WR, 0]);
            pos.ClearPiece(0, 3, 0); // Clear rook from a1
            pos.RecordDirtyPiece(0, Piece.WR, false); // dirty record (M4)
            pos.SetPiece(0, 3, 3);   // Set rook to d1
            pos.RecordDirtyPiece(3, Piece.WR, true); // dirty record (M4)
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.WR, 3]);
            pos.XorNonPawnKey(0, Zobrist.PieceSquare[(int)Piece.WR, 3]);
        }
        // Black: king at e8 (60)
        else if (m.From == 60 && m.To == 62) // black king side e8->g8
        {
            pos.Mailbox[63] = Piece.Empty;
            pos.Mailbox[61] = Piece.BR;
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.BR, 63]);
            pos.XorNonPawnKey(1, Zobrist.PieceSquare[(int)Piece.BR, 63]);
            pos.ClearPiece(1, 3, 63); // Clear rook from h8
            pos.RecordDirtyPiece(63, Piece.BR, false); // dirty record (M4)
            pos.SetPiece(1, 3, 61);   // Set rook to f8
            pos.RecordDirtyPiece(61, Piece.BR, true); // dirty record (M4)
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.BR, 61]);
            pos.XorNonPawnKey(1, Zobrist.PieceSquare[(int)Piece.BR, 61]);
        }
        else if (m.From == 60 && m.To == 58) // black queen side e8->c8
        {
            pos.Mailbox[56] = Piece.Empty;
            pos.Mailbox[59] = Piece.BR;
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.BR, 56]);
            pos.XorNonPawnKey(1, Zobrist.PieceSquare[(int)Piece.BR, 56]);
            pos.ClearPiece(1, 3, 56); // Clear rook from a8
            pos.RecordDirtyPiece(56, Piece.BR, false); // dirty record (M4)
            pos.SetPiece(1, 3, 59);   // Set rook to d8
            pos.RecordDirtyPiece(59, Piece.BR, true); // dirty record (M4)
            pos.XorZobrist(Zobrist.PieceSquare[(int)Piece.BR, 59]);
            pos.XorNonPawnKey(1, Zobrist.PieceSquare[(int)Piece.BR, 59]);
        }
    }

    private static void UnmakeCastlingRookMove(Position pos, Move m)
    {
        if (m.From == 4 && m.To == 6) // white king side
        {
            pos.Mailbox[7] = Piece.WR; 
            pos.Mailbox[5] = Piece.Empty;
            pos.ClearPiece(0, 3, 5);
            pos.SetPiece(0, 3, 7);
        }
        else if (m.From == 4 && m.To == 2) // white queen side
        {
            pos.Mailbox[0] = Piece.WR; 
            pos.Mailbox[3] = Piece.Empty;
            pos.ClearPiece(0, 3, 3);
            pos.SetPiece(0, 3, 0);
        }
        else if (m.From == 60 && m.To == 62) // black king side
        {
            pos.Mailbox[63] = Piece.BR; 
            pos.Mailbox[61] = Piece.Empty;
            pos.ClearPiece(1, 3, 61);
            pos.SetPiece(1, 3, 63);
        }
        else if (m.From == 60 && m.To == 58) // black queen side
        {
            pos.Mailbox[56] = Piece.BR; 
            pos.Mailbox[59] = Piece.Empty;
            pos.ClearPiece(1, 3, 59);
            pos.SetPiece(1, 3, 56);
        }
    }

    private static void UpdateCastlingRights(Position pos, Piece moving, int from, int capturedSquare)
    {
        int rights = pos.CastlingRights;
        
        if (moving == Piece.WK) rights &= ~(1 | 2);  // White king moves, lose both rights
        if (moving == Piece.BK) rights &= ~(4 | 8);  // Black king moves, lose both rights
        if (from == 7 || capturedSquare == 7) rights &= ~1;    // h1 rook moves/captured -> lose White kingside
        if (from == 0 || capturedSquare == 0) rights &= ~2;    // a1 rook moves/captured -> lose White queenside
        if (from == 63 || capturedSquare == 63) rights &= ~4;  // h8 rook moves/captured -> lose Black kingside
        if (from == 56 || capturedSquare == 56) rights &= ~8;  // a8 rook moves/captured -> lose Black queenside
        
        pos.SetCastlingRights(rights);
    }

    private static void UpdateEnPassant(Position pos, Piece moving, Move m)
    {
        // The EP square is ALWAYS set after a double pawn push (even without
        // an adjacent enemy pawn) - stricter than the FIDE repetition rules.
        pos.ClearEnPassant();
        
        if (moving == Piece.WP && m.To - m.From == 16)
        {
            int epSquare = m.From + 8;  // square between from and to
            pos.SetEnPassant(epSquare);
            int file = epSquare % 8;
            pos.XorZobrist(Zobrist.EnPassantFile[file]);
        }
        else if (moving == Piece.BP && m.From - m.To == 16)
        {
            int epSquare = m.From - 8;
            pos.SetEnPassant(epSquare);
            int file = epSquare % 8;
            pos.XorZobrist(Zobrist.EnPassantFile[file]);
        }
    }
}
