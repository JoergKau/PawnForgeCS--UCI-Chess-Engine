using System.Text;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Syzygy integration into search and the UCI layer: the leaf WDL probe
/// inside Search/QuiescenceSearch and the root-probe bestmove from `go`.
/// Table-dependent tests no-op when the tbdata tables are absent.
/// </summary>
[Collection("syzygy-tb")]
public class SyzygySearchTests : IDisposable
{
    private static readonly string? TbDir = TestHelper.FindTbDir();

    public SyzygySearchTests()
    {
        if (TbDir != null)
            Syzygy.Initialize(TbDir);
    }

    public void Dispose()
    {
        Syzygy.Release();
    }

    [Fact]
    public void Search_LeafProbe_ScoresTablebaseWin()
    {
        if (TbDir == null) return;
        // KQ vs K: the search must score the position as a clear win at the
        // leaves (leaf WDL probe returns the TB score 29900) and pick a
        // winning move. The orchestrator's root probe is NOT involved here.
        var pos = Position.FromFEN("8/8/8/8/8/1Q6/8/K6k w - - 0 1");
        var info = new List<string>();
        var tt = new TTable(4);
        var search = new Search(tt, new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: line => info.Add(line));

        Move best = search.IterativeDeepening(pos, 4);

        Assert.False(best.IsNone);
        // W-16: the tablebase win must never be masked by an ordinary evaluation score.
        // Before W-16 that was always the literal flat 29900. Now a win reached by the
        // search is announced as a genuine mate (`score mate N`), and a win the search
        // cannot yet reach is graded by the tablebase distance-to-zeroing, so it sits
        // somewhere in [TbWinScoreMin, TbWinScore]. All three are correct; an eval-sized
        // score is not. This asserts the invariant, not the old magic number.
        Assert.Contains(info, l => ReportsTablebaseWin(l));
        // The chosen move must be legal.
        Span<Move> legal = stackalloc Move[256];
        int n = pos.GenerateMoves(legal);
        Assert.Contains(best, legal[..n].ToArray());
    }

    /// <summary>
    /// True when an <c>info</c> line reports either a real mate or a cp score inside the
    /// tablebase-win band (see <see cref="Syzygy.TbWinScoreMin"/>).
    /// </summary>
    private static bool ReportsTablebaseWin(string infoLine)
    {
        if (infoLine.Contains("score mate")) return true;
        int i = infoLine.IndexOf("score cp ", StringComparison.Ordinal);
        if (i < 0) return false;
        string tail = infoLine[(i + "score cp ".Length)..];
        int end = 0;
        while (end < tail.Length && (char.IsAsciiDigit(tail[end]) || (end == 0 && tail[end] == '-')))
            end++;
        return int.TryParse(tail[..end], out int cp) && Math.Abs(cp) >= Syzygy.TbWinScoreMin;
    }

    [Fact]
    public void Search_AnnouncesMateInsideTablebase()
    {
        if (TbDir == null) return;
        // W-16 regression. KQ vs K with Qg7# available: the
        // position is 3 pieces and therefore inside the tables, and the mate is on the
        // board right now. Before W-16 the quiescence WDL probe fired BEFORE checkmate
        // detection and returned the flat WdlToScore(-2) = -29900, so the engine reported
        // `score cp 29900` and never announced the mate at all — 0/60 on the short bucket
        // of the endgame gate. It must now announce the mate.
        //
        // NOTE this test enables AllowSyzygyCutoffOnPvNodes, the measured W-16 ablation,
        // which is OFF by default. W-16 was measured and REJECTED as a default change:
        // announcing a mate requires the search to find it, i.e. to decline the tablebase's
        // exact answer, and the tablebase picks the right move better than our search does
        // (endgame suite: short solved 60/60 -> 49/60 to buy 0% -> 21.7% announcement).
        // The default therefore keeps the exact value. This test pins the mechanism — that
        // a mated node reports a mate instead of a flat -29900 — not the default policy.
        //
        // FIXTURE (2026-10-08): the position was "7k/5KQ1/8/8/8/8/8/8 w", which is ILLEGAL —
        // White's queen on g7 attacks Black's king on h8 while it is WHITE's move, so the
        // side not to move is in check. the oracle rejects it outright:
        // CRITICAL ERROR... Unsupported position. King can be captured.
        // It only ever passed because the HCE evaluator indexed nothing by king square and
        // so tolerated an impossible position; the NNUE arm cannot (it derives the feature
        // region from the POV king square), and the search reached a king-less position
        // through a capture in quiescence. This was therefore a latent defect in the fixture
        // that the HCE arm was hiding — and it violated RULE HARDENING
        // ("test FENs must be the oracle-verified AT TEST TIME"), which this very gate exists
        // to enforce. Replaced with a legal KQvK mate in one.
        //
        // Verified at 2026-10-08 against the oracle 19: accepted, `score mate 1`, bestmove
        // d1a4 (Qa4#) — the same mate python-chess proves. Still exactly 3 pieces, so the
        // 3-man tables still cover it and the WDL probe still fires.
        var pos = Position.FromFEN("8/8/8/8/8/8/8/k1KQ4 w - - 0 1");
        var info = new List<string>();
        var tt = new TTable(4);
        var search = new Search(tt, new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: line => info.Add(line))
        { AllowSyzygyCutoffOnPvNodes = false };

        search.IterativeDeepening(pos, 8);

        Assert.Contains(info, l => l.Contains("score mate 1"));
        Assert.DoesNotContain(info, l => ReportsTablebaseWin(l) && !l.Contains("score mate"));
    }

