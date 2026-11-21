using System.IO;
using PawnForge.Core.Board;
using PawnForge.Core.TranspositionTable;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Regression tests for predecessor bug 150 (2026-11-18), found through the
/// KnightByte migration's Phase 5 step 0 oracle transcript capture (see the
/// predecessor bug log):
///
/// - Bug 150: the `setoption Hash` callback handed the raw parsed value to
///   TTable.Resize. A value far above the option's declared maximum overflowed
///   the `(int)alignedSize` cast in Resize — a small buffer was allocated and
///   the zeroing loop wrote far past its end (native heap corruption,
///   0xC0000374 / 0xC0000005, with a delayed manifestation that killed the
///   process seconds after the triggering command).
///
/// The fix has two layers, both pinned here:
/// - the Hash callback clamps into the declared 1..1024 MB range
///   (EngineOrchestrator.ClampHashSize);
/// - TTable.Resize and the TTable constructor validate their argument BEFORE
///   touching any state, so an out-of-range size throws a managed exception
///   and leaves the existing table alive and usable.
/// </summary>
public class PredecessorBug150Tests
{
    // ───────────────────────────────────────────────────────────────────────────
    // Layer 1: the option-layer clamp (EngineOrchestrator.ClampHashSize)
    // ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(999999, 1024)]   // the crash trigger: far above the declared maximum
    [InlineData(1025, 1024)]     // just above the declared maximum
    [InlineData(1024, 1024)]     // the declared maximum passes unchanged
    [InlineData(256, 256)]       // the default passes unchanged
    [InlineData(1, 1)]           // the declared minimum passes unchanged
    [InlineData(0, 1)]           // below the declared minimum clamps up
    [InlineData(-50, 1)]         // negative input clamps up
    public void ClampHashSize_ClampsIntoTheDeclaredRange(int input, int expected)
    {
        Assert.Equal(expected, EngineOrchestrator.ClampHashSize(input, 1, 1024));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Layer 2: TTable validates its argument before touching any state
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resize_RejectsAnOutOfRangeSize_AndKeepsTheTableUsable()
    {
        var table = new TTable(4);
        table.Store(0x1234, value: 137, depth: 5, NodeType.Exact, new Move(12, 28), currentAge: 1);

        // Both crash triggers of the raw setoption path must throw managed
        // exceptions now — an unmanaged crash would take the test host down.
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Resize(999999));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Resize(0));

        // The rejected resize must not have touched the existing table.
        bool found = table.Probe(0x1234, out var entry);
        Assert.True(found);
        Assert.Equal(137, entry.Value);
    }

    [Fact]
    public void Resize_AcceptsTheDeclaredBoundaries()
    {
        var table = new TTable(4);
        table.Resize(1);
        Assert.Equal(1, table.GetSizeMB());
        table.Resize(1024);
        Assert.Equal(1024, table.GetSizeMB());
    }

    [Fact]
    public void Constructor_RejectsAnOutOfRangeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TTable(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TTable(-16));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // End to end: the UCI surface survives the crash trigger
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetOption_HashFarAboveTheMaximum_KeepsTheEngineAlive()
    {
        const string commands = "uci\nsetoption name Hash value 999999\nisready\nquit\n";
        var input = new StringReader(commands);
        var output = new StringWriter();

        var handler = new UciHandler();
        handler.Run(input, output);

        string outStr = output.ToString();
        Assert.Contains("uciok", outStr);
        Assert.Contains("readyok", outStr);
        Assert.DoesNotContain("Error", outStr);
    }
}
