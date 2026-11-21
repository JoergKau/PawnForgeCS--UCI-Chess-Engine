using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Tests for singular double/triple extensions (design review, ). The magnitude
/// logic lives in <c>Search.ComputeSingularExtension</c>: 0 when not singular, 1 when singular,
/// and 2 or 3 when the exclusion-search score falls a growing margin below singularBeta. The
/// toggle is <c>UseSingularDoubleExtensions</c>; disabling it reproduces the single-ply (+1)
/// behavior.
/// </summary>
public class SingularExtensionTests
{
    private static (Search search, TTable tt, ThreadData td) CreateSearch(Position pos)
    {
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var search = new Search(tt, td, new SearchSignal(), TestHelper.NewEvaluator(pos));
        return (search, tt, td);
    }

    // Fixed reference values for the table (singularBeta = 100, doubleMargin = 30, tripleMargin = 120).
    // Thresholds: score >= 100 -> 0; [70,99] -> 1; [-20,69] -> 2; < -20 -> 3.
    [Theory]
    // doubleEnabled = true
    [InlineData(100, true, 0)]   // not singular (score >= singularBeta)
    [InlineData(150, true, 0)]   // not singular
    [InlineData(99, true, 1)]    // singular, above the double margin
    [InlineData(70, true, 1)]    // exactly at singularBeta - doubleMargin -> still 1
    [InlineData(69, true, 2)]    // below double margin -> 2
    [InlineData(-20, true, 2)]   // exactly at singularBeta - tripleMargin -> still 2
    [InlineData(-21, true, 3)]   // below triple margin -> 3
    // doubleEnabled = false -> only ever 0 or 1
    [InlineData(69, false, 1)]   // singular -> 1 regardless of how far below
    [InlineData(-500, false, 1)] // still just 1 when doubles disabled
    [InlineData(100, false, 0)]  // not singular -> 0
    public void ComputeSingularExtension_MatchesTable(int score, bool doubleEnabled, int expected)
    {
        Assert.Equal(expected,
            Search.ComputeSingularExtension(score, singularBeta: 100, doubleEnabled, doubleMargin: 30, tripleMargin: 120));
    }

    /// <summary>
    /// A singular move never gets a NEGATIVE or huge extension: the result is always within
    /// [0, 3], for a range of scores and both toggle states.
    /// </summary>
    [Fact]
    public void ComputeSingularExtension_AlwaysWithinZeroToThree()
    {
        for (int score = -1000; score <= 1000; score += 7)
        {
            foreach (bool en in new[] { true, false })
            {
                int e = Search.ComputeSingularExtension(score, 100, en, 30, 120);
                Assert.InRange(e, 0, 3);
            }
        }
    }

    /// <summary>
    /// Behavioral smoke: a search on a tactical midgame position completes and returns a legal
    /// move with double/triple extensions enabled AND disabled - the extra extension never
    /// breaks move selection.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Search_Completes_WithAndWithoutDoubleExtensions(bool enabled)
    {
        var pos = Position.FromFEN("r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9");
        var (search, _, _) = CreateSearch(pos);
        search.UseSingularExtensions = true;
        search.UseSingularDoubleExtensions = enabled;

        Move best = search.IterativeDeepening(pos, maxDepth: 10);

        Assert.False(best.IsNone);
    }
}
