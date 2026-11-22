// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · per-thread state + all tunable option/toggle properties
// ═══════════════════════════════════════════════════════════════════════════════

// ── SPECIAL RELEASE OVERRIDE R1 (2026-10-28) ───────────────────────────────────
// Eight defaults are deliberately set to the v8 measurement campaign's winner
// (point estimates >= +10 Elo, every arm formally INCONCLUSIVE; owner decision).
// This is a RELEASE-ONLY override for special testing: the next champion net
// invalidates these estimates and the campaign must be re-run against it.
// Revert list (old default in parentheses):
// UseTtScoreEval = true (+12.17; false)
// UseFormulaRfp = true (+13.90; false)
// UseLmrHistoryPrecision = true (+12.17; false)
// UseMicroRiders = true (+11.30; false)
// UseStableTimeManagement = true (+36.62; false)
// UseQsTtProbe = true (+16.52; false)
// UseRefSeeThresholds = true (+21.74; false)
// SeePruningMaxDepth = 8 (4, the companion value)
// UseQuiescenceChecks = false (=false arm +11.30; true)

// ── RELEASE OVERRIDE R2 (2026-11-04) ──────────────────────────────────────────
// Owner decision after the v9 lever campaign: two default-OFF levers are baked
// ON on their v9 point estimates even though both arms were formally
// INCONCLUSIVE (LLR inside ±2). This is an OWNER OVERRIDE — not a measured
// win — and it is recorded as such:
// UseRefCorrectionHistory = true (19.13 ± 22.40, LLR +0.45, LOS 95.35 %)
// UseRefNmpEvalScale = true (12.17 ± 21.39, LLR +0.28)
// Both are net-dependent: on v8 the same arms measured -16.52 and -12.17. The
// next net change invalidates these estimates, and any confirmation run is a
// NEW one-variable campaign. Revert list (old default in parentheses):
// UseRefCorrectionHistory = false (the earlier default since 2026-09-08)
// UseRefNmpEvalScale = false (the earlier default)
// Everything else is unchanged from R1.


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
    // Debug trace - enable with PAWNFORGE_TRACE=1 (prints root moves to stderr)
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("PAWNFORGE_TRACE") == "1";

    // Scores at or beyond this bound are mate scores (mate distance encoded
    // as MATE_BOUND - plies). Used by the aspiration windows and `go mate`.
    private const int MateScoreBound = 30000;

    public long Nodes => _td.Nodes; // Accessor for convenience

    /// <summary>
    /// Node limit for `go nodes` (UCI). 0 = no limit. Checked against the
    /// AGGREGATED node count of all threads (via <see cref="_getTotalNodes"/>)
    /// in the periodic stop polling.
    /// </summary>
    public long NodeLimit { get; set; } = 0;

    /// <summary>
    /// `go mate N` (UCI): stop the search as soon as a mate in at most N moves
    /// for the side to move is PROVEN. 0 = off. Mate distance is measured in
    /// moves (a mate score of 31000 - p means mate at ply p = (p+1)/2 moves).
    /// </summary>
    public int MateTarget { get; set; } = 0;

    /// <summary>
    /// `go searchmoves` (UCI): restrict the ROOT move loop to these moves.
    /// Keys are from*64+to (promotion suffixes are folded - a restriction on
    /// e7e8 applies to all e7e8 promotions). null/empty = unrestricted.
    /// </summary>
    public HashSet<long>? RootMoveRestriction { get; set; }

    /// <summary>
    /// `go infinite` (UCI): an unlimited analysis must not answer `bestmove`
    /// when the engine's depth cap is reached. The search stays alive until the
    /// GUI sends `stop`. Unlike <see cref="TimeManager.IsInfinite"/>, this is
    /// false for the depth/node/mate-limited searches that also run without a
    /// clock, so those still end normally.
    /// </summary>
    public bool WaitForStopAtDepthLimit { get; set; } = false;

    /// <summary>
    /// Minimum interval in ms between `info currmove ...` reports at the root
    /// (0 = report every root move; useful for tests).
    /// </summary>
    public int CurrMoveReportIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Ponder mode (`go ponder`): the engine is thinking on the OPPONENT's
    /// move from the current position. The orchestrator sets the time manager
    /// to infinite, so this search never stops by itself (no time-based stop,
    /// no `ShouldContinue` exit) - it runs until `ponderhit` or `stop`. The
    /// single-legal-move instant-play shortcut is suppressed so the search
    /// stays alive while the opponent is thinking (a forced reply must still
    /// be reported only via the normal bestmove path after `ponderhit`).
    /// </summary>
    public bool PonderMode { get; set; } = false;

    /// <summary>
    /// The root best move of the most recently completed iteration (the
    /// pondered move in ponder mode). Initialized to the first legal move as a
    /// fallback so it is never <see cref="Move.None"/> while legal moves exist.
    /// The orchestrator reads this on `ponderhit` to know which opponent move
    /// the pondering search was expecting, then advances the position by it.
    /// </summary>
    public Move CurrentBestMove { get; private set; }

    /// <summary>
    /// The stm-relative score (centipawns, mate in the 31000 convention) of the
    /// most recently COMPLETED iteration — the value reported on that
    /// iteration's `info ... score cp` line. Datagen reads this after every
    /// fixed-node search to fill the training record's teacher-score column
    /// (the same seam the retired the previous generation engine exposed as
    /// <c>SearchResult.Score</c>). 0 until the first iteration completes.
    /// </summary>
    public int LastScore { get; private set; }

    private long _lastCurrMoveReportMs;

    private readonly TTable _tt;
    private readonly ThreadData _td;
    private readonly SearchSignal _signal;
    private readonly Func<long>? _getTotalNodes;
    private int _currentAge = 1;
    private readonly QuiescenceSearch _qsearch;
    
    // Evaluation seam (NNUE)
    private readonly IPositionEvaluator _evaluator;
    
    // Time Management
    private readonly TimeManager _timeManager = new TimeManager();
    
    // Callback for reporting search info (decoupled from Console)
    private readonly Action<string>? _onInfo;

    // Pruning Flags
    /// <summary>
    /// Quiescence-search quiet-check generation passthrough (the actual flag lives on the
    /// private <see cref="_qsearch"/>). Exposed here as a public Search property so it can be
    /// A/B-tested uniformly via the hidden UCI search options alongside the main-search toggles
    /// (it is one of the queued de-stacking experiments: OFF recovers ~18–35% NPS — see
    ///). Reads/writes flow straight through to the
    /// quiescence searcher.
    /// </summary>
    public bool UseQuiescenceChecks
    {
        get => _qsearch.UseQuiescenceChecks;
        set => _qsearch.UseQuiescenceChecks = value;
    }

    /// <summary>
    /// Stable-PV soft-limit shrink passthrough (the actual flag lives on the private
    /// <see cref="_timeManager"/>; see TimeManager.UseStableShrink). Exposed as a public Search
    /// property so the TM1 lever is A/B-testable via
    /// the hidden UCI search options like every other toggle: stable best move ≥ 2 iterations
    /// → soft limit ×0.85, ≥ 3 → ×0.75; false restores the 2026-08-28 no-shrink behavior.
    /// </summary>
    public bool UseStableShrink
    {
        get => _timeManager.UseStableShrink;
        set => _timeManager.UseStableShrink = value;
    }

    /// <summary>
    /// TM2/TM3/TM4 passthroughs (the actual flags live on the private <see cref="_timeManager"/>).
    /// Exposed as public Search properties so the time-management bundle is A/B-testable via the
    /// UCI search options. ⚠️ The 2026-09-10 bundle verdict
    /// REVERTED panic+advantage, so today's compiled defaults are UsePanicMode = **ON**,
    /// UseTimeAdvantage = **ON** (the older note here said "default OFF" — it described the
    /// SPRT ARM, not the shipped default) and UseOverheadSoftClamp = ON (soft ≤ hard − overhead,
    /// pairs with Move Overhead 50). See <see cref="TimeManager"/> for the per-flag history.
    /// </summary>
    public bool UsePanicMode
    {
        get => _timeManager.UsePanicMode;
        set => _timeManager.UsePanicMode = value;
    }

    public bool UseTimeAdvantage
    {
        get => _timeManager.UseTimeAdvantage;
        set => _timeManager.UseTimeAdvantage = value;
    }

    public bool UseOverheadSoftClamp
    {
        get => _timeManager.UseOverheadSoftClamp;
        set => _timeManager.UseOverheadSoftClamp = value;
    }

    // ── clock-imbalance scaler (the design reference pattern) ────────────
    // Passthroughs to <see cref="_timeManager"/>. DEFAULTS TO OFF (UseClockImbalance=false ⇒
    // the TM3-era behaviour, bit-identical). When ON it supersedes TM3 (UseTimeAdvantage),
    // which has no deficit side; the hard-limit clipping keeps the zero-flag guarantee.
    /// <summary>G1: two-sided clock-imbalance scaler (more time when ahead, less when behind).</summary>
    public bool UseClockImbalance
    {
        get => _timeManager.UseClockImbalance;
        set => _timeManager.UseClockImbalance = value;
    }

    /// <summary>G1: max percentage to ADD when far ahead on the clock (100 = +1.0×).</summary>
    public int ClockImbalanceAheadScale
    {
        get => _timeManager.ClockImbalanceAheadScale;
        set => _timeManager.ClockImbalanceAheadScale = value;
    }

    /// <summary>G1: max percentage to SHED when far behind on the clock (50 = −0.5×).</summary>
    public int ClockImbalanceBehindScale
    {
        get => _timeManager.ClockImbalanceBehindScale;
        set => _timeManager.ClockImbalanceBehindScale = value;
    }

    /// <summary>
    /// Twofold-repetition draw inside the search tree. When true
    /// (default), a search node whose position has occurred ONCE before (since the last
    /// irreversible move) is scored as a draw — the "twofold in search, threefold in game"
    /// convention the reference uses (Score.isDoubleRepetition) and most top engines share. When
    /// false (legacy behavior), only threefold repetition draws inside the search.
    /// Offense: in won endgames the engine never walks into a repetition it cannot see
    /// coming. Defense: in worse endings the drawing repetition is found one occurrence
    /// earlier. Game-claim rules (GUI/threefold) are unaffected — see
    /// <see cref="Position.IsDrawForSearch"/>. SPRT-pending change: flip to false if the
    /// SPRT rejects it.
    /// </summary>
    public bool UseTwofoldRepetitionDraw { get; set; } = true;

    /// <summary>
    /// Syzygy DTZ root ranking (2026-09-20, `). When the ROOT is inside
    /// the tablebases, the WDL-only leaf probe makes every win-preserving move score the
    /// same flat <see cref="PawnForge.Core.Tb.Syzygy.TbWinScore"/> (±29900), so the root
    /// move loop keeps whichever move happens to sort first — with no progress signal it
    /// shuffles TB wins until the fifty-move rule (or hangs everything, as in `crazy.pgn`
    /// / KQQvK). This toggle makes the root TRY the 50-move-aware DTZ-optimal move
    /// (<see cref="PawnForge.Core.Tb.Syzygy.ProbeRoot"/>) first at the root, so the
    /// existing "strictly greater" selection naturally keeps it whenever the search also
    /// scores it as a TB win. This is a tie-break, NOT the DTZ root bypass: the search still
    /// runs, still owns repetition handling (a repeating TB move scores 0 at the child and
    /// is replaced by the next winning move), still honors `searchmoves`, and a real mate
    /// score still outranks it. It also corrects the reported root score when the real
    /// fifty-move counter turns a WDL win into a cursed win / draw, so the engine stops
    /// announcing `+29900` in an already-drawn game. Root-only and main-thread-only; a
    /// no-op in every non-TB position. Disable to reproduce the pre-fix behavior exactly.
    /// </summary>
    public bool UseSyzygyDtzRootRanking { get; set; } = true;

    /// <summary>
    /// **MEASURED AND REJECTED as a
    /// default change** — kept only as an ablation switch. Defaults to true, i.e. the
    /// original behaviour: the Syzygy WDL probe may hard-return its exact flat
    /// &#177;<see cref="PawnForge.Core.Tb.Syzygy.TbWinScore"/> at a PV node.
    ///
    /// THE FINDING. Mate announcement under tablebases (0/60 on the short bucket) looks
    /// like a pure reporting bug and is not one. The probe is EXACT inside the tables, so
    /// returning it is correct — and returning it on the principal variation is what makes
    /// endgame move selection perfect. Announcing a mate requires the SEARCH to walk into
    /// the mate, which means declining the tablebase's answer, and the tablebase picks the
    /// right move better than our search does. The two goals are in direct tension.
    ///
    /// Measured A/B on the 120-position endgame suite (DTZ-bucketed, 1 s/position):
    /// <list type="bullet">
    /// <item>true (default): short 60/60 solved, long 60/60 solved, 0.0% announced.</item>
    /// <item>false: short 49/60 solved (81.7%), long 59/60 (98.3%), 21.7% announced.</item>
    /// </list>
    /// 11 lost positions to buy an announcement. Move selection is strength; the
    /// announcement is a GUI nicety. Rejected.
    ///
    /// The two other ways to "fix" the 0% were also measured or reasoned out and rejected:
    /// converting DTZ into a mate score LIES about the distance (on
    /// <c>8/8/8/8/8/8/4p3/R3K2k w</c> DTZ is 1 while true DTM is 7 plies / mate 4, so it
    /// would announce "mate 1" for a mate-in-4), and synthesising the report from the root DTZ
    /// without touching the search has exactly the same unsoundness.
    ///
    /// When false, PV nodes are graded by |DTZ| via
    /// <see cref="PawnForge.Core.Tb.Syzygy.WdlToScore(int,int)"/> so a faster win outranks a
    /// slower one, and a node whose DTZ fits in the remaining depth does not return at all so
    /// the search can reach the mate. DTZ is probed only on this path, so the default costs
    /// nothing. Not reachable from the UCI option list on purpose, so no endgame result can
    /// silently depend on it.
    /// </summary>
    public bool AllowSyzygyCutoffOnPvNodes { get; set; } = true;

    // Root-only TB ranking state. Recomputed once per IterativeDeepening by thread 0;
    // read by the ply-0 move loop (Search.Negamax.cs) as a move-ordering override and by
    // IterativeDeepening for the 50-move-aware score correction.
    // _rootTablebaseMove : DTZ-optimal root move (Move.None = no TB ranking this search)
    // _rootTablebaseWdl : 50-move-aware root WDL class (-2..+2; int.MinValue = none)
    private Move _rootTablebaseMove = Move.None;
    private int _rootTablebaseWdl = int.MinValue;

    /// <summary>
    /// Quiescence-search twofold-repetition passthrough (same semantics as
    /// <see cref="UseTwofoldRepetitionDraw"/>, applied inside quiescence nodes).
    /// </summary>
    public bool QsUseTwofoldRepetitionDraw
    {
        get => _qsearch.UseTwofoldRepetitionDraw;
        set => _qsearch.UseTwofoldRepetitionDraw = value;
    }

    /// <summary>
    /// reference-style 4-term correction history.
    /// When true, the static eval is corrected by FOUR depth-weighted running
    /// averages of (score − staticEval): the pawn-key term, the WHITE and BLACK non-pawn
    /// material-key terms, and the countermove (piece-to) term — weights 99/100/100/96,
    /// applied as sum/64, and gated OFF on nodes that carry a TT move (the reference's
    /// <c>ttMove != null ? 0 : evalCorrection(...)</c>). The same weighted terms feed the
    /// squared complexity signal the reference consumes. When false, the legacy pawn-only additive
    /// design is used.
    ///
    /// DEFAULT FALSE (2026-09-08) -> **DEFAULT TRUE (2026-11-04, R2 owner bake)**: the SPRT vs
    /// the baseline (clean, no tablebases, UHO book, 400 games) returned −5.2 ± 23.5 Elo,
    /// LOS 33.2%, LLR −2.12 (72% toward H0) — neutral-to-negative, not a validated win. The same
    /// transfer failure class: the corrections sit inside the source engine's tuned margins,
    /// whereas PF feeds the ±127 cp corrections into its margins un-tuned. The v9 lever campaign then measured
    /// **+19.13 ± 22.40 Elo, LLR +0.45 (LOS 95.35 %, 400 games)** — still formally INCONCLUSIVE,
    /// and the v8 campaign had −16.52, so this is a net-dependent reversal. It is baked ON by
    /// explicit owner decision (R2 banner at the top of this file), not by a passing verdict.
    /// Revert: set this property back to false.
    /// </summary>
    public bool UseRefCorrectionHistory
    {
        // Stored on the quiescence searcher so the SAME flag
        // drives the main-search correction and (when UseQsCorrectionHistory is on) the QS
        // stand-pat. Default true (R2) lives on the QS field.
        get => _qsearch.UseRefCorrectionHistory;
        set => _qsearch.UseRefCorrectionHistory = value;
    }

    /// <summary>Null-move pruning: skip a turn at depth >= 3 when static eval >= beta (calibrated to the reference 1024 NNUE: R = 4 + depth/3 + evalBonus).</summary>
    public bool UseNullMovePruning { get; set; } = true;
    /// <summary>Late move reductions: search late quiet moves at reduced depth, re-search on fail-high.</summary>
    public bool UseLateMoveReductions { get; set; } = true;
    /// <summary>Late move pruning: skip additional quiet moves at depth &lt;= LmpMaxDepth beyond 8 + depth².</summary>
    public bool UseLateMovePruning { get; set; } = true; // History Pruning

    /// <summary>
    /// Late Move Pruning depth ceiling (Phase 4). Originally
    /// widened to 8, which cost -4 WAC positions and was reverted.
    /// Kept at the original value of 4 pending a re-test now that the accounting
    /// bugs are fixed and the move generator has been rewritten (both changed the
    /// surrounding move-loop context this pruning operates in).
    /// </summary>
    public int LmpMaxDepth { get; set; } = 4; // LMP gate: depth < 5; the depth-5 EBF cliff was measured on the OLD margins
    // Per-depth LMP limits 1..4, originally inherited verbatim as 3/6/12/24 (with
    // the not-improving variant halving them — the improving modifier adopts the
    // same bad-previous-move behaviour). Index = depth 0..4.
    // Direction experiment baked 2026-10-09: PF's 8+depth² thresholds at d1-4
    // (9/12/17/24, previously 3/6/12/24 — the old engine pruned MORE than PF at
    // d1-3). Revert to the inherited shape on a losing verdict.
    public int[] LmpLimitByDepth { get; set; } = { 0, 9, 12, 17, 24 };
    // Per-depth LMP cells as hidden-UCI scalars (G-group SPSA, 2026-09-24):
    // `setoption name LmpLimitByDepth2 value <v>` rewrites that cell for the
    // next search. Index 0 (depth 0) is intentionally NOT exposed. Defaults
    // reproduce the table verbatim; AdvertisedSearchOptionTests lock them.
    public int LmpLimitByDepth1 { get => LmpLimitByDepth[1]; set => LmpLimitByDepth[1] = value; }
    public int LmpLimitByDepth2 { get => LmpLimitByDepth[2]; set => LmpLimitByDepth[2] = value; }
    public int LmpLimitByDepth3 { get => LmpLimitByDepth[3]; set => LmpLimitByDepth[3] = value; }
    public int LmpLimitByDepth4 { get => LmpLimitByDepth[4]; set => LmpLimitByDepth[4] = value; }

    /// <summary> FORMULA LMP (default OFF). When ON, the live
    /// late-move-pruning path uses the reference's linear thresholds instead of the clamped
    /// `LmpLimitByDepth` array:
    /// <c>normal = (3 + 38·depth)/10</c>, <c>optimistic = (1 + 83·depth)/10</c> at
    /// `depth ≤ RefLmpDepth(8)`, where optimistic means
    /// `improving || staticEval >= beta` and the count compared is the TOTAL legal-move count
    /// (the reference's moveCount), not the quiet-only count. Shares the code path and the
    /// `RefLmp*` knobs with the legacy toggle `UseRefLmp` (both now carry the
    /// verified constants, so the two toggles are behaviourally identical — run ONE
    /// of them as the arm, never both). DEFAULT OFF: the table + improving-halving
    /// path is read exactly as before.</summary>
    public bool UseFormulaLmp { get; set; } = false;

    /// <summary>
    /// Move-loop SEE-based pruning depth ceiling (Phase 6).
    /// Skips obviously-losing captures (SEE &lt; -50) and hanging quiet moves (SEE &lt; -100) at
    /// depth &lt;= this value. Original value 2; audit plan proposes widening to 4. Cross-checked
    /// against the reference's own SEE pruning depth (default 10, tunable 6-12) -
    /// the reference is far more aggressive here, though its threshold is depth-scaled
    /// (`seeQuietMargin * depth`) rather than PawnForge's flat -50/-100 margins, so the two are
    /// not directly comparable; PawnForge's own conservative depth-4 target is tested first.
    /// </summary>
    public int SeePruningMaxDepth { get; set; } = 8; // 8 with UseRefSeeThresholds=true (measured +21.74); the older reverted value was 4
    
    /// <summary>Reverse futility pruning: stand pat at depth &lt;= 8 when static eval >= beta + margin (calibrated to the reference 1024 NNUE: base 64, improving delta 60).</summary>
    public bool UseReverseFutilityPruning { get; set; } = true;
    
    /// <summary>
    /// Move-loop futility pruning: a shallow-depth optimization that skips quiet non-pawn moves
    /// when the static evaluation is significantly below alpha. Calibrated to the reference 1024 NNUE
    ///: margin scales with depth (68/ply) and move count (4/move).
    /// </summary>
    public bool UseFutilityPruning { get; set; } = true;
    
    /// <summary>
    /// Maximum search depth at which move-loop futility pruning is applied. Default is 7 plies
    /// (the reference fpDepth = 7).
    /// </summary>
    public int FutilityMaxDepth { get; set; } = 7; // baked 2026-10-09: gate depth 7 (was 4 in the design reference); direction experiment — revert on a losing 300-game verdict

    /// <summary> LINEAR in-loop futility margin (default OFF).
    /// When ON, the LIVE futility path replaces the texel step cells (61/144/268/334, clamped at
    /// index 4) with the reference's linear shape:
    /// <c>margin = FutilityMargin(108) + FutilityScale(82)·depth + hist/98 − 4·legalMoves</c>
    ///; the same margin the dormant path uses, on NOMINAL depth).
    /// The linear form extends futility reach inside the shipped `depth ≤ FutilityMaxDepth(7)`
    /// gate (depth 7 ≈ 682 vs the texel 334 cap) and the history term lets a good quiet buy a
    /// wider tolerance. The legacy comparator (`staticEval + margin &lt; alpha`) and the
    /// quiet/non-pawn/first-move guards are unchanged — this is a MARGIN-SHAPE arm, not the
    /// reducedDepth-gated behaviour. DEFAULT OFF: the texel array is read exactly as before.</summary>
    public bool UseLinearFutilityMargin { get; set; } = false;
    
    /// <summary>
    /// reference-path futility base (UseRefFutilityFormula, default
    /// OFF) — the LIVE path uses FutMarginByDepth. Value kept for the dormant path.
    /// Corrected 96 → 108: the reference's value is 108 (the earlier
    /// port used stale values).
    /// Also the base of the `UseLinearFutilityMargin` live-path arm.
    public int FutilityMargin { get; set; } = 108;
    /// <summary>
    /// (historical doc) Base evaluation margin used in the the reference futility formula.
    /// </summary>
    // Per-depth futility margins, originally transcribed verbatim as
    // 61/144/268/334 for depth ≤1/2/3/4. Index = depth 0..4.
    public int[] FutMarginByDepth { get; set; } = { 0, 61, 144, 268, 334 };
    // Per-depth futility cells as hidden-UCI scalars (same pattern as LMP above).
    public int FutMarginByDepth1 { get => FutMarginByDepth[1]; set => FutMarginByDepth[1] = value; }
    public int FutMarginByDepth2 { get => FutMarginByDepth[2]; set => FutMarginByDepth[2] = value; }
    public int FutMarginByDepth3 { get => FutMarginByDepth[3]; set => FutMarginByDepth[3] = value; }
    public int FutMarginByDepth4 { get => FutMarginByDepth[4]; set => FutMarginByDepth[4] = value; }
    // [PHASE-1] FutilityMoveCountMult REMOVED — the design reference's futility is flat depth·FutMargin.
    /// <summary>Razoring: drop to quiescence at depth &lt;= 4 when static eval + 473·depth &lt; alpha (the reference engine).</summary>
    public bool UseRazoring { get; set; } = true;
    /// <summary>
    /// Improving / opponent-worsening flags: widen the Reverse Futility Pruning and
    /// razoring margins, and lower the Late Move Pruning threshold, when the current
    /// position is NOT improving (staticEval &lt;= staticEval 2 plies ago) or when the
    /// opponent's last move did not measurably help them. Computed from data already
    /// tracked in ThreadData.EvalStack - no new per-node state. When this toggle is
    /// off the margins fall back to their flat form exactly.
    ///
    /// RE-ENABLED (default on) for the 2.0 development line, which is focused on search
    /// depth. In isolation this improved the tactical solve rate at a measured NPS cost;
    /// it is switched back on here as the starting point for depth work, where an
    /// "improving" signal is intended to feed the reduction schedule (see UseCutNode and
    /// the reduction block) rather than only the static-pruning margins. Disabling the
    /// toggle still reproduces the flat pre-flag margins exactly.
    /// </summary>
    public bool UseImprovingFlag { get; set; } = true; // Phase 1 (2026-09-04): restored from v1.1
    /// <summary>
    /// Secondary "small ProbCut": a pure comparison against an already-probed TT LowerBound
    /// entry (no move generation, no child search). Fires even when the entry is too shallow
    /// for the main TT-cutoff path, as long as it is within 4 plies of the current depth and
    /// independently supports a cutoff at beta + margin. Always guarded so it never fires at
    /// the root (the root must always complete its move loop). Disabling this toggle removes
    /// the check entirely, reproducing the pre-feature behavior exactly.
    ///
    /// RE-ENABLED (default on) for the 2.0 development line. Disabling the toggle removes
    /// the check entirely, reproducing the pre-feature behavior exactly.
    /// </summary>
    public bool UseSecondaryProbCut { get; set; } = false; // de-stacking SPRT arm (2026-09-09): default OFF, baked in like the other stacked layers (stacks with UseProbCut=false — both PF-only layers off together)
    /// <summary>
    /// Main ProbCut (appendix, row 1 — PAWN-FORGE-ONLY; the reference has NONE).
    /// The reference explicitly never implemented ProbCut because it "causes verification
    /// churn with this net" — the exact net PawnForge runs. Every verification search
    /// costs NPS, and the churn interacts with the LMR v2 reduction weights. DEFAULT ON
    /// (the pre-restructure behavior) but this toggle now exists so the next gate session can A/B
    /// "the reference's system" (ProbCut off) against the PF-only layer.
    /// </summary>
    public bool UseProbCut { get; set; } = false; // de-stacking SPRT arm (2026-09-09): default OFF, baked in like the other stacked layers (stacks with UseSecondaryProbCut=false — both PF-only layers off together)
    /// <summary>
    /// Shaves 1-2 plies off the full-depth zero-window re-search that follows a reduced-search
    /// fail-high in the PVS ladder, when the original LMR reduction was large (a move that
    /// "wanted" a big reduction is unlikely to need full-depth re-verification either). Never
    /// re-searches shallower than newDepth - reduction (the depth it is improving on), and does
    /// NOT touch the LMR formula itself. Disabling this toggle makes the re-search always use the
    /// full newDepth, reproducing the pre-feature behavior exactly.
    ///
    /// RE-ENABLED (default on) for the 2.0 development line. Disabling the toggle makes the
    /// re-search always use the full newDepth, reproducing the pre-feature behavior exactly.
    /// </summary>
    public bool UseAspirationReSearchReduction { get; set; } = true; // Phase 1 (2026-09-04): restored from v1.1
    /// <summary>
    /// Cut-node-aware Late Move Reductions (Search Depth Wave 2, item 1). A "cut node" is an
    /// interior node the search predicts will fail high (produce a beta cutoff). Such nodes
    /// need only enough search to find ONE refuting move, so late moves there can be reduced
    /// harder. When this toggle is on, the LMR reduction is increased by one ply at nodes
    /// flagged as cut nodes; this narrows the tree and is the primary lever for reaching
    /// greater depth in the same time budget.
    ///
    /// The cut-node flag itself is threaded through the search on every call regardless of
    /// this toggle (it costs nothing when unused); the toggle gates only the APPLICATION of
    /// the extra reduction, so turning it off reproduces the pre-Wave-2 reduction exactly.
    /// </summary>
    // Re-enabled in Phase 1 (2026-09-04) alongside v1.1 configuration; +1 reduction at predicted cut nodes.
    public bool UseCutNode { get; set; } = true; // Phase 3c (2026-09-05): testing independently after 3a/3b failed
    /// <summary>
    /// Aggressive table-based Late Move Reduction schedule (Search Depth Wave 2, EBF plan).
    /// Replaces the legacy linear formula (1 + moveCount/8, +1 at depth >= 6) with a precomputed
    /// reduction[depth][moveNumber] table whose values grow with ln(depth)*ln(moveNumber), tuned
    /// to reduce MORE than the legacy formula at moderate and high depth. The explicit goal is to
    /// lower the effective branching factor (EBF = nodes^(1/depth)) toward the reference target,
    /// which is what buys additional search depth at a fixed node budget.
    ///
    /// The earlier version of this table subtracted reductions for improving / PV nodes and
    /// REGRESSED depth (it widened the tree). Those "reduce less" adjustments were removed; the
    /// history and cut-node adjustments still apply on top of the base for both paths. Disabling
    /// this toggle restores the exact legacy formula.
    /// </summary>
    public bool UseLmrTable { get; set; } = true; // Phase 3a (2026-09-05): testing ln×ln table alone (without modulating terms)

    /// <summary>
    /// LMR Modulating Term 1 ("NOT improving" → reduce more, see the Negamax move loop).
    /// A/B toggle added 2026-09-06 during the Term-1 slow-mate investigation, so the term can
    /// be flipped without commenting code in/out. DISABLED by default — root-caused to a
    /// node-count BLOWUP (not reduction) in positions with an unstable/oscillating PV: the
    /// `improving` signal flips inconsistently between iterative-deepening passes in such
    /// positions, so each new iteration's reduction pattern doesn't align with the previous
    /// iteration's TT-seeded tree, forcing re-search churn that compounds every depth. See
    /// Phase 3 RE-TEST (2026-09-06) for the
    /// measured node-growth data that pinpointed this.
    /// </summary>
    public bool UseTerm1NotImproving { get; set; } = false;

    // ═══════════════════════════════════════════════════════════════════════════════
    // LMR v2 
    // ═══════════════════════════════════════════════════════════════════════════════
    // Master switch for the reference's SPSA-tuned fixed-point LMR architecture (model:
    // the reference search LMR block +.
    // Replaces the whole-ply v1 schedule with a 1024ths-of-a-ply accumulator, two base
    // tables (quiet / noisy captures), nine modulating terms at the reference's exact tuned
    // weights, and the reference's gate (depth >= 2, moveCount >= 3, PV >= 4). Every term has
    // its own sub-toggle so a stack regression can be bisected in minutes; the master
    // toggle off reproduces the v1 schedule exactly.
    //
    // DESIGN RATIONALE : the reference's "not improving" term is +275/1024 ≈ +0.27
    // ply, NOT +1.0 ply — PawnForge's earlier whole-ply Term 1 was ~4× too strong, which
    // caused the unstable-PV node blowup. All v2 terms run at sub-ply granularity with
    // the reference's weight, so the same signal modulates instead of dominating.
    /// <summary>LMR v2 master switch: the reference's fixed-point reduction architecture.</summary>
    public bool UseLmrV2 { get; set; } = false; // SPRT-neutral (0.0 ± 18.2 Elo) — OFF reproduces the v1 LMR schedule exactly. Toggle retained for A/B via the hidden UCI option.

    // ── the reference's tuned weights ("Tunable" defaults, in 1024ths of a ply) ──
    // All weights re-verified against the reference
    // and corrected to the shipped values (the earlier port
    // used a different revision's numbers). Only read on the UseLmrV2 path (default OFF),
    // so the compiled default behaviour is unchanged.
    /// <summary>Term (a): ttPv node (PV or TT-proven PV) → reduce LESS. The reference's weight is 911.</summary>
    public int LmrV2TtPvWeight { get; set; } = 911;
    /// <summary>Term (b): cut node → reduce MORE. the reference LmrCutNode = 2085 (~2.04 ply,.</summary>
    public int LmrV2CutNodeWeight { get; set; } = 2085;
    /// <summary>Term (c): not improving → reduce MORE. the reference LmrNotImproving = 70 (~0.07 ply,.
    /// The default was 275 — a 4x transcription error, and the 8-term
    /// bisect proved that single weight WAS the whole v2 regression. 70 is the reference's real value.</summary>
    public int LmrV2NotImprovingWeight { get; set; } = 70;
    /// <summary>Term (d): plies since last full-window node → reduce MORE, per-ply weight.
    /// PF-ONLY EXTRA (not in the reference's term set); its sub-toggle now defaults OFF so the
    /// UseLmrV2 arm is the reference's term set.</summary>
    public int LmrV2PvDistanceMult { get; set; } = 63;
    /// <summary>Term (d): cap of the PV-distance term (PF-only extra).</summary>
    public int LmrV2PvDistanceMax { get; set; } = 742;
    /// <summary>Term (e): quiet history divisor. the reference LmrQuietHistoryDiv = 3258.</summary>
    public int LmrV2QuietHistoryDiv { get; set; } = 3258;
    /// <summary>Term (e): noisy history divisor. the reference LmrNoisyHistoryDiv = 3155.</summary>
    public int LmrV2NoisyHistoryDiv { get; set; } = 3155;
    /// <summary>Term (f): "LMR-futile" move (eval + margin ≤ alpha) → reduce MORE. the reference LmrFutile = 962.</summary>
    public int LmrV2FutileWeight { get; set; } = 962;
    /// <summary>Term (f): base of the LMR-futility margin. the reference LmrFutileMargin = 108.</summary>
    public int LmrV2FutileMargin { get; set; } = 108;
    /// <summary>Term (f): per-ply scale of the LMR-futility margin. the reference LmrFutileScale = 82.</summary>
    public int LmrV2FutileScale { get; set; } = 82;
    /// <summary>Term (f): history divisor of the LMR-futility margin. the reference LmrFutileHistDivisor = 98.</summary>
    public int LmrV2FutileHistDiv { get; set; } = 98;
    /// <summary>Term (g): parent node has failed high &gt; 2 times → reduce MORE. the reference LmrCutoffCount = 1035 (~1.01 ply,.</summary>
    public int LmrV2FailHighCountWeight { get; set; } = 1035;
    /// <summary>Term (h): eval-complexity divisor (reduce LESS in complex positions). the reference LmrComplexityDivisor = 6144 —
    /// NOT used verbatim: PawnForge carries pawn-only correction history (corr ±16384, squared), so the
    /// divisor is re-derived to 65536 (documented deviation, see UseLmrTermComplexity).</summary>
    public int LmrV2ComplexityDivisor { get; set; } = 65536;

    // ── Gate values ──
    /// <summary>LMR v2 minimum depth. the reference LmrDepth = 2 (v1 used 3).</summary>
    public int LmrV2MinDepth { get; set; } = 2;
    /// <summary>LMR v2 minimum move index at non-PV nodes. the reference LmrMinMoves = 3 (v1 used &gt;3).</summary>
    public int LmrV2MinMoves { get; set; } = 3;
    /// <summary>LMR v2 minimum move index at PV nodes. the reference LmrMinPvMoves = 4.</summary>
    public int LmrV2MinPvMoves { get; set; } = 4;

    // ── Per-term sub-toggles (all default ON; each can be flipped to bisect a regression) ──
    /// <summary>Term (a): ttPv reduce-less. Requires the TT PV flag (TT layout change).</summary>
    public bool UseLmrTermTtPv { get; set; } = true;
    /// <summary>Term (b): cut-node reduce-more at the reference's +2085 weight (replaces v1's flat +1 ply inside v2 mode).</summary>
    public bool UseLmrTermCutNode { get; set; } = true;
    /// <summary>Term (c): not-improving reduce-more at the reference's +70 weight (was
    /// mis-transcribed as 275) — the corrected Term 1 (v1's UseTerm1NotImproving stays off for the
    /// legacy path).</summary>
    public bool UseLmrTermNotImproving { get; set; } = true;
    /// <summary>Term (d): PV-distance reduce-more (continuous, up to +742). PF-ONLY EXTRA —
    /// not part of the reference's term set, so it defaults OFF; flip it
    /// ON only as a separate follow-up arm.</summary>
    public bool UseLmrTermPvDistance { get; set; } = false;
    /// <summary>Term (e): history reduce-less/more (quiet div 3258, noisy div 3155), history clamped to the reference's ±8033 range.</summary>
    public bool UseLmrTermHistory { get; set; } = true;
    /// <summary>Term (f): LMR-futile reduce-more (+962 when eval + margin ≤ alpha).</summary>
    public bool UseLmrTermFutile { get; set; } = true;
    /// <summary>Term (g): parent fail-high-count reduce-more (+1035 when &gt; 2 cutoffs at the parent stack slot).</summary>
    public bool UseLmrTermFailHighCount { get; set; } = true;
    /// <summary>
    /// Term (h): complexity reduce-less from correction history. DEVIATION from the reference (documented):
    /// the reference sums FOUR weighted squared correction terms (pawn, white non-pawn, black non-pawn,
    /// counter); PawnForge carries pawn-only correction, so the term uses
    /// pawnCorr² with a re-derived divisor (65536) chosen so the effective magnitude range matches
    /// the reference's multi-term sum (|corr| = 8192 → ~1 ply of reduce-less; capped at 2048).
    /// </summary>
    public bool UseLmrTermComplexity { get; set; } = true;
    /// <summary>
    /// (noisy LMR): allow captures into LMR via the noisy base table. the reference reduces
    /// captures with `(!isGoodNoisy || !ttPv)`; PawnForge's move loop has no per-move
    /// good/bad-noisy flag, so all captures are treated as "good noisy" (the conservative
    /// half of the reference's rule): they are reduced only at non-ttPv nodes. Promotions remain
    /// excluded (deviation, tactical safety). DEVIATION noted for review.
    /// </summary>
    public bool UseLmrNoisyMoves { get; set; } = false; // default OFF after the mate-in-4 battery regressed
    /// <summary>the reference clamps quiet history to ±8033 (quietHistMaxScore); PawnForge's tables run to ±200000, so the LMR history term clamps its input to this range to preserve the reference's intended weight.</summary>
    public int LmrV2HistoryClamp { get; set; } = 8033;
    /// <summary>Cap of the complexity term in 1024ths (2 plies of reduce-less max).</summary>
    public int LmrV2ComplexityCap { get; set; } = 2048;

    // ═══════════════════════════════════════════════════════════════════════════════
    // // : the reference's
    // move-loop pruning layer that the v2 reduction weights were tuned WITH. The first
    // sweep-only measurement proved the coupling: v2's base (+terms alone) regressed EBF
    // to 2.326 (baseline 2.149) because the reference's schedule assumes the pruning below is
    // killing whole move classes at reduced depth. Each sub-system has its own toggle
    // so the stack can be bisected; toggles off reproduce the pre- behavior exactly.
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reference's move-loop futility formula, gated on REDUCED depth (depth − LMR
    /// reduction) instead of nominal depth: margin = fpMargin(96) + fpScale(76)·reducedDepth
    /// + history/87 − movesSearched·3, condition eval + margin ≤ alpha.
    /// Deviations from the reference: quiet PAWN moves stay exempt
    /// (PawnForge's deliberate strategic-irreversibility guard) and the first legal move of
    /// a node is never pruned (the design reference's expandedNodes == 1 exemption). Toggling off
    /// restores the legacy flat-margin formula at nominal depth.
    /// </summary>
    /// <summary>
    /// : the reference's move-loop futility margin formula + reducedDepth gate. DEFAULT OFF —
    /// both halves were bisected to FAIL the mate-in-4 battery independently (2026-09-07,
    /// position Q7/p1p1q1pk...: proved mate 5 instead of 4): (1) the reducedDepth gate
    /// (futility firing at deep nominal depths for late moves) breaks the mate even with
    /// the legacy margin; (2) the reference's margin SHAPE (108 + 82·reducedDepth + hist/98 − 4·moves,
    /// ≤ alpha; the reference's values prune noticeably more than
    /// the legacy 96 + 68·depth + 4·moves &lt; alpha
    /// at the same gate and also breaks it. the reference's SPSA values assume his uncapped check
    /// extension and horizon profile; PawnForge's capped extension budget (MaxPathExtensions)
    /// changes the calculus. The legacy formula remains the default; this toggle stays for
    /// SPRT-driven A/B per(a single WAC/mate miss must not hard-block a
    /// potentially +EBF feature — that is what SPRT is for).
    /// </summary>
    public bool UseRefFutilityFormula { get; set; } = false;
    /// <summary>
    /// sub-toggle: gate move-loop futility on REDUCED depth (depth − LMR reduction), as
    /// the reference does (fpDepth = 8) so late moves get futility-pruned at deep nominal nodes.
    /// DEFAULT OFF — measured tactical regression: with the reducedDepth gate the
    /// mate-in-4 battery regressed (Q7/p1p1q1pk... proved mate 5 instead of 4; bisected
    /// 2026-09-07 to this exact gate, NOT the margin formula — the reference's 108 + 82·reducedDepth +
    /// hist/98 − 4·moves margin with a nominal-depth gate passes the full battery). The
    /// deep gate presumably interacts with PawnForge's capped extension budget (the reference has
    /// no cap), which changes the horizon profile the gate was SPSA-tuned against. Revisit
    /// only via the WAC + mate-battery + SPRT gates per
    /// margin formula still applies but gates on nominal depth ≤ RefFpDepth (8).
    /// </summary>
    public bool UseFutilityReducedDepthGate { get; set; } = false;
    /// <summary>Reference-path futility depth ceiling (8; the legacy path keeps FutilityMaxDepth = 7 at nominal depth).</summary>
    public int RefFpDepth { get; set; } = 8;
    /// <summary>Reference-path futility margin per-ply scale (82; the legacy path uses 68 at nominal depth). Corrected 76 → 82.</summary>
    public int FutilityScale { get; set; } = 82;
    /// <summary>Futility-margin history divisor (good history widens the margin). Corrected 87 → 98.</summary>
    public int FutilityHistDiv { get; set; } = 98;
    /// <summary>Futility-margin per-searched-move subtraction. Corrected 3 → 4.</summary>
    public int FutilityMoveMult { get; set; } = 4;

    /// <summary>
    /// : the reference's history pruning on REDUCED depth: skip quiet moves whose combined
    /// history score is below (hpMargin·depth + hpOffset) = −2366·depth − 1007, at
    /// reducedDepth ≤ hpMaxDepth (5). Note the gate uses reducedDepth but the threshold
    /// scales with the NOMINAL depth, exactly as the reference does; the
    /// trigger also aborts all remaining quiets (SkipQuiets) like every the reference move-loop
    /// pruning. Toggling off restores the legacy depth ≤ 3 / −2000·depth rule.
    /// </summary>
    public bool UseRefHistoryPruning { get; set; } = false; // [REVERTED C1 2026-09-07] part of the reverted C1 stack; OFF restores the legacy depth<=3 history pruning
    /// <summary>: history-pruning depth ceiling on reduced depth. the reference hpMaxDepth = 5.</summary>
    public int RefHpMaxDepth { get; set; } = 5;
    /// <summary>: history-pruning per-ply margin. the reference hpMargin = −2366.</summary>
    public int RefHpMargin { get; set; } = -2366;
    /// <summary>: history-pruning constant offset. the reference hpOffset = −1007.</summary>
    public int RefHpOffset { get; set; } = -1007;

    /// <summary>
    /// : the reference's LMP thresholds, linear in depth, compared against the TOTAL legal move
    /// count: (lmpBase + lmpScale·depth)/10 = (3 + 39·d)/10 when not optimistic,
    /// (lmpImpBase + lmpImpScale·depth)/10 = (1 + 87·d)/10 when optimistic
    /// (improving or eval ≥ beta), at depth ≤ 8 (the reference's LMP ceiling) — the
    ///. This is the CORRECTED retry of the rejected Phase-4 widening:
    /// the cliff there is explained by the quadratic threshold formula plus the untuned
    /// improving coupling, not by the depth ceiling itself. Toggling off restores the
    /// legacy 8 + depth² formula at LmpMaxDepth compared against the QUIET move count.
    /// </summary>
    public bool UseRefLmp { get; set; } = false; // [REVERTED C1 2026-09-07] part of the reverted C1 stack; OFF restores the legacy 8+depth² LMP at LmpMaxDepth=4
    /// <summary>LMP depth ceiling for the reference path (8; the legacy path keeps LmpMaxDepth = 4).</summary>
    public int RefLmpDepth { get; set; } = 8;
    /// <summary>LMP base when not optimistic (3).</summary>
    public int RefLmpBase { get; set; } = 3;
    /// <summary>LMP per-ply scale when not optimistic (38; corrected 39 → 38).</summary>
    public int RefLmpScale { get; set; } = 38;
    /// <summary>LMP base when optimistic (improving or eval ≥ beta) (1).</summary>
    public int RefLmpImpBase { get; set; } = 1;
    /// <summary>LMP per-ply scale when optimistic (83; corrected 87 → 83).</summary>
    public int RefLmpImpScale { get; set; } = 83;

    /// <summary>
    /// : Alpha-raise reduction (the reference: once a move has RAISED
    /// alpha at a node (without beta-cutoff), the remaining late moves are searched one ply
    /// shallower (depth-- for the rest of the loop) — multiple moves rarely raise alpha in
    /// turn, and the one that did is already fully re-searched by the PVS ladder. Gated to
    /// AlphaRaiseMinDepth &lt; depth &lt; AlphaRaiseMaxDepth and non-mate scores, exactly as
    /// the reference tunes it. Toggling off restores the pre-restructure behavior exactly.
    /// </summary>
    public bool UseAlphaRaiseReduction { get; set; } = false; // [REVERTED C1 2026-09-07] part of the reverted C1 stack (C8a); OFF restores pre-C1 behavior
    /// <summary>: alpha-raise minimum depth. the reference alphaReductionMinDepth = 2.</summary>
    public int AlphaRaiseMinDepth { get; set; } = 2;
    /// <summary>: alpha-raise maximum depth. the reference alphaReductionMaxDepth = 12.</summary>
    public int AlphaRaiseMaxDepth { get; set; } = 12;

    /// <summary>
    /// : score-adaptive re-search (the reference — replaces the static
    /// re-search shave when a reduced move raises alpha: the re-search depth is adjusted
    /// ±1 by how the reduced score compares to the best-so-far score (doDeeperSearch when
    /// score &gt; bestScore + 37 + 5·newDepth — the move rockets above expectations and earns
    /// an extra ply; doShallowerSearch when score &lt; bestScore + newDepth — an unimpressive
    /// fail-high saves a ply). The re-search never goes shallower than newDepth − reduction
    /// (the depth it is improving on). Toggling off restores the static shave.
    /// </summary>
    public bool UseAdaptiveReSearch { get; set; } = false; // [REVERTED C1 2026-09-07] part of the reverted C1 stack (C3); OFF restores the static re-search shave (ComputeReSearchDepth)
    /// <summary>: doDeeper threshold base. the reference lmrDeeperBase = 37.</summary>
    public int LmrV2DeeperBase { get; set; } = 37;
    /// <summary>: doDeeper threshold per-ply scale. the reference lmrDeeperScale = 5.</summary>
    public int LmrV2DeeperScale { get; set; } = 5;
    /// <summary>
    /// : Bad Noisy Pruning (the reference: when even a WINNING capture
    /// cannot lift the eval to alpha (staticEval + 113·depth + 383·moves/133 ≤ alpha), the
    /// first bad-noisy move (SEE &lt; 0) breaks out of the move loop entirely — killing all
    /// remaining captures at once — and the fail-soft bookkeeping records the margin as the
    /// node's lower-bound score so the TT still stores something meaningful. Toggling off
    /// removes the break entirely.
    /// </summary>
    public bool UseBadNoisyPruning { get; set; } = false; // [REVERTED C1 2026-09-07] part of the reverted C1 stack (C5); OFF removes the bad-noisy break
    /// <summary>: BNP depth ceiling. the reference bnpDepth = 6.</summary>
    public int BnpDepth { get; set; } = 6;
    /// <summary>: BNP margin per-ply scale. the reference bnpScale = 113.</summary>
    public int BnpScale { get; set; } = 113;
    /// <summary>: BNP margin per-move offset numerator. the reference bnpOffset = 383.</summary>
    public int BnpOffset { get; set; } = 383;
    /// <summary>: BNP margin per-move offset divisor. the reference bnpDivisor = 133.</summary>
    public int BnpDivisor { get; set; } = 133;

    /// <summary>
    /// The reference's depth-SCALED SEE pruning thresholds: quiets −43·depth, noisies
    /// −26·depth², relaxed by history/128 (good history narrows the pruning window),
    /// with the ceiling raised to 10. The tightening thresholds are what make the deep ceiling safe — the
    /// Phase-6 lesson (of the audit): never widen a ceiling without porting the
    /// threshold formula. Toggling off restores the flat −50/−100 thresholds; the ceiling
    /// (SeePruningMaxDepth) applies to both paths.
    /// </summary>
    public bool UseRefSeeThresholds { get; set; } = true; // measured +21.74 (with SeePruningMaxDepth=8); was false before this override
    /// <summary>: quiet SEE-pruning per-ply margin. the reference seeQuietMargin = −43.</summary>
    public int SeeQuietMargin { get; set; } = -43;
    /// <summary>: noisy SEE-pruning per-ply² margin. the reference seeNoisyMargin = −26.</summary>
    public int SeeNoisyMargin { get; set; } = -26;
    /// <summary>: SEE-pruning history relax divisor. the reference seeHistoryDivisor = 128.</summary>
    public int SeeHistoryDivisor { get; set; } = 128;


    /// <summary>Recapture extension: +1 depth when capturing on the previous move's target square. DISABLED — caused a depth collapse in capture-rich positions.</summary>
    public bool UseRecaptureExtension { get; set; } = false; // DISABLED: caused a depth collapse in capture-rich positions
    /// <summary>Single-response extension: +1 depth at nodes with exactly one pseudo-legal reply (gate: depth ≥ 3, not in check).</summary>
    public bool UseSingleResponseExtension { get; set; } = true; // Phase 1 (2026-09-04): restored from v1.1

    /// <summary>
    /// Negative extensions (PF-only layer): −1 depth for bad-SEE captures and historically bad
    /// quiets at depth ≥ 3, non-PV, not in check. Default ON — the SPRT (2026-09-09, 30+1)
    /// REJECTED the OFF arm at −19.3 ± 27.6 Elo (H0 accepted, LLR −2.96): the layer is worth
    /// its cost. OFF remains settable via hidden setoption for re-tests.
    /// </summary>
    public bool UseNegativeExtensions { get; set; } = true;

    /// <summary>
    /// Per-path extension counter cap (v2.0 Consolidated Audit). Replaces the old flat
    /// "check extension capped at ply &lt; 15" wall - which the audit's own head-to-head vs the reference
    /// and vs the design reference (an independent reference engine) identified as the likely bottleneck behind
    /// PawnForge's seldepth deficit (endgame seldepth gap +22..+23 vs both references, while
    /// nominal-depth gap is only +5..+7).
    ///
    /// NEITHER reference engine uses an absolute ply wall:
    /// - the reference extends checks UNCONDITIONALLY (`if (inCheck) extension = 1`), relying solely on
    /// the global MAX_DEPTH safety net.
    /// - the design reference gates its (singular) extension on `ply &lt; Depth * 2` - relative to the CURRENT
    /// iterative-deepening depth, so the reach grows automatically as the search goes deeper.
    /// PawnForge adopts the audit's synthesis of both: a genuine PER-PATH extension counter
    /// (the strongest mainstream engines' decreasing-extensions style) threaded through Negamax alongside cutNode/excludedMove,
    /// incremented by however many plies were added at this node and capped at
    /// <see cref="MaxPathExtensions"/> total plies of accumulated extension along one search path.
    /// This is a bound on ACCUMULATED extension depth, not a bound on ply position - so a forcing
    /// line that has not yet used its extension budget can keep extending arbitrarily deep (like
    /// the reference/the design reference), while a line that has already extended heavily is capped (the safety valve
    /// the audit's own gap-matrix item E asked for). Disabling this toggle reproduces the exact
    /// pre-existing `ply &lt; 15` legacy behavior for A/B comparison.
    /// </summary>
    public bool UsePerPathExtensionCap { get; set; } = true;

    /// <summary>
    /// Maximum accumulated check/single-response extension depth (in plies) permitted along one
    /// search path when <see cref="UsePerPathExtensionCap"/> is enabled. Chosen generously (32 -
    /// double PawnForge's old flat ply-15 reach) so it acts as a safety valve against pathological
    /// perpetual-check lines rather than a routine limiter; ordinary forcing lines (mating attacks,
    /// check chases) will exhaust before hitting it. Tunable; re-measure EBF/seldepth/WAC if changed.
    /// </summary>
    public int MaxPathExtensions { get; set; } = 32;

    /// <summary>
    /// Initial half-width (cp) of the progressive aspiration window, i.e. the width of
    /// <c>[score − delta, score + delta]</c> the iterative-deepening loop centres on the
    /// previous iteration's score from <see cref="AspirationStartDepth"/> on. Before the
    /// 2026-10-15 audit this was a <c>private const 50</c> inside
    /// <c>IterativeDeepening</c>, so it was not a lever at all — not even a hidden one:
    /// no match runner and no SPSA pass could reach it, and the "narrow the window"
    /// experiment could only be done by editing source and rebuilding per arm.
    ///
    /// Why the value is 50 today, and why it is now tunable: a narrower window is faster
    /// (a tight band makes more nodes fail high/low early and get pruned, buying depth),
    /// BUT it is also the alpha/beta band the static-pruning heuristics — Reverse
    /// Futility Pruning and razoring — compare the static eval against. Narrowing the
    /// window therefore silently makes those heuristics prune HARDER without one pruning
    /// constant changing. The recorded ladder from the original investigation (docs):
    /// 25 cp broke the mate-in-2 battery (RFP/razoring over-pruned the mating branch),
    /// 35 cp held tactics but COST depth via re-search churn, 40 cp was flat. That is why
    /// the lever now exists: the trade-off is real, the margins have since been re-based to
    /// the texel per-depth tables (RFP 102/210/267/394 at depth ≤ 4, razor 86/353/353 at
    /// depth ≤ 3) rather than the old 64·depth-at-depth-8 formula that made 25 cp fatal,
    /// and only a match can re-price it.
    ///
    /// Reference values: the reference <c>AspDelta = 17</c> (widening ×135/100, min depth 4),
    /// the design reference's ladder <c>[33,100,300,900,2700,∞]</c> from <c>AspMinDepth 7</c>. The
    /// audit's recommended first run is 33 (the design reference's ladder start), and the minimum of
    /// 10 is the reference's neighbourhood, not a floor anyone has tested.
    /// </summary>
    public int AspirationBase { get; set; } = 50;

    /// <summary>
    /// First iterative-deepening depth at which the aspiration window is used at all.
    /// Below this depth the full window is used, because shallow scores are volatile
    /// enough that a ±50 cp band would only cause re-searches. Matches the pre-audit
    /// behaviour (<c>d &gt;= 5</c>) exactly.
    /// </summary>
    public int AspirationStartDepth { get; set; } = 5;

    /// <summary>
    /// Maximum number of aspiration re-search cycles before giving up and re-searching
    /// with the full window once (the safety valve for a wildly oscillating score).
    /// </summary>
    public int AspirationMaxCycles { get; set; } = 8;

    // ═══════════════════════════════════════════════════════════════════════════════
    // Track 2 (SPSA) margin properties — wired literals, defaults bit-identical
    // ═══════════════════════════════════════════════════════════════════════════════
    // Previously hardcoded search-margin literals, exposed as public ints so the
    // hidden-UCI reflection (EngineOrchestrator.BuildSearchOptionProps) picks them
    // up automatically — SPSA/fastchess can set `option.<Name>=<value>` per arm
    // with no rebuild. Every default reproduces the exact legacy literal; the
    // AdvertisedSearchOptionTests lock them. Aspiration (50cp, investigated A1,
    // parked) and the LMRv1 ln×ln table shape are deliberately NOT included.
    /// <summary>RFP gate ceiling in plies (was literal 8 in Negamax).</summary>
    public int RfpMaxDepth { get; set; } = 4; // RFP gate: depth < 5
    /// <summary>RFP base margin in cp per ply (was 64).</summary>
    // Per-depth RFP margins, originally transcribed verbatim as
    // 102/210/267/394 for depth ≤1/2/3/4, with the gate at depth < 5;
    // no improving term (the cross-engine standard shape).
    // Index = depth 0..4.
    public int[] RfpMarginByDepth { get; set; } = { 0, 102, 210, 267, 394 };
    // Per-depth RFP cells as hidden-UCI scalars (same pattern as LMP above).
    public int RfpMarginByDepth1 { get => RfpMarginByDepth[1]; set => RfpMarginByDepth[1] = value; }
    public int RfpMarginByDepth2 { get => RfpMarginByDepth[2]; set => RfpMarginByDepth[2] = value; }
    public int RfpMarginByDepth3 { get => RfpMarginByDepth[3]; set => RfpMarginByDepth[3] = value; }
    public int RfpMarginByDepth4 { get => RfpMarginByDepth[4]; set => RfpMarginByDepth[4] = value; }
    /// <summary>RFP margin tightening when improving (the reference: -60).</summary>
    // Improving-awareness RESTORED (was removed in PHASE-1). the reference's
    // additive-delta shape: margin -= improving ? 60 : -2,
    // then -13 more when the opponent is worsening. Here the deltas are ADDED
    // to the texel base margin, so the reference's exact shape is
    // RfpImprovingDelta=-60, RfpNotImprovingDelta=+2,
    // RfpOpponentWorseningDelta=-13. ALL THREE DEFAULT TO 0 — the engine's
    // compiled behaviour stays the flat texel margin (bit-identical to the
    // PERF-BASELINE numbers) until a value is deliberately set (setoption /
    // fastchess) or a stamped default change follows a measured verdict.
    // Applied only when UseImprovingFlag is on (the same gate the reference uses).
    public int RfpImprovingDelta { get; set; } = 0;
    /// <summary>RFP margin widening when NOT improving (the reference: +2).</summary>
    public int RfpNotImprovingDelta { get; set; } = 0;
    /// <summary>RFP extra tightening when the opponent is worsening (the reference: -13).</summary>
    public int RfpOpponentWorseningDelta { get; set; } = 0;

    // ── FORMULA-BASED RFP (Phase 1 of the recommended order) ──
    // The clamped per-depth array above stops at depth 4 and, if its gate is merely raised,
    // clamps depths 5–8 to the 394 cp cell (the "Clamping Trap", Full-Audit). Both
    // reference engines use LINEAR margins that scale to any depth (the reference 63·depth, the design reference
    // 109·depth). This toggle replaces the array with
    // margin = depth * (improving ? RfpImprovingScale : RfpNotImprovingScale)
    // and widens the gate to RfpFormulaMaxDepth. DEFAULTS TO OFF: the compiled behaviour
    // stays exactly the texel array + RfpMaxDepth gate (bit-identical, pinned by the
    // defaults tests) until an A/B arm sets UseFormulaRfp=true.
    /// <summary>B1: use the linear RFP margin formula + widened gate instead of the clamped array.
    /// DEFAULT TRUE for the special release (13.90); the pre-R1 default was false.</summary>
    public bool UseFormulaRfp { get; set; } = true;
    /// <summary>B1: RFP margin per ply when the side to move is improving (formula path).</summary>
    public int RfpImprovingScale { get; set; } = 65;
    /// <summary>B1: RFP margin per ply when NOT improving (formula path).</summary>
    public int RfpNotImprovingScale { get; set; } = 95;
    /// <summary>B1: RFP gate ceiling (plies) on the formula path (the reference/the design reference use 8–9).</summary>
    public int RfpFormulaMaxDepth { get; set; } = 8;
    /// <summary>Razoring gate ceiling in plies (was 4).</summary>
    public int RazorMaxDepth { get; set; } = 3; // razor gate at depth < 4
    /// <summary>Razoring margin in cp per ply (was 473).</summary>
    // Per-depth razor margins, originally transcribed verbatim as
    // 86 for depth ≤1 and 353 for depth 2-3. Index = depth 0..3.
    public int[] RazorMarginByDepth { get; set; } = { 0, 86, 353, 353 };
    // Per-depth razor cells as hidden-UCI scalars (same pattern as LMP above;
    // only depths 1-3 exist in this table).
    public int RazorMarginByDepth1 { get => RazorMarginByDepth[1]; set => RazorMarginByDepth[1] = value; }
    public int RazorMarginByDepth2 { get => RazorMarginByDepth[2]; set => RazorMarginByDepth[2] = value; }
    public int RazorMarginByDepth3 { get => RazorMarginByDepth[3]; set => RazorMarginByDepth[3] = value; }
    /// <summary>Razoring threshold easing when NOT improving (the reference: -20 — the
    /// razor fires on a smaller eval, i.e. prunes more when the position trends down).</summary>
    // the reference shape: val -= 20 when !improving. The
    // delta is SUBTRACTED from the razor threshold; DEFAULT 0 keeps the flat
    // texel threshold (bit-identical) until deliberately set. Applied only
    // when UseImprovingFlag is on (the reference's gate).
    public int RazorNotImprovingDelta { get; set; } = 0;
    /// <summary>NMP gate floor in plies (was 3).</summary>
    public int NmpMinDepth { get; set; } = 3;
    /// <summary>NMP base reduction in plies (was 3).</summary>
    public int NmpBaseReduction { get; set; } = 3; // base depth reduction 3 (the cross-engine standard shape)
    /// <summary>NMP depth divisor (was 4, i.e. depth/4).</summary>
    public int NmpDepthDivisor { get; set; } = 4; // depth divisor 4 (the cross-engine standard shape)
    /// <summary>NMP eval-bonus divisor (82 in the earlier design; 200 here).</summary>
    public int NmpBonusDivisor { get; set; } = 200; // divisor 200 baked 2026-10-09 (the earlier design used 82 — our reduction bonus grows 2.4x faster on big evals, the opposite shape); direction experiment — revert on a losing verdict
    /// <summary>NMP eval-bonus clamp max (3).</summary>
    public int NmpBonusMax { get; set; } = 3;
    /// <summary>Singular-extension gate floor in plies (was 8).</summary>
    public int SingularMinDepth { get; set; } = 8;
    /// <summary>Singular-beta slope in cp per ply (was 2).</summary>
    public int SingularBetaSlope { get; set; } = 2;
    /// <summary>Legacy history-pruning gate ceiling in plies (was 3).</summary>
    public int HistPruneMaxDepth { get; set; } = 3;
    /// <summary>Legacy history-pruning margin per ply in history units (was 2000).</summary>
    public int HistPruneMargin { get; set; } = 2000;
    /// <summary>LMR v1 gate floor in plies (was 3).</summary>
    public int LmrMinDepth { get; set; } = 3;
    /// <summary>LMR v1 gate floor in legal moves (was 3, i.e. moves &gt; 3).</summary>
    public int LmrMinMoves { get; set; } = 3;
    /// <summary>
    /// Quiescence delta-pruning margin in cp (was literal 1100 = queen 900 +
    /// margin 200). Passthrough to the private <see cref="_qsearch"/>.
    /// </summary>
    public int QsDeltaMargin
    {
        get => _qsearch.QsDeltaMargin;
        set => _qsearch.QsDeltaMargin = value;
    }

    /// <summary>
    /// Transposition-table probing inside quiescence.
    /// Passthrough to the private <see cref="_qsearch"/>. DEFAULTS TO OFF — with the toggle
    /// off not one TT instruction runs in QS and the compiled behaviour is bit-identical
    /// (pinned by QsTtProbeTests). Advertised automatically via the hidden-UCI reflection.
    /// </summary>
    public bool UseQsTtProbe
    {
        get => _qsearch.UseQsTtProbe;
        set => _qsearch.UseQsTtProbe = value;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // STRUCTURAL CLOSURES BAND 
    // ═══════════════════════════════════════════════════════════════════════════════
    // The structural closures: prune more with cheap verification and sharper eval
    // accuracy, chosen over further gate-widening after gate-widening was falsified
    // by measurement ("widen the gate = Elo" did not hold). EVERYTHING here ships
    // DEFAULT-OFF (the new behaviour is opt-in), so a fresh binary is bit-identical
    // to the pre-change engine. One arm per toggle, one variable per arm.

    /// <summary>TT-score-as-eval (`canUseTTScore`). When ON, a live TT probe
    /// whose score is CONSISTENT with this position's raw static eval (EXACT, or a
    /// LOWER bound ≥ the raw eval, or an UPPER bound ≤ it) REPLACES the raw + correction
    /// eval as the `staticEval` every pruner reads (RFP, razoring, futility, the
    /// LMR-futile margin, NMP's eval gate). The correction history is kept only when
    /// the entry has no usable score (the reference's shape). Both references skip this
    /// replacement while in check (the reference's static-eval block is gated on
    /// not-in-check; the design reference keeps the parent's eval), which this port mirrors. The quiescence half rides on
    /// `UseQsTtProbe` — with that toggle off QS never probes, so this becomes a
    /// main-search-only lever. DEFAULT OFF: a fresh binary prunes exactly as before.
    /// Stored on the quiescence searcher (passthrough) so ONE property drives both
    /// halves; Negamax reads it through this getter.</summary>
    public bool UseTtScoreEval
    {
        get => _qsearch.UseTtScoreEval;
        set => _qsearch.UseTtScoreEval = value;
    }

    /// <summary> Per-thread EVAL CACHE (default OFF).
    /// A second-level static-eval cache (Zobrist half + side-to-move + int16 score, 8-byte
    /// entries, sized `clamp(hash/2, 8, 1024)` MB) that catches transpositions the TT cannot
    /// serve: TT misses (bucket replacement), depth-0 QS entries and positions whose TT entry
    /// carries no cached `StaticEval`. A probe hit skips the entire NNUE forward pass, so this
    /// is a pure NPS→depth lever with ZERO search-structure risk (the cached value is
    /// bit-identical to a fresh `Evaluate`; a fixed-depth node count must not move).
    /// The cache is per-thread (never shared), lives on <see cref="ThreadData"/>, and is
    /// allocated lazily on first use. the design reference 2.1.0's `EvalCache` is the model (8-byte
    /// item, `hash % size` index, upper-half tag); its search never actually probes it, so the
    /// integration here is our own. Passthrough to the quiescence searcher so ONE option drives
    /// both the main-search static eval and the QS stand-pat.</summary>
    public bool UseEvalCache
    {
        get => _qsearch.UseEvalCache;
        set => _qsearch.UseEvalCache = value;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // STRUCTURAL CLOSURES BAND 3
    // ═══════════════════════════════════════════════════════════════════════════════
    // Extensions & feedback. Same discipline — default-OFF (or a default-OFF
    // master with default-ON sub-toggles for bisection), hidden, identity-gated, one
    // arm each.

    /// <summary> EXTENSION TRIO master toggle (default OFF).
    /// One campaign, three sub-toggles (all default ON inside the master) so a failure can be
    /// bisected without a rebuild:
    /// (a) `UseTrioHindsight` — the reference's hindsight extension: a child of a heavily reduced
    /// parent (prior reduction ≥ 3) whose combined eval fell below 0 gets the ply back.
    /// When the trio is ON this REPLACES the legacy unconditional hindsight pair
    /// (threshold 2 extension + the eval &gt; 62 reduce half) — adding a second extension on
    /// top would double-count the same signal. When the trio is OFF the legacy pair runs
    /// bit-identically.
    /// (b) `UseTrioAlphaRaise` — enable the existing alpha-raise reduction (the
    /// `UseAlphaRaiseReduction` code, 2 &lt; depth &lt; 12, never on mate scores) as part of
    /// the bundle. The standalone `UseAlphaRaiseReduction` lever still works independently.
    /// (c) `UseTrioTtShrink` — the SHRINK half of the reference's TT grow/shrink: at a PV node whose
    /// probed entry is deep enough and within bounds, reduce this node's depth by one and
    /// keep searching instead of taking the TT return / bound narrowing. The GROW half is
    /// (`UseTtExtension`) and is deliberately not duplicated here.
    /// DEFAULT OFF: no trio rule runs; the legacy hindsight pair is unchanged.</summary>
    public bool UseExtensionTrio { get; set; } = false;

    /// <summary> reference-shaped hindsight extension inside the trio (default ON).</summary>
    public bool UseTrioHindsight { get; set; } = true;

    /// <summary> enable the alpha-raise reduction inside the trio (default ON).</summary>
    public bool UseTrioAlphaRaise { get; set; } = true;

    /// <summary> PV-node TT depth-shrink inside the trio (default ON).</summary>
    public bool UseTrioTtShrink { get; set; } = true;

    /// <summary> SELECTIVE SINGULAR ENTRY POLICY (default OFF).
    /// When ON, the singular-extension exclusion search is paid only where it can matter:
    /// `depth >= SelectiveSingularMinDepth` (10), a TT entry of at least `depth - 1` (stricter
    /// than the legacy `depth - 3`), PV or cut nodes only (`isPvNode || cutNode` — the design reference's
    /// `!allNode`), bounded non-mate TT scores, and a SINGLE output tier (+1, never the
    /// double/triple tiers). The per-path extension counter is untouched. DEFAULT OFF: the
    /// legacy gate (depth ≥ 8, depth-3 TT margin, Exact/Lower, double/triple tiers) runs
    /// exactly as before.</summary>
    public bool UseSelectiveSingular { get; set; } = false;

    /// <summary> minimum depth for the selective singular exclusion search (default 10).</summary>
    public int SelectiveSingularMinDepth { get; set; } = 10;

    /// <summary> QUIET-ORDER DYNAMIC PRE-BONUS (default OFF).
    /// At node entry, after the static eval is known, the just-played QUIET move receives a
    /// history bonus from the eval swing: `clamp(EvalSwingBonusMult·−(staticEval +
    /// prevStaticEval), EvalSwingBonusMin, EvalSwingBonusMax)` (the reference's
    /// dynamic policy: 10/−100/200). The bonus lands BEFORE any fail-high result
    /// exists, filling the quiet-ordering gap where no countermove feedback is available.
    /// Gravity-bounded like every other history write. DEFAULT OFF: no pre-bonus is written.</summary>
    public bool UseEvalSwingBonus { get; set; } = false;

    /// <summary> multiplier of the eval-swing pre-bonus (the reference dynamicPolicyMult = 10).</summary>
    public int EvalSwingBonusMult { get; set; } = 10;

    /// <summary> lower clamp of the eval-swing pre-bonus (the reference dynamicPolicyMin = −100).</summary>
    public int EvalSwingBonusMin { get; set; } = -100;

    /// <summary> upper clamp of the eval-swing pre-bonus (the reference dynamicPolicyMax = 200).</summary>
    public int EvalSwingBonusMax { get; set; } = 200;

    /// <summary> LMR v1 HISTORY PRECISION (default OFF).
    /// The v1 history term computes `(long)h · 1024 / divisor` instead of
    /// `h / divisor · 1024`, so sub-divisor history scores no longer truncate to zero —
    /// 1024× finer sub-ply modulation, matching the v2 fixed-point intent. Only the v1 LMR
    /// history term is affected; the v2 block keeps the reference's own divide-first shape. [R1
    /// 2026-10-28] DEFAULT TRUE for the special release (12.17); the pre-R1 default was false.</summary>
    public bool UseLmrHistoryPrecision { get; set; } = true;

    /// <summary> LMR REDUCTION FLOOR (default OFF).
    /// `reduction = min(reduction, max(newDepth − 2, 0))`, so a reduced v1 child always keeps
    /// at least two plies instead of being handed to quiescence at ply 0 (the design reference's
    /// `min(reduction, depth − 2)` table floor). v1 schedule only; v2 keeps its own clamp.
    /// DEFAULT OFF: the legacy `reduction > newDepth` clamp runs exactly as before.</summary>
    public bool UseLmrReductionFloor { get; set; } = false;

    /// <summary> QS CORRECTION HISTORY (default OFF).
    /// When ON, the quiescence stand-pat applies the SAME correction history the main search
    /// applies to `staticEval` (the shipped pawn-key `/512` term, or the reference's 4-term term when
    /// `UseRefCorrectionHistory` is on), removing the score discontinuity at the
    /// main-search/QS boundary. Stored on the quiescence searcher (passthrough) so one option
    /// drives it; the main-search correction flags are shared through the same object.
    /// DEFAULT OFF: the QS stand-pat is the raw eval exactly as before.</summary>
    public bool UseQsCorrectionHistory
    {
        get => _qsearch.UseQsCorrectionHistory;
        set => _qsearch.UseQsCorrectionHistory = value;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // STRUCTURAL CLOSURES BAND 4
    // ═══════════════════════════════════════════════════════════════════════════════
    // Control-plane & NPS polish. The movegen dispatch is automatic runtime
    // selection (no option), plus a timed-only TM model, a draw-score spin and
    // a small batch of micro riders.

    /// <summary> CAPTURES-ONLY MOVEGEN (default OFF).
    /// When ON, a MovePicker in only-captures mode (quiescence, ProbCut) asks the generator
    /// for captures only, so quiet moves are never constructed. The emitted set is EXACTLY
    /// the one the full generator + partition keeps (captures, en passant, capture-promotions;
    /// quiet promotions are dropped in both paths), so the toggle is node-identical — a pure
    /// NPS lever, gated with ``. Passthrough to the quiescence searcher.</summary>
    public bool UseCapturesOnlyMovegen
    {
        get => _qsearch.UseCapturesOnlyMovegen;
        set => _qsearch.UseCapturesOnlyMovegen = value;
    }

    /// <summary> LAZY SEE IN THE MOVE PICKER (default OFF).
    /// When ON, captures are scored by MVV-LVA + capture history at partition time and SEE is
    /// evaluated only when a capture is about to be YIELDED (the design reference MoveGen.GoodCapture:
    /// `piece.Value > capture.Value && See0 < 0` → defer to the bad-capture stage), instead
    /// of calling `SeeGe` for every generated capture up front. This changes the good-capture
    /// ORDER (MVV-LVA instead of SEE-sorted), so it is an ordering arm: measure node count and
    /// NPS, then SPRT if the tree moves. DEFAULT OFF: the eager split is bit-identical.</summary>
    public bool UseLazySee
    {
        get => _qsearch.UseLazySee;
        set => _qsearch.UseLazySee = value;
    }

    /// <summary> STABLE TIME MANAGEMENT (default OFF).
    /// When ON, the soft limit is scaled by the reference's three-signal model instead of the legacy
    /// PV-stability/panic multipliers: best-move stability ladder {2.50, 1.20, 0.90, 0.80,
    /// 0.75} × score-stability ladder {1.25, 1.15, 1.00, 0.94, 0.88} × node fraction
    /// `(1.65 − bestMoveNodes/nodes)·1.47`, clamped [0.12, 2.45] and to
    /// `hardLimit − MoveOverhead`. The hard limit is polled every 4096 nodes while ON. Timed-only: a fixed-depth
    /// search never consults the clock, so the fixed-depth identity gate is unaffected.
    /// DEFAULT OFF: the legacy TM path is bit-identical.</summary>
    public bool UseStableTimeManagement
    {
        get => _timeManager.UseStableTimeManagement;
        set => _timeManager.UseStableTimeManagement = value;
    }

    /// <summary> CONTEMPT (advertised spin, default 0 = OFF).
    /// Draw scores become `Contempt · (2·stm − 1)` instead of flat 0 — the classic
    /// leading mainstream engines-9 `DrawValue[WHITE] = -contempt, DrawValue[BLACK] = +contempt` convention
    /// (White avoids draws, Black seeks them; the audit's H1 formula). Applied at every
    /// in-search draw return: repetition / 50-move / insufficient material and stalemate in
    /// the main search, and the QS draw check. With the default 0 the draw score is exactly 0,
    /// so the compiled behaviour is bit-identical. Range −50..50.</summary>
    public int Contempt
    {
        get => _qsearch.Contempt;
        set => _qsearch.Contempt = value;
    }

    /// <summary> MICRO RIDERS batch (default OFF). Three small
    /// riders, one campaign:
    /// (a) qsearch fail-high averaging: a non-mate QS fail-high returns `(score + beta)/2`
    /// instead of bare `beta` (the reference qsearch;
    /// (b) IIR quality gate: IIR also fires when a TT entry exists but is shallower than
    /// `depth − 4` (a record too shallow to trust), not only when there is no TT move;
    /// (c) 1024-node hard-limit bucketing: the search polls every 1024 nodes instead of 2048
    /// (cheaper per check, more frequent).
    /// DEFAULT OFF: all three legacy behaviours are bit-identical.</summary>
    public bool UseMicroRiders
    {
        get => _qsearch.UseMicroRiders;
        set => _qsearch.UseMicroRiders = value;
    }

    /// <summary> — Skip-fans on the move-loop pruners. When ON, the legacy
    /// history-pruning branch (depth ≤ `HistPruneMaxDepth`) aborts ALL remaining quiet
    /// moves of the node when it trips (`picker.SkipQuiets` — the reference's
    /// `movePicker.skipQuiets(true)`,, not only the one move that
    /// tripped it. Bad noisies still flow through the SEE-prune path and the TT move
    /// is always emitted before any fan can fire (it is picker stage 0). The other two
    /// members of the reference's three-pruner fan family — late-move pruning and in-loop
    /// futility — have ALREADY shipped skip-rest unconditionally since commit 181bd63
    /// (`SkipQuiets` at both call sites); this toggle completes the trio on the one
    /// path that still skipped a single move. DEFAULT OFF: compiled behaviour is
    /// bit-identical (the legacy branch still skips only the tripping move).</summary>
    public bool UseSkipFans { get; set; } = false;

    /// <summary>Quiescence narrowing (per the design reference; defaults
    /// RecaptureDepth 8 / PromotionDepth 2). When ON: at
    /// `qDepth >= QsRecaptureOnlyPly` only captures onto the previous move's target
    /// square are searched (deep capture chains beyond that ply are overwhelmingly
    /// recaptures); at `qDepth >= QsPromotionMaxPly` promotions are skipped entirely.
    /// In-check nodes are exempt (all evasions stay searchable, so mate-in-qsearch
    /// detection is untouched; quiet checks only exist at qDepth &lt; 2 anyway), and a
    /// node with no previous move (direct QS entry from a test) also exempts itself
    /// from the recapture filter — there is no square to recapture onto. DEFAULT OFF:
    /// QS generation is unchanged. Passthrough to the private <see cref="_qsearch"/>,
    /// same pattern as D1's <see cref="UseQsTtProbe"/>.</summary>
    public bool UseQsNarrowing
    {
        get => _qsearch.UseQsNarrowing;
        set => _qsearch.UseQsNarrowing = value;
    }

    /// <summary> qsearch ply from which only recaptures are searched (the design reference
    /// T_QS_RecaptureDepth, default 8).</summary>
    public int QsRecaptureOnlyPly
    {
        get => _qsearch.QsRecaptureOnlyPly;
        set => _qsearch.QsRecaptureOnlyPly = value;
    }

    /// <summary> qsearch ply from which promotions are skipped (the design reference
    /// T_QS_PromotionDepth, default 2 — promotions exist only below it).</summary>
    public int QsPromotionMaxPly
    {
        get => _qsearch.QsPromotionMaxPly;
        set => _qsearch.QsPromotionMaxPly = value;
    }

    /// <summary>SEE pruning reach upgrade (the reference's
    /// SEE-threshold builder). When ON, the move-loop SEE pruner runs
    /// with the FULL reference shape: ceiling `SeeReachMaxDepth` (10), quiets `−44·depth`, noisies
    /// `−17·depth²`, and the history refund `−hist/133` folded into the threshold (a
    /// historically good move is allowed to lose less). The `moveCount > 1` and
    /// not-in-check guards are shared with the existing block and keep running. The
    /// threshold is passed straight to `Position.SeeGe` as a loss tolerance — no sign
    /// flip at the caller (the the reference- sign note). When ON and `UseLmrV2` is
    /// OFF, the combined history score the refund needs is computed on this path too
    /// (pre-change it only existed on the LMR-v2 path, so the history refund was
    /// dead without `UseLmrV2`). Takes precedence over `UseRefSeeThresholds` when
    /// both are set. DEFAULT OFF: the block reads the legacy ceiling cap and the flat
    /// −50/−100 thresholds exactly as before.</summary>
    public bool UseSeeReach { get; set; } = false;

    /// <summary> SEE-pruning depth ceiling on the reach path (the reference
    /// uses 10; 8 is the safer half).</summary>
    public int SeeReachMaxDepth { get; set; } = 10;

    /// <summary> quiet SEE threshold per ply on the reach path (the reference
    /// seeQuietMargin = −44 — the material the mover may lose before being pruned).</summary>
    public int SeeReachQuietMargin { get; set; } = -44;

    /// <summary> noisy SEE threshold per ply² on the reach path (the reference
    /// seeNoisyMargin = −17).</summary>
    public int SeeReachNoisyMargin { get; set; } = -17;

    /// <summary> SEE-threshold history-refund divisor on the reach path (the reference's
    /// divisor is 133; the combined clamped history score is subtracted /133).</summary>
    public int SeeReachHistoryDivisor { get; set; } = 133;

    /// <summary>TT extension (`TtExtensionMaxDepth` = 6).
    /// When ON and a non-root node carries a TT entry that is ALREADY deep enough
    /// (`probe.Depth >= depth + (isPvNode ? 2 : 0)`), looks like a plausible cutoff
    /// (`probe.Value <= alpha || cutNode`), and sits OUT of bounds (`LowerBound` below
    /// beta / `UpperBound` above alpha — the stored value disagrees with this window),
    /// the node is granted one extra ply (`depth++`) while `depth ≤
    /// TtExtensionMaxDepth`. A position the TT says is "more interesting than this
    /// shallow window suggests" gets real depth for one comparison. EXACT entries are
    /// excluded (they are always "within bounds" — the reference prunes on them instead). PF
    /// probes only at depth ≥ 1 (depth-0 nodes route to QS before the probe), so
    /// unlike the reference there is no depth-0 extension, and repetition nodes keep PF's TT
    /// distrust (`allowTTCutoff`). DEFAULT OFF: no depth is ever granted by the TT.</summary>
    public bool UseTtExtension { get; set; } = false;

    /// <summary> maximum (pre-extension) depth at which the TT extension may
    /// fire (the reference uses 6, tunable 0..12).</summary>
    public int TtExtensionMaxDepth { get; set; } = 6;

    /// <summary>NMP hardening pack, riders (a) + (b), one toggle group
    /// (the reference and the design reference agree on both riders).
    /// (a) verified-null mate clamp: on a null-search fail-high a MATE-line score is
    /// never trusted — it is clamped to `beta` before the cut — while a non-mate
    /// fail-high returns the score itself (fail-soft), i.e. exactly the reference's
    /// `Score.isMate(score) ? beta : score` / the design reference's
    /// `Math.Abs(score) &gt; TABLEBASE_WIN ? beta : score`. The legacy path (toggle OFF)
    /// keeps PF's current behaviour: mate-line fail-highs are IGNORED entirely and
    /// everything else returns bare `beta`.
    /// (b) deep verification with a self-limiting seal: at `depth > 14` a fail-high no
    /// longer cuts immediately — the SAME node is re-searched WITHOUT the null move at
    /// `depth − 1 − R` on a `[beta−1, beta]` window, and only a confirmed fail-high
    /// cuts. While that verification runs, <see cref="ThreadData.NmpSealPly"/> is set
    /// to `(3·(depth−1−R)/4) + ply` (the reference's `td.nmpPly`), which SUPPRESSES NMP at
    /// the shallow plies of the verification subtree, so nested verifications
    /// chain-limit themselves instead of exploding. Rider (c) of the audit's pack (the
    /// TT-record gate `!ttHit || cutNode || score >= beta`) is ALREADY the shipped NMP
    /// guard and needs no toggle. DEFAULT OFF: the NMP block behaves exactly as
    /// before.</summary>
    public bool UseVerifiedNmp { get; set; } = false;

    /// <summary> (d) — the reference's eval-proportional NMP reduction scale as a
    /// separate arm: `R += min((staticEval − beta)/191, 4)` (the reference's scale 191 /
    /// max reduction 4) replaces the shipped `min((staticEval − beta)/200, 3)`
    /// (`NmpBonusDivisor`/`NmpBonusMax`). Independent of <see cref="UseVerifiedNmp"/>
    /// (the audit's "c/d as two extra toggles in separate follow-ups"). **DEFAULT TRUE
    /// (2026-11-04, R2 owner bake):** v9 measured +12.17 ± 21.39 Elo, LLR +0.28,
    /// formally inconclusive; v8 was -12.17, so the estimate is net-dependent. Baked ON by
    /// explicit owner decision (R2 banner at the top of this file), not by a passing
    /// verdict. Revert: set this property back to false.</summary>
    public bool UseRefNmpEvalScale { get; set; } = true;

    // Extensions / Heuristics Flags
    /// <summary>Singular extensions: +1 depth for TT moves proven unique at depth ≥ 8 (exclusion search at depth/2).</summary>
    public bool UseSingularExtensions { get; set; } = true;
    /// <summary>
    /// Singular double/triple extensions (Selective-Search audit, item 2). When the singular TT
    /// move does not merely beat the exclusion search but beats it by a growing margin, extend it
    /// by 2 or 3 plies instead of 1 - such a move is so forced that looking deeper down its line
    /// is worthwhile, which adds selective depth on the moves that matter most. Requires
    /// UseSingularExtensions. Disabling this toggle reproduces the single-ply (+1) behavior.
    /// </summary>
    public bool UseSingularDoubleExtensions { get; set; } = true;
    /// <summary>Internal iterative reduction: depth-- at depth ≥ 4 when no TT move exists.</summary>
    public bool UseInternalIterativeReduction { get; set; } = true; // Phase 1 (2026-09-04): restored from v1.1
    public bool UseCorrectionHistory
    {
        // Stored on the quiescence searcher so the SAME flag
        // drives the main-search correction and (when UseQsCorrectionHistory is on) the QS
        // stand-pat. Default true lives on the QS field.
        get => _qsearch.UseCorrectionHistory;
        set => _qsearch.UseCorrectionHistory = value;
    }
    
    // CRITICAL: Prevent TT cutoffs when position has appeared before
    // This prevents the engine from using cached scores that don't account for impending repetitions
    // Without this, the engine will \"hunt\" for repetitions thinking they're winning positions
    public bool DisableTTCutoffForRepetitions { get; set; } = true;

    /// <summary>
    /// Explicit triangular PV table (A). When on,
    /// the principal variation is tracked directly on the search stack via <see cref="PvTable"/>
    /// (<c>InitPly</c>/<c>MergeMove</c> in Negamax) and read at the root with <c>GetPv</c> —
    /// no TT walk, no board make/unmake during PV extraction. This eliminates root move
    /// instability caused by TT churn in non-PV lines (the <c>MOVE-CHANGING</c> warnings) and
    /// stabilizes aspiration windows. It is pure bookkeeping: it never influences a search
    /// decision, so scores and node counts are bit-identical with the toggle on or off. When
    /// off, the legacy TT-walk (<c>GetPvFromTT</c>) is used instead, reproducing the pre-Step-2A
    /// behavior exactly.
    /// </summary>
    public bool UsePvTable { get; set; } = true;

    private readonly PvTable _pvTable = new();

    private Move[][] _killers; // [MaxDepth][4]
}
