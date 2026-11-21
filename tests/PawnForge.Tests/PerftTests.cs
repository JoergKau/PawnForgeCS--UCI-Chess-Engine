using PawnForge.Core.Board;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Perft (performance test) counts for standard reference positions.
/// These validate move generation, make/unmake state restoration, castling,
/// en passant, promotions and pin handling in one shot. A wrong unmake or a
/// corrupted state stack breaks these counts immediately.
/// Reference values: chessprogramming.org/Perft_Results
/// </summary>
public class PerftTests
{
    [Fact]
    public void Startpos_Perft3()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Assert.Equal(20, TestHelper.Perft(pos, 1));
        Assert.Equal(400, TestHelper.Perft(pos, 2));
        Assert.Equal(8902, TestHelper.Perft(pos, 3));
    }

    [Fact]
    public void Startpos_Perft4()
    {
        var pos = Position.FromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Assert.Equal(197281, TestHelper.Perft(pos, 4));
    }

    [Fact]
    public void Kiwipete_Perft3()
    {
        // Castling-heavy position - validates castling generation and execution.
        var pos = Position.FromFEN("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        Assert.Equal(48, TestHelper.Perft(pos, 1));
        Assert.Equal(2039, TestHelper.Perft(pos, 2));
        Assert.Equal(97862, TestHelper.Perft(pos, 3));
    }

    [Fact]
    public void Position3_Perft4()
    {
        // En-passant-heavy position - validates EP generation and pin handling.
        var pos = Position.FromFEN("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1");
        Assert.Equal(14, TestHelper.Perft(pos, 1));
        Assert.Equal(191, TestHelper.Perft(pos, 2));
        Assert.Equal(2812, TestHelper.Perft(pos, 3));
        Assert.Equal(43238, TestHelper.Perft(pos, 4));
    }

    [Fact]
    public void Position4_Perft3()
    {
        // Promotion-heavy position with checks and castling interactions.
        var pos = Position.FromFEN("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1");
        Assert.Equal(6, TestHelper.Perft(pos, 1));
        Assert.Equal(264, TestHelper.Perft(pos, 2));
        Assert.Equal(9467, TestHelper.Perft(pos, 3));
    }

    [Fact]
    public void Position5_Perft3()
    {
        var pos = Position.FromFEN("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8");
        Assert.Equal(44, TestHelper.Perft(pos, 1));
        Assert.Equal(1486, TestHelper.Perft(pos, 2));
        Assert.Equal(62379, TestHelper.Perft(pos, 3));
    }
}
