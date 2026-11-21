namespace PawnForge.Tests;

using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Locks for "#### Band 3 — extensions &amp; feedback" of the Final Full Audit
///:
///
/// — Extension trio (`UseExtensionTrio` + `UseTrioHindsight` /
/// `UseTrioAlphaRaise` / `UseTrioTtShrink`, sub-toggles default ON inside the
/// default-OFF master).
/// — Selective singular entry policy (`UseSelectiveSingular`, depth ≥ 10,
/// depth-1 TT margin, PV/cut-node only, single +1 tier).
/// — Quiet-order dynamic pre-bonus (`UseEvalSwingBonus`, 10·−swing clamped
/// [−100, 200]; the reference).
/// — LMR v1 history precision (`UseLmrHistoryPrecision`, multiply-before-divide).
/// — LMR reduction floor (`UseLmrReductionFloor`, min(reduction, newDepth−2)).
/// — QS correction history (`UseQsCorrectionHistory`).
///
/// Same three test kinds as the earlier band classes: DEFAULTS, DELIVERY, WIRING — plus
/// the pure-function lock and a direct QS-correction proof.
/// </summary>
public class StructuralClosuresBand3Tests
{
    private const string MidgameFen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";
    private const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string SharpMiddlegameFen = "2rr3k/1ppq1ppp/p1bb4/4p3/4P3/1BN1Q3/PPP2PPP/R1B2RK1 w - - 0 1";
    private const string StartposFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static long NodesAt(string fen, int depth, Action<Search>? configure = null)
    {
        var pos = Position.FromFEN(fen);
        var search = NewSearch(pos);
        configure?.Invoke(search);
        search.IterativeDeepening(pos, depth);
        return search.Nodes;
    }

    private static Search NewSearch(Position pos)
        => new(new TTable(4), new ThreadData(0), new SearchSignal(), TestHelper.NewEvaluator(pos),
               getTotalNodes: null, onInfo: null);

    /// <summary>
    /// FEN-verification gate (project rule): every fixture constant used by this class is
    /// parsed by the oracle at test time — legality AND geometry, not just identity.
    /// </summary>
    [Fact]
    public void Band3_Fixtures_AreStockfishVerified()
    {
        Stockfish.VerifyFen(MidgameFen);
        Stockfish.VerifyFen(KiwipeteFen);
        Stockfish.VerifyFen(SharpMiddlegameFen);
        Stockfish.VerifyFen(StartposFen);
    }

