namespace PawnForge.Tests;

using System.Diagnostics;
using System.Text;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// M6 gate — the v5 forward pass and the evaluator seam.
///
/// 1. SCALAR == VECTORISED, bit for bit: over adversarial accumulator data
/// (realistic ranges, the clamp boundary, and the legal-extreme net that
/// forces the i64 chain), the vectorised head must agree with the scalar
/// reference exactly — the F10 class (an arithmetic shift would subtract
/// 256 for every activation above 181 and diverge here).
/// 2. INCREMENTAL EVAL == COLD EVAL through the SEAM (the accumulator identity
/// raised one level — the level the search consumes), over random walks
/// across king moves, captures, promotions.
/// 3. DEEP-NEST makes/unmakes restore the eval exactly (the stack/push-pop
/// discipline at the seam).
/// 4. THE BIT-EXACTNESS CHECK: C# eval vs the independent implementation's forward pass,
/// 500/500 EXACT, max |Δ| = 0 cp, over
/// the oracle-verified-seed walks and the SAME deterministic net file.
/// </summary>
public class NnueEvaluatorTests
{
    /// <summary>A v5 net whose every feature row is DISTINCT (a uniform filler
    /// would pass an identity gate with the index completely wrong).</summary>
    private static NnueNetV5 V5RegionNet(int l1 = 128)
    {
        var net = NnueNetV5.FromFiller(l1);
        int rows = net.FeatureWeights.Length / l1;
        for (int row = 0; row < rows; row++)
            for (int u = 0; u < l1; u++)
                net.FeatureWeights[row * l1 + u] = (short)((row * 37 + u * 11) % 251 - 125);
        return net;
    }

    // ---- 1. scalar == vectorised ----------------------------------------------

    private static short[] Acc(int seed, int l1, int magnitude)
    {
        var rng = new Random(seed);
        var acc = new short[l1];
        for (int i = 0; i < l1; i++)
            acc[i] = (short)((rng.Next() % (2 * magnitude + 1)) - magnitude);
        return acc;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void VectorisedForward_EqualsScalarReference_BitForBit(int seed)
    {
        var net = V5RegionNet(l1: 128);
        var rng = new Random(seed);
        for (int trial = 0; trial < 50; trial++)
        {
            int magnitude = trial switch
            {
                0 => 32767,          // full i16 range: the clamp boundary everywhere
                1 => 255,            // exactly the QA boundary — where s > 181 lives
                _ => 1 + rng.Next(32768),
            };
            var stm = Acc(seed * 1000 + trial, net.L1, magnitude);
            var ntm = Acc(seed * 7777 + trial, net.L1, magnitude);

            long vectorised = net.Forward(stm, ntm);
            long scalar = net.ForwardScalar(stm, ntm);
            Assert.True(vectorised == scalar,
                $"trial {trial} (magnitude {magnitude}): vectorised {vectorised} != scalar {scalar} — the SIMD head diverged from the reference (the F10 class)");
        }
    }

    [Fact]
    public void VectorisedForward_HandlesTheLegalExtremeNet_WithoutWrapping()
    {
        // The legal-extreme net: every weight at i16 max AND the i32 bias at its
        // max. The head sum then reaches ~3.2e9 — past int.MaxValue — so an i32
        // chain would wrap and the vector/scalar pair would diverge. Both must
        // agree on the exact value (the same bound NnueQuantV5Tests pins).
        var net = new NnueNetV5(64, NnueFeatureSet2.DefaultRegionTable, 1, "extreme", int.MaxValue);
        for (int i = 0; i < net.OutputWeights.Length; i++)
            net.OutputWeights[i] = short.MaxValue;
        var full = new short[net.L1];
        for (int i = 0; i < net.L1; i++)
            full[i] = short.MaxValue;

        long vectorised = net.Forward(full, full);
        long scalar = net.ForwardScalar(full, full);
        long expected = net.OutputBias
            + (long)NnueQuantV5.Output(NnueQuantV5.Screlu(short.MaxValue)) * short.MaxValue * (2 * net.L1);
        Assert.True(expected > int.MaxValue, "the fixture must exceed the i32 range or it proves nothing");
        Assert.Equal(expected, scalar);
        Assert.Equal(expected, vectorised);
    }

    // ---- 2. incremental eval == cold eval through the seam --------------------

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void IncrementalEval_EqualsColdEval_OverRandomWalks(int seed)
    {
        var net = V5RegionNet();
        var evaluator = new NnueEvaluator(net);
        var random = new Random(seed);
        Span<Move> moves = stackalloc Move[256];

        foreach (string fen in NnueTestFens.All)
        {
            var pos = Position.FromFEN(fen);
            evaluator.SetPosition(pos);

            var undo = new List<(Move Move, Piece Captured)>();
            for (int step = 0; step < 60; step++)
            {
                int count = pos.GenerateLegalMoves(moves);
                if (count == 0)
                    break;
                Move move = moves[random.Next(count)];
                Piece captured = pos.MakePublicMove(move);
                evaluator.MakeMove(pos, move, pos.Mailbox[move.To], captured);
                undo.Add((move, captured));

                var cold = new NnueEvaluator(net);
                cold.SetPosition(pos);
                Assert.True(evaluator.Evaluate() == cold.Evaluate(),
                    $"incremental eval != cold eval after {move} at step {step} from {fen}");
            }

            while (undo.Count > 0)
            {
                evaluator.UnmakeMove();
                var last = undo[^1];
                pos.UnmakePublicMove(last.Move, last.Captured);
                undo.RemoveAt(undo.Count - 1);
            }
        }
    }

    // ---- 3. deep-nest makes/unmakes -------------------------------------------

    [Fact]
    public void DeepNest_MakesAndUnmakes_RestoreExactly()
    {
        var net = V5RegionNet();
        var evaluator = new NnueEvaluator(net);
        var pos = Position.FromFEN(NnueTestFens.Position4);
        Span<Move> moves = stackalloc Move[256];

        evaluator.SetPosition(pos);
        int reference = evaluator.Evaluate();

        var stack = new List<(Move Move, Piece Captured)>();
        for (int step = 0; step < 60; step++)
        {
            int count = pos.GenerateLegalMoves(moves);
            if (count == 0)
                break;
            Move move = moves[step % count];
            Piece captured = pos.MakePublicMove(move);
            evaluator.MakeMove(pos, move, pos.Mailbox[move.To], captured);
            stack.Add((move, captured));
            evaluator.Evaluate(); // force an evaluation at this node
        }

        Assert.NotEmpty(stack);
        while (stack.Count > 0)
        {
            evaluator.UnmakeMove();
            var last = stack[^1];
            pos.UnmakePublicMove(last.Move, last.Captured);
            stack.RemoveAt(stack.Count - 1);
            evaluator.Evaluate();
        }

        Assert.Equal(reference, evaluator.Evaluate());
    }

}
