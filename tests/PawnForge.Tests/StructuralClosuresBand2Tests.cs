namespace PawnForge.Tests;

using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Locks for "#### Band 2 — reduction &amp; eval plumbing" of the Final Full Audit
///:
///
/// — RFP fail-soft half, folded into `UseFormulaRfp` (the reference's damped spill
/// `beta + (staticEval − beta)/3`).
/// — Linear in-loop futility margin (`UseLinearFutilityMargin`, the reference's
/// constants 108/82/98/4; corrected from stale earlier values).
/// — Formula LMP (`UseFormulaLmp`, (3+38d)/10 / (1+83d)/10, gate 8; the
/// constants were corrected 39/87 → 38/83 against the reference).
/// — LMR full term set: all `LmrV2*` weights re-verified/corrected against
/// the reference; the PF-only PV-distance term now
/// defaults OFF so the `UseLmrV2` arm is the reference's term set.
/// — Per-thread eval cache (`UseEvalCache`, `EvalCache.cs`): a pure NPS lever —
/// a fixed-depth node count MUST NOT move, and cache hits MUST occur.
///
/// Same three test kinds as the Band 1 class: DEFAULTS (bit-identical fresh binary),
/// DELIVERY (hidden-lever registration), WIRING (the toggle reaches the search).
/// </summary>
public class StructuralClosuresBand2Tests
{
    // The same the oracle-verified fixtures the Band 1 class uses (verified again below).
    private const string MidgameFen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";
    private const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    /// <summary>Fixed-depth search on a fresh TT (no shared state between the two arms).</summary>
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
    public void Band2_Fixtures_AreStockfishVerified()
    {
        Stockfish.VerifyFen(MidgameFen);
        Stockfish.VerifyFen(KiwipeteFen);
    }

