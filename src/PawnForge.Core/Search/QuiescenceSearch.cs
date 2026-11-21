using System;
using System.Numerics;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;

namespace PawnForge.Core.Search;

/// <summary>
/// Quiescence search: a capture-only search that continues beyond the nominal
/// search depth until the position is tactically quiet, so the static
/// evaluation is never taken in the middle of an exchange sequence (the
/// "horizon effect").
///
/// PIPELINE per node: stop/draw checks → stand-pat (fail-hard on beta) →
/// delta pruning (margin 1100) → SEE prefilter (approximate, then exact) →
/// capture loop with post-make legality validation.
///
/// SCOPE: when the side to move is NOT in check, captures only (stand-pat + delta/SEE
/// pruning). When the side to move IS in check, the position is not quiet: stand-pat is
/// suppressed and ALL legal evasions are searched (with evasion pruning to bound the tree),
/// and checkmate is detected here. This "check-aware" behavior both fixes a horizon
/// correctness gap and lets forcing checking sequences be followed to their resolution,
/// which is the main source of genuine selective depth. Toggle: UseCheckAwareQuiescence.
/// </summary>
public class QuiescenceSearch
{
    private readonly IPositionEvaluator _evaluator;
    private readonly ThreadData _td;
    private readonly SearchSignal _signal;
    private readonly TimeManager? _timeManager; // Can be null if not passed
    private readonly TTable? _tt;               // [Full-Audit D1] null = no QS TT interaction
    private readonly Func<int>? _currentAge;    // [Full-Audit D1] the Search's TT generation
    private int _selDepth;

    // SEE optimization knobs
    public bool UseSeeApproxPrefilterInQuiescence { get; set; } = true;
    private const int SEE_APPROX_PRUNE_MARGIN = 0;

    /// <summary>
    /// Delta-pruning margin in cp (queen 900 + margin 200 = 1100). Track 2
    /// SPSA lever: set from <see cref="Search"/> via the QsDeltaMargin
    /// passthrough. Default reproduces the legacy literal exactly.
    /// </summary>
    public int QsDeltaMargin { get; set; } = 1100;

    /// <summary>
    /// Check-aware quiescence. When true (default), a node whose side to move is in check does
    /// not stand-pat and searches all legal evasions (following forcing checking lines and
    /// detecting checkmate at the horizon). When false, quiescence is captures-only in every
    /// node, reproducing the previous behavior exactly.
    /// </summary>
    public bool UseCheckAwareQuiescence { get; set; } = true;

    /// <summary>
    /// Quiescence checking moves. When on, the shallowest quiescence plies also search safe QUIET
    /// checking moves (not only captures), following forcing check sequences past the horizon —
    /// a direct selective-depth lever (the gap vs the reference/leading mainstream engines per the consolidated audit).
    ///
    /// HISTORY: the FIRST implementation was net-negative — it made EVERY quiet move and then
    /// called IsKingAttacked to discover it wasn't a check, at every quiescence node, collapsing
    /// single-thread NPS ~60% and dropping nominal depth ~2 ply (it did widen the selective
    /// margin ~1.5 ply, so the idea was sound, only the cost was wrong).
    ///
    /// REIMPLEMENTED (2026-09-04): the quiet-check filter now uses the make-free
    /// <see cref="Position.GivesCheck"/> predicate BEFORE making the move, so only actual
    /// checking quiets are ever made — eliminating the ~60% NPS collapse. GivesCheck was
    /// validated against python-chess over 210k+ real legal moves ,
    /// zero mismatches. This toggle is the A2 selective-depth EXPERIMENT under test vs BASE.
    /// When false, quiescence considers only captures when not in check (previous behavior).
    /// </summary>
    public bool UseQuiescenceChecks { get; set; } = false; // the =false arm measured +11.30 — special-release override; was true

    /// <summary>
    /// Twofold-repetition draw inside quiescence nodes. Set from
    /// <see cref="Search"/> via the QsUseTwofoldRepetitionDraw passthrough. When true
    /// (default), a quiescence node whose position occurred once before already scores 0
    /// (the reference Score.isDoubleRepetition parity); when false, the legacy threefold-only
    /// rule applies.
    /// </summary>
    public bool UseTwofoldRepetitionDraw { get; set; } = true;

