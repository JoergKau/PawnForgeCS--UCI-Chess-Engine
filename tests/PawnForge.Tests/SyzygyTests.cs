using PawnForge.Core.Board;
using PawnForge.Core.Tb;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Syzygy tablebase support: raw WDL/DTZ probing of the.rtbw/.rtbz files.
/// Uses the checked-in 3-man tables (tbdata/) and known reference values that
/// were cross-validated against python-chess (93/93 positions, 141/143 DTZ).
/// The tbdata tables are part of the free public Syzygy 3-man set (see
///); when they are absent (e.g. a stripped CI checkout)
/// the table-dependent tests no-op.
/// </summary>
[Collection("syzygy-tb")]
public class SyzygyTests : IDisposable
{
    private static readonly string? TbDir = TestHelper.FindTbDir();

    public SyzygyTests()
    {
        if (TbDir != null)
            Syzygy.Initialize(TbDir);
    }

    public void Dispose()
    {
        Syzygy.Release();
        Syzygy.ProbingEnabled = true;
        Syzygy.UseRule50 = true;
    }

    private static string WdlStr(int wdl) => wdl switch
    {
        2 => "WIN", -2 => "LOSS", 1 => "cursedWIN", -1 => "cursedLOSS", 0 => "DRAW", _ => "FAIL"
    };

    [Fact]
    public void Initialize_LoadsTables()
    {
        if (TbDir == null) return;
        Assert.True(Syzygy.IsInitialized);
        Assert.True(Syzygy.WdlTableCount >= 3, $"expected >=3 WDL tables, got {Syzygy.WdlTableCount}");
        Assert.Equal(3, Syzygy.MaxPieces);
    }

    [Fact]
    public void ProbeWdl_MatchesReferenceValues()
    {
        if (TbDir == null) return;
        (string Fen, int Expected)[] cases =
        {
            ("8/8/8/8/8/1Q6/8/K6k w - - 0 1", 2),     // KQ vs K: win
            ("8/8/8/8/8/1R6/8/K6k w - - 0 1", 2),     // KR vs K: win
            ("k7/8/8/8/8/8/P7/K7 w - - 0 1", 0),      // KP rook pawn, king behind: draw
            ("4k3/4P3/8/8/8/8/8/6K1 w - - 0 1", 0),   // KP blocked pawn: draw
            ("8/8/8/8/8/8/8/K6k w - - 0 1", 0),       // KvK: draw
            ("8/8/8/8/8/1Q6/8/K6k b - - 0 1", -2),    // KQvK black to move: loss
            ("6k1/8/6K1/8/8/8/8/1R6 b - - 0 1", -2),  // KRvK black to move: loss
        };
        foreach (var (fen, expected) in cases)
        {
            var pos = Position.FromFEN(fen);
            int got = Syzygy.ProbeWdl(pos);
            Assert.Equal(expected, got);
        }
    }

    [Fact]
    public void ProbeWdl_IllegalOrUncovered_ReturnsFail()
    {
        if (TbDir == null) return;
        // Adjacent kings (illegal) must not crash and must report failure.
        var illegal = Position.FromFEN("4k3/5K2/4P3/8/8/8/8/8 w - - 0 1");
        Assert.Equal(int.MinValue, Syzygy.ProbeWdl(illegal));

        // Castling rights present -> probing not possible (like the reference).
        var castling = Position.FromFEN("4k2r/8/8/8/8/8/8/4K2R w Kk - 0 1");
        Assert.Equal(int.MinValue, Syzygy.ProbeWdl(castling));

        // 6 pieces - beyond the loaded 3-man tables.
        var six = Position.FromFEN("k7/8/1K6/8/8/8/8/QQ6 w - - 0 1");
        Assert.Equal(int.MinValue, Syzygy.ProbeWdl(six));
    }

    [Fact]
    public void ProbeRoot_ReturnsWinningMove()
    {
        if (TbDir == null) return;
        var pos = Position.FromFEN("8/8/8/8/8/1Q6/8/K6k w - - 0 1");
        var root = Syzygy.ProbeRoot(pos);
        Assert.False(root.Failed);
        Assert.False(root.Move.IsNone);
        Assert.Equal(2, root.Wdl);
        Assert.True(root.Dtz > 0);

        // The returned move must be legal in the position.
        Span<Move> legal = stackalloc Move[256];
        int n = pos.GenerateMoves(legal);
        Assert.Contains(root.Move, legal[..n].ToArray());
    }

    [Fact]
    public void ProbeRoot_DrawPosition_PreservesDraw()
    {
        if (TbDir == null) return;
        var pos = Position.FromFEN("k7/8/8/8/8/8/P7/K7 w - - 0 1");
        var root = Syzygy.ProbeRoot(pos);
        Assert.False(root.Failed);
        Assert.Equal(0, root.Wdl);
        Assert.Equal(0, root.Dtz);
        Assert.False(root.Move.IsNone);
    }

    [Fact]
    public void ProbeRoot_CheckmateAndStalemate_AreDetected()
    {
        if (TbDir == null) return;
        // KQ mate: black K a8 in check by Qb7, all escape squares covered.
        var kqmate = Position.FromFEN("k7/1Q6/2K5/8/8/8/8/8 b - - 0 1");
        var root = Syzygy.ProbeRoot(kqmate);
        Assert.False(root.Failed);
        Assert.True(root.IsCheckmate, $"expected checkmate, got move {root.Move} wdl {root.Wdl}");

        // KQ stalemate: black K a8, Q b6, K c6; not in check, no legal moves.
        var stale = Position.FromFEN("k7/8/1QK5/8/8/8/8/8 b - - 0 1");
        var r2 = Syzygy.ProbeRoot(stale);
        Assert.False(r2.Failed);
        Assert.True(r2.IsStalemate, $"expected stalemate, got move {r2.Move} wdl {r2.Wdl}");
    }

    [Fact]
    public void WdlToScore_MapsCorrectly()
    {
        Assert.Equal(Syzygy.TbWinScore, Syzygy.WdlToScore(2));
        Assert.Equal(-Syzygy.TbWinScore, Syzygy.WdlToScore(-2));
        Assert.Equal(1, Syzygy.WdlToScore(1));
        Assert.Equal(-1, Syzygy.WdlToScore(-1));
        Assert.Equal(0, Syzygy.WdlToScore(0));
    }

    [Fact]
    public void WdlToScore_BelowMateBound()
    {
        Assert.True(Syzygy.TbWinScore < 30000, "TB win score must stay below the engine's mate bound");
        Assert.True(Syzygy.TbWinScore > 10000, "TB win score must exceed any material evaluation");
    }
}
