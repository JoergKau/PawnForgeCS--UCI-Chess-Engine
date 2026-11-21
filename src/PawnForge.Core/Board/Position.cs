using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace PawnForge.Core.Board;

/// <summary>
/// The central board representation: a hybrid of bitboards and mailbox.
///
/// REPRESENTATION
/// pieces[2][6] bitboards per color (0=white) and piece type
/// (0=Pawn .. 5=King) - the authoritative movegen source
/// occupancy[2] per-color union of the piece bitboards
/// allOccupancy union of both colors (slider blocker set)
/// board[64] mailbox mirror for O(1) piece lookup
///
/// INCREMENTAL STATE (updated in MakeMoveInternal, restored on unmake from
/// the pre-allocated history stack - no heap allocation per move):
/// ZobristKey incremental hash (piece-square, castling, EP file,
/// side to move)
/// EnPassantSquare set after EVERY double push (even without an adjacent
/// enemy pawn - stricter than FIDE, affects repetition)
/// CastlingRights 1=WK 2=WQ 4=BK 8=BQ
/// HalfmoveClock reset on pawn moves/captures (50-move rule at >= 100)
/// Pinned/Checkers cached threat bitboards, refreshed per make/unmake
///
/// THREADING: one Position per thread - never shared. Cloned via the copy
/// constructor for the search; the UCI layer keeps its own master copy.
///
/// Declared <c>partial</c> and split across topic files:
/// Position.See.cs (static exchange evaluation) and Position.Draw.cs
/// (fifty-move / repetition / insufficient-material detection).
/// </summary>
public partial class Position
{
    // board [sq] contains Piece
    private Piece[] board = new Piece[64];

    // Bitboards: pieces[color][pieceIndex(0=P][1=N,2=B,3=R,4=Q,5=K)]
    // optimization: jagged array for faster access (long[][] vs long[,])
    public ulong[][] Pieces { get; private set; }
    private ulong[][] pieces => Pieces; // Backwards compatibility for internal code
    private ulong[] occupancy = new ulong[2];
    private ulong allOccupancy = 0;
    public ulong AllOccupancy => allOccupancy;
    public ulong[] Occupancy => occupancy;

    // pinned pieces per color (bitboards)
    public ulong[] Pinned { get; private set; } = new ulong[2]; // Exposed for MoveGenerator
    private ulong[] pinned => Pinned; 
    
    // pieces giving check to king per color (bitboards)
    public ulong[] Checkers { get; private set; } = new ulong[2]; // Exposed for MoveGenerator
    private ulong[] checkers => Checkers;


    public int SideToMove { get; set; } = 0; // 0 white, 1 black
    public int CastlingRights { get; private set; } = 0; // bitmask WK WQ BK BQ
    public int EnPassantSquare { get; private set; } = -1;
    public int HalfmoveClock { get; private set; } = 0;
    public int FullmoveNumber { get; private set; } = 1;

    // ── Dirty-piece record (migration M4, ported from v1.1's) ────────
    // What the most recent make changed, in board-mutation order: ≤ 4 entries
    // (castling). Written by MoveExecution during MakeMove; reset at the
    // start of every make and on every unmake/null move, so it always
    // describes EXACTLY the latest make. Pre-allocated, zero-alloc — the
    // search touches it on every node's make path. Consumers replay the
    // deltas into their incremental structures (the NNUE accumulator)
    // instead of re-deriving them per move type.
    private readonly DirtyPiece[] _dirtyPieces = new DirtyPiece[DirtyPiece.MaxPerMake];

    /// <summary>How many dirty entries the most recent make produced (0 for a null move).</summary>
    public int DirtyPieceCount { get; private set; }

    /// <summary>
    /// The dirty entries of the most recent make, in board-mutation order:
    /// removals and additions interleaved as they happened. Empty for a null
    /// move. Valid until the next make/unmake.
    /// </summary>
    public ReadOnlySpan<DirtyPiece> DirtyPieces => _dirtyPieces.AsSpan(0, DirtyPieceCount);

