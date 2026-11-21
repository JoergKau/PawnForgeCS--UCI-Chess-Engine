using System.Text;
using PawnForge.Core.Board;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Ponder support (`go ponder` / `ponderhit`) at the UCI layer: parsing,
/// ponder-mode bestmove suppression, the ponderhit transition to a normal
/// search on the advanced position, stop-during-ponder, and the no-op guard.
/// The orchestrator runs real (background) searches, so these tests assert on
/// the emitted protocol stream rather than on internal state.
/// </summary>
public class PonderUciTests : IDisposable
{
    private readonly EngineOrchestrator _orchestrator = new();
    private readonly LockedTextWriter _out = new();

    public void Dispose()
    {
        // Always stop any still-running search so no background thread leaks.
        _orchestrator.Stop();
    }

    private static SearchLimits PonderLimits(string go)
        => UciParser.ParseGoCommand(go);

    /// <summary>Polls the output stream until a substring appears or a timeout elapses.</summary>
    private static string WaitFor(Func<string> text, string needle, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string t;
        do
        {
            Thread.Sleep(25);
            t = text();
        } while (!t.Contains(needle, StringComparison.Ordinal) && sw.ElapsedMilliseconds < timeoutMs);
        return t;
    }

    [Fact]
    public void GoPonder_ParsesPonderFlag()
    {
        // `go ponder` must be parsed into SearchLimits.Ponder and carry the
        // clocks that the post-ponderhit normal search reuses.
        var limits = PonderLimits("go ponder wtime 60000 btime 30000 winc 100 binc 50");

        Assert.True(limits.Ponder);
        Assert.Equal(60_000, limits.WhiteTime);
        Assert.Equal(30_000, limits.BlackTime);
        Assert.Equal(100, limits.WhiteIncrement);
        Assert.Equal(50, limits.BlackIncrement);
    }

    [Fact]
    public void Go_WithoutPonder_IsNotPonder()
    {
        var limits = PonderLimits("go movetime 1000");
        Assert.False(limits.Ponder);
    }

    [Fact]
    public void PonderSearch_DoesNotEmitBestmove_UntilPonderHit()
    {
        // Real GUI flow: engine played e2e4, GUI sends the position (black to
        // move) + `go ponder`. The engine must search but never volunteer a
        // bestmove while the opponent is still thinking.
        _orchestrator.SetPosition("startpos", new[] { "e2e4" }, _out);
        _orchestrator.Go(PonderLimits("go ponder movetime 500"), _out);

        // Wait until the ponder search is demonstrably running (info lines
        // appear) before asserting it never volunteers a bestmove.
        string text = WaitFor(() => _out.Text, "info depth");

        Assert.DoesNotContain("bestmove", text);
    }

    [Fact]
    public void PonderHit_EmitsExactlyOneBestmove_FromAdvancedPosition()
    {
        // After `ponderhit` the engine makes its pondered (opponent) move and
        // continues as a NORMAL search; that search must emit exactly one
        // bestmove (the ponder search itself stays silent).
        _orchestrator.SetPosition("startpos", new[] { "e2e4" }, _out);
        _orchestrator.Go(PonderLimits("go ponder movetime 500"), _out);

        Thread.Sleep(500); // ponder search establishes its expected move
        _orchestrator.PonderHit(_out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.Contains("bestmove", text);

        // Exactly one bestmove line, and it is a real move (not "0000"): the
        // engine is answering from the position advanced by the pondered move.
        var bestMoves = text
            .Split('\n')
            .Where(l => l.StartsWith("bestmove"))
            .ToList();
        Assert.Single(bestMoves);
        var tokens = bestMoves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, tokens.Length);
        Assert.NotEqual("0000", tokens[1]);
    }

    [Fact]
    public void StopDuringPonder_EmitsTheBestMoveSoFar()
    {
        // The opponent deviated (or the GUI aborted the ponder): `stop` while
        // pondering is answered like any stopped search - with the best move of
        // the last completed iteration (quirk Q5 fix; it used to answer the
        // null move "bestmove 0000", which UCI does not allow after `stop`).
        _orchestrator.SetPosition("startpos", new[] { "e2e4" }, _out);
        _orchestrator.Go(PonderLimits("go ponder movetime 60000"), _out);

        Thread.Sleep(300);
        _orchestrator.Stop();

        string text = WaitFor(() => _out.Text, "bestmove");
        var bestMoves = text.Split('\n').Where(l => l.StartsWith("bestmove")).ToList();
        Assert.Single(bestMoves);
        string move = bestMoves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Trim();
        Assert.NotEqual("0000", move);

        // The answer must be a legal reply of the position that was pondered.
        var pos = Position.StartingPosition();
        pos.MakePublicMove(TestHelper.FindMove(pos, "e2", "e4"));
        Span<Move> legal = stackalloc Move[256];
        int n = pos.GenerateMoves(legal);
        Assert.Contains(move, legal[..n].ToArray().Select(UciParser.FormatMove));
    }

    [Fact]
    public void PonderHit_WhenNotPondering_IsIgnored()
    {
        // A spurious `ponderhit` (no `go ponder` in flight) must be a silent
        // no-op - not an error, not a bestmove.
        _orchestrator.SetPosition("startpos", Array.Empty<string>(), _out);

        _orchestrator.PonderHit(_out);

        Assert.DoesNotContain("bestmove", _out.Text);
        Assert.DoesNotContain("info string", _out.Text);
    }

    /// <summary>
    /// Thread-safe TextWriter sink so the background search threads can write
    /// while the test thread reads/polls.
    /// </summary>
    private sealed class LockedTextWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get { lock (_gate) return _sb.ToString(); }
        }

        public override void Write(char value)
        {
            lock (_gate) _sb.Append(value);
        }

        public override void Write(string? value)
        {
            lock (_gate) _sb.Append(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_gate) _sb.AppendLine(value);
        }

        public override void WriteLine()
        {
            lock (_gate) _sb.AppendLine();
        }
    }
}
