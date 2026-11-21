namespace PawnForge.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using PawnForge.UCI;
using Xunit;

/// <summary>
/// Guard for the UCI option CATALOG — delivery surface and visibility policy.
/// An option that is accepted by `setoption` but whose value never reaches the
/// search is worse than no option: it looks like a working lever, an A/B arm
/// sets it, and the game is played by the unmodified engine — with no
/// diagnostic anywhere. That exact failure once happened because a batch of
/// check options was registered through the `UciOption(name, default)`
/// overload, whose `OnChange` parameter defaulted to null, so nothing was
/// written into the `_searchOverrides` store and every one of them was inert.
/// The node-count probe caught it (`AspirationBase=33` produced a
/// bit-identical 225020).
///
/// Two end-to-end mechanisms protect this:
/// 1. For a sample of levers a deterministic `bench` runs twice — bare, then
/// with the option set — and the node count must MOVE. A dead option
/// produces identically-many nodes and fails.
/// 2. Structural guards that need no search at all: no `UciOption`
/// constructor may declare an optional `Action<string>` callback; every
/// registered option carries a non-null callback; and every catalog
/// default must equal the compiled Search default.
///
/// VISIBILITY POLICY (2026-11-07): exactly twelve options are ADVERTISED in
/// the `uci` handshake (Hash, Threads, Move Overhead, Clear Hash, Own Book,
/// Book File, Book Depth, SyzygyPath, SyzygyRootProbe, Syzygy50MoveRule,
/// EvalFile, Contempt) so GUI option dialogs stay focused. Every research
/// lever is registered HIDDEN (OptionsMap.AddLever) — fully settable through
/// `setoption` and the `param:Name=value` argv override, but never listed by a
/// GUI. This suite pins both halves: the hidden levers must still be alive
/// (node-count probes) and the public set must not silently shrink or grow.
/// </summary>

public class AdvertisedSearchOptionTests
{
    private const int BenchDepth = 11;

    /// <summary>Node count of a `bench` run of an unconfigured engine.</summary>
    private static long BareNodes()
    {
        var w = new StringWriter();
        new EngineOrchestrator().Bench(BenchDepth, 1, w);
        return ParseTotalNodes(w.ToString());
    }

    /// <summary>
    /// Node count of a `bench` run with one option set. Returns null when the
    /// run produced no parseable node count (a crash surfaced by the
    /// orchestrator rather than a count).
    /// </summary>
    /// <param name="also">Lever options that must be set TOGETHER with the
    /// lever under test, because the lever is gated on them. See
    /// <see cref="CoupledLevers"/>.</param>
    private static long? NodesWith(string name, string value,
                                   params (string Name, string Value)[] also)
    {
        var w = new StringWriter();
        var orch = new EngineOrchestrator();
        foreach (var (n, v) in also) orch.SetOption(n, v, w);
        orch.SetOption(name, value, w);
        orch.Bench(BenchDepth, 1, w);
        string text = w.ToString();
        var m = System.Text.RegularExpressions.Regex.Match(text, @"Total Nodes:\s*([\d\.\,]+)");
        return m.Success ? (long?)ParseTotalNodes(text) : null;
    }

    private static long ParseTotalNodes(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"Total Nodes:\s*([\d\.\,]+)");
        Assert.True(m.Success, "no 'Total Nodes:' line in bench output:\n" + text);
        // Group separators are culture-dependent (1.314.831 on a German locale,
        // 1,314,567 on en-US): strip every non-digit.
        return long.Parse(System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\D", ""));
    }