    /// <summary>Called by MoveExecution at the start of a make (and by unmake/null) — starts an empty record.</summary>
    internal void ResetDirtyPieces() => DirtyPieceCount = 0;

    /// <summary>Called by MoveExecution at each board mutation — appends one delta. Capacity is DirtyPiece.MaxPerMake; a make cannot exceed it.</summary>
    internal void RecordDirtyPiece(int square, Piece piece, bool added)
    {
        _dirtyPieces[DirtyPieceCount++] = new DirtyPiece(square, piece, added);
    }


    // Zobrist key & Pawn key
    /// <summary>
    /// Incremental full Zobrist hash key (pieces, castling rights, EP file, side to move).
    /// </summary>
    public ulong ZobristKey { get; private set; }

    /// <summary>
    /// Incremental pawn structure hash key (XOR of all White and Black pawns on their squares).
    /// Used to index Correction History across identical pawn structures.
    /// </summary>
    public ulong PawnKey { get; private set; }

    /// <summary>
    /// Incremental per-color NON-PAWN material keys (XOR of the White / Black non-pawn pieces
    /// on their squares — the reference's <c>BoardState.nonPawnKeys[0/1]</c> 1:1). Pawns and
    /// castling/EP/side do not touch them. Indexed by Correction History's non-pawn terms,
    /// which stay ACTIVE in pawnless / pawn-sparse endgames where the pawn key is constant
    /// .
    /// </summary>
    public ulong NonPawnKeyWhite { get; private set; }
    public ulong NonPawnKeyBlack { get; private set; }

    /// <summary>Per-color non-pawn key accessor (color 0 = White, 1 = Black).</summary>
    public ulong NonPawnKey(int color) => color == 0 ? NonPawnKeyWhite : NonPawnKeyBlack;

    // Optimization: Use array + ply index instead of Stack (Heap allocation avoidance)
    // State history array architecture
    internal const int MaxHistory = 2048;
    internal History[] historyStack = new History[MaxHistory];
    internal int historyPly = 0;

    internal readonly record struct History(Move Move, Piece Captured, int CapturedSquare, int EnPassantSquare, int CastlingRights, int HalfmoveClock, int FullmoveNumber, ulong ZobristKey, ulong PawnKey, ulong NonPawnKeyWhite, ulong NonPawnKeyBlack);
    public Move LastMove => historyPly > 0 ? historyStack[historyPly - 1].Move : Move.None;

    /// <summary>
    /// The piece captured by the move that led to this position, or <see cref="Piece.Empty"/>
    /// when that move was quiet. Needed by the quiet-order dynamic pre-bonus (the reference
    /// credits the just-played QUIET move only, and the post-move board no
    /// longer carries the captured piece. Includes en-passant captures (the history record
    /// stores the removed pawn), so a caller must also exclude promotions explicitly.
    /// </summary>
    public Piece LastMoveCaptured => historyPly > 0 ? historyStack[historyPly - 1].Captured : Piece.Empty;

    public Move GetMoveFromHistory(int plyBack)
    {
        if (plyBack < 1 || historyPly < plyBack) return Move.None;
        return historyStack[historyPly - plyBack].Move;
    }





    public Position()
    {
        // Init jagged arrays
        Pieces = new ulong[2][];
        Pieces[0] = new ulong[6];
        Pieces[1] = new ulong[6];

        // set empty
        for (int i = 0; i < 64; i++) board[i] = Piece.Empty;
    }

