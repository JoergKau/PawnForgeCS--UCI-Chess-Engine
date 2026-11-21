namespace PawnForge.Tests;

using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Locks for the "#### Band 1 — do these first" steps of the Final Full Audit
///:
///
/// — TT-score-as-eval (`UseTtScoreEval`, main search + QS half; the reference
/// /963, the oracle search.cpp:842-845).
/// — Skip-fans (`UseSkipFans`): skip-rest on the legacy history-pruning fan.
/// — Qsearch narrowing (`UseQsNarrowing` + RecaptureOnlyPly/PromotionMaxPly;
/// the design reference MoveGen.cs:239-258).
/// — SEE reach (`UseSeeReach` + 4 spins; the reference seeMaxDepth 10 / −44·d /
/// −17·d² / −hist/133).
/// — Bad-capture LMR decoupling (`UseLmrNoisyMoves` now honoured by the live
/// v1 schedule, not only the default-OFF v2).
/// — TT extension (`UseTtExtension` + max depth; the reference TtExtDepth 6).
/// — NMP hardening pack (`UseVerifiedNmp` riders a+b, `UseRefNmpEvalScale`
/// rider d; the reference the design reference).
///
/// Same three kinds of test as StructuralClosuresPhase123Tests, in the order they matter:
/// 1. DEFAULTS — every new knob defaults to the pre-change behaviour so a fresh
/// binary is bit-identical (bench_identity_check is the external gate).
/// 2. DELIVERY — every new knob is an ADVERTISED uci option with the right default,
/// or a fastchess/SPSA arm silently runs defaults-vs-defaults (the SPSA v1 void).
/// 3. WIRING — each toggle must demonstrably reach the search (node counts move).
/// </summary>
public class StructuralClosuresBand1Tests
{
    // The review's own middlegame bench FEN (used by the shipped locks too) — quiet
    // enough for the cheap wiring searches, rich enough that every pruner fires.
    private const string MidgameFen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";
    // Kiwipete — capture-rich, the canonical quiescence stress position.
    private const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    // K+R vs K: Ra1-a8 is mate (the oracle-verified inside the test that uses it).
    private const string MateInOneFen = "6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1";
    // Rook endgame, pawn on each side: cheap at depth 17, where the NMP verification
    // path (depth > 14 at a non-root node) is reachable.
    private const string RookEndgameFen = "8/5pk1/8/3p4/8/5PK1/6P1/3R4 w - - 0 1";
    // WAC-style sharp middlegame: large eval swings, so the NMP eval-scale rider (d)
    // changes the reduction bucket (|eval−beta| ≥ 382) at a moderate depth.
    private const string SharpMiddlegameFen = "2rr3k/1ppq1ppp/p1bb4/4p3/4P3/1BN1Q3/PPP2PPP/R1B2RK1 w - - 0 1";

    /// <summary>
    /// FEN-verification gate (project rule): every fixture constant used by this class
    /// is parsed by the oracle at test time — legality AND geometry, not just identity.
    /// </summary>
    [Fact]
    public void Band1_Fixtures_AreStockfishVerified()
    {
        Stockfish.VerifyFen(MidgameFen);
        Stockfish.VerifyFen(KiwipeteFen);
        Stockfish.VerifyFen(MateInOneFen);
        Stockfish.VerifyFen(RookEndgameFen);
        Stockfish.VerifyFen(SharpMiddlegameFen);
    }

    /// <summary>
    /// Runs a fixed-depth iterative-deepening search on a FRESH TTable (the two
    /// sides of a wiring comparison must not share TT state — a warm TT from the
    /// first run would silently hand the second side pre-computed cutoffs).
    /// </summary>
    private static long NodesAt(string fen, int depth, Action<Search>? configure = null)
    {
        var pos = Position.FromFEN(fen);
        var search = new Search(new TTable(4), new ThreadData(0), new SearchSignal(),
                                TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: null);
        configure?.Invoke(search);
        search.IterativeDeepening(pos, depth);
        return search.Nodes;
    }

