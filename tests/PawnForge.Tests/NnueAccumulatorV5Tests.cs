namespace PawnForge.Tests;

using System.Collections.Generic;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// M5 gate — the scheme-2 accumulator's correctness gate (ported from
/// the previous generation's NnueAccumulatorV5Tests, adapted to the CS board API).
///
/// The scheme-2 accumulator is INCREMENTAL with lazy own-king refresh (the
/// scheme-2 index is stm-independent — pinned by M2's bit-exactness check). The
/// identity that protects this class is
///
/// after an arbitrary random walk, the INCREMENTALLY UPDATED accumulator
/// must equal a COLD-START (full-refresh) one, bit for bit.
///
/// That covers the whole failure surface:
/// * the incremental path itself (dirty-piece enumeration, add vs subtract,
/// indices computed under the POST-make own-king square),
/// * the lazy refresh path (active-feature enumeration + row adds) whenever
/// a king moves mid-walk — the two paths must agree at the seam,
/// * the push/pop stack restoring a VALID (value, stale) pair — a bug there
/// hands back a stale-flagged-but-valid or valid-flagged-but-stale pair
/// and the engine then evaluates from an accumulator that belongs to
/// another position,
/// * null moves, which change no pieces and invalidate NOTHING, yet still
/// push/pop the stack,
/// * region changes (own-king moves) — the one case that must force a
/// refresh.
///
/// A net that exercises several regions and both mirror states is required: a
/// filler net whose rows are all identical would pass this gate with the index
/// completely wrong. `V5RegionNet` therefore fills every row distinctly.
/// </summary>
public class NnueAccumulatorV5Tests
{
    /// <summary>A v5 net whose every feature row is DISTINCT and whose bias makes
    /// the accumulator's sign carry information — so a wrong row offset, a wrong
    /// region or a wrong mirror changes the accumulator and cannot cancel out.</summary>
    private static NnueNetV5 V5RegionNet(int l1 = 128)
    {
        var net = NnueNetV5.FromFiller(l1);
        int rows = net.FeatureWeights.Length / l1;
        for (int row = 0; row < rows; row++)
            for (int u = 0; u < l1; u++)
                net.FeatureWeights[row * l1 + u] = (short)((row * 37 + u * 11) % 251 - 125);
        return net;
    }