    public Position(Position other)
    {
        System.Array.Copy(other.board, board, 64);

        // Init and deep copy jagged arrays
        Pieces = new ulong[2][];
        Pieces[0] = new ulong[6];
        Pieces[1] = new ulong[6];
        System.Array.Copy(other.Pieces[0], Pieces[0], 6);
        System.Array.Copy(other.Pieces[1], Pieces[1], 6);

        System.Array.Copy(other.occupancy, occupancy, 2);
        allOccupancy = other.allOccupancy;
        
        // Init properties
        Pinned = new ulong[2];
        Checkers = new ulong[2];
        System.Array.Copy(other.Pinned, Pinned, 2);
        System.Array.Copy(other.Checkers, Checkers, 2);

        SideToMove = other.SideToMove;
        CastlingRights = other.CastlingRights;
        EnPassantSquare = other.EnPassantSquare;
        HalfmoveClock = other.HalfmoveClock;
        FullmoveNumber = other.FullmoveNumber;
        ZobristKey = other.ZobristKey;
        PawnKey = other.PawnKey;
        NonPawnKeyWhite = other.NonPawnKeyWhite;
        NonPawnKeyBlack = other.NonPawnKeyBlack;

        // Clone history
        historyPly = other.historyPly;
        // Since History is a struct, Array.Copy copies values (deep copy for structs)
        System.Array.Copy(other.historyStack, historyStack, historyPly);
    }

    // Mailbox access (exposes board array for APIs/tests)
    public Piece[] Mailbox => board;

    public static Position FromFEN(string fen)
    {
        var pos = new Position();
        var parts = fen.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var rows = parts[0].Split('/');
        if (rows.Length != 8) throw new System.ArgumentException("Invalid FEN ranks");
        for (int r = 0; r < 8; r++)
        {
            var row = rows[r];
            int file = 0;
            foreach (var ch in row)
            {
                if (char.IsDigit(ch)) { file += ch - '0'; continue; }
                int sq = (7 - r) * 8 + file;
                pos.board[sq] = CharToPiece(ch);
                file++;
            }
        }
        pos.SideToMove = parts.Length > 1 && parts[1] == "b" ? 1 : 0;
        pos.CastlingRights = 0;
        if (parts.Length > 2)
        {
            var cr = parts[2];
            // Standard chess castling rights
            if (cr.Contains('K')) pos.CastlingRights |= 1;
            if (cr.Contains('Q')) pos.CastlingRights |= 2;
            if (cr.Contains('k')) pos.CastlingRights |= 4;
            if (cr.Contains('q')) pos.CastlingRights |= 8;
        }
        pos.EnPassantSquare = -1;
        if (parts.Length > 3 && parts[3] != "-")
        {
            pos.EnPassantSquare = Square.FromAlgebraic(parts[3]);
            // set en-passant file into Zobrist initial as well later
        }
        if (parts.Length > 4) pos.HalfmoveClock = int.Parse(parts[4]);
        if (parts.Length > 5) pos.FullmoveNumber = int.Parse(parts[5]);
        // build bitboards and initial zobrist
        for (int i = 0; i < 64; i++)
        {
            var p = pos.board[i];
            if (p == Piece.Empty) continue;
            int color = (p >= Piece.WP && p <= Piece.WK) ? 0 : 1;
            int pi = pos.PieceToIndex(p);
            pos.pieces[color][pi] |= (1UL << i);
            pos.occupancy[color] |= (1UL << i);
            pos.allOccupancy |= (1UL << i);
            pos.ZobristKey ^= Zobrist.PieceSquare[(int)p,i];
            if (p == Piece.WP || p == Piece.BP) pos.PawnKey ^= Zobrist.PieceSquare[(int)p,i];
            else if (color == 0) pos.NonPawnKeyWhite ^= Zobrist.PieceSquare[(int)p,i];
            else pos.NonPawnKeyBlack ^= Zobrist.PieceSquare[(int)p,i];
        }
        if (pos.EnPassantSquare != -1)
        {
            int file = pos.EnPassantSquare % 8;
            pos.ZobristKey ^= Zobrist.EnPassantFile[file];
        }
        if (pos.CastlingRights != 0)
        {
            if ((pos.CastlingRights & 1) != 0) pos.ZobristKey ^= Zobrist.Castling[0];
            if ((pos.CastlingRights & 2) != 0) pos.ZobristKey ^= Zobrist.Castling[1];
            if ((pos.CastlingRights & 4) != 0) pos.ZobristKey ^= Zobrist.Castling[2];
            if ((pos.CastlingRights & 8) != 0) pos.ZobristKey ^= Zobrist.Castling[3];
        }
        if (pos.SideToMove == 1) pos.ZobristKey ^= Zobrist.Side;
        pos.UpdateKingThreats();
        return pos;
    }

