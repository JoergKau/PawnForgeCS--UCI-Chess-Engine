using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// P0 search instrumentation: the counters must
/// be invisible when off (same fixed-depth tree) and must actually record
/// qsearch nodes, TT probes and cutoff indices when on.
/// </summary>
public class SearchStatsTests
{
    private const string MidgameFen =
        "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";

    private static Search NewSearch(Position pos, ThreadData td)
        => new(new TTable(4), td, new SearchSignal(), TestHelper.NewEvaluator(pos),
               getTotalNodes: null, onInfo: null);

    [Fact]
    public void StatsAggregation_Works()
    {
        var a = new SearchStats();
        var b = new SearchStats();
        a.RecordQNode();
        a.RecordTtProbe(true);
        a.RecordCutoff(0);
        b.RecordQTtProbe(true);
        b.RecordCutoff(1);

        a.Add(b);
        Assert.Equal(1, a.QNodes);
        Assert.Equal(1, a.TtProbes);
        Assert.Equal(1, a.TtHits);
        Assert.Equal(1, a.QTtProbes);
        Assert.Equal(1, a.QTtHits);
        Assert.Equal(2, a.Cutoffs);
        Assert.Equal(1, a.CutoffIndex[0]);
        Assert.Equal(1, a.CutoffIndex[1]);

        a.Reset();
        Assert.Equal(0, a.Cutoffs);
        Assert.Equal(0, a.CutoffIndex[0]);
    }

    [Fact]
    public void DiagnosticsOff_LeavesTheTreeIdentical_AndOn_Counts()
    {
        var posOff = Position.FromFEN(MidgameFen);
        var tdOff = new ThreadData(0);
        SearchDiagnostics.Enabled = false;
        NewSearch(posOff, tdOff).IterativeDeepening(posOff, 10);

        var posOn = Position.FromFEN(MidgameFen);
        var tdOn = new ThreadData(0);
        var previous = SearchDiagnostics.Enabled;
        SearchDiagnostics.Enabled = true;
        try
        {
            NewSearch(posOn, tdOn).IterativeDeepening(posOn, 10);
        }
        finally
        {
            SearchDiagnostics.Enabled = previous;
        }

        // Counting must not change the search tree one node.
        Assert.Equal(tdOff.Nodes, tdOn.Nodes);

        // And the counters must actually have observed a search.
        Assert.True(tdOn.Stats.QNodes > 0, "qsearch nodes were not counted");
        Assert.True(tdOn.Stats.TtProbes > 0, "TT probes were not counted");
        Assert.True(tdOn.Stats.Cutoffs > 0, "beta cutoffs were not counted");
        Assert.True(tdOn.Stats.CutoffIndex[0] > 0,
            "no first-move cutoff recorded — the index is probably off by one");
    }
}