    [Fact]
    public void Search_WithoutSyzygy_NoTablebaseScores()
    {
        // With no tables loaded the same position must be scored normally.
        Syzygy.Release();
        var pos = Position.FromFEN("8/8/8/8/8/1Q6/8/K6k w - - 0 1");
        var info = new List<string>();
        var tt = new TTable(4);
        var search = new Search(tt, new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: line => info.Add(line));

        search.IterativeDeepening(pos, 4);

        Assert.DoesNotContain(info, l => l.Contains("score cp 299"));
    }
}

/// <summary>UCI-level integration of the Syzygy options and the root probe.</summary>
[Collection("syzygy-tb")]
public class SyzygyUciTests : IDisposable
{
    private readonly EngineOrchestrator _orchestrator = new();
    private readonly LockedTextWriter _out = new();

    public SyzygyUciTests()
    {
        var dir = TestHelper.FindTbDir();
        if (dir != null)
            _orchestrator.SetOption("SyzygyPath", dir, _out);
    }

    public void Dispose()
    {
        _orchestrator.Stop();
    }

    private static string WaitFor(Func<string> text, string needle, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string t;
        do
        {
            Thread.Sleep(20);
            t = text();
        } while (!t.Contains(needle, StringComparison.Ordinal) && sw.ElapsedMilliseconds < timeoutMs);
        return t;
    }

    [Fact]
    public void SyzygyPathOption_LoadsTables()
    {
        var dir = TestHelper.FindTbDir();
        if (dir == null) return;
        Assert.Contains("syzygy tablebases", _orchestrator.SyzygyStatus);
        Assert.DoesNotContain("none", _orchestrator.SyzygyStatus);
    }

    [Fact]
    public void Go_TablebaseRootProbe_PlaysOptimalMove()
    {
        string? tbDir = TestHelper.FindTbDir();
        if (tbDir == null) return;
        // The DTZ root probe (direct play) is behind the SyzygyRootProbe option and
        // DEFAULT OFF (F4 fix: blind DTZ play bypasses the search —
        // no repetition awareness, DTZ-optimal ≠ fastest mate). Enable it explicitly.
        _orchestrator.SetOption("SyzygyPath", tbDir, _out);
        _orchestrator.SetOption("SyzygyRootProbe", "true", _out);
        _orchestrator.SetPosition("8/8/8/8/8/1Q6/8/K6k w - - 0 1", Array.Empty<string>(), _out);
        _orchestrator.Go(UciParser.ParseGoCommand("go movetime 300"), _out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.Contains("tablebase move", text);
        Assert.Contains("bestmove", text);
    }

    [Fact]
    public void Go_TablebaseRootProbe_DefaultOff_SearchRuns()
    {
        string? tbDir = TestHelper.FindTbDir();
        if (tbDir == null) return;
        // Default OFF: with tables loaded, the root must be SEARCHED (with WDL leaf
        // probing), NOT played from the DTZ probe — the search must emit info lines
        // and never the "tablebase move" line.
        _orchestrator.SetOption("SyzygyPath", tbDir, _out);
        _orchestrator.SetPosition("8/8/8/8/8/1Q6/8/K6k w - - 0 1", Array.Empty<string>(), _out);
        _orchestrator.Go(UciParser.ParseGoCommand("go movetime 300"), _out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.DoesNotContain("tablebase move", text);
        Assert.Contains("info depth", text);
        Assert.Contains("bestmove", text);
    }

    [Fact]
    public void Go_TablebaseCheckmate_EmitsBestmove0000()
    {
        if (TestHelper.FindTbDir() == null) return;
        _orchestrator.SetPosition("k7/1Q6/2K5/8/8/8/8/8 b - - 0 1", Array.Empty<string>(), _out);
        _orchestrator.Go(UciParser.ParseGoCommand("go movetime 300"), _out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.Contains("bestmove 0000", text);
    }

    private sealed class LockedTextWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get { lock (_gate) return _sb.ToString(); }
        }

        public override void Write(char value) { lock (_gate) _sb.Append(value); }
        public override void Write(string? value) { lock (_gate) _sb.Append(value); }
        public override void WriteLine(string? value) { lock (_gate) _sb.AppendLine(value); }
        public override void WriteLine() { lock (_gate) _sb.AppendLine(); }
    }
}
