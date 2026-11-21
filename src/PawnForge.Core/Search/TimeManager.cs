namespace PawnForge.Core.Search;

/// <summary>
/// Time management: converts UCI clock inputs into soft/hard stop limits and
/// decides after each iteration whether to start the next one.
///
/// ALLOCATION (Updated 2026-08-28 for better clock utilization):
/// tournament (movesToGo > 0): time/movesToGo, capped at time/10
/// sudden death: time/30 + full increment (was time/40 + increment/2)
/// safety buffer: 50 ms below 1 s remaining, else 500 ms
/// hard limit = min(4x allocation, time/4)
/// fixed movetime: soft = 95%, hard = 100%
/// time advantage: when ahead on clock, multiply alloc by up to 1.5×
/// tactical floor: minimum 3s or 1% of clock for sharp positions
///
/// The updated allocation formulas address systematic under-spend (PawnForge
/// leaving 2-3 min while opponents are down to seconds in 5+1 games). Key fixes:
/// 1. Raised sudden-death base: time/30 (was time/40) + full inc (was inc/2)
/// 2. Stable-PV multiplier floored at 1.0 (was 0.7×-0.85× reduction)
/// 3. Removed redundant 0.85× guard from ShouldContinue (double-discount)
/// 4. Time-advantage multiplier reinvests surplus when ahead on clock
/// 5. Tactical-position floor ensures sharp positions get minimum time
///
/// All changes preserve the zero-flag guarantee: monotonic clock
/// (Environment.TickCount64), hard limit ≤ time/4, and mid-iteration
/// ShouldStop abort remain intact.
///
/// STOPPING:
/// The effective soft limit = allocation × PV-stability/panic multiplier
/// (1.0x-1.5x range: stable PV never reduces time, unstable extends up to 1.5×).
/// ShouldStop is polled mid-iteration by the search every 2048 nodes
/// (thread 0): once the effective soft is crossed the current iteration is
/// cut off. The hard limit (min(4x alloc, time/4)) is the absolute emergency
/// abort for a runaway iteration - never more than 25% of the remaining clock.
///
/// DYNAMIC ADJUSTMENTS:
/// PV stability multiplier 1.0x-1.5x (stable PV uses base time, unstable
/// extends up to 1.5×), panic mode ×1.5 when the score drops > 50 cp,
/// time-advantage multiplier when ahead on clock (up to 1.5×),
/// tactical-position floor for sharp/check positions,
/// move-overhead lag compensation subtracted from the total time.
/// </summary>
public class TimeManager
{
    /// <summary>
    /// Monotonic millisecond clock for ALL time management. Uses the OS tick
    /// counter (Environment.TickCount64, based on the interrupt time) instead
    /// of DateTime.UtcNow: the WALL clock can be stepped backward by NTP /
    /// W32Time corrections, which would make "currentTime < hardLimit" hold
    /// forever, freeze the soft/hard aborts and let a search run to the depth
    /// cap - silently burning the whole clock ("loses on time"). The monotonic
    /// tick counter never moves backward.
    /// </summary>
    public static long NowMs() => Environment.TickCount64;

    private long _startTime;
    private long _hardLimit; // Absolute maximum time
    private long _softLimit; // Target time
    private bool _useTime;
    
    // Search context for dynamic adjustment
    private int _movesToGo;

    // ── Minimum-depth safety guard (2026-09-07) ──────────────────────────────────────────
    // Number of iterative-deepening iterations that have fully completed in the current
    // search. Updated by the search via SetCompletedDepth after every completed depth.
    //
    // WHY: a single-move search explosion (an iteration whose tree balloons because its
    // reduction/pruning pattern misaligns with the TT-seeded tree from prior game moves) can
    // blow past the SOFT time limit before depth 2 even finishes. The mid-iteration stop then
    // aborts it and the engine plays the only completed result — its DEPTH-1 best move — which
    // is frequently a blunder (observed in a real 40/15 game: r7/2p2qbk/... played e5e4 and
    // lost, while a full search plays c7b6). Guarding against a soft-limit stop until at least
    // MinIterationDepth iterations have completed makes that failure mode structurally
    // impossible. The HARD limit (<= time/4) remains the absolute backstop, so this can never
    // cause a time forfeit; it only trades a little extra time on rare pathological moves for
    // never shipping a depth-1 move. On normal moves depth MinIterationDepth is reached in a
    // few milliseconds, so the guard is inert. Applied to real time controls only (not fixed
    // `go movetime`, which is an explicit user budget, and not infinite/depth/nodes searches).
    private int _completedDepth;

