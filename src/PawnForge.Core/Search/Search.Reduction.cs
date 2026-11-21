// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · LMR schedule, re-search depth, singular & cut-node reductions
// ═══════════════════════════════════════════════════════════════════════════════

using System;

namespace PawnForge.Core.Search;

public partial class Search
{
    /// <summary>
    /// Computes the depth for the full-depth zero-window re-search after a reduced-search
    /// fail-high. When enabled and the original LMR reduction was large, shaves 1-2 plies off
    /// newDepth (a move that wanted a big reduction is unlikely to need full re-verification).
    /// The result is floored at (newDepth - reduction) so the re-search is never shallower than
    /// the reduced search it is trying to improve on. When disabled, always returns newDepth.
    /// </summary>
    internal static int ComputeReSearchDepth(int newDepth, int reduction, bool enabled)
    {
        if (!enabled) return newDepth;
        int reSearchDepth = newDepth;
        if (reduction >= 3) reSearchDepth--;
        if (reduction >= 5 && newDepth > 2) reSearchDepth--;
        int minDepth = newDepth - reduction;
        if (reSearchDepth < minDepth) reSearchDepth = minDepth;
        return reSearchDepth;
    }

    /// <summary>
    /// Cut-node-aware Late Move Reduction adjustment (whole-ply variant). When the current node
    /// is predicted to fail high (<paramref name="cutNode"/>) and the feature is enabled, the LMR
    /// reduction is increased by one ply: such nodes only need to find a single refuting move, so
    /// late moves can be searched more shallowly. When disabled (or at non-cut nodes) the reduction
    /// is returned unchanged.
    /// NOTE: the production cut-node adjustment lives INLINE in the Negamax move loop's LMR block
    /// (Term 4, applied in 1024ths before the final division); this whole-ply helper is retained
    /// because it is exercised by <c>CutNodeReductionTests</c>. Keep both in sync.
    /// </summary>
    internal static int ApplyCutNodeReduction(int reduction, bool cutNode, bool enabled)
    {
        if (enabled && cutNode) return reduction + 1;
        return reduction;
    }

    // Margins (centipawns, below singularBeta) at which a singular move earns a second / third
    // ply of extension. Deliberately generous so triple extensions are rare; tunable.
    private const int SingularDoubleMargin = 30;
    private const int SingularTripleMargin = 120;

    // the reference's HindsightExtensionLimit
    //: a prior reduction at or above this threshold earns the ply back
    // when the combined eval fell below 0 (only read when UseExtensionTrio + UseTrioHindsight
    // are on; the legacy unconditional pair uses its own thresholds).
    private const int HindsightMinReduction = 3;

    /// <summary>
    /// Magnitude of the singular extension. Returns 0 when the move is not singular
    /// (<paramref name="score"/> did not fall below <paramref name="singularBeta"/>). When it is
    /// singular, returns 1; and if double/triple extensions are enabled, 2 or 3 when the score
    /// falls a further <paramref name="doubleMargin"/> / <paramref name="tripleMargin"/> below
    /// singularBeta (the move is so forced that extending further down its line pays off).
    /// </summary>
    internal static int ComputeSingularExtension(
        int score, int singularBeta, bool doubleEnabled, int doubleMargin, int tripleMargin)
    {
        if (score >= singularBeta) return 0;   // not singular
        if (!doubleEnabled) return 1;          // singular, single-ply extension
        int ext = 1;
        if (score < singularBeta - doubleMargin) ext = 2;
        if (score < singularBeta - tripleMargin) ext = 3;
        return ext;
    }

    // Precomputed Late Move Reduction base table, indexed [remainingDepth][moveNumber].
    // Built once at type load. The reduction grows with the product of the logarithms of
    // depth and move index, so late moves at high depth are reduced substantially while
    // early moves and shallow nodes are barely reduced. Dimensions are capped; callers clamp
    // their indices via LmrBaseReduction. The scaling constants are initial values and are
    // deliberately aggressive and tunable: the divisor is lowered until the measured effective
    // branching factor approaches the target, with WAC watched as the tactical guardrail.
    private const int LmrTableSize = 64;
    private static readonly int[][] _lmrReductionTable = BuildLmrReductionTable();

    private static int[][] BuildLmrReductionTable()
    {
        var table = new int[LmrTableSize][];
        for (int d = 0; d < LmrTableSize; d++)
        {
            table[d] = new int[LmrTableSize];
            for (int m = 0; m < LmrTableSize; m++)
            {
                if (d == 0 || m == 0)
                {
                    table[d][m] = 0;
                    continue;
                }
                // 1.0 + ln(depth) * ln(moveNumber) / 1.75 (aggressive: reduces more than the
                // legacy formula at moderate/high depth; tuned toward the target EBF).
                double r = 1.0 + Math.Log(d) * Math.Log(m) / 1.75;
                table[d][m] = (int)r;
            }
        }
        return table;
    }