    /// <summary>
    /// Transposition-table probing INSIDE quiescence.
    ///
    /// WHY: QS is 55–70 % of all nodes and previously had ZERO TT interaction — every
    /// repeated exchange sequence was re-searched from scratch and the cached static eval
    /// was never reused. Both reference engines probe the TT in QS.
    ///
    /// KEYING (the "negative depth" idea, adapted to this TT): the depth field is a u8, so
    /// a QS entry is stored at <c>depth = 0</c> and a QS CUTOFF is only accepted from an
    /// entry whose stored depth is exactly 0. Main-search nodes always store depth ≥ 1
    /// (depth ≤ 0 routes to QS), so a QS entry can never alias a main-search entry, and
    /// conversely a depth-0 entry can never satisfy a main-search cutoff (which requires
    /// <c>entry.Depth >= depth >= 1</c>). No TT layout change, no negative encoding.
    ///
    /// DEFAULTS TO OFF: with the toggle off, not a single TT instruction runs in QS and the
    /// compiled behaviour is bit-identical (pinned by QsTtProbeTests).
    /// </summary>
    public bool UseQsTtProbe { get; set; } = true; // measured +16.52 — special-release override; was false

    /// <summary>
    /// TT-score-as-eval, quiescence half
    /// (`canUseTTScore`, the reference/963). Lives here because the QS
    /// object is where the toggle's QS behaviour executes; the main search reads it
    /// through <see cref="Search.UseTtScoreEval"/>. When ON (and <see cref="UseQsTtProbe"/>
    /// is ON, so a probe actually happened), a probed score that is CONSISTENT with this
    /// node's raw stand-pat — EXACT, or a LOWER bound ≥ the raw eval, or an UPPER bound ≤
    /// it — replaces the raw eval as the stand-pat base before the beta test, delta
    /// pruning and the alpha raise. Any probed entry may serve (depth-0 QS entry or a
    /// main-search entry cached by an earlier visit); only the CUTOFF path keeps the
    /// depth-0 keying. DEFAULT OFF: the stand-pat is the raw eval exactly as before.
    /// </summary>
    public bool UseTtScoreEval { get; set; } = true; // measured +12.17 — special-release override; was false

    /// <summary>
    /// Quiescence narrowing (per the design reference). When ON, two ply-indexed limits collapse
    /// the deep QS tree: at <c>qDepth >= QsRecaptureOnlyPly</c> only captures onto the
    /// previous move's target square are searched, and at <c>qDepth >= QsPromotionMaxPly</c>
    /// promotions are skipped entirely. In-check nodes are exempt (all evasions stay
    /// searchable, so mate-in-qsearch detection is untouched), as is a node with no
    /// previous move (there is no square to recapture onto). DEFAULT OFF: generation is
    /// unchanged.
    /// </summary>
    public bool UseQsNarrowing { get; set; } = false;

    /// <summary> qsearch ply from which only recaptures are searched
    /// (the design reference T_QS_RecaptureDepth default 8).</summary>
    public int QsRecaptureOnlyPly { get; set; } = 8;

    /// <summary> qsearch ply from which promotions are skipped
    /// (the design reference T_QS_PromotionDepth default 2 — promotions exist only below it).</summary>
    public int QsPromotionMaxPly { get; set; } = 2;

    /// <summary>
    /// Eval-cache passthrough (the actual flag lives on this
    /// object so ONE <see cref="Search.UseEvalCache"/> property drives both the main-search
    /// static eval and the QS stand-pat). The cache itself is per-thread
    /// (<see cref="ThreadData.EvalCache"/>), never shared. DEFAULT OFF: the stand-pat is a
    /// fresh evaluator call exactly as before.
    /// </summary>
    public bool UseEvalCache { get; set; } = false;

    /// <summary>
    /// Correction-history flags shared with the main search
    /// (passthrough storage — <see cref="Search.UseCorrectionHistory"/> and
    /// <see cref="Search.UseRefCorrectionHistory"/> read/write these). The QS stand-pat
    /// applies the same correction only when <see cref="UseQsCorrectionHistory"/> is ON.
    /// Defaults match the compiled main-search defaults (legacy pawn-only correction ON, the reference
    /// 4-term **ON from the R2 owner bake, 2026-11-04**).
    /// </summary>
    public bool UseCorrectionHistory { get; set; } = true;

