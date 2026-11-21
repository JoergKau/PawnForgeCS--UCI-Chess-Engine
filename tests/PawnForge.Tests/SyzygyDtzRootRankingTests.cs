using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Regression tests for the Syzygy DTZ root ranking fix (2026-09-20,
/// <c></c>): without it, every TB win-preserving move scored the
/// same flat ±29900 and the root kept whichever move move-ordering happened to
/// put first, shuffling TB wins into fifty-move / insufficient-material draws
/// (the <c>crazy.pgn</c> game and the engine's own KQQvK self-play).
///
/// Table-dependent tests no-op when the large (3-4-5 + 6-man) tables are absent.
/// Discovery order: <c>PAWNFORGE_SYZYGY_PATH</c> env var, then the standard
/// local ETB directory (both sub-dirs, colon-joined).
/// </summary>
[Collection("syzygy-tb")]
public class SyzygyDtzRootRankingTests : IDisposable
{
    private static readonly string? BigTbDir = FindBigTbDir();

    public SyzygyDtzRootRankingTests()
    {
        if (BigTbDir != null)
            Syzygy.Initialize(BigTbDir);
    }

    public void Dispose()
    {
        Syzygy.Release();
    }

    /// <summary>
    /// The tablebase list to use: PAWNFORGE_SYZYGY_PATH first, then the standard
    /// install locations for this platform.
    ///
    /// The path LIST separator is platform-specific — ';' on Windows, ':' on
    /// macOS and Linux — because it is what Syzygy.Initialize splits on (the same
    /// rule the engine's own SyzygyPath option applies). Joining with ':' on
    /// Windows yields ONE bogus directory name, the tables silently fail to load,
    /// and every DTZ assertion below quietly skips.
    /// </summary>
    private static string? FindBigTbDir()
    {
        var env = Environment.GetEnvironmentVariable("PAWNFORGE_SYZYGY_PATH");
        if (!string.IsNullOrEmpty(env))
            return env;

        char sep = Path.PathSeparator;   // ';' on Windows, ':' elsewhere

        // 3-4-5 and 6-men live in two sibling directories; a root that contains
        // both directly is also accepted. Override the search with
        // PAWNFORGE_SYZYGY_PATH when the tables live elsewhere.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        {
            Path.Combine(home, "syzygy"),
            Path.Combine(home, "test", "syzygy"),
        };
        foreach (string root in roots)
        {
            string wdl = Path.Combine(root, "Syzygy-3-4-5");
            string six = Path.Combine(root, "Syzygy-6");
            if (Directory.Exists(wdl) && Directory.Exists(six))
                return wdl + sep + six;
        }

        return null;
    }

    private static Search NewSearch(Position pos) =>
        new(new TTable(4), new ThreadData(0), new SearchSignal(),
            TestHelper.NewEvaluator(pos), getTotalNodes: null, onInfo: null);

    // d2 = 11, e2 = 12, c1 = 2 on the 0-based mailbox.
    private const int D2 = 11;
    private const int E2 = 12;
    private const int C1 = 2;

    /// <summary>
    /// The reported fixture (after 4...Kd4). Every win-preserving move scores the
    /// same flat +29900; the DTZ root ranking must prefer the fastest-progress
    /// Ke2 (d2e2) over the lazy Kc1 (d2c1) the engine played in `crazy.pgn`.
    /// </summary>
    [Fact]
    public void RootRanking_PlaysDtzOptimalMove_InsteadOfLazyShuffle()
    {
        if (BigTbDir == null) return;

        var pos = Position.FromFEN("8/8/8/8/3kP3/5P2/3K4/8 w - - 0 5");
        var search = NewSearch(pos);

        Move best = search.IterativeDeepening(pos, 12);

        Assert.False(best.IsNone);
        Assert.Equal(D2, best.From);
        Assert.Equal(E2, best.To);
    }

    /// <summary>
    /// With the ranking disabled, the pre-fix behavior is reproduced exactly
    /// (the first-searched flat +29900 move, Kc1). This proves the change is
    /// isolated and bisectable.
    /// </summary>
    [Fact]
    public void RootRanking_ToggleOff_ReproducesLegacyShuffle()
    {
        if (BigTbDir == null) return;

        var pos = Position.FromFEN("8/8/8/8/3kP3/5P2/3K4/8 w - - 0 5");
        var search = NewSearch(pos);
        search.UseSyzygyDtzRootRanking = false;

        Move best = search.IterativeDeepening(pos, 12);

        Assert.False(best.IsNone);
        Assert.Equal(D2, best.From);
        Assert.Equal(C1, best.To);
    }

    /// <summary>
    /// The full reported failure: self-play from the game FEN must reach
    /// CHECKMATE for the winning side, not the pre-fix walk into an
    /// insufficient-material draw (K vs K after both promoted queens were hung).
    /// Bounded so a regression fails by not mating in time rather than hanging.
    /// </summary>
    [Fact]
    public void RootRanking_ConvertsWonEndgame_ToCheckmate()
    {
        if (BigTbDir == null) return;

        var pos = Position.FromFEN("8/3k4/4p3/8/8/3PPP2/3K4/8 b - - 0 1");
        Span<Move> buf = stackalloc Move[256];

        bool checkmate = false;
        for (int ply = 0; ply < 80; ply++)
        {
            int n = pos.GenerateMoves(buf);
            if (n == 0)
            {
                checkmate = pos.IsKingAttacked(pos.SideToMove);
                break;
            }

            var search = NewSearch(pos);
            Move best = search.IterativeDeepening(pos, 12);
            Assert.False(best.IsNone);
            pos.MakePublicMove(best);
        }

        Assert.True(checkmate, "TB win was not converted to checkmate within 80 plies");
    }
}