    /// <summary>
    /// Minimum iterations that must complete before a SOFT-limit stop is honored. Depth 1-3
    /// are shallow "noise" on many positions (the buggy FEN above plays e5e4 at d1, h5f4 at
    /// d2, f3f2 at d3, and only settles on the sound c7b6 from d4 on); 5 gives a safety margin
    /// above that band. Tunable — raise for more safety at the cost of more time on the rare
    /// exploding move, lower to spend less. The hard limit always overrides.
    /// </summary>
    private const int MinIterationDepth = 5;
    
    // PV stability tracking
    private int _pvChanges;
    private int _lastPVMove;
    private int _stableIterations;

    /// <summary>
    /// TM1 : stable-PV soft-limit shrink. When true,
    /// a best move stable for ≥ 2 iterations scales the soft limit to 0.85×, ≥ 3 iterations
    /// to 0.75×. Default FALSE — the TM1 SPRT (2026-09-10, 30+1, 500 games against the
    /// stacked baseline) returned +1.4 ± 21.7 where the stack already expected ~+12.5:
    /// the shrink's own contribution ≈ −11 Elo, null-to-negative → reverted per the queue rule.
    /// The 2026-08-28 no-shrink behavior (stable PV multiplier floored at 1.0) is the default.
    /// Unstable-PV extensions (1.2/1.5) and the panic multiplier are NOT affected by this
    /// toggle; the Bug-17 min-depth guard and hard limit backstop apply exactly as before.
    /// </summary>
    public bool UseStableShrink { get; set; } = false;

    /// <summary>
    /// TM2 : panic-mode soft-limit extension.
    /// Default TRUE — the TM2–TM4 bundle SPRT (2026-09-10, 30+1, 500 games) returned a null
    /// (+2.8 ± 21.3 vs a stack baseline already carrying ~+12.5): bundle
    /// contribution ≈ −10 Elo → panic + advantage REVERTED to the 2026-08-28 behavior per
    /// the queue rule (PF-only extensions have now defended themselves three times:
    /// the negative extensions, and panic/advantage here). When true, panic extends the
    /// soft limit up to ×1.5 (joint cap with PV stability). PV-stability extensions are
    /// independent of this toggle.
    /// </summary>
    public bool UsePanicMode { get; set; } = true;

    /// <summary>
    /// TM3 : clock time-advantage multiplier
    /// (sudden death, clock > 30 s: up to ×1.5 when ahead of the opponent). Default TRUE —
    /// same bundle-null verdict as TM2 (contribution ≈ −10 for panic+advantage together);
    /// the 2026-08-28 behavior is restored. The tactical floor and safety buffer are
    /// independent of this toggle.
    /// </summary>
    public bool UseTimeAdvantage { get; set; } = true;

    /// <summary>
    /// TM4 : overhead hardening. When true
    /// (default = SPRT arm, bundled with TM2/TM3), the soft limit is clamped to
    /// hard − moveOverhead (the reference's uciOverhead hardening: the soft stop must also leave
    /// the lag margin intact, not just the hard backstop). Pairs with the `Move Overhead`
    /// UCI option default raised 10 → 50 ms in the same bundle. Not applied to fixed
    /// movetime (that path sets its own 95 %/100 % split) or to degenerate tiny clocks
    /// where the clamp would push the soft limit below the start time.
    /// </summary>
    public bool UseOverheadSoftClamp { get; set; } = true;

    /// <summary>
    /// Stable-TM model (default OFF). When ON,
    /// <see cref="EffectiveSoftLimit"/> uses the reference's three-signal scaler (best-move stability
    /// ladder × score-stability ladder × node fraction, clamped [0.12, 2.45] and to
    /// hard − overhead) INSTEAD of the legacy PV-stability/panic multipliers. Timed-only.
    /// </summary>
    public bool UseStableTimeManagement { get; set; } = true; // measured +36.62 — special-release override; was false

    // ── stable-TM state ───────────────────────────────────────────
    /// <summary>Move overhead in ms (from the UCI option), used for the hard-limit clamp.</summary>
    private long _moveOverhead = 50;
    /// <summary>Consecutive completed iterations whose score stayed within ±10 cp.</summary>
    private int _scoreStability;
    /// <summary>Last completed iteration's total node count (thread 0).</summary>
    private long _stableTmNodes;
    /// <summary>Nodes spent on the best root move, accumulated across the search.</summary>
    private long _stableTmBestMoveNodes;

