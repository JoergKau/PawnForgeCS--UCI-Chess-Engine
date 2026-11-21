// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · ITERATIVE DEEPENING + PVS + FULL EXTENSION/PRUNING STACK
// ═══════════════════════════════════════════════════════════════════════════════
//
// ARCHITECTURE OVERVIEW
// ─────────────────────
// One Search instance exists per engine thread (Lazy SMP): every thread runs
// the SAME full search from the root over a SHARED transposition table,
// synchronized by a shared SearchSignal and its own IPositionEvaluator + ThreadData
// (histories, killers, per-ply buffers). Thread 0 is the "main" thread: it
// alone emits info lines, updates the TT generation and drives time management.
//
// PER-ITERATION FLOW (IterativeDeepening, depth d >= 1):
// 1. Progressive aspiration window around the previous score (d >= 5),
// geometric widening on a miss (see the loop below)
// 2. Negamax with PVS: first legal move full window, later moves
// zero-window with a re-search ladder
// 3. TT store with depth-preferred + age-blended replacement
// 4. info emission (depth/seldepth/score/nodes/nps/hashfull/time/pv)
// 5. `go mate N` stop check, time checks, PV-stability update,
// ShouldContinue (next-depth decision)
//
// NEGAMAX NODE PIPELINE (in execution order):
// stop polling → draw detection → mate-distance pruning → check
// extension → qsearch drop (depth <= 0) → static eval → hindsight
// reductions → RFP → razoring → TT probe/cutoff → null-move pruning →
// ProbCut → IIR → singular extension → move loop:
// per move: SEE pruning → LMP → history pruning → make →
// legality check → searchmoves filter → currmove report →
// recapture extension → LMR → PVS → unmake
// → mate/stalemate detection → TT store
//
// EXTENSIONS (+depth) AND PRUNINGS (skip/reduce) — all individually
// toggleable via the Use* properties:
// + check extension (per-path cap, - SEE pruning (depth <= 2)
// see below) - LMP (depth <= 4, threshold 8+d²)
// + single-response (exactly 1 reply, - history pruning (depth <= 3)
// shares the per-path cap) - null-move pruning (R = 3 + d/6)
// + recapture (same target square) - reverse futility (d <= 8, 80·d)
// + singular (depth >= 8, TT move) - razoring (d <= 3)
// + hindsight adjustments - ProbCut (d >= 5)
// - negative extensions (bad SEE/hist)
// Continuation histories: 1/2/4/6-ply, butterfly + capture history,
// correction history (write-side), all aged x7/8 per iteration.
//
// CHECK-EXTENSION GATING (v2.0 Consolidated Audit — UPDATED)
// ────────────────────────────────────────────────────────────────────
// Was: a flat "ply < 15" wall (a hard cap on ply POSITION from the root).
// Now: a per-path ACCUMULATED-EXTENSION counter (UsePerPathExtensionCap,
// MaxPathExtensions, default cap 32 plies), threaded through Negamax's
// pathExtensions parameter exactly like cutNode/excludedMove. Design was
// synthesized from reading two independent reference engines directly
// :
// - the reference: unconditional check extension, no cap at all (relies solely
// on the global MAX_DEPTH ceiling).
// - the design reference: a depth-RELATIVE gate ("ply < Depth * 2") that grows with
// the current iterative-deepening depth, so reach scales automatically.
// Both references measurably out-depth PawnForge's old flat wall (endgame
// seldepth gap +22..+23 vs both, while nominal-depth gap was only +5..+7 -
// i.e. an EXTENSION problem, not an EBF/LMR problem). Toggling
// UsePerPathExtensionCap off reproduces the exact legacy "ply < 15" formula
// byte-for-byte for A/B comparison.
//
// SCORE CONVENTIONS
// ─────────────────
// cp scores from the NNUE (centipawns, side-to-move relative)
// mate scores: 31000 - plies-to-mate (e.g. mate at ply 3 = 30997)
// draws: 0 (repetition, 50-move, insufficient material)
//
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Tb;
using TTable = PawnForge.Core.TranspositionTable.TTable;

namespace PawnForge.Core.Search;