    // ── 1. DEFAULTS ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Search.UseTtScoreEval), true)]      // measured winner (+12.17)
    [InlineData(nameof(Search.UseSkipFans), false)]        // OFF = legacy skip-one
    [InlineData(nameof(Search.UseQsNarrowing), false)]     // OFF = unlimited QS generation
    [InlineData(nameof(Search.UseSeeReach), false)]        // OFF = flat −50/−100, ceiling ≤ 4
    [InlineData(nameof(Search.UseTtExtension), false)]     // OFF = no TT-granted depth
    [InlineData(nameof(Search.UseVerifiedNmp), false)]     // OFF = legacy NMP returns
    [InlineData(nameof(Search.UseRefNmpEvalScale), true)]  // owner bake (+12.17); was false
    public void Band1_BoolToggles_DefaultOff(string propName, bool expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    [Theory]
    [InlineData(nameof(Search.QsRecaptureOnlyPly), 8)]      // quiescence recapture-only ply
    [InlineData(nameof(Search.QsPromotionMaxPly), 2)]       // quiescence promotion cutoff ply
    [InlineData(nameof(Search.SeeReachMaxDepth), 10)]       // SEE-reach ceiling
    [InlineData(nameof(Search.SeeReachQuietMargin), -44)]   // SEE-reach quiet margin
    [InlineData(nameof(Search.SeeReachNoisyMargin), -17)]   // SEE-reach noisy margin
    [InlineData(nameof(Search.SeeReachHistoryDivisor), 133)]// SEE-reach history divisor
    [InlineData(nameof(Search.TtExtensionMaxDepth), 6)]     // TT-extension ceiling
    public void Band1_IntKnobs_DefaultValues(string propName, int expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    private static Search NewSearch(Position pos)
        => new(new TTable(4), new ThreadData(0), new SearchSignal(), TestHelper.NewEvaluator(pos),
               getTotalNodes: null, onInfo: null);

    // ── 2. DELIVERY (advertised handshake) ────────────────────────────────────────

    [Fact]
    public void Band1_NewKnobs_AdvertisedInHandshakeWithMatchingDefaults()
    {
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        var expected = new (string Name, string Default)[]
        {
            (nameof(Search.UseTtScoreEval), "true"),
            (nameof(Search.UseSkipFans), "false"),
            (nameof(Search.UseQsNarrowing), "false"),
            (nameof(Search.QsRecaptureOnlyPly), "8"),
            (nameof(Search.QsPromotionMaxPly), "2"),
            (nameof(Search.UseSeeReach), "false"),
            (nameof(Search.SeeReachMaxDepth), "10"),
            (nameof(Search.SeeReachQuietMargin), "-44"),
            (nameof(Search.SeeReachNoisyMargin), "-17"),
            (nameof(Search.SeeReachHistoryDivisor), "133"),
            (nameof(Search.UseTtExtension), "false"),
            (nameof(Search.TtExtensionMaxDepth), "6"),
            (nameof(Search.UseVerifiedNmp), "false"),
            (nameof(Search.UseRefNmpEvalScale), "true"), // owner bake
        };
        foreach (var (name, def) in expected)
        {
            Assert.True(orchestrator.Options.TryGetOption(name, out var opt),
                $"{name} is not advertised in the uci handshake — fastchess/SPSA would silently skip it");
            Assert.NotNull(opt);
            Assert.Equal(def, opt.DefaultValue);
        }
    }

    // ── 3. WIRING ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Defaults must be EXACTLY the old behaviour: the compiled defaults and an explicit
    /// assignment of the same defaults must produce identical node counts. This is the
    /// in-suite mirror of (the external bit-identity gate).
    /// </summary>
    [Fact]
    public void Band1_DefaultsAreExplicitlyEquivalent_NodeIdentical()
    {
        long implicitNodes = NodesAt(MidgameFen, 8);

        long explicitNodes = NodesAt(MidgameFen, 8, s =>
        {
            s.UseTtScoreEval = true;
            s.UseSkipFans = false;
            s.UseQsNarrowing = false;
            s.QsRecaptureOnlyPly = 8;
            s.QsPromotionMaxPly = 2;
            s.UseSeeReach = false;
            s.SeeReachMaxDepth = 10;
            s.SeeReachQuietMargin = -44;
            s.SeeReachNoisyMargin = -17;
            s.SeeReachHistoryDivisor = 133;
            s.UseTtExtension = false;
            s.TtExtensionMaxDepth = 6;
            s.UseVerifiedNmp = false;
            s.UseRefNmpEvalScale = false;
        });

        Assert.Equal(implicitNodes, explicitNodes);
    }

    /// <summary>helper: pure-function lock for the reference's canUseTTScore rule.</summary>
    [Fact]
    public void Step1_CanUseTtScore_BoundConsistency()
    {
        static TTEntry E(NodeType flag, int value) => new() { Flag = flag, Value = value };

        // EXACT always passes.
        Assert.True(Search.CanUseTtScore(E(NodeType.Exact, 10), rawStaticEval: 500));
        Assert.True(Search.CanUseTtScore(E(NodeType.Exact, -10), rawStaticEval: 500));
        // LOWER: usable only at or above the raw eval.
        Assert.True(Search.CanUseTtScore(E(NodeType.LowerBound, 500), rawStaticEval: 500));
        Assert.True(Search.CanUseTtScore(E(NodeType.LowerBound, 501), rawStaticEval: 500));
        Assert.False(Search.CanUseTtScore(E(NodeType.LowerBound, 499), rawStaticEval: 500));
        // UPPER: usable only at or below the raw eval.
        Assert.True(Search.CanUseTtScore(E(NodeType.UpperBound, 500), rawStaticEval: 500));
        Assert.True(Search.CanUseTtScore(E(NodeType.UpperBound, 499), rawStaticEval: 500));
        Assert.False(Search.CanUseTtScore(E(NodeType.UpperBound, 501), rawStaticEval: 500));
    }

    /// <summary>
    /// with the toggle ON, a live TT score must replace the raw eval for the
    /// pruners — the tree moves. (On a quiet middlegame at depth ≥ 9 the iterative
    /// deepening has populated the TT, so usable entries exist.)
    /// </summary>
    [Fact]
    public void Step1_TtScoreEval_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9, s => s.UseTtScoreEval = false); // explicit OFF baseline; R1 default is ON
        long on = NodesAt(MidgameFen, 9, s => s.UseTtScoreEval = true);
        Assert.True(off != on, $"UseTtScoreEval did not reach the search (off={off}, on={on})");
    }

