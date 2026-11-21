namespace PawnForge.Tests;

using System.Collections.Generic;
using System.IO;
using PawnForge.Core.Tools;
using PawnForge.UCI;
using Xunit;
using SearchEngine = PawnForge.Core.Search.Search;

/// <summary>
/// regression: <c>bench</c> must search with the SAME per-thread
/// configuration the UCI path uses.
/// <para>
/// The benchmark built its <see cref="SearchEngine"/> instances directly and
/// never applied the <c>setoption</c> override map, so every knob benchmarked
/// as its compiled default: <c>setoption name QsDeltaMargin 700</c> and
/// <c>1500</c> both reported <c>Total Nodes: 1.314.831</c>. A knob A/B was
/// therefore indistinguishable from "this knob does nothing" — and the
/// project's first cheap gate is a node-count comparison.
/// </para>
/// <para>
/// These tests assert the FIX (the hook is honored, and the report names what
/// it applied). The 4.5.4b liveness gate in <c></c> depends on this:
/// it perturbs 15 margin cells and reads node counts.
/// </para>
/// </summary>
public class BenchOverrideTests
{
    /// <summary>Depth 8 over the 4 fixed bench positions: fast (well under a
    /// second) yet deep enough that every pruning knob is actually firing —
    /// see the startpos trap recorded in.</summary>
    private const int BenchDepth = 8;

    private static (Benchmark.BenchmarkResult result, string text) RunBench(
        Action<SearchEngine>? applyOverrides = null,
        IEnumerable<KeyValuePair<string, string>>? overrides = null)
    {
        var writer = new StringWriter();
        // NNUE is the only arm, so the benchmark has no built-in default
        // evaluator any more — the caller names it, exactly as the UCI path does.
        var result = Benchmark.Run(BenchDepth, 1, writer, applyOverrides, overrides,
                                   evaluatorFactory: TestHelper.NewEvaluator);
        return (result, writer.ToString());
    }

    [Fact]
    public void NoOverrides_ReproducesCompiledDefaults()
    {
        // Preservation half: with no hook passed, the benchmark must behave
        // EXACTLY as before the fix, or every number
        // silently becomes wrong.
        //
        // Deliberately NOT a magic node-count constant: the published binary
        // benchmarks depth 13 with a 256 MB TT (the `Hash` option), while an
        // in-process run uses TTable.Shared's own size — the counts differ for
        // a reason that has nothing to do with this fix, so pinning the
        // binary's 1.314.831 here would be a flaky anchor. The invariant that
        // actually matters is determinism plus a clean, unlabelled report.
        var (first, firstText) = RunBench();
        var (second, secondText) = RunBench();

        Assert.Equal(first.TotalNodes, second.TotalNodes);
        Assert.True(first.TotalNodes > 0);
        Assert.DoesNotContain("bench override:", firstText);
        Assert.DoesNotContain("bench override:", secondText);
    }

    [Fact]
    public void ApplyOverrides_ChangesNodeCount()
    {
        // The defect itself: two different knob values produced byte-identical
        // node counts. QsDeltaMargin 700 vs the compiled default 1100 is the
        // exact pair measured in.
        var (baseline, _) = RunBench();
        var (tuned, _) = RunBench(search => search.QsDeltaMargin = 700);

        Assert.NotEqual(baseline.TotalNodes, tuned.TotalNodes);
    }

    [Fact]
    public void ApplyOverrides_ReachesThePerThreadSearchInstance()
    {
        // Belt-and-braces: the hook must actually be handed the live engine
        // instance, not a copy that is thrown away. Captured on the instance
        // itself, so a hook that ran against a different object would fail here.
        int observed = -1;
        RunBench(search => observed = search.QsDeltaMargin);

        Assert.Equal(1100, observed);
    }

    [Fact]
    public void Overrides_AreNamedInTheReport()
    {
        // A knob-configured bench must be identifiable from its own
        // transcript, or it becomes the next arm's baseline without anyone
        // knowing the arms were not comparable.
        var (_, text) = RunBench(
            search => search.QsDeltaMargin = 700,
            new[] { new KeyValuePair<string, string>("QsDeltaMargin", "700") });

        Assert.Contains("bench override: QsDeltaMargin = 700", text);
    }

    [Fact]
    public void OrchestratorBench_ForwardsSetoptionOverrides()
    {
        // End-to-end through the real UCI surface: `setoption` + `bench` on the
        // orchestrator must now produce a different node count than a bare
        // `bench`. This is the exact user-visible shape of.
        var bare = new StringWriter();
        new EngineOrchestrator().Bench(BenchDepth, 1, bare);
        long bareNodes = ParseTotalNodes(bare.ToString());

        var configured = new EngineOrchestrator();
        configured.SetOption("QsDeltaMargin", "700", new StringWriter());
        var after = new StringWriter();
        configured.Bench(BenchDepth, 1, after);

        Assert.NotEqual(bareNodes, ParseTotalNodes(after.ToString()));
        Assert.Contains("bench override: QsDeltaMargin = 700", after.ToString());
    }

    /// <summary>
    /// Pulls the node count out of a bench transcript. Group separators are
    /// culture-dependent (":N0" renders 1.314.831 on a German locale and
    /// 1,314,567 on an en-US one), so every non-digit is stripped rather than
    /// assumed to be a dot.
    /// </summary>
    private static long ParseTotalNodes(string text)
    {
        const string marker = "Total Nodes: ";
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(marker, System.StringComparison.Ordinal);
            if (i < 0) continue;
            // Strip EVERY non-digit from the line tail — not just the leading
            // digit run. The documented intent ("every non-digit is stripped")
            // was implemented as TakeWhile(IsDigit), which silently broke on a
            // German locale whenever the total dropped below 1,000,000: the
            // thousands separator then sits INSIDE the number ("99.436") and
            // the parser returned 99. Found by the M7 arm switch, where the
            // NNUE bench totals are an order of magnitude smaller than the
            // HCE's (exposed 2026-10-07).
            var digits = new string(line[(i + marker.Length)..]
                .Where(char.IsDigit)
                .ToArray());
            if (digits.Length > 0) return long.Parse(digits);
        }
        throw new System.InvalidOperationException("no 'Total Nodes:' line in bench output");
    }
}