    /// <summary>The reference's best-move-stability ladder.</summary>
    private static readonly int[] BestMoveStabilityFactor = { 250, 120, 90, 80, 75 };
    /// <summary>The reference's score-stability ladder.</summary>
    private static readonly int[] ScoreStabilityFactor = { 125, 115, 100, 94, 88 };
    /// <summary>The reference's node-fraction base/scale.</summary>
    private const int NodeTmBaseHundredths = 165;
    private const int NodeTmScaleHundredths = 147;
    /// <summary>the reference's scale clamp (softTimeScaleMin/Max).</summary>
    private const double StableTmScaleMin = 0.12;
    private const double StableTmScaleMax = 2.45;
    /// <summary>Minimum depth for the node-fraction term.</summary>
    private const int NodeTmMinDepth = 5;

    /// <summary>
    /// Feeds the per-iteration node statistics used by the stable-TM
    /// node-fraction term: the total nodes searched and the nodes spent on the best root move
    /// (accumulated across the search, the reference's `nodesPerMove`). Called by the search after
    /// each completed iteration (thread 0).
    /// </summary>
    public void SetNodeStats(long bestMoveNodes, long totalNodes)
    {
        _stableTmBestMoveNodes = bestMoveNodes;
        _stableTmNodes = totalNodes;
    }

    // ── CLOCK-IMBALANCE SCALER (the design reference pattern) ────────────
    // TWO-SIDED clock management: spend MORE when ahead on the clock and LESS when behind,
    // clipped to the existing hard limit (the zero-flag guarantee is untouched because the
    // hard limit is still min(4x alloc, myTime/4) and the soft stop still respects it).
    //
    // Why a new lever instead of just the existing TM3: TM3 (UseTimeAdvantage, default ON)
    // is ONE-SIDED — it reinvests a surplus when ahead (×1.0–1.5) and does ABSOLUTELY NOTHING
    // when behind, where the risk actually is (running the clock down in a level or losing
    // time battle). G1 scales the deficit side too.
    //
    // DEFAULTS TO OFF ⇒ the compiled behaviour is exactly the TM3-era one (bit-identical). With
    // the toggle ON, G1 SUPERSEDES TM3 (the TM3 block is skipped) — applying both would
    // double-count the ahead side. The scales are SPSA-able percentages:
    // ratio = myTime / (myTime + theirTime) // 0.5 = dead level
    // scale = 1 + (ratio - 0.5)·2·(AheadScale /100) when ratio >= 0.5
    // scale = 1 - (0.5 - ratio)·2·(BehindScale /100) when ratio < 0.5
    // so a 9:1 clock split at the defaults is +50 % / −50 %, and a level clock is 1.0 (inert).
    /// <summary>G1: two-sided clock-imbalance scaler (more time when ahead, less when behind).</summary>
    public bool UseClockImbalance { get; set; } = false;
    /// <summary>G1: max percentage to ADD when far ahead on the clock (100 = +1.0×).</summary>
    public int ClockImbalanceAheadScale { get; set; } = 50;
    /// <summary>G1: max percentage to SHED when far behind on the clock (50 = −0.5×).</summary>
    public int ClockImbalanceBehindScale { get; set; } = 50;
    
    // Panic Mode (Score Trend)
    private int _previousScore;
    private bool _panicMode;
    private bool _firstScore;
    
