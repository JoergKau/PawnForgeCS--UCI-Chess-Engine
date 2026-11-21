using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// UCI-output compliance for the degenerate-clock path.
///
/// Background: fastchess's compliance probe sends `go wtime 100` with no
/// `btime`, so the time manager computes a 12 ms hard limit for a 100 ms clock. If the
/// search thread is scheduled late (a busy host, a cold process), the hard limit is
/// already expired when the first node poll runs. The pre-fix search then broke out of
/// iterative deepening before depth 1 and emitted `bestmove` with NO preceding
/// `info depth...` line - exactly what the compliance probe reported as
/// "engine answered `go wtime 100` with `bestmove a2a3`".
///
/// These tests pin the invariant that matters to GUIs and to the compliance probe:
/// on thread 0, every search that returns a real move first emits an `info` line whose
/// PV starts with that move (the same invariant the normal exit path already keeps).
/// </summary>
public class SearchTimeComplianceTests
{
    /// <summary>Start position - no fixture arithmetic, so no the oracle-verification ambiguity.</summary>
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>
    /// Reproduces the reported timing deterministically: initialize the clock as if the
    /// thread had been descheduled for 50 ms between `Init` and the first search node.
    /// The 100 ms clock's hard limit (12 ms) is then already in the past, so depth 1
    /// cannot complete. The engine must still report an info line before `bestmove`.
    /// </summary>
    [Fact]
    public void ExpiredHardLimit_StillEmitsInfoLineBeforeBestmove()
    {
        var pos = Position.FromFEN(StartFen);
        var infos = new List<string>();
        var search = new Search(new TTable(4), new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: infos.Add);

        // wtime = 100, overhead = 50 -> hard limit 12 ms; start 50 ms in the past.
        search.TimeManager.Init(100, 100, 0, 0, movesToGo: 0, pos.SideToMove,
            TimeManager.NowMs() - 50, moveOverhead: 50);

        Move best = search.IterativeDeepening(pos, maxDepth: 64);

        Assert.False(best.IsNone);
        string? info = infos.LastOrDefault(l => l.StartsWith("info depth", StringComparison.Ordinal));
        Assert.NotNull(info);
        Assert.Contains($" pv {best}", info);
    }

    /// <summary>
    /// The abort path a piped `go`/`quit` exercises: `quit` stops the signal
    /// before the first iteration, so the search returns the first-legal
    /// fallback move (a2a3 on startpos). That move is expected; what must NOT
    /// happen is a bare `bestmove`. The same depth-0 line must precede it.
    /// </summary>
    [Fact]
    public void AlreadyStoppedSearch_StillEmitsInfoLineBeforeBestmove()
    {
        var pos = Position.FromFEN(StartFen);
        var infos = new List<string>();
        var signal = new SearchSignal();
        signal.Stop(); // exactly what `quit`/EOF does to a running search

        var search = new Search(new TTable(4), new ThreadData(0), signal,
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: infos.Add);
        search.TimeManager.SetInfinite();

        Move best = search.IterativeDeepening(pos, maxDepth: 64);

        Assert.False(best.IsNone);
        string? info = infos.LastOrDefault(l => l.StartsWith("info depth", StringComparison.Ordinal));
        Assert.NotNull(info);
        Assert.Contains($" pv {best}", info);
    }

    /// <summary>
    /// The same invariant for the normal path, at a generous clock: at least one
    /// `info depth...` line must precede the returned move (this is the behaviour
    /// every GUI already relies on; it also proves the fallback emitter does not
    /// duplicate or replace a real iteration's line).
    /// </summary>
    [Fact]
    public void NormalSearch_EmitsInfoLinesWithTheReturnedMove()
    {
        var pos = Position.FromFEN(StartFen);
        var infos = new List<string>();
        var search = new Search(new TTable(4), new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: infos.Add);

        search.TimeManager.Init(1000, 1000, 0, 0, movesToGo: 0, pos.SideToMove,
            TimeManager.NowMs(), moveOverhead: 50);

        Move best = search.IterativeDeepening(pos, maxDepth: 6);

        Assert.False(best.IsNone);
        Assert.Contains(infos, l => l.StartsWith("info depth", StringComparison.Ordinal));
        string last = infos.Last(l => l.StartsWith("info depth", StringComparison.Ordinal));
        Assert.Contains($" pv {best}", last);
    }
}
