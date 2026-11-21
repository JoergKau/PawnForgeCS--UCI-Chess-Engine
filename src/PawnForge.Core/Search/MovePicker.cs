using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;

namespace PawnForge.Core.Search;

/// <summary>A move paired with its ordering score; sorted descending for move selection.</summary>
public struct ScoredMove : IComparable<ScoredMove>
{
    public Move Move;
    public int Score;
    public int CompareTo(ScoredMove other) => other.Score.CompareTo(Score); // Descending
}

/// <summary>
/// Staged move-ordering iterator: generates and scores moves LAZILY, one
/// class at a time, so expensive scoring is only paid for moves actually
/// reached before a beta cutoff.
///
/// EFFECTIVE STAGES (in emission order):
/// 1. TT move (if pseudo-legally valid in this position)
/// 2. all pseudo-legal moves, partitioned into:
/// a. good captures (SEE &gt;= 0, sorted by SEE + MVV-LVA + capture history)
/// b. quiets (sorted by killers/countermove via static score,
/// then butterfly + continuation histories)
/// c. bad captures (SEE &lt; 0, emitted last)
///
/// NOTE: the declared KILLERS/COUNTERMOVE stages are currently unreachable -
/// killers and the countermove receive large static scores inside the quiet
/// scoring instead (see ScoreQuiet). The stage constants remain for clarity.
///
/// BUFFERS: all spans are passed in from ThreadData (per-ply, pre-allocated) -
/// the picker allocates nothing. It is a ref struct and must not leave the
/// stack frame of the search loop that created it.
/// </summary>
public ref struct MovePicker
{
    private readonly Position _pos;
    private readonly Move _ttMove;
    private readonly ReadOnlySpan<Move> _killers;
    private readonly Move _counterMove;
    private readonly int[][][]? _history;
    private readonly int[][]? _contHist;
    private readonly int[][]? _contHist2;
    private readonly int[][]? _contHist4;
    private readonly int[][]? _contHist6;
    private readonly int[][][]? _captureHistory;
    private readonly bool _onlyCaptures;
    /// <summary> Request a captures-only GENERATION pass
    /// (no quiet moves constructed) when this picker is in <see cref="_onlyCaptures"/> mode.
    /// Gated by the search's `UseCapturesOnlyMovegen` toggle; the emitted set is exactly the
    /// one the full generator + partition would keep, so the toggle is node-identical.</summary>
    private readonly bool _capturesOnlyGeneration;
    /// <summary> Lazy SEE: score captures by MVV-LVA +
    /// capture history at partition time and evaluate SEE only when a capture is yielded
    /// (deferring losers to the bad-capture stage), instead of `SeeGe` on every capture.</summary>
    private readonly bool _lazySee;

    // Buffers - passed from outside (Search/ThreadData)
    private Span<Move> _genBuf;
    private Span<ScoredMove> _capsBuf;
    private Span<ScoredMove> _quietsBuf;
    private Span<Move> _badBuf;

    private int _capsCount;
    private int _capsIdx;

    private int _quietsCount;
    private int _quietsIdx;

    private int _badCount;
    private int _badIdx;

    private int _stage;
    private int _killerIdx;
    private bool _skipQuiets;

    // Staged iterator architecture: moves are generated lazily per stage (the reference & the design reference)
    private const int STAGE_TT_MOVE = 0;
    private const int STAGE_GENERATE_CAPTURES = 1;
    private const int STAGE_GOOD_CAPTURES = 2;
    private const int STAGE_KILLERS = 3;
    private const int STAGE_COUNTERMOVE = 4;
    private const int STAGE_GENERATE_QUIETS = 5;
    private const int STAGE_QUIETS = 6;
    private const int STAGE_BAD_CAPTURES = 7;
    private const int STAGE_DONE = 8;

    /// <summary>
    /// Creates the picker. Buffers come from ThreadData (per-ply,
    /// pre-allocated) - the picker allocates nothing on the heap.
    /// </summary>
    /// <param name="pos">The position at this node.</param>
    /// <param name="ttMove">The TT move to try first (validated pseudo-legally; Move.None if absent).</param>
    /// <param name="killers">Up to four killer moves of this ply (may be null).</param>
    /// <param name="history">Butterfly history [side][from][to] (may be null).</param>
    /// <param name="counterMove">The countermove heuristic candidate for this ply.</param>
    /// <param name="contHist">1-ply continuation history (may be null).</param>
    /// <param name="contHist2">2-ply continuation history (may be null).</param>
    /// <param name="contHist4">4-ply continuation history (may be null).</param>
    /// <param name="contHist6">6-ply continuation history (may be null).</param>
    /// <param name="captureHistory">Capture history [attacker][to][victim] (may be null).</param>
    /// <param name="onlyCaptures">Emit captures only (quiescence-search mode).</param>
    /// <param name="capturesOnlyGeneration"> Also GENERATE captures only
    /// (skip constructing quiets) when <paramref name="onlyCaptures"/> is true.</param>
    /// <param name="genBuf">Scratch buffer for the pseudo-legal generation.</param>
    /// <param name="capsBuf">Output buffer for good captures.</param>
    /// <param name="quietsBuf">Output buffer for quiet moves.</param>
    /// <param name="badBuf">Output buffer for bad captures.</param>
    public MovePicker(
        Position pos,
        Move ttMove,
        Move[]? killers,
        int[][][]? history,
        Move counterMove,
        int[][]? contHist,
        int[][]? contHist2,
        int[][]? contHist4,
        int[][]? contHist6,
        int[][][]? captureHistory,
        bool onlyCaptures,
        bool capturesOnlyGeneration,
        bool lazySee,
        Span<Move> genBuf,
        Span<ScoredMove> capsBuf,
        Span<ScoredMove> quietsBuf,
        Span<Move> badBuf)
    {
        _pos = pos;
        _ttMove = ttMove;
        _killers = killers != null ? new ReadOnlySpan<Move>(killers) : ReadOnlySpan<Move>.Empty;
        _history = history;
        _counterMove = counterMove;
        _contHist = contHist;
        _contHist2 = contHist2;
        _contHist4 = contHist4;
        _contHist6 = contHist6;
        _captureHistory = captureHistory;
        _onlyCaptures = onlyCaptures;
        _capturesOnlyGeneration = capturesOnlyGeneration;
        _lazySee = lazySee;

        // Assign buffers
        _genBuf = genBuf;
        _capsBuf = capsBuf;
        _quietsBuf = quietsBuf;
        _badBuf = badBuf;

        _stage = STAGE_TT_MOVE;
        _capsCount = 0;
        _capsIdx = 0;
        _killerIdx = 0;
        _quietsCount = 0;
        _quietsIdx = 0;
        _badCount = 0;
        _badIdx = 0;
        _skipQuiets = false;
    }

    /// <summary>
    /// Backward-compatible overload for callers that do not use the /20 NPS flags
    /// (tests, tools): identical to the full constructor with both flags off.
    /// </summary>
    public MovePicker(
        Position pos,
        Move ttMove,
        Move[]? killers,
        int[][][]? history,
        Move counterMove,
        int[][]? contHist,
        int[][]? contHist2,
        int[][]? contHist4,
        int[][]? contHist6,
        int[][][]? captureHistory,
        bool onlyCaptures,
        Span<Move> genBuf,
        Span<ScoredMove> capsBuf,
        Span<ScoredMove> quietsBuf,
        Span<Move> badBuf)
        : this(pos, ttMove, killers, history, counterMove, contHist, contHist2, contHist4,
               contHist6, captureHistory, onlyCaptures,
               capturesOnlyGeneration: false, lazySee: false,
               genBuf, capsBuf, quietsBuf, badBuf)
    {
    }

    // No Dispose needed as buffers are managed externally
    public void Dispose() { }

    /// <summary>
    /// Aborts remaining quiet moves for this node (the reference engine,.
    /// Called when Late Move Pruning or Move-Loop Futility triggers, avoiding redundant
    /// make-move / legality check / pruning cycles for all subsequent quiets.
    /// </summary>
    public void SkipQuiets()
    {
        _skipQuiets = true;
    }

    /// <summary>
    /// Returns the next ordered move, lazily advancing through the stages.
    /// Move.None signals that all moves have been emitted.
    /// </summary>
    public Move Next()
    {
        while (true)
        {
            switch (_stage)
            {
                case STAGE_TT_MOVE:
                    _stage = STAGE_GENERATE_CAPTURES;
                    // Basic validation
                    if (!_ttMove.IsNone && IsMovePseudoLegal(_ttMove))
                        return _ttMove;
                    break;

                case STAGE_GENERATE_CAPTURES:
                    // Using pre-assigned buffers - no alloc
                    // Generate moves with check-aware legal generator.
                    // When the search's
                    // `UseCapturesOnlyMovegen` toggle is on and this picker is in
                    // only-captures mode, ask the generator for captures only: quiets are
                    // never constructed (the partition below would discard them anyway).
                    // The emitted set is identical, so the toggle is node-identical.
                    int totalMoves = _onlyCaptures && _capturesOnlyGeneration
                        ? _pos.GenerateLegalMoves(_genBuf, true, capturesOnly: true)
                        : _pos.GenerateLegalMoves(_genBuf, true);

                    // Partition moves into good captures, bad captures, and un-scored quiets
                    for (int i = 0; i < totalMoves; i++)
                    {
                        var m = _genBuf[i];
                        if (!_ttMove.IsNone && m.Equals(_ttMove)) continue;

                        if (IsCapture(m))
                        {
                            // Lazy SEE: keep ALL captures in
                            // the scored buffer (MVV-LVA + capture history, no SEE) and defer
                            // losers at YIELD time. Eager mode keeps the exact good/bad split.
                            if (_lazySee)
                            {
                                if (_capsCount < _capsBuf.Length)
                                    _capsBuf[_capsCount++] = new ScoredMove { Move = m, Score = ScoreCaptureLazy(m) };
                            }
                            else if (_pos.SeeGe(m, 0))
                            {
                                if (_capsCount < _capsBuf.Length)
                                    _capsBuf[_capsCount++] = new ScoredMove { Move = m, Score = ScoreCapture(m) };
                            }
                            else
                            {
                                if (_badCount < _badBuf.Length)
                                    _badBuf[_badCount++] = m;
                            }
                        }
                        else if (!_onlyCaptures)
                        {
                            if (_quietsCount < _quietsBuf.Length)
                                _quietsBuf[_quietsCount++] = new ScoredMove { Move = m, Score = 0 }; // un-scored for now!
                        }
                    }

                    _stage = STAGE_GOOD_CAPTURES;
                    break;

                case STAGE_GOOD_CAPTURES:
                    if (_capsIdx < _capsCount)
                    {
                        PickBest(_capsBuf, _capsIdx, _capsCount);
                        Move bestCap = _capsBuf[_capsIdx].Move;
                        // Lazy SEE at yield time (the design reference
                        // MoveGen.GoodCapture): only a capture whose attacker is worth more
                        // than its victim can be a loser worth checking; defer it to the
                        // bad-capture stage and try the next one.
                        if (_lazySee && IsLosingCaptureForLazySee(bestCap))
                        {
                            _capsIdx++;
                            if (_badCount < _badBuf.Length)
                                _badBuf[_badCount++] = bestCap;
                            break;
                        }
                        _capsIdx++;
                        return bestCap;
                    }
                    _stage = _onlyCaptures ? STAGE_BAD_CAPTURES : STAGE_KILLERS;
                    break;

                case STAGE_KILLERS:
                    // STAGE 3: Try killer moves before generating/scoring bulk quiets
                    while (_killerIdx < _killers.Length)
                    {
                        Move k = _killers[_killerIdx++];
                        if (k.IsNone) continue;
                        // Match against pre-partitioned quiets to verify legality and non-capture
                        for (int i = 0; i < _quietsCount; i++)
                        {
                            if (!_quietsBuf[i].Move.IsNone && _quietsBuf[i].Move.Equals(k))
                            {
                                _quietsBuf[i] = new ScoredMove { Move = Move.None, Score = 0 }; // Mark played
                                return k;
                            }
                        }
                    }
                    _stage = STAGE_COUNTERMOVE;
                    break;

                case STAGE_COUNTERMOVE:
                    // STAGE 4: Try countermove before generating/scoring bulk quiets
                    _stage = STAGE_GENERATE_QUIETS;
                    if (!_counterMove.IsNone)
                    {
                        for (int i = 0; i < _quietsCount; i++)
                        {
                            if (!_quietsBuf[i].Move.IsNone && _quietsBuf[i].Move.Equals(_counterMove))
                            {
                                _quietsBuf[i] = new ScoredMove { Move = Move.None, Score = 0 }; // Mark played
                                return _counterMove;
                            }
                        }
                    }
                    break;

                case STAGE_GENERATE_QUIETS:
                    // STAGE 5: Only reached if TT, all good captures, killers, and countermove missed!
                    if (_skipQuiets)
                    {
                        _stage = STAGE_BAD_CAPTURES;
                        break;
                    }
                    // Compact un-played quiet moves and compute their history scores
                    int compactIdx = 0;
                    for (int i = 0; i < _quietsCount; i++)
                    {
                        Move m = _quietsBuf[i].Move;
                        if (!m.IsNone)
                        {
                            _quietsBuf[compactIdx++] = new ScoredMove { Move = m, Score = ScoreQuiet(m) };
                        }
                    }
                    _quietsCount = compactIdx;
                    _quietsIdx = 0;
                    _stage = STAGE_QUIETS;
                    break;

                case STAGE_QUIETS:
                    if (_skipQuiets)
                    {
                        _stage = STAGE_BAD_CAPTURES;
                        break;
                    }
                    if (_quietsIdx < _quietsCount)
                    {
                        PickBest(_quietsBuf, _quietsIdx, _quietsCount);
                        return _quietsBuf[_quietsIdx++].Move;
                    }
                    _stage = STAGE_BAD_CAPTURES;
                    break;

                case STAGE_BAD_CAPTURES:
                    if (_badIdx < _badCount)
                    {
                        return _badBuf[_badIdx++];
                    }
                    _stage = STAGE_DONE;
                    return Move.None;

                case STAGE_DONE:
                    return Move.None;
            }
        }
    }

    /// <summary>
    /// Partial selection sort: finds the highest-scoring move in moves[start..count-1]
    /// and swaps it into moves[start]. Avoids full O(N log N) sorting when early moves cut off.
    /// </summary>
    private static void PickBest(Span<ScoredMove> moves, int start, int count)
    {
        int bestIdx = start;
        int bestScore = moves[start].Score;
        for (int i = start + 1; i < count; i++)
        {
            if (moves[i].Score > bestScore)
            {
                bestScore = moves[i].Score;
                bestIdx = i;
            }
        }
        if (bestIdx != start)
        {
            (moves[start], moves[bestIdx]) = (moves[bestIdx], moves[start]);
        }
    }


    /// <summary>
    /// Validates a TT move against the current position: correct side, target
    /// not own-occupied, and physically reachable for the piece type (guards
    /// against TT collisions producing corrupt moves).
    /// </summary>
    private bool IsMovePseudoLegal(Move m)
    {
        // Faster, more robust validation of TT moves
        var p = _pos.GetPieceAt(m.From);
        if (p == Piece.Empty) return false;
        if ((IsWhite(p) ? 0 : 1) != _pos.SideToMove) return false;

        // Verify destination square is not occupied by own piece
        var target = _pos.GetPieceAt(m.To);
        if (target != Piece.Empty && (IsWhite(target) ? 0 : 1) == _pos.SideToMove) 
            return false;

        // Verify move is physically possible for this piece type (Pseudo-legality)
        // This prevents board corruption from TT collisions
        int from = m.From;
        int to = m.To;
        ulong toBB = 1UL << to;

        switch (p)
        {
            case Piece.WP:
                // Simple pawn move check
                if (m.IsEnPassant) return to == _pos.EnPassantSquare;
                int diff = to - from;
                if (diff == 8 || diff == 16) return true; // push
                if (diff == 7 || diff == 9) return target != Piece.Empty; // capture
                return false;
            case Piece.BP:
                if (m.IsEnPassant) return to == _pos.EnPassantSquare;
                int diffB = from - to;
                if (diffB == 8 || diffB == 16) return true;
                if (diffB == 7 || diffB == 9) return target != Piece.Empty;
                return false;
            case Piece.WN:
            case Piece.BN:
                return (MagicBitboards.GetKnightAttacks(from) & toBB) != 0;
            case Piece.WK:
            case Piece.BK:
                // B2-class fix (2026-09-10, cross-check from the previous generation): a
                // table-sourced castle (TT collision / stale entry) used to be trusted on its
                // flag alone — "Trust flag for now". MakeMove executes castles UNCONDITIONALLY
                // (hardcoded rook teleport), so an illegal castle here silently corrupts the
                // board (LIFO-balanced make/unmake, invisible until an illegal bestmove or a
                // king capture). Full legality is cheap and kills the whole class.
                if (m.IsCastle) return IsCastleFullyLegal(m);
                return (MagicBitboards.GetKingAttacks(from) & toBB) != 0;
            case Piece.WB:
            case Piece.BB:
                return (MagicBitboards.BishopAttacks(from, _pos.AllOccupancy) & toBB) != 0;
            case Piece.WR:
            case Piece.BR:
                return (MagicBitboards.RookAttacks(from, _pos.AllOccupancy) & toBB) != 0;
            case Piece.WQ:
            case Piece.BQ:
                return (MagicBitboards.GetQueenAttacks(from, _pos.AllOccupancy) & toBB) != 0;
            default:
                return false;
        }
    }
    
    private bool IsWhite(Piece p) => p >= Piece.WP && p <= Piece.WK;

    /// <summary>
    /// Full castling legality for a table-sourced castle move (B2-class fix, 2026-09-10):
    /// standard geometry, castling right present, rook actually home, transit squares empty,
    /// king not in check and not crossing an attacked square. Mirrors what the legal move
    /// generator asserts before it stamps IsCastle — a stale/collided TT entry no longer gets
    /// a free pass to the unconditional castle execution in MakeMove.
    /// </summary>
    private bool IsCastleFullyLegal(Move m)
    {
        bool white = _pos.SideToMove == 0;
        int enemy = 1 - _pos.SideToMove;

        // White: e1=4, h1 rook=7 (K, right 1), a1 rook=0 (Q, right 2)
        // Black: e8=60, h8 rook=63 (k, right 4), a8 rook=56 (q, right 8)
        int kSideRight = white ? 1 : 4;
        int qSideRight = white ? 2 : 8;
        int kingSq = white ? 4 : 60;
        int kRook = white ? 7 : 63;
        int qRook = white ? 0 : 56;
        Piece ownRook = white ? Piece.WR : Piece.BR;

        if (m.From != kingSq) return false;

        if (m.To == kingSq + 2) // king-side (g-file)
        {
            if ((_pos.CastlingRights & kSideRight) == 0) return false;
            if (_pos.GetPieceAt(kRook) != ownRook) return false;
            if (_pos.GetPieceAt(kingSq + 1) != Piece.Empty || _pos.GetPieceAt(kingSq + 2) != Piece.Empty)
                return false;
            return !_pos.IsSquareAttackedBy(kingSq, enemy)
                && !_pos.IsSquareAttackedBy(kingSq + 1, enemy)
                && !_pos.IsSquareAttackedBy(kingSq + 2, enemy);
        }
        if (m.To == kingSq - 2) // queen-side (c-file)
        {
            if ((_pos.CastlingRights & qSideRight) == 0) return false;
            if (_pos.GetPieceAt(qRook) != ownRook) return false;
            if (_pos.GetPieceAt(kingSq - 1) != Piece.Empty || _pos.GetPieceAt(kingSq - 2) != Piece.Empty
                || _pos.GetPieceAt(kingSq - 3) != Piece.Empty)
                return false;
            return !_pos.IsSquareAttackedBy(kingSq, enemy)
                && !_pos.IsSquareAttackedBy(kingSq - 1, enemy)
                && !_pos.IsSquareAttackedBy(kingSq - 2, enemy);
        }
        return false; // not a standard king-square geometry
    }

    /// <summary>True if the move lands on an enemy piece or is an en-passant capture.</summary>
    private bool IsCapture(Move m)
    {
        return m.IsEnPassant || _pos.GetPieceAt(m.To) != Piece.Empty;
    }

    // Reuse Sort with Comparison<T>
    // But we need to store scores. List<Move> doesn't store scores.
    // We can use a separate list of scores? Or a struct.
    // To avoid changing List<Move> to List<ScoredMove>, we can't easily Sort in-place with scores computed once.
    // We would have to recompute scores in Comparison, which is slow.
    // Or, convert List<Move> to List<ScoredMove>, sort, then yield?
    
    // Let's use a wrapper struct for sorting.


    /// <summary>
    /// Capture ordering score: MVV-LVA base + exact SEE + capture history.
    /// Good captures sort by this; bad captures (SEE &lt; 0) are emitted last.
    /// </summary>
    private int ScoreCapture(Move m)
    {
        // MVV/LVA + SEE + Capture History
        var captured = _pos.GetPieceAt(m.To);
        var attacker = _pos.GetPieceAt(m.From);
        int dp = (PieceValuesSafe(PieceToIndex(captured)) * 100) - PieceValuesSafe(PieceToIndex(attacker));
        // Exact SEE for capture ordering
        int see = _pos.SeeValue(m);
        
        // Add capture history bonus
        int captureBonus = 0;
        if (_captureHistory != null)
        {
            int pieceIdx = PieceToIndex(attacker);
            int victimIdx = PieceToIndex(captured);
            if (pieceIdx >= 0 && pieceIdx < 6 && victimIdx >= 0 && victimIdx < 6)
            {
                captureBonus = _captureHistory[pieceIdx][m.To][victimIdx];
            }
        }
        
        return 1000000 + dp + see + captureBonus; 
    }

    /// <summary>
    /// Lazy-SEE capture score: MVV-LVA + capture history
    /// only. The exact SEE term is deliberately omitted — SEE is paid at YIELD time (see
    /// <see cref="IsLosingCaptureForLazySee"/>), so captures that are never reached cost
    /// nothing.
    /// </summary>
    private int ScoreCaptureLazy(Move m)
    {
        var captured = _pos.GetPieceAt(m.To);
        var attacker = _pos.GetPieceAt(m.From);
        int dp = (PieceValuesSafe(PieceToIndex(captured)) * 100) - PieceValuesSafe(PieceToIndex(attacker));

        int captureBonus = 0;
        if (_captureHistory != null)
        {
            int pieceIdx = PieceToIndex(attacker);
            int victimIdx = PieceToIndex(captured);
            if (pieceIdx >= 0 && pieceIdx < 6 && victimIdx >= 0 && victimIdx < 6)
            {
                captureBonus = _captureHistory[pieceIdx][m.To][victimIdx];
            }
        }

        return 1000000 + dp + captureBonus;
    }

    /// <summary>
    /// the design reference's lazy-SEE predicate (MoveGen.GoodCapture):
    /// a capture can only be a material loser when the attacker is worth more than the
    /// victim, so the SEE call is paid only for those; a cheap attacker taking an expensive
    /// piece is accepted immediately. (En passant sees an empty target square, so it is
    /// accepted without SEE — a documented ordering deviation of the lazy mode.)
    /// </summary>
    private bool IsLosingCaptureForLazySee(Move m)
    {
        var captured = _pos.GetPieceAt(m.To);
        var attacker = _pos.GetPieceAt(m.From);
        int attackerValue = PieceValuesSafe(PieceToIndex(attacker));
        int victimValue = PieceValuesSafe(PieceToIndex(captured));
        return attackerValue > victimValue && !_pos.SeeGe(m, 0);
    }

    /// <summary>
    /// Quiet ordering score: butterfly + 1/2/4/6-ply continuation histories.
    /// Note: Killers and countermoves are emitted earlier in STAGE_KILLERS and
    /// STAGE_COUNTERMOVE, so only remaining quiet moves reach this method.
    /// </summary>
    private int ScoreQuiet(Move m)
    {
        // History
        int score = 0;
        int side = _pos.SideToMove;
        if (_history != null && _history.Length > side && _history[side] != null)
        {
             score += _history[side][m.From][m.To];
        }
        if (_contHist != null)
        {
             score += _contHist[m.From][m.To];
        }
        if (_contHist2 != null)
        {
             score += _contHist2[m.From][m.To] / 2;
        }
        if (_contHist4 != null)
        {
             score += _contHist4[m.From][m.To] / 2;
        }
        if (_contHist6 != null)
        {
             score += _contHist6[m.From][m.To] / 2;
        }
        return score;
    }

    /// <summary>Piece values used by capture ordering, indexed by
    /// <see cref="PieceToIndex"/>. A single static table instead of a fresh
    /// six-element array per call: this runs twice per scored capture (and
    /// twice more in the lazy-SEE predicate), so the old literal was a steady
    /// allocation source on the move-picker path.</summary>
    private static readonly int[] PieceValuesByIndex = { 100, 320, 330, 500, 900, 20000 };

    private static int PieceValuesSafe(int idx)
    {
        if ((uint)idx >= (uint)PieceValuesByIndex.Length) return 0;
        return PieceValuesByIndex[idx];
    }

    private int PieceToIndex(Piece p)
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
}