    public string ToFEN()
    {
        var sb = new StringBuilder();
        for (int r = 7; r >= 0; r--)
        {
            int empty = 0;
            for (int f = 0; f < 8; f++)
            {
                var p = board[r*8 + f];
                if (p == Piece.Empty) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(PieceToChar(p));
            }
            if (empty > 0) sb.Append(empty);
            if (r > 0) sb.Append('/');
        }
        sb.Append(' ');
        sb.Append(SideToMove == 0 ? 'w' : 'b');
        sb.Append(' ');
        string cr = "-";

        // Standard chess castling rights
        var parts = new List<char>();
        if ((CastlingRights & 1) != 0) parts.Add('K');
        if ((CastlingRights & 2) != 0) parts.Add('Q');
        if ((CastlingRights & 4) != 0) parts.Add('k');
        if ((CastlingRights & 8) != 0) parts.Add('q');
        if (parts.Count > 0) cr = new string(parts.ToArray());

        sb.Append(cr);
        sb.Append(' ');
        sb.Append(EnPassantSquare == -1 ? '-' : Square.ToString(EnPassantSquare));
        sb.Append(' ');
        sb.Append(HalfmoveClock);
        sb.Append(' ');
        sb.Append(FullmoveNumber);
        return sb.ToString();
    }

    private static Piece CharToPiece(char c) => c switch
    {
        'P' => Piece.WP, 'N' => Piece.WN, 'B' => Piece.WB, 'R' => Piece.WR, 'Q' => Piece.WQ, 'K' => Piece.WK,
        'p' => Piece.BP, 'n' => Piece.BN, 'b' => Piece.BB, 'r' => Piece.BR, 'q' => Piece.BQ, 'k' => Piece.BK,
        _ => Piece.Empty
    };

    private static char PieceToChar(Piece p) => p switch
    {
        Piece.WP => 'P', Piece.WN => 'N', Piece.WB => 'B', Piece.WR => 'R', Piece.WQ => 'Q', Piece.WK => 'K',
        Piece.BP => 'p', Piece.BN => 'n', Piece.BB => 'b', Piece.BR => 'r', Piece.BQ => 'q', Piece.BK => 'k',
        _ => '.'
    };

    public int GenerateMoves(Span<Move> buffer)
    {
        return MoveGenerator.GenerateMoves(this, buffer);
    }



    public int GeneratePseudoLegalMoves(Span<Move> moves, bool respectPin = true)
    {
        return MoveGenerator.GeneratePseudoLegalMoves(this, moves, SideToMove, respectPin);
    }

    /// <summary>
    /// Check-aware pseudo-legal generation (2026-09-06): additionally filters out moves that
    /// don't resolve a check and king moves into an attacked square, at generation time -
    /// see MoveGenerator's class remarks ("CHECK-AWARE GENERATION") for the full design.
    /// Still pseudo-legal with respect to a small residual set of edge cases (en passant
    /// discovered check through the double-pawn gap) that the caller must still validate,
    /// exactly like the plain pseudo-legal path.
    /// </summary>
    public int GenerateLegalMoves(Span<Move> moves, bool respectPin = true, bool capturesOnly = false)
    {
        return MoveGenerator.GenerateLegalMoves(this, moves, respectPin, capturesOnly);
    }

