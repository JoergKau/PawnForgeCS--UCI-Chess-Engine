using System.IO;
using System.Text;
using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Regression tests for the 2026-11-19 quirk clean-up (decided by the project
/// owner, applied identically to the KnightByte migration target):
///
/// - Bug 151 (was Q9): a present-but-corrupt DTZ file also switched off the
///   healthy WDL table of the same material (one shared error flag).
/// - Q6: the startup banner no longer goes to stdout.
/// - Q7: the first-`isready` warm-up no longer writes into the shared table.
/// - Q8: the single-legal-move instant answer serves time-managed searches only.
///
/// Q5 (a stopped ponder search answers its best move, not `bestmove 0000`) is
/// pinned in <see cref="PonderUciTests"/>; the timed/untimed Q8 split at the
/// search level is pinned in SearchSanityTests.
/// </summary>
[Collection("syzygy-tb")]
public class PredecessorQuirkFixesTests : IDisposable
{
    private const string KqvkFen = "8/8/8/8/8/1Q6/8/K6k w - - 0 1";
    private const string ForcedMoveFen = "8/8/8/8/8/1k6/r6P/K7 w - - 0 1"; // only Kb1 is legal

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        Syzygy.Release();
        Syzygy.ProbingEnabled = true;
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Bug 151 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a table directory from the committed KQvK pair. The corrupt
    /// variants zero the file magic but keep the size, so the loader's
    /// size check passes and the failure happens at mapping time - the exact
    /// "present but unreadable" case.
    /// </summary>
    private string? MakeTableDir(bool wdl, bool dtz, bool corruptWdl = false, bool corruptDtz = false)
    {
        string? src = TestHelper.FindTbDir();
        if (src == null) return null;

        string dir = Path.Combine(Path.GetTempPath(), "pf-bug151-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        void Copy(string name, bool corrupt)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(src, name));
            if (corrupt) { bytes[0] = bytes[1] = bytes[2] = bytes[3] = 0; }
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        }

        if (wdl) Copy("KQvK.rtbw", corruptWdl);
        if (dtz) Copy("KQvK.rtbz", corruptDtz);
        return dir;
    }

    [Fact]
    public void Bug151_CorruptDtz_DoesNotDisableWdl()
    {
        string? dir = MakeTableDir(wdl: true, dtz: true, corruptDtz: true);
        if (dir == null) return;
        Syzygy.Initialize(dir);
        var pos = Position.FromFEN(KqvkFen);

        // Probe DTZ FIRST: that is the order that used to poison the material.
        Assert.Equal(int.MinValue, Syzygy.ProbeDtz(pos));
        Assert.Equal(2, Syzygy.ProbeWdl(pos));
    }

    [Fact]
    public void Bug151_AbsentDtz_WdlStillWorks()
    {
        string? dir = MakeTableDir(wdl: true, dtz: false);
        if (dir == null) return;
        Syzygy.Initialize(dir);
        var pos = Position.FromFEN(KqvkFen);

        Assert.Equal(int.MinValue, Syzygy.ProbeDtz(pos));
        Assert.Equal(2, Syzygy.ProbeWdl(pos));
    }

    [Fact]
    public void Bug151_CorruptWdl_StillFailsWdl()
    {
        string? dir = MakeTableDir(wdl: true, dtz: true, corruptWdl: true);
        if (dir == null) return;
        Syzygy.Initialize(dir);
        var pos = Position.FromFEN(KqvkFen);

        Assert.Equal(int.MinValue, Syzygy.ProbeWdl(pos));
    }

    [Fact]
    public void Bug151_Search_KeepsTheExactTablebaseScore_WithACorruptDtzFile()
    {
        string? dir = MakeTableDir(wdl: true, dtz: true, corruptDtz: true);
        if (dir == null) return;
        Syzygy.Initialize(dir);

        var pos = Position.FromFEN(KqvkFen);
        var info = new List<string>();
        var search = new Search(new TTable(4), new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: line => info.Add(line));
        search.IterativeDeepening(pos, 4);

        // Root DTZ ranking cannot run (its table is corrupt), but the leaf WDL
        // probe must still return the exact win - before the fix the search
        // reported an ordinary evaluation (cp 783).
        Assert.Contains(info, l => l.Contains("score cp 29900") || l.Contains("score mate"));
    }

    // ── Q6 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Q6_TheBannerIsNeverWrittenToStdout()
    {
        var output = new StringWriter();
        new UciHandler().Run(new StringReader("uci\nquit\n"), output);

        string text = output.ToString();
        Assert.StartsWith("id name", text.TrimStart());
        Assert.DoesNotContain("*****", text);
    }

    // ── Q7 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Q7_WarmUp_LeavesTheSharedTranspositionTableUntouched()
    {
        // The warm-up search has a 10 ms budget. In a cold process the JIT eats
        // all of it and nothing is stored, so first JIT-warm the engine on a
        // PRIVATE table: the warm-up then completes iterations, and before the
        // fix it left the start position and its children in the shared table.
        var start = Position.StartingPosition();
        var preWarm = new Search(new TTable(16), new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(start), getTotalNodes: null, onInfo: null);
        preWarm.IterativeDeepening(start, 8);

        var orchestrator = new EngineOrchestrator();
        TTable.Shared.Clear();

        orchestrator.WarmUp(new StringWriter());

        Span<Move> moves = stackalloc Move[256];
        int n = start.GenerateMoves(moves);
        int found = TTable.Shared.Probe(start.ZobristKey, out _, 0, start) ? 1 : 0;
        for (int i = 0; i < n; i++)
        {
            var child = Position.StartingPosition();
            child.MakePublicMove(moves[i]);
            if (TTable.Shared.Probe(child.ZobristKey, out _, 1, child)) found++;
        }
        Assert.Equal(0, found); // the warm-up must not write into the shared table
    }

    // ── Q8 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Q8_GoInfinite_OnAForcedMove_WaitsForStop()
    {
        var output = new LockedTextWriter();
        var orchestrator = new EngineOrchestrator();
        try
        {
            orchestrator.SetPosition(ForcedMoveFen, Array.Empty<string>(), output);
            orchestrator.Go(UciParser.ParseGoCommand("go infinite"), output);

            Thread.Sleep(400);
            Assert.DoesNotContain("bestmove", output.Text); // UCI: no bestmove until `stop`

            orchestrator.Stop();
            Assert.Contains("bestmove a1b1", output.Text);
        }
        finally
        {
            orchestrator.Stop();
        }
    }

    [Fact]
    public void Q8_GoDepth_OnAForcedMove_ReportsItsIterations()
    {
        var output = new LockedTextWriter();
        var orchestrator = new EngineOrchestrator();
        try
        {
            orchestrator.SetPosition(ForcedMoveFen, Array.Empty<string>(), output);
            orchestrator.Go(UciParser.ParseGoCommand("go depth 4"), output);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!output.Text.Contains("bestmove") && sw.ElapsedMilliseconds < 15_000)
                Thread.Sleep(20);

            Assert.Contains("info depth 1 ", output.Text);
            Assert.DoesNotContain("info depth 0 ", output.Text);
            Assert.Contains("bestmove a1b1", output.Text);
        }
        finally
        {
            orchestrator.Stop();
        }
    }

    [Fact]
    public void Q8_TimedSearch_OnAForcedMove_StillAnswersAtOnce()
    {
        var output = new LockedTextWriter();
        var orchestrator = new EngineOrchestrator();
        try
        {
            orchestrator.SetPosition(ForcedMoveFen, Array.Empty<string>(), output);
            orchestrator.Go(UciParser.ParseGoCommand("go wtime 60000 btime 60000"), output);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!output.Text.Contains("bestmove") && sw.ElapsedMilliseconds < 5_000)
                Thread.Sleep(10);

            Assert.Contains("info depth 0 ", output.Text); // the instant-answer line, kept for timed play
            Assert.Contains("bestmove a1b1", output.Text);
        }
        finally
        {
            orchestrator.Stop();
        }
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