    // ── 1. DEFAULTS ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Search.UseExtensionTrio), false)]      // master OFF
    [InlineData(nameof(Search.UseTrioHindsight), true)]       // sub-toggles ON inside the master
    [InlineData(nameof(Search.UseTrioAlphaRaise), true)]
    [InlineData(nameof(Search.UseTrioTtShrink), true)]
    [InlineData(nameof(Search.UseSelectiveSingular), false)]  // OFF = legacy gate
    [InlineData(nameof(Search.UseEvalSwingBonus), false)]     // OFF = no pre-bonus
    [InlineData(nameof(Search.UseLmrHistoryPrecision), true)] // measured winner (+12.17)
    [InlineData(nameof(Search.UseLmrReductionFloor), false)]  // OFF = legacy clamp
    [InlineData(nameof(Search.UseQsCorrectionHistory), false)]// OFF = raw stand-pat
    public void Band3_BoolToggles_DefaultValues(string propName, bool expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    [Theory]
    [InlineData(nameof(Search.SelectiveSingularMinDepth), 10)] // gate
    [InlineData(nameof(Search.EvalSwingBonusMult), 10)]        // multiplier of the eval-swing pre-bonus
    [InlineData(nameof(Search.EvalSwingBonusMin), -100)]       // dynamicPolicyMin
    [InlineData(nameof(Search.EvalSwingBonusMax), 200)]        // dynamicPolicyMax
    public void Band3_IntKnobs_DefaultValues(string propName, int expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    // ── 2. DELIVERY (advertised handshake) ────────────────────────────────────────

    [Fact]
    public void Band3_NewKnobs_AdvertisedInHandshakeWithMatchingDefaults()
    {
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        var expected = new (string Name, string Default)[]
        {
            (nameof(Search.UseExtensionTrio), "false"),
            (nameof(Search.UseTrioHindsight), "true"),
            (nameof(Search.UseTrioAlphaRaise), "true"),
            (nameof(Search.UseTrioTtShrink), "true"),
            (nameof(Search.UseSelectiveSingular), "false"),
            (nameof(Search.UseEvalSwingBonus), "false"),
            (nameof(Search.UseLmrHistoryPrecision), "true"),
            (nameof(Search.UseLmrReductionFloor), "false"),
            (nameof(Search.UseQsCorrectionHistory), "false"),
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

    /// <summary>the trio bundle must move the tree at a fixed depth.</summary>
    [Fact]
    public void Step13_ExtensionTrio_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseExtensionTrio = true);
        Assert.True(off != on, $"UseExtensionTrio did not reach the search (off={off}, on={on})");
    }

    /// <summary>
    /// coupling lock: the three sub-toggles are advertised for bisection, but they are
    /// INERT without the master (the whole trio block is gated on `UseExtensionTrio`). Pinned
    /// by flipping each OFF alone and requiring the bare node count.
    /// </summary>
    [Theory]
    [InlineData(nameof(Search.UseTrioHindsight))]
    [InlineData(nameof(Search.UseTrioAlphaRaise))]
    [InlineData(nameof(Search.UseTrioTtShrink))]
    public void Step13_TrioSubToggles_AreInertWithoutTheMaster(string propName)
    {
        long bare = NodesAt(MidgameFen, 9);
        long alone = NodesAt(MidgameFen, 9, s =>
            typeof(Search).GetProperty(propName)!.SetValue(s, false));
        Assert.Equal(bare, alone);
    }

    /// <summary>selective singular (gate depth 10) must move the tree.</summary>
    [Fact]
    public void Step14_SelectiveSingular_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 12);
        long on = NodesAt(MidgameFen, 12, s => s.UseSelectiveSingular = true);
        Assert.True(off != on, $"UseSelectiveSingular did not reach the singular block (off={off}, on={on})");
    }