    /// <summary>
    /// with the history-pruning margin tuned so the legacy pruner demonstrably
    /// trips (its default −2000·depth rarely fires), the skip-fan must change the tree.
    /// The wiring claim is "when the pruner trips, the fan aborts the rest of the quiets".
    /// </summary>
    [Fact]
    public void Step2_SkipFans_ChangesNodeCount()
    {
        void Tune(Search s)
        {
            s.HistPruneMargin = 500; // the pruner trips often, so the fan's effect is measurable
            // isolate this wiring test from every new special-release default:
            s.UseTtScoreEval = false; s.UseQsTtProbe = false; s.UseFormulaRfp = false;
            s.UseLmrHistoryPrecision = false; s.UseMicroRiders = false;
            s.UseStableTimeManagement = false; s.UseRefSeeThresholds = false;
            s.SeePruningMaxDepth = 4; s.UseQuiescenceChecks = true;
        }

        long off = NodesAt(KiwipeteFen, 8, s => { Tune(s); s.UseSkipFans = false; });
        long on = NodesAt(KiwipeteFen, 8, s => { Tune(s); s.UseSkipFans = true; });
        Assert.True(off != on, $"UseSkipFans did not reach the history-pruning branch (off={off}, on={on})");
    }

    /// <summary>
    /// the toggled search must move the tree — the limits reach the QS loop.
    ///
    /// NET-INDEPENDENT BY CONSTRUCTION (2026-11-02). The old form compared a
    /// depth-10 full-search node count on Kiwipete with the DEFAULT spins (recapture-only
    /// from QS ply 8, promotions off from ply 2). With the promoted net that tree never
    /// reached qDepth 8, so the differential collapsed to exactly zero (23346 == 23346) —
    /// a net-dependent no-op, not a wiring failure. This probe instead runs the QS
    /// DIRECTLY on a constructed position: white's only root capture is Rxd5 (wins the
    /// queen), and in the child black's Rxe5 is a NON-recapture capture (target e5 != d5).
    /// With recapture-only ON that capture is skipped, so the searched tree MUST differ,
    /// for any evaluator. The DEFAULT spins are still pinned explicitly below.
    /// </summary>
    [Fact]
    public void Step3_QsNarrowing_ChangesNodeCount()
    {
        // White: Ke1, Rd1, Re7. Black: Kc8, Qd5, Re8. White's Rxd5 wins the queen and
        // gives NO check (the king is on c8), so the child is a normal QS node; black's
        // Rxe7 then wins the white rook on e7, onto a square (e7) that is NOT the
        // recapture target (d5) — so recapture-only must SKIP it and the child falls
        // back to its stand-pat. (Earlier drafts failed for position reasons: a king on
        // d8 made Rxd5+ an in-check node, which is exempt; a pawn on e5 let the white
        // rook recapture, so SEE pruned the move in BOTH arms.)
        const string fen = "2k1r3/4R3/8/3q4/8/8/8/3RK3 w - - 0 1";
        Stockfish.VerifyFen(fen);

        // the DEFAULT spins stay pinned: the limits are only observable when a tree
        // reaches the gated depth, so the differential below forces the recapture ply.
        var probe = new QuiescenceSearch(new MaterialEvaluator(), new ThreadData(0), new SearchSignal());
        Assert.Equal(8, probe.QsRecaptureOnlyPly);
        Assert.Equal(2, probe.QsPromotionMaxPly);

        int Run(bool narrowing)
        {
            var pos = Position.FromFEN(fen);
            var evaluator = new MaterialEvaluator();
            evaluator.SetPosition(pos);
            var qs = new QuiescenceSearch(evaluator, new ThreadData(0), new SearchSignal())
            {
                UseQsNarrowing = narrowing,
                QsRecaptureOnlyPly = 0, // force the limit to qDepth 0 so the child is filtered
                UseTtScoreEval = false,
                UseQsTtProbe = false,
            };
            return qs.Search(pos, -30000, 30000, ply: 0);
        }

        int off = Run(narrowing: false);
        int on = Run(narrowing: true);
        Assert.True(
            off != on,
            $"UseQsNarrowing did not reach the QS loop (off={off}, on={on}) - the child's "
            + "non-recapture capture Rxe7 must be searched only when narrowing is off");
    }

