namespace PawnForge.Core.Evaluation;

// ============================================================================
// NnueNetFile.cs — the one place a net FILE becomes a live net object (M1)
// ----------------------------------------------------------------------------
// Ported from the previous generation and reduced to what PawnForgeCS runs:
// the **v5 format only** (scheme 2). The v4 scheme-1 path of the original has
// no counterpart here — CS never carried a net, so there is no legacy format
// to stay compatible with.
//
// The version is read from the file itself (offset 4) rather than inferred from
// the file name, so renaming a file can never decide the format. An unknown
// version is rejected BY NAME — loudly, at load, before a single position is
// evaluated. That is the loader obligation, and it is why the orchestrator's
// net-load path (M6/M7) goes through here instead of picking a format itself.
//
// ⚠️ This file and the bit-exactness check (M2/M6) MUST agree on the header layout:
// they changed together for v4 in the previous generation and must change
// together for v5 — if the gate and the loader disagree, the gate validates the
// wrong bytes and reports PASS.
// ============================================================================

/// <summary>Format-dispatching net loader (v5-only in PawnForgeCS).</summary>
public static class NnueNetFile
{
    /// <summary>The formats this build can run.</summary>
    public const int SupportedVersion = 5;

    /// <summary>Reads the format version field (offset 4) without loading the
    /// net. Throws on a bad magic or an unreadable header. The engine-side
    /// equivalent of the loader's first two checks, usable for diagnostics
    /// (e.g. the loud-fallback reason at M7) without paying the full load.</summary>
    public static int PeekVersion(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != 0x4E4E434Du) // "MCNN"
            throw new InvalidDataException($"'{path}' is not a v5 net file (bad magic)");
        return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
    }

    /// <summary>Loads a net file and returns the matching net object. Unknown
    /// versions are rejected with the version named — the caller turns that
    /// into a loud load-failure path, never into a silent default.</summary>
    public static NnueNetV5 Load(string path) => PeekVersion(path) switch
    {
        5 => NnueNetV5.Load(path),
        int v => throw new InvalidDataException(
            $"'{path}': unsupported net format version {v} (this build reads v{SupportedVersion})")
    };
}