    public int GeneratePseudoLegalMoves(Span<Move> moves, int side, bool respectPin = true)
    {
        return MoveGenerator.GeneratePseudoLegalMoves(this, moves, side, respectPin);
    }

    /// <summary>
    /// Whether the side to move has exactly one pseudo-legal move. Counts the
    /// non-pawn attack sets directly and only builds the full list in the rare
    /// 0-or-1 case; see <see cref="MoveGenerator.HasExactlyOnePseudoLegalMove"/>.
    /// The search's single-response extension uses this instead of generating a
    /// throw-away move list at every eligible node.
    /// </summary>
    public bool HasExactlyOnePseudoLegalMove()
        => MoveGenerator.HasExactlyOnePseudoLegalMove(this);

    public IEnumerable<Move> GenerateMoves()
    {
        // Compatibility wrapper
        Span<Move> buffer = stackalloc Move[256];
        int count = GenerateMoves(buffer);
        var list = new List<Move>(count);
        for (int i = 0; i < count; i++) list.Add(buffer[i]);
        return list;
    }

    // helpers for generating moves (refactored to Span)



    // Material values used for SEE
    // Piece values for SEE (P, N, B, R, Q, K)
    private static readonly int[] PieceValues = new int[] { 109, 403, 435, 679, 1242, 20000 };

    public bool IsSquareAttackedBy(int sq, int attackerSide)
    {
        return AttackDetection.IsSquareAttackedBy(this, sq, attackerSide);
    }

    public bool IsWhite(Piece p) => p >= Piece.WP && p <= Piece.WK;

    // === Internal helpers for MoveExecution ===

    internal void SaveHistory(Move m, Piece captured, int capturedSquare)
    {
        historyStack[historyPly] = new History(m, captured, capturedSquare, EnPassantSquare, CastlingRights, HalfmoveClock, FullmoveNumber, ZobristKey, PawnKey, NonPawnKeyWhite, NonPawnKeyBlack);
        historyPly++;
    }
    
    internal History PopHistory()
    {
        historyPly--;
        return historyStack[historyPly];
    }
    
    internal void XorZobrist(ulong value) => ZobristKey ^= value;
    internal void RestoreZobrist(ulong value) => ZobristKey = value;
    internal void XorPawnKey(ulong value) => PawnKey ^= value;
    internal void RestorePawnKey(ulong value) => PawnKey = value;
    internal void XorNonPawnKey(int color, ulong value)
    {
        if (color == 0) NonPawnKeyWhite ^= value;
        else NonPawnKeyBlack ^= value;
    }
    internal void RestoreNonPawnKeys(ulong white, ulong black)
    {
        NonPawnKeyWhite = white;
        NonPawnKeyBlack = black;
    }
    
    internal void ClearPiece(int color, int pieceIndex, int square)
    {
        pieces[color][pieceIndex] &= ~(1UL << square);
        occupancy[color] &= ~(1UL << square);
        allOccupancy &= ~(1UL << square);
    }
    
    internal void SetPiece(int color, int pieceIndex, int square)
    {
        pieces[color][pieceIndex] |= (1UL << square);
        occupancy[color] |= (1UL << square);
        allOccupancy |= (1UL << square);
    }
    
    internal void ToggleSide() => SideToMove ^= 1;
    internal void SetCastlingRights(int rights) => CastlingRights = rights;
    internal void RestoreCastlingRights(int rights) => CastlingRights = rights;
    internal void SetEnPassant(int square) => EnPassantSquare = square;
    internal void ClearEnPassant() => EnPassantSquare = -1;
    internal void RestoreEnPassant(int square) => EnPassantSquare = square;
    internal void SetHalfmoveClock(int value) => HalfmoveClock = value;
    internal void IncrementHalfmoveClock() => HalfmoveClock++;
    internal void IncrementFullmoveNumber() => FullmoveNumber++;
    internal void RestoreClocks(int halfmove, int fullmove)
    {
        HalfmoveClock = halfmove;
        FullmoveNumber = fullmove;
    }
    
