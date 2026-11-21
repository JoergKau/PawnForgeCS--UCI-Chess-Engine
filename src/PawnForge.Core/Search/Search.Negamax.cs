// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · the recursive alpha-beta (Negamax) node pipeline
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
    // Core negamax skeleton
    /// <summary>
    /// The negamax alpha-beta search: one node of the PVS tree. Handles the
    /// full node pipeline (draw detection, mate-distance pruning, check
    /// extension, TT probe, null-move pruning, ProbCut, IIR, singular
    /// extension, the staged move loop with all prunings, LMR and PVS
    /// re-searches) and the terminal mate/stalemate detection.
    /// </summary>
    /// <param name="pos">The position at this node (the side to move is set).</param>
    /// <param name="depth">Remaining search depth (0 drops to quiescence search).</param>
    /// <param name="alpha">Lower search bound.</param>
    /// <param name="beta">Upper search bound.</param>
    /// <param name="pvMove">Receives the best move of this node (PV extraction).</param>
    /// <param name="ply">Distance from the root (0 = root).</param>
    /// <param name="cutNode">True when this node is predicted to fail high (feeds cut-node-aware LMR).</param>
    /// <param name="excludedMove">Move excluded from the search (singular-extension verification).</param>
    /// <param name="verificationSearch">True inside a ProbCut verification (disables history updates).</param>
    /// <param name="pathExtensions">
    /// Total extension plies (check + single-response) already accumulated along THIS search path
    /// since the root. Threaded through every recursive call (like <paramref name="cutNode"/>);
    /// each move-loop recursion passes its own accumulated total, so the count is per-PATH, not
    /// per-subtree - two children of the same node do not share or double-count each other's
    /// extensions. See <see cref="UsePerPathExtensionCap"/>.
    /// </param>
    /// <returns>The node score in centipawns (side-to-move relative).</returns>
    public int Negamax(Position pos, int depth, int alpha, int beta, ref Move pvMove, int ply, bool cutNode = false, Move excludedMove = default, bool verificationSearch = false, int pathExtensions = 0)
    {
        // Periodic check for stop signal and time limit.
        // Poll cadence: the stable-TM model polls the
        // hard limit every 4096 nodes (the reference), the micro-riders batch every 1024, and the
        // legacy default stays at 2048. All three check the same conditions; only the
        // frequency differs, so a fixed-depth search is unaffected.
        long pollMask = UseMicroRiders ? 1023L : (UseStableTimeManagement ? 4095L : 2047L);
        if ((_td.Nodes & pollMask) == 0)
        {
            if (_signal.ShouldStop) return 0;
            
            // Check time limit (main thread only)
            if (_td.ThreadId == 0)
            {
                if (!_timeManager.IsInfinite)
                {
                    long currentTime = TimeManager.NowMs(); // monotonic
                    // Stop at the effective SOFT limit too, not only the hard
                    // limit: otherwise a long iteration overshoots the planned
                    // spend and burns the clock (see TimeManager.ShouldStop).
                    if (_timeManager.ShouldStop(currentTime))
                    {
                        _signal.Stop();
                        return 0;
                    }
                }

                // Node limit (`go nodes`): aggregate across all threads
                if (NodeLimit > 0)
                {
                    long totalNodes = _getTotalNodes != null ? _getTotalNodes() : _td.Nodes;
                    if (totalNodes >= NodeLimit)
                    {
                        _signal.Stop();
                        return 0;
                    }
                }
            }
        }
        
        if (ply >= 100) return EvaluateCached(pos); // Safety guard

        
        _td.IncrementNodes();
        // seldepth tracking is now in QuiescenceSearch

        // A: initialize this node's row of the explicit triangular PV table.
        // Must run on EVERY node visit at this ply — including TT-cutoff, null-move,
        // ProbCut-verification and singular-extension nodes — so a previous visitor of
        // the same ply can never leave a stale line that a later MergeMove would adopt.
        // Pure bookkeeping: it writes only the per-ply PV length and never changes any
        // search decision, so scores/node counts are identical whether UsePvTable is on
        // or off.
        _pvTable.InitPly(ply);

        bool isRoot = (ply == 0);
        // IMPORTANT: compute WITHOUT subtraction! With a full window
        // (alpha = int.MinValue+1, beta = int.MaxValue-1) the expression
        // `beta - alpha` overflows int32 and turns NEGATIVE, making every
        // full-window node look like a zero-window node. That disabled ALL
        // PV-node protections (LMP/SEE/history pruning fired in PV nodes)
        // and hid short forced mates. the reference avoids this by using small
        // window bounds (Score.MIN/MAX = +/-32767); we keep the int extremes
        // and use an overflow-safe comparison instead.
        // isPvNode <=> "non-zero window" <=> beta >= alpha + 2.
        bool isPvNode = beta > alpha + 1;

        // LMR v2 per-ply stack maintenance (the reference engine:
        //
        // pvDistance — "plies since the last full-window node" (`curr.pvDistance = pvNode ? 0 :
        // prev.pvDistance + 1`). Set on EVERY node visit before any recursion, so children read
        // the correct distance from the PV. The LMR v2 PV-distance term reduces late moves
        // harder the further the current node sits from the principal variation.
        //
        // failHighCount reset — the reference zeroes the stack slot TWO plies ahead at node entry
        // (`ss.get(ply + 2).failHighCount = 0`). The slot then accumulates the beta cutoffs of
        // every node that occupies it (cutoffs break the move loop, so the counter survives
        // across sibling subtrees and iterative-deepening re-visits) until the next grandparent
        // visit resets it. The LMR v2 fail-high-count term reads this accumulated counter at
        // the PARENT slot (ply - 1).
        _td.PvDistanceStack[ply] = isPvNode ? 0 : (ply > 0 ? _td.PvDistanceStack[ply - 1] + 1 : 0);
        if (ply + 2 < 128)
        {
            _td.FailHighCountStack[ply + 2] = 0;
        }

        // Draw Detection (except at root - let root handle it for proper move reporting).
        // Repetition threshold follows UseTwofoldRepetitionDraw: when on, the SECOND
        // occurrence of a position in the (game + search) history already scores as a draw —
        // the reference Score.isDoubleRepetition parity, the
        // When off, the legacy threefold-only behavior is reproduced exactly.
        if (!isRoot && pos.IsDrawForSearch(UseTwofoldRepetitionDraw))
        {
            // Contempt: `Contempt·(2·stm−1)` instead of
            // flat 0 (leading mainstream engines-9 DrawValue convention). Contempt = 0 (default) is exactly 0.
            return DrawScore(pos.SideToMove);
        }

        // Mate Distance Pruning
        if (!isRoot)
        {
            int mateValue = 31000 - ply;
            if (alpha < -mateValue) alpha = -mateValue;
            if (beta > mateValue - 1) beta = mateValue - 1;
            if (alpha >= beta) return alpha;
        }

        // Check Extensions (v2.0 Consolidated Audit): extend when in check.
        //
        // PER-PATH EXTENSION CAP (replaces the old flat "ply < 15" wall - see
        // UsePerPathExtensionCap doc comment for the full rationale and the
        // cross-engine comparison). `pathExtensions` is the count of extension plies already
        // accumulated along THIS path since the root; it is only consulted/incremented here,
        // not reset per node, so it behaves like a "decreasing extensions" counter passed down
        // through the recursion. A path that has not extended much can keep extending arbitrarily
        // deep (matching the reference's unconditional check extension and the design reference's depth-relative
        // gate); a path that has already accumulated MaxPathExtensions plies of extension stops
        // extending further (the safety valve neither reference needed because the reference relies on
        // the global MAX_DEPTH ceiling and the design reference's gate self-limits via iteration depth - this
        // explicit cap is PawnForge's equivalent safety net for the same unbounded-reach change).
        bool inCheck = pos.IsKingAttacked(pos.SideToMove);
        int extensionsUsed = pathExtensions;
        if (inCheck)
        {
            bool canExtend = !UsePerPathExtensionCap || extensionsUsed < MaxPathExtensions;
            // Legacy fallback (UsePerPathExtensionCap = false): reproduce the exact pre-v2.0
            // "ply < 15" wall byte-for-byte for A/B comparison against the new counter.
            if (!UsePerPathExtensionCap)
            {
                canExtend = ply < 15;
            }
            if (canExtend)
            {
                depth++;
                extensionsUsed++;
            }
        }

        // Single-Response Extension: if the side to move has exactly ONE
        // (pseudo-legal) reply, the line is forced - branching factor 1 - so
        // searching it one ply deeper is nearly free and resolves forced
        // sequences (recapture chains, forced blocks) beyond the horizon.
        // The pseudo-legal count is a safe shortcut: with exactly one
        // pseudo-legal move the legal count is 1 (forced) or 0 (mate /
        // stalemate - both handled by the move loop later). In-check nodes
        // already got the check extension, the root has instant play.
        // Gate: depth >= 3 (away from the qsearch horizon; measured identical
        // to an ungated variant but cheaper - horizon nodes skip the count).
        // Shares the same per-path counter as the check extension above (a forced-reply chain
        // immediately following a check chase should count against the same budget).
        if (UseSingleResponseExtension && !inCheck && ply > 0 && depth >= 3
            && (!UsePerPathExtensionCap || extensionsUsed < MaxPathExtensions))
        {
            // Count without building the list: the answer is "no" at almost every node,
            // and the count path returns as soon as it sees a second move.
            if (pos.HasExactlyOnePseudoLegalMove())
            {
                depth++;
                extensionsUsed++;
            }
        }

        // Syzygy tablebase probe (WDL) — UNCONDITIONAL on depth (SF main-search semantics,
        //): when the position is inside the tables, the probed
        // value is EXACT, so it must never be masked by heuristic scores. The old
        // `depth <= SyzygyProbeDepth` gate was too shallow for this engine's pruning stack
        // (RFP <= 8, futility <= 7, LMP, razoring <= 4 all fire BEFORE the old probe zone
        // at remaining depth <= 4): their material-ish returns (~+900 cp) masked the true
        // +29900 TB value from depth 12 up — empirically confirmed on a KBvKPP win
        // (score collapsed 29900 -> 467 between depth 11 and 12; with the depth gate
        // removed the same search returns 29900 at ~1/8 the nodes).
        // !isRoot: the root always completes its move loop (Bug-8 lesson) — the root
        // benefit comes through the children.
        if (!isRoot && Syzygy.ProbingEnabled && Syzygy.IsInitialized && Syzygy.MaxPieces > 0
            && BitOperations.PopCount(pos.GetAllOccupancy()) <= Syzygy.MaxPieces)
        {
            int tbWdl = Syzygy.ProbeWdl(pos);
            // A PV node must NOT hard-return the tablebase value.
            // Returning here unconditionally is what killed mate announcement: the probe
            // fired at every non-root node including PV nodes, so the very first TB node
            // below the root returned +29900 at depth 1 and the search never descended
            // (measured: seldepth 0, 630 nodes, `score cp 29900`). The mainstream engines do not do
            // this — they cut off on the TB value only at NON-PV nodes, so on the principal
            // variation the search keeps going, walks into the real checkmate, and that
            // mate score outranks the TB value (TbWinScore is deliberately below
            // MateScoreBound, so a genuine mate always beats it) and gets announced.
            //
            // We do NOT synthesise a mate score from the DTZ instead. Measured on the very
            // FEN in the baseline (8/5K2/8/8/8/8/5Q2/1k6 w, 3 pieces): true DTM is 12 plies
            // (mate 6, confirmed to depth 24 with the mainstream engines) while DTZ is 9. DTZ < DTM, so a
            // DTZ-derived score would announce `mate 5` for a mate-6 position — an
            // under-claim of our own distance, the one thing a mate score must never do.
            // Letting the search find the mate is both sound and what the reference engine
            // does. The DTZ is kept for its original job: ordering tablebase wins by speed.
            //
            // Two paths, deliberately different:
            // non-PV -> exact flat WdlToScore(wdl), hard return, NO dtz probe. This is
            // the no-masking invariant (exact TB values must never be masked by
            // heuristic returns) and it is what every cutoff below relies on;
            // grading it would also cost a second table probe per node.
            // PV -> grade by |DTZ| so a faster win outranks a slower one (otherwise
            // every TB win ties at TbWinScore and the DTZ-optimal move can only
            // be picked by move ORDER, never by score). When the DTZ is within
            // the remaining depth we do NOT return at all: the mate may be
            // reachable this iteration, so we descend and let the search find
            // the real mate score, which then wins outright. This is why the
            // mate appears as depth grows rather than never or always.
            if (tbWdl != int.MinValue)
            {
                if (isPvNode && !AllowSyzygyCutoffOnPvNodes)
                {
                    int dtz = Syzygy.ProbeDtz(pos);
                    int dtzAbs = dtz == int.MinValue ? 0 : Math.Abs(dtz);
                    if (dtzAbs <= 0 || dtzAbs > depth)
                        return Syzygy.WdlToScore(tbWdl, dtzAbs);
                    // else: fall through and search for the mate itself.
                }
                else
                {
                    return Syzygy.WdlToScore(tbWdl);
                }
            }
        }

        if (depth <= 0) return Quiescence(pos, alpha, beta, ply);

        var originalAlpha = alpha;

        // Probe TT
        // Pass ply to Probe for mate score adjustment
        TTEntry probe = default;
        bool hadProbe = _tt.Probe(pos.ZobristKey, out probe, ply, pos);
        if (SearchDiagnostics.Enabled) _td.Stats.RecordTtProbe(hadProbe);

        // ttPv (the reference engine): a node counts as
        // "PV-proven" when it is searched with a full window OR the probed TT entry itself
        // was stored by a PV-proven node. the reference derives it locally (no parameter threading):
        // entries set the flag at store time (see the final Store below), so the signal
        // propagates through the tree purely via the TT. Consumed by the LMR v2 ttPv
        // reduce-less term and the noisy-LMR ttPv exemption.
        bool ttPv = isPvNode || (hadProbe && probe.IsPv);
        
        // CRITICAL: Check for repetitions before allowing TT cutoffs
        // If this position has appeared before, we MUST search it fresh to detect the potential
        // threefold repetition. Using cached scores here causes the "repetition hunting" bug:
        // - 1st occurrence: Engine evaluates as +3.00, stores in TT
        // - 2nd occurrence: TT returns +3.00, but doesn't see next move leads to 3-fold (draw = 0)
        // - Engine plays into the repetition thinking it's winning
        bool allowTTCutoff = !DisableTTCutoffForRepetitions || pos.GetRepetitionCount() == 0;

        // ── TT extension (the reference's TtExtDepth) ─────────────
        // The reference— when a TT entry is already deep enough
        // for a cutoff (`probe.Depth >= depth + (isPvNode ? 2 : 0)`), looks like a plausible
        // cutoff (`probe.Value <= alpha || cutNode`), and yet is OUT of bounds (a LOWER
        // bound below beta / an UPPER bound above alpha — the stored value DISAGREES with
        // this window), the position is more interesting than this shallow window suggests:
        // grant one extra ply (depth++), at pre-extension depth <= TtExtensionMaxDepth (6).
        // EXACT entries are excluded by construction — they are always "within bounds"
        // (the reference prunes on them instead; PF's cutoff block handles that case). The check
        // runs BEFORE the cutoff block so alpha/beta are the un-narrowed window values
        // (the reference's ordering), and `allowTTCutoff` keeps PF's repetition protection: a
        // repeated position must not have its depth anchored to a cached entry.
        // PF probes only at depth >= 1, so there is no depth-0 extension like the reference's
        // QS-side one (that space belongs to D1's depth-0 QS keying).
        if (UseTtExtension && !isRoot && hadProbe && allowTTCutoff
            && probe.Flag != NodeType.Exact
            && probe.Depth >= depth + (isPvNode ? 2 : 0)
            && (probe.Value <= alpha || cutNode)
            && depth <= TtExtensionMaxDepth)
        {
            bool ttOutOfBounds = probe.Flag == NodeType.LowerBound
                ? probe.Value < beta     // a lower bound below beta disagrees with the window
                : probe.Value > alpha;   // an upper bound above alpha disagrees with the window
            if (ttOutOfBounds) depth++;
        }

        if (hadProbe && allowTTCutoff)
        {
            // The SHRINK half of the reference's TT
            // grow/shrink: at a PV node whose probed entry is deep
            // enough and WITHIN bounds, reduce this node's depth by one and keep searching
            // instead of taking the TT return. The GROW half is (`UseTtExtension`),
            // deliberately not duplicated here. `depth > 1` keeps the node out of the
            // depth-0 move-loop case; the trio master + sub-toggle gate it.
            bool trioTtShrink = UseExtensionTrio && UseTrioTtShrink && isPvNode && !isRoot && depth > 1;
            if (probe.Depth >= depth)
            {
                if (probe.Flag == NodeType.Exact)
                {
                    // At non-root nodes, trust the exact stored value — it was
                    // searched at sufficient depth and is fully reliable.
                    // At the root we must always complete the move loop: returning
                    // here would hand back whatever move happens to be cached,
                    // which may be stale or from a completely different line.
                    // The TT move is still used for ordering via the ttMove
                    // extraction below, so move-order quality is fully preserved.
                    if (!isRoot)
                    {
                        if (trioTtShrink)
                        {
                            depth--; // PV depth-shrink: verify the line instead of returning
                        }
                        else
                        {
                            pvMove = probe.BestMove;
                            return probe.Value;
                        }
                    }
                    // Root: fall through so the move loop runs and confirms the best move.
                }
                if (probe.Flag == NodeType.LowerBound)
                {
                    // Early TT-Cut History Update: reward the cutoff move
                    // Update history tables when we get a TT cutoff with a quiet move
                    // Only if we found the move quickly (moveCount < 4 in previous ply)
                    if (!probe.BestMove.IsNone && !isPvNode)
                    {
                        var occThem = pos.GetOccupancy(1 - pos.SideToMove);
                        bool isCapture = probe.BestMove.IsEnPassant || Bitboard.IsSet(occThem, probe.BestMove.To) || probe.BestMove.Promoted != 0;
                        
                        if (!isCapture && ply > 0 && _td.MoveCountStack[ply - 1] < 4)
                        {
                            // HIST1: gravity-bounded butterfly bonus (was += d², cap 200000)
                            int u = Math.Min(SearchHeuristics.ButterflyBonusScale * depth, SearchHeuristics.ButterflyBonusMax);
                            int side = pos.SideToMove;
                            ref int h = ref _td.ButterflyHistory[side][probe.BestMove.From][probe.BestMove.To];
                            h = SearchHeuristics.Gravity(h, u, SearchHeuristics.ButterflyMax);
                        }
                    }
                    
                    // At the root a LowerBound cutoff would return the cached
                    // move without running the move loop. Only cut at non-root nodes;
                    // at the root, the window is driven by IterativeDeepening aspiration.
                    if (!isRoot)
                    {
                        if (probe.Value >= beta)
                        {
                            if (trioTtShrink)
                            {
                                depth--; // within bounds at a PV node → shrink, no narrowing
                            }
                            else
                            {
                                pvMove = probe.BestMove;
                                return probe.Value;
                            }
                        }
                        else
                        {
                            alpha = Math.Max(alpha, probe.Value);
                        }
                    }
                }
                else if (probe.Flag == NodeType.UpperBound)
                {
                    // At the root an UpperBound cutoff would return the move stored
                    // from a fail-low node — not a confirmed best move — without
                    // executing any search. Only cut at non-root nodes; at the root,
                    // the window is driven by IterativeDeepening aspiration.
                    if (!isRoot)
                    {
                        if (probe.Value <= alpha)
                        {
                            if (trioTtShrink)
                            {
                                depth--; // within bounds at a PV node → shrink, no narrowing
                            }
                            else
                            {
                                pvMove = probe.BestMove;
                                return probe.Value;
                            }
                        }
                        else
                        {
                            beta = Math.Min(beta, probe.Value);
                        }
                    }
                }

                // Window collapsed through combined bound narrowing (LowerBound
                // raised alpha, UpperBound lowered beta). Only short-circuit at
                // non-root nodes; the root must always execute the move loop.
                if (!isRoot && alpha >= beta)
                {
                    pvMove = probe.BestMove;
                    return probe.Value;
                }
            }

            // Secondary "small ProbCut": fires from the TT probe alone, tolerating an entry up
            // to 4 plies SHALLOWER than the main cutoff path above requires. No move generation,
            // no child search - purely a comparison on data already in scope.
            //
            // CRITICAL SAFETY: !isRoot-guarded. This check must NEVER return a value at the root
            // without the move loop running - the root must always confirm its best move by
            // actual search (the same invariant the Exact/LowerBound/UpperBound paths above
            // already enforce with their !isRoot guards).
            //
            // !isPvNode: consistent with the existing (full) ProbCut block further down in this
            // method, which is also non-PV-only.
            //
            // Mate-score exclusion uses the same `< 30000` idiom the existing ProbCut block uses
            // (mate-score convention is 31000 - plies), so this shortcut never fires near a mate.
            if (UseSecondaryProbCut && !isRoot && !isPvNode && !inCheck
                && probe.Flag == NodeType.LowerBound
                && probe.Depth >= depth - 4
                && Math.Abs(beta) < 30000 && Math.Abs(probe.Value) < 30000)
            {
                const int SecondaryProbCutMargin = 200;
                int probCutBeta = beta + SecondaryProbCutMargin;
                if (probe.Value >= probCutBeta)
                {
                    // No pvMove assignment: like the fail-soft RFP/razoring early returns, this
                    // path does not claim to know the position's best move, only that it is at
                    // least this good.
                    return probCutBeta;
                }
            }
        }

        // ─── Static Evaluation (Phase 5a: reuse the TT-cached eval; reorder) ───────────
        // The TT has now already been probed and any cutoff taken ABOVE this point, so the
        // expensive static evaluation runs ONLY on nodes that actually keep searching — never on a
        // node that just cut off. (That was: the eval used to be computed before the probe
        // and was thrown away on every TT-cutoff node.) On a TT hit whose entry carries a cached
        // eval we reuse it verbatim (Phase 5a — the reference's design): the raw static eval is
        // deterministic per position, so the reused value is bit-identical to a fresh Evaluate,
        // making this a pure speed win, not a behavioural change. On a miss we compute it once here;
        // it is written back into this node's TT entry at the Store below, so future visits (this
        // search or a transposition) can reuse it.
        //
        // NOTE (in-check nodes): the eval is STILL computed when in check (it feeds the
        // `improving` flag below, which is not !inCheck-gated), so `rawStaticEval` is a real value
        // here either way. It is only the STORE that suppresses caching on in-check nodes
        // (`inCheck ? TTable.NoStaticEval : rawStaticEval` below), because the raw eval of an
        // in-check position would be reused incorrectly if a later visit probes from a quieter
        // context.
        int rawStaticEval = (hadProbe && probe.StaticEval != TTable.NoStaticEval)
            ? probe.StaticEval
            : EvaluateCached(pos);
        int staticEval = rawStaticEval;

        // Previous move + its mover, needed by the correction history's countermove (piece-to)
        // term below and by move ordering further down. Hoisted here from the move-ordering
        // block (it has no dependency on anything in between). Pure reads of the node's own
        // state — recursion-safe.
        Move prevMove = verificationSearch ? Move.None : pos.LastMove;
        int prevPieceIndex = -1;
        if (!prevMove.IsNone)
        {
            // The piece that MADE the previous move, the reference's `sse.piece`: the PRE-move
            // moving piece. In the current node's board it now sits on prevMove.To — except
            // after a promotion, where the mover was a pawn (the reference stores the pre-promotion
            // piece, so must we).
            prevPieceIndex = prevMove.Promoted != 0
                ? 0 // pawn
                : pos.PieceToIndex(pos.GetPieceAt(prevMove.To));
        }

        // LMR v2 complexity accumulator (term (h)); populated by the correction-history
        // block just below, consumed by the LMR block in the move loop. Local on purpose — see
        // the note there (recursion safety).
        int evalComplexity = 0;

        // Correction History: apply learned correction to the static evaluation before pruning decisions.
        // Adjusts staticEval based on past search diffs stored for this pawn structure hash key (PawnKey & 16383).
        // Using PawnKey allows learned corrections to generalize across positions with identical pawn structures.
        // NOTE: only the *corrected* copy (staticEval) is used by the heuristics below; the RAW value
        // (rawStaticEval) is what gets cached in the TT, matching the reference's rawStaticEval semantics so
        // cached evals stay correction-free and reusable across positions that share a key.
        if (UseCorrectionHistory && depth > 0 && !inCheck)
        {
            if (UseRefCorrectionHistory)
            {
                // ── reference-style 4-term correction history
                // (a faithful port) ─────────────────────────────────
                // correction = (pawn·99 + nonPawnW·100 + nonPawnB·100 + counter·96) / 64
                // the reference gates the correction on ttMove == null — a TT hit means a cached
                // score for this exact position exists, so the statistical correction is
                // unnecessary noise. PF's ttMove is extracted further below, so the same
                // condition is computed here from the probe data (hadProbe && BestMove set).
                bool hasTtMove = hadProbe && !probe.BestMove.IsNone;
                evalComplexity = 0;
                if (!hasTtMove)
                {
                    int side = pos.SideToMove;
                    // the reference applies per-term WEIGHTS as percentages (config.corrPawnWeight
                    // = 99, corrNonPawnWeight = 100, corrCounterWeight = 96 — i.e. ×99/100,
                    // ×100/100, ×96/100), THEN divides the sum by SCALE (64). Each table
                    // value is clamped to ±2048, so the total correction is bounded by
                    // 2048·(99+100+100+96)/100/64 ≈ ±127 cp.
                    int pawn = _td.CorrectionPawn[side][(int)(pos.PawnKey & 16383)] * 99 / 100;
                    int white = _td.CorrectionNonPawn[0][side][(int)(pos.NonPawnKeyWhite & 16383)] * 100 / 100;
                    int black = _td.CorrectionNonPawn[1][side][(int)(pos.NonPawnKeyBlack & 16383)] * 100 / 100;
                    // Countermove (piece-to) term: the reference reads the PREVIOUS move's
                    // (piece, to-square) for the side to move.
                    // prevMove is the move made one ply earlier (side-flipped mover), so the
                    // mover's side is 1 - pos.SideToMove.
                    int counter = 0;
                    if (ply > 0 && !prevMove.IsNone)
                    {
                        counter = _td.CorrectionCont[side][prevPieceIndex][prevMove.To] * 96 / 100;
                    }
                    evalComplexity = pawn * pawn + white * white + black * black + counter * counter;
                    int corr = (pawn + white + black + counter) / 64;
                    staticEval += corr;
                    staticEval = Math.Clamp(staticEval, -29999, 29999);
                }
            }
            else
            {
            const int W = 512; // 16384 / W = 32 cp max correction (<= 50 cp cap)
            int idx = (int)(pos.PawnKey & 16383);
            int corr = _td.CorrectionHistory[pos.SideToMove][idx];
            staticEval += corr / W;
            staticEval = Math.Clamp(staticEval, -29999, 29999);

            // LMR v2 complexity term (term (h)). The reference's complexity is the sum of
            // its FOUR weighted squared correction terms; PawnForge
            // carries pawn-only correction history, so the complexity proxy is the squared
            // raw pawn correction. A large correction magnitude means the static eval has been
            // systematically wrong for this pawn structure — the position is dynamically
            // complex — and late moves there deserve LESS reduction. The raw value (±16384)
            // is squared and divided by LmrV2ComplexityDivisor in the LMR block; it must be a
            // LOCAL (not a field) because Negamax is recursive and children would clobber it.
            // DEVIATION from the reference documented in Search.Options.UseLmrTermComplexity.
            evalComplexity = corr * corr;
            }
        }

        // ── TT-score-as-eval (`canUseTTScore`) ────────────
        // The reference engine's TT-score helper — the same
        // rule the strongest mainstream engines apply. When the probed entry's score is
        // CONSISTENT with this position's raw static eval (EXACT, or a LOWER bound at
        // least as high as the raw eval, or an UPPER bound at most as low), it is a
        // strictly more informed value than raw + correction, so it REPLACES `staticEval`
        // — the input of every pruner below (RFP, razoring, futility, the LMR-futile
        // margin, NMP's `staticEval >= beta` gate, improving/hindsight). The correction
        // history is kept only when the entry has no usable score (the reference's shape:
        // compute the correction, then overwrite). Both references skip this replacement
        // while in check (the reference's static-eval block is `!inCheck`; leading mainstream engines keeps the
        // parent's eval) — mirrored here. `allowTTCutoff` additionally keeps PF's
        // repetition-hunting protection: a node whose position already repeated must not
        // have its pruning anchored to a cached score from a different game context.
        // `rawStaticEval` is untouched — the TT must keep caching correction-free raw
        // evals (a reused value must stay bit-identical to a fresh Evaluate).
        if (UseTtScoreEval && !inCheck && allowTTCutoff && hadProbe
            && CanUseTtScore(probe, rawStaticEval))
        {
            staticEval = probe.Value;
        }

        // Hindsight Reductions: adjust depth by the previous ply's reduction
        // Dynamically adjust depth based on previous ply's reduction and evaluation
        if (ply > 0 && depth >= 2 && !inCheck && !isPvNode)
        {
            int prevReduction = _td.ReductionStack[ply - 1];
            int prevEval = _td.EvalStack[ply - 1];

            // Two shapes:
            // * trio ON (UseExtensionTrio + UseTrioHindsight): the reference's hindsight EXTENSION
            // only — a child of a heavily reduced parent (prior reduction >= 3,
            // HindsightExtensionLimit) whose combined eval fell below 0 gets the ply back
            //. the reference has NO reduce half.
            // * trio OFF (the shipped default): the legacy unconditional pair, bit-identical —
            // extend at prior reduction >= 2 and combined eval < 0, reduce at prior
            // reduction >= 1 and combined eval > 62.
            // The trio REPLACES the legacy pair rather than adding to it: both react to the
            // same signal, so stacking them would double-count the extension.
            if (UseExtensionTrio && UseTrioHindsight)
            {
                if (prevReduction >= HindsightMinReduction && staticEval + prevEval < 0)
                {
                    depth++;
                }
            }
            else
            {
                // If previous ply was heavily reduced AND combined eval < 0 -> extend
                if (prevReduction >= 2 && staticEval + prevEval < 0)
                {
                    depth++;
                }
                // If previous ply was reduced AND combined eval > 62 -> reduce
                if (prevReduction >= 1 && staticEval + prevEval > 62)
                {
                    depth--;
                }
            }
        }

        // ── Quiet-order dynamic pre-bonus ────────────────
        // the reference: the just-played QUIET move earns a history bonus from
        // the eval swing between the parent and this node — `clamp(mult·−(staticEval +
        // prevStaticEval), min, max)` — written BEFORE any fail-high result exists, which is
        // exactly the feedback the quiet ordering otherwise lacks. Only a quiet previous move
        // qualifies (no capture, no promotion, no en passant); the mover's side is
        // `1 - pos.SideToMove` because the board is already at the child position. The write
        // goes through the same gravity bound as every other butterfly-history update.
        // DEFAULT OFF: nothing is written and ordering is bit-identical.
        if (UseEvalSwingBonus && !inCheck && !isRoot && ply > 0 && !prevMove.IsNone
            && prevMove.Promoted == 0 && pos.LastMoveCaptured == Piece.Empty)
        {
            int swing = staticEval + _td.EvalStack[ply - 1];
            int bonus = Math.Clamp(EvalSwingBonusMult * -swing, EvalSwingBonusMin, EvalSwingBonusMax);
            int moverSide = 1 - pos.SideToMove;
            ref int h = ref _td.ButterflyHistory[moverSide][prevMove.From][prevMove.To];
            h = SearchHeuristics.Gravity(h, bonus, SearchHeuristics.ButterflyMax);
        }

        // Improving / Opponent-Worsening flags: computed once per node from data already
        // tracked in the per-thread eval-stack (the same array the hindsight-reduction block
        // above reads). Pure boolean comparisons - negligible cost. The UseImprovingFlag
        // toggle gates APPLICATION at each consuming site below, not this computation, so
        // disabling it reproduces today's flat margins byte-for-byte.
        // improving: is our own position trending up vs. 2 plies ago
        // (same side to move both times)?
        // opponentWorsening: did the opponent's last move fail to improve their
        // position (their eval + our eval above a small positive bar)?
        bool improving = !isRoot && ply >= 2 && staticEval > _td.EvalStack[ply - 2];
        bool opponentWorsening = !isRoot && !inCheck && ply >= 1
                                  && staticEval + _td.EvalStack[ply - 1] > 1;

        // Reverse Futility Pruning (RFP) / Static Null Move Pruning — calibrated to the reference 1024 NNUE
        // Condition: Not in check, not root, not PV node, depth <= gate, no excluded move.
        // Margins are Track 2 SPSA levers (defaults = legacy literals).
        //
        // Two margin sources:
        // * array path (DEFAULT): texel per-depth cells, gate RfpMaxDepth (4) — unchanged,
        // bit-identical to the PERF-BASELINE numbers;
        // * formula path (UseFormulaRfp=true): margin = depth * (improving ? 65 : 95),
        // gate RfpFormulaMaxDepth (8). The improving-awareness is built into the SCALE
        // choice here, so the array path's additive deltas are deliberately NOT applied
        // on the formula path (that would double-count improving).
        int rfpGateDepth = UseFormulaRfp ? RfpFormulaMaxDepth : RfpMaxDepth;
        if (UseReverseFutilityPruning && !inCheck && !isRoot && !isPvNode && depth <= rfpGateDepth && excludedMove.IsNone)
        {
            // Per-depth RFP margins, originally transcribed verbatim as
            // 102/210/267/394; gate depth < 5. The reference's
            // improving-awareness layered on top via deltas:
            // margin -= improving ? 60 : -2, then -13 when the opponent is
            // worsening) — ALL DELTAS DEFAULT TO 0, so with the compiled
            // defaults the margin below is exactly the flat texel one
            // (bit-identical to the PERF-BASELINE numbers; pinned by
            // ImprovingFlagTests). the reference's values via setoption:
            // RfpImprovingDelta=-60 RfpNotImprovingDelta=2
            // RfpOpponentWorseningDelta=-13.
            int margin;
            if (UseFormulaRfp)
            {
                // [B1] linear formula — scales to any depth (no clamping trap).
                margin = depth * ((UseImprovingFlag && improving) ? RfpImprovingScale : RfpNotImprovingScale);
            }
            else
            {
                margin = RfpMarginByDepth[Math.Min(depth, RfpMarginByDepth.Length - 1)];
                if (UseImprovingFlag)
                {
                    margin += improving ? RfpImprovingDelta : RfpNotImprovingDelta;
                    if (opponentWorsening) margin += RfpOpponentWorseningDelta;
                }
            }
            if (staticEval - margin >= beta)
            {
                // RFP FAIL-SOFT HALF, folded into the B1
                // formula arm. PF's legacy return is already fail-soft (`staticEval`, not
                // `beta` — a "bare beta" premise was stale). The reference instead
                // DAMPS the spill: `beta + (staticEval − beta)/3`, so a
                // fail-high cascade raises the parent's alpha without handing it the full
                // overshoot. The formula arm ships both B1 halves (linear margin + damped
                // spill); the array path keeps the exact pre-change return. The rule lives
                // in RfpFailSoftScore.
                return RfpFailSoftScore(staticEval, beta, UseFormulaRfp);
            }
        }

        // Razoring — calibrated to the reference 1024 NNUE
        // If static eval is VERY low, and depth <= RazorMaxDepth, drop to qsearch.
        // Margin is a Track 2 SPSA lever (default = legacy literal).
        if (UseRazoring && !inCheck && !isRoot && !isPvNode && depth <= RazorMaxDepth && excludedMove.IsNone)
        {
             // Per-depth razor margins, originally transcribed verbatim as
             // 86 @d≤1 and 353 @d2-3; threshold = staticEval + margin <= alpha.
             // The reference's not-improving easing is layered on top
             // (val -= 20 when !improving) — the delta
             // DEFAULTS TO 0 (flat texel threshold, bit-identical; pinned by
             // ImprovingFlagTests). the reference's value via setoption:
             // RazorNotImprovingDelta=20.
             int val = staticEval + RazorMarginByDepth[Math.Min(depth, RazorMarginByDepth.Length - 1)];
             if (UseImprovingFlag && !improving) val -= RazorNotImprovingDelta;
             if (val <= alpha)
             {
                 // Verify with Quiescence
                 int q = Quiescence(pos, alpha, beta, ply);
                 if (q < alpha) return alpha;
             }
        }

        // Null Move Pruning — calibrated to the reference 1024 NNUE
        // Null-Move Pruning: depth >= NmpMinDepth, not in check, not at the root.
        // Formula: R = NmpBaseReduction + depth / NmpDepthDivisor + evalBonus.
        // Reduction terms are Track 2 SPSA levers (defaults = legacy literals).
        //
        // Rider (b)'s verification seal: `NmpSealPly` is 0
        // unless a deep verification search is running, so `ply >= _td.NmpSealPly` is a
        // no-op on the default path and suppresses NMP inside verification subtrees when
        // `UseVerifiedNmp` is armed.
        // Rider (c) of the audit's pack — the TT-record gate `!ttHit || cutNode ||
        // score >= beta` — is ALREADY this condition (shipped NMP guard), so it needs no
        // toggle.
        if (UseNullMovePruning && !isRoot && depth >= NmpMinDepth && !inCheck && staticEval >= beta
            && ply >= _td.NmpSealPly
            && (!hadProbe || cutNode || probe.Value >= beta))
        {
            // Zugzwang guard (Req 5): suppress NMP in pawn-and-king-only positions
            // In pure pawn endgames, having to move can be detrimental (zugzwang), violating the NMP null-move assumption.
            bool pawnKingOnly = (pos.Occupancy[pos.SideToMove]
                                 & ~pos.Pieces[pos.SideToMove][0]   // exclude pawns
                                 & ~pos.Pieces[pos.SideToMove][5])  // exclude king
                                == 0;
            if (!pawnKingOnly)
            {
                // Cross-engine standard: base reduction R = 3 + d/4, plus an
                // eval-scaled bonus min(3, (eval−β)/82) — the eval-scaled
                // bonus term is the classic HCE-standard shape.
                // Our zugzwang + TT guards stay (documented safety shape).
                // `UseRefNmpEvalScale` swaps in the reference's
                // exact eval-proportional shape: min((staticEval − beta)/191, 4)
                // (the reference's scale 191 / max reduction 4) instead
                // of the shipped min(…/200, 3) knobs. Independent arm.
                int evalBonus = UseRefNmpEvalScale
                    ? Math.Min((staticEval - beta) / 191, 4)
                    : Math.Clamp((staticEval - beta) / NmpBonusDivisor, 0, NmpBonusMax);
                int R = NmpBaseReduction + depth / NmpDepthDivisor + evalBonus;
                if (R >= depth) R = depth - 1;
                
                // Make Null Move
                pos.MakeNullMove();
                _evaluator.MakeMove(pos, Move.None, Piece.Empty, Piece.Empty);
                _tt.Prefetch(pos.ZobristKey);
                Move nullDummy = default;
                // Search with reduced depth
                // Null-move child: the opposite node type (a node we expect to fail low).
                int score = -Negamax(pos, depth - 1 - R, -beta, -beta + 1, ref nullDummy, ply + 1, cutNode: !cutNode, pathExtensions: extensionsUsed);
                _evaluator.UnmakeMove();
                pos.UnmakeNullMove();
                
                if (_signal.ShouldStop) return 0;

                if (score >= beta)
                {
                    if (!UseVerifiedNmp)
                    {
                        // Legacy: mate-line fail-highs are IGNORED (a mate score is not
                        // trusted through a null search, so the node keeps searching);
                        // everything else cuts off fail-hard at beta.
                        if (Math.Abs(score) < 30000)
                            return beta;
                    }
                    else if (_td.NmpSealPly > 0 || depth <= 14)
                    {
                        // ── verified-null mate clamp ─────────────────
                        // the reference and the design reference agree: on the
                        // direct path (inside a seal, or depth ≤ 14) a MATE-line score is
                        // clamped to beta before the cut — the fail-high is real but the
                        // mate distance is not trusted through a null move — while a
                        // non-mate fail-high returns the score fail-soft (the parent's
                        // alpha raise is free EBF). The rule lives in NmpFailHighScore.
                        return NmpFailHighScore(score, beta);
                    }
                    else
                    {
                        // ── deep verification with self-limiting seal ─
                        // the reference: at depth > 14, re-search the SAME
                        // node WITHOUT the null move at the null child's depth on a
                        // [beta−1, beta] window; only a confirmed fail-high cuts off. The
                        // seal (3·(depth−1−R)/4 + ply, the reference's td.nmpPly) suppresses NMP
                        // at the shallow plies of the verification subtree, so a nested
                        // null move takes the resp. direct path above instead of launching
                        // its own verification — verifications chain-limit themselves.
                        _td.NmpSealPly = (3 * (depth - 1 - R) / 4) + ply;
                        Move verifyDummy = default;
                        // `excludedMove` is threaded through: PF's NMP is not gated on
                        // `excludedMove.IsNone` (unlike the reference's singularSearch guard), so a
                        // verification inside a singular-extension probe must keep searching
                        // the same exclusion context — otherwise it would silently include
                        // the excluded TT move in the re-search.
                        int verifScore = Negamax(pos, depth - 1 - R, beta - 1, beta, ref verifyDummy, ply,
                                                 cutNode: true, excludedMove: excludedMove, pathExtensions: extensionsUsed);
                        _td.NmpSealPly = 0;
                        if (_signal.ShouldStop) return 0;
                        if (verifScore >= beta)
                            return score;
                        // Verification failed: the null-move fail-high is NOT confirmed —
                        // fall through and search this node normally.
                    }
                }
            }
        }

        // Determine TT move if probe provided it
        Move ttMove = default;
        if (hadProbe && !probe.BestMove.IsNone)
        {
            // Optimization: Don't call IsMoveLegal here ($O(N)$ slowdown).
            // MovePicker will validate pseudo-legality (piece existence, etc) efficiently.
            // Full legality (check) is validated when the move is played.
            ttMove = probe.BestMove;
        }

        // Syzygy DTZ root ranking (2026-09-20): when the root is inside the
        // tablebases, IterativeDeepening (thread 0) computed the 50-move-aware DTZ-optimal
        // root move. The WDL-only leaf probe scores every win-preserving move the same flat
        // ±29900, so without this the root keeps whichever move the TT/history happens to
        // order first — the TB-win shuffle. Trying the DTZ-optimal move FIRST lets the
        // existing strict `score > bestValue` selection keep it whenever the search also
        // returns the flat TB score. The MovePicker validates pseudo-legality; a move
        // forbidden by `searchmoves` is filtered by the root loop's restriction check; a
        // repeating move evaluates to a child draw (0) and is replaced by the next winning
        // move. The TT itself is untouched (ordering hint only). Root-only by construction
        // (_rootTablebaseMove is Move.None at every other ply).
        if (isRoot && UseSyzygyDtzRootRanking && !_rootTablebaseMove.IsNone)
        {
            ttMove = _rootTablebaseMove;
        }

        // Internal Iterative Reduction (IIR)
        // Micro riders: the QUALITY GATE also fires when a
        // TT entry exists but is too shallow to trust (`probe.Depth < depth - 4`). the reference's IIR
        // runs at PV **or cut** nodes, so the quality-gate case is additionally admitted at cut
        // nodes (the legacy no-TT-move case stays PV/root-only). Default OFF: the legacy
        // `ttMove.IsNone` gate only.
        bool iirQualityGate = UseMicroRiders && hadProbe && probe.Depth < depth - 4;
        bool iirNodeType = isPvNode || isRoot || (cutNode && iirQualityGate);
        if (UseInternalIterativeReduction && (ttMove.IsNone || iirQualityGate) && depth >= 4 && !inCheck && excludedMove.IsNone && iirNodeType)
        {
             depth--; 
        }

        // Singular Extensions (SE). Gate floor and beta slope are Track 2
        // SPSA levers (defaults = legacy literals).
        //
        // SELECTIVE ENTRY POLICY (`UseSelectiveSingular`,
        // default OFF): pay the exclusion search only where it can matter — depth ≥
        // SelectiveSingularMinDepth (10), a TT entry of at least `depth - 1` (stricter than
        // the legacy `depth - 3`), PV/cut nodes only (`isPvNode || cutNode`, the design reference's
        // `!allNode`), the same bounded non-mate score demand, and a SINGLE output tier (+1,
        // never the double/triple margins). The per-path extension counter is untouched.
        // DEFAULT OFF: the legacy gate and the double/triple tiers run exactly as before.
        int singularGateDepth = UseSelectiveSingular ? SelectiveSingularMinDepth : SingularMinDepth;
        bool singularTtQuality = !UseSelectiveSingular
            || ((isPvNode || cutNode) && probe.Depth >= depth - 1);
        int singularExtension = 0;
        if (UseSingularExtensions && !isRoot && depth >= singularGateDepth && hadProbe && !ttMove.IsNone && excludedMove.IsNone && 
            (probe.Flag == NodeType.Exact || probe.Flag == NodeType.LowerBound) && 
            Math.Abs(probe.Value) < 30000
            && singularTtQuality
            && (UseSelectiveSingular || probe.Depth >= depth - 3)) 
        {
             int singularBeta = probe.Value - (SingularBetaSlope * depth); 
             Move dummy = default;
             int score = Negamax(pos, depth / 2, singularBeta - 1, singularBeta, ref dummy, ply, cutNode: cutNode, excludedMove: ttMove, pathExtensions: extensionsUsed);
             // Extend by 1 when singular, or by 2/3 when singular by a growing margin (double /
             // triple extension). Gated by UseSingularDoubleExtensions. The selective policy
             // deliberately keeps only the single (+1) tier.
             singularExtension = UseSelectiveSingular
                 ? (score < singularBeta ? 1 : 0)
                 : ComputeSingularExtension(
                     score, singularBeta, UseSingularDoubleExtensions,
                     SingularDoubleMargin, SingularTripleMargin);
        }

        // Move ordering via MovePicker
        Move prevPrevMove = verificationSearch ? Move.None : pos.GetMoveFromHistory(2);
        Move prev4PlyMove = verificationSearch ? Move.None : pos.GetMoveFromHistory(4);
        Move prev6PlyMove = verificationSearch ? Move.None : pos.GetMoveFromHistory(6);
        Move counterMove = default;
        int[][]? contHist = null;
        int[][]? contHist2 = null;
        int[][]? contHist4 = null;
        int[][]? contHist6 = null;

        if (!prevMove.IsNone)
        {
             contHist = _td.ContinuationHistory1Ply[pos.SideToMove][prevMove.To];
             counterMove = _td.CounterMoves[prevMove.From, prevMove.To];
        }
        if (!prevPrevMove.IsNone)
        {
             contHist2 = _td.ContinuationHistory2Ply[pos.SideToMove][prevPrevMove.To];
        }
        if (!prev4PlyMove.IsNone)
        {
             contHist4 = _td.ContinuationHistory4Ply[pos.SideToMove][prev4PlyMove.To];
        }
        if (!prev6PlyMove.IsNone)
        {
             contHist6 = _td.ContinuationHistory6Ply[pos.SideToMove][prev6PlyMove.To];
        }

        // ProbCut Pruning - Conservative implementation (depth >= 5, not in check, not PV)
        // Checks if a shallow search can establish a beta cutoff, indicating the position is "too good"
        // appendix: PAWNFORGE-ONLY (the reference dropped ProbCut for this net) —
        // gated by UseProbCut so the next gate session can A/B "the reference's system" (off).
        if (UseProbCut && depth >= 5 && !isPvNode && !inCheck && Math.Abs(beta) < 30000)
        {
            // ProbCut beta with improving heuristic: beta + 235, minus 63 when improving
            int probCutBeta = beta + 235;
            if (ply >= 2 && staticEval > _td.EvalStack[ply - 2])
            {
                probCutBeta -= 63; // Position is improving
            }

            // Only attempt if static eval suggests a cutoff is likely
            if (staticEval >= probCutBeta && (hadProbe == false || probe.Value >= probCutBeta - 1))
            {
                int seeThreshold = probCutBeta - staticEval;
                int probCutDepth = depth - 4;

                // Generate and test capturing moves only (fast verification)
                Span<Move> probGenBuf = _td.MoveBuffers[ply][0];
                Span<ScoredMove> probCapsBuf = _td.ScoredMoveBuffers[ply][0];
                Span<ScoredMove> probQuietsBuf = _td.ScoredMoveBuffers[ply][1];
                Span<Move> probBadBuf = _td.MoveBuffers[ply][1];

                var probPicker = new MovePicker(pos, ttMove, null, null, default, null, null, null, null, _td.CaptureHistory, 
                                               true, UseCapturesOnlyMovegen, UseLazySee, probGenBuf, probCapsBuf, probQuietsBuf, probBadBuf); // onlyCaptures=true

                Move probMove;
                while ((probMove = probPicker.Next()) != Move.None)
                {
                    // SEE filter - skip moves that don't look promising enough
                    if (!pos.SeeGe(probMove, seeThreshold))
                        continue;

                    // Make the move
                    // Get moving piece BEFORE MakePublicMove (board will be modified)
                    Piece probMovingPiece = pos.GetMovingPiece(probMove);
                    var probCaptured = pos.MakePublicMove(probMove);
                    _evaluator.MakeMove(pos, probMove, probMovingPiece, probCaptured);

                    // Check legality
                    if (pos.IsKingAttacked(1 - pos.SideToMove))
                    {
                        _evaluator.UnmakeMove();
                        pos.UnmakePublicMove(probMove, probCaptured);
                        continue;
                    }

                    // 1. Quiescence check first (fast)
                    int cutScore = -Quiescence(pos, -probCutBeta, -probCutBeta + 1, ply + 1);

                    // 2. If qs holds the cut, do reduced depth search (verification)
                    if (cutScore >= probCutBeta)
                    {
                        Move dummy = default;
                        _tt.Prefetch(pos.ZobristKey);
                        // verificationSearch=true prevents the verification search
                        cutScore = -Negamax(pos, probCutDepth, -probCutBeta, -probCutBeta + 1, ref dummy, ply + 1, cutNode: !cutNode, verificationSearch: true, pathExtensions: extensionsUsed);
                    }

                    _evaluator.UnmakeMove();
                    pos.UnmakePublicMove(probMove, probCaptured);

                    if (_signal.ShouldStop)
                        return 0;

                    // If cut holds, return it
                    if (cutScore >= probCutBeta)
                    {
                        // Store in TT for future reference (cache this node's raw static eval too —
                        // Phase 5a; ProbCut is !inCheck-gated so rawStaticEval is always valid here).
                        _tt.Store(pos.ZobristKey, cutScore, probCutDepth, NodeType.LowerBound, probMove, _currentAge, ply, rawStaticEval);
                        return cutScore;
                    }
                }
            }
        }

        // Buffer allocation on stack
        // Use ThreadData buffers (Pre-allocated)
        Span<Move> genBuf = _td.MoveBuffers[ply][0];
        Span<ScoredMove> capsBuf = _td.ScoredMoveBuffers[ply][0];
        Span<ScoredMove> quietsBuf = _td.ScoredMoveBuffers[ply][1]; 
        Span<Move> badBuf = _td.MoveBuffers[ply][1];

        var picker = new MovePicker(pos, ttMove, (ply < 256 ? _killers[ply] : null), _td.ButterflyHistory, counterMove, contHist, contHist2, contHist4, contHist6, _td.CaptureHistory, false, UseCapturesOnlyMovegen, UseLazySee, genBuf, capsBuf, quietsBuf, badBuf);
        
        Move bestMove = default;
        int bestValue = int.MinValue + 1;
        int legalMoves = 0; // Track LEGAL moves only (not pseudo-legal)
        int quietMoveCount = 0; // Track quiet moves only for LMP
        
        // Track quiet moves that fail low for the history penalty
        Span<Move> quietsSearched = stackalloc Move[64];
        int quietsSearchedCount = 0;

        Move m;

        // LMR v2 fixed-point unit, shared by the early v2 block and the legacy block below.
        const int LmrUnit = 1024;

        while ((m = picker.Next()) != Move.None)
        {
            if (m.Equals(excludedMove)) continue;
            // SEE-based early pruning for captures at shallow depths
            // We can delegate this to MovePicker but here we have alpha context
            var occThem = pos.GetOccupancy(1 - pos.SideToMove);
            bool isCapture = m.IsEnPassant || Bitboard.IsSet(occThem, m.To);
            bool isPromotion = m.Promoted != 0;

            // LMR v2 state for THIS move. The combined history score is computed
            // PRE-move (pos.SideToMove is still the mover here — the same side the history
            // tables are indexed by, so no 1-SideToMove flip is needed at this point). It is
            // consumed by the SEE thresholds (below), the LMR v2 history/futile terms, and
            // the / the reference futility/history-pruning formulas. Clamped to the reference's quiet
            // history range (±8033): PawnForge's tables run to ±200000 and every consumer of
            // this value expects the reference's magnitude scale.
            bool lmrV2Handled = UseLateMoveReductions && UseLmrV2;
            int lmrHistoryScore = 0;
            // The combined clamped history score is also
            // the input of the SEE-pruning history refund (the reference's seeThreshold subtracts
            // hist/divisor), so it must be computed whenever ANY of its consumers is live.
            // Pre-change only the LMR-v2 path computed it, which made the history refund
            // silently dead without UseLmrV2 — the reach arm needs it on the live v1
            // schedule. The computation itself is a set of pure reads; nothing consumes it
            // unless a consumer is enabled.
            // `UseLinearFutilityMargin` consumes it too
            // (the reference's `+ hist/98` term in the linear futility margin).
            if (lmrV2Handled || UseRefSeeThresholds || UseSeeReach || UseLinearFutilityMargin)
            {
                if (!isCapture)
                {
                    lmrHistoryScore = _td.ButterflyHistory[pos.SideToMove][m.From][m.To];
                    if (contHist != null) lmrHistoryScore += contHist[m.From][m.To] / 2;
                    if (contHist2 != null) lmrHistoryScore += contHist2[m.From][m.To] / 2;
                    if (contHist4 != null) lmrHistoryScore += contHist4[m.From][m.To] / 2;
                    if (contHist6 != null) lmrHistoryScore += contHist6[m.From][m.To] / 2;
                }
                else
                {
                    // Pre-move victim determination: a normal capture takes whatever stands on
                    // the target square; en passant always removes a pawn.
                    int attackerIdx = PieceToIndex(pos.GetMovingPiece(m));
                    int victimIdx = m.IsEnPassant ? 0 : pos.GetPieceAt(m.To).ToIndex();
                    if (attackerIdx >= 0 && attackerIdx < 6)
                    {
                        lmrHistoryScore = _td.CaptureHistory[attackerIdx][m.To][victimIdx];
                    }
                }
                lmrHistoryScore = Math.Clamp(lmrHistoryScore, -LmrV2HistoryClamp, LmrV2HistoryClamp);
            }

            // ═══════════════════════════════════════════════════════════════════════════════════
            // Move-number accounting order.
            // ═══════════════════════════════════════════════════════════════════════════════════
            // Both reference engines checked for this fix (the design reference's search,
            // the reference search) increment their move-number counter IMMEDIATELY
            // after make-move + legality validation succeeds, and BEFORE any move-loop pruning
            // (futility, LMP, history/SEE-based skip) runs. Pruned moves still occupy a slot in
            // that count, because move-number is meant to represent "how far into the move
            // picker's ordering are we", which is exactly what Late Move Reductions indexes by
            // (the design reference: `LMR[depth, expandedNodes - 1]`; the reference: `lmrReductions[...][moveCount]`).
            //
            // PawnForge previously incremented `legalMoves`/`quietMoveCount` AFTER the four
            // move-loop pruning blocks (SEE, LMP, history, futility) had already had a chance to
            // `continue` past a move. Every move skipped by one of those blocks meant every
            // SUBSEQUENT move at that node was credited with a move-number lower than its real
            // position in the list, so LMR reduced it LESS than intended — a deflated move-number
            // means less reduction, i.e. a deeper (not shallower) search of that subtree. This
            // could make a pruning feature that skips moves still increase total node count, and
            // it silently biased every Phase 2/3 EBF/depth measurement taken while it was present.
            //
            // FIX: only SEE-based pruning stays here, before make-move, because PawnForge's
            // Position.See computes off the PRE-move board (it reads `board[mv.From]` and the
            // pre-move `allOccupancy` and simulates the exchange internally) — unlike the design reference's
            // See1, which is explicitly documented to operate on "the last move" (i.e. AFTER
            // make-move). Rewriting See to be post-move would be a much larger, riskier change
            // touching move ordering and quiescence search as well, and it is UNNECESSARY here:
            // SEE-based pruning only fires at depth <= 2, while Late Move Reductions only fire at
            // depth >= 3 (see the LMR gate below: `depth >= 3`). These ranges never overlap, so
            // SEE's residual pre-move move-counting quirk has ZERO effect on the LMR bug being
            // fixed. LMP, History Pruning, and Futility Pruning all use only pre-move-independent
            // data (isCapture/isPromotion, staticEval, history tables, piece type at m.From) and
            // are moved below, AFTER make-move + legality-check + counter increment, exactly
            // mirroring where the design reference and the reference run their equivalent checks.
            //
            // ADDITIONAL ALIGNMENT: the design reference explicitly exempts the first successfully
            // made move at a node from ALL move-loop pruning via `interesting = ... ||
            // expandedNodes == 1`. We mirror that here: LMP/History/Futility below are gated with
            // `legalMoves > 1` (skip pruning on the first accepted legal move), and the SEE check
            // just below is gated with `legalMoves > 0` (skip pruning while no legal move has been
            // confirmed yet at this node — SEE runs pre-legality-check, so this is the closest
            // equivalent given its earlier position in the loop). This guarantees every node
            // always fully searches at least one candidate move rather than risking an all-pruned
            // fail-low with no real recursive search behind it.
            // ═══════════════════════════════════════════════════════════════════════════════════
            // ═══════════════════════════════════════════════════════════════════════════════
            // SEE-based move-loop pruning (the reference
            // ═══════════════════════════════════════════════════════════════════════════════
            // the reference's thresholds SCALE WITH DEPTH: quiets −43·depth, noisies −26·depth²,
            // relaxed by history/128 (a historically good move gets a narrower pruning
            // window). The tightening thresholds are what make the reference's deep ceiling
            // (ceiling 10) safe — the Phase-6 lesson: never widen a ceiling without
            // porting the threshold formula. With the flat legacy thresholds the effective
            // ceiling is capped at 4 (the Phase-6-validated configuration).
            //
            // UseSeeReach is the reach upgrade the audit
            // ordered: the FULL reference shape — ceiling SeeReachMaxDepth (10),
            // quiets −44·depth, noisies −17·depth², history refund −hist/133 folded into
            // the threshold (the reference. It takes precedence over
            // UseRefSeeThresholds when both are set (the reach values ARE a
            // reference-shaped threshold, with the corrected constants and the refund made
            // live on the v1 schedule). The negative margins are passed straight to
            // Position.SeeGe as a LOSS tolerance — `SeeGe(m, t)` is "SEE(m) >= t", so
            // `t = −44·depth` prunes only moves that lose more than 44·depth; no sign
            // flip at the caller (the reference- sign note).
            int seeCeiling = UseRefSeeThresholds ? SeePruningMaxDepth : Math.Min(SeePruningMaxDepth, 4);
            if (UseSeeReach && SeeReachMaxDepth > seeCeiling) seeCeiling = SeeReachMaxDepth;
            if (depth <= seeCeiling && !inCheck && !isPvNode && legalMoves > 0)
            {
                if (isCapture)
                {
                    int seeThreshold = UseSeeReach
                        ? SeeReachNoisyMargin * depth * depth - lmrHistoryScore / SeeReachHistoryDivisor
                        : (UseRefSeeThresholds
                            ? SeeNoisyMargin * depth * depth - lmrHistoryScore / SeeHistoryDivisor
                            : -50);
                    if (!pos.SeeGe(m, seeThreshold)) continue;
                }
                else if (!isPromotion)
                {
                    // CRITICAL FIX: Do not prune King moves with SEE
                    // SEE might be inaccurate for King safety or we might miss evasions
                    Piece p = pos.Mailbox[m.From];
                    if (p != Piece.WK && p != Piece.BK)
                    {
                        int seeThreshold = UseSeeReach
                            ? SeeReachQuietMargin * depth - lmrHistoryScore / SeeReachHistoryDivisor
                            : (UseRefSeeThresholds
                                ? SeeQuietMargin * depth - lmrHistoryScore / SeeHistoryDivisor
                                : -100);
                        if (!pos.SeeGe(m, seeThreshold)) continue;
                    }
                }
            }

            // Get moving piece BEFORE MakePublicMove (board will be modified)
            Piece movingPiece = pos.GetMovingPiece(m);

            // Capture the SEE verdict for THIS
            // capture BEFORE make-move, for later use by the Negative Extensions block below.
            // Position.See/SeeGe internally reads `board[mv.From]` (the moving piece) and uses
            // `SideToMove` to determine "side to move after our capture" — both assumptions are
            // only valid PRE-move. Every other SeeGe call site in the codebase (QuiescenceSearch,
            // MovePicker, ProbCut, the depth<=2 move-loop pruning above) already calls it
            // pre-move; Negative Extensions was the sole exception, calling it AFTER
            // pos.MakePublicMove(m) had already run — at that point mv.From is empty (silently
            // read as a pawn via PieceToIndex's default case) and SideToMove is already flipped,
            // so the whole exchange simulation ran with the wrong piece value and swapped sides.
            // Fix: evaluate it here, at the only point in the loop where Position's state is
            // guaranteed pre-move, and reuse the cached result after the move is made.
            bool captureLosesSee = isCapture && !pos.SeeGe(m, 0);

            var captured = pos.MakePublicMove(m);
            _evaluator.MakeMove(pos, m, movingPiece, captured);
            
            // CRITICAL FIX: Validate move legality
            // After MakePublicMove, SideToMove has ALREADY FLIPPED
            // So pos.SideToMove now refers to the OPPONENT
            // We need to check if the MOVING SIDE (1 - pos.SideToMove) left king in check
            if (pos.IsKingAttacked(1 - pos.SideToMove))
            {
                _evaluator.UnmakeMove();
                pos.UnmakePublicMove(m, captured);
                continue; // Illegal move - moving side's king is in check
            }
            
            // `go searchmoves`: at the root, skip moves outside the restriction.
            // Placed AFTER the legality check so only legal moves are counted;
            // IterativeDeepening disables the restriction when no legal move
            // matches, so this can never filter out ALL moves.
            if (isRoot && RootMoveRestriction is { Count: > 0 }
                && !RootMoveRestriction.Contains(m.From * 64L + m.To))
            {
                _evaluator.UnmakeMove();
                pos.UnmakePublicMove(m, captured);
                continue;
            }

            // Prefetch the child's transposition-table bucket into CPU L1 cache (Phase 5c,
            // the design reference). The child node probes the TT as its very first action.
            _tt.Prefetch(pos.ZobristKey);
            // ... and the child's eval-cache line, which its static-eval
            // site probes right after the TT. No-op when the cache toggle is off/unallocated.
            if (UseEvalCache) _td.EvalCache?.Prefetch(pos.ZobristKey);

            // Root repetitions: the move leads to a known position
            if (isRoot && pos.GetRepetitionCount() > 0)
            {
                // This move leads to a position we've seen before!
                // Note: We still search it (might be forced), but warn for debugging
                // In production, you might log this to stderr or a debug file
            }

            // CRITICAL: Count only LEGAL moves (after validation).
            // This increment now happens BEFORE the LMP/History/Futility pruning
            // blocks below (previously it ran after all move-loop pruning had already run), so
            // pruned moves still occupy a slot in legalMoves/quietMoveCount — matching the design reference's
            // expandedNodes++ and the reference's moveCount++, both of which run immediately after their
            // make-move + legality check and before their equivalent pruning blocks.
            legalMoves++;

            // Count quiet moves for LMP tracking
            if (!isCapture && !isPromotion)
            {
                quietMoveCount++;
            }

            // Update per-ply stacks for the hindsight reductions
            _td.MoveCountStack[ply] = legalMoves;

            // `info currmove` reporting (main thread, time-throttled).
            // legalMoves is already incremented above, so currmovenumber uses it
            // directly instead of the pre-increment `legalMoves + 1` used previously.
            if (isRoot && _td.ThreadId == 0 && CurrMoveReportIntervalMs >= 0)
            {
                long nowMs = TimeManager.NowMs(); // monotonic (see TimeManager.NowMs)
                if (nowMs - _lastCurrMoveReportMs >= CurrMoveReportIntervalMs)
                {
                    _lastCurrMoveReportMs = nowMs;
                    _onInfo?.Invoke($"info currmove {m} currmovenumber {legalMoves} " +
                                    $"nodes {(_getTotalNodes != null ? _getTotalNodes() : _td.Nodes)}");
                }
            }

            // Does this move give check? (post-make-make: pos.SideToMove is the OPPONENT, so
            // "the opponent's king is attacked" == the mover delivered check.) Computed ONCE
            // here and reused by the LMR gate and the Negative Extensions block below (the
            // pre-restructure code computed it later in the loop; same expression, same board state —
            // zero behavior change, one fewer recomputation).
            bool givesCheck = pos.IsKingAttacked(pos.SideToMove);

            // ═══════════════════════════════════════════════════════════════════════════════
            // LMR v2 EARLY COMPUTATION (restructure, the reference.
            // ═══════════════════════════════════════════════════════════════════════════════
            // the reference computes the LMR reduction BEFORE the move-loop pruning blocks, because
            // those blocks gate on REDUCED depth (`reducedDepth = depth - reduction`): futility
            // and history pruning fire at nominal depths 8-10+ for moves the reduction already
            // sent near the horizon. That coupling is why alone (reductions without the
            // reducedDepth-gated pruning) regressed EBF to 2.326 — measured 2026-09-07. This
            // early block reproduces the reference's ordering for the v2 schedule; the legacy (v1)
            // schedule keeps computing its reduction at its original site further down, so
            // UseLmrV2 = false reproduces the pre-restructure behavior exactly.
            //
            // The combined history score is computed ONCE here (the reference's
            // `scoredMove.historyScore`) and shared by the LMR history term, the LMR-futile
            // margin, the reference's futility margin, and the reference's history pruning. Clamp to the reference's
            // quiet-history range (±8033) — PawnForge's tables run to ±200000, and the whole
            // point of the fixed-point terms is sub-ply modulation, not table-scale blowups.
            int reduction = 0;
            if (lmrV2Handled)
            {
                int lmrMinMoves = (isPvNode ? LmrV2MinPvMoves : LmrV2MinMoves) + (isRoot ? 1 : 0);
                bool lmrReducible = !isPromotion && (!isCapture || (UseLmrNoisyMoves && !ttPv));
                if (depth >= LmrV2MinDepth && legalMoves >= lmrMinMoves && lmrReducible
                    && !inCheck && !givesCheck)
                {
                    // ── BASE: quiet / noisy two-table design, 1024ths of a ply.
                    int r = LmrV2BaseReduction(noisy: isCapture, depth, legalMoves);

                    // All weights below are the reference
                    //(re-verified against the source). The block is
                    // calibrated as a SET — do not ship term-by-term.
                    // ── Term (a): ttPv → reduce LESS (−911 ≈ −0.89 ply).
                    if (UseLmrTermTtPv && ttPv)
                    {
                        r -= LmrV2TtPvWeight;
                    }

                    // ── Term (b): cut node → reduce MORE (+2085 ≈ +2.04 ply).
                    if (UseLmrTermCutNode && cutNode)
                    {
                        r += LmrV2CutNodeWeight;
                    }

                    // ── Term (c): not improving → reduce MORE (+70 ≈ +0.07 ply) — the
                    // CORRECTED Term 1 : modulates instead of dominating.
                    // weight default corrected 275 → 70.
                    if (UseLmrTermNotImproving && !improving)
                    {
                        r += LmrV2NotImprovingWeight;
                    }

                    // ── Term (d): PV distance → reduce MORE (PF-only extra, default OFF).
                    if (UseLmrTermPvDistance)
                    {
                        r += Math.Min(_td.PvDistanceStack[ply] * LmrV2PvDistanceMult, LmrV2PvDistanceMax);
                    }

                    // ── Term (e): history → continuous ± (quiet div 3258, noisy div 3155).
                    if (UseLmrTermHistory)
                    {
                        int histDiv = isCapture ? LmrV2NoisyHistoryDiv : LmrV2QuietHistoryDiv;
                        r -= lmrHistoryScore / histDiv * LmrUnit;
                    }

                    // ── Term (f): "LMR-futile" → reduce MORE (+962 ≈ +0.94 ply): the SOFTER
                    // tier of futility — don't skip the move, search it one extra ply shallower.
                    if (UseLmrTermFutile)
                    {
                        int lmrFutileMargin = LmrV2FutileMargin + depth * LmrV2FutileScale
                                              + lmrHistoryScore / LmrV2FutileHistDiv;
                        if (staticEval + lmrFutileMargin <= alpha)
                        {
                            r += LmrV2FutileWeight;
                        }
                    }

                    // ── Term (g): parent fail-high count → reduce MORE (+1035 ≈ +1.01 ply).
                    if (UseLmrTermFailHighCount && !isRoot && ply > 0 && _td.FailHighCountStack[ply - 1] > 2)
                    {
                        r += LmrV2FailHighCountWeight;
                    }

                    // ── Term (h): complexity → reduce LESS (see the eval region for the
                    // pawn-only deviation note and the divisor derivation).
                    if (UseLmrTermComplexity)
                    {
                        r -= Math.Min(evalComplexity / LmrV2ComplexityDivisor, LmrV2ComplexityCap);
                    }

                    // ── FINAL: divide out the fixed point (Java-int-division truncation, then
                    // floor at 0 — no negative reductions). The upper clamp against newDepth
                    // happens at the search site (newDepth depends on extensions computed
                    // further down the loop).
                    reduction = r / LmrUnit;
                    if (reduction < 0) reduction = 0;
                }
            }

            // REDUCED depth: the effective depth this move will actually be searched at,
            // BEFORE extensions. the reference's move-loop pruning blocks gate on exactly this
            // quantity: `int reducedDepth = depth - reduction;`).
            int reducedDepth = depth - reduction;
            if (reducedDepth < 0) reducedDepth = 0;

            // Late Move Pruning (History Pruning)
            // If quiet move, and we searched enough QUIET moves, and depth is low -> skip
            // Runs AFTER make-move + counter increment (was before), and is
            // additionally gated by `legalMoves > 1` so the first legal move at this node is
            // never pruned (mirrors the design reference's `expandedNodes == 1` exemption).
            if (UseLateMovePruning && !inCheck && !isCapture && !isPromotion && !isPvNode
                && legalMoves > 1)
            {
                if (UseRefLmp || UseFormulaLmp)
                {
                    // ── the reference's LMP (lmp*, ──────────
                    // Linear thresholds against the TOTAL legal-move count (the reference compares his
                    // moveCount, which includes captures): (3 + 38·d)/10 normally,
                    // (1 + 83·d)/10 when "optimistic" (improving or eval ≥ beta — we can afford
                    // to look at more candidate moves when the position trends our way). At
                    // depth ≤ 8 — vs the legacy ceiling of 4.
                    // `UseFormulaLmp` carries exactly this formula (verified constants 38/83); it shares
                    // the path with the legacy toggle `UseRefLmp`, so the two
                    // are behaviourally identical — run ONE as the arm, never both.
                    if (depth <= RefLmpDepth)
                    {
                        bool optimistic = (UseImprovingFlag && improving) || staticEval >= beta;
                        int lmpThreshold = optimistic
                            ? (RefLmpImpBase + RefLmpImpScale * depth) / 10
                            : (RefLmpBase + RefLmpScale * depth) / 10;
                        if (legalMoves >= lmpThreshold)
                        {
                            picker.SkipQuiets();
                            _evaluator.UnmakeMove();
                            pos.UnmakePublicMove(m, captured);
                            continue;
                        }
                    }
                }
                else
                {
                 // LMP limits 3/6/12/24 (depth 1..4)
                 int lmpThreshold = LmpLimitByDepth[Math.Min(depth, LmpLimitByDepth.Length - 1)];
                 // Not improving -> halve (texel's badPrevMove variant 2/4/7/15):
                 // the position is trending down, so spending nodes on many late
                 // quiet tries is less likely to pay off.
                 if (UseImprovingFlag && !improving)
                 {
                     lmpThreshold = Math.Max(1, lmpThreshold / 2);
                     if (lmpThreshold < 1) lmpThreshold = 1;
                 }
                 // Follow-up: quietMoveCount is now incremented BEFORE this check
                 // (inclusive of the current move), matching the design reference's post-increment
                 // `expandedNodes > LMP[depth]` convention. Using `>=` here (as before the
                 // restructuring, when quietMoveCount was still pre-increment) would prune one
                 // quiet move too early — an off-by-one that was caught by MateInFourTests
                 // failing to find a mate that depends on a quiet move at exactly the threshold
                 // boundary. `>` restores the original "keep quiet moves 1..threshold" behavior.
                  if (depth <= LmpMaxDepth && quietMoveCount > lmpThreshold) // Count only quiet moves, not captures
                  {
                      picker.SkipQuiets(); // LMP: stop trying remaining quiets for this node
                      _evaluator.UnmakeMove();
                      pos.UnmakePublicMove(m, captured);
                      continue;
                  }
                }
            }

            // History Pruning: prune quiet moves with very poor history
            if (UseRefHistoryPruning && lmrV2Handled && !isPvNode && !isCapture && !isPromotion
                && !inCheck && legalMoves > 1)
            {
                // ── the reference's history pruning (hp*, ────
                // Threshold scales with the NOMINAL depth (hpMargin·depth + hpOffset =
                // −2366·depth − 1007) but the GATE is REDUCED depth ≤ hpMaxDepth (5): a late
                // quiet move already reduced to reducedDepth ≤ 5 at nominal depth 10 is still
                // history-pruned. The trigger aborts all remaining quiets (the reference's
                // skipQuiets). History score is the combined, clamped v2 score computed above.
                if (reducedDepth <= RefHpMaxDepth
                    && lmrHistoryScore < RefHpMargin * depth + RefHpOffset)
                {
                    picker.SkipQuiets();
                    _evaluator.UnmakeMove();
                    pos.UnmakePublicMove(m, captured);
                    continue;
                }
            }
            else if (depth <= HistPruneMaxDepth && !isPvNode && !isCapture && !isPromotion && !inCheck && legalMoves > 1)
            {
                // This block now runs AFTER MakePublicMove(m), so pos.SideToMove is
                // the OPPONENT (side-to-move flips on every make-move). ButterflyHistory is
                // indexed by the side that MADE the move (see the convention established by
                // UpdateHistory(pos.SideToMove, ...), which is called only after UnmakePublicMove
                // has restored the mover's turn) — so this must read `1 - pos.SideToMove`, not
                // `pos.SideToMove`, to stay consistent with every other ButterflyHistory access.
                int side = 1 - pos.SideToMove;
                int historyScore = _td.ButterflyHistory[side][m.From][m.To];

                // Continuation history bonuses (1-ply, 2-ply, 4-ply, 6-ply - Req 2)
                if (contHist != null)
                {
                    historyScore += contHist[m.From][m.To] / 2;
                }
                if (contHist2 != null)
                {
                    historyScore += contHist2[m.From][m.To] / 2;
                }
                if (contHist4 != null)
                {
                    historyScore += contHist4[m.From][m.To] / 2;
                }
                if (contHist6 != null)
                {
                    historyScore += contHist6[m.From][m.To] / 2;
                }

                // Threshold margin: HistPruneMargin (Track 2 lever, default 2000)
                // keeps depth-3 threshold at -6000 (<= 7500 cap per Req 2.2)
                int historyThreshold = -HistPruneMargin * depth;
                
                if (historyScore < historyThreshold)
                {
                    // ── Skip-fans (the reference ──
                    // When UseSkipFans is ON, the tripped threshold aborts ALL remaining quiet
                    // moves of this node — the already-computed "these quiets are historically
                    // bad" signal is reused for the whole stage instead of being thrown away
                    // and re-derived one move at a time. `picker.SkipQuiets` skips the
                    // remaining QUIET stages only (killers/countermove have already been
                    // emitted; the TT move is stage 0; bad noisies continue through the
                    // SEE-prune path), matching the reference's `movePicker.skipQuiets(true)`.
                    // The default (OFF) keeps the pre-change skip-one behaviour bit-identically.
                    if (UseSkipFans) picker.SkipQuiets();
                    // PRUNE this historically very bad move
                    _evaluator.UnmakeMove();
                    pos.UnmakePublicMove(m, captured);
                    continue;
                }
            }

            // ═══════════════════════════════════════════════════════════════════════════════════
            // Move-Loop Futility Pruning
            // ═══════════════════════════════════════════════════════════════════════════════════
            // Skip quiet non-pawn moves at shallow depths when the static evaluation is significantly
            // below alpha. This pruning addresses positions where the evaluation deficit is so large
            // that ordinary quiet moves (those that don't immediately change material or create forcing
            // threats) cannot realistically close the gap within the remaining search depth.
            //
            // CHESS PRINCIPLE: Quiet moves that preserve the current material balance and pawn
            // structure typically improve the evaluation gradually (10-50 cp per move). When the
            // current position is evaluated at, say, -400 cp and we need to reach 0 cp (alpha) to
            // improve our best line, a quiet knight reposition or king shuffle is almost never the
            // solution—we need a tactical blow (capture, promotion, or critical pawn push). Rather
            // than waste search nodes on moves that won't bridge the gap, we skip them and focus
            // computational effort on more promising branches of the tree.
            //
            // FORMULA: A move is pruned when staticEval + depth × FutilityMargin < alpha
            // - staticEval: Current position's evaluation from our perspective
            // - depth: Remaining search depth (plies to horizon)
            // - FutilityMargin: Centipawn allowance per ply (default 120 cp)
            // - alpha: Lower bound on the score we need to improve our position
            //
            // EXAMPLE: At depth 5, alpha = 0, staticEval = -500, margin = 120:
            // Test: -500 + (5 × 120) = -500 + 600 = +100
            // Since +100 > 0 (alpha), this move would NOT be pruned (within striking distance).
            // But if staticEval were -700: -700 + 600 = -100 < 0 → PRUNE (too far behind).
            //
            // WHY NON-PAWN MOVES ONLY:
            // Pawn moves are excluded from this pruning because they irreversibly alter the position:
            // • Pawns cannot move backward, making every pawn push a permanent commitment
            // • A single pawn move can create a passed pawn, open a critical file, or unlock a piece
            // • Pawn breaks (e.g., e5 in the French Defense) can transform a losing position into
            // a winning attack, even when the static evaluation is deeply negative
            // • Defensive pawn pushes (e.g., h6 preventing back-rank mate threats) may be the only
            // move that holds the position, regardless of the evaluation gap
            //
            // In contrast, piece moves (knight, bishop, rook, queen, king) to non-capturing squares
            // are more often reversible in their strategic impact: a knight that retreats to d2 can
            // later return to f3; a king that shuffles on g1-h1 does not fundamentally change the
            // position's assessment. These are the moves we can safely skip when far below alpha.
            //
            // SAFETY GUARDS (all checked before entering this block):
            // • !inCheck: When in check, every legal move is potentially the only escape; we cannot
            // afford to skip any move without risking missing the sole defensive resource.
            // • !isPvNode: Principal variation nodes (full alpha-beta window) require exhaustive
            // search for accuracy; pruning at PV nodes would risk cutting the best line.
            // • !isCapture: Captures immediately change material and can swing the evaluation by
            // hundreds of centipawns in a single move; they must always be searched.
            // • !isPromotion: Promoting a pawn to a queen (or underpromotion to knight for tactics)
            // is a game-changing event that bypasses normal evaluation logic entirely.
            // • depth <= FutilityMaxDepth: Beyond depth 7-9, the evaluation becomes less reliable
            // (we're further from the horizon where tactics resolve),
            // and the margin formula becomes less accurate.
            // • legalMoves > 1: never prune the first legal move at a node (mirrors
            // the design reference's `expandedNodes == 1` exemption). Guarantees every node
            // fully searches at least one real candidate.
            //
            // DESIGN NOTE: This pruning fires AFTER the make-move operation (moved here as part of
            // so the legal-move counter can increment before this check runs — see
            // the large comment block above the SEE-pruning check), using the static evaluation
            // computed before the move and the piece type captured into `movingPiece` BEFORE
            // MakePublicMove ran (pos.Mailbox[m.From] would now read the square the piece just
            // vacated, which is stale/empty post-move — movingPiece is the correct pre-move value
            // and must be reused here rather than re-reading the mailbox). We do NOT check whether
            // the move gives check (givesCheck) because checking moves are rare enough and
            // high-value enough that the small risk of pruning one is outweighed by the node
            // savings from pruning hundreds of quiet non-checking moves.
            //
            // INTERACTION WITH OTHER PRUNING:
            // • Complements Reverse Futility Pruning (RFP): RFP prunes the entire node when eval
            // is far ABOVE beta (we're already too good). Futility pruning addresses the mirror
            // case: eval is far BELOW alpha (we're too far behind).
            // • Works alongside Late Move Pruning (LMP): LMP prunes based on move count (skip late
            // quiets after we've tried the most promising ones). Futility pruning prunes based on
            // evaluation deficit (skip any quiet that can't close the gap), so a "promising" move
            // by LMP's ordering heuristics can still be futile if the eval gap is insurmountable.
            // • History Pruning (above) prunes historically bad moves at any eval. Futility pruning
            // prunes ALL quiet non-pawn moves when eval is critically low, regardless of history.
            //
            // REFERENCES: This pruning technique is a standard component of modern alpha-beta engines,
            // documented in the Chess Programming Wiki under "Futility Pruning" (move-loop variant)
            // and used across reference engines of all strengths. The principle
            // was formalized in computer chess literature in the early 2000s as part of the family of
            // "forward pruning" techniques that reduce the search tree by skipping moves unlikely to
            // affect the final result.
            // ═══════════════════════════════════════════════════════════════════════════════════
            if (UseFutilityPruning && !inCheck && !isPvNode && !isCapture && !isPromotion
                && legalMoves > 1)
            {
                // Identify pawn moves: pawns are the only piece type we must NOT prune, even when
                // the evaluation is deeply negative, because of their strategic irreversibility.
                // Uses movingPiece (captured pre-move above), NOT pos.Mailbox[m.From] (which is
                // now stale/empty post-move — see the DESIGN NOTE above).
                bool isPawn = (movingPiece == Piece.WP || movingPiece == Piece.BP);

                // : the reference's formula gates on REDUCED depth (depth − LMR
                // reduction) — a late move the reduction already sent near the horizon is
                // futility-pruned even at nominal depth 10. The margin also scales with
                // reducedDepth (82/ply), shrinks as more moves are searched (−4/move), and
                // widens with the move's history (+hist/98). Legacy path: the flat
                // 96 + 68·depth + 4·moves formula at NOMINAL depth ≤ FutilityMaxDepth.
                bool useRefFutility = UseRefFutilityFormula && lmrV2Handled;
                bool futilityGate = useRefFutility
                    ? (UseFutilityReducedDepthGate
                          ? reducedDepth <= RefFpDepth                       // deep gate
                          : depth <= RefFpDepth)                             // nominal-depth gate (tactical-safe)
                    : depth <= FutilityMaxDepth;
                if (futilityGate)
                {
                    // Apply the futility condition: if this is NOT a pawn move, and the static
                    // evaluation plus the scaled margin is still below alpha, skip this move
                    // entirely (the reference formula: 108 + 82·reducedDepth + hist/98 − 4·moves,
                    //.
                    // `UseLinearFutilityMargin` (default OFF)
                    // replaces the texel step cells with the SAME linear shape on the LIVE
                    // path, but on NOMINAL depth and with the legacy `< alpha` comparator:
                    // 108 + 82·depth + hist/98 − 4·legalMoves (the reference's
                    // fpMargin/fpScale/fpHistDivisor/fpMoveMultiplier values). The linear
                    // form extends reach inside the shipped `depth ≤ FutilityMaxDepth(7)` gate
                    // (≈682 cp at d7 vs the texel 334 cap) and lets a good quiet widen the
                    // tolerance. This is a MARGIN-SHAPE arm, not the reducedDepth-gated 
                    // behaviour (UseRefFutilityFormula).
                    int futilityMargin = useRefFutility
                        ? FutilityMargin + FutilityScale * reducedDepth
                          + lmrHistoryScore / FutilityHistDiv - legalMoves * FutilityMoveMult
                        : UseLinearFutilityMargin
                            ? FutilityMargin + FutilityScale * depth
                              + lmrHistoryScore / FutilityHistDiv - legalMoves * FutilityMoveMult
                            : FutMarginByDepth[Math.Min(depth, FutMarginByDepth.Length - 1)]; // per-depth margins (61/144/268/334)
                    bool futilityTriggers = useRefFutility
                        ? staticEval + futilityMargin <= alpha   // fail-high gate
                        : staticEval + futilityMargin < alpha;   // legacy: <
                    if (!isPawn && futilityTriggers)
                    {
                        picker.SkipQuiets(); // LMP: stop trying remaining quiets for this node
                        // Skip this move: too far below alpha to matter
                        _evaluator.UnmakeMove();
                        pos.UnmakePublicMove(m, captured);
                        continue;
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // Bad Noisy Pruning (the reference
            // ═══════════════════════════════════════════════════════════════════════════════
            // When the static eval plus margin says even a WINNING capture cannot lift the
            // score to alpha, the FIRST bad-noisy move (SEE < 0) breaks out of the move loop
            // entirely — killing all remaining captures at once instead of trying each one.
            // Fail-soft bookkeeping: the margin becomes the node's score floor so the TT
            // still stores a meaningful UpperBound value. In PawnForge's staged picker the
            // bad captures are served last, so the first bad noisy encountered IS the start
            // of the bad-capture stage — the break semantics match the reference's exactly.
            // `captureLosesSee` is the pre-move SEE verdict cached for every capture (
            // fix infrastructure), so this costs nothing extra.
            if (UseBadNoisyPruning && !inCheck && depth < BnpDepth
                && isCapture && captureLosesSee)
            {
                int bnpMargin = staticEval + BnpScale * depth + BnpOffset * legalMoves / BnpDivisor;
                if (bnpMargin <= alpha)
                {
                    if (Math.Abs(bestValue) < 30000 && bestValue <= bnpMargin)
                    {
                        bestValue = bnpMargin; // fail-soft floor (the reference sets bestScore = margin)
                    }
                    _evaluator.UnmakeMove();
                    pos.UnmakePublicMove(m, captured);
                    break; // break, not continue — no remaining move can save this node
                }
            }

            Move childPv = default;
            int score;
            
            // Calculate extension for this move
            int extension = 0;
            if (m.Equals(ttMove)) extension = singularExtension;

            // (givesCheck was computed once EARLY in the loop — see the LMR v2 early block.)

            // Negative Extensions: reduce depth for suspicious moves
            // Reduce depth for moves that look suspicious (bad SEE or bad history)
            // de-stacking: gated behind UseNegativeExtensions (default ON = current behavior;
            // SPRT arm turns it OFF to recover the depth/NPS spent on this PF-only layer).
            if (UseNegativeExtensions && depth >= 3 && !isPvNode && !inCheck)
            {
                if (!givesCheck)
                {
                    if (isCapture)
                    {
                        // Reduce depth for bad captures (SEE < 0).
                        // Uses the pre-move-computed `captureLosesSee` (cached above,
                        // before pos.MakePublicMove(m) ran) instead of re-calling pos.SeeGe(m, 0)
                        // here, which would evaluate SEE on a POST-move board — see the FIX note
                        // at the `captureLosesSee` declaration for why that was wrong.
                        if (captureLosesSee)
                        {
                            extension -= 1;
                        }
                    }
                    else if (!isPromotion)
                    {
                        // For quiet moves, check history
                        // This code runs AFTER
                        // pos.MakePublicMove(m), so pos.SideToMove is the OPPONENT, not the side
                        // that played m. Must use `1 - pos.SideToMove` (the mover's side) to read
                        // the correct ButterflyHistory table — same defect as LMR Term 3 above.
                        int side = 1 - pos.SideToMove;
                        // HIST1 (2026-09-10): threshold recalibrated for the gravity-bounded
                        // ±8033 scale (was −5000 on the ±200,000 scale = 2.5 % of cap; −3000
                        // ≈ 37 % of the new cap — a genuinely bad quiet under the linear
                        // 200·d malus).
                        if (_td.ButterflyHistory[side][m.From][m.To] < -3000)
                        {
                            extension -= 1; // Reduce depth for historically bad move
                        }
                    }
                }
            }
            
            // Recapture Extension: capturing on the square the opponent JUST
            // captured on (or just moved to) resolves the full exchange before
            // the horizon truncates it. Math.Max keeps the +1 alive even when
            // a negative extension fired for the same move.
            if (UseRecaptureExtension && isCapture && !prevMove.IsNone && m.To == prevMove.To)
            {
                extension = Math.Max(extension, 1);
            }

            int newDepth = depth - 1 + extension;

            // LMR v2 upper clamp : the early computation clamps the reduction only
            // at 0 because newDepth (which includes the singular/negative extensions) is not
            // known yet at that point. Now that newDepth is final, never reduce below depth 0.
            if (lmrV2Handled && reduction > newDepth)
            {
                reduction = newDepth;
            }

            // ═══════════════════════════════════════════════════════════════════════════════════
            // LMR (Late Move Reductions) — Phase 3: Aggressive Reduction with Correctly-Signed Terms
            // ═══════════════════════════════════════════════════════════════════════════════════
            // Late Move Reductions are the primary mechanism for controlling effective branching
            // factor (EBF). By searching late moves (those ordered after the most promising captures,
            // killers, and high-history quiets) at reduced depth, we can explore a wider tree without
            // proportionally increasing the node count. If a reduced move unexpectedly raises alpha,
            // we re-search it at full depth to verify the result.
            //
            // CHESS PRINCIPLE: Move ordering heuristics (TT move, MVV-LVA, killers, history) place
            // the most likely best move first. By the time we reach move #10, #15, #20 in a quiet
            // position, the probability that one of these late moves is actually best drops sharply.
            // Rather than search every late move at full depth (which explodes the tree), we reduce
            // them by R plies, effectively saying "if this move is good enough to raise alpha, we'll
            // notice even at reduced depth and re-search; if it's not, we save R plies worth of nodes."
            //
            // THE CORRECTLY-SIGNED TERMS PRINCIPLE:
            // The base reduction (ln(depth) × ln(moveNumber)) is aggressive. We then ADD or SUBTRACT
            // based on signals that predict whether this move is likely to be better or worse than
            // the current alpha:
            // - REDUCE MORE (+reduction) when: position is NOT improving, opponent is weak,
            // we're at a predicted cut node (only need one refutation), history is bad
            // - REDUCE LESS (−reduction) when: move gives check (forcing), move is a killer or
            // counter (historically good), we're at a PV node (need full accuracy), history is good
            //
            // The key insight from the Phase 2 analysis: we must reduce MORE much more often than
            // we reduce LESS. If "reduce less" terms fire 40-50% of the time (e.g., all improving
            // positions), they cancel the base reduction and raise EBF. The correct pattern is:
            // - "Reduce MORE" terms fire 30-50% of the time (not improving, low opponent material)
            // - "Reduce LESS" terms fire 5-15% of the time (checks, PV nodes, killers)
            //
            // FORMULA: reduction = base + modulating_terms, where:
            // base = ln(depth) × ln(moveNumber) / 1.75 (the aggressive ln×ln table)
            // modulating_terms = sum of ±adjustments based on position characteristics
            //
            // FIXED-POINT ARITHMETIC (1024-unit accumulator):
            // All reductions are computed in 1024ths of a ply to enable sub-ply precision. A term
            // like "reduce 0.3 ply less at PV nodes" would round to 0 in integer math, but is
            // exactly 307/1024 in fixed-point. This allows fine-grained tuning of reduction strength.
            //
            // RE-SEARCH LADDER:
            // When a reduced move raises alpha (score > alpha), we perform up to two re-searches:
            // 1. Zero-window re-search at full depth (or slightly shaved depth for large reductions)
            // 2. If still above alpha and below beta, full-window PV re-search
            // This ensures we never miss a critical move due to the reduction, while still saving
            // nodes on the ~90%+ of late moves that don't raise alpha.
            // ═══════════════════════════════════════════════════════════════════════════════════
            // LMR v1 was computed EARLY (the reference ordering — see the early block above),
            // because the move-loop pruning blocks gate on reducedDepth. Only the LEGACY
            // (v1) schedule computes its reduction here, exactly as before.
            // Gate floors are Track 2 SPSA levers (defaults = legacy literals).
            //
            // BAD-CAPTURE DECOUPLING: `UseLmrNoisyMoves`
            // used to live only inside the v2 early block, so with the shipped
            // `UseLmrV2=false` it was dead code (the Bug-116 coupling class). It is now
            // also honoured on the LIVE v1 schedule: captures with `SeeGe(m, 0) == false`
            // (bad noisies — neither reference excludes them from LMR) become reducible,
            // at non-ttPv nodes only (the reference's `!isGoodNoisy || !ttPv`, taking the
            // conservative bad-noisy-only half rather than treating every capture as
            // good-noisy). Their base reduction comes from the NOISY table
            // (`LmrV2BaseReduction(noisy: true, ...)`, already in 1024ths — the reference's
            // two-table design); the v1 quiet base stays the ln×ln table. Promotions
            // remain excluded. The v1 history term is skipped for the noisy leg: it is a
            // QUIET-history term (butterfly + continuation), meaningless for captures.
            bool lmrV1NoisyCapture = UseLmrNoisyMoves && isCapture && captureLosesSee && !ttPv;
            if (!lmrV2Handled && UseLateMoveReductions && depth >= LmrMinDepth && legalMoves > LmrMinMoves
                && (!isCapture || lmrV1NoisyCapture) && !isPromotion && !inCheck && !givesCheck)
            {
                 // Fixed-point arithmetic: accumulate reduction in 1024ths of a ply, divide at end.
                 // (LmrUnit is declared once above, shared with the v2 block.)
                 int reduction1024;

                 // ───────────────────────────────────────────────────────────────────────────────
                 // BASE REDUCTION: Aggressive ln×ln schedule
                 // ───────────────────────────────────────────────────────────────────────────────
                 // At depth 20, move #16: ln(20) × ln(16) / 1.75 ≈ 3.0 × 2.8 / 1.75 ≈ 4.8 plies
                 // This is significantly more aggressive than the legacy formula (1 + 16/8 = 3 plies).
                 // The aggressive base is the foundation; modulating terms adjust from there.
                 if (UseLmrTable)
                 {
                     // [ ] quiet leg: the v1 ln·ln table (whole plies → 1024ths);
                     // noisy leg: the noisy base table (already in 1024ths).
                     reduction1024 = lmrV1NoisyCapture
                         ? LmrV2BaseReduction(noisy: true, depth, legalMoves)
                         : LmrBaseReduction(depth, legalMoves) * LmrUnit;
                 }
                 else
                 {
                     // Legacy base formula (exact pre-Phase-3 fallback), scaled into 1024ths.
                     // Kept for A/B testing and as a conservative fallback.
                     int legacy = 1 + (legalMoves / 8);
                     if (depth >= 6) legacy++;
                     reduction1024 = legacy * LmrUnit;
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // PHASE 3 RE-INVESTIGATION (2026-09-06): Term 1 re-enabled for retest.
                 // Original finding (pre-Bug-10/11/12): Conservative futility (5/100) + legacy
                 // LMR (1+moves/8): 282/300 WAC. + All 7 modulating terms: 279/300 WAC (FAIL).
                 // Term 1 alone was the only term with measurable depth/EBF impact, but was
                 // disabled alongside the others out of caution. Since then, three real bugs in
                 // this exact code region were found and fixed: (move-number miscounting
                 // affecting the base LMR reduction every term modulates), (Term 3's
                 // ButterflyHistory read used the wrong side post-move — Term 1 does not touch
                 // ButterflyHistory, so this doesn't directly explain a Term-1 regression, but
                 // it confirms this code region had real defects contaminating prior WAC runs),
                 // and (Negative Extensions' SEE call corrupted by a post-move board).
                 // The move generator was also rewritten (check-aware masks). Re-testing Term 1
                 // in isolation now that the surrounding code is verified correct.
                 // ───────────────────────────────────────────────────────────────────────────────
                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 1: NOT Improving → Reduce MORE (+1 ply)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // If the current position's static evaluation is NOT better than it was 2 plies ago
                 // (same side to move both times), the position is trending down or stagnant. Late
                 // quiet moves in deteriorating positions are less likely to be critical than in
                 // improving ones. We can afford to reduce them more aggressively.
                 //
                 // This fires ~40-50% of the time (when position is not improving), which is the
                 // RIGHT frequency for a "reduce MORE" term. Compare to the failed attempt in the
                 // consolidated audit (b) which tried "−1 for improving" (reduce LESS), firing
                 // ~50% of the time and canceling the base reduction.
                 //
                 // Example: Position eval 2 plies ago was +50 cp, now it's +30 cp → improving = false
                 // → we're losing ground, so late moves are less likely to turn things around.
                 if (UseTerm1NotImproving && !improving)
                 {
                     reduction1024 += 1 * LmrUnit; // +1.0 ply
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 2: Opponent Has Few Pieces → Reduce MORE (+1 ply)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // When the opponent has 4 or fewer pieces (approaching a won endgame), late moves
                 // in the position are rarely game-changing. The winning side typically has a large
                 // material advantage and multiple winning continuations; the exact move order matters
                 // less. We can reduce late moves more aggressively in these positions because even
                 // if we miss a "best" move, the evaluation will still show a winning advantage.
                 //
                 // This fires ~10-20% of the time (in won endgames), which is appropriate for a
                 // "reduce MORE" term.
                 //
                 // Design note: We count all opponent pieces (not just non-pawns) to catch positions
                 // like K+2P vs K+P where the side to move is winning but needs to avoid stalemate
                 // tricks. Even here, late quiet moves are less critical than forcing moves.
                 int opponentPieceCount = BitOperations.PopCount(pos.Occupancy[1 - pos.SideToMove]);
                 if (opponentPieceCount <= 4)
                 {
                     reduction1024 += 1 * LmrUnit; // +1.0 ply
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 3: History Score → Continuous adjustment (±history/8192 plies)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // Butterfly history tracks which [from][to] moves have caused beta cutoffs in
                 // previous searches. A move with high history (e.g., +10000) has frequently been
                 // good; a move with low history (e.g., −5000) has frequently failed low.
                 //
                 // The divisor 8192 is chosen so that typical history scores (±5000 to ±15000) map
                 // to ±0.6 to ±1.8 plies of reduction adjustment. This makes history a significant
                 // but not overwhelming term — it fine-tunes the reduction around the base rather
                 // than dominating it.
                 //
                 // Good history (positive) → reduce LESS (subtract from reduction)
                 // Bad history (negative) → reduce MORE (adds to reduction via the negative sign)
                 //
                 // This term fires on 100% of quiet moves (every move has a history score), but its
                 // MAGNITUDE is continuous and typically small (±0.5 to ±1.5 plies). The net effect
                 // across the tree is roughly neutral (good moves reduce less, bad moves reduce more),
                 // which is the correct behavior for a fine-tuning term.
                 // This code runs AFTER
                 // pos.MakePublicMove(m), so pos.SideToMove is the OPPONENT, not the side that
                 // played m. ButterflyHistory is indexed by the side that MADE the move (see the
                 // write-side convention: UpdateHistory(pos.SideToMove, ...) is called only after
                 // UnmakePublicMove restores the mover's turn, and MovePicker.ScoreQuiet reads
                 // _pos.SideToMove pre-move, i.e. also the mover's side). Must use
                 // `1 - pos.SideToMove` here, not `pos.SideToMove`, or this term reads the
                 // opponent's history table instead of the mover's.
                 // HIST1 (2026-09-10): continuous,
                 // clamped, COMBINED history term replacing the old whole-ply
                 // `butterfly/8192` (butterfly-only, unclamped — up to ±24 plies at the
                 // old table cap). Combined quiet score = butterfly + cont/2 ×3 on the
                 // new gravity-bounded ±8k scale (same formula as the v2 path);
                 // max contribution 8033/3258 ≈ ±2.47 plies by construction.
                 // Runs AFTER MakePublicMove, so
                 // pos.SideToMove is the OPPONENT; butterfly is indexed by the MOVER's
                 // side → `1 - pos.SideToMove`. The contHist slices were fetched
                 // pre-make (mover's slices), so their references are already correct.
                 // [ ] the noisy leg skips this term: butterfly + continuation
                 // history is a QUIET-move signal (captures never write it).
                 if (!lmrV1NoisyCapture)
                 {
                     int side = 1 - pos.SideToMove;
                     int historyScore = _td.ButterflyHistory[side][m.From][m.To];
                     if (contHist != null) historyScore += contHist[m.From][m.To] / 2;
                     if (contHist2 != null) historyScore += contHist2[m.From][m.To] / 2;
                     if (contHist4 != null) historyScore += contHist4[m.From][m.To] / 2;
                     if (contHist6 != null) historyScore += contHist6[m.From][m.To] / 2;
                     // `UseLmrHistoryPrecision` switches
                     // the term to multiply-before-divide (sub-ply precision); OFF keeps the
                     // legacy divide-first truncation bit-identically.
                     reduction1024 -= SearchHeuristics.LmrHistoryTerm1024(
                         historyScore, quiet: true, precise: UseLmrHistoryPrecision);
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 4: Cut Node → Reduce MORE (+1 ply)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // A "cut node" is a node where we predict a beta cutoff (fail high). At cut nodes,
                 // we only need to find ONE move that refutes the position (beats beta); we don't
                 // need to find the absolute best move. This makes late moves at cut nodes even
                 // less critical than usual — if the first few moves didn't cut, the late ones are
                 // unlikely to either.
                 //
                 // This fires ~30-40% of the time (the cutNode flag is propagated through the tree
                 // via the PVS framework), which is appropriate for a "reduce MORE" term.
                 //
                 // Design note: Cut-node prediction is heuristic (based on the parent node's search
                 // behavior), so it's not always correct. But statistically, nodes flagged as cut
                 // nodes do fail high more often than random nodes, making this a useful signal.
                 //
                 // PHASE 3c: UseCutNode currently disabled during investigation, so this term won't fire
                 if (UseCutNode && cutNode)
                 {
                     reduction1024 += 1 * LmrUnit; // +1.0 ply
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 5: PV Node → Reduce LESS (−1 ply)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // A PV (Principal Variation) node is a node searched with a full [alpha, beta]
                 // window (not a zero-window scout). These are rare — typically 1-5% of all nodes
                 // in the tree. PV nodes represent the main line of play (the expected best
                 // continuation from the current position), so they require higher search accuracy.
                 //
                 // This fires ~1-5% of the time (only at PV nodes), which is the RIGHT frequency
                 // for a "reduce LESS" term. Compare to the failed "−1 for improving" term which
                 // fired ~50% of the time and canceled the base reduction.
                 //
                 // Design note: We check isPvNode (beta > alpha + 1), not isRoot, because root
                 // nodes are always PV but not all PV nodes are root. The PV extends several plies
                 // deep into the tree.
                 if (isPvNode)
                 {
                     reduction1024 -= 1 * LmrUnit; // −1.0 ply
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 6: Checking Move → Reduce LESS (−1 ply)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // A move that gives check (the opponent's king is in check after the move) is
                 // forcing — the opponent MUST respond to the check, which limits their options.
                 // Forcing moves are more likely to be critical than quiet non-checking moves, even
                 // if they appear late in the move order.
                 //
                 // This check is performed BEFORE the move (via Position.GivesCheck, a make-free
                 // oracle verified against 210k positions). We guard the LMR block with !givesCheck
                 // at the top, but this term is included here for completeness and documentation.
                 //
                 // In practice, this term never fires because givesCheck moves are excluded from
                 // the LMR block entirely. It's listed here to document the design: IF we later
                 // allow checking moves into LMR (with a separate depth-scaled reduction), this
                 // term would ensure they're reduced less than non-checking moves.
                 //
                 // Frequency: ~5-10% of quiet moves give check, which is appropriate for a "reduce
                 // LESS" term. However, the current implementation excludes these moves from LMR
                 // entirely, so this term is dormant.
                 // if (givesCheck) { reduction1024 -= 1 * LmrUnit; } // −1.0 ply (DORMANT: excluded)

                 // ───────────────────────────────────────────────────────────────────────────────
                 // MODULATING TERM 7: Killer or Counter Move → Reduce LESS (−1 ply)
                 // ───────────────────────────────────────────────────────────────────────────────
                 // Killer moves (moves that caused beta cutoffs at the same depth in sibling nodes)
                 // and counter moves (the best response to the opponent's last move) are stored
                 // heuristics that help move ordering. If a move matches a killer or counter, it's
                 // more likely to be strong than a random late move, even if its history score is
                 // not exceptional.
                 //
                 // This fires ~2-4 times per node (4 killer slots + 1 counter move), which is
                 // appropriate for a "reduce LESS" term (total ~5-10% of quiet moves).
                 //
                 // Design note: We check against the pre-extracted killers and counterMove. This
                 // is a simple equality check (Move.Equals), not a contains-scan, so the overhead
                 // is negligible.
                 bool isKillerOrCounter = false;
                 if (ply < 256) // Killers are indexed by ply
                 {
                     var killers = _killers[ply];
                     for (int i = 0; i < killers.Length; i++)
                     {
                         if (m.Equals(killers[i]))
                         {
                             isKillerOrCounter = true;
                             break;
                         }
                     }
                 }
                 if (!isKillerOrCounter && !counterMove.IsNone && m.Equals(counterMove))
                 {
                     isKillerOrCounter = true;
                 }

                 if (isKillerOrCounter)
                 {
                     reduction1024 -= 1 * LmrUnit; // −1.0 ply
                 }

                 // ───────────────────────────────────────────────────────────────────────────────
                 // FINAL REDUCTION: Convert from 1024ths back to whole plies and apply bounds
                 // ───────────────────────────────────────────────────────────────────────────────
                 // Divide the accumulated fixed-point reduction by LmrUnit to get whole plies.
                 // Truncation toward zero matches standard integer division behavior.
                 //
                 // Bounds:
                 // - reduction cannot exceed newDepth (can't reduce below depth 0)
                 // - reduction cannot be negative (negative values are clamped to 0)
                 //
                 // Typical reductions at depth 20, move #16:
                 // Base (ln×ln): 4.8 plies
                 // + not improving: +1.0 → 5.8 plies
                 // + opponent weak: +1.0 → 6.8 plies
                 // + cut node: +1.0 → 7.8 plies
                 // − good history (e.g., +8192): −1.0 → 6.8 plies
                 // Net: 6-8 ply reduction for a late move at depth 20 in a won position.
                 reduction = reduction1024 / LmrUnit;

                 // Cap reduction to never exceed the remaining depth (floor at depth 0)
                 if (reduction > newDepth) reduction = newDepth;

                 // `UseLmrReductionFloor` keeps a reduced
                 // v1 child at >= 2 plies (the design reference's `min(reduction, depth - 2)` table
                 // floor): a move the reduction would otherwise hand to quiescence at ply 0
                 // or 1 is searched deeper. DEFAULT OFF: the legacy clamp above is final.
                 if (UseLmrReductionFloor)
                 {
                     int reductionFloor = newDepth - 2;
                     if (reductionFloor < 0) reductionFloor = 0;
                     if (reduction > reductionFloor) reduction = reductionFloor;
                 }
                 
                 // Clamp negative reductions to 0 (no "negative reduction" = extension)
                 if (reduction < 0) reduction = 0;
            }
            // ═══════════════════════════════════════════════════════════════════════════════════
            // End of LMR block
            // ═══════════════════════════════════════════════════════════════════════════════════
            
            // Update stacks for hindsight
            _td.ReductionStack[ply] = reduction;
            _td.EvalStack[ply] = staticEval;

            // Root node accounting for the stable-TM node
            // fraction: attribute this move's whole subtree (including re-searches) to its
            // [from,to] slot (the reference's td.addNodes). Root only — that is the tree the time
            // manager reads.
            long nodesBeforeMove = isRoot ? _td.Nodes : 0;

            // PVS (Principal Variation Search)
            if (legalMoves == 1) // First LEGAL move
            {
                // First move: a PV child at PV nodes, otherwise the opposite node type.
                score = -Negamax(pos, newDepth, -beta, -alpha, ref childPv, ply + 1, cutNode: isPvNode ? false : !cutNode, pathExtensions: extensionsUsed);
            }
            else
            {
                // Research with reduction?
                // Try Zero Window with reduction
                // Reduced scout of a late move: treated as an expected cut node.
                score = -Negamax(pos, newDepth - reduction, -alpha - 1, -alpha, ref childPv, ply + 1, cutNode: true, pathExtensions: extensionsUsed);

                // If fail high (better than alpha) but we reduced, re-search. The re-search
                // depth is either STATIC (shaved by ComputeReSearchDepth for large reductions,
                // the pre-restructure behavior) or ADAPTIVE (the reference: ±1 ply
                // depending on how the reduced score compares to the best-so-far score.
                if (score > alpha && reduction > 0)
                {
                     int reSearchDepth;
                     if (UseAdaptiveReSearch)
                     {
                         // ── : score-adaptive depth adjustment.
                         // doDeeper: the reduced search rocketed above everything seen so far
                         // (score > bestValue + 37 + 5·newDepth) — the move deserves an extra
                         // ply of verification. doShallower: the fail-high is unimpressive
                         // (score < bestValue + newDepth) — save a ply. Floor: the re-search is
                         // never shallower than the reduced search it improves on.
                         bool doDeeperSearch = score > bestValue + LmrV2DeeperBase + LmrV2DeeperScale * newDepth;
                         bool doShallowerSearch = score < bestValue + newDepth;
                         reSearchDepth = newDepth + (doDeeperSearch ? 1 : 0) - (doShallowerSearch ? 1 : 0);
                         int minDepth = newDepth - reduction;
                         if (reSearchDepth < minDepth) reSearchDepth = minDepth;
                     }
                     else
                     {
                         reSearchDepth = ComputeReSearchDepth(newDepth, reduction, UseAspirationReSearchReduction);
                     }
                     score = -Negamax(pos, reSearchDepth, -alpha - 1, -alpha, ref childPv, ply + 1, cutNode: !cutNode, pathExtensions: extensionsUsed);
                }

                // Re-search if failed high and we missed PV (and not just a beta cutoff)
                if (score > alpha && score < beta)
                {
                     // Full-window re-search establishes the PV: a PV child (never a cut node).
                     score = -Negamax(pos, newDepth, -beta, -alpha, ref childPv, ply + 1, cutNode: false, pathExtensions: extensionsUsed);
                }
            }

            _evaluator.UnmakeMove();
            pos.UnmakePublicMove(m, captured);

            if (isRoot)
            {
                _td.NodesPerMove[m.From, m.To] += _td.Nodes - nodesBeforeMove;
            }

            if (TraceEnabled && ply == 0)
            {
                Console.Error.WriteLine(
                    $"[trace] move {m.From}->{m.To} score={score} alpha={alpha} beta={beta} " +
                    $"bestValue={bestValue} legalMoves={legalMoves} newDepth={newDepth} reduction={reduction}");
            }

            if (_signal.ShouldStop) return 0;

            if (score > bestValue)
            {
                bestValue = score;
                bestMove = m;
                pvMove = m;
            }

            if (score > alpha)
            {
                alpha = score;
                if (alpha >= beta)
                {
                    if (SearchDiagnostics.Enabled) _td.Stats.RecordCutoff(legalMoves - 1);
                    // Beta Cutoff
                    // LMR v2 term (g) bookkeeping (the reference: count the
                    // beta cutoff at this node's stack slot. Because a cutoff breaks the loop,
                    // the counter accumulates ACROSS visits that reuse this slot until the
                    // grandparent's entry resets it two plies back — the "cutoff-hot zone"
                    // signal the fail-high-count LMR term consumes at children.
                    _td.FailHighCountStack[ply]++;
                    if (!isCapture)
                    {
                        // Update Killers
                        UpdateKillers(m, ply);
                        // Update History
                        UpdateHistory(pos.SideToMove, m, depth, quietsSearched.Slice(0, quietsSearchedCount));
                        
                        // Update Counter & Continuation history on a quiet beta cutoff.
                        //
                        // Continuation history rewards the cutoff move CONDITIONED on a move
                        // played N plies earlier (the "prev-to-current" pairing). PawnForge reads
                        // FOUR ply-distances — 1, 2, 4, 6 — in both move ordering
                        // (MovePicker.ScoreQuiet) and history pruning. All four MUST therefore be
                        // written here, or a read table stays permanently ~0 and is pure overhead
                        // (a wasted read plus a wasted x7/8 aging pass over a [2][64][64][64]
                        // array every iteration). Prior to this fix only the 1-ply and 2-ply
                        // tables were updated; the 4-ply and 6-ply tables were read + aged but
                        // never populated (see docs). Each table is guarded by its own
                        // "was there a move at that ply distance?" flag (the contHistN slice is
                        // non-null exactly when prevNPlyMove existed).
                        if (!prevMove.IsNone)
                        {
                            _td.CounterMoves[prevMove.From, prevMove.To] = m;
                            if (contHist != null)
                            {
                                 UpdateContHistory(contHist, m, depth);
                            }
                        }
                        if (!prevPrevMove.IsNone && contHist2 != null)
                        {
                            UpdateContHistory(contHist2, m, depth);
                        }
                        if (contHist4 != null)
                        {
                            UpdateContHistory(contHist4, m, depth);
                        }
                        if (contHist6 != null)
                        {
                            UpdateContHistory(contHist6, m, depth);
                        }

                        // HIST1 (2026-09-10): continuation-history MALUS for the failed-low
                        // quiets (was bonus-only). The tables are indexed by THIS node's
                        // previous-move context, which is shared by every move searched here,
                        // so the same table slices apply — only [From][To] differ. Gravity
                        // bounds each entry; the malus is what keeps the tables discriminating.
                        int contMalusDepth = depth;
                        for (int i = 0; i < quietsSearchedCount; i++)
                        {
                            Move bad = quietsSearched[i];
                            if (contHist != null) UpdateContHistory(contHist, bad, contMalusDepth, bonus: false);
                            if (!prevPrevMove.IsNone && contHist2 != null) UpdateContHistory(contHist2, bad, contMalusDepth, bonus: false);
                            if (contHist4 != null) UpdateContHistory(contHist4, bad, contMalusDepth, bonus: false);
                            if (contHist6 != null) UpdateContHistory(contHist6, bad, contMalusDepth, bonus: false);
                        }
                    }
                    else
                    {
                        // Update Capture History on beta cutoff
                        var piece = pos.GetPieceAt(m.From);
                        if (piece != Piece.Empty)
                        {
                            int pieceIdx = PieceToIndex(piece);
                            var capturedPiece = pos.GetPieceAt(m.To);
                            int capturedIdx = capturedPiece.ToIndex();

                            if (pieceIdx >= 0 && pieceIdx < 6 && capturedIdx >= 0 && capturedIdx < 6)
                            {
                                // HIST1: gravity-bounded linear bonus (was += d², cap 200000)
                                UpdateCaptureHistory(pieceIdx, m.To, capturedIdx, depth);
                            }
                        }
                    }
                    break; 
                }

                // A: an EXACT PV improvement (score > alpha, score < beta).
                // Record the move in the triangular PV table, concatenating it with the
                // child's already-computed line (the full-window child search at ply+1
                // populated its own row before returning). Mirrors the design reference
                // (PV merge after the beta-cutoff break). This is the ONLY write
                // that builds the root PV line; it never affects a search decision.
                if (UsePvTable)
                {
                    _pvTable.MergeMove(ply, m);
                }

                // ── Alpha-raise reduction (the reference).
                // A move has just raised alpha WITHOUT a beta cutoff. It is unlikely that
                // multiple moves at the same node raise alpha in turn (the PVS ladder already
                // re-searched this one fully), so the REMAINING moves of this node are searched
                // one ply shallower — the local `depth` drives every subsequent LMR base and
                // pruning gate in this loop. Mate scores are exempt (a confirmed mating line
                // must not degrade the verification of follow-up moves). the reference tunes the
                // window 2 ≤ depth < 12; outside it the tree is either too shallow to matter
                // or too deep to trust the shortcut.
                // The trio master can enable this code as
                // part of the extension bundle; the standalone UseAlphaRaiseReduction lever
                // still works independently.
                bool alphaRaiseEnabled = UseAlphaRaiseReduction || (UseExtensionTrio && UseTrioAlphaRaise);
                if (alphaRaiseEnabled && depth > AlphaRaiseMinDepth
                    && depth < AlphaRaiseMaxDepth && Math.Abs(score) < 30000)
                {
                    depth--;
                }
            }
            else
            {
                // Move failed low (score <= alpha)
                if (!isCapture && !isPromotion && quietsSearchedCount < 64)
                    quietsSearched[quietsSearchedCount++] = m;
            }
        }

        // CRITICAL: Check for mate/stalemate based on LEGAL moves only
        if (legalMoves == 0)
        {
            if (inCheck)
                return -31000 + ply; // Checkmate
            // Stalemate is a draw: contempt-adjusted like every draw.
            return DrawScore(pos.SideToMove);
        }

        // store in TT
        NodeType flag = NodeType.Exact;
        // When every move failed low (UpperBound), bestMove holds whichever
        // move the loop happened to try first — typically the TT/killer-ordered
        // move — but it was not confirmed as best. Propagating it would let a
        // poor move accumulate priority in future searches.
        // Store the incoming TT hint (ttMove) instead: it is the best ordering
        // information we have for this position. If no prior hint exists, ttMove
        // is Move.None, which is the safe fallback — no stale move is forwarded.
        // Exact and LowerBound nodes are unaffected: moveToStore stays bestMove.
        Move moveToStore = bestMove;
        if (bestValue <= originalAlpha)
        {
            flag = NodeType.UpperBound;
            moveToStore = ttMove; // preserve prior ordering hint, not fail-low first-tried move
        }
        else if (bestValue >= beta)
            flag = NodeType.LowerBound;
        
        // Pass ply to Store for mate score adjustment.
        // Phase 5a /: also cache this node's RAW static eval so a future visit can skip the
        // static evaluation. rawStaticEval is NoStaticEval when the node was in check (no eval
        // computed) — Store treats that sentinel as "nothing to cache".
        if (excludedMove.IsNone)
        {
            // : the `ttPv` argument marks this entry as PV-proven (full-window node, or
            // a node whose probed entry already carried the flag — see the ttPv derivation at
            // the top of the node). the reference's HashEntry carries the same bit (TranspositionTable
            // put(..., ttPv)); it propagates the PV signal through the tree across searches and
            // feeds the LMR v2 ttPv reduce-less term.
            _tt.Store(pos.ZobristKey, bestValue, depth, flag, moveToStore, _currentAge, ply,
                      inCheck ? TTable.NoStaticEval : rawStaticEval, ttPv);

            // Update Correction History (Exact nodes only — legacy pawn-only additive design;
            // used when Search.UseRefCorrectionHistory is OFF)
            if (UseCorrectionHistory && !UseRefCorrectionHistory && flag == NodeType.Exact
                && !inCheck && Math.Abs(bestValue) < 30000)
            {
                int diff = bestValue - staticEval;
                int idx = (int)(pos.PawnKey & 16383);
                int current = _td.CorrectionHistory[pos.SideToMove][idx];

                // Slow learning: additive bonus with clamping
                int bonus = diff * 16;
                if (bonus > 512) bonus = 512;
                if (bonus < -512) bonus = -512;

                _td.CorrectionHistory[pos.SideToMove][idx] = Math.Clamp(current + bonus, -16384, 16384);
            }

            // ── reference-style 4-term correction-history update
            // (the reference's correction-history machinery) ──
            // Each table holds a depth-weighted running average of (score − staticEval):
            // newValue = diff · 64; newWeight = min(depth+1, 16)
            // update = (old·(64−newWeight) + newValue·newWeight) / 64, clamp ±2048
            // the reference's update conditions, mirrored exactly:
            // !inCheck, !singularSearch, score defined, quiet best move (or none), and
            // not a "trusted" bound result (LOWER already ≥ eval / UPPER already ≤ eval —
            // those carry no NEW eval-error information).
            // TB GATE (Syzygy/eval interaction fix, 2026-09-08): a node above a tablebase
            // leaf carries bestValue = ±TbWinScore (29900) while its staticEval is a
            // normal ~±1000s — a diff of ~±30,000 that would slam the correction tables
            // to their ±2048 clamp and poison every position sharing that material key
            // with a bogus ±127 cp correction. Tablebase-derived scores are exact, not
            // eval-error information, so they are excluded like mate scores.
            if (UseCorrectionHistory && UseRefCorrectionHistory
                && !inCheck && excludedMove.IsNone && Math.Abs(bestValue) < 30000
                && Math.Abs(bestValue) < Syzygy.TbWinScore
                && (moveToStore.IsNone
                    // quiet check (Bug 149 fix): "quiet" means the destination is not occupied by
                    // the OPPONENT (a capture), and the move is neither en passant nor a promotion
                    // - the reference's board.isQuiet(bestMove) at the same point. The board is
                    // already unmade/restored at this store site, so pos.SideToMove IS the mover
                    // and the opponent occupancy is the correct side to test. The previous code
                    // tested the MOVER'S OWN occupancy, which is never set at a legal move's
                    // destination, so the first disjunct was dead code and every ordinary capture
                    // passed as quiet, folding capture noise into the correction tables.
                    || IsQuietStoreMove(pos, moveToStore))
                && !(flag == NodeType.LowerBound && rawStaticEval >= bestValue)
                && !(flag == NodeType.UpperBound && rawStaticEval <= bestValue))
            {
                int diff = bestValue - staticEval;   // staticEval here is the CORRECTED eval (reference parity)
                int newValue = diff * 64;            // CorrectionHistoryTable.SCALE
                int newWeight = Math.Min(depth + 1, 16);
                int oldWeight = 64 - newWeight;
                int side = pos.SideToMove;

                UpdateCorrTable(_td.CorrectionPawn, side, (int)(pos.PawnKey & 16383), oldValue => (oldValue * oldWeight + newValue * newWeight) / 64);
                UpdateCorrTable(_td.CorrectionNonPawn[0], side, (int)(pos.NonPawnKeyWhite & 16383), oldValue => (oldValue * oldWeight + newValue * newWeight) / 64);
                UpdateCorrTable(_td.CorrectionNonPawn[1], side, (int)(pos.NonPawnKeyBlack & 16383), oldValue => (oldValue * oldWeight + newValue * newWeight) / 64);
                if (!prevMove.IsNone && prevPieceIndex >= 0)
                    UpdateCorrTable(_td.CorrectionCont[side], prevPieceIndex, prevMove.To, oldValue => (oldValue * oldWeight + newValue * newWeight) / 64);
            }
        }

        return bestValue;
    }

    /// <summary>
    /// Quiet classification for the reference-style correction-history update gate (Bug 149 fix).
    /// A stored best move counts as quiet when its destination is NOT occupied by the opponent
    /// (an ordinary capture) and the move is neither an en-passant capture nor a promotion. This
    /// mirrors the reference's <c>board.isQuiet(bestMove)</c> evaluated on the node position at the
    /// store site (the board is already unmade there, so <c>pos.SideToMove</c> is the mover).
    /// Extracted from the node-exit gate so the classification is directly unit-testable: the
    /// pre-fix code tested the MOVER'S OWN occupancy at the destination, which is never set for a
    /// legal move, so the capture disjunct was dead code and every ordinary capture counted as
    /// quiet - polluting the correction tables with capture noise.
    /// </summary>
    /// <param name="pos">The node position (post-unmake) the move was stored from.</param>
    /// <param name="m">The best move being stored.</param>
    /// <returns>True when the move is quiet in the correction-history sense.</returns>
    internal static bool IsQuietStoreMove(Position pos, Move m)
    {
        if (m.IsNone) return true;
        return !(Bitboard.IsSet(pos.GetOccupancy(1 - pos.SideToMove), m.To)
                 || m.IsEnPassant || m.Promoted != 0);
    }

    /// <summary>
    /// the reference CorrectionHistoryTable.correction applied to one cell: depth-weighted running
    /// average of the eval-vs-search diff, clamped to ±2048 (MAX = SCALE·32), stored as short.
    /// <paramref name="row"/> indexes the side table; <paramref name="col"/> the key/move cell
    /// (the cont table is flattened to one row, so its column is piece·64 + to and the extra
    /// index is unused).
    /// </summary>
    private static void UpdateCorrTable(short[][] rowTable, int row, int col, Func<int, int> blend)
    {
        int oldValue = rowTable[row][col];
        rowTable[row][col] = (short)Math.Clamp(blend(oldValue), -2048, 2048);
    }

    /// <summary>
    /// TT-score gate (mirrors the reference's helper):
    /// may the probed TT score stand in for this position's
    /// static evaluation? Only when its bound is CONSISTENT with the raw eval —
    /// an EXACT value always may; a LOWER bound must be at least as high as the raw
    /// eval (a deeper search said "this is at least X", and X ≥ our 1-ply estimate);
    /// an UPPER bound at most as low. The same rule the strongest mainstream engines phrase as
    /// "ttValue can be used as a better position evaluation" (an exact bound carries
    /// both flag bits). Pure function of the entry and the raw eval — unit-testable.
    /// </summary>
    internal static bool CanUseTtScore(TTEntry entry, int rawStaticEval)
    {
        return entry.Flag == NodeType.Exact
            || (entry.Flag == NodeType.LowerBound && entry.Value >= rawStaticEval)
            || (entry.Flag == NodeType.UpperBound && entry.Value <= rawStaticEval);
    }

    /// <summary>
    /// The verified-null mate clamp
    /// (the reference's <c>Score.isMate(score) ? beta : score</c>; the design reference's
    /// <c>Math.Abs(score) &gt; TABLEBASE_WIN ? beta : score</c>):
    /// a null-search fail-high returns <paramref name="beta"/> when its score is
    /// MATE-LINE — a mate distance measured through a null move is not a real distance
    /// (the null move never happened), so the fail-high is kept but the mate claim is
    /// dropped — and the score itself otherwise (fail-soft propagation raises the
    /// parent's alpha for free). Pure function, unit-tested.
    /// </summary>
    internal static int NmpFailHighScore(int score, int beta)
        => Math.Abs(score) >= MateScoreBound ? beta : score;

    /// <summary>
    /// RFP fail-soft return, the second half of the B1
    /// formula arm. When the formula arm is ON, the reference's damped spill
    /// <c>beta + (staticEval − beta)/3</c> is returned: the fail-high
    /// still propagates, but the parent's alpha is raised by a third of the overshoot
    /// instead of all of it. When the formula arm is OFF (the shipped default) the legacy
    /// full fail-soft return <c>staticEval</c> is kept bit-identically. Pure function,
    /// unit-tested.
    /// </summary>
    internal static int RfpFailSoftScore(int staticEval, int beta, bool formulaArm)
        => formulaArm ? beta + (staticEval - beta) / 3 : staticEval;

    /// <summary>
    /// Draw score for a side: <c>Contempt · (2·stm − 1)</c>
    /// — White's draw value is −Contempt and Black's is +Contempt (the classic leading mainstream engines-9
    /// `DrawValue[WHITE]/[BLACK]` convention the audit's H1 formula names). With Contempt = 0
    /// every draw is exactly 0, so the compiled default is bit-identical. Pure function,
    /// unit-tested.
    /// </summary>
    internal static int DrawScoreFor(int contempt, int stm) => contempt * (2 * stm - 1);

    /// <summary>
    /// QS fail-high averaging (the reference qsearch
    ///: a non-mate fail-high returns `(score + beta)/2` instead of bare
    /// `beta`, softening the value the parent sees. Mate-line scores (either side) keep the
    /// bare beta clamp, and a score below beta is returned as the bare beta (this helper is
    /// only meaningful on the fail-high path). Pure function, unit-tested.
    /// </summary>
    internal static int QsFailHighScore(int score, int beta)
        => score >= beta && Math.Abs(score) < MateScoreBound && Math.Abs(beta) < MateScoreBound
            ? (score + beta) / 2
            : beta;

    /// <summary> The configured draw score for the side to move.</summary>
    private int DrawScore(int stm) => DrawScoreFor(Contempt, stm);
}
