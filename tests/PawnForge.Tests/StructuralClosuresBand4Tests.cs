namespace PawnForge.Tests;

using System;
using PawnForge.Core;
using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Locks for "#### Band 4 — control-plane &amp; NPS polish" of the Final Full Audit
///:
///
/// — Captures-only movegen (`UseCapturesOnlyMovegen`; node-identical, NPS only).
/// — Lazy SEE in the MovePicker (`UseLazySee`; ordering/NPS).
/// — PEXT/magic runtime selection (automatic; `PextBitboards`).
/// — Stable time management (`UseStableTimeManagement`; timed only).
/// — Contempt spin (`Contempt`, draw score = Contempt·(2·stm−1)).
/// — Micro riders (`UseMicroRiders`: QS fail-high averaging, IIR quality gate,
/// 1024-node poll).
/// </summary>
public class StructuralClosuresBand4Tests
{
    private const string MidgameFen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";
    private const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

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

    /// <summary>FEN-verification gate (project rule).</summary>
    [Fact]
    public void Band4_Fixtures_AreStockfishVerified()
    {
        Stockfish.VerifyFen(MidgameFen);
        Stockfish.VerifyFen(KiwipeteFen);
    }

    // ── 1. DEFAULTS ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Search.UseCapturesOnlyMovegen), false)]
    [InlineData(nameof(Search.UseLazySee), false)]
    [InlineData(nameof(Search.UseStableTimeManagement), true)] // +36.62
    [InlineData(nameof(Search.UseMicroRiders), true)]         // +11.30
    public void Band4_BoolToggles_DefaultOff(string propName, bool expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    [Fact]
    public void Step23_Contempt_DefaultsToZero()
    {
        var pos = Position.FromFEN(MidgameFen);
        Assert.Equal(0, NewSearch(pos).Contempt);
    }

    // ── 2. DELIVERY (advertised handshake) ────────────────────────────────────────

    [Fact]
    public void Band4_NewKnobs_AdvertisedInHandshakeWithMatchingDefaults()
    {
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        var expected = new (string Name, string Default)[]
        {
            (nameof(Search.UseCapturesOnlyMovegen), "false"),
            (nameof(Search.UseLazySee), "false"),
            (nameof(Search.UseStableTimeManagement), "true"),
            (nameof(Search.Contempt), "0"),
            (nameof(Search.UseMicroRiders), "true"),
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
    /// — THE NODE-IDENTITY CLAIM: captures-only generation emits exactly the set the
    /// full generator + picker partition keeps, so a fixed-depth search must be identical.
    /// </summary>
    [Fact]
    public void Step19_CapturesOnlyMovegen_IsNodeIdentical()
    {
        long off = NodesAt(KiwipeteFen, 9);
        long on = NodesAt(KiwipeteFen, 9, s => s.UseCapturesOnlyMovegen = true);
        Assert.Equal(off, on);
    }

    /// <summary>the captures-only generator set equals the filtered full set.</summary>
    [Theory]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")] // kiwipete
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")] // promotion-rich endgame
    [InlineData("r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9")]
    public void Step19_CapturesOnlyGeneration_MatchesFilteredFullSet(string fen)
    {
        var pos = Position.FromFEN(fen);
        Span<Move> full = stackalloc Move[256];
        int fullCount = pos.GenerateLegalMoves(full);

        Span<Move> caps = stackalloc Move[256];
        int capsCount = pos.GenerateLegalMoves(caps, respectPin: true, capturesOnly: true);

        // The picker's captures-only mode keeps captures / en passant / capture-promotions.
        var expected = new System.Collections.Generic.HashSet<Move>();
        for (int i = 0; i < fullCount; i++)
        {
            Move m = full[i];
            bool isCapture = m.IsEnPassant || pos.GetPieceAt(m.To) != Piece.Empty;
            if (isCapture) expected.Add(m);
        }

        var actual = new System.Collections.Generic.HashSet<Move>();
        for (int i = 0; i < capsCount; i++) actual.Add(caps[i]);

        Assert.Equal(expected, actual);
    }

    /// <summary>lazy SEE changes the capture order and must move the tree.</summary>
    [Fact]
    public void Step20_LazySee_ChangesNodeCount()
    {
        long off = NodesAt(KiwipeteFen, 9);
        long on = NodesAt(KiwipeteFen, 9, s => s.UseLazySee = true);
        Assert.True(off != on, $"UseLazySee did not reach the MovePicker (off={off}, on={on})");
    }

    /// <summary>
    /// PEXT and magic must be bit-identical on every square and a spread of random
    /// occupancies. When BMI2 is unavailable the test documents that and passes (the dispatch
    /// then uses magic).
    /// </summary>
    [Fact]
    public void Step21_Pext_MatchesMagic_OnEverySquareAndRandomOccupancies()
    {
        if (!PextBitboards.Ready)
            return; // no BMI2 PEXT on this CPU: the dispatch correctly stays on magic

        ulong seed = 0xD1B54A32D192ED03UL;
        for (int sample = 0; sample < 256; sample++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            ulong occ = seed;
            for (int sq = 0; sq < 64; sq++)
            {
                Assert.Equal(MagicBitboards.RookAttacks(sq, occ), PextBitboards.RookAttacks(sq, occ));
                Assert.Equal(MagicBitboards.BishopAttacks(sq, occ), PextBitboards.BishopAttacks(sq, occ));
            }
        }
    }

    /// <summary>
    /// the stable-TM scaler must produce a different soft limit than the legacy path
    /// for a stable, node-concentrated search, and must stay inside its clamp band and below
    /// hard − overhead.
    /// </summary>
    [Fact]
    public void Step22_StableTimeManagement_ScalesTheSoftLimit()
    {
        var tm = new TimeManager();
        tm.UseStableTimeManagement = false; // explicit legacy baseline; R1 default is ON
        tm.Init(wtime: 60_000, btime: 60_000, winc: 0, binc: 0, movesToGo: 0, side: 0,
                currentTime: 1_000_000, moveOverhead: 50);

        // Feed a stable best move/score and a node-concentrated search.
        for (int i = 0; i < 6; i++) tm.UpdatePVStability(currentBestMove: 42, stable: true);
        tm.SetCompletedDepth(8);
        tm.SetNodeStats(bestMoveNodes: 500_000, totalNodes: 1_000_000);
        tm.ShouldContinue(1_000_000, 42, score: 30);

        long legacy = tm.EffectiveSoftLimit();

        tm.UseStableTimeManagement = true;
        long stable = tm.EffectiveSoftLimit();

        Assert.NotEqual(legacy, stable);

        long alloc = tm.SoftLimit - tm.StartTime;
        long lower = tm.StartTime + (long)(alloc * 0.12);
        long upper = Math.Min(tm.StartTime + (long)(alloc * 2.45), tm.HardLimit - 50);
        Assert.InRange(stable, lower, upper);
    }

    /// <summary>pure-function lock: Contempt·(2·stm−1), 0 by default.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(10, 0, -10)]  // White's draw value is −Contempt
    [InlineData(10, 1, 10)]   // Black's is +Contempt
    [InlineData(-10, 0, 10)]
    public void Step23_DrawScoreFor_MatchesTheH1Formula(int contempt, int stm, int expected)
    {
        Assert.Equal(expected, Search.DrawScoreFor(contempt, stm));
    }

    /// <summary>(a) pure-function lock: non-mate fail-highs average, mate keeps beta.</summary>
    [Theory]
    [InlineData(120, 100, 110)]    // (120+100)/2
    [InlineData(31000, 100, 100)]  // mate-line score → bare beta
    [InlineData(120, 30990, 30990)]// mate-line beta → bare beta
    public void Step24_QsFailHighScore_AveragesOnlyNonMate(int score, int beta, int expected)
    {
        Assert.Equal(expected, Search.QsFailHighScore(score, beta));
    }

    /// <summary>the rider batch (IIR quality gate + QS averaging) must move the tree.</summary>
    [Fact]
    public void Step24_MicroRiders_ChangeNodeCount()
    {
        long off = NodesAt(MidgameFen, 10, s => s.UseMicroRiders = false); // explicit OFF baseline; R1 default is ON
        long on = NodesAt(MidgameFen, 10, s => s.UseMicroRiders = true);
        Assert.True(off != on, $"UseMicroRiders did not reach the search (off={off}, on={on})");
    }

    /// <summary>
    /// (a) direct QS proof: on a winning capture that fails high, the legacy QS returns
    /// bare beta while the rider returns `(score + beta)/2`. The position is a free queen
    /// (Rxd5+), and the check makes the CHILD return a fail-soft score below −beta, so the
    /// root's fail-high score strictly exceeds beta (the only case where averaging differs
    /// from the bare-beta return). The wide-window call yields the true score.
    /// </summary>
    [Fact]
    public void Step24_QsFailHighAveraging_ReturnsTheAverage()
    {
        const string fen = "3k4/8/8/3q4/8/8/8/3RK3 w - - 0 1";
        Stockfish.VerifyFen(fen);

        var posTrue = Position.FromFEN(fen);
        // NET-INDEPENDENT: the probe is about the fail-high averaging rule, so
        // the evaluator is a fixed material count, not the embedded net. With the net this
        // premise flipped by 1 cp when the champion changed (516 -> 515).
        var evaluator = new MaterialEvaluator();
        evaluator.SetPosition(posTrue);
        int standPat = evaluator.Evaluate(); // the raw stand-pat the QS uses
        var qsTrue = new QuiescenceSearch(evaluator, new ThreadData(0), new SearchSignal());
        qsTrue.UseMicroRiders = false; qsTrue.UseTtScoreEval = false; qsTrue.UseQsTtProbe = false;
        int trueScore = qsTrue.Search(posTrue, -30000, 30000, ply: 0);
        int beta = standPat + 1; // above the stand-pat (no cutoff), below the winning capture
        Assert.True(trueScore > beta, $"expected a winning capture above {beta}, got {trueScore}");

        var posOff = Position.FromFEN(fen);
        var evalOff = new MaterialEvaluator();
        evalOff.SetPosition(posOff);
        var qsOff = new QuiescenceSearch(evalOff, new ThreadData(0), new SearchSignal());
        qsOff.UseMicroRiders = false; qsOff.UseTtScoreEval = false; qsOff.UseQsTtProbe = false;
        int legacy = qsOff.Search(posOff, -30000, beta, ply: 0);
        Assert.Equal(beta, legacy); // bare beta

        var posOn = Position.FromFEN(fen);
        var evalOn = new MaterialEvaluator();
        evalOn.SetPosition(posOn);
        var qsOn = new QuiescenceSearch(evalOn, new ThreadData(0), new SearchSignal());
        qsOn.UseMicroRiders = true; qsOn.UseTtScoreEval = false; qsOn.UseQsTtProbe = false;
        int rider = qsOn.Search(posOn, -30000, beta, ply: 0);
        Assert.Equal((trueScore + beta) / 2, rider);
    }
}
