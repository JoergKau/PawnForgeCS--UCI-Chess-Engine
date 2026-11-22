// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · iterative deepening + aspiration + PV extraction + PV legality
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
    // Iterative deepening entry point. Returns best move found (or default if none).
    /// <summary>
    /// The search driver: iterative deepening from depth 1 to maxDepth with
    /// progressive aspiration windows (from depth 5), single-legal-move
    /// instant play, `go mate N` / `go searchmoves` / `go nodes` handling,
    /// per-iteration info emission and the time-manager stop decisions.
    /// </summary>
    /// <param name="pos">The position to search (the evaluator is positioned on it).</param>
    /// <param name="maxDepth">Maximum search depth (the loop may stop earlier via time/mate target).</param>
    /// <param name="timeLimit">Optional legacy wall-clock limit.</param>
    /// <returns>The best move found; the first legal move as a fallback when nothing was searched.</returns>
    public Move IterativeDeepening(Position pos, int maxDepth, TimeSpan? timeLimit = null)
    {
        // CRITICAL FIX: EVERY thread must initialize its own evaluator!
        // Each thread has its own NNUE accumulator state that needs SetPosition called
        // Previous bug: Only thread 0 initialized, threads 1-7 had uninitialized evaluator state
        _evaluator.SetPosition(pos);
        
        _td.Reset();
        _qsearch.ResetSelectiveDepth();
        
        // Reset Killers
        for (int i = 0; i < 256; i++)
             Array.Clear(_killers[i], 0, 4);

        // Only main thread updates the TT age
        if (_td.ThreadId == 0)
        {
            _tt.NewSearch();
            _currentAge++;
        }

        _lastCurrMoveReportMs = 0;

        Move bestMove = default;
        Move lastBestMove = default; // Track last iteration's best for PV stability
        bool infoEmitted = false;    // thread 0: has ANY info line been sent this search?
        int score = 0;
        long startMs = TimeManager.NowMs(); // monotonic clock (see TimeManager.NowMs)
        
        // CRITICAL FIX: If search is stopped before completing depth 1, return first legal move
        // This prevents "bestmove 0000" when commands arrive rapidly
        var legalMoves = pos.GenerateMoves().ToArray();
        if (legalMoves.Length > 0)
        {
            bestMove = legalMoves[0]; // Fallback to first legal move
        }
        CurrentBestMove = bestMove;

        // `go searchmoves` safety: if NO legal move matches the restriction,
        // ignore it (otherwise the root would report a false mate/stalemate).
        if (RootMoveRestriction is { Count: > 0 }
            && !legalMoves.Any(m => RootMoveRestriction.Contains(m.From * 64L + m.To)))
        {
            RootMoveRestriction = null;
        }

        // SINGLE-LEGAL-MOVE INSTANT PLAY:
        // If exactly ONE legal move exists, there is nothing to decide -
        // searching would only burn the entire time budget on a forced move.
        // Play it immediately and report a minimal info line for the GUI.
        // (Common in blitz/bullet: check evasions, recaptures, forced blocks.)
        // Suppressed in ponder mode: the search must stay alive until the
        // opponent actually plays (ponderhit) or the GUI stops it.
        // Quirk Q8 fix: the shortcut only serves TIME-MANAGED searches (clock or
        // `go movetime`), where it saves the budget. It used to fire for every
        // search, so `go infinite` answered a `bestmove` at once (UCI: do not
        // leave an infinite search until told to) and `go depth/nodes/mate`
        // analysis of a forced position returned no iteration lines. Those
        // searches now run normally.
        if (!PonderMode && !_timeManager.IsInfinite && legalMoves.Length == 1)
        {
            bestMove = legalMoves[0];
            int forcedEval = Evaluate(pos);
            LastScore = forcedEval;
            _onInfo?.Invoke($"info depth 0 score cp {forcedEval} pv {bestMove}");
            return bestMove;
        }

        // ── Syzygy DTZ root ranking (2026-09-20) ──────────────────────────
        // The leaf WDL probe makes every TB win-preserving move score the same flat ±29900,
        // so the root cannot tell a mate-in-2 from a 90-move shuffle. Probe the ROOT once
        // (50-move aware, via the already-tested ProbeRoot) and let the root move loop try
        // the DTZ-optimal move first (Search.Negamax.cs) — a tie-break, not a search bypass.
        // Main thread only: only thread 0's best move is played, and probing on every worker
        // would just be wasted lock contention. Ponder is excluded (it must not volunteer a
        // move). No castling-rights / >MaxPieces root can be probed (ProbeRoot returns Fail).
        _rootTablebaseMove = Move.None;
        _rootTablebaseWdl = int.MinValue;
        if (UseSyzygyDtzRootRanking && _td.ThreadId == 0 && !PonderMode
            && Syzygy.ProbingEnabled && Syzygy.IsInitialized && Syzygy.MaxPieces > 0
            && System.Numerics.BitOperations.PopCount(pos.GetAllOccupancy()) <= Syzygy.MaxPieces)
        {
            var tbRoot = Syzygy.ProbeRoot(pos);
            if (!tbRoot.Failed && !tbRoot.IsCheckmate && !tbRoot.IsStalemate && !tbRoot.Move.IsNone)
            {
                _rootTablebaseMove = tbRoot.Move;
                _rootTablebaseWdl = tbRoot.Wdl; // 50-move-aware: +2 win, +1 cursed, 0 draw, ...
            }
        }

        for (int d = 1; d <= maxDepth; d++)
        {
            if (_signal.ShouldStop)
            {
                break;
            }

            // Progressive Aspiration Windows:
            // From depth 5 on, search around the previous iteration's score
            // with a NARROW window (faster: more beta cutoffs). If the score
            // falls outside, the window is widened GEOMETRICALLY and the
            // position re-searched - usually one small widening is enough,
            // which is much cheaper than the old jump straight to the full
            // window. Around mate scores the window makes no sense, so those
            // always use the full window.
            // ---------------------------------------------------------------------------
            // AspirationBase — the initial half-width of the aspiration window (cp).
            // ---------------------------------------------------------------------------
            // A study note on the coupling that makes this value tricky to tune:
            //
            // A NARROW window is faster — the search runs inside a tight [score-delta,
            // score+delta] band, so more nodes fail high/low early and get pruned, buying
            // depth. But the window ALSO sets the alpha/beta band that descends into the tree,
            // and the static-pruning heuristics (Reverse Futility Pruning, Razoring) compare
            // the static eval against that band. So narrowing the window silently makes those
            // heuristics prune HARDER — even though not one pruning constant changed.
            //
            // HISTORY: this was 50 cp for the whole v1.1 line. A naive narrowing to 25 cp
            // (2026-09-01) broke the mate-in-2 battery — RFP/razoring pruned the mating branch
            // (docs). The window could not be narrowed alone.
            //
            // A study note on why this value is 50 and was NOT narrowed:
            //
            // A narrower window (the reference uses 12 cp) searches inside a tighter band and can, in
            // principle, reach more depth in the same time. It was investigated thoroughly
            // (docs, "A1"). It did not pan out FOR THIS ENGINE:
            // * 25 cp — broke the mate-in-2 battery: a tighter window tightens the alpha/beta
            // band that RFP/razoring compare against, so they over-pruned the mating branch.
            // * 35 cp — held tactics but COST depth (avg 19.8 -> 19.5, EBF slightly up). The
            // tight window triggered more aspiration re-search cycles (fail-high/low widening)
            // than it saved, net-shaving a ply.
            // * 40 cp — matched the 50 cp baseline within noise on every cheap gate: no gain.
            // Conclusion: the window is not a strength lever for PawnForge at its current pruning
            // margins and score volatility. Narrowing either breaks tactics or churns re-searches;
            // loosening just reproduces the baseline. Left at 50. See docs for the full ladder.
            // AUDIT WAVE A3 : the window half-width was a
            // private const, so it was not a lever at all — not even a hidden UCI option.
            // It is now <see cref="AspirationBase"/> (default 50, bit-identical to this
            // const) so the "narrow the window" experiment can be run as a match arm
            // instead of a per-arm rebuild. MaxAspirationCycles is now
            // <see cref="AspirationMaxCycles"/>, and the fixed `d >= 5` gate is now
            // <see cref="AspirationStartDepth"/>.
            int delta = AspirationBase;
            int alpha = int.MinValue + 1;
            int beta = int.MaxValue - 1;
            if (d >= AspirationStartDepth && Math.Abs(score) < MateScoreBound)
            {
                alpha = score - delta;
                beta = score + delta;
            }

            Move iterationBest = default;
            int currentScore = score;
            int cycles = 0;

            while (true)
            {
                try
                {
                    _tt.Prefetch(pos.ZobristKey);
                    currentScore = Negamax(pos, d, alpha, beta, ref iterationBest, 0);
                }
                catch (Exception ex)
                {
                    if (_td.ThreadId == 0) _onInfo?.Invoke($"info string Exception at depth {d}: {ex.Message}");
                    throw;
                }

                // 50-move-aware root score correction (2026-09-20). The search's
                // leaf probe ignores the real halfmove clock (ProbeWdl uses rule50: 0), so a
                // position that is a WDL win but a curse / fifty-move draw under the ACTUAL
                // counter still returns ±TbWinScore. When the 50-move-aware root probe says the
                // result is not decisive (+1 cursed win, 0 draw, -1 blessed loss), clamp the
                // reported root score to a draw so the engine stops announcing a phantom win in
                // an already-drawn game. Mate scores and genuine ±2 results are untouched.
                if (_rootTablebaseWdl is 1 or 0 or -1
                    && Math.Abs(currentScore) >= Syzygy.TbWinScoreMin
                    && Math.Abs(currentScore) < MateScoreBound)
                {
                    currentScore = 0;
                }

                if (_signal.ShouldStop) break;

                // Score inside the window: valid result for this iteration.
                if (currentScore > alpha && currentScore < beta) break;

                if (Math.Abs(currentScore) >= MateScoreBound || cycles >= AspirationMaxCycles)
                {
                    // Mate found (aspiration is pointless) or safety limit
                    // reached: one final search with the full window - any
                    // score lies inside it, so the next pass terminates.
                    alpha = int.MinValue + 1;
                    beta = int.MaxValue - 1;
                    delta = AspirationBase;
                    continue;
                }

                if (currentScore <= alpha)
                {
                    // Fail low: position is worse than expected - widen down.
                    beta = (alpha + beta) / 2;
                    alpha -= delta;
                }
                else
                {
                    // Fail high: position is better than expected - widen up.
                    beta += delta;
                }

                delta += delta / 2; // geometric widening: 50, 75, 112, 168, ...
                cycles++;
            }

            if (_signal.ShouldStop) break;

            score = currentScore;
            LastScore = score; // datagen reads this after each fixed-node search
            if (!iterationBest.IsNone)
            {
                lastBestMove = bestMove;
                bestMove = iterationBest;
                CurrentBestMove = bestMove;
            }

            // Minimum-depth safety guard bookkeeping (2026-09-07): iteration `d` fully
            // completed (we did not break out at the ShouldStop check above), so record it.
            // The TimeManager uses this to refuse a soft-limit stop until MinIterationDepth
            // iterations are done — preventing a single exploding iteration from leaving the
            // engine on a depth-1 blunder (see TimeManager.ShouldStop / SetCompletedDepth).
            _timeManager.SetCompletedDepth(d);

            // Feed the stable-TM node-fraction term: the
            // nodes spent on the best root move (accumulated across the search) and the total
            // (thread 0's tree is the one the time manager reads).
            if (_td.ThreadId == 0 && !_timeManager.IsInfinite && !bestMove.IsNone)
            {
                _timeManager.SetNodeStats(_td.NodesPerMove[bestMove.From, bestMove.To], _td.Nodes);
            }

            // Update PV stability tracking (main thread only)
            if (_td.ThreadId == 0 && !_timeManager.IsInfinite)
            {
                // Track PV stability for time management
                bool pvStable = (d > 1 && lastBestMove.Equals(bestMove));
                _timeManager.UpdatePVStability(bestMove.GetHashCode(), pvStable);
            }

            // Info output (Main Thread Only)
            if (_td.ThreadId == 0)
            {
                var elapsed = TimeManager.NowMs() - startMs;
                long totalNodes = _getTotalNodes != null ? _getTotalNodes() : _td.Nodes;
                var nps = elapsed > 0 ? (long)(totalNodes * 1000 / elapsed) : 0;
                
                string scoreStr = $"cp {score}";
                if (Math.Abs(score) > 30000) scoreStr = $"mate {(score > 0 ? (31000 - score + 1) / 2 : -(31000 + score + 1) / 2)}";

                // A: the PV comes directly from the explicit triangular PV table — the
                // line was built on the search stack (InitPly/MergeMove in Negamax), so no TT
                // walk, no legal-move re-matching and no board make/unmake are needed here.
                // Fallback: when UsePvTable is off, the legacy TT-walk path reproduces the
                // pre-Step-2A behavior exactly.
                string pvStr;
                if (UsePvTable)
                {
                    var pvMoves = _pvTable.GetPv();
                    pvStr = pvMoves.Length > 0 ? string.Join(" ", pvMoves.ToArray()) : string.Empty;
                }
                else
                {
                    var pvLine = GetPvFromTT(pos, d);
                    pvStr = string.Join(" ", pvLine);
                }

                // hashfull in permille (0-1000), standard UCI unit - TT reports percent
                int hashfull = _tt.GetHashfullPercent() * 10;

                _onInfo?.Invoke($"info depth {d} seldepth {_qsearch.SelectiveDepth} score {scoreStr} nodes {totalNodes} nps {nps} hashfull {hashfull} time {(long)elapsed} pv {pvStr}");
                infoEmitted = true;
            }

            // Check time limits (TimeManager integration) - monotonic clock.
            // Placed AFTER the info emission (2026-09-08, fastchess abort fix): when an
            // iteration completes exactly as the clock expires, the new best move is
            // already adopted above, so its info line MUST reach the GUI before we stop.
            // Breaking before the info emission left the last reported PV starting with
            // the PREVIOUS iteration's move while bestmove carried the new one — the
            // "Bestmove does not match beginning of last PV" warning that made fastchess
            // --strict abort the tournament. This mirrors the mainstream engines'
            // invariant: a final info ... pv ... is always sent before bestmove
            // (uciPvSent / output_pv before onBestmove), so last-pv[0] == bestmove holds
            // on every exit path. The mid-iteration abort path needs no change: there the
            // ShouldStop guards skip the best-move adoption entirely, so bestmove stays
            // equal to the last printed PV.
            long currentTime = TimeManager.NowMs();

            // Check hard time limit
            if (_timeManager.IsTimeUp(currentTime))
            {
                _signal.Stop(); // Stop all threads
                break;
            }

            // Legacy timeLimit support (for compatibility)
            if (timeLimit.HasValue && (TimeManager.NowMs() - startMs) > timeLimit.Value.TotalMilliseconds)
            {
                _signal.Stop(); // Stop other threads too if we timed out
                break;
            }

            // `go mate N`: stop as soon as a mate within N moves is PROVEN.
            // Placed AFTER the info emission so the GUI actually sees the
            // mating iteration's info line (that is the point of `go mate`).
            // Mate score convention: 31000 - plies-to-mate (e.g. mate at ply 3
            // = mate in 2 moves = 30997). Distance in moves = (plies + 1) / 2.
            if (MateTarget > 0 && Math.Abs(score) > MateScoreBound)
            {
                int mateDistancePlies = 31000 - Math.Abs(score);
                if ((mateDistancePlies + 1) / 2 <= MateTarget)
                {
                    break;
                }
            }
            
            // Check if we should continue to next depth (main thread only, after completing depth)
            if (_td.ThreadId == 0 && !_timeManager.IsInfinite)
            {
                if (!_timeManager.ShouldContinue(currentTime, bestMove.GetHashCode(), score))
                {
                    _signal.Stop(); // Stop all threads - not enough time for next depth
                    break;
                }
            }
            
            // HIST1 (2026-09-10): aging (×7/8 per iteration) KEPT — it is the saturation
            // regulator for PF's persistent history (gravity alone lets tables ride the
            // ±8033 cap within one search and poison the next; see SearchHeuristics doc).
            // Combined with gravity the hot-cell steady state is ≈ ±4108, the the reference
            // magnitude the LMR divisor (3419) assumes.
            if (d >= 4)
            {
                AgeHistory();
            }
        }

        // `go infinite` must not leave the search on its own (UCI): the depth
        // cap is an implementation limit, not the end of the analysis. Wait for
        // `stop`, repeating the last info line now and then so a GUI watchdog
        // sees the engine is still alive at the cap. This is only set for a
        // real `go infinite`; depth/node/mate-limited searches still return.
        if (WaitForStopAtDepthLimit && !_signal.ShouldStop)
        {
            long lastBeat = TimeManager.NowMs();
            while (!_signal.ShouldStop)
            {
                System.Threading.Thread.Sleep(10);
                if (_td.ThreadId == 0 && infoEmitted
                    && TimeManager.NowMs() - lastBeat >= 5_000)
                {
                    long total = _getTotalNodes != null ? _getTotalNodes() : _td.Nodes;
                    _onInfo?.Invoke(
                        $"info depth {maxDepth} seldepth {_qsearch.SelectiveDepth} nodes {total}");
                    lastBeat = TimeManager.NowMs();
                }
            }
        }

        // (2026-11-03): NEVER return a real move without a preceding info line.
        //
        // The degenerate-clock path can break out of the loop before depth 1 emits
        // anything: e.g. `go wtime 100` with a 50 ms overhead gives a 12 ms hard limit,
        // and a search thread that is scheduled >12 ms late hits `IsTimeUp` on its very
        // first node poll. The pre-fix engine then returned the first legal move bare
        // (`info string eval-source: ...` / `bestmove a2a3`), which fastchess
        // --compliance reports as "no info line" and some GUIs show as depth 0/unknown.
        //
        // This is a REPORTING fix only: it does not search, extend the clock or change
        // which move is played. It emits the same minimal line the single-legal-move
        // shortcut already uses, and keeps the "last PV starts with bestmove" invariant.
        if (_td.ThreadId == 0 && !infoEmitted && !bestMove.IsNone)
        {
            int fallbackScore = Evaluate(pos);
            LastScore = fallbackScore;
            _onInfo?.Invoke($"info depth 0 score cp {fallbackScore} pv {bestMove}");
        }

        return bestMove;
    }

    // Recover PV from TT
    private List<Move> GetPvFromTT(Position pos, int depth)
    {
        var pv = new List<Move>();
        // Avoid infinite loops with visited keys or depth limit
        // We use a simple ply loop
        int limit = depth; 
        // We need to clone position to make moves? Or just probe?
        // We cannot make moves on 'pos' as it is passed by reference (class). 
        // We need a copy of position to simulate PV.
        // Assuming Position is cloneable or we can make/unmake.
        // Copying position is expensive? Position is struct or class? Class. 
        // PawnForge Position has Copy? Or we can make/unmake and restore.
        // Let's use make/unmake and restore at the end.
        
        var movesMade = new List<Move>();
        var capturedPieces = new List<Piece>(); // Need captured piece to unmake
        // Unmake all in finally block to ensure board consistency
        try
        {
            for (int i = 0; i < limit; i++)
            {
                if (_tt.Probe(pos.ZobristKey, out var entry))
                {
                    Move m = entry.BestMove;
                    if (m.IsNone || !entry.Key.Equals(pos.ZobristKey)) break; // simple collision check or empty
                    
                    // Verify pseudo-legal (MovePicker logic or IsMoveLegal)
                    // If legal, add to PV and make move
                    if (IsMoveLegal(pos, m, out Move legalMove))
                    {
                        pv.Add(legalMove);
                        var cap = pos.MakePublicMove(legalMove);
                        movesMade.Add(legalMove);
                        capturedPieces.Add(cap);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
        }
        finally
        {
            // Unmake all
            for (int i = movesMade.Count - 1; i >= 0; i--)
            {
                pos.UnmakePublicMove(movesMade[i], capturedPieces[i]);
            }
        }
        
        return pv;
    }

    private bool IsMoveLegal(Position pos, Move m, out Move legalMove)
    {
        // TT packing loses IsEnPassant/IsCastle flags, so we match by coordinates only
        // and return the actual legal move (with correct flags) for use in PV
        legalMove = default;
        foreach(var x in pos.GenerateMoves())
        {
            if(x.From == m.From && x.To == m.To && x.Promoted == m.Promoted)
            {
                legalMove = x;
                return true;
            }
        }
        return false;
    }

    // Convenience overload for tests
    /// <summary>Convenience overload: full-window search at the root (ply 0).</summary>
    public int Negamax(Position pos, int depth, int alpha, int beta)
    {
        Move dummy = default;
        return Negamax(pos, depth, alpha, beta, ref dummy, 0);
    }
}