    /// <summary> the reference 4-term correction selector (see the main search).
    /// R2 owner bake (2026-11-04): true (v9 +19.13 ± 22.40, LLR +0.45, formally
    /// inconclusive; v8 was -16.52). This is the storage field for
    /// <see cref="Search.UseRefCorrectionHistory"/>.</summary>
    public bool UseRefCorrectionHistory { get; set; } = true;

    /// <summary>
    /// Apply correction history to the QS stand-pat (default
    /// OFF). When ON, `stand = raw + correction` — the shipped pawn-key `/512` term, or the reference's
    /// 4-term term when <see cref="UseRefCorrectionHistory"/> is on — clamped to ±29999,
    /// matching the main search's corrected `staticEval` and removing the score discontinuity at
    /// the main-search/QS boundary. DEFAULT OFF: the raw eval exactly as before.
    /// </summary>
    public bool UseQsCorrectionHistory { get; set; } = false;

    /// <summary>
    /// Captures-only GENERATION passthrough (the actual flag
    /// lives here so ONE <see cref="Search.UseCapturesOnlyMovegen"/> property drives the main
    /// search, ProbCut and quiescence). When ON, a picker in only-captures mode asks the
    /// generator to skip constructing quiets. DEFAULT OFF: the full generation pass runs
    /// exactly as before.
    /// </summary>
    public bool UseCapturesOnlyMovegen { get; set; } = false;

    /// <summary>
    /// Lazy-SEE passthrough (the picker computes SEE when a
    /// capture is yielded instead of up front). DEFAULT OFF: the eager good/bad split runs
    /// exactly as before.
    /// </summary>
    public bool UseLazySee { get; set; } = false;

    /// <summary>
    /// Contempt passthrough (the actual value lives here so
    /// ONE <see cref="Search.Contempt"/> property drives the main search and quiescence).
    /// Draw score = `Contempt · (2·stm − 1)`; default 0 = flat draw exactly as before.
    /// </summary>
    public int Contempt { get; set; } = 0;

    /// <summary>
    /// Micro-riders passthrough (the flag lives here so ONE
    /// <see cref="Search.UseMicroRiders"/> property drives the main search and quiescence).
    /// Enables the QS fail-high averaging, the IIR quality gate and the 1024-node poll.
    /// </summary>
    public bool UseMicroRiders { get; set; } = true; // measured +11.30 — special-release override; was false

    // How many of the shallowest quiescence plies (qDepth) also generate quiet checks.
    private const int QuiescenceCheckPlies = 2;

