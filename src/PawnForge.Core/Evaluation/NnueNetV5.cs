namespace PawnForge.Core.Evaluation;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;

// ============================================================================
// NnueNetV5.cs — the v5 net format: the fc0-less, region-bucketed net (M1)
// ----------------------------------------------------------------------------
// Ported from the previous generation. The normative derivation of the frozen
// output scale lives in NnueQuantV5.cs. Little-endian
// throughout. (The previous generation keeps its scheme-1 v4 format for its own
// compatibility; PawnForgeCS implements v5 ONLY — there is no v4 net to be
// compatible with.)
//
// offset size content
// 0 4 magic "MCNN"
// 4 4 u32 format version = 5
// 8 4 i32 feature scheme id = 2 (768 slots, region buckets)
// 12 4 i32 region count K (power of two, 1..32)
// 16 64 king-region table, one byte per square (a1 = 0), 0..K-1,
// every region non-empty
// 80 4 i32 L1 — hidden width, 64..2048, multiple of 16
// 84 4 i32 output buckets — MUST be 1 in this build. The format
// range is a power of two in 1..32 (the header field stays a
// TRAINER flag, not a version), but the evaluator implements
// ONE global head and no per-position head selection, so a
// multi-bucket file would be read as a single head. That is a
// silent mis-evaluation (the checksum covers the payload, not
// the header), so the loader REFUSES buckets != 1 by name —
// see `ValidateArch`.
// 88 4 i32 QA = 255 -- pinned per build
// 92 4 i32 ScreluShift = 8 -- pinned per build
// 96 4 i32 OutputShift (0) -- pinned per build
// 100 4 i32 EvalScaleMul (1600) -- pinned per build
// 104 4 i32 EvalScaleShift (20) -- pinned per build
// 108 4 i32 description length
// 112 n UTF-8 description
// ... 768*K*L1 i16 feature weights, BUCKET-MAJOR (region 0's 768 rows first)
// ... L1 i16 accumulator biases
// ... 2*L1 i16 output-row weights (see OutputWeights)
// ... 4 i32 output bias
// end 4 u32 checksum (XOR of all preceding u32-aligned words)
//
// M1 SCOPE NOTE: this file carries the LOADER/WRITER and the deterministic
// filler. The forward pass (scalar, portable vectorised, and the x64 AVX2 /
// ARM64 AdvSimd heads) lives further down in this file and is covered by its
// bit-exactness tests (M6).
//
// LOADER OBLIGATIONS (all of them, or the file is rejected BEFORE any position
// is evaluated — a net the loader cannot run must fail loudly, never silently):
// reject unknown magic; reject unknown version; bounds-check EVERY arch field;
// bounds-check the region table (range AND every region non-empty);
// exact-match reject when a pinned quant constant disagrees with this build;
// verify the checksum before use; and reject a truncated payload.
// ============================================================================

/// <summary>A loaded v5 net: region-bucketed 768-slot features, one output
/// head, no fc0. Immutable after load — shared read-only across Lazy SMP
/// evaluator instances (the accumulator state is per-thread, the weights are
/// not).</summary>
public sealed class NnueNetV5
{
    // ---- header bounds (identical to the format table in the file header) -----
    private const int MinL1 = 64, MaxL1 = 2048;
    private const int MinBuckets = 1, MaxBuckets = 32;
    private const int MaxDescriptionBytes = 4096;

    /// <summary>Region count K (number of weight blocks).</summary>
    public int RegionCount { get; }

    /// <summary>The 64-byte king-region table, one byte per square (a1 = 0).</summary>
    public byte[] RegionTable { get; }

    /// <summary>L1 — the single hidden width shared by both perspectives.</summary>
    public int L1 { get; }

    /// <summary>Output bucket count. 1 for now; kept in the format so
    /// phase-bucketed heads are a trainer flag rather than a format version.</summary>
    public int Buckets { get; }

    /// <summary>Feature scheme id (always 2 for this class).</summary>
    public int FeatureScheme => NnueFeatureSet2.SchemeId;

    /// <summary>The parameterized feature set (index formula owner).</summary>
    public NnueFeatureSet2 Features { get; }

    public string Description { get; init; } = string.Empty;

    /// <summary>FeatureWeights[(region * 768 + slot) * L1 + unit] — BUCKET-MAJOR,
    /// i16. Total length 768*K*L1.</summary>
    public short[] FeatureWeights { get; }

    /// <summary>Accumulator biases — L1 i16.</summary>
    public short[] FeatureBiases { get; }

    /// <summary>Output-head weights — <b>2 * L1</b> i16 (fed the
    /// CONCATENATION of the two perspectives' SCReLU activations).
    ///
    /// This is the single most easily misread thing in the whole migration.
    /// The trainer's canonical dual-perspective graph is:
    /// <code>
    /// l0 = affine(768*K -> L1) // ONE matrix, both POVs
    /// l1 = affine(2*L1 -> 1) // <-- 2*L1, not L1
    /// out = l1(concat(screlu(l0(stm)), screlu(l0(ntm))))
    /// </code>
    /// so the output head consumes 2*L1 inputs, not L1. The head's first L1
    /// weights belong to the SIDE TO MOVE, not to White — reading it as a
    /// single-perspective head, or feeding it white-first, produces a net that
    /// trains, exports, loads and plays badly with no error anywhere.</summary>
    public short[] OutputWeights { get; }