    /// <summary>
    /// must not break mates: the forced Ra1-a8 is found with the narrowing ON
    /// (in-check qsearch nodes already generate all evasions, untouched).
    /// </summary>
    [Fact]
    public void Step3_QsNarrowing_KeepsForcedMate()
    {
        Stockfish.AssertBestMove(MateInOneFen, 6, "a1a8");

        var posOff = Position.FromFEN(MateInOneFen);
        var searchOff = NewSearch(posOff);
        Move bestOff = searchOff.IterativeDeepening(posOff, 6);

        var posOn = Position.FromFEN(MateInOneFen);
        var searchOn = NewSearch(posOn);
        searchOn.UseQsNarrowing = true;
        searchOn.QsPromotionMaxPly = 0;
        Move bestOn = searchOn.IterativeDeepening(posOn, 6);

        Move expected = TestHelper.FindMove(posOff, "a1", "a8");
        Assert.Equal(expected, bestOff);
        Assert.Equal(expected, bestOn);
    }

    /// <summary>the reach shape (ceiling 10, depth-scaled margins) moves the tree.</summary>
    [Fact]
    public void Step4_SeeReach_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseSeeReach = true);
        Assert.True(off != on, $"UseSeeReach did not reach the SEE pruner (off={off}, on={on})");
    }

    /// <summary>
    /// — THE DECOUPLING PROOF: `UseLmrNoisyMoves` must change the tree with the
    /// live v1 LMR schedule (`UseLmrV2` stays at its default false). Before this change
    /// it was read only inside the v2 early block and was silently dead alone.
    /// </summary>
    [Fact]
    public void Step5_LmrNoisyMoves_ChangesNodeCount_WithoutLmrV2()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s =>
        {
            Assert.False(s.UseLmrV2); // the point of the decoupling: v2 must stay OFF
            s.UseLmrNoisyMoves = true;
        });
        Assert.True(off != on,
            $"UseLmrNoisyMoves is still coupled to UseLmrV2 (off={off}, on={on})");
    }

    /// <summary>the TT extension must shift the tree at a depth where entries exist.</summary>
    [Fact]
    public void Step6_TtExtension_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseTtExtension = true);
        Assert.True(off != on, $"UseTtExtension did not reach the TT probe (off={off}, on={on})");
    }

    /// <summary>
    /// the NMP hardening pack must move the tree. Depth 18 keeps the
    /// verification gate live (children reach depth 17 &gt; 14, rider b).
    /// Rebased 2026-10-26: the original RookEndgameFen @ 17 differential
    /// degraded to EXACT ZERO on the current source with BOTH champion nets
    /// embedded - the verified-NMP path fires so rarely at that (fen, depth) that
    /// v5_poolv8_egb3mo-150's fail-high mix produces a byte-identical tree there.
    /// The wiring itself was proven live four ways (probe table); this test
    /// was rebalanced onto a position where the effect is LARGE and stable:
    /// perft pos 3 (pawn endgame, zugzwang-prone - the case verified NMP exists
    /// for) at depth 18 measured off=575,162 / on=602,540 (delta +27,378 = +4.8 %).
    /// The >= 1000-node margin turns "changed at all" into "changed by a real
    /// amount", so a future silent zero-drift cannot sneak through as PASS.
    /// </summary>
    [Fact]
    public void Step7_VerifiedNmp_ChangesNodeCount()
    {
        const string NmpVerificationFen = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
        long off = NodesAt(NmpVerificationFen, 18);
        long on = NodesAt(NmpVerificationFen, 18, s => s.UseVerifiedNmp = true);
        Assert.True(Math.Abs(on - off) >= 1000,
            $"UseVerifiedNmp differential collapsed off={off}, on={on}, delta={on - off} " +
            "(expected |delta| >= 1000 on the rebalanced perft-3 depth-18 probe)");
    }

    /// <summary>
    /// rider (d): the reference's 191/4 eval-scale must move the tree too. The sharp
    /// middlegame reaches |eval−beta| ≥ 382, where /191 and /200 land in different
    /// reduction buckets. Since the R2 owner bake (2026-11-04) made the toggle default
    /// true, both arms are now set explicitly.
    /// </summary>
    [Fact]
    public void Step7d_NmpEvalScale_ChangesNodeCount()
    {
        long off = NodesAt(SharpMiddlegameFen, 12, s => s.UseRefNmpEvalScale = false);
        long on = NodesAt(SharpMiddlegameFen, 12, s => s.UseRefNmpEvalScale = true);
        Assert.True(off != on, $"UseRefNmpEvalScale did not reach the NMP reduction (off={off}, on={on})");
    }

    /// <summary>rider (a): pure-function lock for the verified-null mate clamp.</summary>
    [Theory]
    [InlineData(31000, 50, 50)]     // mate-line fail-high → clamped to beta
    [InlineData(-31000, -50, -50)]  // negative mate-line → clamped to beta
    [InlineData(29999, 50, 29999)]  // just inside the mate bound → fail-soft score
    [InlineData(120, 50, 120)]      // ordinary fail-high → fail-soft score
    public void Step7_NmpFailHighScore_ClampsOnlyMateLines(int score, int beta, int expected)
    {
        Assert.Equal(expected, Search.NmpFailHighScore(score, beta));
    }

    /// <summary>
    /// must not break mates: the mate clamp only clamps MATE-LINE fail-highs to
    /// beta (it does not produce or suppress mate scores the search itself proves).
    /// </summary>
    [Fact]
    public void Step7_VerifiedNmp_KeepsForcedMate()
    {
        Stockfish.AssertBestMove(MateInOneFen, 6, "a1a8");

        var pos = Position.FromFEN(MateInOneFen);
        var search = NewSearch(pos);
        search.UseVerifiedNmp = true;
        Move best = search.IterativeDeepening(pos, 6);

        Move expected = TestHelper.FindMove(pos, "a1", "a8");
        Assert.Equal(expected, best);
    }
}