    private static void AssertIncrementalEqualsColdStart(NnueAccumulatorV5 incremental, Position pos, string context)
    {
        var cold = new NnueAccumulatorV5(incremental.Net);
        cold.SetPosition(pos);

        foreach (int pov in new[] { NnueFeatureSet2.WhitePov, NnueFeatureSet2.BlackPov })
        {
            var a = incremental.GetAccumulator(pos, pov).ToArray();
            var b = cold.GetAccumulator(pos, pov).ToArray();
            Assert.True(a.AsSpan().SequenceEqual(b),
                $"incrementally updated accumulator != cold start for POV {pov} {context}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Incremental_EqualsColdStart_OverRandomWalks(int seed)
    {
        var net = V5RegionNet();
        var incremental = new NnueAccumulatorV5(net);
        var random = new Random(seed);
        // Once per test, NOT per ply (CA2014: xunit's worker stack is small).
        Span<Move> moves = stackalloc Move[256];

        foreach (string fen in NnueTestFens.All)
        {
            var pos = Position.FromFEN(fen);
            incremental.SetPosition(pos);
            AssertIncrementalEqualsColdStart(incremental, pos, $"at setup of {fen}");
            Assert.False(incremental.IsStale(NnueFeatureSet2.WhitePov), "setup left the white POV stale");
            Assert.False(incremental.IsStale(NnueFeatureSet2.BlackPov), "setup left the black POV stale");

            var undo = new List<(Move Move, Piece Captured)>();
            for (int step = 0; step < 80; step++)
            {
                int count = pos.GenerateLegalMoves(moves);
                if (count == 0)
                    break;
                Move move = moves[random.Next(count)];

                bool whiteKingMoved = pos.Mailbox[move.From] is Piece.WK;
                bool blackKingMoved = pos.Mailbox[move.From] is Piece.BK;
                int whiteFrameBefore = net.Features.Frame(pos, NnueFeatureSet2.WhitePov);
                int blackFrameBefore = net.Features.Frame(pos, NnueFeatureSet2.BlackPov);

                Piece captured = pos.MakePublicMove(move);
                incremental.MakeMove(pos);
                undo.Add((move, captured));

                // Exactly a POV whose own king moved AND changed the feature
                // frame (region + mirror) goes stale — and nothing else. A king
                // move inside the same frame changes no row index, so it is a
                // normal incremental update. Asserted BEFORE the identity check,
                // because reading the accumulator refreshes a stale POV lazily
                // and would clear the flag.
                bool whiteFrameChanged = whiteKingMoved &&
                    net.Features.Frame(pos, NnueFeatureSet2.WhitePov) != whiteFrameBefore;
                bool blackFrameChanged = blackKingMoved &&
                    net.Features.Frame(pos, NnueFeatureSet2.BlackPov) != blackFrameBefore;
                Assert.Equal(whiteFrameChanged, incremental.IsStale(NnueFeatureSet2.WhitePov));
                Assert.Equal(blackFrameChanged, incremental.IsStale(NnueFeatureSet2.BlackPov));

                // THE identity, at every node of the walk.
                AssertIncrementalEqualsColdStart(incremental, pos, $"after {move} at step {step} from {fen}");
            }

            // Unwind the whole walk with unmake: the stack must return the
            // accumulator to exactly the setup state, step by step.
            int made = 0;
            while (undo.Count > 0 && made < 80)
            {
                var last = undo[^1];
                pos.UnmakePublicMove(last.Move, last.Captured);
                incremental.UnmakeMove();
                undo.RemoveAt(undo.Count - 1);
                made++;
                AssertIncrementalEqualsColdStart(incremental, pos, $"after unmake {made} in {fen}");
            }
            Assert.Equal(0, incremental.StackPly);
        }
    }

    /// <summary>Step 2 of how-to-improve-with-dotnet11.md replaced the accumulator's
    /// Span-shaped row add/subtract with ref arithmetic (<c>Vector.LoadUnsafe</c> /
    /// <c>StoreUnsafe</c>). The random-walk gate above only exercises L1 = 128, so this
    /// theory pins the row primitive at EVERY legal L1 (64..2048, multiple of 16) —
    /// including 144 and 160, which are not multiples of a 32-lane vector and are the
    /// reason the loop bound is <c>offset + width &lt;= l1</c> instead of <c>offset &lt; l1</c>.
    /// The identity is still incremental == cold start, so a wrong chunk count, a wrong
    /// row offset or an out-of-bounds write shows up as a mismatch (or a crash).</summary>
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(144)]
    [InlineData(160)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void IncrementalPath_IsBitExact_ForEveryLegalL1(int l1)
    {
        var net = V5RegionNet(l1);
        Assert.Equal(l1, net.L1);

        var acc = new NnueAccumulatorV5(net);
        var pos = Position.FromFEN(NnueTestFens.UhoOpening);
        var random = new Random(l1);
        // Once per test, not per step (CA2014).
        Span<Move> moves = stackalloc Move[256];

        acc.SetPosition(pos);
        AssertIncrementalEqualsColdStart(acc, pos, $"setup at L1={l1}");

        // Six make/unmake pairs: enough to hit quiet moves (add + subtract rows),
        // captures (dirty removals) and — depending on the walk — a king move
        // (lazy refresh). Unmaking exercises the stack restore at the same width.
        for (int step = 0; step < 6; step++)
        {
            int count = pos.GenerateLegalMoves(moves);
            if (count == 0)
                break;

            Move move = moves[random.Next(count)];
            Piece captured = pos.MakePublicMove(move);
            acc.MakeMove(pos);
            AssertIncrementalEqualsColdStart(acc, pos, $"step {step} at L1={l1} after {move}");

            pos.UnmakePublicMove(move, captured);
            acc.UnmakeMove();
            AssertIncrementalEqualsColdStart(acc, pos, $"step {step} at L1={l1} after unmake");
        }
    }

    /// <summary>Region changes must be handled, and this must be DETERMINISTIC —
    /// the the previous generation original noted that asserting a random walk happened to
    /// move a king is flaky by construction. Here the king move is chosen
    /// explicitly and the region change is asserted rather than hoped for.</summary>
    [Fact]
    public void AKingMove_ChangesTheRegion_AndTheAccumulatorFollows()
    {
        var net = V5RegionNet();
        var lazy = new NnueAccumulatorV5(net);
        var pos = Position.FromFEN(NnueTestFens.CastlingRook);

        lazy.SetPosition(pos);
        int regionBefore = net.Features.Region(pos, NnueFeatureSet2.WhitePov);
        AssertIncrementalEqualsColdStart(lazy, pos, "at setup");

        // King e1 -> g1: a1..h1 map to regions 0,1,2,3,3,2,1,0, so e1 (3) -> g1 (1).
        Move castling = TestHelper.FindMove(pos, "e1", "g1");
        Piece captured = pos.MakePublicMove(castling);
        lazy.MakeMove(pos);
        int regionAfter = net.Features.Region(pos, NnueFeatureSet2.WhitePov);

        Assert.NotEqual(regionBefore, regionAfter);
        Assert.Equal(1, regionAfter);
        Assert.True(lazy.IsStale(NnueFeatureSet2.WhitePov), "an own-king move must mark the white POV stale");
        Assert.False(lazy.IsStale(NnueFeatureSet2.BlackPov), "the opponent's king move must NOT mark the black POV stale");
        AssertIncrementalEqualsColdStart(lazy, pos, "after O-O (king e1 -> g1, region 3 -> 1)");

        pos.UnmakePublicMove(castling, captured);
        lazy.UnmakeMove();
        Assert.Equal(regionBefore, net.Features.Region(pos, NnueFeatureSet2.WhitePov));
        AssertIncrementalEqualsColdStart(lazy, pos, "after unmake");
    }

    /// <summary>A null move changes no piece and — because the scheme-2 index is
    /// stm-INDEPENDENT — must invalidate NEITHER POV. The stack must still
    /// push/pop so the state is restored bit-for-bit (getting THAT wrong is the
    /// board-desync class one level up: an accumulator belonging to another
    /// position).</summary>
    /// <summary>A king move INSIDE the same feature frame must stay incremental:
    /// the frame (region + mirror) is what selects every row index, so e1 -> f1
    /// changes no index at all and a full rebuild would be wasted work. The
    /// identity gate still runs, so a wrong incremental update cannot pass.</summary>
    [Fact]
    public void KingMoveInsideTheSameFrame_StaysIncremental_AndIsBitExact()
    {
        var net = V5RegionNet();
        var acc = new NnueAccumulatorV5(net);
        // a5 -> b5: rank 5 is one all-files region (4) and both files are in
        // the a-d mirror half, so the frame is unchanged.
        var pos = Position.FromFEN("7k/8/8/K7/8/8/8/8 w - - 0 1");
        Stockfish.VerifyFen("7k/8/8/K7/8/8/8/8 w - - 0 1");
        acc.SetPosition(pos);
        int frameBefore = net.Features.Frame(pos, NnueFeatureSet2.WhitePov);

        Move move = TestHelper.FindMove(pos, "a5", "b5");
        Piece captured = pos.MakePublicMove(move);
        acc.MakeMove(pos);

        Assert.Equal(frameBefore, net.Features.Frame(pos, NnueFeatureSet2.WhitePov));
        Assert.False(acc.IsStale(NnueFeatureSet2.WhitePov),
            "a king move inside the same frame must not force a full refresh");
        AssertIncrementalEqualsColdStart(acc, pos, "after a5-b5 (same frame)");

        pos.UnmakePublicMove(move, captured);
        acc.UnmakeMove();
        AssertIncrementalEqualsColdStart(acc, pos, "after unmake a5-b5");
    }

    [Fact]
    public void NullMove_InvalidatesNothing_AndUnmakeRestoresExactly()
    {
        var net = V5RegionNet();
        var lazy = new NnueAccumulatorV5(net);
        var pos = Position.FromFEN(NnueTestFens.UhoOpening);

        lazy.SetPosition(pos);
        short[] beforeW = lazy.GetAccumulator(pos, NnueFeatureSet2.WhitePov).ToArray();
        short[] beforeB = lazy.GetAccumulator(pos, NnueFeatureSet2.BlackPov).ToArray();
        int plyBefore = lazy.StackPly;

        lazy.MakeNullMove();
        Assert.False(lazy.IsStale(NnueFeatureSet2.WhitePov), "a null move must NOT invalidate the white POV");
        Assert.False(lazy.IsStale(NnueFeatureSet2.BlackPov), "a null move must NOT invalidate the black POV");

        // No piece changed, so the values are unchanged; the point of the test is
        // that they were not rebuilt to get here.
        short[] afterW = lazy.GetAccumulator(pos, NnueFeatureSet2.WhitePov).ToArray();
        short[] afterB = lazy.GetAccumulator(pos, NnueFeatureSet2.BlackPov).ToArray();
        Assert.True(beforeW.AsSpan().SequenceEqual(afterW), "a null move changed the white accumulator");
        Assert.True(beforeB.AsSpan().SequenceEqual(afterB), "a null move changed the black accumulator");

        lazy.UnmakeNullMove();
        Assert.Equal(plyBefore, lazy.StackPly);
        short[] restoredW = lazy.GetAccumulator(pos, NnueFeatureSet2.WhitePov).ToArray();
        short[] restoredB = lazy.GetAccumulator(pos, NnueFeatureSet2.BlackPov).ToArray();

        Assert.True(beforeW.AsSpan().SequenceEqual(restoredW), "white accumulator not restored exactly across a null move");
        Assert.True(beforeB.AsSpan().SequenceEqual(restoredB), "black accumulator not restored exactly across a null move");
    }
}