    internal int PieceToIndex(Piece p)
    {
        return p switch
        {
            Piece.WP or Piece.BP => 0,
            Piece.WN or Piece.BN => 1,
            Piece.WB or Piece.BB => 2,
            Piece.WR or Piece.BR => 3,
            Piece.WQ or Piece.BQ => 4,
            Piece.WK or Piece.BK => 5,
            _ => 0
        };
    }
    // === End internal helpers ===

    public void MakeMoveInternal(Move m)
    {
        MoveExecution.MakeMove(this, m);
    }

    public void UnmakeMoveInternal(Move m, Piece captured)
    {
        MoveExecution.UnmakeMove(this, m, captured);
    }



    public long Perft(int depth)
    {
        if (depth == 0) return 1;
        long nodes = 0;
        foreach (var m in GenerateMoves())
        {
            var captured = MakePublicMove(m);
            nodes += Perft(depth - 1);
            UnmakePublicMove(m, captured);
        }
        return nodes;
    }

    public bool IsKingAttacked(int side)
    {
        // find king using bitboard for efficiency
        Piece king = side == 0 ? Piece.WK : Piece.BK;
        int ksq = -1;

        // Use the bitboard to find the king square
        ulong kingBB = pieces[side][ 5]; // 5 is the king piece index
        if (kingBB != 0)
        {
            ksq = Bitboard.LsbIndex(kingBB);
        }
        else
        {
            return true; // no king -> attacked (should not happen in valid positions)
        }

        // Use targeted attack checks to avoid side effects
        return IsSquareAttackedBy(ksq, 1 - side);
    }

    // Public wrappers for external consumers (UCI etc.)
    public Piece MakePublicMove(Move m)
    {
        int capturedSquare = m.IsEnPassant ? (SideToMove==0 ? m.To - 8 : m.To + 8) : m.To;
        var captured = board[capturedSquare];
        MakeMoveInternal(m);
        return captured;
    }

    public void UnmakePublicMove(Move m, Piece captured)
    {
        UnmakeMoveInternal(m, captured);
    }

    public Piece GetPieceAt(int sq) => board[sq];

    /// <summary>
    /// Returns the moving piece for a (pseudo-legal) move, evaluated BEFORE
    /// <see cref="MakePublicMove"/> runs. For a promotion the mover is the
    /// promoted-from pawn (WP/BP, matching the side to move); otherwise it is the
    /// piece currently standing on the move's from-square.
    /// MUST be called pre-move: after MakePublicMove the from-square is vacated
    /// and SideToMove has already flipped, so the result would be wrong. All
    /// search/move-loop call sites compute this before making the move.
    /// </summary>
    public Piece GetMovingPiece(Move m)
    {
        return m.PromotionPiece != Piece.Empty
            ? (SideToMove == 0 ? Piece.WP : Piece.BP)
            : board[m.From];
    }

    // generate pseudo-legal moves (no legality filter against king-in-check)
    public IEnumerable<Move> GeneratePseudoLegalMoves()
    {
        Span<Move> buffer = stackalloc Move[256];
        int count = GeneratePseudoLegalMoves(buffer, respectPin: false);
        var list = new List<Move>(count);
        for (int i = 0; i < count; i++) list.Add(buffer[i]);
        return list;
    }