    /// <summary>Output-head bias — one i32.</summary>
    public int OutputBias { get; private set; }

    /// <summary>Creates a v5 net with the given geometry and zeroed tables. The
    /// region table is validated by <see cref="NnueFeatureSet2"/>.</summary>
    public NnueNetV5(int l1, ReadOnlySpan<byte> regionTable, int buckets = 1,
                     string description = "", int outputBias = 0)
    {
        ValidateArch(l1, buckets);
        L1 = l1;
        Buckets = buckets;
        OutputBias = outputBias;
        RegionTable = regionTable.ToArray();
        Features = new NnueFeatureSet2(RegionTable);
        RegionCount = Features.RegionCount;
        FeatureWeights = new short[Features.InputDimensions * l1];
        FeatureBiases = new short[l1];
        OutputWeights = new short[2 * l1];
        Description = description;
    }

    private static void ValidateArch(int l1, int buckets)
    {
        if (l1 < MinL1 || l1 > MaxL1 || l1 % 16 != 0)
            throw new InvalidDataException($"v5 L1 {l1} out of bounds/alignment ({MinL1}..{MaxL1}, multiple of 16)");
        if (buckets < MinBuckets || buckets > MaxBuckets || (buckets & (buckets - 1)) != 0)
            throw new InvalidDataException($"v5 output buckets {buckets} must be a power of two in {MinBuckets}..{MaxBuckets}");

        // ── REFUSAL, not a silent single-head read (2026-11-02) ──────────────
        // The format reserves a bucket count, but this evaluator (`Forward`) has
        // no bucket index: it always consumes the first 2*L1 output weights. A
        // header claiming N>1 buckets is therefore a promise the engine cannot
        // keep — and the checksum covers the PAYLOAD, not the header, so nothing
        // else would notice. Refusing by name is the project's "a bad net is a
        // refusal, never a fallback" rule applied to the file's own header.
        // Re-open only together with a real per-position head selection, in the
        // engine AND the trainer.
        if (buckets != 1)
            throw new InvalidDataException(
                $"v5 output buckets {buckets}: this build implements ONE global head and would read " +
                "this file as a single head (silent mis-evaluation); multi-bucket nets are refused " +
                "until the evaluator selects a head per position");
    }

