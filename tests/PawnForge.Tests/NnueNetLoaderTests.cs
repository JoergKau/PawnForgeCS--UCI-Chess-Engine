namespace PawnForge.Tests;

using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// M1 gate — the v5 net loader's negative controls and round-trips.
///
/// Every rejection test MUTATES a valid net's bytes and then REPAIRS the
/// checksum, so the intended check is the one that fires (a stale checksum
/// would reject everything and prove nothing — the negative-control discipline
/// from the previous generation's loader tests). One positive block pins the round trip
/// (write == read, all tables) and the format dispatch.
///
/// NOTE on the rejection inventory: the format's nominal "any table byte >= K"
/// rejection cannot fire independently — the loader first demands
/// regions == tableMax + 1, under which every stored byte is < regions by
/// construction. Its two REAL handles are "declared K disagrees with the
/// table" and the feature set's "every region non-empty"; both are tested
/// here, so the inventory is complete even though one nominal row is not
/// reachable in isolation.
/// </summary>
public class NnueNetLoaderTests
{
    /// <summary>A small valid net's raw file bytes: L1 = 64 (minimum), the
    /// default K = 8 region table, deterministic table contents. Built through
    /// the real writer so the mutations below attack the real encoding.</summary>
    private static byte[] ValidNetBytes(int l1 = 64)
    {
        var net = new NnueNetV5(l1, NnueFeatureSet2.DefaultRegionTable, 1, "m1-test-net");
        // Deterministic non-zero contents (small magnitudes; the loader must
        // not care, the checksum must cover them).
        for (int i = 0; i < net.FeatureWeights.Length; i++)
            net.FeatureWeights[i] = (short)((i * 37 + 11) % 251 - 125);
        for (int i = 0; i < net.FeatureBiases.Length; i++)
            net.FeatureBiases[i] = (short)((i * 13 + 3) % 61 - 30);
        for (int i = 0; i < net.OutputWeights.Length; i++)
            net.OutputWeights[i] = (short)((i * 7 + 5) % 1023 - 511);
        using var ms = new MemoryStream();
        net.Write(ms);
        return ms.ToArray();
    }

