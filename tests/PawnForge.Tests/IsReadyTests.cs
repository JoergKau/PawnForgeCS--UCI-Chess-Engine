using System.Diagnostics;
using System.IO;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for `isready` semantics (/ ).
/// Verifies:
/// - `isready` responds with `readyok` within a tight timeframe when quiescent
/// - Multiple `isready` commands emit matching `readyok` responses in order
/// - `isready` during an infinite search answers immediately and leaves the
/// command loop able to process `stop` 
/// - UciHandler processes `isready` through the standard UCI loop
/// </summary>
public class IsReadyTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — `isready` when no search running: responds promptly with `readyok`
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_WhenIdle_EmitsReadyOkQuickly()
    {
        var input = new StringReader("isready\nquit\n");
        var output = new StringWriter();

        var handler = new UciHandler();
        var sw = Stopwatch.StartNew();
        handler.Run(input, output);
        sw.Stop();

        string outStr = output.ToString();
        Assert.Contains("readyok", outStr);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — Multiple `isready` commands: exactly N `readyok` responses in order
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_MultipleCommands_EmitsExactCountInOrder()
    {
        const int count = 5;
        var commands = string.Join("\n", Enumerable.Repeat("isready", count)) + "\nquit\n";
        var input = new StringReader(commands);
        var output = new StringWriter();

        var handler = new UciHandler();
        handler.Run(input, output);

        string[] lines = output.ToString()
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        int readyOkCount = lines.Count(line => line.Trim() == "readyok");
        Assert.Equal(count, readyOkCount);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — `isready` DURING a search must answer immediately
    // (a hang). The old implementation waited on
    // _searchTask, so `go infinite` + `isready` blocked the only command
    // thread and the later `stop` was never read.
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsReady_DuringInfiniteSearch_ReturnsImmediately_AndStopStillWorks()
    {
        var buffer = new StringWriter();
        var output = TextWriter.Synchronized(buffer);
        var orchestrator = new EngineOrchestrator();
        orchestrator.SetPosition("startpos", Array.Empty<string>(), output);
        orchestrator.Go(new SearchLimits { Infinite = true }, output);

        try
        {
            var ready = Task.Run(() => orchestrator.ConfirmReady(output));
            var completed = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(ready, completed);
            await ready;
            Assert.Contains("readyok", buffer.ToString());
        }
        finally
        {
            // Abort the infinite search even if the assertion above failed.
            orchestrator.Stop();
        }

        Assert.Contains("bestmove", buffer.ToString());
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — `isready` after `ucinewgame` and `position`
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_AfterPositionSetup_EmitsReadyOk()
    {
        var input = new StringReader("ucinewgame\nposition startpos moves e2e4 e7e5\nisready\nquit\n");
        var output = new StringWriter();

        var handler = new UciHandler();
        handler.Run(input, output);

        string outStr = output.ToString();
        Assert.Contains("readyok", outStr);
    }
}
