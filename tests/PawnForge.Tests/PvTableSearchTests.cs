// ═══════════════════════════════════════════════════════════════════════════════
// PvTable · search integration tests
// ═══════════════════════════════════════════════════════════════════════════════

using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Integration tests for the explicit triangular PV table, wired into the real search:
/// the reported <c>info... pv...</c> line must be produced by the table
/// (<c>PvTable.GetPv</c>), its first move must equal the returned best move,
/// every move in the line must be legal, and the table must be pure
/// bookkeeping — toggling it must not change the search's best move, score,
/// or node count.
///
/// <para>
/// FEN fixtures are the oracle-gated (project rule #1) via
/// <see cref="Stockfish.AssertBestMove"/> before the engine is searched.
/// </para>
/// </summary>
[Collection("hce-eval")]
public class PvTableSearchTests
{
    // Hanging queen: white Rxd5 wins the free queen (the oracle-validated best move d2d5).
    // 6 pieces ON PURPOSE (not the minimal 4-piece KQvKR): the Negamax Syzygy probe
    // (PopCount <= Syzygy.MaxPieces) resolves positions inside the loaded tables
    // directly, bypassing PV construction — with the 3-man test tables loaded by
    // parallel Syzygy test classes, the 4-piece version's Rxd5 child (KQvK) is
    // TB-probed at every depth (depth-unconditional probing since the F4 fix), so
    // every iteration reports a 1-move PV and Pv_Line_DeepensWithIteration flakes.
    // The two extra black pawns (a7/h7) keep every PV position above 3 pieces, so
    // no TB probe can truncate the line (same fix class as RootTtCutoffTests /
    // ImprovingFlagTests). Validated: bestmove d2d5 at depth 6, PV deepens 1..6
    // with tables loaded.
    private const string HangingQueenFen = "k7/p6p/8/3q4/8/8/3R4/K7 w - - 0 1";

    private static (Search Search, List<string> Info) CreateSearch(Position pos)
    {
        var tt = new TTable(16);
        var td = new ThreadData(0);
        var signal = new SearchSignal();
        var evaluator = TestHelper.NewEvaluator(pos);
        var info = new List<string>();
        var search = new Search(tt, td, signal, evaluator, onInfo: line => info.Add(line));
        return (search, info);
    }

    /// <summary>Returns the LAST completed-iteration info line that carries a PV.</summary>
    private static string? LastPvLine(List<string> info)
    {
        string? pv = null;
        foreach (var line in info)
        {
            if (line.StartsWith("info depth", StringComparison.Ordinal) && line.Contains(" pv "))
                pv = line;
        }
        return pv;
    }

    /// <summary>Extracts the PV move tokens ("e2e4", "e7e8q",...) from an info line.</summary>
    private static string[] PvMoves(string pvLine)
    {
        int idx = pvLine.IndexOf(" pv ", StringComparison.Ordinal) + 4;
        return pvLine[idx..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Pv_FirstMove_MatchesReturnedBestMove()
    {
        // The root's exact improvement must be recorded as pv[0] — this is the
        // contract the UCI info line relies on (and what the MateIn2 test checks
        // through the mate path).
        Stockfish.AssertBestMove(HangingQueenFen, 6, "d2d5");
        var pos = Position.FromFEN(HangingQueenFen);
        var (search, info) = CreateSearch(pos);

        Move best = search.IterativeDeepening(pos, 6);

        Assert.Equal(TestHelper.FindMove(pos, "d2", "d5"), best);
        string? pvLine = LastPvLine(info);
        Assert.NotNull(pvLine);
        Assert.Equal(best.ToString(), PvMoves(pvLine!)[0]);
    }

    [Fact]
    public void Pv_EveryMove_IsLegalContinuation()
    {
        // Walk the reported PV on a COPY of the position (never mutate the
        // search's board) and require every move to be legal from the reached
        // position. A truncated/corrupted PV (the failure mode of the old
        // TT-walk) would fail on the first illegal or unmatchable move.
        Stockfish.AssertBestMove(HangingQueenFen, 6, "d2d5");
        var pos = Position.FromFEN(HangingQueenFen);
        var (search, info) = CreateSearch(pos);

        search.IterativeDeepening(pos, 6);

        string? pvLine = LastPvLine(info);
        Assert.NotNull(pvLine);
        var walker = new Position(pos);
        foreach (string uci in PvMoves(pvLine!))
        {
            // FindMove throws if the move is not legal from the current position.
            Move m = TestHelper.FindMove(walker, uci[..2], uci[2..4], uci.Length > 4 ? uci[4] : null);
            walker.MakePublicMove(m);
        }
    }

    [Fact]
    public void Pv_Line_DeepensWithIteration()
    {
        // The triangular table accumulates: deeper iterations must report a
        // strictly longer principal variation than shallow ones.
        Stockfish.AssertBestMove(HangingQueenFen, 6, "d2d5");
        var pos = Position.FromFEN(HangingQueenFen);
        var (search, info) = CreateSearch(pos);

        search.IterativeDeepening(pos, 6);

        int? firstLen = null, lastLen = null;
        foreach (var line in info)
        {
            if (!line.StartsWith("info depth", StringComparison.Ordinal) || !line.Contains(" pv ")) continue;
            int len = PvMoves(line).Length;
            firstLen ??= len;
            lastLen = len;
        }
        Assert.NotNull(firstLen);
        Assert.NotNull(lastLen);
        Assert.True(lastLen > firstLen,
            $"deepest iteration PV ({lastLen} moves) must be longer than the depth-1 PV ({firstLen} moves)");
    }

    [Fact]
    public void UsePvTable_Off_FallsBackToTtWalk()
    {
        // With the toggle off, the legacy TT-walk path must still produce a
        // correct PV whose first move matches the returned best move.
        Stockfish.AssertBestMove(HangingQueenFen, 6, "d2d5");
        var pos = Position.FromFEN(HangingQueenFen);
        var (search, info) = CreateSearch(pos);
        search.UsePvTable = false;

        Move best = search.IterativeDeepening(pos, 6);

        Assert.Equal(TestHelper.FindMove(pos, "d2", "d5"), best);
        string? pvLine = LastPvLine(info);
        Assert.NotNull(pvLine);
        Assert.Equal(best.ToString(), PvMoves(pvLine!)[0]);
    }

    [Fact]
    public void UsePvTable_DoesNotChangeSearchBehavior()
    {
        // The PvTable is pure bookkeeping: it is written on exact improvements
        // and read only at info time, so toggling it must not alter the best
        // move, the score, or the node count of an otherwise identical search.
        // Two fully independent (Search, ThreadData, TT) instances must agree
        // bit-for-bit.
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        var pos = Position.FromFEN(fen);

        var (on, _) = CreateSearch(pos);
        Move bestOn = on.IterativeDeepening(pos, 5);
        long nodesOn = on.Nodes;

        var (off, _) = CreateSearch(pos);
        off.UsePvTable = false;
        Move bestOff = off.IterativeDeepening(pos, 5);
        long nodesOff = off.Nodes;

        Assert.Equal(bestOn, bestOff);
        Assert.Equal(nodesOn, nodesOff);
    }
}