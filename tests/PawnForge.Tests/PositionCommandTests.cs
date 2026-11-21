using System.IO;
using System.Linq;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for the `position` and `ucinewgame` handling of the engine orchestrator.
/// Verifies:
/// - a fresh engine starts from the start position
/// - an illegal move rejects the whole `position` command and keeps the previous position
/// - an invalid FEN rejects the command and keeps the previous position
/// - `ucinewgame` resets to the start position, and `go` still answers with a `bestmove`
/// The positions are compared by board placement and side to move. The FENs were checked
/// against Stockfish: the start position and the position after e2e4 (Stockfish omits the
/// en-passant square there because no en-passant capture is possible; the engine writes it).
/// </summary>
public class PositionCommandTests
{
    private const string StartPlacementAndSide = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w";
    private const string AfterE2E4PlacementAndSide = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b";

    private static string PlacementAndSide(EngineOrchestrator orchestrator) =>
        string.Join(' ', orchestrator.CurrentPosition!.ToFEN().Split(' ').Take(2));

    [Fact]
    public void Fresh_engine_starts_from_the_start_position()
    {
        var orchestrator = new EngineOrchestrator();

        Assert.Equal(StartPlacementAndSide, PlacementAndSide(orchestrator));
    }

    [Fact]
    public void Illegal_move_rejects_the_whole_command_and_keeps_the_previous_position()
    {
        var output = new StringWriter();
        var orchestrator = new EngineOrchestrator();
        orchestrator.SetPosition("startpos", new[] { "e2e4" }, output);

        orchestrator.SetPosition("startpos", new[] { "e2e4", "e2e4", "d7d5" }, output);

        Assert.Equal(AfterE2E4PlacementAndSide, PlacementAndSide(orchestrator));
        Assert.Contains("info string Error: illegal move e2e4", output.ToString());
    }

    [Fact]
    public void Invalid_fen_rejects_the_command_and_keeps_the_previous_position()
    {
        var output = new StringWriter();
        var orchestrator = new EngineOrchestrator();
        orchestrator.SetPosition("startpos", new[] { "e2e4" }, output);

        orchestrator.SetPosition("this-is-not-a-fen", Array.Empty<string>(), output);

        Assert.Equal(AfterE2E4PlacementAndSide, PlacementAndSide(orchestrator));
        Assert.Contains("info string Error: invalid position, previous position kept", output.ToString());
    }

    [Fact]
    public void Ucinewgame_resets_to_the_start_position_and_go_still_answers()
    {
        var output = new StringWriter();
        var orchestrator = new EngineOrchestrator();
        orchestrator.SetPosition("startpos", new[] { "e2e4" }, output);

        orchestrator.UciNewGame();
        Assert.Equal(StartPlacementAndSide, PlacementAndSide(orchestrator));

        orchestrator.Go(new SearchLimits { Depth = 1 }, output);
        orchestrator.Stop();
        Assert.Contains("bestmove", output.ToString());
    }
}
