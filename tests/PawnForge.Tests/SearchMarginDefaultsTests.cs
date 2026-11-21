namespace PawnForge.Tests;

using System.Linq;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Search-margin wiring guard: the formerly hardcoded search-margin literals
/// exposed as public <see cref="Search"/> ints must default to the exact shipped
/// values, so a freshly built binary searches bit-identically to before the
/// wiring. Any intentional default change must update these asserts explicitly.
/// Also asserts every tuning-param name resolves through the hidden-UCI
/// reflection (a typo'd lever would otherwise be silently skipped by
/// EngineOrchestrator.ApplySearchOverrides).
/// </summary>
public class SearchMarginDefaultsTests
{
    private static Search NewSearch()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        return new Search(new TTable(4), new ThreadData(0), new SearchSignal(), TestHelper.NewEvaluator(pos));
    }

    [Theory]
    [InlineData(nameof(Search.RfpMaxDepth), 4)]
                    [InlineData(nameof(Search.RazorMaxDepth), 3)]
            [InlineData(nameof(Search.NmpMinDepth), 3)]
    [InlineData(nameof(Search.NmpBaseReduction), 3)]
    [InlineData(nameof(Search.NmpDepthDivisor), 4)]
                [InlineData(nameof(Search.FutilityMaxDepth), 7)] // gate depth 7 (the design reference used 4)
    [InlineData(nameof(Search.SingularMinDepth), 8)]
    [InlineData(nameof(Search.SingularBetaSlope), 2)]
    [InlineData(nameof(Search.HistPruneMaxDepth), 3)]
    [InlineData(nameof(Search.HistPruneMargin), 2000)]
    [InlineData(nameof(Search.LmrMinDepth), 3)]
    [InlineData(nameof(Search.LmrMinMoves), 3)]
    [InlineData(nameof(Search.QsDeltaMargin), 1100)]
    [InlineData(nameof(Search.RfpImprovingDelta), 0)]   // reference -60; default 0 = flat
    [InlineData(nameof(Search.RfpNotImprovingDelta), 0)] // reference +2;  default 0 = flat
    [InlineData(nameof(Search.RfpOpponentWorseningDelta), 0)] // reference -13; default 0 = flat
    [InlineData(nameof(Search.RazorNotImprovingDelta), 0)] // reference 20; default 0 = flat
    public void SpsaWiredMargin_DefaultEqualsLegacyLiteral(string propName, int expected)
    {
        var search = NewSearch();
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(search));
    }

    [Fact]
    public void MarginParamSet_AllNamesResolveViaHiddenUciReflection()
    {
        // Mirrors EngineOrchestrator.BuildSearchOptionProps filtering (public
        // instance bool|int, minus the 3 operational blocklist entries).
        var blocklist = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            nameof(Search.PonderMode),
            nameof(Search.MateTarget),
            nameof(Search.CurrMoveReportIntervalMs),
        };
        var resolvable = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var p in typeof(Search).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (p.PropertyType != typeof(bool) && p.PropertyType != typeof(int)) continue;
            if (blocklist.Contains(p.Name)) continue;
            resolvable.Add(p.Name);
        }

        foreach (var name in SearchLeverNames.Names)
            Assert.Contains(name, resolvable);
    }

    public static readonly string[] PerDepthCellNames =
    {
        nameof(Search.LmpLimitByDepth1), nameof(Search.LmpLimitByDepth2),
        nameof(Search.LmpLimitByDepth3), nameof(Search.LmpLimitByDepth4),
        nameof(Search.FutMarginByDepth1), nameof(Search.FutMarginByDepth2),
        nameof(Search.FutMarginByDepth3), nameof(Search.FutMarginByDepth4),
        nameof(Search.RfpMarginByDepth1), nameof(Search.RfpMarginByDepth2),
        nameof(Search.RfpMarginByDepth3), nameof(Search.RfpMarginByDepth4),
        nameof(Search.RazorMarginByDepth1), nameof(Search.RazorMarginByDepth2),
        nameof(Search.RazorMarginByDepth3),
    };

    [Fact]
    public void PerDepthCells_AllNamesResolveViaHiddenUciReflection()
    {
        // Same mirror as SpsaV1ParamSet_AllNamesResolveViaHiddenUciReflection.
        var blocklist = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            nameof(Search.PonderMode),
            nameof(Search.MateTarget),
            nameof(Search.CurrMoveReportIntervalMs),
        };
        var resolvable = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var p in typeof(Search).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (p.PropertyType != typeof(bool) && p.PropertyType != typeof(int)) continue;
            if (blocklist.Contains(p.Name)) continue;
            resolvable.Add(p.Name);
        }

        foreach (var name in PerDepthCellNames)
            Assert.Contains(name, resolvable);
    }

    [Theory]
    [InlineData(nameof(Search.LmpLimitByDepth1), 9)]
    [InlineData(nameof(Search.LmpLimitByDepth2), 12)]
    [InlineData(nameof(Search.LmpLimitByDepth3), 17)]
    [InlineData(nameof(Search.LmpLimitByDepth4), 24)]
    [InlineData(nameof(Search.FutMarginByDepth1), 61)]
    [InlineData(nameof(Search.FutMarginByDepth2), 144)]
    [InlineData(nameof(Search.FutMarginByDepth3), 268)]
    [InlineData(nameof(Search.FutMarginByDepth4), 334)]
    [InlineData(nameof(Search.RfpMarginByDepth1), 102)]
    [InlineData(nameof(Search.RfpMarginByDepth2), 210)]
    [InlineData(nameof(Search.RfpMarginByDepth3), 267)]
    [InlineData(nameof(Search.RfpMarginByDepth4), 394)]
    [InlineData(nameof(Search.RazorMarginByDepth1), 86)]
    [InlineData(nameof(Search.RazorMarginByDepth2), 353)]
    [InlineData(nameof(Search.RazorMarginByDepth3), 353)]
    public void PerDepthCell_DefaultEqualsTableCell(string propName, int expected)
    {
        var search = NewSearch();
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(search));
    }

    [Fact]
    public void PerDepthCell_SetRewritesOnlyThatCell()
    {
        var search = NewSearch();
        var prop = typeof(Search).GetProperty(nameof(Search.RfpMarginByDepth2));
        Assert.NotNull(prop);
        prop.SetValue(search, 999);
        Assert.Equal(new[] { 0, 102, 999, 267, 394 }, search.RfpMarginByDepth);
    }

    [Fact]
    public void PerDepthMarginTables_MatchShippedValues()
    {
        // The per-depth margin tables as shipped.
        var s = NewSearch();
        Assert.Equal(new[] { 0, 102, 210, 267, 394 }, s.RfpMarginByDepth);
        Assert.Equal(new[] { 0, 86, 353, 353 }, s.RazorMarginByDepth);
        Assert.Equal(new[] { 0, 61, 144, 268, 334 }, s.FutMarginByDepth);
        Assert.Equal(new[] { 0, 9, 12, 17, 24 }, s.LmpLimitByDepth);
        Assert.Equal(200, s.NmpBonusDivisor);
        Assert.Equal(3, s.NmpBonusMax);
    }

    /// <summary>Single source of the tuning-lever names checked for reflection resolvability.</summary>
    internal static class SearchLeverNames
    {
        internal static readonly string[] Names =
        {
            // The scalar depth gates stay listed; the per-depth TABLE CELLS
            // (RfpMarginByDepth1..4 etc.) resolve via the same reflection and
            // are locked by PerDepthCell_* below — a future margin-SPSA tunes
            // the cells individually.
            nameof(Search.FutilityMaxDepth),
            nameof(Search.LmpMaxDepth),
            nameof(Search.SeePruningMaxDepth),
            nameof(Search.RfpMaxDepth),
            nameof(Search.RazorMaxDepth),
            nameof(Search.NmpBaseReduction),
            nameof(Search.QsDeltaMargin),
            nameof(Search.HistPruneMargin),
        };
    }

    /// <summary>
    /// Hidden-lever lock: the four improving-awareness deltas are registered as
    /// HIDDEN levers with default 0 (the compiled flat behaviour) — fully
    /// settable, but never listed in the `uci` handshake so GUI option dialogs
    /// stay focused on the user-facing options.
    /// </summary>
    [Fact]
    public void ImprovingDeltas_AreHiddenLeversWithDefaultZero()
    {
        string[] names =
        {
            nameof(Search.RfpImprovingDelta),
            nameof(Search.RfpNotImprovingDelta),
            nameof(Search.RfpOpponentWorseningDelta),
            nameof(Search.RazorNotImprovingDelta),
        };
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        string handshake = orchestrator.Options.ToString();
        foreach (var name in names)
        {
            Assert.True(orchestrator.Options.TryGetOption(name, out var opt),
                $"{name} is not registered — a lever that cannot be set is dead weight");
            Assert.NotNull(opt);
            Assert.Equal("0", opt.DefaultValue);
            Assert.True(opt.Hidden);
            Assert.DoesNotContain(name, handshake);
        }
    }
}
