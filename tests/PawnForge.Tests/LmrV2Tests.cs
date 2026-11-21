using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for LMR v2: the reference's
/// SPSA-tuned fixed-point reduction architecture. Covers the two base tables (quiet / noisy,
/// in 1024ths of a ply), the TT PV-flag round trip that feeds the ttPv term, the per-ply
/// pvDistance / failHighCount stacks, and behavioral smokes proving the v2 path is active and
/// every sub-toggle bisects cleanly. Values are quoted from the reference engine
/// (+).
/// </summary>
public class LmrV2Tests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Base tables (Search.Reduction.cs)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The quiet base table follows round((0.92 + ln(d)·ln(m)/divisor) · 1024) with the
    /// RE-TUNED divisor 2.50 (Search.Reduction.cs, — lowered from the reference's 3.09 to
    /// recover v1's aggression at deep/late nodes while keeping the v2 fixed-point precision).
    /// Spot values verified by hand; the zero-row/column rule (no reduction at depth 0 or for
    /// the first move) matches the v1 table.
    /// </summary>
    [Theory]
    [InlineData(0, 10, 0)]     // depth 0 -> 0
    [InlineData(10, 0, 0)]     // move 0  -> 0
    [InlineData(1, 1, 942)]    // 0.92 * 1024 = 942.08 -> 942
    [InlineData(10, 10, 3114)] // (0.92 + ln10·ln10/2.50) * 1024 = 3113.6 -> 3114
    [InlineData(20, 16, 4344)] // (0.92 + ln20·ln16/2.50) * 1024 = 4344.3 -> 4344
    public void LmrV2_QuietBase_MatchesReferenceFormula(int depth, int moveNumber, int expected)
    {
        Assert.Equal(expected, Search.LmrV2BaseReduction(noisy: false, depth, moveNumber));
    }

    /// <summary>
    /// The noisy (capture) base table follows round((0.93 + ln(d)·ln(m)/divisor) · 1024) with
    /// the RE-TUNED divisor 2.50 (lowered from the reference's 3.07) and is distinct from
    /// the quiet table.
    /// </summary>
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(1, 1, 952)]    // 0.93 * 1024 = 952.32 -> 952
    [InlineData(10, 10, 3124)] // (0.93 + ln10·ln10/2.50) * 1024 = 3123.6 -> 3124
    [InlineData(20, 16, 4354)] // (0.93 + ln20·ln16/2.50) * 1024 = 4354.5 -> 4354
    public void LmrV2_NoisyBase_MatchesReferenceFormula(int depth, int moveNumber, int expected)
    {
        Assert.Equal(expected, Search.LmrV2BaseReduction(noisy: true, depth, moveNumber));
    }

    /// <summary>
    /// Both v2 tables are monotonic non-decreasing in depth and move number (the ln·ln term is
    /// non-negative for d, m >= 1), and large indices clamp instead of throwing.
    /// </summary>
    [Fact]
    public void LmrV2_BaseTables_MonotonicAndClamped()
    {
        foreach (var noisy in new[] { false, true })
        {
            for (int d = 1; d <= 63; d++)
            {
                for (int m = 1; m <= 63; m++)
                {
                    Assert.True(Search.LmrV2BaseReduction(noisy, d, m) >= Search.LmrV2BaseReduction(noisy, d - 1, m),
                        $"not monotonic in depth (noisy={noisy}) at d={d}, m={m}");
                    Assert.True(Search.LmrV2BaseReduction(noisy, d, m) >= Search.LmrV2BaseReduction(noisy, d, m - 1),
                        $"not monotonic in moveNumber (noisy={noisy}) at d={d}, m={m}");
                }
            }
            int maxEntry = Search.LmrV2BaseReduction(noisy, 63, 63);
            Assert.Equal(maxEntry, Search.LmrV2BaseReduction(noisy, 500, 500));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TT PV flag (Phase C1 layout change: payload bit 63, age shrank to 5 bits)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The PV flag survives a Store/Probe round trip together with all the other payload
    /// fields (score, depth, flag, move, static eval, age) — the age field shrank from 6 to
    /// 5 bits to make room, so the age value must also round-trip within its 0..31 range.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TTable_PvFlag_RoundTripsWithAllFields(bool pv)
    {
        var tt = new TTable(1);
        ulong key = 0x0123456789ABCDEFUL;
        var move = new Move(12, 28, 0);
        tt.Store(key, value: 137, depth: 9, flag: NodeType.Exact, best: move,
                 currentAge: 7, ply: 0, staticEval: -421, pv: pv);

        Assert.True(tt.Probe(key, out var entry, ply: 0));
        Assert.Equal(pv, entry.IsPv);
        Assert.Equal(137, entry.Value);
        Assert.Equal(9, entry.Depth);
        Assert.Equal(NodeType.Exact, entry.Flag);
        Assert.Equal(move, entry.BestMove);
        Assert.Equal(-421, entry.StaticEval);
        Assert.Equal(7, entry.Age);
    }

    /// <summary>
    /// The PV bit must not corrupt the lockless XOR verification: a stored PV entry probes
    /// cleanly under its own key, and a DIFFERENT key rejects it (the XOR check still binds
    /// Data to Lock — the PV bit is inside Data, so Data ^ Lock recovers the true key).
    /// </summary>
    [Fact]
    public void TTable_PvFlag_PreservesLocklessVerification()
    {
        var tt = new TTable(1);
        ulong key = 0xDEADBEEFCAFEBABEUL;
        tt.Store(key, value: 42, depth: 4, flag: NodeType.LowerBound, best: default,
                 currentAge: 1, ply: 0, staticEval: TTable.NoStaticEval, pv: true);

        Assert.True(tt.Probe(key, out var hit, ply: 0));
        Assert.True(hit.IsPv);

        // A different key must miss: the XOR check rejects any entry not stored under it.
        Assert.False(tt.Probe(key ^ 0xFF, out _, ply: 0));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Per-ply stacks (ThreadData)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Reset zeroes the new LMR v2 stacks together with the pre-existing ones.</summary>
    [Fact]
    public void ThreadData_Reset_ClearsLmrV2Stacks()
    {
        var td = new ThreadData(0);
        td.PvDistanceStack[3] = 5;
        td.FailHighCountStack[3] = 9;

        td.Reset();

        Assert.Equal(0, td.PvDistanceStack[3]);
        Assert.Equal(0, td.FailHighCountStack[3]);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Behavioral smokes
    // ─────────────────────────────────────────────────────────────────────────────

    private const string StartposFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static (Search search, TTable tt, ThreadData td) CreateSearch(Position pos, Action<Search>? configure = null)
    {
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var signal = new SearchSignal();
        var evaluator = TestHelper.NewEvaluator(pos);
        var search = new Search(tt, td, signal, evaluator);
        configure?.Invoke(search);
        return (search, tt, td);
    }

    /// <summary>
    /// A fixed-depth root search completes with LMR v2 enabled and produces a legal best move
    /// and a non-empty PV. (Tactical correctness of the v2 schedule is validated by the WAC and
    /// SPRT gates per; this smoke only asserts structural integrity.)
    /// </summary>
    [Fact]
    public void LmrV2_SearchCompletes_WithLegalBestMove()
    {
        var pos = Position.FromFEN(StartposFen);
        var (search, _, _) = CreateSearch(pos);
        Move pv = default;
        int score = search.Negamax(pos, depth: 6, alpha: int.MinValue + 1, beta: int.MaxValue - 1,
                                   ref pv, ply: 0);
        Assert.False(pv.IsNone);
        Assert.InRange(score, -30000, 30000);
    }

    /// <summary>
    /// The v2 path actually changes the search: with UseLmrV2 on, the node count at a fixed
    /// depth differs from the legacy (v1) schedule. (Exact counts differ per term configuration;
    /// equality would mean the toggle is dead.)
    /// </summary>
    [Fact]
    public void LmrV2_Toggle_ActuallyChangesSearch()
    {
        long NodesWith(bool useV2, int depth)
        {
            var pos = Position.FromFEN(StartposFen);
            var (search, _, td) = CreateSearch(pos, s => s.UseLmrV2 = useV2);
            Move pv = default;
            search.Negamax(pos, depth, alpha: int.MinValue + 1, beta: int.MaxValue - 1,
                           ref pv, ply: 0);
            return td.Nodes;
        }

        // The v2 base was re-tuned (divisor 3.09 -> 2.50) CLOSER to the v1 base,
        // so at some shallow depths the two schedules can coincidentally yield identical node
        // counts. Proving the toggle is live only requires a difference at SOME depth: scan a
        // small range and assert at least one diverges (equality at every depth would mean the
        // v2 path is dead).
        bool differsSomewhere = false;
        for (int depth = 6; depth <= 9 && !differsSomewhere; depth++)
        {
            if (NodesWith(useV2: false, depth) != NodesWith(useV2: true, depth))
                differsSomewhere = true;
        }
        Assert.True(differsSomewhere,
            "UseLmrV2 produced identical node counts to the legacy schedule at every tested depth");
    }

    /// <summary>
    /// Term bisection: with UseLmrV2 on, flipping any single sub-toggle OFF still yields a
    /// completed search (each term is independently toggleable for regression isolation, and
    /// no sub-toggle is load-bearing for structural integrity).
    /// </summary>
    [Theory]
    [InlineData(nameof(Search.UseLmrTermTtPv))]
    [InlineData(nameof(Search.UseLmrTermCutNode))]
    [InlineData(nameof(Search.UseLmrTermNotImproving))]
    [InlineData(nameof(Search.UseLmrTermPvDistance))]
    [InlineData(nameof(Search.UseLmrTermHistory))]
    [InlineData(nameof(Search.UseLmrTermFutile))]
    [InlineData(nameof(Search.UseLmrTermFailHighCount))]
    [InlineData(nameof(Search.UseLmrTermComplexity))]
    [InlineData(nameof(Search.UseLmrNoisyMoves))]
    public void LmrV2_EachSubToggle_CanBeDisabledIndependently(string toggleName)
    {
        var pos = Position.FromFEN(StartposFen);
        var (search, _, _) = CreateSearch(pos, s =>
            typeof(Search).GetProperty(toggleName)!.SetValue(s, false));
        Move pv = default;
        search.Negamax(pos, depth: 6, alpha: int.MinValue + 1, beta: int.MaxValue - 1,
                       ref pv, ply: 0);
        Assert.False(pv.IsNone);
    }

    /// <summary>
    /// The fail-high-count stack is actually populated by cutoffs (the LMR v2 term (g) signal
    /// exists in real searches) and the root slot stays at zero distance for pvDistance (root is
    /// a full-window node).
    /// </summary>
    [Fact]
    public void LmrV2_Stacks_PopulatedByRealSearch()
    {
        var pos = Position.FromFEN(StartposFen);
        var (search, _, td) = CreateSearch(pos);
        Move pv = default;
        search.Negamax(pos, depth: 8, alpha: int.MinValue + 1, beta: int.MaxValue - 1,
                       ref pv, ply: 0);

        Assert.Equal(0, td.PvDistanceStack[0]); // root: full window -> distance 0
        Assert.Contains(td.FailHighCountStack, c => c > 0); // cutoffs happened somewhere
    }

    /// <summary>
    /// A PV-flagged TT entry is actually produced by a real search: after a full-window search
    /// at the root, the root position's entry carries IsPv (the reference's ttPv propagation).
    /// </summary>
    [Fact]
    public void LmrV2_SearchStoresPvFlagInTt()
    {
        var pos = Position.FromFEN(StartposFen);
        var (search, tt, _) = CreateSearch(pos);
        Move pv = default;
        search.Negamax(pos, depth: 6, alpha: int.MinValue + 1, beta: int.MaxValue - 1,
                       ref pv, ply: 0);

        Assert.True(tt.Probe(pos.ZobristKey, out var entry, ply: 0));
        Assert.True(entry.IsPv, "root full-window search must store the TT PV flag");
    }
}