    /// <summary>
    /// Base Late Move Reduction for a given remaining <paramref name="depth"/> and 1-based
    /// <paramref name="moveNumber"/>, read from the precomputed table with both indices clamped
    /// into range. Grows monotonically (non-decreasing) in both arguments. This is only the
    /// base amount; callers apply the improving / PV / history / cut-node adjustments and the
    /// final [0, newDepth] clamp on top.
    /// </summary>
    internal static int LmrBaseReduction(int depth, int moveNumber)
    {
        int d = depth < 0 ? 0 : (depth >= LmrTableSize ? LmrTableSize - 1 : depth);
        int m = moveNumber < 0 ? 0 : (moveNumber >= LmrTableSize ? LmrTableSize - 1 : moveNumber);
        return _lmrReductionTable[d][m];
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    // LMR v2 : the reference's SPSA-tuned
    // fixed-point reduction architecture.
    // ═══════════════════════════════════════════════════════════════════════════════════
    // The reference calculateLmrTable and
    // search LMR block. the reference maintains TWO separate base tables — one for
    // quiet moves and one for noisy moves (captures) — each built from its own (base,
    // divisor) pair and stored in 1024ths-of-a-ply fixed point so the modulating terms can
    // adjust the reduction with sub-ply precision (the key lesson of the Term-1
    // investigation: a whole-ply term at +1.0 blew up on unstable-PV positions, while
    // the reference's equivalent term is only +275/1024 ≈ +0.27 ply).
    //
    // the reference's tuned values: quiet base 92/100, quiet divisor 309/100;
    // noisy ("cap") base 93/100, noisy divisor 307/100. Formula per cell:
    // reduction(plies) = base + ln(moveNumber) * ln(depth) / divisor
    // stored as round(reduction * 1024). Unlike the v1 table there is NO +1.0 additive
    // base — the modulating terms supply the extra weight, which is why the v2 total
    // lands differently from the v1 `1.0 + ln·ln/1.75` shape.
    //
    // Table dimensions mirror the v1 table (64×64, indices clamped on read). We write our
    // own C# implementation per the project rules (no code copying).
    // ── BASE-AGGRESSION RE-TUNE (2026-09-07, step 2) ─────────────────────────
    // The gate result FALSIFIED the assumption that porting the reference's base verbatim would
    // lower EBF: EBF regressed 2.14 → 2.20 and depth 20.8 → 20.0. Root cause :
    // the reference's base is ~2 plies GENTLER than PawnForge's v1 base at deep/late nodes
    // (d20/m16: the reference 3.61 vs v1 5.75), and the reference only recoups that with reducedDepth-gated
    // pruning that is DISABLED here (it broke the mate battery). So we shipped the reference's
    // gentle base without the pruning that pays for it → a wider tree.
    //
    // Fix: keep the v2 fixed-point ARCHITECTURE (sub-ply, stable, WAC-positive) but restore
    // aggression by LOWERING THE DIVISOR (not raising the base constant). The divisor scales
    // the ln(d)·ln(m) growth term, so this adds reduction precisely on late moves at high depth
    // — exactly where the EBF gap is — while leaving early/shallow moves (tactically sensitive)
    // almost untouched. Raising the base constant would do the opposite and endanger tactics.
    //
    // the reference values were 3.09 (quiet) / 3.07 (noisy). New target 2.50 lands the base at
    // ~4.1 ply at d20/m16 and ~4.6 ply at d30/m30 — squarely between the reference (3.6/4.66) and the
    // blunt v1 base (5.7/7.6), leaning reference-ward so the sub-ply modulating terms still
    // dominate the fine structure. If the SPRT vs the pre-restructure baseline shows a tactical
    // regression, sweep back up toward 2.75/3.09; if EBF is still high, sweep down toward 2.2.
    // (Noisy captures are only reduced when UseLmrNoisyMoves is ON — OFF by default — so the
    // noisy divisor is inert in the shipped config; kept in lockstep for the experiment.)
    private const int LmrV2BaseQuiet = 92;       // ×100 (0.92 plies) — reference base, unchanged
    private const int LmrV2DivisorQuiet = 250;   // ×100 (2.50) — RE-TUNED from the reference's 3.09 (tuning pass)
    private const int LmrV2BaseNoisy = 93;       // ×100 (0.93 plies) — reference base, unchanged
    private const int LmrV2DivisorNoisy = 250;   // ×100 (2.50) — RE-TUNED from the reference's 3.07 (tuning pass)
    private const int LmrV2Unit = 1024;

    private static readonly int[][] _lmrV2QuietTable = BuildLmrV2Table(LmrV2BaseQuiet, LmrV2DivisorQuiet);
    private static readonly int[][] _lmrV2NoisyTable = BuildLmrV2Table(LmrV2BaseNoisy, LmrV2DivisorNoisy);

    private static int[][] BuildLmrV2Table(int baseHundredths, int divisorHundredths)
    {
        var table = new int[LmrTableSize][];
        double b = baseHundredths / 100.0;
        double div = divisorHundredths / 100.0;
        for (int d = 0; d < LmrTableSize; d++)
        {
            table[d] = new int[LmrTableSize];
            for (int m = 0; m < LmrTableSize; m++)
            {
                if (d == 0 || m == 0)
                {
                    table[d][m] = 0;
                    continue;
                }
                double r = b + Math.Log(m) * Math.Log(d) / div;
                table[d][m] = (int)Math.Round(r * LmrV2Unit); // 1024ths of a ply
            }
        }
        return table;
    }

    /// <summary>
    /// LMR v2 base reduction in 1024ths of a ply for a given remaining <paramref name="depth"/>
    /// and 1-based <paramref name="moveNumber"/>, selected from the quiet or noisy (capture)
    /// table per the reference's two-table design. Indices are clamped into range exactly like the v1
    /// table. The caller applies the fixed-point modulating terms and the final
    /// [0, newDepth] clamp.
    /// </summary>
    internal static int LmrV2BaseReduction(bool noisy, int depth, int moveNumber)
    {
        int d = depth < 0 ? 0 : (depth >= LmrTableSize ? LmrTableSize - 1 : depth);
        int m = moveNumber < 0 ? 0 : (moveNumber >= LmrTableSize ? LmrTableSize - 1 : moveNumber);
        return (noisy ? _lmrV2NoisyTable : _lmrV2QuietTable)[d][m];
    }
}
