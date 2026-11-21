namespace PawnForge.Tests;

using System.Collections.Generic;
using PawnForge.Core.Board;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

/// <summary>
/// Locks for the first three items of the 's recommended execution order
///:
///
/// B1 — FORMULA RFP: `UseFormulaRfp` + `RfpImprovingScale`/`RfpNotImprovingScale` +
/// `RfpFormulaMaxDepth` replace the clamped per-depth array (the "Clamping Trap")
/// with a linear margin that scales to any depth.
/// C1 — LMR v2 `NotImproving` weight corrected 275 → 70 (the reference;
/// the 8-term bisect proved that single weight was the whole v2 regression).
/// D1 — QS TT probing: `UseQsTtProbe`, storing QS nodes at depth 0 so a QS entry can never
/// alias a main-search entry (the "negative depth" keying adapted to the u8 field).
///
/// Three kinds of test, in the order they matter:
/// 1. DEFAULTS — every new knob must default to the pre-change behaviour (B1/D1 OFF,
/// C1's 70 only reachable on the UseLmrV2 path), so a fresh binary is bit-identical.
/// 2. DELIVERY — every new knob must be an ADVERTISED uci option with the right default,
/// or a fastchess/SPSA arm silently runs defaults-vs-defaults (the SPSA v1 void,
///).
/// 3. WIRING — each toggle must demonstrably reach the search (node counts / TT state
/// change), because a toggle that cannot change anything is a comment.
/// </summary>
public class StructuralClosuresPhase123Tests
{
    // NOTE (2026-10-24): every wiring search gets a FRESH TTable. The class used to share
    // one static table across tests; once the Band 2 suite changed test ordering/parallelism,
    // a warm table let the depth-9 B1 arm cut off almost immediately (516 nodes on BOTH arms),
    // which made a node-count inequality test order-dependent. Fresh tables make each wiring
    // comparison self-contained and reproducible.

    private static Search NewSearch(Position pos, List<string>? infoSink = null)
        => new(new TTable(4), new ThreadData(0), new SearchSignal(), TestHelper.NewEvaluator(pos),
               getTotalNodes: null,
               onInfo: infoSink != null ? line => infoSink.Add(line) : null);

