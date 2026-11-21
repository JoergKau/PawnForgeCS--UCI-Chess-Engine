namespace PawnForge.Core.Evaluation;

// ============================================================================
// NnueQuantV5.cs — the fc0-LESS quantization chain for the v5 net format / scheme 2
// ----------------------------------------------------------------------------
// Ported 1:1 from the previous generation.
// Decision of record:
// Scheme 2 has ONE output head (2·L1 -> 1) and NO fc0, so the v4 tail (Fc0Shift /
// QB0 / K1) describes a tensor that no longer exists. These constants are
// DERIVED ONCE and FROZEN; the bit-exactness check (M2/M6) is the referee. Do not
// re-derive them in a second place ("do not invent them twice").
//
// THE v5 CHAIN
// ------------
// a in [-32768, 32767] i16 accumulator (feature weights + biases, i16)
// s = clamp(a, 0, 255) QA = 255
// s2 = (s * s) >> 8 ScreluShift = 8 -> [0, 254]
// o = clamp(s2 >> OUTPUT_SHIFT, 0, OUTPUT_CAP) -> [0, 254]
// out = SUM_i o_i * w_i + b i64 accumulation (see the bound below)
// cp = (out * EvalScaleMul) >> EvalScaleShift int64 multiply
//
// THE DERIVATION (why each constant is what it is)
// ------------------------------------------------
// The float net publishes out_f = SS * (SUM_i x_i * W1_i) + b1 in centipawns,
// where x_i = clamp(acc_f_i, 0, 1)^2 and SS = trainer SCORE_SCALE = 400.
//
// — quantize the feature weights at QA, so acc_q ~ QA * acc_f:
// s2 = (QA^2 / 2^ScreluShift) * x (clamping acc_q at QA is clamping
// x at 1 — the clamp IS the saturate)
// — there is no fc0 left to absorb a scale factor, so the ONLY gain left
// between x and the output layer is the OutputShift:
// o = (QA^2 / 2^(ScreluShift + OutputShift)) * x = GAIN * x
// and GAIN is then folded into the fc1 weight scale K1 (below). OutputShift
// is therefore free to be chosen purely for PRECISION, not for gain.
//
// — choose OutputShift = 0:
// * OutputShift >= 8 annihilates the signal: s2 <= 254, so s2 >> 8 == 0.
// The usable range is 0..7.
// * OutputShift = 0 keeps the full 8-bit activation range (o spans 0..254),
// which is the maximum available precision.
// So GAIN = QA^2 / 2^8 = 254.00390625. (The clamp-parity subtlety: the
// realisable maximum is 65025>>8 = 254, one quant unit below GAIN — absorbed
// by K1 below.)
//
// — pick K1 so the int chain reproduces out_f EXACTLY in cp:
// K1 = SS * 2^S / (M * GAIN)
// With M = EvalScaleMul = 1600, S = EvalScaleShift = 20:
// K1 = 400 * 2^20 / (1600 * 254.00390625) = 1032.047120
// The fc1 BIAS is not multiplied by GAIN, so it keeps the same scale:
// QB1 = SS * 2^S / M = 262144
//
// K1 / QB1 are EXPORT-side constants (the trainer derives them from the same
// formula). They are deliberately NOT header fields: the engine never needs
// them, and pinning them here is what makes the two sides provably agree.
//
// i32 OVERFLOW — the wide arithmetic is STRUCTURAL, not defensive
// ---------------------------------------------------------------
// |out| <= OutputCap * 32767 * L1 + |b| = 255 * 32767 * 2048 ~= 1.7e10, and
// cp = out * 1600 reaches ~2.7e13. BOTH exceed i32. Every stage from `out`
// onwards is therefore int64 — the accumulation AND the final multiply. A
// healthy net is far smaller (|W1| <= 32767/K1 = 31.8, giving |out| <= 8.1e6),
// but the ENGINE must not overflow for any file the loader accepts, so the wide
// arithmetic is unconditional rather than "only when it looks big". (This is
// the same rule as v1.1's in ScreluActivation.cs — the chain here
// widens to long from the first term instead of draining per chunk.)
// ============================================================================