    /// <summary>
    /// Initialize time management for a time-controlled search.
    /// </summary>
    /// <param name="wtime">White time remaining in milliseconds</param>
    /// <param name="btime">Black time remaining in milliseconds</param>
    /// <param name="winc">White increment per move in milliseconds</param>
    /// <param name="binc">Black increment per move in milliseconds</param>
    /// <param name="movesToGo">Moves until next time control (0 for sudden death)</param>
    /// <param name="side">Side to move (0 for White, 1 for Black)</param>
    /// <param name="currentTime">Current monotonic time in milliseconds (see <see cref="NowMs"/>).</param>
    /// <param name="moveOverhead">Move overhead lag compensation in milliseconds (default 10ms)</param>
    /// <summary>
    /// Initializes time management from the UCI clock inputs.
    /// </summary>
    /// <param name="wtime">White's remaining time in ms.</param>
    /// <param name="btime">Black's remaining time in ms.</param>
    /// <param name="winc">White's increment per move in ms.</param>
    /// <param name="binc">Black's increment per move in ms.</param>
    /// <param name="movesToGo">Moves remaining until the next time control (0 = sudden death).</param>
    /// <param name="side">The side to move (0 = white, 1 = black).</param>
    /// <param name="currentTime">Current wall-clock time in ms (epoch-based).</param>
    /// <param name="moveOverhead">Lag compensation subtracted from the usable time.</param>
    public void Init(long wtime, long btime, long winc, long binc, int movesToGo, int side, long currentTime, int moveOverhead)
    {
        _startTime = currentTime;
        _useTime = true;
        _movesToGo = movesToGo;
        _moveOverhead = moveOverhead;
        _completedDepth = 0; // min-depth guard: no iteration completed yet
        ResetPVStability();
        _panicMode = false;
        _firstScore = true;
        
        long myTime = (side == 0) ? wtime : btime; // 0 = White, 1 = Black
        long myInc = (side == 0) ? winc : binc;
        long theirTime = (side == 0) ? btime : wtime; // opponent's time for advantage calc
        
        // Apply Move Overhead (Lag Compensation)
        myTime = Math.Max(100, myTime - moveOverhead);
        
        // ══════════════════════════════════════════════════════════════════════
        // BASE ALLOCATION (Updated 2026-08-28: better clock utilization)
        //
        // Previous allocation (time/40 + inc/2) systematically under-spent the
        // clock, leaving 2-3 minutes unused in 5+1 games while opponents ran
        // down to seconds. Root causes:
        // • time/40 planned for 40 moves, leaving ~36% of clock by design
        // • Only half the increment was budgeted
        // • Stable-PV multiplier reduced time to 0.7× (backward logic)
        // • Redundant 0.85× guard double-discounted the allocation
        //
        // Updated allocation addresses these issues:
        // • time/30 plans for 30 moves (~13% reserve vs 36%)
        // • Full increment is budgeted (always earned back next move)
        // • Stable-PV multiplier floored at 1.0× (no reduction)
        // • 0.85× guard removed (soft limit is sole criterion)
        // • Time-advantage multiplier when ahead on clock
        // • Tactical-position floor for sharp/check positions
        //
        // The hard limit (≤ time/4) and monotonic clock remain unchanged,
        // preserving the zero-flag guarantee.
        // ══════════════════════════════════════════════════════════════════════
        long alloc;
        if (movesToGo > 0)
        {
            // Tournament mode: remaining time / moves to the next control.
            // No log-scaled boost, and never more than a 10-move share of the
            // clock for a single move.
            alloc = myTime / movesToGo;
            if (alloc > myTime / 10)
                alloc = myTime / 10;
        }
        else
        {
            // Sudden death (Updated 2026-08-28):
            // Base: time/30 (was time/40) + full increment (was inc/2)
            // This raises per-move budget from ~2.7% to ~3.6% of the clock
            // (5+1 example: 8s → 11s base allocation before multipliers)
            alloc = (myTime / 30) + myInc;
        }
        
        // Priority 4: Time-advantage multiplier (NEW 2026-08-28; TM3 toggle, 2026-09-10)
        // When ahead on clock vs opponent, reinvest surplus time to reach
        // deeper search depths. Only applies in sudden death when:
        // • Opponent time is known (theirTime > 0)
        // • We have surplus (myTime > theirTime)
        // • Enough clock remains (myTime > 30s) to avoid ultra-short-time instability
        // The multiplier scales linearly with advantage, capped at 1.5×.
        // Hard limit (≤ myTime/4) is applied after this, so even with maximum
        // advantage the absolute ceiling remains 25% of the clock.
        // TM3 (default ON after the 2026-09-10 bundle revert): the OFF experiment
        // (audited as speculative) measured ≈ −10 Elo together with TM2 and was
        // reverted — the multiplier stays.
        //
        // When UseClockImbalance is ON it SUPERSEDES this
        // one-sided block (see the G1 block below): applying both would double-count the
        // ahead side, and TM3 has no deficit side at all.
        if (UseClockImbalance && movesToGo != -1 && theirTime > 0 && myTime > 5000)
        {
            // Two-sided, continuously scaled, and clipped by the hard limit below.
            // Requires BOTH clocks above a floor (5 s here) so that near-zero clocks
            // (a lost-on-time race, or a GUI reporting garbage) cannot distort a
            // ratio-dominated multiplier.
            double ratio = (double)myTime / (myTime + theirTime);
            double scale = 1.0;
            if (ratio >= 0.5)
                scale = 1.0 + (ratio - 0.5) * 2.0 * (ClockImbalanceAheadScale / 100.0);
            else
                scale = 1.0 - (0.5 - ratio) * 2.0 * (ClockImbalanceBehindScale / 100.0);
            alloc = (long)(alloc * scale);
        }
        else if (UseTimeAdvantage && movesToGo == 0 && theirTime > 0 && myTime > 30000)
        {
            long surplus = myTime - theirTime;
            if (surplus > 0)
            {
                // advantage multiplier: 1.0 + min(surplus/myTime, 0.5)
                // Example: 180s vs 60s → surplus = 120s → mult = 1.0 + min(120/180, 0.5) = 1.0 + 0.5 = 1.5×
                double advantageMult = 1.0 + Math.Min((double)surplus / myTime, 0.5);
                alloc = (long)(alloc * advantageMult);
            }
        }
        
        // Priority 5: Tactical-position floor (NEW 2026-08-28)
        // Ensure sharp/tactical positions get a minimum allocation even if
        // stable-PV or time-advantage logic would otherwise starve them.
        // Only applies when enough clock remains (myTime > 30s) to avoid
        // ultra-short-time edge cases. Floor is max(3s, 1% of clock).
        // Note: This uses a heuristic proxy (will be refined in Search.cs
        // to pass inCheck/eval from the actual position; for now we apply
        // a conservative floor that never hurts).
        if (myTime > 30000)
        {
            long minAlloc = Math.Max(3000, myTime / 100);  // at least 3s or 1% of clock
            alloc = Math.Max(alloc, minAlloc);
        }
        
        // Safety buffer: keep at least 50 ms (sub-second clock) or 500 ms in
        // reserve so the engine can always answer the next `go`.
        long safetyBuffer = (myTime < 1000) ? 50 : 500;
        
        if (alloc > myTime - safetyBuffer)
        {
            alloc = Math.Max(10, myTime - safetyBuffer);
        }
        
        _softLimit = _startTime + alloc;
        // Hard limit: emergency bound for a runaway single iteration. Never
        // more than 4x the allocation AND never more than 25% of the remaining
        // clock - a single move can no longer flag the game by itself.
        _hardLimit = Math.Min(_startTime + (alloc * 4), _startTime + myTime / 4);

        if (_hardLimit < _softLimit)
            _hardLimit = _softLimit;

        // TM4 (2026-09-10, default ON): overhead hardening — clamp the soft limit to
        // hard − moveOverhead so the SOFT stop also preserves the lag margin (the reference
        // clamps its scaled soft limit the same way). Real time controls only: fixed
        // movetime sets its own 95%/100% split, and degenerate tiny clocks where
        // hard − overhead < start would invert the limit are skipped.
        if (UseOverheadSoftClamp && _movesToGo != -1)
        {
            long maxSoft = _hardLimit - moveOverhead;
            if (maxSoft > _startTime && _softLimit > maxSoft)
                _softLimit = maxSoft;
        }
    }
    