    // ── 1. DEFAULTS ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Search.UseLinearFutilityMargin), false)] // OFF = the shipped step cells
    [InlineData(nameof(Search.UseFormulaLmp), false)]           // OFF = clamped array + halving
    [InlineData(nameof(Search.UseEvalCache), false)]            // OFF = raw evaluator call
    [InlineData(nameof(Search.UseLmrTermPvDistance), false)]    // PF-only extra now OFF
    public void Band2_BoolToggles_DefaultOff(string propName, bool expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    /// <summary>
    /// the LMR weights must be the reference 's real values (:
    /// 62-72), not the earlier revision's numbers. The earlier C1 port carried
    /// 1264/1910/966/1088/3419/3257/101/80/71; these are the verified values.
    /// </summary>
    [Theory]
    [InlineData(nameof(Search.LmrV2TtPvWeight), 911)]
    [InlineData(nameof(Search.LmrV2CutNodeWeight), 2085)]
    [InlineData(nameof(Search.LmrV2NotImprovingWeight), 70)]
    [InlineData(nameof(Search.LmrV2FutileWeight), 962)]
    [InlineData(nameof(Search.LmrV2FailHighCountWeight), 1035)]
    [InlineData(nameof(Search.LmrV2QuietHistoryDiv), 3258)]
    [InlineData(nameof(Search.LmrV2NoisyHistoryDiv), 3155)]
    [InlineData(nameof(Search.LmrV2FutileMargin), 108)]
    [InlineData(nameof(Search.LmrV2FutileScale), 82)]
    [InlineData(nameof(Search.LmrV2FutileHistDiv), 98)]
    public void Step11_LmrWeights_MatchReferenceValues(string propName, int expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    /// <summary>
    /// /10: the futility and LMP constants were corrected to the source while
    /// implementing the linear arms (the dormant C4a/C4c paths had 96/76/87/3 and 39/87).
    /// </summary>
    [Theory]
    [InlineData(nameof(Search.FutilityMargin), 108)]
    [InlineData(nameof(Search.FutilityScale), 82)]
    [InlineData(nameof(Search.FutilityHistDiv), 98)]
    [InlineData(nameof(Search.FutilityMoveMult), 4)]
    [InlineData(nameof(Search.RefLmpScale), 38)]
    [InlineData(nameof(Search.RefLmpImpScale), 83)]
    public void Step9And10_Constants_MatchReferenceValues(string propName, int expected)
    {
        var pos = Position.FromFEN(MidgameFen);
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    // ── 2. DELIVERY (advertised handshake) ────────────────────────────────────────

    [Fact]
    public void Band2_NewKnobs_AdvertisedInHandshakeWithMatchingDefaults()
    {
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        var expected = new (string Name, string Default)[]
        {
            (nameof(Search.UseLinearFutilityMargin), "false"),
            (nameof(Search.UseFormulaLmp), "false"),
            (nameof(Search.UseEvalCache), "false"),
            // PF-only extra: advertised default matches the compiled default.
            (nameof(Search.UseLmrTermPvDistance), "false"),
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

    /// <summary>pure-function lock: formula arm damps, array arm keeps the legacy return.</summary>
    [Theory]
    [InlineData(200, 100, true, 133)]    // beta + (200-100)/3 = 133 (integer division)
    [InlineData(200, 100, false, 200)]   // legacy full fail-soft
    [InlineData(-50, -100, true, -84)]   // -100 + 50/3 = -100 + 16 = -84 (truncation toward zero)
    [InlineData(100, 100, true, 100)]    // boundary: exactly beta
    public void Step8_RfpFailSoftScore_FormulaArmDampsLegacyKeeps(
        int staticEval, int beta, bool formulaArm, int expected)
    {
        Assert.Equal(expected, Search.RfpFailSoftScore(staticEval, beta, formulaArm));
    }

    /// <summary>
    /// with the toggle ON the live futility path reads the linear margin, so the
    /// tree must move at a fixed depth. (Midgame, depth 9: 43,597 → 65,287 when measured.)
    /// </summary>
    [Fact]
    public void Step9_LinearFutilityMargin_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseLinearFutilityMargin = true);
        Assert.True(off != on, $"UseLinearFutilityMargin did not reach the futility block (off={off}, on={on})");
    }

    /// <summary>
    /// the linear LMP thresholds (gate 8) must move the tree at a fixed depth.
    /// (Midgame, depth 9: 43,597 → 30,897 when measured.)
    /// </summary>
    [Fact]
    public void Step10_FormulaLmp_ChangesNodeCount()
    {
        long off = NodesAt(MidgameFen, 9);
        long on = NodesAt(MidgameFen, 9, s => s.UseFormulaLmp = true);
        Assert.True(off != on, $"UseFormulaLmp did not reach the LMP block (off={off}, on={on})");
    }

    /// <summary>
    /// — THE PURE-SPEED CLAIM: the eval cache returns values bit-identical to a
    /// fresh Evaluate, so the fixed-depth tree must be IDENTICAL with the toggle on and
    /// off, while the hit counter proves the cache is actually used.
    /// </summary>
    [Fact]
    public void Step12_EvalCache_IsBitIdentical_AndActuallyHits()
    {
        long off = NodesAt(MidgameFen, 9);

        var pos = Position.FromFEN(MidgameFen);
        var search = NewSearch(pos);
        search.UseEvalCache = true;
        search.IterativeDeepening(pos, 9);

        Assert.Equal(off, search.Nodes);
        Assert.True(search.EvalCacheHits > 0,
            $"the eval cache was never hit (nodes={search.Nodes}, hits={search.EvalCacheHits})");
    }

    // ── EvalCache unit tests ────────────────────────────────────────────

    [Fact]
    public void EvalCache_StoreThenProbe_HitsAndReturnsTheStoredScore()
    {
        using var cache = new EvalCache(8);
        const ulong key = 0x0123456789ABCDEFUL;
        cache.Store(key, stm: 0, score: 1234);

        Assert.True(cache.TryProbe(key, stm: 0, out int score));
        Assert.Equal(1234, score);
        Assert.False(cache.TryProbe(key, stm: 1, out _));           // wrong side to move
        Assert.False(cache.TryProbe(key ^ 0xFFFF_0000_0000_0000UL, stm: 0, out _)); // wrong tag
    }

    [Fact]
    public void EvalCache_EmptySlot_IsAMiss()
    {
        using var cache = new EvalCache(8);
        Assert.False(cache.TryProbe(0xDEADBEEFCAFEBABEUL, stm: 0, out int score));
        Assert.Equal(0, score);
    }

    [Fact]
    public void EvalCache_OutOfRangeScores_AreNotCached()
    {
        using var cache = new EvalCache(8);
        const ulong key = 0x1122334455667788UL;
        cache.Store(key, stm: 0, score: short.MaxValue + 1);
        Assert.False(cache.TryProbe(key, stm: 0, out _));

        cache.Store(key, stm: 0, score: short.MinValue); // the empty sentinel is never a real score
        Assert.False(cache.TryProbe(key, stm: 0, out _));
    }

    [Fact]
    public void EvalCache_SizeIsClampedToTheAuditRange()
    {
        // The clamp is a pure helper so the 1 GB upper bound is testable without allocating it.
        Assert.Equal(8, EvalCache.ClampSizeMb(1));
        Assert.Equal(8, EvalCache.ClampSizeMb(8));
        Assert.Equal(128, EvalCache.ClampSizeMb(128));
        Assert.Equal(1024, EvalCache.ClampSizeMb(4096));

        using var small = new EvalCache(16);
        Assert.Equal(16, small.SizeMb);
    }

    [Fact]
    public void EvalCache_Clear_InvalidatesEveryEntry()
    {
        using var cache = new EvalCache(8);
        const ulong key = 0x99AABBCCDDEEFF00UL;
        cache.Store(key, stm: 1, score: -321);
        Assert.True(cache.TryProbe(key, stm: 1, out _));
        cache.Clear();
        Assert.False(cache.TryProbe(key, stm: 1, out _));
    }
}