    // ── 1. DEFAULTS ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Search.UseFormulaRfp), true)]         // measured winner (+13.90)
    [InlineData(nameof(Search.UseQsTtProbe), true)]          // measured winner (+16.52)
    public void Phase123_BoolToggles_DefaultOff(string propName, bool expected)
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    [Theory]
    [InlineData(nameof(Search.RfpImprovingScale), 65)]     // B1 formula scale (improving)
    [InlineData(nameof(Search.RfpNotImprovingScale), 95)]  // B1 formula scale (not improving)
    [InlineData(nameof(Search.RfpFormulaMaxDepth), 8)]     // widened gate (the reference implementations use 8–9)
    [InlineData(nameof(Search.LmrV2NotImprovingWeight), 70)] // C1: was 275 (4x transcription error)
    public void Phase123_IntKnobs_DefaultValues(string propName, int expected)
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var prop = typeof(Search).GetProperty(propName);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop.GetValue(NewSearch(pos)));
    }

    [Fact]
    public void C1_LmrV2NotImprovingWeight_IsTheReferenceValue_NotTheTranscriptionError()
    {
        // The regression the review identified was a CONSTANT, so pin it explicitly:
        // the reference reads 70, not 275.
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Assert.Equal(70, NewSearch(pos).LmrV2NotImprovingWeight);
    }

    // ── 2. DELIVERY (advertised handshake) ────────────────────────────────────────

    [Fact]
    public void Phase123_NewKnobs_AdvertisedInHandshakeWithMatchingDefaults()
    {
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        var expected = new (string Name, string Default)[]
        {
            (nameof(Search.UseFormulaRfp), "true"),
            (nameof(Search.RfpImprovingScale), "65"),
            (nameof(Search.RfpNotImprovingScale), "95"),
            (nameof(Search.RfpFormulaMaxDepth), "8"),
            (nameof(Search.UseQsTtProbe), "true"),
            (nameof(Search.LmrV2NotImprovingWeight), "70"),
        };
        foreach (var (name, def) in expected)
        {
            Assert.True(orchestrator.Options.TryGetOption(name, out var opt),
                $"{name} is not advertised in the uci handshake — fastchess/SPSA would silently skip it");
            Assert.NotNull(opt);
            Assert.Equal(def, opt.DefaultValue);
        }
    }

    // ── 3. WIRING ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// B1: the toggle must reach the search. At a fixed depth the formula gate (margin 65/95
    /// per ply, gate 8) prunes on a different schedule than the clamped array (gate 4), so the
    /// node count must move. A toggle that changes nothing is dead code.
    /// </summary>
    [Fact]
    public void FormulaRfp_ChangesNodeCount_AtFixedDepth()
    {
        const string fen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9"; // the review's middlegame bench FEN

        var posArray = Position.FromFEN(fen);
        var arraySearch = NewSearch(posArray);
        arraySearch.UseFormulaRfp = false; // explicit OFF baseline; R1 default is ON
        arraySearch.IterativeDeepening(posArray, 9);
        long nodesArray = arraySearch.Nodes;

        var posFormula = Position.FromFEN(fen);
        var formulaSearch = NewSearch(posFormula);
        formulaSearch.UseFormulaRfp = true;
        formulaSearch.IterativeDeepening(posFormula, 9);
        long nodesFormula = formulaSearch.Nodes;

        Assert.NotEqual(nodesArray, nodesFormula);
    }

    /// <summary>
    /// B1 default path must be EXACTLY the old behaviour: the compiled defaults and an
    /// explicit assignment of the same defaults must produce identical node counts.
    /// </summary>
    [Fact]
    public void DefaultsAreExplicitlyEquivalent_NodeIdentical()
    {
        const string fen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";

        var posImplicit = Position.FromFEN(fen);
        var implicitSearch = NewSearch(posImplicit);
        implicitSearch.IterativeDeepening(posImplicit, 8);
        long implicitNodes = implicitSearch.Nodes;

        var posExplicit = Position.FromFEN(fen);
        var explicitSearch = NewSearch(posExplicit);
        explicitSearch.UseFormulaRfp = true;
        explicitSearch.RfpImprovingScale = 65;
        explicitSearch.RfpNotImprovingScale = 95;
        explicitSearch.RfpFormulaMaxDepth = 8;
        explicitSearch.UseQsTtProbe = true;
        explicitSearch.IterativeDeepening(posExplicit, 8);
        long explicitNodes = explicitSearch.Nodes;

        Assert.Equal(implicitNodes, explicitNodes);
    }

    /// <summary>
    /// C1: with v2 LMR ON, the weight must be live — 70 vs 275 must not produce the same tree.
    /// </summary>
    [Fact]
    public void C1_NotImprovingWeight_IsLive_WhenLmrV2On()
    {
        const string fen = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9";

        var posA = Position.FromFEN(fen);
        var searchA = NewSearch(posA);
        searchA.UseLmrV2 = true;
        searchA.LmrV2NotImprovingWeight = 70;
        searchA.IterativeDeepening(posA, 9);
        long nodesA = searchA.Nodes;

        var posB = Position.FromFEN(fen);
        var searchB = NewSearch(posB);
        searchB.UseLmrV2 = true;
        searchB.LmrV2NotImprovingWeight = 275;
        searchB.IterativeDeepening(posB, 9);
        long nodesB = searchB.Nodes;

        Assert.NotEqual(nodesA, nodesB);
    }

    /// <summary>
    /// D1 (default OFF): handing QS a TT must change NOTHING while the toggle is off — the
    /// bit-identity claim of the whole change rests on this.
    /// </summary>
    [Fact]
    public void D1_ToggleOff_WithTtIsBitIdenticalToNoTt()
    {
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"; // kiwipete

        var posA = Position.FromFEN(fen);
        var tdA = new ThreadData(0);
        var qsNoTt = new QuiescenceSearch(TestHelper.NewEvaluator(posA), tdA, new SearchSignal());
        int scoreNoTt = qsNoTt.Search(posA, -2000, 2000, ply: 0);
        long nodesNoTt = tdA.Nodes;

        var posB = Position.FromFEN(fen);
        var tdB = new ThreadData(0);
        var qsWithTt = new QuiescenceSearch(TestHelper.NewEvaluator(posB), tdB, new SearchSignal(),
                                            timeManager: null, tt: new TTable(4), currentAge: () => 1);
        int scoreWithTt = qsWithTt.Search(posB, -2000, 2000, ply: 0);
        long nodesWithTt = tdB.Nodes;

        Assert.Equal(scoreNoTt, scoreWithTt);
        Assert.Equal(nodesNoTt, nodesWithTt);
    }

    /// <summary>
    /// D1 (ON): the store must happen (a depth-0 entry exists after a QS node) and a REPEAT
    /// of the same node must be served from the TT (strictly fewer nodes) with the same score.
    /// </summary>
    [Fact]
    public void D1_ToggleOn_StoresDepthZeroEntry_AndRepeatsFromTt()
    {
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"; // kiwipete

        var pos = Position.FromFEN(fen);
        var td = new ThreadData(0);
        var tt = new TTable(4);
        var qs = new QuiescenceSearch(TestHelper.NewEvaluator(pos), td, new SearchSignal(),
                                      timeManager: null, tt: tt, currentAge: () => 1);
        qs.UseQsTtProbe = true;

        int firstScore = qs.Search(pos, -2000, 2000, ply: 0);
        long firstNodes = td.Nodes;

        Assert.True(tt.Probe(pos.ZobristKey, out var entry), "QS must have stored a TT entry for the node");
        Assert.Equal(0, entry.Depth); // the QS keying: depth 0 only

        long beforeSecond = td.Nodes;
        int secondScore = qs.Search(pos, -2000, 2000, ply: 0);
        long secondNodes = td.Nodes - beforeSecond;

        Assert.Equal(firstScore, secondScore);
        Assert.True(secondNodes < firstNodes,
            $"a repeated QS node must be served from the TT (first {firstNodes} nodes, second {secondNodes})");
    }

    /// <summary>
    /// D1 must not break tactics: on a forced mate the best move is identical with the toggle
    /// on and off (the review's "no regression" bar for a TT addition).
    /// </summary>
    [Fact]
    public void D1_ToggleOn_KeepsTheForcedMateMove()
    {
        const string fen = "6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1"; // Ra1-a8 is mate (Stockfish-verified fixture)
        Stockfish.AssertBestMove(fen, 6, "a1a8");

        var posOff = Position.FromFEN(fen);
        var searchOff = NewSearch(posOff);
        Move bestOff = searchOff.IterativeDeepening(posOff, 6);

        var posOn = Position.FromFEN(fen);
        var searchOn = NewSearch(posOn);
        searchOn.UseQsTtProbe = true;
        Move bestOn = searchOn.IterativeDeepening(posOn, 6);

        Move expected = TestHelper.FindMove(posOff, "a1", "a8");
        Assert.Equal(expected, bestOff);
        Assert.Equal(expected, bestOn);
    }
}