    /// <summary>Recomputes the payload checksum and patches the trailing
    /// u32, so a mutation test exercises ITS check, not the checksum's.</summary>
    private static void FixChecksum(byte[] net)
    {
        uint xor = 0;
        for (int i = NnueNetV5.HeaderBytes; i + 4 <= net.Length - 4; i += 4)
            xor ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(net.AsSpan(i, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            net.AsSpan(net.Length - 4, 4), xor);
    }

    private static void WriteI32(byte[] net, int offset, int value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            net.AsSpan(offset, 4), value);

    private static void Rejects(byte[] net, string because)
    {
        using var ms = new MemoryStream(net);
        var ex = Record.Exception(() => NnueNetV5.Read(ms));
        Assert.NotNull(ex);
        Assert.IsType<InvalidDataException>(ex);
    }

    // ---- the rejections -------------------------------------------------------

    [Fact]
    public void BadMagic_IsRejected()
    {
        var net = ValidNetBytes();
        net[0] = (byte)'X';
        Rejects(net, "magic");
    }

    [Fact]
    public void UnknownVersion_IsRejected()
    {
        var net = ValidNetBytes();
        WriteI32(net, 4, 6);
        FixChecksum(net); // the header is checksummed, so repair it first
        Rejects(net, "version");
    }

    [Fact]
    public void WrongFeatureScheme_IsRejected()
    {
        var net = ValidNetBytes();
        WriteI32(net, 8, 1); // scheme 1 does not exist in this build
        FixChecksum(net);
        Rejects(net, "scheme");
    }

    [Fact]
    public void DeclaredRegionCount_DisagreeingWithTable_IsRejected()
    {
        var net = ValidNetBytes();
        WriteI32(net, 12, 7); // the default table implies K = 8
        FixChecksum(net);
        Rejects(net, "K-vs-table");
    }

    [Fact]
    public void RegionCount_NotPowerOfTwo_IsRejected()
    {
        // A table whose max byte is 4 implies K = 5 — declared honestly, so the
        // disagreement check passes and the power-of-two bound fires.
        var net = ValidNetBytes();
        for (int sq = 0; sq < 64; sq++)
            if (net[16 + sq] > 4)
                net[16 + sq] = 4;
        WriteI32(net, 12, 5);
        FixChecksum(net);
        Rejects(net, "K-not-power-of-two");
    }

    [Fact]
    public void RegionTable_WithUnusedRegion_IsRejected()
    {
        // Regions 0..3 implied (max byte 3 = K 4, power of two) but region 2 is
        // never used — the feature set's non-empty rule must fire (translated
        // to InvalidDataException by the loader).
        var net = ValidNetBytes();
        for (int sq = 0; sq < 64; sq++)
            net[16 + sq] = (byte)(sq == 0 ? 3 : 0);
        WriteI32(net, 12, 4);
        FixChecksum(net);
        Rejects(net, "unused-region");
    }

    [Theory]
    [InlineData(32)]    // below MinL1
    [InlineData(2064)]  // above MaxL1
    public void L1_OutOfBounds_IsRejected(int l1)
    {
        var net = ValidNetBytes();
        WriteI32(net, 80, l1);
        FixChecksum(net);
        Rejects(net, "L1-bounds");
    }

    [Fact]
    public void L1_NotMultipleOf16_IsRejected()
    {
        var net = ValidNetBytes();
        WriteI32(net, 80, 72);
        FixChecksum(net);
        Rejects(net, "L1-alignment");
    }

    [Fact]
    public void OutputBuckets_NotPowerOfTwo_IsRejected()
    {
        var net = ValidNetBytes();
        WriteI32(net, 84, 3);
        FixChecksum(net);
        Rejects(net, "buckets");
    }

    /// <summary>
    /// Buckets = 2 is a VALID v5 bucket count but an UNIMPLEMENTED layout: the
    /// evaluator has ONE global head and no per-position selection, so reading a
    /// 2-head file as one head is a silent mis-evaluation. The loader must refuse
    /// it BY NAME (/) — guarding the one hole the payload
    /// checksum cannot see, because the header is not checksummed.
    /// </summary>
    [Fact]
    public void OutputBuckets_MultiHead_IsRejectedByName()
    {
        var net = ValidNetBytes();
        WriteI32(net, 84, 2);
        FixChecksum(net);
        using var ms = new MemoryStream(net);
        var ex = Record.Exception(() => NnueNetV5.Read(ms));
        Assert.NotNull(ex);
        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("ONE global head", ex.Message);
    }

    [Theory]
    [InlineData(88, 254)]  // QA
    [InlineData(92, 9)]    // ScreluShift
    [InlineData(96, 1)]    // OutputShift
    [InlineData(100, 1601)]// EvalScaleMul
    [InlineData(104, 21)]  // EvalScaleShift
    public void PinnedQuantConstant_OffByOne_IsRejected(int offset, int value)
    {
        var net = ValidNetBytes();
        WriteI32(net, offset, value);
        FixChecksum(net);
        Rejects(net, "quant-pin");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void DescriptionLength_OutOfBounds_IsRejected(int length)
    {
        var net = ValidNetBytes();
        WriteI32(net, 108, length);
        FixChecksum(net);
        Rejects(net, "description-length");
    }

    [Fact]
    public void BadChecksum_IsRejected()
    {
        // Deliberately NO checksum repair: the checksum check is the target.
        var net = ValidNetBytes();
        net[net.Length - 8] ^= 0xFF;
        Rejects(net, "checksum");
    }

    [Fact]
    public void TruncatedPayload_IsRejected()
    {
        var full = ValidNetBytes();
        var net = new byte[full.Length - 3];
        Array.Copy(full, net, net.Length);
        Rejects(net, "truncation");
    }

    // ---- the positives --------------------------------------------------------

    [Fact]
    public void KnownWeights_RoundTrip_ByteForByte()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf-m1-{Guid.NewGuid():N}.nnue");
        try
        {
            var net = new NnueNetV5(64, NnueFeatureSet2.DefaultRegionTable, 1, "round-trip");
            for (int i = 0; i < net.FeatureWeights.Length; i++)
                net.FeatureWeights[i] = (short)((i * 37 + 11) % 251 - 125);
            for (int i = 0; i < net.FeatureBiases.Length; i++)
                net.FeatureBiases[i] = (short)((i * 13 + 3) % 61 - 30);
            for (int i = 0; i < net.OutputWeights.Length; i++)
                net.OutputWeights[i] = (short)((i * 7 + 5) % 1023 - 511);

            net.Save(path);
            var loaded = NnueNetFile.Load(path);

            Assert.Equal(net.RegionCount, loaded.RegionCount);
            Assert.Equal(net.L1, loaded.L1);
            Assert.Equal(net.Buckets, loaded.Buckets);
            Assert.Equal(net.Description, loaded.Description);
            Assert.Equal(net.RegionTable, loaded.RegionTable);
            Assert.Equal(net.FeatureWeights, loaded.FeatureWeights);
            Assert.Equal(net.FeatureBiases, loaded.FeatureBiases);
            Assert.Equal(net.OutputWeights, loaded.OutputWeights);
            Assert.Equal(net.OutputBias, loaded.OutputBias);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Filler_IsDeterministic_AndRoundTrips()
    {
        var a = NnueNetV5.FromFiller();
        var b = NnueNetV5.FromFiller();
        Assert.Equal(a.FeatureWeights, b.FeatureWeights);
        Assert.Equal(a.FeatureBiases, b.FeatureBiases);
        Assert.Equal(a.OutputWeights, b.OutputWeights);

        string path = Path.Combine(Path.GetTempPath(), $"pf-m1-{Guid.NewGuid():N}.nnue");
        try
        {
            a.Save(path);
            var loaded = NnueNetFile.Load(path);
            Assert.Equal(a.FeatureWeights, loaded.FeatureWeights);
            Assert.Equal(a.OutputWeights, loaded.OutputWeights);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PeekVersion_KnowsV5_AndNamesAnUnknownOne()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf-m1-{Guid.NewGuid():N}.nnue");
        string bad = Path.Combine(Path.GetTempPath(), $"pf-m1-{Guid.NewGuid():N}.nnue");
        try
        {
            NnueNetV5.FromFiller(l1: 64).Save(path);
            Assert.Equal(5, NnueNetFile.PeekVersion(path));

            var net = ValidNetBytes();
            WriteI32(net, 4, 9);
            FixChecksum(net);
            File.WriteAllBytes(bad, net);
            var ex = Record.Exception(() => NnueNetFile.Load(bad));
            Assert.NotNull(ex);
            Assert.IsType<InvalidDataException>(ex);
            Assert.Contains("9", ex.Message); // the version is NAMED, not implied
        }
        finally
        {
            File.Delete(path);
            File.Delete(bad);
        }
    }

    // ---- the region-table renderings ------------------------------------------

    [Fact]
    public void MirrorTable32_RoundTrips_BothWays()
    {
        byte[] expanded = NnueFeatureSet2.ExpandMirrorTable32(NnueFeatureSet2.MirrorTable32());
        Assert.Equal(NnueFeatureSet2.DefaultRegionTable.ToArray(), expanded);
    }

    [Fact]
    public void MirrorTable32_RefusesAnAsymmetricTable()
    {
        var table = NnueFeatureSet2.DefaultRegionTable.ToArray();
        table[0] = 5; // a1's file-mirror partner h1 (sq 7) still says 0
        Assert.Throws<InvalidOperationException>(() => NnueFeatureSet2.MirrorTable32(table));
    }

    [Fact]
    public void RegionTable_Transposition_WouldBeCaught()
    {
        // F11: the table literal is in square-index order (rank 1 FIRST). The
        // transposed reading (rank 8 first) produces a DIFFERENT 64-byte table
        // — assert the two differ, so a future edit that silently transposes
        // the literal cannot pass this suite.
        var correct = NnueFeatureSet2.DefaultRegionTable.ToArray();
        var transposed = new byte[64];
        for (int sq = 0; sq < 64; sq++)
            transposed[sq] = correct[63 - sq];
        Assert.NotEqual(correct, transposed);
        // And the correct table maps a1 -> 0 (the transposed reading maps it
        // to h8's region 7).
        Assert.Equal(0, correct[0]);
        Assert.Equal(7, transposed[0]);
    }
}
