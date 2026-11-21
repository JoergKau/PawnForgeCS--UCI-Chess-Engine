using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for the PVS re-search depth shave (search-depth work, ): the
/// <c>ComputeReSearchDepth</c> arithmetic that decides the depth of the full-depth
/// zero-window re-search after a reduced-search fail-high, plus a behavioral smoke test
/// that a full search still completes with the shave enabled.
/// </summary>
public class PvsResearchDepthShaveTests
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
    /// **Validates: Requirements 7.1, 7.2, 7.3, 7.4**
    ///
    /// The re-search depth arithmetic follows the documented table:
    /// - reduction &lt; 3: no shave (returns newDepth).
    /// - reduction &gt;= 3: shave one ply.
    /// - reduction &gt;= 5 AND newDepth &gt; 2: shave a second ply.
    /// - The result is floored at (newDepth - reduction) so the re-search is never shallower
    /// than the reduced search it improves on.
    /// - When disabled, always returns exactly newDepth (toggle-off exact fallback, ).
    ///
    /// Every enabled case also asserts the floor invariant `result &gt;= newDepth - reduction`.
    /// </summary>
    [Theory]
    // Enabled = true
    [InlineData(10, 0, true, 10)]  // reduction < 3 -> no shave
    [InlineData(10, 2, true, 10)]  // reduction < 3 -> no shave
    [InlineData(10, 3, true, 9)]   // one shave (reduction >= 3)
    [InlineData(10, 4, true, 9)]   // one shave
    [InlineData(10, 5, true, 8)]   // two shaves (reduction >= 3 AND >= 5, newDepth > 2)
    [InlineData(10, 6, true, 8)]   // two shaves
    [InlineData(2, 5, true, 1)]    // reduction >= 3 -> 1; second shave gated off (newDepth not > 2); floor = -3 -> 1
    [InlineData(1, 3, true, 0)]    // reduction >= 3 -> 0; floor = -2 -> max(0, -2) = 0 (depth 0 drops to qsearch)
    // Enabled = false (exact fallback)
    [InlineData(10, 5, false, 10)]
    [InlineData(2, 5, false, 2)]
    public void ComputeReSearchDepth_MatchesDocumentedTable(int newDepth, int reduction, bool enabled, int expected)
    {
        int result = Search.ComputeReSearchDepth(newDepth, reduction, enabled);

        Assert.Equal(expected, result);

        // Floor invariant (): only meaningful when the shave is enabled - the shave
        // must never re-search shallower than the reduced search it is trying to improve on.
        if (enabled)
        {
            Assert.True(result >= newDepth - reduction,
                $"reSearchDepth {result} dropped below floor {newDepth - reduction} " +
                $"(newDepth={newDepth}, reduction={reduction}).");
        }
    }

    /// <summary>
    /// **Validates: Requirements 7.1**
    ///
    /// Behavioral smoke check: a normal midgame position searched with
    /// <c>UseAspirationReSearchReduction = true</c> still completes normally - the shave never
    /// breaks the PVS ladder or move selection, returning a legal non-None best move and a
    /// positive node count.
    /// </summary>
    [Fact]
    public void ReSearchShave_Enabled_SearchCompletesNormally()
    {
        // Normal full-board midgame position (Italian Game-ish) with plenty of late quiet
        // moves, so LMR reductions and the reduced-search re-search ladder are exercised.
        var pos = Position.FromFEN("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4");

        var (search, _, td) = CreateSearch(pos);
        search.UseAspirationReSearchReduction = true;

        Move best = search.IterativeDeepening(pos, maxDepth: 5);

        Assert.False(best.IsNone);
        Assert.True(td.Nodes > 0);
    }
}