/// <summary>
/// The alpha-beta search driver: one instance per engine thread.
/// Runs iterative deepening with progressive aspiration windows, principal
/// variation search, the full extension/pruning stack and NNUE evaluation,
/// reading and writing the shared transposition table.
///
/// Declared <c>partial</c> and split across topic files to keep each one
/// manageable: Search.Options.cs (state + toggles), Search.IterativeDeepening.cs,
/// Search.Negamax.cs, Search.Reduction.cs, Search.History.cs.
/// </summary>
/// <remarks>
/// Threading: all threads execute the same root search (Lazy SMP). TT and
/// stop signal are shared; histories, killers and the NNUE evaluator state are
/// per-thread. Only thread 0 emits info lines and updates the TT generation.
/// </remarks>
public partial class Search
{
    public Search(TTable tt, ThreadData td, SearchSignal signal, IPositionEvaluator evaluator, Func<long>? getTotalNodes = null, Action<string>? onInfo = null)
    { 
        _tt = tt;
        _td = td;
        _signal = signal;
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _getTotalNodes = getTotalNodes;
        _onInfo = onInfo;

        // Size hint for the (default-OFF) per-thread eval
        // cache: clamp(hash/2, 8, 1024) MB, the audit's/the design reference's sizing rule. The cache
        // itself is allocated lazily on the first cached eval, so a toggle-OFF run pays nothing.
        _td.EvalCacheSizeMb = Math.Clamp(tt.GetSizeMB() / 2, 8, 1024);
        
        // Initialize Quiescence Search
        // hand the shared TT and the live generation counter to
        // QS so the (default-OFF) UseQsTtProbe toggle can probe/store there.
        _qsearch = new QuiescenceSearch(evaluator, td, signal, _timeManager, _tt, () => _currentAge);
        
        // Initialize Killers
        _killers = new Move[256][];
        for (int i = 0; i < 256; i++)
            _killers[i] = new Move[4];
    }

    /// <summary>
    /// Get the TimeManager instance for configuring time controls.
    /// </summary>
    public TimeManager TimeManager => _timeManager;

    /// <summary>
    /// CRITICAL FIX #2: Explicit stop signal for this thread.
    /// Signals this search to stop gracefully. Used during multi-threaded shutdown.
    /// </summary>
    public void Stop()
    {
        _signal.Stop();
    }

    /// <summary>
    /// Static evaluation of the position for the side to move: the NNUE
    /// value from the thread's evaluator.
    /// </summary>
    public int Evaluate(Position pos)
    {
        // Full-recompute evaluator; the NNUE keeps its own incremental state.
        return _evaluator.Evaluate();
    }

    /// <summary>
    /// Static eval with the optional per-thread eval cache.
    /// When <see cref="UseEvalCache"/> is ON, a verified cache hit returns the cached value
    /// (bit-identical to a fresh <see cref="Evaluate"/>) and skips the NNUE forward pass;
    /// a miss computes once and stores. When OFF this is exactly <see cref="Evaluate"/> —
    /// not one cache instruction runs (the default is bit-identical, pinned by the identity
    /// gate and <c>FinalAuditBand2Tests</c>).
    /// </summary>
    private int EvaluateCached(Position pos)
    {
        if (!UseEvalCache) return Evaluate(pos);

        EvalCache cache = _td.EnsureEvalCache();
        if (cache.TryProbe(pos.ZobristKey, pos.SideToMove, out int cached))
        {
            _td.EvalCacheHits++;
            return cached;
        }

        int eval = Evaluate(pos);
        cache.Store(pos.ZobristKey, pos.SideToMove, eval);
        return eval;
    }

    /// <summary> This thread's eval-cache hit count (diagnostic; get-only
    /// so it is never picked up by the hidden-UCI reflection).</summary>
    public long EvalCacheHits => _td.EvalCacheHits;

    // SEE optimization knob (delegated to quiescence search)
    public bool UseSeeApproxPrefilterInQuiescence 
    { 
        get => _qsearch.UseSeeApproxPrefilterInQuiescence;
        set => _qsearch.UseSeeApproxPrefilterInQuiescence = value;
    }

    /// <summary>
    /// Delegates to the quiescence search: resolves captures (with SEE
    /// prefiltering and delta pruning) beyond the nominal search depth so
    /// the evaluation is only taken in tactically quiet positions.
    /// Checks are NOT searched here (captures only).
    /// </summary>
    public int Quiescence(Position pos, int alpha, int beta, int ply = 0)
    {
        return _qsearch.Search(pos, alpha, beta, ply);
    }

    private void AgeHistory()
    {
        SearchHeuristics.AgeHistory(_td);
    }
}