    /// <summary>
    /// Set fixed time control (movetime mode).
    /// </summary>
    /// <param name="timeMs">Fixed time allocation in milliseconds</param>
    /// <param name="currentTime">Current time in milliseconds</param>
    /// <summary>Fixed `go movetime` mode: soft = 95%, hard = 100% of the given time.</summary>
    public void SetFixedTime(long timeMs, long currentTime)
    {
        _startTime = currentTime;
        _useTime = true;
        // For fixed time (movetime), use full allocation
        // Soft limit slightly less than hard limit to avoid overrun
        _softLimit = currentTime + (long)(timeMs * 0.95);
        _hardLimit = currentTime + timeMs;
        _movesToGo = -1; // Flag to indicate this is movetime mode
        _completedDepth = 0; // min-depth guard reset (guard itself is TC-only, see ShouldStop)
        ResetPVStability();
    }

    /// <summary>
    /// Records that an iterative-deepening iteration of the given depth has fully completed.
    /// Feeds the minimum-depth safety guard in <see cref="ShouldStop"/> / <see cref="ShouldContinue"/>
    /// so a soft-limit stop cannot play a move before <c>MinIterationDepth</c> iterations are done.
    /// Called once per completed depth by the search (thread 0).
    /// </summary>
    public void SetCompletedDepth(int depth)
    {
        if (depth > _completedDepth) _completedDepth = depth;
    }
    