/// <summary>mcnnue-v5 (scheme 2, fc0-less) quantization constants and activation
/// helpers. Frozen; see the derivation in the file header.</summary>
public static class NnueQuantV5
{
    /// <summary>Accumulator clamp (SCReLU input cap). Same as v4.</summary>
    public const int QA = 255;

    /// <summary>Pre-scale shift of the squared SCReLU. Same as v4.</summary>
    public const int ScreluShift = 8;

    /// <summary>Post-SCReLU shift before the output layer. DERIVED = 0 — see
    /// step 3 in the header. The activation gain it would otherwise control is
    /// folded into the export-side K1 instead, so this field is free to buy
    /// precision. Values >= 8 annihilate the signal (s2 maxes at 254).</summary>
    public const int OutputShift = 0;

    /// <summary>Clamp cap on the output-layer input. At OutputShift = 0 this
    /// never binds (o <= 254) — it is kept because the format exposes it and a
    /// future variant may need it.</summary>
    public const int OutputCap = 255;

    /// <summary>Evaluation scale: cp = (out * 1600) &gt;&gt; 20. Unchanged from v4 so
    /// consumers of the cp scale stay valid across the format swap.</summary>
    public const int EvalScaleMul = 1600;
    public const int EvalScaleShift = 20;

    /// <summary>Trainer SCORE_SCALE: the float score head emits raw units and the
    /// forward float path publishes raw * 400 centipawns. Mirrored from the
    /// trainer so the derivation above can be checked against it in one place.</summary>
    public const int ScoreScale = 400;

    /// <summary>Activation gain actually realised by the int chain:
    /// GAIN = QA^2 / 2^(ScreluShift + OutputShift) = 254.00390625.</summary>
    public const double OutputGain = (double)QA * QA / (1 << (ScreluShift + OutputShift));

    /// <summary>Export-side fc1 weight scale: K1 = SS * 2^S / (M * GAIN)
    /// = 1032.047120. The engine never uses it; the bit-exactness check must derive the
    /// identical value from the identical formula.</summary>
    public const double K1 = ScoreScale * (1 << EvalScaleShift) / (EvalScaleMul * OutputGain);

    /// <summary>Export-side fc1 bias scale: QB1 = SS * 2^S / M = 262144. The
    /// bias is not multiplied by GAIN.</summary>
    public const double QB1 = ScoreScale * (double)(1 << EvalScaleShift) / EvalScaleMul;

    /// <summary>Largest |fc1 weight| representable in the i16 storage without
    /// clipping: 32767 / K1 = 31.75 raw. The exporter MUST assert this — a net
    /// that clips its own head weights exports a net that is quietly wrong.</summary>
    public const double MaxAbsFc1Weight = 32767.0 / K1;

    /// <summary>SCReLU: clamp to [0, QA], square, pre-scale. i16 in, i32 out,
    /// result in [0, 254]. The squaring cannot overflow 16 bits because
    /// s*s &lt;= 65025 &lt; 2^16 — that property is what makes the SIMD square legal.</summary>
    public static int Screlu(int accumulator)
    {
        int s = accumulator < 0 ? 0 : (accumulator > QA ? QA : accumulator);
        return (s * s) >> ScreluShift;
    }

    /// <summary>Output-layer input: shift by <see cref="OutputShift"/> and clamp.
    /// At OutputShift = 0 this is a no-op clamp, kept for format generality.</summary>
    public static int Output(int screlu)
    {
        int scaled = screlu >> OutputShift;
        return scaled < 0 ? 0 : (scaled > OutputCap ? OutputCap : scaled);
    }

    /// <summary>Final eval in centipawns from the output-layer result.
    ///
    /// The multiply MUST be 64-bit — see the overflow note in the file header;
    /// the fc1 dot product itself is int64 too (see NnueNetV5's forward, M6), so
    /// the whole tail from the accumulator to centipawns is wide.</summary>
    public static int ToCp(long fc1Result)
        => (int)((fc1Result * EvalScaleMul) >> EvalScaleShift);
}