    // rebuild bitboards from current board array
    private void RebuildBitboardsFromBoard()
    {
        for (int c = 0; c < 2; c++) for (int i = 0; i < 6; i++) pieces[c][i] = 0UL;
        occupancy[0] = occupancy[1] = 0UL;
        allOccupancy = 0UL;
        ZobristKey = 0UL;
        PawnKey = 0UL;
        NonPawnKeyWhite = 0UL;
        NonPawnKeyBlack = 0UL;
        for (int i = 0; i < 64; i++)
        {
            var p = board[i];
            if (p == Piece.Empty) continue;
            int color = IsWhite(p) ? 0 : 1;
            int idx = PieceToIndex(p);
            pieces[color][idx] |= (1UL << i);
            occupancy[color] |= (1UL << i);
            allOccupancy |= (1UL << i);
            ZobristKey ^= Zobrist.PieceSquare[(int)p,i];
            if (p == Piece.WP || p == Piece.BP) PawnKey ^= Zobrist.PieceSquare[(int)p,i];
            else if (color == 0) NonPawnKeyWhite ^= Zobrist.PieceSquare[(int)p,i];
            else NonPawnKeyBlack ^= Zobrist.PieceSquare[(int)p,i];
        }
        if (EnPassantSquare != -1) ZobristKey ^= Zobrist.EnPassantFile[EnPassantSquare % 8];
        if ((CastlingRights & 1) != 0) ZobristKey ^= Zobrist.Castling[0];
        if ((CastlingRights & 2) != 0) ZobristKey ^= Zobrist.Castling[1];
        if ((CastlingRights & 4) != 0) ZobristKey ^= Zobrist.Castling[2];
        if ((CastlingRights & 8) != 0) ZobristKey ^= Zobrist.Castling[3];
        if (SideToMove == 1) ZobristKey ^= Zobrist.Side;

        // update pinned/checkers info after bitboards are rebuilt
        UpdateKingThreats();
    }

    // compute zobrist key from scratch for validation
    public ulong ComputeZobristKey()
    {
        ulong k = 0UL;
        for (int i = 0; i < 64; i++)
        {
            var p = board[i];
            if (p == Piece.Empty) continue;
            k ^= Zobrist.PieceSquare[(int)p,i];
        }
        if (EnPassantSquare != -1) k ^= Zobrist.EnPassantFile[EnPassantSquare % 8];
        if ((CastlingRights & 1) != 0) k ^= Zobrist.Castling[0];
        if ((CastlingRights & 2) != 0) k ^= Zobrist.Castling[1];
        if ((CastlingRights & 4) != 0) k ^= Zobrist.Castling[2];
        if ((CastlingRights & 8) != 0) k ^= Zobrist.Castling[3];
        if (SideToMove == 1) k ^= Zobrist.Side;
        return k;
    }

    /// <summary>
    /// Computes the pawn structure hash key from scratch (for testing and validation).
    /// </summary>
    public ulong ComputePawnKey()
    {
        ulong k = 0UL;
        for (int i = 0; i < 64; i++)
        {
            var p = board[i];
            if (p == Piece.WP || p == Piece.BP)
                k ^= Zobrist.PieceSquare[(int)p, i];
        }
        return k;
    }

    /// <summary>
    /// Computes a per-color non-pawn material key from scratch (for testing and validation) —
    /// the XOR of all non-pawn pieces of <paramref name="color"/> on their squares. The
    /// incremental counterpart must match this exactly after every move (the reference BoardState
    /// nonPawnKeys parity).
    /// </summary>
    public ulong ComputeNonPawnKey(int color)
    {
        ulong k = 0UL;
        for (int i = 0; i < 64; i++)
        {
            var p = board[i];
            if (p == Piece.Empty || p == Piece.WP || p == Piece.BP) continue;
            if ((IsWhite(p) ? 0 : 1) == color)
                k ^= Zobrist.PieceSquare[(int)p, i];
        }
        return k;
    }

    // Helper: returns bitboard of squares strictly between a and b (exclusive)