    /// <summary>
    /// Set infinite search mode (search until stopped).
    /// </summary>
    /// <summary>`go infinite` mode: no time-based stopping (only the stop signal applies).</summary>
    public void SetInfinite()
    {
        _useTime = false;
    }
    
    /// <summary>
    /// Check if search is in infinite mode.
    /// </summary>
    /// <summary>True when no time limits apply (infinite / depth-limited search).</summary>
    public bool IsInfinite => !_useTime;
    
    /// <summary>
    /// Check if hard time limit has been exceeded.
    /// </summary>
    /// <param name="currentTime">Current time in milliseconds</param>
    /// <returns>True if hard limit exceeded</returns>
    /// <summary>Hard-limit check: true when the search must stop immediately.</summary>
    public bool IsTimeUp(long currentTime)
    {
        if (!_useTime)
            return false;
        return currentTime >= _hardLimit;
    }

    /// <summary>
    /// Periodic-stop decision used by the search's node polling (thread 0):
    /// true when the search must abort at the next node.
    ///
    /// Stops at the HARD limit (absolute abort) OR at the EFFECTIVE SOFT
    /// limit - the planned per-move spend, extended by the PV-stability/panic
    /// multiplier (capped 1.5x).
    ///
    /// The soft limit is honored both mid-iteration (here) and between
    /// iterations (in ShouldContinue), so when the planned time runs out
    /// mid-iteration the engine cuts off the current iteration and reports
    /// the last completed iteration's best move. This keeps actual spend
    /// close to the allocation and prevents multi-second overshoots that
    /// burned the clock in earlier versions.
    /// </summary>
    public bool ShouldStop(long currentTime)
    {
        if (!_useTime)
            return false;
        if (currentTime >= _hardLimit)
            return true; // absolute backstop — always honored, guarantees no time forfeit
        // Minimum-depth safety guard (real time controls only): do NOT allow a soft-limit
        // stop to abort the search before MinIterationDepth iterations have completed. This
        // prevents a single exploding iteration from leaving the engine on a depth-1 blunder.
        // The hard limit above still applies, so this can never overrun the clock dangerously.
        if (_movesToGo != -1 && _completedDepth < MinIterationDepth)
            return false;
        return currentTime >= EffectiveSoftLimit();
    }

    /// <summary>
    /// The soft stop time for the current search state: the base allocation
    /// scaled by the current PV-stability/panic multiplier (1.0x-1.5x range).
    /// Shared by ShouldStop (mid-iteration) and ShouldContinue (between
    /// iterations) so both agree on when to stop.
    /// </summary>
    internal long EffectiveSoftLimit()
    {
        // Stable-TM model (default OFF): the reference's three
        // continuous signals replace the legacy PV-stability/panic multipliers. The scaled
        // limit is clamped to [0.12, 2.45]× the allocation AND to hardLimit − MoveOverhead
        // (the same overhead hardening the legacy TM4 clamp applies). Timed-only.
        if (UseStableTimeManagement && _movesToGo != -1)
        {
            double scale = BestMoveStabilityFactorValue()
                         * ScoreStabilityFactorValue()
                         * NodeTmFactorValue();
            if (scale < StableTmScaleMin) scale = StableTmScaleMin;
            if (scale > StableTmScaleMax) scale = StableTmScaleMax;

            long limit = _startTime + (long)((_softLimit - _startTime) * scale);
            long hardMinusOverhead = _hardLimit - _moveOverhead;
            if (limit > hardMinusOverhead) limit = hardMinusOverhead;
            if (limit < _startTime) limit = _startTime;
            return limit;
        }

        double pv = GetPVStabilityMultiplier();
        // TM2 (default ON after 2026-09-10 bundle revert): panic extension — spending more
        // when the position deteriorates; the OFF experiment measured ≈ −10 Elo together with
        // TM3 and was reverted.
        if (_panicMode && UsePanicMode)
            pv = Math.Min(pv * 1.5, 1.5);  // panic extends up to 1.5× total
        return _startTime + (long)((_softLimit - _startTime) * pv);
    }

    /// <summary> Best-move-stability ladder value (the reference).</summary>
    private double BestMoveStabilityFactorValue()
    {
        int i = Math.Min(_stableIterations, BestMoveStabilityFactor.Length - 1);
        return BestMoveStabilityFactor[i] / 100.0;
    }