    /// <summary>Deterministic filler net: all tables from a seeded PRNG with
    /// small magnitudes, so loader round-trips and (from M6) the v5 forward pass
    /// and its integration tests run before any trained net exists. Values are
    /// kept small enough that the accumulator cannot leave i16 on any legal
    /// position: 32 features x |w| plus |bias| stays far below 32767.</summary>
    public static NnueNetV5 FromFiller(int l1 = 128, ulong seed = 0x243F6A8885A308D3UL,
                                        ulong headSeed = 0x13198A2E03707344UL)
    {
        var net = new NnueNetV5(l1, NnueFeatureSet2.DefaultRegionTable, 1,
            "mcnnue-v5 deterministic filler (scheme 2 gate; no trained net yet)");

        ulong ftState = seed;
        int NextFt()
        {
            ftState ^= ftState << 13;
            ftState ^= ftState >> 7;
            ftState ^= ftState << 17;
            return (int)(ftState >> 48) % 129 - 64; // ±64
        }
        for (int i = 0; i < net.FeatureWeights.Length; i++)
            net.FeatureWeights[i] = (short)NextFt();
        for (int i = 0; i < net.FeatureBiases.Length; i++)
            net.FeatureBiases[i] = (short)NextFt();

        ulong state = headSeed;
        int Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return (int)(state >> 56) % 1025 - 512; // ±512: the real head range
        }
        for (int i = 0; i < net.OutputWeights.Length; i++)
            net.OutputWeights[i] = (short)Next();
        return net;
    }

    // ---- forward pass ---------------------------------------------------------

    /// <summary>The scheme-2 forward pass: both POV accumulators -> raw head sum.
    ///
    /// The two accumulators are concatenated and fed to the single output row in
    /// SIDE-TO-MOVE order — <paramref name="stm"/> first, then
    /// <paramref name="ntm"/> — because the head's first L1 weights belong to the
    /// MOVER, not to White (the perspective-network convention). Feeding
    /// white-first swaps the two halves of the input for every black-to-move
    /// position: the net then runs a different linear function than the one it
    /// was trained on, with no error anywhere.
    ///
    /// <b>int64 from the dot product out</b> — see the overflow note in
    /// NnueQuantV5 (|out| reaches ~1.7e10 at L1 2048 for a legal net, past i32).
    ///
    /// The result is the head sum for the position as given; the caller applies
    /// NnueQuantV5.ToCp (the caller does NOT sign-flip — the head is already
    /// mover-relative).
    ///
    /// Dispatch: on hardware that can execute it, a dedicated intrinsic arm is
    /// selected — AVX2 on x64, AdvSimd on ARM64. Both arms compute the same
    /// arithmetic as the scalar oracle in a different lane order, so every path
    /// is bit-exact. The portable <see cref="ForwardVectorised"/> path is the
    /// fallback when no intrinsic is available, and <see cref="ForwardScalar"/>
    /// is the general-quant fallback and the oracle the tests compare against.
    /// Integer addition is exact and order-free, so all paths agree bit for
    /// bit.</summary>
    public long Forward(ReadOnlySpan<short> stm, ReadOnlySpan<short> ntm)
    {
        // The dispatch is a pure function of compile-time constants and
        // JIT-time ISA flags, so at most one arm is reachable per target and the
        // switch adds no per-evaluation work the old branch did not already pay.
        switch (SelectForwardPath())
        {
            case ForwardPath.AdvSimd:
                return ForwardAdvSimd(stm, ntm);
            case ForwardPath.Avx2:
                return ForwardAvx2(stm, ntm);
            case ForwardPath.Vectorised:
                return ForwardVectorised(stm, ntm);
            default:
                return ForwardScalar(stm, ntm);
        }
    }

    /// <summary>The head implementations <see cref="Forward"/> can dispatch to.
    /// Internal so the tests can prove the intrinsic arm is actually selected
    /// instead of silently falling back to the portable path.</summary>
    internal enum ForwardPath
    {
        /// <summary>The i64 oracle — also the general-quant fallback.</summary>
        Scalar,

        /// <summary>Portable <see cref="System.Numerics.Vector{T}"/> fallback.</summary>
        Vectorised,

        /// <summary>x64 AVX2 (<c>vpmaddwd</c> plus a widening drain).</summary>
        Avx2,

        /// <summary>ARM64 AdvSimd (<c>smlal</c>/<c>smlal2</c> plus a widening
        /// drain).</summary>
        AdvSimd,
    }

    /// <summary>Chooses the forward-pass implementation for this net and host.
    ///
    /// <para>Guards, in order:</para>
    /// <list type="number">
    /// <item><see cref="NnueQuantV5.OutputShift"/> == 0 — the only value the
    /// intrinsic paths implement (they skip the post-SCRELU clamp, which cannot
    /// bind at shift 0). Any other quant chain takes <see cref="ForwardScalar"/>.</item>
    /// <item><c>L1 % 16 == 0</c> — the loader's <c>ValidateArch</c> already
    /// enforces this (MinL1 64, MaxL1 2048, multiple of 16), so every net the
    /// engine accepts reaches an intrinsic arm; the check keeps the invariant
    /// explicit rather than relying on a second file.</item>
    /// <item><see cref="AdvSimd.IsSupported"/> — the ARM64 (NEON) arm. Checked
    /// first because it is the only intrinsic this host can execute on Apple
    /// silicon; on x64 the flag is a JIT-time <c>false</c>, so the arm folds
    /// away.</item>
    /// <item><see cref="Avx2.IsSupported"/> — the x64 arm.</item>
    /// <item>Portable <see cref="ForwardVectorised"/> when the width divides
    /// <see cref="System.Numerics.Vector{T}.Count"/>; otherwise scalar.</item>
    /// </list>
    /// Both intrinsic arms are guarded by their own <c>IsSupported</c> flag, so
    /// a binary carrying both can never enter an arm the running CPU cannot
    /// execute.</summary>
    internal ForwardPath SelectForwardPath()
    {
        // Positive test on purpose: OutputShift is a compile-time constant 0, so
        // writing `!= 0` first would make the rest flagged as unreachable code.
        if (NnueQuantV5.OutputShift == 0)
        {
            if (L1 % 16 == 0 && AdvSimd.Arm64.IsSupported)
                return ForwardPath.AdvSimd;

            if (L1 % 16 == 0 && Avx2.IsSupported)
                return ForwardPath.Avx2;

            if (L1 % System.Numerics.Vector<short>.Count == 0)
                return ForwardPath.Vectorised;
        }

        return ForwardPath.Scalar;
    }

    /// <summary>The scalar reference path: identical arithmetic to the vectorised
    /// one, in i64 from the first product. Kept as the bit-exactness oracle
    /// (internal — the tests compare this against <see cref="Forward"/>) and as
    /// the fallback for any quant chain the vector path cannot represent.</summary>
    internal long ForwardScalar(ReadOnlySpan<short> stm, ReadOnlySpan<short> ntm)
    {
        long sum = OutputBias;
        for (int i = 0; i < L1; i++)
        {
            int a = stm[i];
            if (a > 0)
                sum += (long)NnueQuantV5.Output(NnueQuantV5.Screlu(a)) * OutputWeights[i];
            int b = ntm[i];
            if (b > 0)
                sum += (long)NnueQuantV5.Output(NnueQuantV5.Screlu(b)) * OutputWeights[L1 + i];
        }
        return sum;
    }

    /// <summary>The vectorised head: per <see cref="System.Numerics.Vector{S}"/>-lane
    /// i16 chunk, SCReLU in i16 (clamp; square — the low 16 bits of s*s ARE s*s
    /// because s*s &lt;= 65025 &lt; 2^16; LOGICAL shift right by
    /// <see cref="NnueQuantV5.ScreluShift"/> via an unsigned reinterpret, which is
    /// what makes s &gt; 181 correct — an arithmetic shift would subtract 256 for
    /// every square above 181), then widen to i32 and multiply by the widened
    /// weights (|o*w| &lt;= 254*32767 &lt; 2^31, exact), sum the chunk's products
    /// in i32 (at most 2*width products &lt;= 1.4e8, no overflow) and widen THAT
    /// into the i64 accumulator. The final horizontal add and the ToCp multiply
    /// stay i64. The OutputShift/OutputCap clamp is not applied here — with the
    /// frozen OutputShift = 0 it cannot bind (s2 &lt;= 254 &lt; OutputCap = 255);
    /// any other constant takes the scalar path in <see cref="Forward"/>.
    /// Addition is exact and order-free over integers, so this is bit-identical
    /// to the scalar loop (pinned by NnueEvaluatorTests).</summary>
    internal long ForwardVectorised(ReadOnlySpan<short> stm, ReadOnlySpan<short> ntm)
    {
        int width = System.Numerics.Vector<short>.Count;
        var zero = System.Numerics.Vector<short>.Zero;
        var qa = new System.Numerics.Vector<short>((short)NnueQuantV5.QA);
        var sum = System.Numerics.Vector<long>.Zero;
        ReadOnlySpan<short> weights = OutputWeights;

        for (int i = 0; i < L1; i += width)
        {
            var a = new System.Numerics.Vector<short>(stm.Slice(i, width));
            var b = new System.Numerics.Vector<short>(ntm.Slice(i, width));

            // SCReLU, both halves of the head input, in i16 lanes.
            var sa = System.Numerics.Vector.Min(System.Numerics.Vector.Max(a, zero), qa);
            var sb = System.Numerics.Vector.Min(System.Numerics.Vector.Max(b, zero), qa);
            var sqA = (System.Numerics.Vector<short>)((System.Numerics.Vector<ushort>)(sa * sa) >> NnueQuantV5.ScreluShift);
            var sqB = (System.Numerics.Vector<short>)((System.Numerics.Vector<ushort>)(sb * sb) >> NnueQuantV5.ScreluShift);

            // Widen and multiply in i32, reduce the chunk in i32, accumulate i64.
            System.Numerics.Vector.Widen(sqA, out var aL, out var aH);
            System.Numerics.Vector.Widen(sqB, out var bL, out var bH);
            System.Numerics.Vector.Widen(new System.Numerics.Vector<short>(weights.Slice(i, width)), out var wL, out var wH);
            System.Numerics.Vector.Widen(new System.Numerics.Vector<short>(weights.Slice(L1 + i, width)), out var w2L, out var w2H);

            System.Numerics.Vector<int> chunk = (aL * wL + aH * wH) + (bL * w2L + bH * w2H);
            System.Numerics.Vector.Widen(chunk, out var chunkL, out var chunkH);
            sum += chunkL + chunkH;
        }

        // Horizontal add of the i64 lane accumulator (Vector<long>.Count is 2 on
        // arm64, 4 on AVX2 — never index a fixed lane).
        long total = 0;
        for (int lane = 0; lane < System.Numerics.Vector<long>.Count; lane++)
            total += sum[lane];
        return OutputBias + total;
    }

    /// <summary>ARM64 (NEON) head: SCReLU in 8 i16 lanes, then a widening
    /// multiply-accumulate (<c>smlal</c> / <c>smlal2</c>) into two i32
    /// accumulators, drained into i64 with per-lane scalar widening every
    /// 16 chunks.
    ///
    /// <para>Bit-identical to <see cref="ForwardScalar"/> by construction: every
    /// product is computed in i32 (|product| ≤ 254 · 32767 = 8,322,818, exact);
    /// a chunk puts 4 products per i32 lane (lower + upper half × both
    /// perspectives); a 16-chunk window therefore holds ≤ 64 products per lane
    /// (≤ 532,660,352, 4× under i32); and the drain widens every lane to i64
    /// BEFORE the lanes are summed, so the total is exact for every net the
    /// loader accepts. Integer addition is exact and order-free, hence bit
    /// equality.</para>
    ///
    /// <para>The shape is load-bearing: TWO accumulators — one per perspective,
    /// which breaks the four dependent <c>smlal</c> chains — and NO unrolling
    /// (2× and 4× measured slower). Do not "optimise" the loop without a
    /// measurement.</para>
    ///
    /// <para><b>Drain shape is pinned by a .NET 11 JIT regression (Bug 143).</b>
    /// <see cref="AdvSimd.AddWideningLower(Vector64{int}, Vector64{int})"/> is
    /// miscompiled on arm64 by the 11.0.0 JIT: it emits SADDW semantics (the
    /// FIRST operand is consumed as raw 64-bit lanes and leaks into the high
    /// half of each result lane) instead of SADDL semantics (both operands
    /// sign-widened). Probing <c>acc = {100, 200, 300, 400}</c> returns lanes
    /// <c>{200·2³² + 200, 400·2³² + 500}</c> instead of <c>{200, 400}</c> —
    /// on .NET 10 the same code is exact. The widening drain therefore uses
    /// per-lane scalar extraction (8 <c>GetElement</c>s per 16-chunk window,
    /// ~4 windows per forward at L1 512 — noise next to the smlal loop).
    /// Re-introduce a vector drain only after the upstream fix is verified on
    /// the affected runtimes.</para></summary>
    internal long ForwardAdvSimd(ReadOnlySpan<short> stm, ReadOnlySpan<short> ntm)
    {
        const int VecWidth = 8;          // 128-bit NEON: 8 × i16 lanes
        const int ChunksPerWindow = 16;  // 16 chunks × 4 products per i32 lane

        Vector128<short> vZero = Vector128<short>.Zero;
        Vector128<short> vQA = Vector128.Create((short)NnueQuantV5.QA);
        ReadOnlySpan<short> wStm = OutputWeights.AsSpan(0, L1);
        ReadOnlySpan<short> wNtm = OutputWeights.AsSpan(L1, L1);
        // Ref locals for the chunk loads: Vector128.LoadUnsafe takes a ref + element offset, so
        // the inner loop needs no Span construction and no bounds checks. Measured 63.8 -> 48.6
        // ns/call (-24 %) at L1 = 512 (how-to-improve-with-dotnet11.md §2.3). The spans stay
        // alive for the refs' lifetime; every read is bounded by `chunks * VecWidth == L1`.
        ref short pStm = ref MemoryMarshal.GetReference(stm);
        ref short pNtm = ref MemoryMarshal.GetReference(ntm);
        ref short pWStm = ref MemoryMarshal.GetReference(wStm);
        ref short pWNtm = ref MemoryMarshal.GetReference(wNtm);
        int chunks = L1 / VecWidth;
        long sum = 0;

        for (int chunkBase = 0; chunkBase < chunks; chunkBase += ChunksPerWindow)
        {
            // Math.Min keeps every legal L1 exact, including 64 / 144 / 160,
            // where L1 / VecWidth is not a multiple of the window.
            int end = Math.Min(chunkBase + ChunksPerWindow, chunks);
            Vector128<int> accStm = Vector128<int>.Zero;
            Vector128<int> accNtm = Vector128<int>.Zero;

            for (int c = chunkBase; c < end; c++)
            {
                nuint i = (nuint)(c * VecWidth);
                Vector128<short> a = Vector128.LoadUnsafe(ref pStm, i);
                Vector128<short> b = Vector128.LoadUnsafe(ref pNtm, i);
                Vector128<short> wA = Vector128.LoadUnsafe(ref pWStm, i);
                Vector128<short> wB = Vector128.LoadUnsafe(ref pWNtm, i);

                Vector128<short> cA = AdvSimd.Min(AdvSimd.Max(a, vZero), vQA);
                Vector128<short> cB = AdvSimd.Min(AdvSimd.Max(b, vZero), vQA);

                // Low 16 bits of s*s ARE s*s (≤ 65,025 < 2^16). The shift is
                // LOGICAL, so the product is reinterpreted as u16 first — an
                // arithmetic shift would corrupt every activation above 181.
                Vector128<short> sqA = AdvSimd.ShiftRightLogical(
                    AdvSimd.Multiply(cA, cA).AsUInt16(), NnueQuantV5.ScreluShift).AsInt16();
                Vector128<short> sqB = AdvSimd.ShiftRightLogical(
                    AdvSimd.Multiply(cB, cB).AsUInt16(), NnueQuantV5.ScreluShift).AsInt16();

                // smlal / smlal2: i16 × i16 → i32 widening MAC. The 128-bit
                // "lower" form takes Vector64 (GetLower); the "upper" form takes
                // the full Vector128 and uses its high half.
                accStm = AdvSimd.MultiplyWideningLowerAndAdd(accStm, sqA.GetLower(), wA.GetLower());
                accStm = AdvSimd.MultiplyWideningUpperAndAdd(accStm, sqA, wA);
                accNtm = AdvSimd.MultiplyWideningLowerAndAdd(accNtm, sqB.GetLower(), wB.GetLower());
                accNtm = AdvSimd.MultiplyWideningUpperAndAdd(accNtm, sqB, wB);
            }

            // Drain: sum the four i32 lanes of each accumulator into the i64
            // total with PER-LANE scalar widening. No `long` ever holds more
            // than one lane's partial sum, so the total is exact.
            //
            // This is deliberately NOT `AddWideningLower` + `AddWideningUpper`:
            // the .NET 11.0.0 arm64 JIT miscompiles the Vector64 overload of
            // AddWideningLower with SADDW semantics (first operand read as raw
            // 64-bit lanes -> garbage in the high half of every result lane,
            // see the class doc / Bug 143), which corrupted every head sum and
            // with it every eval, TT score and mate score. The same is also the
            // reason `AddAcross` stays banned: it reduces four i32 lanes in
            // i32 and can overflow before the widening step.
            sum += (long)accStm.GetElement(0) + accNtm.GetElement(0)
                 + (long)accStm.GetElement(1) + accNtm.GetElement(1)
                 + (long)accStm.GetElement(2) + accNtm.GetElement(2)
                 + (long)accStm.GetElement(3) + accNtm.GetElement(3);
        }

        return OutputBias + sum;
    }

    /// <summary>x64 AVX2 head: SCReLU in 16 i16 lanes, <c>vpmaddwd</c>
    /// (<see cref="Avx2.MultiplyAddAdjacent"/>) into two i32 accumulators,
    /// drained into i64 with a widening add every 16 chunks.
    ///
    /// <para>Bit-identical to <see cref="ForwardScalar"/> by construction: every
    /// product is exact in i32 (|product| ≤ 254 · 32767 = 8,322,818);
    /// <c>vpmaddwd</c> adds two adjacent products per i32 lane (≤ 16,645,636,
    /// still exact); a 16-chunk window puts ≤ 32 products per lane
    /// (≤ 266,330,176, 8× under i32); and the horizontal reduction widens every
    /// lane to i64 BEFORE summing, so the total is exact for every net the
    /// loader accepts. Integer addition is exact and order-free, hence bit
    /// equality.</para>
    ///
    /// <para>The shape is load-bearing: TWO accumulator registers — one per
    /// perspective — and NO unrolling. Both were measured faster on ARM64 and
    /// the arithmetic is identical here; do not "optimise" the loop without a
    /// measurement.</para></summary>
    internal long ForwardAvx2(ReadOnlySpan<short> stm, ReadOnlySpan<short> ntm)
    {
        const int VecWidth = 16;         // 256-bit AVX2: 16 × i16 lanes
        const int ChunksPerWindow = 16;  // 16 chunks × 2 products per i32 lane × 2 POVs

        Vector256<short> vZero = Vector256<short>.Zero;
        Vector256<short> vQA = Vector256.Create((short)NnueQuantV5.QA);
        ReadOnlySpan<short> wStm = OutputWeights.AsSpan(0, L1);
        ReadOnlySpan<short> wNtm = OutputWeights.AsSpan(L1, L1);
        // Same LoadUnsafe shape as the ARM64 arm (measured there; the x64 arm must be
        // re-verified on the Windows host because this branch cannot execute on Apple Silicon).
        // Every read is bounded by `chunks * VecWidth == L1`.
        ref short pStm = ref MemoryMarshal.GetReference(stm);
        ref short pNtm = ref MemoryMarshal.GetReference(ntm);
        ref short pWStm = ref MemoryMarshal.GetReference(wStm);
        ref short pWNtm = ref MemoryMarshal.GetReference(wNtm);
        int chunks = L1 / VecWidth;
        long sum = 0;

        for (int chunkBase = 0; chunkBase < chunks; chunkBase += ChunksPerWindow)
        {
            // Math.Min keeps every legal L1 exact, including 64 / 144 / 160,
            // where L1 / VecWidth is not a multiple of the window.
            int end = Math.Min(chunkBase + ChunksPerWindow, chunks);
            Vector256<int> accStm = Vector256<int>.Zero;
            Vector256<int> accNtm = Vector256<int>.Zero;

            for (int c = chunkBase; c < end; c++)
            {
                nuint i = (nuint)(c * VecWidth);
                Vector256<short> a = Vector256.LoadUnsafe(ref pStm, i);
                Vector256<short> b = Vector256.LoadUnsafe(ref pNtm, i);
                Vector256<short> wA = Vector256.LoadUnsafe(ref pWStm, i);
                Vector256<short> wB = Vector256.LoadUnsafe(ref pWNtm, i);

                Vector256<short> cA = Avx2.Min(Avx2.Max(a, vZero), vQA);
                Vector256<short> cB = Avx2.Min(Avx2.Max(b, vZero), vQA);

                // Low 16 bits of s*s ARE s*s (≤ 65,025 < 2^16). The shift is
                // LOGICAL, so the product is reinterpreted as u16 first — an
                // arithmetic shift would corrupt every activation above 181.
                Vector256<short> sqA = Avx2.ShiftRightLogical(
                    Avx2.MultiplyLow(cA, cA).AsUInt16(), NnueQuantV5.ScreluShift).AsInt16();
                Vector256<short> sqB = Avx2.ShiftRightLogical(
                    Avx2.MultiplyLow(cB, cB).AsUInt16(), NnueQuantV5.ScreluShift).AsInt16();

                // vpmaddwd: adjacent i16 pairs → i32, added into the accumulator.
                accStm = Avx2.Add(accStm, Avx2.MultiplyAddAdjacent(sqA, wA));
                accNtm = Avx2.Add(accNtm, Avx2.MultiplyAddAdjacent(sqB, wB));
            }

            // Drain: add the two i32 accumulators lane-wise, WIDEN to i64, then
            // sum the 4 + 4 lanes. No `int` ever holds more than one lane's
            // partial sum, so the total is exact.
            Vector256<long> lo = Avx2.Add(
                Avx2.ConvertToVector256Int64(accStm.GetLower()),
                Avx2.ConvertToVector256Int64(accNtm.GetLower()));
            Vector256<long> hi = Avx2.Add(
                Avx2.ConvertToVector256Int64(Avx2.ExtractVector128(accStm, 1)),
                Avx2.ConvertToVector256Int64(Avx2.ExtractVector128(accNtm, 1)));
            Vector256<long> t = Avx2.Add(lo, hi);
            for (int lane = 0; lane < Vector256<long>.Count; lane++)
                sum += t.GetElement(lane);
        }

        return OutputBias + sum;
    }

    // ---- file format ----------------------------------------------------------

    private const uint Magic = 0x4E4E434Du; // "MCNN"
    private const uint Version = 5;

    /// <summary>Fixed header size: everything before the description LENGTH, which
    /// is the first payload field and therefore the first checksummed byte.
    /// (108, not 112 — the description bytes follow the length.)</summary>
    public const int HeaderBytes = 108;

    /// <summary>
    /// The embedded champion net's RESOURCE file name — <b>jokausch.nnue</b>, a name that
    /// is deliberately PERMANENT so promoting a champion touches NO code and NO name.
    /// The net ships as an EmbeddedResource of PawnForge.Core (the csproj's
    /// <c>&lt;EmbeddedResource Include="Resources/jokausch.nnue" /&gt;</c> entry turns it
    /// into a manifest resource named <c>PawnForge.Core.Resources.jokausch.nnue</c>) and
    /// every loader matches it through this constant (EngineOrchestrator.EnsureNnueLoaded,
    /// NnueSourceName, and the TestHelper).
    ///
    /// PROMOTION FLOW (the only sanctioned way a champion changes): export the new
    /// champion from its training checkpoint (the raw weights payload — no header,
    /// no region table, no checksum, and
    /// the loader refuses it), write the result over src/PawnForge.Core/Resources/jokausch.nnue,
    /// rebuild. The net's true provenance (lineage, generation, L1) lives in the v5
    /// DESCRIPTION header inside the file, never in the file name. A name that drifts
    /// fails LOUDLY (missing-resource refusal), never silently.</summary>
    public const string EmbeddedNetResourceName = "jokausch.nnue";

    public void Save(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(stream);
    }

    /// <summary>Writes the net: 108-byte header, payload, checksum.</summary>
    public void Write(Stream stream)
    {
        var payload = new MemoryStream();
        WritePayload(payload);
        WriteU32(stream, Magic);
        WriteU32(stream, Version);
        WriteI32(stream, FeatureScheme);
        WriteI32(stream, RegionCount);
        stream.Write(RegionTable);
        WriteI32(stream, L1);
        WriteI32(stream, Buckets);
        WriteI32(stream, NnueQuantV5.QA);
        WriteI32(stream, NnueQuantV5.ScreluShift);
        WriteI32(stream, NnueQuantV5.OutputShift);
        WriteI32(stream, NnueQuantV5.EvalScaleMul);
        WriteI32(stream, NnueQuantV5.EvalScaleShift);
        payload.Position = 0;
        payload.CopyTo(stream);
        WriteU32(stream, ChecksumOf(payload));
    }

    /// <summary>Everything AFTER the fixed 108-byte header: description length,
    /// description, feature weights (bucket-major), biases, output row + bias.</summary>
    private void WritePayload(Stream stream)
    {
        byte[] description = Encoding.UTF8.GetBytes(Description);
        WriteI32(stream, description.Length);
        stream.Write(description);
        WriteI16Span(stream, FeatureWeights);
        WriteI16Span(stream, FeatureBiases);
        WriteI16Span(stream, OutputWeights);
        WriteI32(stream, OutputBias);
    }

    /// <summary>XOR of the u32 words of the payload. Trailing partial words are
    /// DROPPED (the payload's i16 tail plus the i32 bias always ends
    /// 4-aligned here, but the rule is the loader's, not the writer's — the
    /// read side must implement exactly the same drop, see <see cref="ChecksumRange"/>).</summary>
    private static uint ChecksumOf(MemoryStream payload)
    {
        payload.Position = 0;
        uint xor = 0;
        Span<byte> chunk = stackalloc byte[8192];
        int read;
        while ((read = payload.Read(chunk)) > 0)
        {
            int words = read / 4; // trailing partial word dropped
            for (int i = 0; i < words; i++)
                xor ^= BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(i * 4, 4));
        }
        payload.Position = 0;
        return xor;
    }

    public static NnueNetV5 Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Read(stream);
    }

    /// <summary>Reads and validates a v5 net from a stream. EVERY failure mode is
    /// an <see cref="InvalidDataException"/> — the caller (UCI setoption, the
    /// orchestrator's net load, the negative-control tests) only ever has to
    /// catch one type. Truncation arrives as <see cref="EndOfStreamException"/>
    /// from the <c>ReadExactly</c> calls and is translated here, so a truncated
    /// file is a rejected FILE, not an unexpected stream state.</summary>
    public static NnueNetV5 Read(Stream stream)
    {
        try
        {
            return ReadInternal(stream);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("net file is truncated (payload shorter than the header declares)", ex);
        }
    }

    private static NnueNetV5 ReadInternal(Stream stream)
    {
        if (ReadU32(stream) != Magic)
            throw new InvalidDataException("not a v5 net file (bad magic)");
        if (ReadU32(stream) != Version)
            throw new InvalidDataException($"unsupported net format version (this build reads v{Version})");

        int scheme = ReadI32(stream);
        if (scheme != NnueFeatureSet2.SchemeId)
            throw new InvalidDataException($"v5 feature scheme {scheme} unsupported (this build implements {NnueFeatureSet2.SchemeId})");
        int regions = ReadI32(stream);

        byte[] regionTable = new byte[64];
        stream.ReadExactly(regionTable);
        // K is validated against the table itself as well as the declared count:
        // a header that claims K = 8 over a table whose max byte is 5 is a lie, and
        // silently believing it would size the weight block wrongly.
        int tableMax = 0;
        foreach (byte r in regionTable)
            if (r > tableMax)
                tableMax = r;
        if (regions != tableMax + 1)
            throw new InvalidDataException($"v5 region count {regions} disagrees with the king-region table (max byte {tableMax} implies {tableMax + 1})");
        if (regions < NnueFeatureSet2.MinRegions || regions > NnueFeatureSet2.MaxRegions || (regions & (regions - 1)) != 0)
            throw new InvalidDataException($"v5 region count {regions} must be a power of two in {NnueFeatureSet2.MinRegions}..{NnueFeatureSet2.MaxRegions}");
        for (int sq = 0; sq < 64; sq++)
            if (regionTable[sq] >= regions)
                throw new InvalidDataException($"v5 king-region table: square {sq} maps to region {regionTable[sq]} (>= K = {regions})");

        int l1 = ReadI32(stream);
        int buckets = ReadI32(stream);
        ValidateArch(l1, buckets);

        // The quant fields stay PINNED to the build (they live in NnueQuantV5 and
        // the exporter must agree); the ARCH fields are bounds-checked only, so a
        // width change on the training side needs no engine change.
        if (ReadI32(stream) != NnueQuantV5.QA ||
            ReadI32(stream) != NnueQuantV5.ScreluShift ||
            ReadI32(stream) != NnueQuantV5.OutputShift ||
            ReadI32(stream) != NnueQuantV5.EvalScaleMul ||
            ReadI32(stream) != NnueQuantV5.EvalScaleShift)
            throw new InvalidDataException("v5 net quant fields do not match this build");

        int descriptionLength = ReadI32(stream);
        if (descriptionLength < 0 || descriptionLength > MaxDescriptionBytes)
            throw new InvalidDataException($"v5 description length {descriptionLength} out of bounds (0..{MaxDescriptionBytes})");
        byte[] description = new byte[descriptionLength];
        stream.ReadExactly(description);

        // NnueFeatureSet2 re-validates the table (range + every region non-empty). Its
        // constructor throws ArgumentException because it is also the programmatic
        // entry point; from the LOADER every problem is a bad FILE, so the
        // exception is translated. Callers then only ever have to catch
        // InvalidDataException.
        NnueNetV5 net;
        try
        {
            net = new NnueNetV5(l1, regionTable, buckets)
            {
                Description = Encoding.UTF8.GetString(description)
            };
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("v5 king-region table is invalid: " + ex.Message, ex);
        }
        ReadI16Span(stream, net.FeatureWeights);
        ReadI16Span(stream, net.FeatureBiases);
        ReadI16Span(stream, net.OutputWeights);
        net.OutputBias = ReadI32(stream);

        uint stored = ReadU32(stream);
        long payloadEnd = stream.Position - 4; // the stored checksum is not part of the body
        uint actual = ChecksumRange(stream, HeaderBytes, payloadEnd - HeaderBytes);
        if (stored != actual)
            throw new InvalidDataException("net checksum mismatch");
        return net;
    }

    /// <summary>XOR of the u32 words in [start, start + length). The payload is
    /// read FULLY before the XOR: chunked streaming would split 4-byte words at
    /// chunk boundaries and silently drop bytes, and the write side (a
    /// MemoryStream, always full reads) would then disagree. Trailing partial
    /// words are dropped — exactly matching <see cref="ChecksumOf"/>.</summary>
    private static uint ChecksumRange(Stream stream, long start, long length)
    {
        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = stream.Read(buffer, offset, (int)(length - offset));
            if (read == 0)
                break;
            offset += read;
        }
        uint xor = 0;
        for (int i = 0; i + 4 <= length; i += 4)
            xor ^= BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i, 4));
        stream.Seek(0, SeekOrigin.End);
        return xor;
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteI32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteI16Span(Stream s, ReadOnlySpan<short> values)
    {
        // Bulk write: little-endian on disk, little-endian host — so the
        // in-memory i16 bytes ARE the on-disk bytes.
        if (!BitConverter.IsLittleEndian)
        {
            Span<byte> b = stackalloc byte[2];
            foreach (short v in values)
            {
                BinaryPrimitives.WriteInt16LittleEndian(b, v);
                s.Write(b);
            }
            return;
        }
        s.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values));
    }

    private static uint ReadU32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static int ReadI32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    private static void ReadI16Span(Stream s, short[] target)
    {
        if (BitConverter.IsLittleEndian)
        {
            Span<byte> raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes(target.AsSpan());
            s.ReadExactly(raw);
            return;
        }
        Span<byte> b = stackalloc byte[2];
        for (int i = 0; i < target.Length; i++)
        {
            s.ReadExactly(b);
            target[i] = BinaryPrimitives.ReadInt16LittleEndian(b);
        }
    }
}