    // Compute pinned pieces and checkers for both colors
    // Compute pinned pieces and checkers for both colors
    internal void UpdateKingThreats()
    {
        pinned[0] = pinned[1] = 0UL;
        checkers[0] = checkers[1] = 0UL;

        for (int color = 0; color <= 1; color++)
        {
            int kingBB = Bitboard.LsbIndex(pieces[color][ 5]); // king square
            if (kingBB < 0) continue;

            // pawn and knight checks
            checkers[color] |= (AttackDetection.GetPawnAttacks(kingBB, color) & pieces[1 - color][ 0]);
            checkers[color] |= (AttackDetection.GetKnightAttacks(kingBB) & pieces[1 - color][ 1]);

            // diagonal sliders (bishop/queen) - find all potential attackers on rays from king (no blockers)
            ulong diagCandidates = AttackDetection.GetBishopAttacks(kingBB, 0UL) & (pieces[1 - color][ 2] | pieces[1 - color][ 4]);
            // orthogonal sliders (rook/queen) - find all potential attackers on rays from king (no blockers)
            ulong orthoCandidates = AttackDetection.GetRookAttacks(kingBB, 0UL) & (pieces[1 - color][ 3] | pieces[1 - color][ 4]);

            ulong cands = diagCandidates | orthoCandidates;
            while (cands != 0UL)
            {
                int sq = Bitboard.LsbIndex(cands);
                cands = Bitboard.ClearLsb(cands);
                ulong blockers = AttackDetection.BetweenTable[kingBB, sq] & allOccupancy; // Check ALL pieces as blockers
                int cnt = Bitboard.PopCount(blockers);
                if (cnt == 0)
                {
                    // no blockers -> direct check
                    checkers[color] |= (1UL << sq);
                }
                else if (cnt == 1)
                {
                    // single blocker -> if it's ours, it's pinned
                    if ((blockers & occupancy[color]) != 0)
                    {
                        pinned[color] |= blockers;
                    }
                }
            }
        }
    }

    public void MakeNullMove()
    {
        // Dirty-piece record (M4): a null move changes NO pieces, so its
        // record is empty — but the PREVIOUS make's record must not survive
        // it (a consumer would replay stale deltas).
        ResetDirtyPieces();

        historyStack[historyPly] = new History(Move.None, Piece.Empty, -1, EnPassantSquare, CastlingRights, HalfmoveClock, FullmoveNumber, ZobristKey, PawnKey, NonPawnKeyWhite, NonPawnKeyBlack);
        historyPly++;

        if (EnPassantSquare != -1)
        {
            ZobristKey ^= Zobrist.EnPassantFile[EnPassantSquare % 8];
            EnPassantSquare = -1;
        }

        SideToMove = 1 - SideToMove;
        ZobristKey ^= Zobrist.Side;

        // Increment halfmove clock
        HalfmoveClock++;
        if (SideToMove == 0) FullmoveNumber++; // Was black, now white.
    }

    public void UnmakeNullMove()
    {
        historyPly--;
        var h = historyStack[historyPly];

        // Dirty-piece record (M4): nothing to restore (a null move's
        // record is empty) — just clear whatever a prior make left.
        ResetDirtyPieces();

        // Restore meta
        EnPassantSquare = h.EnPassantSquare;
        CastlingRights = h.CastlingRights;
        HalfmoveClock = h.HalfmoveClock;
        FullmoveNumber = h.FullmoveNumber;
        ZobristKey = h.ZobristKey;
        PawnKey = h.PawnKey;
        NonPawnKeyWhite = h.NonPawnKeyWhite;
        NonPawnKeyBlack = h.NonPawnKeyBlack;

        // Flip side back
        SideToMove = 1 - SideToMove;
    }

    public ulong GetPieceBitboard(int color, int pieceIndex) => pieces[color][pieceIndex];
    public ulong GetOccupancy(int color) => occupancy[color];
    public ulong GetAllOccupancy() => allOccupancy;

    // convenience for tests
    public static Position StartingPosition() => FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

    // ============================
    // Draw Detection
    // ============================

    /// <summary>
    /// Check if the current position is drawn by the 50-move rule.
    /// Returns true if halfmove clock >= 100 (50 full moves without pawn move or capture).
    /// </summary>
}