    /// <summary> Score-stability ladder value (the reference).</summary>
    private double ScoreStabilityFactorValue()
    {
        int i = Math.Min(_scoreStability, ScoreStabilityFactor.Length - 1);
        return ScoreStabilityFactor[i] / 100.0;
    }

    /// <summary> Node-fraction term (the reference): a larger share of
    /// the search spent on the best move makes the move more trustworthy, so less extra time
    /// is granted. Returns 1.0 below the node-TM minimum depth or before any nodes exist.</summary>
    private double NodeTmFactorValue()
    {
        if (_completedDepth < NodeTmMinDepth || _stableTmNodes <= 0) return 1.0;
        double fraction = (double)_stableTmBestMoveNodes / _stableTmNodes;
        return (NodeTmBaseHundredths / 100.0 - fraction) * (NodeTmScaleHundredths / 100.0);
    }

    /// <summary>
    /// Called when a depth is completed.
    /// Decides if we should start the next depth based on time used, PV stability, and panic mode.
    /// </summary>
    /// <param name="currentTime">Current time in milliseconds</param>
    /// <param name="bestMove">Current best move</param>
    /// <param name="score">Current search score</param>
    /// <returns>True if next iteration should be started</returns>
    /// <summary>
    /// Called when a depth is completed. Decides whether to start the next
    /// depth based on time used, PV stability and panic mode.
    /// </summary>
    public bool ShouldContinue(long currentTime, int bestMove, int score)
    {
        if (!_useTime)
            return true;
        if (currentTime >= _hardLimit)
            return false;
        
        long elapsed = currentTime - _startTime;
        long total = _hardLimit - _startTime;
        
        // For movetime (fixed time), use a more aggressive threshold
        // to maximize search depth within the time limit
        if (_movesToGo == -1)
        {
            // For movetime: continue if we have used less than 95% of allocated time
            // This allows the engine to use the full movetime allocation (with 5% safety margin)
            return elapsed < (total * 0.95);
        }
        
        // For regular time controls: use PV stability and softer limits
        // Update Panic Mode (Score Trend tracking)
        if (!_firstScore && Math.Abs(score) < 20000 && Math.Abs(_previousScore) < 20000)
        {
            if (score < _previousScore - 50)
            {
                _panicMode = true; // Score dropped significantly
            }
            else if (score > _previousScore + 20)
            {
                _panicMode = false; // Score recovered
            }
        }
        // Score stability for the stable-TM model: an
        // iteration whose score stayed within ±10 cp of the previous one increments the
        // counter, anything else resets it (as the reference does).
        // MUST run BEFORE `_previousScore` is overwritten below.
        if (UseStableTimeManagement && !_firstScore
            && Math.Abs(score) < 30000 && Math.Abs(_previousScore) < 30000)
        {
            _scoreStability = (score >= _previousScore - 10 && score <= _previousScore + 10)
                ? _scoreStability + 1
                : 0;
        }

        _previousScore = score;
        _firstScore = false;

        // Minimum-depth safety guard: keep starting new iterations until at least
        // MinIterationDepth have completed (the hard limit above is the only override), so a
        // soft-limit stop can never leave the engine on a shallow depth-1/2 blunder. Inert on
        // normal moves (min depth is reached in milliseconds). See the _completedDepth note.
        if (_completedDepth < MinIterationDepth)
            return true;

        // Apply PV stability multiplier to soft limit
        // The combined panic + PV-stability multiplier is capped at 1.5x
        // (see EffectiveSoftLimit).
        long adjustedSoftLimit = EffectiveSoftLimit();

        // Standard Soft Limit check (with PV adjustment)
        if (currentTime >= adjustedSoftLimit)
        {
            return false;
        }
        
        // Priority 3: Removed redundant 0.85× guard (2026-08-28)
        // The old code had an extra "if (elapsed > allocated * 0.85) return false"
        // check here that stacked on top of the effective soft limit, throwing
        // away an additional 15% of the already-discounted budget. With the
        // stable-PV multiplier now floored at 1.0× (Priority 1), the effective
        // soft limit is the sole between-iteration stop criterion.
        // The mid-iteration ShouldStop poll (every 2048 nodes) prevents overshoot,
        // so this extra guard was redundant double-discounting.
        
        return true;
    }
    
