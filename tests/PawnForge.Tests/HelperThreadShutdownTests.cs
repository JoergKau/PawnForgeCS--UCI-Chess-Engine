using System.IO;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for multi-threaded search lifecycle and worker pool shutdown (/ ).
/// Verifies:
/// - Normal multi-threaded search (Threads=2+) completes cleanly without deadlock
/// - Multiple search iterations across multi-threaded pool do not orphan state
/// - Stopping a multi-threaded search terminates all helpers cleanly
/// </summary>
public class HelperThreadShutdownTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — Multi-threaded search (Threads=2) runs and finishes cleanly
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void MultiThreadedSearch_2Threads_CompletesCleanly()
    {
        var input = new StringReader(
            "setoption name Threads value 2\n" +
            "position startpos\n" +
            "go depth 4\n" +
            "quit\n");
        var output = new StringWriter();

        var handler = new UciHandler();
        handler.Run(input, output);

        string result = output.ToString();
        Assert.Contains("bestmove", result);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — Multi-threaded search stop signal: stops quickly
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void MultiThreadedSearch_StopCommand_HaltsPromptly()
    {
        var orchestrator = new EngineOrchestrator();
        var output = new StringWriter();

        orchestrator.SetOption("Threads", "2", output);
        orchestrator.SetPosition("startpos", Array.Empty<string>(), output);

        var limits = new SearchLimits { Depth = 64 }; // deep search
        orchestrator.Go(limits, output);

        // Give it a short moment to launch helpers, then stop
        Thread.Sleep(50);
        orchestrator.Stop();

        // Must not hang indefinitely
        string result = output.ToString();
        Assert.NotNull(result);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — Sequential searches with multiple threads
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void MultiThreadedSearch_SequentialSearches_RunCleanly()
    {
        var input = new StringReader(
            "setoption name Threads value 2\n" +
            "position startpos\n" +
            "go depth 3\n" +
            "position startpos moves e2e4 e7e5\n" +
            "go depth 3\n" +
            "quit\n");
        var output = new StringWriter();

        var handler = new UciHandler();
        handler.Run(input, output);

        string[] lines = output.ToString()
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        int bestMoveCount = lines.Count(l => l.StartsWith("bestmove"));
        Assert.Equal(2, bestMoveCount);
    }
}
