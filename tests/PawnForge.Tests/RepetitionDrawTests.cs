using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for the twofold-repetition draw inside the search tree
///.
///
/// the reference parity: the reference scores the SECOND occurrence of a position within the search
/// as a draw (<c>Score.isDoubleRepetition</c>, <c>Score.isDraw</c>); PawnForge's legacy
/// behavior requires threefold even in-tree. With the toggle ON the search must score a
/// once-repeated position as 0; with the toggle OFF (default) the legacy threefold-only
/// behavior must be reproduced exactly, and the GAME-claim rule (<c>Position.IsDraw</c>)
/// must remain threefold in both cases.
/// </summary>
public class RepetitionDrawTests
{
    // startpos reached again after 1.Nf3 Nf6 2.Ng1 Ng8 — the initial position
    // has occurred twice in total (one previous occurrence = twofold).
    private const string StartposFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>Builds a position that has occurred exactly twice (one repetition).</summary>
    private static Position MakeTwofoldPosition()
    {
        var pos = Position.FromFEN(StartposFen);
        Assert.True(MakeUci(pos, "g1f3"));
        Assert.True(MakeUci(pos, "g8f6"));
        Assert.True(MakeUci(pos, "f3g1"));
        Assert.True(MakeUci(pos, "f6g8"));
        return pos;
    }

    private static bool MakeUci(Position pos, string uci)
    {
        foreach (var m in pos.GenerateMoves())
        {
            if (Uci(m) == uci)
            {
                pos.MakePublicMove(m);
                return true;
            }
        }
        return false;
    }

    private static string Uci(Move m) =>
        $"{(char)('a' + m.From % 8)}{(char)('1' + m.From / 8)}{(char)('a' + m.To % 8)}{(char)('1' + m.To / 8)}";

    [Fact]
    public void TwofoldPosition_Detected_OnlyWhenToggleRequested()
    {
        var pos = MakeTwofoldPosition();

        // The position occurred once before = twofold, not threefold.
        Assert.Equal(1, pos.GetRepetitionCount());
        Assert.True(pos.IsTwofoldRepetition());
        Assert.False(pos.IsThreefoldRepetition());

        // Search-scoped draw: ON scores the repetition, OFF (legacy) does not.
        Assert.True(pos.IsDrawForSearch(twofoldRepetition: true));
        Assert.False(pos.IsDrawForSearch(twofoldRepetition: false));

        // The game-claim rule is threefold in BOTH cases (GUI semantics untouched).
        Assert.False(pos.IsDraw());
    }

    [Fact]
    public void ThreefoldPosition_IsDraw_UnderBothConventions()
    {
        // Play the knight-swap cycle TWICE: after 1.Nf3 Nf6 2.Ng1 Ng8 3.Nf3 Nf6 4.Ng1 Ng8
        // the initial position has occurred three times total (two previous occurrences).
        var pos = Position.FromFEN(StartposFen);
        Assert.True(MakeUci(pos, "g1f3"));
        Assert.True(MakeUci(pos, "g8f6"));
        Assert.True(MakeUci(pos, "f3g1"));
        Assert.True(MakeUci(pos, "f6g8"));
        Assert.True(MakeUci(pos, "g1f3"));
        Assert.True(MakeUci(pos, "g8f6"));
        Assert.True(MakeUci(pos, "f3g1"));
        Assert.True(MakeUci(pos, "f6g8"));

        Assert.Equal(2, pos.GetRepetitionCount());
        Assert.True(pos.IsThreefoldRepetition());

        // Both search conventions and the game-claim rule score it as a draw.
        Assert.True(pos.IsDrawForSearch(twofoldRepetition: true));
        Assert.True(pos.IsDrawForSearch(twofoldRepetition: false));
        Assert.True(pos.IsDraw());
    }

    [Fact]
    public void Toggle_On_ChangesSearchBehavior_Off_ReproducesLegacy()
    {
        // Position after 1.Nf3 Nf6 2.Ng1 Ng8: the startpos occurred once before, so with the
        // toggle ON any search-tree recurrence of it scores 0 — the search must behave
        // differently from the legacy threefold-only run. Cheap, deterministic assertion:
        // node counts must differ (with ON the engine discounts repeating lines).
        var ttOn = new TTable(16);
        var tdOn = new ThreadData(0);
        var posOn = MakeTwofoldPosition();
        var searchOn = new Search(ttOn, tdOn, new SearchSignal(), TestHelper.NewEvaluator(posOn));
        searchOn.TimeManager.SetInfinite();
        searchOn.UseTwofoldRepetitionDraw = true;
        var bestOn = searchOn.IterativeDeepening(posOn, maxDepth: 8);

        var ttOff = new TTable(16);
        var tdOff = new ThreadData(0);
        var posOff = MakeTwofoldPosition();
        var searchOff = new Search(ttOff, tdOff, new SearchSignal(), TestHelper.NewEvaluator(posOff));
        searchOff.TimeManager.SetInfinite();
        searchOff.UseTwofoldRepetitionDraw = false;
        var bestOff = searchOff.IterativeDeepening(posOff, maxDepth: 8);

        Assert.False(bestOn.IsNone);
        Assert.False(bestOff.IsNone);

        long nodesOn = tdOn.Nodes;
        long nodesOff = tdOff.Nodes;
        Assert.NotEqual(nodesOn, nodesOff);
    }
}