    /// <summary>the dynamic pre-bonus writes history and must move the tree.</summary>
    [Fact]
    public void Step15_EvalSwingBonus_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseEvalSwingBonus = true);
        Assert.True(off != on, $"UseEvalSwingBonus did not reach the quiet history (off={off}, on={on})");
    }

    /// <summary>
    /// pure-function lock: the precise form keeps sub-divisor history scores, the
    /// legacy form truncates them to zero. The v1 term keeps its own HIST1 divisors
    /// (3419/3257 — part of the shipped default behaviour, deliberately NOT moved to the v2
    /// values); only the multiply/divide order changes.
    /// </summary>
    [Fact]
    public void Step16_LmrHistoryTerm1024_PreciseKeepsSubPlyPrecision()
    {
        const int small = 1000;
        Assert.Equal(0, SearchHeuristics.LmrHistoryTerm1024(small, quiet: true));
        Assert.Equal(small * SearchHeuristics.LmrUnit / SearchHeuristics.LmrQuietHistoryDiv,
                     SearchHeuristics.LmrHistoryTerm1024(small, quiet: true, precise: true));

        const int max = SearchHeuristics.LmrHistoryClamp; // 8033
        Assert.Equal(2 * SearchHeuristics.LmrUnit, SearchHeuristics.LmrHistoryTerm1024(max, quiet: true));
        Assert.Equal(max * SearchHeuristics.LmrUnit / SearchHeuristics.LmrQuietHistoryDiv,
                     SearchHeuristics.LmrHistoryTerm1024(max, quiet: true, precise: true));

        // Noisy moves use the noisy divisor; the clamp is symmetric.
        Assert.Equal(-(max * SearchHeuristics.LmrUnit / SearchHeuristics.LmrNoisyHistoryDiv),
                     SearchHeuristics.LmrHistoryTerm1024(-max, quiet: false, precise: true));
    }

    /// <summary>the toggle must move the tree at a fixed depth.</summary>
    [Fact]
    public void Step16_LmrHistoryPrecision_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9, s => s.UseLmrHistoryPrecision = false); // explicit OFF baseline; R1 default is ON
        long on = NodesAt(MidgameFen, 9, s => s.UseLmrHistoryPrecision = true);
        Assert.True(off != on, $"UseLmrHistoryPrecision did not reach the LMR term (off={off}, on={on})");
    }

    /// <summary>the reduction floor must move the tree at a fixed depth.</summary>
    [Fact]
    public void Step17_LmrReductionFloor_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseLmrReductionFloor = true);
        Assert.True(off != on, $"UseLmrReductionFloor did not reach the LMR clamp (off={off}, on={on})");
    }

    /// <summary>the QS correction must move the tree at a fixed depth.</summary>
    [Fact]
    public void Step18_QsCorrectionHistory_ChangesNodeCount()
    {
        long off = NodesAt(KiwipeteFen, 10);
        long on = NodesAt(KiwipeteFen, 10, s => s.UseQsCorrectionHistory = true);
        Assert.True(off != on, $"UseQsCorrectionHistory did not reach the QS stand-pat (off={off}, on={on})");
    }

    /// <summary>
    /// direct proof: a seeded pawn-key correction of 5120 history units is +10 cp
    /// (`/512`), and the QS stand-pat must include exactly that when the toggle is ON. The
    /// start position is quiet, so the returned score IS the stand-pat.
    /// </summary>
    [Fact]
    public void Step18_QsCorrectionHistory_AppliesTheExactCorrection()
    {
        var posOff = Position.FromFEN(StartposFen);
        var qsOff = new QuiescenceSearch(TestHelper.NewEvaluator(posOff), new ThreadData(0), new SearchSignal());
        qsOff.UseRefCorrectionHistory = false; // R2 (2026-11-04) made the default true; this arm tests the legacy /512 path
        int off = qsOff.Search(posOff, -30000, 30000, ply: 0);

        var posOn = Position.FromFEN(StartposFen);
        var tdOn = new ThreadData(0);
        tdOn.CorrectionHistory[posOn.SideToMove][(int)(posOn.PawnKey & 16383)] = 5120; // /512 = +10
        var qsOn = new QuiescenceSearch(TestHelper.NewEvaluator(posOn), tdOn, new SearchSignal());
        qsOn.UseQsCorrectionHistory = true;
        qsOn.UseRefCorrectionHistory = false; // the legacy /512 term is what is seeded here (R2 default is true)
        int on = qsOn.Search(posOn, -30000, 30000, ply: 0);

        Assert.Equal(off + 10, on);
    }

    /// <summary>
    /// direct proof for the reference's 4-term path: a seeded pawn term of 640 units
    /// contributes `640·99/100/64 = 9` cp. The countermove term is zero (no previous move).
    /// </summary>
    [Fact]
    public void Step18_QsCorrectionHistory_ReferencePath_AppliesTheWeightedTerm()
    {
        var posOff = Position.FromFEN(StartposFen);
        var qsOff = new QuiescenceSearch(TestHelper.NewEvaluator(posOff), new ThreadData(0), new SearchSignal());
        qsOff.UseRefCorrectionHistory = true; // explicit: the R2 default, isolated from the legacy path
        int off = qsOff.Search(posOff, -30000, 30000, ply: 0);

        var posOn = Position.FromFEN(StartposFen);
        var tdOn = new ThreadData(0);
        tdOn.CorrectionPawn[posOn.SideToMove][(int)(posOn.PawnKey & 16383)] = 640; // 640*99/100/64 = 9
        var qsOn = new QuiescenceSearch(TestHelper.NewEvaluator(posOn), tdOn, new SearchSignal());
        qsOn.UseQsCorrectionHistory = true;
        qsOn.UseRefCorrectionHistory = true;
        int on = qsOn.Search(posOn, -30000, 30000, ply: 0);

        Assert.Equal(off + 9, on);
    }
}