    /// <summary>
    /// Get hard time limit (absolute maximum).
    /// </summary>
    /// <summary>Absolute hard stop time (ms since start).</summary>
    public long HardLimit => _hardLimit;
    
    /// <summary>
    /// Get soft time limit (target allocation).
    /// </summary>
    /// <summary>Preferred stop time (ms since start) - may be exceeded only by the current iteration.</summary>
    public long SoftLimit => _softLimit;
    
    /// <summary>
    /// Get start time of current search.
    /// </summary>
    /// <summary>Search start time (ms, epoch-based).</summary>
    public long StartTime => _startTime;
    
    /// <summary>
    /// Check if panic mode is active (score dropped significantly).
    /// </summary>
    /// <summary>True while panic mode extends the time (score dropped sharply).</summary>
    public bool IsPanicMode => _panicMode;
    
    /// <summary>
    /// Get number of PV changes during search.
    /// </summary>
    /// <summary>How often the best move changed across iterations.</summary>
    public int PVChanges => _pvChanges;
    
    /// <summary>
    /// Get number of stable iterations (PV unchanged).
    /// </summary>
    /// <summary>How many consecutive iterations kept the same best move.</summary>
    public int StableIterations => _stableIterations;
    
    /// <summary>
    /// Update PV stability tracking.
    /// Called when best move is determined at each iteration.
    /// </summary>
    /// <param name="currentBestMove">Best move from current iteration (encoded as int)</param>
    /// <param name="stable">True if PV is considered stable</param>
    /// <summary>Feeds the per-iteration best-move result into the stability tracker.</summary>
    public void UpdatePVStability(int currentBestMove, bool stable)
    {
        if (currentBestMove != _lastPVMove)
        {
            _pvChanges++;
            _stableIterations = 0;
            _lastPVMove = currentBestMove;
        }
        else if (stable)
        {
            _stableIterations++;
        }
    }
    
    /// <summary>
    /// Reset PV stability counters for new search.
    /// </summary>
    /// <summary>Clears the PV stability tracking (new search).</summary>
    public void ResetPVStability()
    {
        _pvChanges = 0;
        _lastPVMove = 0;
        _stableIterations = 0;
        _scoreStability = 0;
        _stableTmNodes = 0;
        _stableTmBestMoveNodes = 0;
    }
    
    /// <summary>
    /// Get time multiplier based on PV stability.
    /// Updated 2026-08-28: Stable PV now returns 1.0× (was 0.7×-0.85× reduction).
    /// Unstable PV extends time up to 1.5×.
    /// For movetime mode, always returns 1.0.
    /// </summary>
    /// <returns>Multiplier for time allocation (1.0 to 1.5)</returns>
    /// <summary>
    /// Time multiplier from PV stability: 1.5× after 3+ best-move changes,
    /// 1.2× after 2, and (TM1, 2026-09-09) 0.75×/0.85× after 2/3+ STABLE
    /// iterations when <see cref="UseStableShrink"/> is on — the soft-limit
    /// shrink banks the unneeded budget for later moves (reference-style soft
    /// limit usage). With the toggle off,
    /// the 2026-08-28 behavior applies (stable PV floored at 1.0×).
    /// Bypassed for fixed-movetime searches (always 1.0).
    ///
    /// History: the pre-2026-08-28 code reduced to 0.7×/0.85× for stable PV
    /// and was floored to 1.0× on reasoning (not SPRT); the TM1 lever
    /// re-introduces a slightly gentler shrink behind the toggle for
    /// SPRT measurement at real match TCs.
    /// </summary>
    public double GetPVStabilityMultiplier()
    {
        // Don't adjust time for fixed movetime searches
        if (_movesToGo == -1)
        {
            return 1.0;
        }

        if (_pvChanges >= 3)
        {
            // Very unstable - search longer
            return 1.5;
        }
        else if (_pvChanges == 2)
        {
            // Somewhat unstable
            return 1.2;
        }
        else if (_stableIterations >= 3)
        {
            // TM1: stable PV — shrink the soft limit to bank the unused budget
            // (toggle off restores the 2026-08-28 floor of 1.0×, was 0.7× pre-fix)
            return UseStableShrink ? 0.75 : 1.0;
        }
        else if (_stableIterations >= 2)
        {
            // TM1: fairly stable PV — gentle shrink (toggle off → 1.0×, was 0.85×)
            return UseStableShrink ? 0.85 : 1.0;
        }
        return 1.0; // Normal
    }
}
