using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for the table-based Late Move Reduction schedule (search-depth work, ):
/// the precomputed <c>reduction[depth][moveNumber]</c> base table exposed via
/// <c>Search.LmrBaseReduction</c>, plus a behavioral smoke that the table path and the legacy
/// formula path both search cleanly. The base amount grows with the logarithm of both depth and
/// move index; callers add improving / PV / history / cut-node adjustments and clamp on top.
/// </summary>
public class LmrTableTests
{
    private static (Search search, TTable tt, ThreadData td) CreateSearch(Position pos)
    {
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var signal = new SearchSignal();
        var evaluator = TestHelper.NewEvaluator(pos);
        var search = new Search(tt, td, signal, evaluator);
        return (search, tt, td);
    }

    /// <summary>
    /// The base reduction is zero whenever either index is zero (no reduction at depth 0 or for
    /// the very first move), and known spot values follow the documented
    /// <c>1 + ln(depth)*ln(moveNumber)/1.75</c> formula (integer-truncated).
    /// </summary>
    [Theory]
    [InlineData(0, 10, 0)]  // depth 0  -> 0
    [InlineData(10, 0, 0)]  // move 0   -> 0
    [InlineData(3, 4, 1)]
    [InlineData(6, 4, 2)]
    [InlineData(10, 10, 4)]
    [InlineData(20, 16, 5)]
    public void LmrBaseReduction_MatchesFormula(int depth, int moveNumber, int expected)
    {
        Assert.Equal(expected, Search.LmrBaseReduction(depth, moveNumber));
    }

    /// <summary>
    /// The table is monotonic non-decreasing in BOTH arguments: searching later moves or at
    /// greater remaining depth never reduces less than an earlier/shallower entry.
    /// </summary>
    [Fact]
    public void LmrBaseReduction_IsMonotonicInDepthAndMoveNumber()
    {
        for (int d = 1; d <= 63; d++)
        {
            for (int m = 1; m <= 63; m++)
            {
                Assert.True(Search.LmrBaseReduction(d, m) >= Search.LmrBaseReduction(d - 1, m),
                    $"not monotonic in depth at d={d}, m={m}");
                Assert.True(Search.LmrBaseReduction(d, m) >= Search.LmrBaseReduction(d, m - 1),
                    $"not monotonic in moveNumber at d={d}, m={m}");
            }
        }
    }

    /// <summary>
    /// Indices beyond the table are clamped, not out-of-range: very large inputs return the same
    /// value as the maximum in-range entry.
    /// </summary>
    [Fact]
    public void LmrBaseReduction_ClampsLargeIndices()
    {
        int maxEntry = Search.LmrBaseReduction(63, 63);
        Assert.Equal(maxEntry, Search.LmrBaseReduction(1000, 1000));
        Assert.Equal(maxEntry, Search.LmrBaseReduction(64, 64));
    }

    /// <summary>
    /// Behavioral smoke: a fixed-depth root search on a normal midgame position completes and
    /// returns a positive node count with the table-based schedule enabled AND with it disabled
    /// (legacy formula) - neither path breaks move selection.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LmrTable_SearchCompletes_BothPaths(bool useTable)
    {
        var pos = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4");
        var (search, _, td) = CreateSearch(pos);
        search.UseLmrTable = useTable;

        int score = search.Negamax(pos, depth: 8, alpha: -30000, beta: 30000);

        Assert.True(td.Nodes > 1);
        Assert.InRange(score, -30000, 30000);
    }
}