    /// <summary>
    /// One row per lever: the option name, the value to set on the arm, and
    /// whether it is expected to change the node count. A lever that ships ON
    /// has its experiment value `false`; one that ships OFF uses `true`.
    /// </summary>
    public static IEnumerable<object[]> Levers()
    {
        // (name, value) pairs sampled across every feature family in closure.
        // The third element lists levers that must be set together with the
        // lever under test because the lever is GATED on them — advertising a
        // coupled lever alone would ship a knob that silently does nothing,
        // which is the class in a subtler form.
        yield return new object[] { "AspirationBase", "33", Array.Empty<(string, string)>() };
        yield return new object[] { "NmpDepthDivisor", "2", Array.Empty<(string, string)>() };
        yield return new object[] { "UseLmrV2", "true", Array.Empty<(string, string)>() };
        // UseLmrNoisyMoves was DECOUPLED from UseLmrV2 on 2026-10-24 —
        // it now reduces bad captures on the live v1 schedule, so it is a standalone lever.
        yield return new object[] { "UseLmrNoisyMoves", "true", Array.Empty<(string, string)>() };
        // --- coupled to UseLmrV2: the early LMR-v2 block guards these ---
        yield return new object[] { "UseRefHistoryPruning", "true", new[] { ("UseLmrV2", "true") } };
        yield return new object[] { "UseRefFutilityFormula", "true", new[] { ("UseLmrV2", "true") } };
        // --- independent ---
        yield return new object[] { "UseTerm1NotImproving", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseLmrTable", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseCutNode", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseRefLmp", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseRefSeeThresholds", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseBadNoisyPruning", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseAlphaRaiseReduction", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseAdaptiveReSearch", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseProbCut", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseSecondaryProbCut", "true", Array.Empty<(string, string)>() };
        yield return new object[] { "UseNegativeExtensions", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseImprovingFlag", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseRefCorrectionHistory", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseTwofoldRepetitionDraw", "false", Array.Empty<(string, string)>() };
        yield return new object[] { "UseQuiescenceChecks", "true", Array.Empty<(string, string)>() };
    }

    /// <summary>
    /// The levers that are GATED on another lever, and therefore only move the
    /// search when their partner is set too. Shipping these as standalone
    /// options without recording the coupling would sell a knob that does
    /// nothing on its own: <c>UseRefFutilityFormula</c> and
    /// <c>UseRefHistoryPruning</c> are read inside the LMR-v2 early block
    /// (<c>Search.Negamax.cs</c>: <c>lmrV2Handled = UseLateMoveReductions &amp;&amp;
    /// UseLmrV2</c>), so with <c>UseLmrV2=false</c> they are dead code.
    /// The review's own recommendation is that these two are run WITH
    /// <c>UseLmrV2=true</c>, never standalone.
    ///
    /// NOTE (2026-10-24): <c>UseLmrNoisyMoves</c> USED to be
    /// the third member of this list. It has been DECOUPLED — the live v1 LMR
    /// schedule now honours it for bad captures (`SeeGe(m,0) == false`, non-ttPv)
    /// through the noisy base table, so it is deliberately NO LONGER inert alone.
    /// Its standalone wiring is locked by
    /// <c>StructuralClosuresBand1Tests.Step5_LmrNoisyMoves_ChangesNodeCount_WithoutLmrV2</c>.
    /// </summary>
    public static IEnumerable<object[]> CoupledLevers()
    {
        yield return new object[] { "UseRefHistoryPruning" };
        yield return new object[] { "UseRefFutilityFormula" };
    }

    [Theory]
    [MemberData(nameof(CoupledLevers))]
    public void CoupledLever_IsInertWithoutItsPartner(string name)
    {
        // The coupling must be REAL (and recorded): the lever alone must not
        // move the bench, otherwise the "run with UseLmrV2=true" instruction in
        // the review is stale and could be dropped without anyone noticing.
        long bare = BareNodes();
        long? alone = NodesWith(name, "true");

        Assert.True(alone.HasValue, $"{name}: bench produced no node count");
        Assert.Equal(bare, alone!.Value);
    }


    /// <summary>
    /// THE guard for dead options. Check options were once registered through
    /// the <c>UciOption(name, default)</c> overload, whose <c>onChange</c>
    /// parameter defaulted to <c>null</c> — so every one of them was ACCEPTED
    /// by <c>setoption</c> and DEAD, with no compiler diagnostic and a
    /// bit-identical node count on both arms. The fix is that every
    /// <c>UciOption</c> constructor now takes its callback as a REQUIRED
    /// parameter, so that omission is a compile error rather than a runtime
    /// ON-vs-ON match.
    ///
    /// This test asserts the constructors' signatures directly (rather than
    /// trusting the fix to survive a refactor): no <c>UciOption</c>
    /// constructor may declare an optional <see cref="Action{T}"/> callback.
    /// </summary>
    [Fact]
    public void UciOption_Constructors_RequireTheCallback()
    {
        var offenders = typeof(UciOption)
            .GetConstructors()
            .Where(c => c.GetParameters().Any(p =>
                p.ParameterType == typeof(Action<string>) && p.IsOptional))
            .Select(c => "UciOption(" + string.Join(", ",
                c.GetParameters().Select(p =>
                    $"{p.ParameterType.Name} {p.Name}" + (p.IsOptional ? " = null" : ""))) + ")")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A UciOption constructor declares an OPTIONAL Action<string> callback. An option " +
            "registered through it with no callback is silently inert — accepted " +
            "by setoption, and dead. Make the callback required:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The companion to the signature guard: every option the orchestrator actually
    /// registers must carry a non-null callback (or be a button, which has its own
    /// path). This catches a future registration that passes an explicit <c>null</c>,
    /// which the required-parameter signature alone would not stop.
    /// </summary>
    [Fact]
    public void EveryRegisteredOption_CarriesACallback()
    {
        var optionsField = typeof(OptionsMap).GetField("_options",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(optionsField);

        var orchestrator = new EngineOrchestrator();
        var dict = (System.Collections.IDictionary)optionsField!
            .GetValue(orchestrator.Options)!;
        Assert.True(dict.Count > 0, "no options registered — wrong field?");

        var dead = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var opt = (UciOption)entry.Value!;
            if (opt.Type == "button") continue;          // buttons press instead of store
            if (opt.OnChange is null) dead.Add((string)entry.Key);
        }

        Assert.True(dead.Count == 0,
            "Options registered with a null OnChange — they accept setoption and do nothing " +
            "(inert-lever failure):\n  " + string.Join("\n  ", dead));
    }

    /// <summary>
    /// MaxPathExtensions is the ONE advertised lever that legitimately does not
    /// move the bench: the cap of 32 accumulated extension plies is far beyond
    /// what an 11-ply bench reaches, so widening it to 64 changes nothing here.
    /// It is asserted separately (rather than dropped) so the cap's reach stays
    /// visible, and so nobody mistakes it for the dead-option class.
    /// </summary>
    [Fact]
    public void MaxPathExtensions_IsInertAtBenchDepth_AndThatIsDocumented()
    {
        long bare = BareNodes();
        Assert.Equal(bare, NodesWith("MaxPathExtensions", "64")!.Value);
        Assert.Equal(bare, NodesWith("MaxPathExtensions", "8")!.Value);
    }

    /// <summary>
    /// THE guard: a registered lever must actually change the search. This is
    /// the assertion that failed when the check options were registered
    /// without an OnChange callback. Hidden levers are exercised exactly like
    /// advertised ones — `setoption` resolves hidden names too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Levers))]
    public void HiddenLever_ChangesTheSearch(string name, string value,
                                             (string Name, string Value)[] also)
    {
        long bare = BareNodes();
        long? armed = NodesWith(name, value, also);

        Assert.True(armed.HasValue,
            $"{name}: the bench run produced no node count — the option broke the run.");
        Assert.NotEqual(bare, armed!.Value);
    }

    /// <summary>
    /// A hidden lever must never appear in the `uci` handshake (that is what
    /// keeps GUI option dialogs focused), and the PUBLIC set that IS announced
    /// must be exactly the twelve user-facing options. Anything else is
    /// either a GUI-flooding regression or a lost user option.
    /// </summary>
    [Fact]
    public void Handshake_AdvertisesOnlyThePublicSet()
    {
        string handshake = new EngineOrchestrator().Options.ToString();
        var re = new System.Text.RegularExpressions.Regex(@"^option name (.+?) type ");
        var advertised = handshake
            .Split('\n')
            .Select(l => re.Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        string[] expectedPublic =
        [
            "Hash", "Threads", "Move Overhead", "Clear Hash",
            "Own Book", "Book File", "Book Depth",
            "SyzygyPath", "SyzygyRootProbe", "Syzygy50MoveRule",
            "EvalFile",
            "Contempt",
        ];

        var missing = expectedPublic.Where(n => !advertised.Contains(n)).ToList();
        var extra = advertised.Except(expectedPublic).ToList();

        Assert.True(missing.Count == 0,
            "Public options disappeared from the handshake:\n  " + string.Join("\n  ", missing));
        Assert.True(extra.Count == 0,
            "Non-public options are advertised — this is what floods GUI option dialogs:\n  " +
            string.Join("\n  ", extra));
    }

    /// <summary>
    /// Every catalog entry — HIDDEN levers included — must advertise the same
    /// default as the compiled Search property. Registering a different
    /// default would silently change engine behaviour with no source change
    /// at all — and would make every "keep the current behaviour" A/B arm an
    /// arm for a different engine. This walks the full catalog
    /// (OptionsMap.ToFullString), so default drift cannot hide behind the
    /// visibility policy either.
    /// </summary>
    [Fact]
    public void Catalog_DefaultEqualsCompiledDefault()
    {
        var searchProps = typeof(PawnForge.Core.Search.Search)
            .GetProperties(System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite &&
                        (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int)))
            .ToDictionary(p => p.Name, p => p);

        string fullCatalog = new EngineOrchestrator().Options.ToFullString();
        var pos = PawnForge.Core.Board.Position.FromFEN(
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var search = new PawnForge.Core.Search.Search(
            new PawnForge.Core.TranspositionTable.TTable(4),
            new PawnForge.Core.Search.ThreadData(0),
            new PawnForge.Core.Search.SearchSignal(),
            TestHelper.NewEvaluator(pos));

        int checkedCount = 0;
        var failures = new List<string>();

        foreach (var kv in searchProps)
        {
            string? line = fullCatalog
                .Split('\n')
                .FirstOrDefault(l => l.StartsWith($"option name {kv.Key} "));
            if (line == null) continue; // not registered — allowed
            checkedCount++;

            object compiled = kv.Value.GetValue(search)!;
            string expected = kv.Value.PropertyType == typeof(bool)
                ? ((bool)compiled ? "true" : "false")
                : compiled.ToString()!;
            string catalogDefault = System.Text.RegularExpressions.Regex
                .Match(line, @"default (\S+)").Groups[1].Value;

            if (catalogDefault != expected)
                failures.Add(
                    $"{kv.Key}: catalog default '{catalogDefault}' != compiled default '{expected}'");
        }

        Assert.True(checkedCount >= 32,
            $"Expected at least 32 registered Search options (the lever catalog), found " +
            $"{checkedCount}. Someone removed registrations whole — that silently deletes " +
            "testing levers.");
        Assert.True(failures.Count == 0,
            "Default drift between the option catalog and the compiled defaults:\n  " +
            string.Join("\n  ", failures));
    }
}
