using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Regression tests for the isPvNode integer overflow that disabled
/// all PV-node protections and hid short forced mates.
/// The root must find the mate-in-1 even though Ra8+ is a LATE quiet move
/// (the old bug let LMP prune it at the root).
/// </summary>
public class TraceTests
{
    [Fact]
    public void M1_RookEndgame_MateFoundAtDepth2()
    {
        // Oracle-validated: mate 1 via a1a8 (unique; Rb8+ is NOT mate).
        var pos = Position.FromFEN("6k1/1R6/8/8/8/8/8/R6K w - - 1 1");
        Stockfish.AssertMateIn("6k1/1R6/8/8/8/8/8/R6K w - - 1 1", 1);

        var tt = new TTable(16);
        var search = new Search(tt, new ThreadData(0), new SearchSignal(), TestHelper.NewEvaluator(pos));
        Move best = search.IterativeDeepening(pos, 2);

        Assert.Equal(TestHelper.FindMove(pos, "a1", "a8"), best);
    }
}