    public QuiescenceSearch(IPositionEvaluator evaluator, ThreadData td, SearchSignal signal, TimeManager? timeManager = null,
                            TTable? tt = null, Func<int>? currentAge = null)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _td = td ?? throw new ArgumentNullException(nameof(td));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _timeManager = timeManager;
        // Optional TT access for QS probing/storing. Absent (null) is the
        // pre-D1 state: the toggle can then never do anything, which keeps every existing
        // construction site (tests included) valid and behaviour-identical.
        _tt = tt;
        _currentAge = currentAge;
    }

    /// <summary>
    /// Gets the maximum selective depth reached during quiescence search.
    /// </summary>
    public int SelectiveDepth => _selDepth;

    /// <summary>
    /// Resets the selective depth counter. Should be called at the start of each search.
    /// </summary>
    public void ResetSelectiveDepth() => _selDepth = 0;

    /// <summary>
    /// Performs quiescence search: a capture-only search to resolve tactical exchange sequences.
    /// </summary>
    /// <param name="pos">The position to search</param>
    /// <param name="alpha">Lower bound of the search window</param>
    /// <param name="beta">Upper bound of the search window</param>
    /// <param name="ply">Current ply from root</param>
    /// <param name="qDepth">Quiescence depth (0 = horizon ply, >0 = deeper plies)</param>
    /// <returns>The evaluation score</returns>
    public int Search(Position pos, int alpha, int beta, int ply = 0, int qDepth = 0)
    {
        _td.IncrementNodes();
        if (SearchDiagnostics.Enabled) _td.Stats.RecordQNode();
        
        // Safety check: Don't hang in QS limits
        // Same poll cadence as the main search.
        long pollMask = UseMicroRiders
            ? 1023L
            : ((_timeManager?.UseStableTimeManagement ?? false) ? 4095L : 2047L);
        if ((_td.Nodes & pollMask) == 0)
        {
            if (_signal.ShouldStop) return 0;

            // Check time limits (Main Thread only)
            // This prevents massive QS trees from causing time losses
            if (_td.ThreadId == 0 && _timeManager != null && !_timeManager.IsInfinite)
            {
                long currentTime = TimeManager.NowMs(); // monotonic (see TimeManager.NowMs)
                // Soft OR hard stop: overshooting the planned spend
                // burned the clock (see TimeManager.ShouldStop).
                if (_timeManager.ShouldStop(currentTime))
                {
                    _signal.Stop();
                    return 0;
                }
            }
        }

        // Update seldepth tracking
        if (ply > _selDepth) _selDepth = ply;
        
        // Prevent infinite recursion in qsearch
        if (ply >= 100) return EvaluateCached(pos);
        
        // Check for draw (prevents infinite loops in tactical sequences).
        // Repetition threshold follows UseTwofoldRepetitionDraw (the reference isDoubleRepetition
        // parity when on; legacy threefold when off) — see
        // Draw score is contempt-adjusted (0 by default).
        if (pos.IsDrawForSearch(UseTwofoldRepetitionDraw))
            return PawnForge.Core.Search.Search.DrawScoreFor(Contempt, pos.SideToMove);

        // Check-aware quiescence: is the side to move in check at this node?
        // When in check the position is NOT quiet - standing pat would be unsound, and only
        // considering captures could miss the single quiet move that escapes the check. So we
        // search ALL legal evasions instead. This also follows forcing checking sequences to
        // their end (the main driver of real selective depth) and lets us detect checkmate here.
        bool inCheck = UseCheckAwareQuiescence && pos.IsKingAttacked(pos.SideToMove);

        // Syzygy tablebase probe (WDL): exact value for positions inside the
        // tables (capture resolution is part of the probe). Depth-unconditional
        // (exact TB values must never be masked by heuristic returns),
        // gated on the loaded material cardinality.
        bool tbRange = Syzygy.ProbingEnabled && Syzygy.IsInitialized && Syzygy.MaxPieces > 0
            && BitOperations.PopCount(pos.GetAllOccupancy()) <= Syzygy.MaxPieces;

        // At a MATED node this probe used to return the flat
        // WdlToScore(-2) = -29900, so the genuine mate score was never produced and the
        // engine reported `score cp 29900` — 0/60 mate announcements on the short bucket.
        //
        // The obvious fix — skip the probe whenever the side to move is in check — was
        // MEASURED and REJECTED. It reads well (mate does require being in check) but it
        // strips the exact tablebase value from every in-check TB position, breaking the
        // no-masking invariant that every cutoff below depends on. Endgame suite: short solved
        // 60/60 -> 49/60 and long 60/60 -> 58/60, in exchange for 0% -> 21.7% announcement.
        // Trading 13 solved positions for a cosmetic announcement is a bad deal.
        //
        // So we test the thing that actually matters instead: mate requires being in check
        // AND having no legal move. Only that one node type diverges from the original
        // behaviour; every other in-check TB position keeps its exact value, and positions
        // outside tablebase range pay nothing at all. The mate score uses the same
        // convention as the "mated at this ply" seed below.
        if (inCheck && tbRange)
        {
            Span<Move> mateTest = stackalloc Move[256];
            if (pos.GenerateLegalMoves(mateTest) == 0)
                return -31000 + ply;
        }

        if (tbRange)
        {
            int tbWdl = Syzygy.ProbeWdl(pos);
            if (tbWdl != int.MinValue)
                return Syzygy.WdlToScore(tbWdl);
        }

        // ── Quiescence TT probe ────────────────────────────
        // Off by default: with UseQsTtProbe=false not one TT instruction runs here.
        // "Negative depth" keying: QS stores at depth 0 and only a depth-0 entry may cut
        // off a QS node; main-search entries (stored at depth >= 1) are therefore never
        // used for a QS cutoff, and a depth-0 entry can never satisfy a main-search cutoff
        // (which requires entry.Depth >= depth >= 1). No TT layout change.
        TTEntry qsEntry = default;
        bool qsTtHit = false;
        if (UseQsTtProbe && _tt is not null)
        {
            qsTtHit = _tt.Probe(pos.ZobristKey, out qsEntry, ply, pos);
            if (SearchDiagnostics.Enabled) _td.Stats.RecordQTtProbe(qsTtHit);
            if (qsTtHit && qsEntry.Depth == 0)
            {
                if (qsEntry.Flag == NodeType.Exact
                    || (qsEntry.Flag == NodeType.LowerBound && qsEntry.Value >= beta)
                    || (qsEntry.Flag == NodeType.UpperBound && qsEntry.Value <= alpha))
                {
                    return qsEntry.Value;
                }
            }
        }

        int bestScore;
        int alphaOrig = alpha;              // [D1] for the stored node flag
        int qsStaticEval = TTable.NoStaticEval; // [D1] what we cache in the TT entry
        if (inCheck)
        {
            // No stand-pat while in check. Seed with a "mated at this ply" baseline so that a
            // node with no legal evasions is correctly scored as checkmate; any real evasion
            // raises it. Delta pruning is skipped (it assumes a quiet position).
            bestScore = -31000 + ply;
        }
        else
        {
            // [D1] Reuse the TT's cached RAW static eval when one is present (the same
            // reuse the main search performs): it is bit-identical to a fresh Evaluate
            // by construction (the TT only caches values that round-trip the 16-bit field).
            int stand = (qsTtHit && qsEntry.StaticEval != TTable.NoStaticEval)
                ? qsEntry.StaticEval
                : EvaluateCached(pos);
            qsStaticEval = stand; // [D1] the RAW eval — the TT must keep caching correction-free evals

            // ── TT-score-as-eval, QS half ────────────────
            // canUseTTScore (the reference/963): when the probed score is
            // CONSISTENT with the raw stand-pat (EXACT, or LOWER ≥ raw, or UPPER ≤ raw) it
            // is a strictly more informed value and becomes the new base for the beta
            // test, delta pruning and the alpha raise below. Gated on UseQsTtProbe (no
            // probe ⇒ no upgrade) and read via the same UseTtScoreEval passthrough the
            // main search uses, so ONE option drives both halves.
            if (UseTtScoreEval && qsTtHit && PawnForge.Core.Search.Search.CanUseTtScore(qsEntry, stand))
            {
                stand = qsEntry.Value;
            }

            // ── QS CORRECTION HISTORY ───────────────────
            // Apply the same correction the main search applies to `staticEval` (the shipped
            // pawn-key /512 term, or the reference's 4-term term when UseRefCorrectionHistory is
            // on), so the stand-pat matches the main-search score at the boundary instead of
            // jumping by the correction magnitude. `qsStaticEval` above stays RAW — the TT
            // must keep caching correction-free evals. The the reference path's TT-move gate uses
            // the QS-visible entry (requires UseQsTtProbe for exact parity with the main
            // search's `hasTtMove` gate). DEFAULT OFF: `stand` is untouched.
            if (UseQsCorrectionHistory && UseCorrectionHistory)
            {
                int correction = CorrectionFor(pos, hasTtMove: qsTtHit && !qsEntry.BestMove.IsNone);
                if (correction != 0)
                    stand = Math.Clamp(stand + correction, -29999, 29999);
            }

            if (stand >= beta)
            {
                // [D1] a stand-pat fail-high is a genuine lower bound — worth caching.
                // MICRO RIDERS: the reference's QS is fail-soft
                // — its stand-pat cutoff returns `staticEval` itself,
                // NOT beta. That overshoot is what makes the `(bestScore + beta)/2` averaging
                // below reachable at all; with the legacy hard `beta` return the averaging is
                // provably dead (every child is clamped to the window). Default OFF: beta.
                int standReturn = UseMicroRiders ? stand : beta;
                StoreQs(pos, standReturn, NodeType.LowerBound, Move.None, ply, qsStaticEval);
                return standReturn;
            }
            if (alpha < stand) alpha = stand;

            // Delta Pruning
            // If standing eval is significantly below alpha, and we don't have dangerous captures (promotions), we can prune.
            // Safety: delta = Queen (900) + Margin (200) = 1100 (QsDeltaMargin, Track 2 lever).
            // Using the conservative 1100 margin to avoid pruning too many winning captures
            if (stand + QsDeltaMargin < alpha)
            {
                 // Can't improve alpha even with best capture (queen + promotion)
                 return alpha;
            }

            bestScore = stand;
        }

        // Move generation:
        // - in check: ALL legal moves (evasions);
        // - not in check, shallow plies with quiet-check generation on: ALL moves, but only
        // captures/promotions and SAFE quiet CHECKS are actually searched;
        // - otherwise: captures only (previous behavior).
        Span<Move> genBuf = _td.MoveBuffers[ply][0];
        Span<ScoredMove> capsBuf = _td.ScoredMoveBuffers[ply][0];
        Span<ScoredMove> quietsBuf = _td.ScoredMoveBuffers[ply][1];
        Span<Move> badBuf = _td.MoveBuffers[ply][1];

        // Generate quiet checks only in the shallowest quiescence plies (bounds the node cost)
        // and only when not already in check (the in-check path searches all evasions anyway).
        bool genChecks = !inCheck && UseQuiescenceChecks && qDepth < QuiescenceCheckPlies;
        bool onlyCaptures = !inCheck && !genChecks;

        // ── Quiescence narrowing (per the design reference) ──
        // Two ply-indexed limits, resolved once per node and applied as cheap loop filters
        // (the picker has already partitioned the moves; filtering before the SEE work costs
        // two field reads and never makes a move that the reference would not generate).
        // * recapture-only past `QsRecaptureOnlyPly`: the deep capture chain is assumed to
        // be a recapture sequence, so only captures onto the previous move's target
        // square survive. Needs a previous move to exist: a direct QS entry from a test
        // (LastMove == None) exempts itself rather than skipping every capture.
        // * no promotions past `QsPromotionMaxPly` (the design reference: promotions only below
        // PromotionDepth 2; queen-underpromotion madness at depth is pure waste).
        // In-check nodes are exempt from both — every evasion stays searchable, which keeps
        // mate-in-qsearch detection and the in-check failure scoring untouched.
        bool qsRecaptureOnly = UseQsNarrowing && !inCheck && qDepth >= QsRecaptureOnlyPly
                               && !pos.LastMove.IsNone;
        bool qsNoPromotions = UseQsNarrowing && !inCheck && qDepth >= QsPromotionMaxPly;

        var picker = new MovePicker(pos, Move.None, null, null, default, null, null, null, null, null, onlyCaptures, UseCapturesOnlyMovegen, UseLazySee, genBuf, capsBuf, quietsBuf, badBuf);
        Move m;
        int legalMoves = 0;
        Move bestMoveQs = Move.None; // [D1] best move for the stored TT entry
        while ((m = picker.Next()) != Move.None)
        {
            // [ ] narrowing filters run FIRST — they are pure skips of entire move
            // classes, so nothing below (SEE prefilter, delta work, make/unmake) is paid
            // for a move the references would not have generated at this ply.
            if (qsRecaptureOnly && m.To != pos.LastMove.To) continue;
            if (qsNoPromotions && m.Promoted != 0) continue;

            // Quiet = neither a capture nor a promotion (also used for evasion pruning below).
            // Bug 148 fix: an en-passant capture lands on an EMPTY square (the captured pawn sits
            // one rank behind the target), so the mailbox test alone classified every en-passant
            // capture as quiet and quiescence never searched one - although the captures-only
            // generator explicitly generates en passant and the picker's IsCapture already treats
            // IsEnPassant as a capture. The flag is the authoritative capture marker here; with
            // the fix an en-passant capture takes the capture path (SEE prefilter, SeeGe, MVV-LVA
            // ordering) like any other capture.
            bool isQuiet = pos.Mailbox[m.To] == Piece.Empty && !m.IsEnPassant && m.PromotionPiece == Piece.Empty;

            if (!inCheck)
            {
                if (!isQuiet)
                {
                    // Capture/promotion pruning (unchanged): skip captures that lose material.
                    if (UseSeeApproxPrefilterInQuiescence)
                    {
                        int approx = pos.SeeApproximate(m);
                        if (approx < -50) continue;
                    }
                    if (!pos.SeeGe(m, -50)) continue;
                }
                else
                {
                    // Quiet move: only a candidate when generating quiet checks in a shallow ply.
                    if (!genChecks) continue;
                    // CHEAP CHECK FILTER (the fix that makes quiet-checks viable): decide whether
                    // the move gives check WITHOUT making it, via the validated make-free
                    // Position.GivesCheck predicate. The previous implementation made EVERY quiet
                    // move and then called IsKingAttacked to discover it wasn't a check, which
                    // collapsed NPS ~60%. Now non-checking quiets are rejected here, before any
                    // make/unmake, so only real checking quiets ever reach the search.
                    if (!pos.GivesCheck(m)) continue;
                    // Skip quiet checks that hang material (SEE < 0): a losing quiet check is
                    // almost never worth following past the horizon.
                    if (!pos.SeeGe(m, 0)) continue;
                }
            }

            // Get moving piece BEFORE MakePublicMove (board will be modified)
            Piece qsMovingPiece = pos.GetMovingPiece(m);
            var captured = pos.MakePublicMove(m);
            _evaluator.MakeMove(pos, m, qsMovingPiece, captured);

            // Prefetch the child's eval-cache line: the child's
            // stand-pat probes it immediately. No-op when the toggle is off or the cache
            // was never allocated (the default path runs zero cache instructions).
            if (UseEvalCache) _td.EvalCache?.Prefetch(pos.ZobristKey);
            
            // Validate move legality: skip if our king is in check after the move
            if (pos.IsKingAttacked(1 - pos.SideToMove))
            {
                _evaluator.UnmakeMove();
                pos.UnmakePublicMove(m, captured);
                continue; // Illegal move - our king is attacked
            }

            // NOTE: the old post-make "is this quiet move actually a check?" test was removed —
            // GivesCheck (above, pre-make) already guarantees every quiet move reaching here is a
            // check, so re-testing after the make is redundant work.

            legalMoves++;
            int score = -Search(pos, -beta, -alpha, ply + 1, qDepth + 1);
            _evaluator.UnmakeMove();
            pos.UnmakePublicMove(m, captured);

            if (score > bestScore) { bestScore = score; bestMoveQs = m; } // [D1] track for the TT store
            if (score >= beta)
            {
                if (SearchDiagnostics.Enabled) _td.Stats.RecordQCutoff(legalMoves - 1);
                // Micro riders: the reference breaks out of the
                // capture loop and averages the fail-soft bestScore with beta AFTER the loop
                //, instead of returning beta immediately. Default OFF:
                // bare beta, bit-identical.
                if (UseMicroRiders) break;
                return beta;
            }
            if (score > alpha) alpha = score;

            // Evasion pruning: once in check and at least one legal evasion has been tried that
            // is not getting mated, stop searching further QUIET evasions (captures/promotions
            // keep being searched, and the MovePicker emits those before quiets). This bounds
            // the in-check node count without dropping the forcing lines that matter.
            if (inCheck && isQuiet && legalMoves > 1 && bestScore > -30000)
                break;
        }

        // In check with no legal move => checkmate, detected inside quiescence.
        if (inCheck && legalMoves == 0)
            return -31000 + ply;

        // Not in check keeps the previous fail-hard return (alpha); in check returns the best
        // evasion score found (fail-soft), never a spurious stand-pat.
        // Micro riders: a capture fail-high BREAKS the loop
        // with the raw fail-soft bestScore, so it must be returned (not alpha) and then averaged
        // with beta when both are non-mate (the reference.
        int result = (inCheck || (UseMicroRiders && bestScore >= beta)) ? bestScore : alpha;
        if (UseMicroRiders && result >= beta)
            result = PawnForge.Core.Search.Search.QsFailHighScore(result, beta);
        // [D1] Cache the node result at depth 0 (the QS keying). A no-op when the toggle is
        // off. Flag semantics are the standard fail-soft mapping against the ORIGINAL window.
        StoreQs(pos, result,
                result >= beta ? NodeType.LowerBound
                               : (result > alphaOrig ? NodeType.Exact : NodeType.UpperBound),
                bestMoveQs, ply, qsStaticEval);
        return result;
    }

    /// <summary>
    /// Stores a quiescence node result into the shared TT at
    /// <c>depth = 0</c> — the "negative depth" keying adapted to this TT's u8 depth field
    /// (see <see cref="UseQsTtProbe"/>). A no-op when the toggle is off or no TT was handed
    /// in, so every pre-D1 construction path stays bit-identical.
    /// </summary>
    /// <param name="pos">The node's position (its Zobrist key is the TT key).</param>
    /// <param name="value">The node's returned score.</param>
    /// <param name="flag">Exact / LowerBound / UpperBound against the node's original window.</param>
    /// <param name="bestMove">Best move found (for future ordering); <see cref="Move.None"/> if none.</param>
    /// <param name="ply">Distance from the root — mate scores are stored root-relative.</param>
    /// <param name="staticEval">The RAW eval computed at this node, or <see cref="TTable.NoStaticEval"/>.</param>
    private void StoreQs(Position pos, int value, NodeType flag, Move bestMove, int ply, int staticEval)
    {
        if (!UseQsTtProbe || _tt is null) return;
        _tt.Store(pos.ZobristKey, value, 0, flag, bestMove, _currentAge?.Invoke() ?? 1, ply, staticEval);
    }

    private static bool IsCapture(Position pos, Move m) => m.IsEnPassant || pos.GetPieceAt(m.To) != Piece.Empty;

    /// <summary>
    /// Delegates to the thread's NNUE evaluator.
    /// </summary>
    private int Evaluate(Position pos)
    {
        return _evaluator.Evaluate();
    }

    /// <summary>
    /// Static eval with the optional per-thread eval cache
    /// (the same cache the main search uses — <see cref="ThreadData.EvalCache"/>). A verified
    /// hit returns a value bit-identical to a fresh <see cref="Evaluate"/> and skips the NNUE
    /// forward pass; a miss computes once and stores. DEFAULT OFF: exactly <see cref="Evaluate"/>.
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

    /// <summary>
    /// The main search's correction value for this position,
    /// mirroring `Search.Negamax.cs` exactly:
    /// <list type="bullet">
    /// <item>legacy path (UseRefCorrectionHistory = false): the pawn-key additive term
    /// <c>CorrectionHistory[side][PawnKey &amp; 16383] / 512</c>;</item>
    /// <item>the reference 4-term path: <c>(pawn·99 + whiteNonPawn·100 + blackNonPawn·100 +
    /// counter·96) / 64</c>, with the same TT-move gate
    /// (<paramref name="hasTtMove"/> ⇒ no correction).</item>
    /// </list>
    /// The caller clamps the sum to ±29999. Pure reads of the per-thread tables.
    /// </summary>
    private int CorrectionFor(Position pos, bool hasTtMove)
    {
        int side = pos.SideToMove;

        if (UseRefCorrectionHistory)
        {
            if (hasTtMove) return 0;

            Move prevMove = pos.LastMove;
            int prevPieceIndex = -1;
            if (!prevMove.IsNone)
            {
                // Pre-move mover: after a promotion the mover was a pawn (the reference parity).
                prevPieceIndex = prevMove.Promoted != 0
                    ? 0
                    : pos.PieceToIndex(pos.GetPieceAt(prevMove.To));
            }

            int pawn = _td.CorrectionPawn[side][(int)(pos.PawnKey & 16383)] * 99 / 100;
            int white = _td.CorrectionNonPawn[0][side][(int)(pos.NonPawnKeyWhite & 16383)];
            int black = _td.CorrectionNonPawn[1][side][(int)(pos.NonPawnKeyBlack & 16383)];
            int counter = 0;
            if (!prevMove.IsNone && prevPieceIndex >= 0)
                counter = _td.CorrectionCont[side][prevPieceIndex][prevMove.To] * 96 / 100;
            return (pawn + white + black + counter) / 64;
        }

        const int W = 512; // 16384 / W = 32 cp max correction (same as the main search)
        return _td.CorrectionHistory[side][(int)(pos.PawnKey & 16383)] / W;
    }
}
