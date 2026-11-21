using PawnForge.Core.Board;
using PawnForge.Core.Search;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// cross-port guard (the previous generation, 2026-10-27): a continuation-history
/// table is only useful when the READ key equals the WRITE key. the previous generation wrote
/// <c>[piece][from][to]</c> and read <c>[piece][from][prevTo]</c>, so the whole
/// 1/2/4/6-ply quiet-ordering term contributed exactly zero for seven weeks with
/// no error, no crash and no failing gate anywhere.
///
/// This test goes through the REAL production paths on BOTH ends:
///   WRITE: <see cref="SearchHeuristics.UpdateContHistory"/> - the helper
/// <c>Search.Negamax</c> calls at a quiet beta cutoff;
///   READ:  <see cref="MovePicker.Next"/> - the picker scores quiets with
/// <c>ScoreQuiet</c>, the only consumer of the ordering half of the term.
/// A future key change on either side (transposed squares, a prevTo-style key, a
/// different slice) breaks the ordering assertion below. A unit test of the
/// update helper alone cannot catch it - that is the whole lesson of.
/// </summary>
public class ContinuationHistoryRoundTripTests
{
    /// <summary>Standard start position; verified against the oracle at test time
    /// (project rule: no comment-only FEN verification).</summary>
    private const string StartposFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // Two ordinary quiet pawn pushes. Square = rank*8 + file (a1 = 0), so
    // e2 = 12, e4 = 28; d2 = 13, d4 = 29. Quiet moves carry no flags, so these
    // are value-equal to the generator's moves.
    private static readonly Move E2E4 = new(12, 28);
    private static readonly Move D2D4 = new(13, 29);

    [Fact]
    public void UpdateThroughTheProductionHelper_IsReadThroughTheProductionPicker()
    {
        Stockfish.VerifyFen(StartposFen);
        Position pos = Position.FromFEN(StartposFen);

        // CONTROL: without a table, neither boosted move is first - otherwise the
        // assertions below could pass for the wrong reason (generation order).
        Move baseline = FirstQuiet(pos, null, null, null, null);
        Assert.NotEqual(E2E4, baseline);
        Assert.NotEqual(D2D4, baseline);

        int[][] slice = EmptyTable();

        // ── WRITE: exactly what Search.Negamax does at a quiet beta cutoff ──
        SearchHeuristics.UpdateContHistory(slice, E2E4, depth: 30);

        // The write must land on [from][to] and NOT on the transposed cell: a
        // [to][from] key (or a prevTo-style key) would still "look written" on
        // the cell the helper touched, so assert both directions.
        Assert.True(slice[E2E4.From][E2E4.To] > 0, "the cutoff cell was not written");
        Assert.Equal(0, slice[E2E4.To][E2E4.From]);
        Assert.Equal(0, slice[D2D4.From][D2D4.To]);

        // ── READ: the picker must rank the written move first ──
        Assert.Equal(E2E4, FirstQuiet(pos, slice, null, null, null));

        // A LARGER bonus on the other move must reorder. This proves the read is
        // live and key-addressed, not a constant or a generation-order tie.
        SearchHeuristics.UpdateContHistory(slice, D2D4, depth: 30);
        SearchHeuristics.UpdateContHistory(slice, D2D4, depth: 30);
        Assert.True(slice[D2D4.From][D2D4.To] > slice[E2E4.From][E2E4.To],
            "the gravity update did not make d2d4 hotter than e2e4");
        Assert.Equal(D2D4, FirstQuiet(pos, slice, null, null, null));
    }

    /// <summary>
    /// All four continuation distances (1/2/4/6-ply) are written by the SAME
    /// helper and read by the SAME picker method, so each must reorder when it is
    /// the only table supplied. the previous generation's dead read affected all four at once;
    /// a fix that only repaired one distance would still be caught here.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void AllFourPlyDistances_AreReadAfterAWrite(int ply)
    {
        Stockfish.VerifyFen(StartposFen);
        Position pos = Position.FromFEN(StartposFen);

        int[][] slice = EmptyTable();
        SearchHeuristics.UpdateContHistory(slice, E2E4, depth: 30);

        // Only the distance under test gets the table; the other slots stay null,
        // so the reorder is attributable to THIS distance alone.
        int[][]? h1 = ply == 1 ? slice : null;
        int[][]? h2 = ply == 2 ? slice : null;
        int[][]? h4 = ply == 4 ? slice : null;
        int[][]? h6 = ply == 6 ? slice : null;

        Assert.Equal(E2E4, FirstQuiet(pos, h1, h2, h4, h6));
    }

    /// <summary>64×64 zero table - the shape the search hands to the picker.</summary>
    private static int[][] EmptyTable()
    {
        var t = new int[64][];
        for (int i = 0; i < 64; i++)
            t[i] = new int[64];
        return t;
    }

    /// <summary>
    /// Drains the picker's first emitted move. The picker's stages put the TT
    /// move / captures first, and startpos has neither, so this is the best-scored
    /// QUIET - i.e. the production <c>ScoreQuiet</c> verdict.
    /// </summary>
    private static Move FirstQuiet(
        Position pos, int[][]? h1, int[][]? h2, int[][]? h4, int[][]? h6)
    {
        Move[] genBuf = new Move[256];
        ScoredMove[] capsBuf = new ScoredMove[256];
        ScoredMove[] quietsBuf = new ScoredMove[256];
        Move[] badBuf = new Move[256];

        var picker = new MovePicker(
            pos,
            ttMove: Move.None,
            killers: null,
            history: null,
            counterMove: Move.None,
            contHist: h1,
            contHist2: h2,
            contHist4: h4,
            contHist6: h6,
            captureHistory: null,
            onlyCaptures: false,
            genBuf: genBuf,
            capsBuf: capsBuf,
            quietsBuf: quietsBuf,
            badBuf: badBuf);

        return picker.Next();
    }
}
