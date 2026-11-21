// ═══════════════════════════════════════════════════════════════════════════
// SYZYGY TABLEBASE PROBER (WDL + DTZ) — 3-4-5 piece (7-man capable)
// ═══════════════════════════════════════════════════════════════════════════
//
// A C# port of the Syzygy tablebase probing code. The algorithm, the
// on-disk format handling, the bit-compressed Pairs decompression, the
// piece/square encoding/indexing and the WDL/DTZ capture-and-pawn-move
// resolution follow the public Syzygy tablebase format:
//
// • https://syzygy-tables.info/ (tablebase download/FAQ)
// • https://chessprogramming.org/Syzygy_Bases
//
// All code here is an independent C# implementation written for this
// project; third-party attribution and license notices for the format and
// the reference probing code are collected in THIRD-PARTY-NOTICES.md.
// The .rtbw/.rtbz tablebase FILES themselves are NOT redistributed here.
//
// Only the .rtbw (WDL) and .rtbz (DTZ) tables are used (DTM .rtbm files do
// not exist in practice; their structure is kept but never loaded).
//
// ═══════════════════════════════════════════════════════════════════════════

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using PawnForge.Core.Board;

namespace PawnForge.Core.Tb;

/// <summary>
/// Syzygy tablebase prober. Loads .rtbw/.rtbz files found under one or more
/// directories (multiple paths separated by ';' on Windows / ':' elsewhere)
/// and answers:
/// • <see cref="ProbeWdl(Position)"/> — Win/Draw/Loss for a leaf position
/// during search (side-to-move perspective, -2..2).
/// • <see cref="ProbeRoot(Position)"/> — best move + WDL + DTZ at the root,
/// with 50-move-rule (DTZ) awareness.
/// Probing fails gracefully (returns a "failed" result) when the material is
/// not covered, castling rights remain, or no tables are loaded.
/// </summary>
public static class Syzygy
{
    // ── Config / limits (mirrors the reference probing code) ────────────────
    private const int TB_PIECES = 7;
    private const int TB_HASHBITS = 12;
    private const int TB_MAX_PIECE = 650;
    private const int TB_MAX_PAWN = 861;
    private const int TB_MAX_SYMS = 4096;
    private const int TB_MAX_MOVES = 193;
    private const int TB_MAX_CAPTURES = 64;
    private const int TB_MAX_PLY = 256;

    // HARDENING: ProbeAb descends only through legal captures, each removing
    // at least one piece, so a valid position can never recurse more than
    // ~2 * #pieces plies (and Syzygy tables are <= 7 pieces). This generous cap
    // guarantees the WDL capture-resolution recursion terminates even if state
    // is corrupt (e.g. a memory-mapped table on a disconnected volume) instead
    // of overflowing the stack or spinning.
    private const int MAX_PROBE_DEPTH = 64;

    private const byte WDL = 0, DTM = 1, DTZ = 2;
    private const byte PIECE_ENC = 0, FILE_ENC = 1, RANK_ENC = 2;

    private const int BEST_NONE = 0xFFFF;
    private const int SCORE_ILLEGAL = 0x7FFF;
    private const ushort MOVE_STALEMATE = 0xFFFF;
    private const ushort MOVE_CHECKMATE = 0xFFFE;

    private const int TB_VALUE_PAWN = 100;
    private const int TB_VALUE_DRAW = 0;
    private const int TB_VALUE_MATE = 32000;
    private const int TB_MAX_MATE_PLY = 255;
    private const int TB_VALUE_INFINITE = 32001;

    private const byte TB_PROMOTES_NONE = 0, TB_PROMOTES_QUEEN = 1,
                       TB_PROMOTES_ROOK = 2, TB_PROMOTES_BISHOP = 3,
                       TB_PROMOTES_KNIGHT = 4;

    // piece codes (tbchess.c): white 1-6, black 9-14
    private const int W_PAWN = 1, W_KNIGHT = 2, W_BISHOP = 3, W_ROOK = 4,
                     W_QUEEN = 5, W_KING = 6;
    private const int B_PAWN = 9, B_KNIGHT = 10, B_BISHOP = 11, B_ROOK = 12,
                     B_QUEEN = 13, B_KING = 14;

    // Color: BLACK = 0, WHITE = 1 (note: opposite of leading mainstream engines)
    private const int BLACK = 0, WHITE = 1;

    private static readonly ulong[] Primes =
    {
        0, // unused
        17008651141875982339, // W pawn
        15202887380319082783, // W knight
        12311744257139811149, // W bishop
        10979190538029446137, // W rook
        11811845319353239651, // W queen
        0, 0, 0,
        11695583624105689831, // B pawn
        13469005675588064321, // B knight
        15394650811035483107, // B bishop
        18264461213049635989, // B rook
        15484752644942473553, // B queen
        0
    };

    private static readonly byte[] PieceToChar = { (byte)' ', (byte)'P', (byte)'N', (byte)'B', (byte)'R', (byte)'Q', (byte)'K',
                                                   (byte)' ', (byte)' ', (byte)'p', (byte)'n', (byte)'b', (byte)'r', (byte)'q', (byte)'k' };

    private static readonly string[] TbSuffix = { ".rtbw", ".rtbm", ".rtbz" };
    private static readonly uint[] TbMagic = { 0x5d23e871, 0x88ac504b, 0xa50c66d7 };

    // ── Geometry tables (data constants of the format) ───────────────────────
    private static readonly sbyte[] OffDiag =
    {
        0,-1,-1,-1,-1,-1,-1,-1, 1, 0,-1,-1,-1,-1,-1,-1, 1, 1, 0,-1,-1,-1,-1,-1,
        1, 1, 1, 0,-1,-1,-1,-1, 1, 1, 1, 1, 0,-1,-1,-1, 1, 1, 1, 1, 1, 0,-1,-1,
        1, 1, 1, 1, 1, 1, 0,-1, 1, 1, 1, 1, 1, 1, 1, 0
    };
    private static readonly byte[] Triangle =
    {
        6, 0, 1, 2, 2, 1, 0, 6, 0, 7, 3, 4, 4, 3, 7, 0, 1, 3, 8, 5, 5, 8, 3, 1,
        2, 4, 5, 9, 9, 5, 4, 2, 2, 4, 5, 9, 9, 5, 4, 2, 1, 3, 8, 5, 5, 8, 3, 1,
        0, 7, 3, 4, 4, 3, 7, 0, 6, 0, 1, 2, 2, 1, 0, 6
    };
    private static readonly byte[] FlipDiag =
    {
        0, 8,16,24,32,40,48,56, 1, 9,17,25,33,41,49,57, 2,10,18,26,34,42,50,58,
        3,11,19,27,35,43,51,59, 4,12,20,28,36,44,52,60, 5,13,21,29,37,45,53,61,
        6,14,22,30,38,46,54,62, 7,15,23,31,39,47,55,63
    };
    private static readonly byte[] Lower =
    {
        28, 0, 1, 2, 3, 4, 5, 6, 0,29, 7, 8, 9,10,11,12, 1, 7,30,13,14,15,16,17,
         2, 8,13,31,18,19,20,21, 3, 9,14,18,32,22,23,24, 4,10,15,19,22,33,25,26,
         5,11,16,20,23,25,34,27, 6,12,17,21,24,26,27,35
    };
    private static readonly byte[] Diag =
    {
        0, 0, 0, 0, 0, 0, 0, 8, 0, 1, 0, 0, 0, 0, 9, 0, 0, 0, 2, 0, 0,10, 0, 0,
        0, 0, 0, 3,11, 0, 0, 0, 0, 0, 0,12, 4, 0, 0, 0, 0, 0,13, 0, 0, 5, 0, 0,
        0,14, 0, 0, 0, 0, 6, 0,15, 0, 0, 0, 0, 0, 0, 7
    };
    private static readonly byte[][] Flap =
    {
        new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 6,12,18,18,12, 6, 0, 1, 7,13,19,19,13, 7, 1,
            2, 8,14,20,20,14, 8, 2, 3, 9,15,21,21,15, 9, 3, 4,10,16,22,22,16,10, 4,
            5,11,17,23,23,17,11, 5, 0, 0, 0, 0, 0, 0, 0, 0
        },
        new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 2, 1, 0, 4, 5, 6, 7, 7, 6, 5, 4,
            8, 9,10,11,11,10, 9, 8,12,13,14,15,15,14,13,12,16,17,18,19,19,18,17,16,
            20,21,22,23,23,22,21,20, 0, 0, 0, 0, 0, 0, 0, 0
        }
    };
    private static readonly byte[][] PawnTwist =
    {
        new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0,47,35,23,11,10,22,34,46,45,33,21, 9, 8,20,32,44,
            43,31,19, 7, 6,18,30,42,41,29,17, 5, 4,16,28,40,39,27,15, 3, 2,14,26,38,
            37,25,13, 1, 0,12,24,36, 0, 0, 0, 0, 0, 0, 0, 0
        },
        new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0,47,45,43,41,40,42,44,46,39,37,35,33,32,34,36,38,
            31,29,27,25,24,26,28,30,23,21,19,17,16,18,20,22,15,13,11, 9, 8,10,12,14,
            7, 5, 3, 1, 0, 2, 4, 6, 0, 0, 0, 0, 0, 0, 0, 0
        }
    };
    private static readonly short[][] KKIdx =
    {
        new short[] { -1,-1,-1, 0, 1, 2, 3, 4,-1,-1,-1, 5, 6, 7, 8, 9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57 },
        new short[] { 58,-1,-1,-1,59,60,61,62,63,-1,-1,-1,64,65,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115 },
        new short[] { 116,117,-1,-1,-1,118,119,120,121,122,-1,-1,-1,123,124,125,126,127,128,129,130,131,132,133,134,135,136,137,138,139,140,141,142,143,144,145,146,147,148,149,150,151,152,153,154,155,156,157,158,159,160,161,162,163,164,165,166,167,168,169,170,171,172,173 },
        new short[] { 174,-1,-1,-1,175,176,177,178,179,-1,-1,-1,180,181,182,183,184,-1,-1,-1,185,186,187,188,189,190,191,192,193,194,195,196,197,198,199,200,201,202,203,204,205,206,207,208,209,210,211,212,213,214,215,216,217,218,219,220,221,222,223,224,225,226,227,228 },
        new short[] { 229,230,-1,-1,-1,231,232,233,234,235,-1,-1,-1,236,237,238,239,240,-1,-1,-1,241,242,243,244,245,246,247,248,249,250,251,252,253,254,255,256,257,258,259,260,261,262,263,264,265,266,267,268,269,270,271,272,273,274,275,276,277,278,279,280,281,282,283 },
        new short[] { 284,285,286,287,288,289,290,291,292,293,-1,-1,-1,294,295,296,297,298,-1,-1,-1,299,300,301,302,303,-1,-1,-1,304,305,306,307,308,309,310,311,312,313,314,315,316,317,318,319,320,321,322,323,324,325,326,327,328,329,330,331,332,333,334,335,336,337,338 },
        new short[] { -1,-1,339,340,341,342,343,344,-1,-1,345,346,347,348,349,350,-1,-1,441,351,352,353,354,355,-1,-1,-1,442,356,357,358,359,-1,-1,-1,-1,443,360,361,362,-1,-1,-1,-1,-1,444,363,364,-1,-1,-1,-1,-1,-1,445,365,-1,-1,-1,-1,-1,-1,-1,446 },
        new short[] { -1,-1,-1,366,367,368,369,370,-1,-1,-1,371,372,373,374,375,-1,-1,-1,376,377,378,379,380,-1,-1,-1,447,381,382,383,384,-1,-1,-1,-1,448,385,386,387,-1,-1,-1,-1,-1,449,388,389,-1,-1,-1,-1,-1,-1,450,390,-1,-1,-1,-1,-1,-1,-1,451 },
        new short[] { 452,391,392,393,394,395,396,397,-1,-1,-1,-1,398,399,400,401,-1,-1,-1,-1,402,403,404,405,-1,-1,-1,-1,406,407,408,409,-1,-1,-1,-1,453,410,411,412,-1,-1,-1,-1,-1,454,413,414,-1,-1,-1,-1,-1,-1,455,415,-1,-1,-1,-1,-1,-1,-1,456 },
        new short[] { 457,416,417,418,419,420,421,422,-1,458,423,424,425,426,427,428,-1,-1,-1,-1,-1,429,430,431,-1,-1,-1,-1,-1,432,433,434,-1,-1,-1,-1,-1,435,436,437,-1,-1,-1,-1,-1,459,438,439,-1,-1,-1,-1,-1,-1,460,440,-1,-1,-1,-1,-1,-1,-1,461 }
    };
    private static readonly byte[] FileToFile = { 0, 1, 2, 3, 3, 2, 1, 0 };
    private static readonly int[] WdlToMap = { 1, 3, 0, 2, 0 };
    private static readonly byte[] PAFlags = { 8, 0, 0, 0, 4 };
    private static readonly int[] WdlToDtz = { -1, -101, 0, 101, 1 };

    private static readonly ulong[][] Binomial = new ulong[TB_PIECES][];
    private static readonly ulong[,,] PawnIdx = new ulong[2, 6, 24];
    private static readonly ulong[,] PawnFactorFile = new ulong[6, 4];
    private static readonly ulong[,] PawnFactorRank = new ulong[6, 6];

    // ── Attack tables (built at init) ────────────────────────────────────────
    private static ulong[] _kingAttacks = new ulong[64];
    private static ulong[] _knightAttacks = new ulong[64];
    private static ulong[,] _diagAttacks = new ulong[64, 64];
    private static ulong[,] _antiAttacks = new ulong[64, 64];
    private static ulong[,] _rankAttacks = new ulong[64, 64];
    private static ulong[,] _fileAttacks = new ulong[64, 64];
    private static ulong[,] _pawnAttacks = new ulong[2, 64];

    private static readonly int[] Square2Diag =
    {
        0, 1, 2, 3, 4, 5, 6, 7,14, 0, 1, 2, 3, 4, 5, 6,13,14, 0, 1, 2, 3, 4, 5,
        12,13,14, 0, 1, 2, 3, 4,11,12,13,14, 0, 1, 2, 3,10,11,12,13,14, 0, 1, 2,
        9,10,11,12,13,14, 0, 1, 8, 9,10,11,12,13,14, 0
    };
    private static readonly int[] Square2Anti =
    {
        8, 9,10,11,12,13,14, 0, 9,10,11,12,13,14, 0, 1,10,11,12,13,14, 0, 1, 2,
        11,12,13,14, 0, 1, 2, 3,12,13,14, 0, 1, 2, 3, 4,13,14, 0, 1, 2, 3, 4, 5,
        14, 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3, 4, 5, 6, 7
    };
    private static readonly ulong[] Diag2Board =
    {
        0x8040201008040201, 0x0080402010080402, 0x0000804020100804, 0x0000008040201008,
        0x0000000080402010, 0x0000000000804020, 0x0000000000008040, 0x0000000000000080,
        0x0100000000000000, 0x0201000000000000, 0x0402010000000000, 0x0804020100000000,
        0x1008040201000000, 0x2010080402010000, 0x4020100804020100
    };
    private static readonly ulong[] Anti2Board =
    {
        0x0102040810204080, 0x0204081020408000, 0x0408102040800000, 0x0810204080000000,
        0x1020408000000000, 0x2040800000000000, 0x4080000000000000, 0x8000000000000000,
        0x0000000000000001, 0x0000000000000101, 0x0000000000010204, 0x0000000001020408,
        0x0000000102040810, 0x0000010204081020, 0x0001020408102040
    };

    private const ulong BOARD_RANK_EDGE = 0x8181818181818181;
    private const ulong BOARD_FILE_EDGE = 0xFF000000000000FF;
    private const ulong BOARD_EDGE = BOARD_RANK_EDGE | BOARD_FILE_EDGE;

    // ── Loaded tables ────────────────────────────────────────────────────────
    private static readonly object _lock = new();
    private static bool _initialized;
    private static string? _pathString;
    private static string[] _paths = Array.Empty<string>();
    private static int _numWdl, _numDtm, _numDtz;
    private static int _tbNumPiece, _tbNumPawn;
    private static readonly TbEntry[] _pieceEntry = new TbEntry[TB_MAX_PIECE];
    private static readonly TbEntry[] _pawnEntry = new TbEntry[TB_MAX_PAWN];
    private static readonly TbHashEntry[] _tbHash = new TbHashEntry[1 << TB_HASHBITS];
    private static readonly List<MemoryMappedFile> _mappedFiles = new();

    /// <summary>Number of pieces the largest loaded table covers (0 = none).</summary>
    public static int MaxPieces { get; private set; }

    /// <summary>
    /// Master switch for tablebase probing during search (main search + quiescence).
    ///
    /// DEFAULT TRUE (suspension lifted 2026-09-12, owner approved): fully
    /// resolved — two root causes found and fixed:
    /// (1) the DTZ value-map reads double-added the map base (garbage DTZ on all
    /// map-bearing tables), (2) the static Anti2Board table was corrupted from index 9
    /// on (the port's capture generator missed anti-diagonal captures such as Bd4xg1).
    /// Validated: 650/650 positions consistent with python-chess across the full
    /// 3-4-5 + 6-man sets; all six original failing FENs exact; Syzygy unit suite 14/14.
    /// (History: the 2026-09-08 suspension defaulted this to false until the then-
    /// unexplained root-probe contradiction was explained.)
    /// </summary>
    public static bool ProbingEnabled { get; set; } = true;

    /// <summary>Whether the 50-move rule is honored by the DTZ root probe (Syzygy50MoveRule).</summary>
    public static bool UseRule50 { get; set; } = true;

    /// <summary>
    /// Score returned for a tablebase WIN (below the engine's mate bound so
    /// aspiration windows still treat it as a normal cp score, above any
    /// material evaluation).
    /// </summary>
    public const int TbWinScore = 29900;

    /// <summary>
    /// Largest amount by which <see cref="TbWinScore"/> is reduced to express the
    /// tablebase distance-to-zeroing. Caps the DTZ term so a very long win (6-manual DTZ can
    /// exceed 900 plies) cannot slide the score down into the ordinary evaluation range, and
    /// fixes the floor that the 50-move draw adjudication tests against.
    /// </summary>
    public const int TbWinScoreRange = 1000;

    /// <summary>
    /// Lowest score a DTZ-ordered tablebase win can take. Search results at or above this
    /// magnitude are "some kind of tablebase win", which is what
    /// <see cref="Search.SearchOptions"/>'s draw adjudication keys on — it must track this
    /// constant rather than <see cref="TbWinScore"/>, or a distance-ordered win (which is
    /// strictly BELOW <see cref="TbWinScore"/>) would slip past the clamp and let the engine
    /// announce a phantom win in an already-drawn game.
    /// </summary>
    public const int TbWinScoreMin = TbWinScore - TbWinScoreRange;

    /// <summary>Converts a WDL value (-2..2) to a search score.</summary>
    public static int WdlToScore(int wdl) => wdl switch
    {
        2 => TbWinScore,
        -2 => -TbWinScore,
        1 => 1,
        -1 => -1,
        _ => 0
    };

    /// <summary>
    /// Like <see cref="WdlToScore(int)"/>, but a decisive result is graded by the
    /// tablebase distance-to-zeroing so a faster win outranks a slower one instead of every
    /// tablebase win tying at <see cref="TbWinScore"/>.
    ///
    /// This ONLY orders wins and losses against each other; it never crosses
    /// <see cref="TbWinScore"/> upwards, so a genuine mate score (above
    /// <c>MateScoreBound</c>) still outranks every tablebase value.
    ///
    /// <paramref name="dtzAbs"/> is |DTZ| in plies; pass 0 (or any non-positive value) to get
    /// the flat <see cref="WdlToScore(int)"/> result. Cursed/blessed results (±1) are never
    /// distance-graded — they are fifty-move draws in all but name, so scaling them would
    /// claim a mate that does not exist.
    /// </summary>
    public static int WdlToScore(int wdl, int dtzAbs)
    {
        if (wdl == 2 || wdl == -2)
        {
            if (dtzAbs > 0)
            {
                int graded = TbWinScore - Math.Min(dtzAbs, TbWinScoreRange);
                return wdl == 2 ? graded : -graded;
            }
        }
        return WdlToScore(wdl);
    }

    /// <summary>True once tablebases have been initialized (even if none were found).</summary>
    public static bool IsInitialized => _initialized;

    // TEMPORARY Bug-20 diagnosis hook (remove after the DTZ decode is validated).
    /// <summary>Opt-in tablebase trace flag. Read ONCE at type initialisation,
    /// never per probe: an environment lookup on the probe path is exactly the
    /// kind of per-node cost that a sampling profile finds late. Set
    /// <c>PF_TBDEBUG=1</c> before the process starts to enable it.</summary>
    internal static readonly bool DebugProbe =
        Environment.GetEnvironmentVariable("PF_TBDEBUG") == "1";

    /// <summary>Number of WDL table files found.</summary>
    public static int WdlTableCount => _numWdl;

    // ── Internal types ───────────────────────────────────────────────────────

    /// <summary>The reference implementation's Pos: bitboards + 50-move counter + EP square + side to move.</summary>
    internal struct Pos
    {
        public ulong White, Black, Kings, Queens, Rooks, Bishops, Knights, Pawns;
        public byte Rule50;
        public byte Ep;
        public bool Turn;
    }

    /// <summary>Reads a memory-mapped tablebase file at a byte offset (endian-aware).</summary>
    internal readonly struct MapPtr
    {
        public readonly MemoryMappedViewAccessor A;
        public readonly long Off;

        public MapPtr(MemoryMappedViewAccessor a, long off) { A = a; Off = off; }

        public byte U8(long d) => A.ReadByte(Off + d);
        public ushort U16(long d) => A.ReadUInt16(Off + d);                                   // LE
        public uint U32(long d) => A.ReadUInt32(Off + d);                                     // LE
        public ulong U64(long d) => A.ReadUInt64(Off + d);                                    // LE
        public uint U32BE(long d) => BinaryPrimitives.ReverseEndianness(A.ReadUInt32(Off + d));
        public ulong U64BE(long d) => BinaryPrimitives.ReverseEndianness(A.ReadUInt64(Off + d));
        public MapPtr Add(long d) => new(A, Off + d);
    }

    internal sealed class PairsData
    {
        public MapPtr IndexTable;
        public MapPtr SizeTable;
        public MapPtr Data;
        public ushort[] Offset = Array.Empty<ushort>();
        public byte[] SymLen = Array.Empty<byte>();
        public byte[] SymPat = Array.Empty<byte>();
        public byte BlockSize;
        public byte IdxBits;
        public byte MinLen;
        public readonly byte[] ConstValue = new byte[2];
        public ulong[] Base = Array.Empty<ulong>();
    }

    internal sealed class EncInfo
    {
        public PairsData? Precomp;
        public readonly ulong[] Factor = new ulong[TB_PIECES];
        public readonly byte[] Pieces = new byte[TB_PIECES];
        public readonly byte[] Norm = new byte[TB_PIECES];
    }

    /// <summary>One material combination (a "tablebase"): up to 3 loaded tables (WDL/DTM/DTZ).</summary>
    internal sealed class TbEntry
    {
        public ulong Key;
        public readonly MapPtr[] Data = new MapPtr[3];
        public readonly bool[] Ready = new bool[3];
        public MemoryMappedFile?[] Mmap = new MemoryMappedFile?[3];
        public byte Num;
        public bool Symmetric, HasPawns, HasDtm, HasDtz, DtmLossOnly;
        public bool KkEnc;
        public byte Pawns0, Pawns1;
        public readonly EncInfo[] Ei;
        public ushort[] DtmMap = Array.Empty<ushort>();
        public readonly ushort[] DtmMapIdx = new ushort[24]; // [t][bside][s]
        public MapPtr DtzMap;
        public readonly ushort[] DtzMapIdx = new ushort[16]; // pawn [t][m], piece [m]
        public readonly byte[] DtzFlags = new byte[4];
        public bool DtmSwitched;

        public TbEntry()
        {
            Ei = new EncInfo[24];
            for (int i = 0; i < Ei.Length; i++)
                Ei[i] = new EncInfo();
        }
    }

    private struct TbHashEntry
    {
        public ulong Key;
        public TbEntry? Ptr;
        /// <summary>
        /// Bit per table kind (1 &lt;&lt; WDL/DTM/DTZ) whose file failed to load.
        /// Bug 151: this used to be ONE flag for the whole material, so a
        /// present-but-corrupt .rtbz (DTZ) also switched off the healthy .rtbw
        /// (WDL) of the same material and the search silently lost its exact
        /// tablebase scores. Each kind now fails on its own.
        /// </summary>
        public byte ErrorMask;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the tablebases. <paramref name="path"/> is one or more
    /// directories containing .rtbw/.rtbz files, separated by ';' (Windows)
    /// or ':' (other OS); an empty string or "&lt;empty&gt;" clears any tables.
    /// Returns true on success; probing simply reports "failed" when no
    /// matching table exists.
    /// </summary>
    public static bool Initialize(string path)
    {
        lock (_lock)
        {
            if (!_initialized)
            {
                InitIndices();
                InitAttackTables();
                _initialized = true;
            }

            ReleaseLocked();

            if (string.IsNullOrEmpty(path) || path == "<empty>")
                return true;

            _pathString = path;
            var seps = path.Split(Environment.OSVersion.Platform == PlatformID.Win32NT ? ';' : ':');
            _paths = seps.Where(p => p.Length > 0).ToArray();

            _tbNumPiece = _tbNumPawn = 0;
            _numWdl = _numDtm = _numDtz = 0;
            MaxPieces = 0;
            Array.Clear(_tbHash);

            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 5; i++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append("vK"); InitTb(sb.ToString()); }
            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append("vK").Append((char)PieceToChar[5 - j]); InitTb(sb.ToString()); }
            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append("vK"); InitTb(sb.ToString()); }
            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++)
                    for (int k = 0; k < 5; k++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append("vK").Append((char)PieceToChar[5 - k]); InitTb(sb.ToString()); }
            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++)
                    for (int k = j; k < 5; k++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append((char)PieceToChar[5 - k]).Append("vK"); InitTb(sb.ToString()); }

            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++)
                    for (int k = i; k < 5; k++)
                        for (int l = (i == k) ? j : k; l < 5; l++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append("vK").Append((char)PieceToChar[5 - k]).Append((char)PieceToChar[5 - l]); InitTb(sb.ToString()); }
            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++)
                    for (int k = j; k < 5; k++)
                        for (int l = 0; l < 5; l++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append((char)PieceToChar[5 - k]).Append("vK").Append((char)PieceToChar[5 - l]); InitTb(sb.ToString()); }
            for (int i = 0; i < 5; i++)
                for (int j = i; j < 5; j++)
                    for (int k = j; k < 5; k++)
                        for (int l = k; l < 5; l++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append((char)PieceToChar[5 - k]).Append((char)PieceToChar[5 - l]).Append("vK"); InitTb(sb.ToString()); }

            if (IntPtr.Size == 8)
            {
                for (int i = 0; i < 5; i++)
                    for (int j = i; j < 5; j++)
                        for (int k = j; k < 5; k++)
                            for (int l = k; l < 5; l++)
                                for (int m = l; m < 5; m++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append((char)PieceToChar[5 - k]).Append((char)PieceToChar[5 - l]).Append((char)PieceToChar[5 - m]).Append("vK"); InitTb(sb.ToString()); }
                for (int i = 0; i < 5; i++)
                    for (int j = i; j < 5; j++)
                        for (int k = j; k < 5; k++)
                            for (int l = k; l < 5; l++)
                                for (int m = 0; m < 5; m++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append((char)PieceToChar[5 - k]).Append((char)PieceToChar[5 - l]).Append("vK").Append((char)PieceToChar[5 - m]); InitTb(sb.ToString()); }
                for (int i = 0; i < 5; i++)
                    for (int j = i; j < 5; j++)
                        for (int k = j; k < 5; k++)
                            for (int l = 0; l < 5; l++)
                                for (int m = l; m < 5; m++) { sb.Clear(); sb.Append('K').Append((char)PieceToChar[5 - i]).Append((char)PieceToChar[5 - j]).Append((char)PieceToChar[5 - k]).Append("vK").Append((char)PieceToChar[5 - l]).Append((char)PieceToChar[5 - m]); InitTb(sb.ToString()); }
            }

            MaxPieces = Math.Max(MaxPieces, 0);
            return true;
        }
    }

    /// <summary>Releases all mapped tables and resets the prober.</summary>
    public static void Release()
    {
        lock (_lock)
        {
            ReleaseLocked();
        }
    }

    private static void ReleaseLocked()
    {
        for (int i = 0; i < _tbNumPiece; i++) FreeEntry(_pieceEntry[i]);
        for (int i = 0; i < _tbNumPawn; i++) FreeEntry(_pawnEntry[i]);
        foreach (var mmf in _mappedFiles) mmf.Dispose();
        _mappedFiles.Clear();
        _pathString = null;
        _paths = Array.Empty<string>();
        _numWdl = _numDtm = _numDtz = 0;
        _tbNumPiece = _tbNumPawn = 0;
        MaxPieces = 0;
        Array.Clear(_tbHash);
    }

    private static void FreeEntry(TbEntry? be)
    {
        if (be is null) return;
        for (int type = 0; type < 3; type++)
        {
            if (be.Ready[type])
            {
                int num = NumTables(be, type);
                int baseIdx = FirstEiIndex(be, type);
                for (int t = 0; t < num; t++)
                {
                    be.Ei[baseIdx + t].Precomp = null;
                    if (type != DTZ)
                        be.Ei[baseIdx + num + t].Precomp = null;
                }
                be.Ready[type] = false;
            }
        }
    }

    /// <summary>
    /// Probes the WDL table for <paramref name="pos"/>. Returns -2 (loss),
    /// -1 (cursed loss), 0 (draw), 1 (cursed win), 2 (win) from the side to
    /// move's perspective, or <see cref="int.MinValue"/> when the position is
    /// not covered / probing is not possible (e.g. castling rights remain).
    /// </summary>
    public static int ProbeWdl(Position pos)
    {
        lock (_lock)
        {
            // HARDENING: no exception may escape a probe. A corrupt or
            // disconnected memory-mapped table (e.g. the drive was unplugged
            // mid-run) can make the low-level decoder throw or, without the
            // bounded loops in DecompressPairs, spin at 100% CPU. Catching
            // here turns any failure into "position not covered" so the caller
            // (search, UCI root probe, test harness) always continues.
            try
            {
                // The reference WDL wrapper refuses to probe when castling
                // rights exist or rule50 != 0 (during search the counter is
                // ignored, exactly like leading mainstream engines).
                if (!_initialized || pos.CastlingRights != 0)
                    return int.MinValue;
                var p = ToPos(pos, rule50: 0);
                if (!IsValid(p))
                    return int.MinValue;
                int success;
                int v = ProbeWdl(ref p, out success);
                return success == 0 ? int.MinValue : v;
            }
            catch
            {
                return int.MinValue;
            }
        }
    }

    /// <summary>
    /// Probes the DTZ table for <paramref name="pos"/> and returns the distance to
    /// zeroing in plies from the side to move's perspective, or <see cref="int.MinValue"/>
    /// when the position is not covered / probing is not possible.
    ///
    /// Mirrors <see cref="ProbeWdl(Position)"/> exactly in its preconditions and hardening
    /// (lock, no exception escapes, refuses castling rights, uses rule50: 0 so it means the
    /// same thing as the WDL probe during search) so the two can never disagree about
    /// whether a position is in the tables.
    ///
    /// Deliberately NOT used to synthesise mate scores. DTZ is the distance to a ZEROING
    /// move (capture / pawn move), not the distance to mate, and a winner can zero far faster
    /// than it can mate. Measured on <c>8/8/8/8/8/8/4p3/R3K2k w</c>: DTZ is 1 while true DTM is
    /// 7 plies (mate 4), so a DTZ-derived score would announce "mate 1" for a mate-in-4. DTZ is
    /// used only to ORDER tablebase wins, never to state a mate distance.
    /// </summary>
    public static int ProbeDtz(Position pos)
    {
        lock (_lock)
        {
            try
            {
                if (!_initialized || pos.CastlingRights != 0)
                    return int.MinValue;
                var p = ToPos(pos, rule50: 0);
                if (!IsValid(p))
                    return int.MinValue;
                int success;
                int v = ProbeDtz(ref p, out success);
                return success == 0 ? int.MinValue : v;
            }
            catch
            {
                return int.MinValue;
            }
        }
    }

    /// <summary>Result of a root probe: best move + WDL + DTZ (50-move aware).</summary>
    public readonly struct RootResult
    {
        public readonly Move Move;
        public readonly int Wdl;      // -2..2 (side to move)
        public readonly int Dtz;      // distance-to-zero in plies (0 = draw)
        public readonly bool IsCheckmate;
        public readonly bool IsStalemate;
        public readonly bool Failed;
        internal RootResult(Move move, int wdl, int dtz, bool mate, bool stale, bool failed)
        { Move = move; Wdl = wdl; Dtz = dtz; IsCheckmate = mate; IsStalemate = stale; Failed = failed; }
        public static RootResult Fail => new(Move.None, 0, 0, false, false, true);
    }

    /// <summary>
    /// Probes the DTZ tables at the root and returns the best move together
    /// with the WDL value and distance-to-zero. The returned move preserves
    /// the WDL value and is 50-move-rule aware (DTZ). Handles mate/stalemate.
    /// </summary>
    public static RootResult ProbeRoot(Position pos)
    {
        lock (_lock)
        {
            if (!_initialized || pos.CastlingRights != 0)
                return RootResult.Fail;

            var p = ToPos(pos, rule50: UseRule50 ? Math.Clamp(pos.HalfmoveClock, 0, 100) : 0);
            if (!IsValid(p))
                return RootResult.Fail;
            var results = new uint[TB_MAX_MOVES];
            ushort move = ProbeRootImpl(ref p, results, out int dtz);
            if (move == 0)
                return RootResult.Fail;
            if (move == MOVE_CHECKMATE)
                return new RootResult(Move.None, 0, 0, true, false, false);
            if (move == MOVE_STALEMATE)
                return new RootResult(Move.None, 0, 0, false, true, false);

            int from = (move >> 6) & 0x3f;
            int to = move & 0x3f;
            int promotes = (move >> 12) & 0x7;
            int wdl = DtzToWdl(p.Rule50, dtz) - 2; // -2..2, side to move

            // Convert to a real Move, matched against the legal moves so all
            // flags (promotion piece, en passant) are correct.
            Span<Move> legal = stackalloc Move[256];
            int n = pos.GenerateMoves(legal);
            Move mv = Move.None;
            for (int i = 0; i < n; i++)
            {
                var m = legal[i];
                if (m.From != from || m.To != to) continue;
                if (promotes != TB_PROMOTES_NONE)
                {
                    int wantType = promotes switch
                    {
                        TB_PROMOTES_QUEEN => 4,
                        TB_PROMOTES_ROOK => 3,
                        TB_PROMOTES_BISHOP => 2,
                        _ => 1
                    };
                    if (m.Promoted == 0 || ((Piece)m.Promoted).ToIndex() != wantType) continue;
                }
                else if (m.Promoted != 0) continue;
                mv = m;
                break;
            }
            if (mv.IsNone)
                return RootResult.Fail;

            return new RootResult(mv, wdl, dtz < 0 ? -dtz : dtz, false, false, false);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BOARD LAYER (port of tbchess.c)
    // ═══════════════════════════════════════════════════════════════════════

    private static int ColorOfPiece(int piece) => (piece >> 3) == 0 ? WHITE : BLACK;
    private static int TypeOfPiece(int piece) => piece & 7;
    private static ulong Board(int sq) => 1UL << sq;

    private static ulong PiecesByType(in Pos pos, int color, int type)
    {
        ulong mask = color == WHITE ? pos.White : pos.Black;
        return type switch
        {
            1 => pos.Pawns & mask,
            2 => pos.Knights & mask,
            3 => pos.Bishops & mask,
            4 => pos.Rooks & mask,
            5 => pos.Queens & mask,
            _ => pos.Kings & mask,
        };
    }

    private static ulong KingAttacks(int sq) => _kingAttacks[sq];
    private static ulong KnightAttacks(int sq) => _knightAttacks[sq];

    private static ulong BishopAttacks(int sq, ulong occ)
    {
        occ &= ~Board(sq);
        int d = Square2Diag[sq], a = Square2Anti[sq];
        ulong dOcc = occ & (Diag2Board[d] & ~BOARD_EDGE);
        ulong aOcc = occ & (Anti2Board[a] & ~BOARD_EDGE);
        ulong dIdx = Diag2Index(dOcc);
        ulong aIdx = Anti2Index(aOcc);
        return _diagAttacks[sq, dIdx] | _antiAttacks[sq, aIdx];
    }

    private static ulong RookAttacks(int sq, ulong occ)
    {
        occ &= ~Board(sq);
        int r = sq >> 3, f = sq & 7;
        ulong rOcc = occ & ((0xFFUL << (8 * r)) & ~BOARD_RANK_EDGE);
        ulong fOcc = occ & ((0x0101010101010101UL << f) & ~BOARD_FILE_EDGE);
        ulong rIdx = Rank2Index(rOcc, r);
        ulong fIdx = File2Index(fOcc, f);
        return _rankAttacks[sq, rIdx] | _fileAttacks[sq, fIdx];
    }

    private static ulong QueenAttacks(int sq, ulong occ) => RookAttacks(sq, occ) | BishopAttacks(sq, occ);
    private static ulong PawnAttacks(int sq, int color) => _pawnAttacks[color, sq];

    /// <summary>TEMPORARY Bug-20 diagnosis hook (remove after validation): exposes the port's attack tables.</summary>
    public static ulong DebugBishopAttacks(int sq, ulong occ) => BishopAttacks(sq, occ);
    public static ulong DebugQueenAttacks(int sq, ulong occ) => QueenAttacks(sq, occ);
    public static (int dIdx, int aIdx) DebugDiagIdx(int sq, ulong occ)
    {
        occ &= ~Board(sq);
        int d = Square2Diag[sq], a = Square2Anti[sq];
        ulong dOcc = occ & (Diag2Board[d] & ~BOARD_EDGE);
        ulong aOcc = occ & (Anti2Board[a] & ~BOARD_EDGE);
        return ((int)Diag2Index(dOcc), (int)Anti2Index(aOcc));
    }

    private static ulong Diag2Index(ulong b)
    {
        b *= 0x0101010101010101UL;
        b >>= 56;
        b >>= 1;
        return b;
    }
    private static ulong Anti2Index(ulong b) => Diag2Index(b);
    private static ulong Rank2Index(ulong b, int r)
    {
        b >>= (8 * r);
        b >>= 1;
        return b;
    }
    private static ulong File2Index(ulong b, int f)
    {
        b >>= f;
        b *= 0x0102040810204080UL;
        b >>= 56;
        b >>= 1;
        return b;
    }

    private static void InitAttackTables()
    {
        for (int s = 0; s < 64; s++)
        {
            int r = s >> 3, f = s & 7;
            ulong b = 0;
            if (r != 0 && f != 0) b |= Board(((r - 1) << 3) | (f - 1));
            if (r != 0) b |= Board(((r - 1) << 3) | f);
            if (r != 0 && f != 7) b |= Board(((r - 1) << 3) | (f + 1));
            if (f != 7) b |= Board((r << 3) | (f + 1));
            if (r != 7 && f != 7) b |= Board(((r + 1) << 3) | (f + 1));
            if (r != 7) b |= Board(((r + 1) << 3) | f);
            if (r != 7 && f != 0) b |= Board(((r + 1) << 3) | (f - 1));
            if (f != 0) b |= Board((r << 3) | (f - 1));
            _kingAttacks[s] = b;

            b = 0;
            void Add(int dr, int df)
            {
                int rr = r + dr, ff = f + df;
                if (rr >= 0 && rr <= 7 && ff >= 0 && ff <= 7) b |= Board((rr << 3) | ff);
            }
            Add(-1, -2); Add(-1, 2); Add(-2, -1); Add(-2, 1);
            Add(1, -2); Add(1, 2); Add(2, -1); Add(2, 1);
            _knightAttacks[s] = b;
        }

        // bishop: diag / anti tables
        for (int idx = 0; idx < 64; idx++)
        {
            int idx1 = idx << 1;
            for (int s = 0; s < 64; s++)
            {
                int r = s >> 3, f = s & 7;
                ulong b = 0;
                for (int i = -1; f + i >= 0 && r + i >= 0; i--)
                {
                    int occ = 1 << (f + i);
                    b |= Board(((r + i) << 3) | (f + i));
                    if ((idx1 & occ) != 0) break;
                }
                for (int i = 1; f + i <= 7 && r + i <= 7; i++)
                {
                    int occ = 1 << (f + i);
                    b |= Board(((r + i) << 3) | (f + i));
                    if ((idx1 & occ) != 0) break;
                }
                _diagAttacks[s, idx] = b;
            }
        }
        for (int idx = 0; idx < 64; idx++)
        {
            int idx1 = idx << 1;
            for (int s = 0; s < 64; s++)
            {
                int r = s >> 3, f = s & 7;
                ulong b = 0;
                for (int i = -1; f + i >= 0 && r - i <= 7; i--)
                {
                    int occ = 1 << (f + i);
                    b |= Board(((r - i) << 3) | (f + i));
                    if ((idx1 & occ) != 0) break;
                }
                for (int i = 1; f + i <= 7 && r - i >= 0; i++)
                {
                    int occ = 1 << (f + i);
                    b |= Board(((r - i) << 3) | (f + i));
                    if ((idx1 & occ) != 0) break;
                }
                _antiAttacks[s, idx] = b;
            }
        }

        // rook: rank / file tables
        for (int idx = 0; idx < 64; idx++)
        {
            int idx1 = idx << 1;
            int occ;
            for (int f = 0; f <= 7; f++)
            {
                ulong b = 0;
                if (f > 0)
                {
                    int i = f - 1;
                    do
                    {
                        occ = 1 << i;
                        b |= Board(i);
                        i--;
                    } while ((idx1 & occ) == 0 && i >= 0);
                }
                if (f < 7)
                {
                    int i = f + 1;
                    do
                    {
                        occ = 1 << i;
                        b |= Board(i);
                        i++;
                    } while ((idx1 & occ) == 0 && i <= 7);
                }
                for (int r = 0; r <= 7; r++)
                {
                    _rankAttacks[(r << 3) | f, idx] = b;
                    b <<= 8;
                }
            }
        }
        for (int idx = 0; idx < 64; idx++)
        {
            int idx1 = idx << 1;
            int occ;
            for (int r = 0; r <= 7; r++)
            {
                ulong b = 0;
                if (r > 0)
                {
                    int i = r - 1;
                    do
                    {
                        occ = 1 << i;
                        b |= Board(i << 3);
                        i--;
                    } while ((idx1 & occ) == 0 && i >= 0);
                }
                if (r < 7)
                {
                    int i = r + 1;
                    do
                    {
                        occ = 1 << i;
                        b |= Board(i << 3);
                        i++;
                    } while ((idx1 & occ) == 0 && i <= 7);
                }
                for (int f = 0; f <= 7; f++)
                {
                    _fileAttacks[(r << 3) | f, idx] = b;
                    b <<= 1;
                }
            }
        }

        // pawn attacks: color 1 = white (up), color 0 = black (down)
        for (int s = 0; s < 64; s++)
        {
            int r = s >> 3, f = s & 7;
            ulong b = 0;
            if (r != 7)
            {
                if (f != 0) b |= Board(((r + 1) << 3) | (f - 1));
                if (f != 7) b |= Board(((r + 1) << 3) | (f + 1));
            }
            _pawnAttacks[1, s] = b;
            b = 0;
            if (r != 0)
            {
                if (f != 0) b |= Board(((r - 1) << 3) | (f - 1));
                if (f != 7) b |= Board(((r - 1) << 3) | (f + 1));
            }
            _pawnAttacks[0, s] = b;
        }
    }

    private static ulong CalcKey(in Pos pos, bool mirror)
    {
        ulong white = pos.White, black = pos.Black;
        if (mirror) { (white, black) = (black, white); }
        return (ulong)BitOperations.PopCount(white & pos.Queens) * Primes[W_QUEEN] +
               (ulong)BitOperations.PopCount(white & pos.Rooks) * Primes[W_ROOK] +
               (ulong)BitOperations.PopCount(white & pos.Bishops) * Primes[W_BISHOP] +
               (ulong)BitOperations.PopCount(white & pos.Knights) * Primes[W_KNIGHT] +
               (ulong)BitOperations.PopCount(white & pos.Pawns) * Primes[W_PAWN] +
               (ulong)BitOperations.PopCount(black & pos.Queens) * Primes[B_QUEEN] +
               (ulong)BitOperations.PopCount(black & pos.Rooks) * Primes[B_ROOK] +
               (ulong)BitOperations.PopCount(black & pos.Bishops) * Primes[B_BISHOP] +
               (ulong)BitOperations.PopCount(black & pos.Knights) * Primes[B_KNIGHT] +
               (ulong)BitOperations.PopCount(black & pos.Pawns) * Primes[B_PAWN];
    }

    private static ulong CalcKeyFromPcs(int[] pcs, bool mirror)
    {
        int m = mirror ? 8 : 0;
        return (ulong)pcs[(W_QUEEN ^ m) & 15] * Primes[W_QUEEN] +
               (ulong)pcs[(W_ROOK ^ m) & 15] * Primes[W_ROOK] +
               (ulong)pcs[(W_BISHOP ^ m) & 15] * Primes[W_BISHOP] +
               (ulong)pcs[(W_KNIGHT ^ m) & 15] * Primes[W_KNIGHT] +
               (ulong)pcs[(W_PAWN ^ m) & 15] * Primes[W_PAWN] +
               (ulong)pcs[(B_QUEEN ^ m) & 15] * Primes[B_QUEEN] +
               (ulong)pcs[(B_ROOK ^ m) & 15] * Primes[B_ROOK] +
               (ulong)pcs[(B_BISHOP ^ m) & 15] * Primes[B_BISHOP] +
               (ulong)pcs[(B_KNIGHT ^ m) & 15] * Primes[B_KNIGHT] +
               (ulong)pcs[(B_PAWN ^ m) & 15] * Primes[B_PAWN];
    }

    private static ushort MakeMove(int promote, int from, int to) =>
        (ushort)(((promote & 7) << 12) | ((from & 0x3f) << 6) | (to & 0x3f));
    private static int MoveFrom(ushort m) => (m >> 6) & 0x3f;
    private static int MoveTo(ushort m) => m & 0x3f;
    private static int MovePromotes(ushort m) => (m >> 12) & 0x7;

    private static int TypeOfPieceMoved(in Pos pos, ushort move)
    {
        ulong sq = Board(MoveFrom(move));
        for (int i = 1; i <= 6; i++)
        {
            if ((PiecesByType(pos, pos.Turn ? WHITE : BLACK, i) & sq) != 0)
                return i;
        }
        return 0;
    }

    private static int AddMove(Span<ushort> moves, int at, bool promotes, int from, int to)
    {
        if (!promotes)
        {
            moves[at++] = MakeMove(TB_PROMOTES_NONE, from, to);
        }
        else
        {
            moves[at++] = MakeMove(TB_PROMOTES_QUEEN, from, to);
            moves[at++] = MakeMove(TB_PROMOTES_KNIGHT, from, to);
            moves[at++] = MakeMove(TB_PROMOTES_ROOK, from, to);
            moves[at++] = MakeMove(TB_PROMOTES_BISHOP, from, to);
        }
        return at;
    }

    /// <summary>Generates (at least) all legal captures including (under)promotions and EP.</summary>
    private static int GenCaptures(in Pos pos, Span<ushort> moves)
    {
        ulong occ = pos.White | pos.Black;
        ulong us = pos.Turn ? pos.White : pos.Black;
        ulong them = pos.Turn ? pos.Black : pos.White;
        ulong b, att;
        int at = 0;

        ulong kingBB = pos.Kings & us;
        if (kingBB == 0)
            return 0; // defensive: no king of the side to move (illegal position)
        int fromKing = BitOperations.TrailingZeroCount(kingBB);
        att = KingAttacks(fromKing) & them;
        while (att != 0)
        {
            int to = BitOperations.TrailingZeroCount(att);
            at = AddMove(moves, at, false, fromKing, to);
            att &= att - 1;
        }
        b = us & pos.Queens;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = QueenAttacks(from, occ) & them;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Rooks;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = RookAttacks(from, occ) & them;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Bishops;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = BishopAttacks(from, occ) & them;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Knights;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = KnightAttacks(from) & them;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Pawns;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = PawnAttacks(from, pos.Turn ? WHITE : BLACK);
            if (pos.Ep != 0 && (att & Board(pos.Ep)) != 0)
            {
                int to = pos.Ep;
                at = AddMove(moves, at, false, from, to);
            }
            att &= them;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                bool promo = (to >> 3) == 7 || (to >> 3) == 0;
                at = AddMove(moves, at, promo, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        return at;
    }

    /// <summary>Generates all (pseudo-legal) moves.</summary>
    private static int GenMoves(in Pos pos, Span<ushort> moves)
    {
        ulong occ = pos.White | pos.Black;
        ulong us = pos.Turn ? pos.White : pos.Black;
        ulong them = pos.Turn ? pos.Black : pos.White;
        ulong b, att;
        int at = 0;

        ulong kingBB = pos.Kings & us;
        if (kingBB == 0)
            return 0; // defensive: no king of the side to move (illegal position)
        int fromKing = BitOperations.TrailingZeroCount(kingBB);
        att = KingAttacks(fromKing) & ~us;
        while (att != 0)
        {
            int to = BitOperations.TrailingZeroCount(att);
            at = AddMove(moves, at, false, fromKing, to);
            att &= att - 1;
        }
        b = us & pos.Queens;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = QueenAttacks(from, occ) & ~us;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Rooks;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = RookAttacks(from, occ) & ~us;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Bishops;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = BishopAttacks(from, occ) & ~us;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Knights;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            att = KnightAttacks(from) & ~us;
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                at = AddMove(moves, at, false, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        b = us & pos.Pawns;
        while (b != 0)
        {
            int from = BitOperations.TrailingZeroCount(b);
            int next = from + (pos.Turn ? 8 : -8);
            att = PawnAttacks(from, pos.Turn ? WHITE : BLACK);
            if (pos.Ep != 0 && (att & Board(pos.Ep)) != 0)
            {
                int to = pos.Ep;
                at = AddMove(moves, at, false, from, to);
            }
            att &= them;
            if ((Board(next) & occ) == 0)
            {
                att |= Board(next);
                int next2 = from + (pos.Turn ? 16 : -16);
                if ((pos.Turn ? (from >> 3) == 1 : (from >> 3) == 6) && (Board(next2) & occ) == 0)
                    att |= Board(next2);
            }
            while (att != 0)
            {
                int to = BitOperations.TrailingZeroCount(att);
                bool promo = (to >> 3) == 7 || (to >> 3) == 0;
                at = AddMove(moves, at, promo, from, to);
                att &= att - 1;
            }
            b &= b - 1;
        }
        return at;
    }

    private static bool IsEnPassant(in Pos pos, ushort move)
    {
        int from = MoveFrom(move);
        int to = MoveTo(move);
        ulong us = pos.Turn ? pos.White : pos.Black;
        if (pos.Ep == 0) return false;
        if (to != pos.Ep) return false;
        if ((Board(from) & us & pos.Pawns) == 0) return false;
        return true;
    }

    private static bool IsCapture(in Pos pos, ushort move)
    {
        int to = MoveTo(move);
        ulong them = pos.Turn ? pos.Black : pos.White;
        return (them & Board(to)) != 0 || IsEnPassant(pos, move);
    }

    /// <summary>Tests whether the position is legal (kings safe, no pawns on back ranks, etc.).</summary>
    private static bool IsLegal(in Pos pos)
    {
        ulong occ = pos.White | pos.Black;
        ulong us = pos.Turn ? pos.Black : pos.White;
        ulong them = pos.Turn ? pos.White : pos.Black;
        ulong king = pos.Kings & us;
        if (king == 0) return false;
        int sq = BitOperations.TrailingZeroCount(king);
        if ((KingAttacks(sq) & (pos.Kings & them)) != 0) return false;
        ulong ratt = RookAttacks(sq, occ);
        ulong batt = BishopAttacks(sq, occ);
        if ((ratt & (pos.Rooks & them)) != 0) return false;
        if ((batt & (pos.Bishops & them)) != 0) return false;
        if (((ratt | batt) & (pos.Queens & them)) != 0) return false;
        if ((KnightAttacks(sq) & (pos.Knights & them)) != 0) return false;
        if ((PawnAttacks(sq, pos.Turn ? BLACK : WHITE) & (pos.Pawns & them)) != 0) return false;
        return true;
    }

    private static bool IsCheck(in Pos pos)
    {
        ulong occ = pos.White | pos.Black;
        ulong us = pos.Turn ? pos.White : pos.Black;
        ulong them = pos.Turn ? pos.Black : pos.White;
        ulong king = pos.Kings & us;
        int sq = BitOperations.TrailingZeroCount(king);
        ulong ratt = RookAttacks(sq, occ);
        ulong batt = BishopAttacks(sq, occ);
        if ((ratt & (pos.Rooks & them)) != 0) return true;
        if ((batt & (pos.Bishops & them)) != 0) return true;
        if (((ratt | batt) & (pos.Queens & them)) != 0) return true;
        if ((KnightAttacks(sq) & (pos.Knights & them)) != 0) return true;
        if ((PawnAttacks(sq, pos.Turn ? WHITE : BLACK) & (pos.Pawns & them)) != 0) return true;
        return false;
    }

    private static bool IsValid(in Pos pos)
    {
        if (BitOperations.PopCount(pos.Kings) != 2) return false;
        if (BitOperations.PopCount(pos.Kings & pos.White) != 1) return false;
        if (BitOperations.PopCount(pos.Kings & pos.Black) != 1) return false;
        if ((pos.White & pos.Black) != 0) return false;
        if ((pos.Kings & (pos.Queens | pos.Rooks | pos.Bishops | pos.Knights | pos.Pawns)) != 0) return false;
        if ((pos.Queens & (pos.Rooks | pos.Bishops | pos.Knights | pos.Pawns)) != 0) return false;
        if ((pos.Rooks & (pos.Bishops | pos.Knights | pos.Pawns)) != 0) return false;
        if ((pos.Bishops & (pos.Knights | pos.Pawns)) != 0) return false;
        if ((pos.Knights & pos.Pawns) != 0) return false;
        if ((pos.Pawns & BOARD_FILE_EDGE) != 0) return false;
        if ((pos.White | pos.Black) != (pos.Kings | pos.Queens | pos.Rooks | pos.Bishops | pos.Knights | pos.Pawns))
            return false;
        return IsLegal(pos);
    }

    private static ulong DoBbMove(ulong b, int from, int to)
        => (b & ~Board(to) & ~Board(from)) | (((b >> from) & 1) << to);

    /// <summary>Makes a move (writes into <paramref name="pos"/>); false if the move was illegal.</summary>
    private static bool DoMove(ref Pos pos, in Pos pos0, ushort move)
    {
        int from = MoveFrom(move);
        int to = MoveTo(move);
        int promotes = MovePromotes(move);
        pos.Turn = !pos0.Turn;
        pos.White = DoBbMove(pos0.White, from, to);
        pos.Black = DoBbMove(pos0.Black, from, to);
        pos.Kings = DoBbMove(pos0.Kings, from, to);
        pos.Queens = DoBbMove(pos0.Queens, from, to);
        pos.Rooks = DoBbMove(pos0.Rooks, from, to);
        pos.Bishops = DoBbMove(pos0.Bishops, from, to);
        pos.Knights = DoBbMove(pos0.Knights, from, to);
        pos.Pawns = DoBbMove(pos0.Pawns, from, to);
        pos.Ep = 0;
        if (promotes != TB_PROMOTES_NONE)
        {
            pos.Pawns &= ~Board(to);
            switch (promotes)
            {
                case TB_PROMOTES_QUEEN: pos.Queens |= Board(to); break;
                case TB_PROMOTES_ROOK: pos.Rooks |= Board(to); break;
                case TB_PROMOTES_BISHOP: pos.Bishops |= Board(to); break;
                case TB_PROMOTES_KNIGHT: pos.Knights |= Board(to); break;
            }
            pos.Rule50 = 0;
        }
        else if ((Board(from) & pos0.Pawns) != 0)
        {
            pos.Rule50 = 0;
            if ((from >> 3) == 1 && (to >> 3) == 3 &&
                (PawnAttacks(from + 8, WHITE) & pos0.Pawns & pos0.Black) != 0)
                pos.Ep = (byte)(from + 8);
            else if ((from >> 3) == 6 && (to >> 3) == 4 &&
                (PawnAttacks(from - 8, BLACK) & pos0.Pawns & pos0.White) != 0)
                pos.Ep = (byte)(from - 8);
            else if (to == pos0.Ep)
            {
                int epTo = pos0.Turn ? to - 8 : to + 8;
                ulong epMask = ~Board(epTo);
                pos.White &= epMask;
                pos.Black &= epMask;
                pos.Pawns &= epMask;
            }
        }
        else if ((Board(to) & (pos0.White | pos0.Black)) != 0)
            pos.Rule50 = 0;
        else
            pos.Rule50 = (byte)(pos0.Rule50 + 1);

        return IsLegal(pos);
    }

    private static bool LegalMove(in Pos pos, ushort move)
    {
        Pos pos1 = default;
        return DoMove(ref pos1, pos, move);
    }

    private static bool IsMate(in Pos pos)
    {
        if (!IsCheck(pos)) return false;
        Span<ushort> moves = stackalloc ushort[TB_MAX_MOVES];
        int end = GenMoves(pos, moves);
        for (int i = 0; i < end; i++)
        {
            Pos pos1 = default;
            if (DoMove(ref pos1, pos, moves[i]))
                return false;
        }
        return true;
    }

    private static int GenLegal(in Pos pos, Span<ushort> moves)
    {
        Span<ushort> pl = stackalloc ushort[TB_MAX_MOVES];
        int end = GenMoves(pos, pl);
        int at = 0;
        for (int i = 0; i < end; i++)
        {
            if (LegalMove(pos, pl[i]))
                moves[at++] = pl[i];
        }
        return at;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INDEXING / ENCODING (port of tbprobe.c)
    // ═══════════════════════════════════════════════════════════════════════

    private static void InitIndices()
    {
        for (int i = 0; i < TB_PIECES; i++)
        {
            Binomial[i] = new ulong[64];
            for (int j = 0; j < 64; j++)
            {
                ulong f = 1, l = 1;
                for (int k = 0; k < i; k++)
                {
                    f *= (ulong)(j - k);
                    l *= (ulong)(k + 1);
                }
                Binomial[i][j] = i == 0 ? 1 : f / l;
            }
        }

        for (int i = 0; i < 6; i++)
        {
            ulong s = 0;
            for (int j = 0; j < 24; j++)
            {
                PawnIdx[0, i, j] = s;
                s += Binomial[i][PawnTwist[0][((1 + (j % 6)) * 8) + (j / 6)]];
                if ((j + 1) % 6 == 0)
                {
                    PawnFactorFile[i, j / 6] = s;
                    s = 0;
                }
            }
        }
        for (int i = 0; i < 6; i++)
        {
            ulong s = 0;
            for (int j = 0; j < 24; j++)
            {
                PawnIdx[1, i, j] = s;
                s += Binomial[i][PawnTwist[1][((1 + (j / 4)) * 8) + (j % 4)]];
                if ((j + 1) % 4 == 0)
                {
                    PawnFactorRank[i, j / 4] = s;
                    s = 0;
                }
            }
        }
    }

    private static int LeadingPawn(int[] p, TbEntry be, int enc)
    {
        for (int i = 1; i < be.Pawns0; i++)
        {
            if (Flap[enc - 1][p[0]] > Flap[enc - 1][p[i]])
                (p[0], p[i]) = (p[i], p[0]);
        }
        return enc == FILE_ENC ? FileToFile[p[0] & 7] : (p[0] - 8) >> 3;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ulong Encode(int[] p, EncInfo ei, TbEntry be, int enc)
    {
        int n = be.Num;
        ulong idx;
        int k;

        if ((p[0] & 0x04) != 0)
            for (int i = 0; i < n; i++) p[i] ^= 0x07;

        if (enc == PIECE_ENC)
        {
            if ((p[0] & 0x20) != 0)
                for (int i = 0; i < n; i++) p[i] ^= 0x38;

            for (int i = 0; i < n; i++)
            {
                if (OffDiag[p[i]] != 0)
                {
                    if (OffDiag[p[i]] > 0 && i < (be.KkEnc ? 2 : 3))
                        for (int j = 0; j < n; j++) p[j] = FlipDiag[p[j]];
                    break;
                }
            }

            if (be.KkEnc)
            {
                idx = (ulong)KKIdx[Triangle[p[0]]][p[1]];
                k = 2;
            }
            else
            {
                int s1 = p[1] > p[0] ? 1 : 0;
                int s2 = (p[2] > p[0] ? 1 : 0) + (p[2] > p[1] ? 1 : 0);
                if (OffDiag[p[0]] != 0)
                    idx = (ulong)Triangle[p[0]] * 63 * 62 + (ulong)(p[1] - s1) * 62 + (ulong)(p[2] - s2);
                else if (OffDiag[p[1]] != 0)
                    idx = 6UL * 63 * 62 + (ulong)Diag[p[0]] * 28 * 62 + (ulong)Lower[p[1]] * 62 + (ulong)(p[2] - s2);
                else if (OffDiag[p[2]] != 0)
                    idx = 6UL * 63 * 62 + 4UL * 28 * 62 + (ulong)Diag[p[0]] * 7 * 28 + (ulong)(Diag[p[1]] - s1) * 28 + Lower[p[2]];
                else
                    idx = 6UL * 63 * 62 + 4UL * 28 * 62 + 4UL * 7 * 28 + (ulong)Diag[p[0]] * 7 * 6 + (ulong)(Diag[p[1]] - s1) * 6 + (ulong)(Diag[p[2]] - s2);
                k = 3;
            }
            idx *= ei.Factor[0];
        }
        else
        {
            for (int i = 1; i < be.Pawns0; i++)
                for (int j = i + 1; j < be.Pawns0; j++)
                    if (PawnTwist[enc - 1][p[i]] < PawnTwist[enc - 1][p[j]])
                        (p[i], p[j]) = (p[j], p[i]);

            k = be.Pawns0;
            idx = PawnIdx[enc - 1, k - 1, Flap[enc - 1][p[0]]];
            for (int i = 1; i < k; i++)
                idx += Binomial[k - i][PawnTwist[enc - 1][p[i]]];
            idx *= ei.Factor[0];

            if (be.Pawns1 != 0)
            {
                int t = k + be.Pawns1;
                for (int i = k; i < t; i++)
                    for (int j = i + 1; j < t; j++)
                        if (p[i] > p[j]) (p[i], p[j]) = (p[j], p[i]);
                ulong s = 0;
                for (int i = k; i < t; i++)
                {
                    int sq = p[i];
                    int skips = 0;
                    for (int j = 0; j < k; j++)
                        skips += sq > p[j] ? 1 : 0;
                    s += Binomial[i - k + 1][sq - skips - 8];
                }
                idx += s * ei.Factor[k];
                k = t;
            }
        }

        for (; k < n;)
        {
            int t = k + ei.Norm[k];
            for (int i = k; i < t; i++)
                for (int j = i + 1; j < t; j++)
                    if (p[i] > p[j]) (p[i], p[j]) = (p[j], p[i]);
            ulong s = 0;
            for (int i = k; i < t; i++)
            {
                int sq = p[i];
                int skips = 0;
                for (int j = 0; j < k; j++)
                    skips += sq > p[j] ? 1 : 0;
                s += Binomial[i - k + 1][sq - skips];
            }
            idx += s * ei.Factor[k];
            k = t;
        }

        return idx;
    }

    /// <summary>C(n, k).</summary>
    private static ulong Subfactor(int k, int n)
    {
        ulong f = (ulong)n, l = 1;
        for (int i = 1; i < k; i++)
        {
            f *= (ulong)(n - i);
            l *= (ulong)(i + 1);
        }
        return f / l;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ulong InitEncInfo(EncInfo ei, TbEntry be, MapPtr tb, int shift, int t, int enc)
    {
        bool morePawns = enc != PIECE_ENC && be.Pawns1 > 0;

        for (int i = 0; i < be.Num; i++)
        {
            ei.Pieces[i] = (byte)((tb.U8(i + 1 + (morePawns ? 1 : 0)) >> shift) & 0x0f);
            ei.Norm[i] = 0;
        }

        int order = (tb.U8(0) >> shift) & 0x0f;
        int order2 = morePawns ? (tb.U8(1) >> shift) & 0x0f : 0x0f;

        int k = ei.Norm[0] = (byte)(enc != PIECE_ENC ? be.Pawns0 : be.KkEnc ? 2 : 3);

        if (morePawns)
        {
            ei.Norm[k] = be.Pawns1;
            k += ei.Norm[k];
        }

        for (int i = k; i < be.Num; i += ei.Norm[i])
            for (int j = i; j < be.Num && ei.Pieces[j] == ei.Pieces[i]; j++)
                ei.Norm[i]++;

        int n = 64 - k;
        ulong f = 1;

        for (int i = 0; k < be.Num || i == order || i == order2; i++)
        {
            if (i == order)
            {
                ei.Factor[0] = f;
                f *= enc == FILE_ENC ? PawnFactorFile[ei.Norm[0] - 1, t]
                    : enc == RANK_ENC ? PawnFactorRank[ei.Norm[0] - 1, t]
                    : be.KkEnc ? 462UL : 31332UL;
            }
            else if (i == order2)
            {
                ei.Factor[ei.Norm[0]] = f;
                f *= Subfactor(ei.Norm[ei.Norm[0]], 48 - ei.Norm[0]);
            }
            else
            {
                ei.Factor[k] = f;
                f *= Subfactor(ei.Norm[k], n);
                n -= ei.Norm[k];
                k += ei.Norm[k];
            }
        }

        return f;
    }

    private static void CalcSymLen(PairsData d, int s, byte[] tmp)
    {
        byte[] w = d.SymPat;
        int w3 = 3 * s;
        int s2 = (w[w3 + 2] << 4) | (w[w3 + 1] >> 4);
        if (s2 == 0x0fff)
        {
            d.SymLen[s] = 0;
        }
        else
        {
            int s1 = ((w[w3 + 1] & 0xf) << 8) | w[w3];
            if (tmp[s1] == 0) CalcSymLen(d, s1, tmp);
            if (tmp[s2] == 0) CalcSymLen(d, s2, tmp);
            d.SymLen[s] = (byte)(d.SymLen[s1] + d.SymLen[s2] + 1);
        }
        tmp[s] = 1;
    }

    private static PairsData? SetupPairs(ref long dataOff, MemoryMappedViewAccessor accessor, ulong tbSize, long[] size, ref byte flags, int type)
    {
        var data = new MapPtr(accessor, dataOff);
        PairsData d;

        flags = data.U8(0);
        if ((data.U8(0) & 0x80) != 0)
        {
            d = new PairsData
            {
                IdxBits = 0,
            };
            d.ConstValue[0] = type == WDL ? data.U8(1) : (byte)0;
            d.ConstValue[1] = 0;
            dataOff = dataOff + 2;
            size[0] = size[1] = size[2] = 0;
            return d;
        }
        byte blockSize = data.U8(1);
        byte idxBits = data.U8(2);
        uint realNumBlocks = data.U32(4);
        uint numBlocks = realNumBlocks + data.U8(3);
        int maxLen = data.U8(8);
        int minLen = data.U8(9);
        int h = maxLen - minLen + 1;
        uint numSyms = data.U16(10 + 2 * h);

        d = new PairsData { BlockSize = blockSize, IdxBits = idxBits, MinLen = (byte)minLen };
        d.Offset = new ushort[h];
        for (int i = 0; i < h; i++) d.Offset[i] = data.U16(10 + 2 * i);
        d.SymLen = new byte[numSyms];
        d.SymPat = new byte[3 * numSyms];
        for (int i = 0; i < 3 * numSyms; i++) d.SymPat[i] = data.U8(12 + 2 * h + i);
        d.Base = new ulong[h];

        dataOff = dataOff + 12 + 2 * h + 3 * numSyms + (numSyms & 1);

        ulong numIndices = (tbSize + (1UL << idxBits) - 1) >> idxBits;
        size[0] = 6L * (long)numIndices;
        size[1] = 2L * numBlocks;
        size[2] = (long)realNumBlocks << blockSize;

        var tmp = new byte[numSyms];
        for (uint s = 0; s < numSyms; s++)
            if (tmp[s] == 0)
                CalcSymLen(d, (int)s, tmp);

        d.Base[h - 1] = 0;
        for (int i = h - 2; i >= 0; i--)
            d.Base[i] = (d.Base[i + 1] + d.Offset[i] - d.Offset[i + 1]) / 2;
        for (int i = 0; i < h; i++)
            d.Base[i] <<= 64 - (minLen + i);

        return d;
    }

    private static bool InitTable(TbEntry be, string str, int type)
    {
        MemoryMappedFile mmf;
        MemoryMappedViewAccessor accessor;
        try
        {
            mmf = MemoryMappedFile.CreateFromFile(ResolvePath(str + TbSuffix[type]), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch
        {
            return false;
        }
        lock (_mappedFiles) _mappedFiles.Add(mmf);
        var data = new MapPtr(accessor, 0);

        if (data.U32(0) != TbMagic[type])
        {
            accessor.Dispose();
            return false;
        }

        be.Data[type] = data;
        be.Mmap[type] = mmf;

        bool split = type != DTZ && (data.U8(4) & 0x01) != 0;
        if (type == DTM)
            be.DtmLossOnly = (data.U8(4) & 0x04) != 0;

        long dataOff = 5;
        ulong[,] tbSize = new ulong[6, 2];
        int num = NumTables(be, type);
        int eiBase = FirstEiIndex(be, type);
        int enc = !be.HasPawns ? PIECE_ENC : type != DTM ? FILE_ENC : RANK_ENC;

        for (int t = 0; t < num; t++)
        {
            tbSize[t, 0] = InitEncInfo(be.Ei[eiBase + t], be, new MapPtr(accessor, dataOff), 0, t, enc);
            if (split)
                tbSize[t, 1] = InitEncInfo(be.Ei[eiBase + num + t], be, new MapPtr(accessor, dataOff), 4, t, enc);
            dataOff += be.Num + 1 + (be.HasPawns && be.Pawns1 != 0 ? 1 : 0);
        }
        dataOff += dataOff & 1;

        long[,,] size = new long[6, 2, 3];
        for (int t = 0; t < num; t++)
        {
            byte flags = 0;
            var sz0 = new long[3];
            be.Ei[eiBase + t].Precomp = SetupPairs(ref dataOff, accessor, tbSize[t, 0], sz0, ref flags, type);
            size[t, 0, 0] = sz0[0]; size[t, 0, 1] = sz0[1]; size[t, 0, 2] = sz0[2];
            if (type == DTZ)
            {
                if (!be.HasPawns) be.DtzFlags[0] = flags;
                else be.DtzFlags[t] = flags;
            }
            if (split)
            {
                var sz1 = new long[3];
                be.Ei[eiBase + num + t].Precomp = SetupPairs(ref dataOff, accessor, tbSize[t, 1], sz1, ref flags, type);
                size[t, 1, 0] = sz1[0]; size[t, 1, 1] = sz1[1]; size[t, 1, 2] = sz1[2];
            }
            else if (type != DTZ)
                be.Ei[eiBase + num + t].Precomp = null;
        }

        if (type == DTZ)
        {
            be.DtzMap = new MapPtr(accessor, dataOff);
            for (int t = 0; t < num; t++)
            {
                byte fl = be.HasPawns ? be.DtzFlags[t] : be.DtzFlags[0];
                if ((fl & 2) != 0)
                {
                    if ((fl & 16) == 0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            be.DtzMapIdx[(be.HasPawns ? t * 4 : 0) + i] = (ushort)(dataOff + 1 - be.DtzMap.Off);
                            dataOff += 1 + data.U8(dataOff);
                        }
                    }
                    else
                    {
                        dataOff += dataOff & 1;
                        for (int i = 0; i < 4; i++)
                        {
                            // The reference implementation stores mapIdx in uint16-UNITS
                            // ((uint16_t*)data + 1 - (uint16_t*)map) and probes
                            // ((uint16_t*)map)[mapIdx + v] — a byte address of
                            // mapBase + 2*mapIdx + 2*v. This port keeps BYTE offsets
                            // in DtzMapIdx (dataOff is 2-aligned here), so the DTZ
                            // decode below must scale by 2.: the old code
                            // stored byte units AND read unscaled — every u16-map
                            // table (KQvKR/KRvKP/...) decoded garbage DTZ values
                            // (observed 315-430 where genuine max is ~70).
                            be.DtzMapIdx[(be.HasPawns ? t * 4 : 0) + i] = (ushort)(dataOff - be.DtzMap.Off);
                            dataOff += 2 + 2 * data.U16(dataOff);
                        }
                    }
                }
            }
            dataOff += dataOff & 1;
        }

        for (int t = 0; t < num; t++)
        {
            be.Ei[eiBase + t].Precomp!.IndexTable = new MapPtr(accessor, dataOff);
            dataOff += size[t, 0, 0];
            if (split)
            {
                be.Ei[eiBase + num + t].Precomp!.IndexTable = new MapPtr(accessor, dataOff);
                dataOff += size[t, 1, 0];
            }
        }
        for (int t = 0; t < num; t++)
        {
            be.Ei[eiBase + t].Precomp!.SizeTable = new MapPtr(accessor, dataOff);
            dataOff += size[t, 0, 1];
            if (split)
            {
                be.Ei[eiBase + num + t].Precomp!.SizeTable = new MapPtr(accessor, dataOff);
                dataOff += size[t, 1, 1];
            }
        }
        for (int t = 0; t < num; t++)
        {
            dataOff = (dataOff + 0x3f) & ~0x3fL;
            be.Ei[eiBase + t].Precomp!.Data = new MapPtr(accessor, dataOff);
            dataOff += size[t, 0, 2];
            if (split)
            {
                dataOff = (dataOff + 0x3f) & ~0x3fL;
                be.Ei[eiBase + num + t].Precomp!.Data = new MapPtr(accessor, dataOff);
                dataOff += size[t, 1, 2];
            }
        }

        return true;
    }

    private static string ResolvePath(string path)
    {
        foreach (string dir in _paths)
        {
            var full = Path.Combine(dir, path);
            if (File.Exists(full))
                return full;
        }
        throw new FileNotFoundException(path);
    }

    /// <summary>Decompresses the Pairs-compressed value for index <paramref name="idx"/>; returns the two value bytes.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (byte V0, byte V1) DecompressPairs(PairsData d, ulong idx)
    {
        // Constant-compressed tables store the same value for every index.
        if (d.IdxBits == 0)
            return (d.ConstValue[0], d.ConstValue[1]);

        uint mainIdx = (uint)(idx >> d.IdxBits);
        int litIdx = (int)((idx & ((1UL << d.IdxBits) - 1)) - (1UL << (d.IdxBits - 1)));
        uint block = d.IndexTable.U32(6L * mainIdx);
        int idxOffset = d.IndexTable.U16(6L * mainIdx + 4);
        litIdx += idxOffset;

        // HARDENING: `block` indexes into SizeTable (2 bytes/entry). With a
        // corrupt or disconnected memory-mapped table the SizeTable entries
        // can be garbage, which would otherwise make the backward/forward
        // adjustment loops below walk off the end forever (observed as a
        // 100% CPU spin when a table volume was unplugged mid-run). Clamp the
        // walk to the SizeTable's actual extent: valid lookups stay well
        // inside, garbage terminates instead of spinning.
        long sizeBytes = d.SizeTable.A.Capacity - d.SizeTable.Off;
        int maxBlock = sizeBytes <= 0 ? 0 : (int)Math.Min((long)int.MaxValue, sizeBytes / 2 - 1);
        if (block > (uint)maxBlock)
            throw new InvalidOperationException($"decompress: block {block} out of range (maxBlock {maxBlock})");

        if (litIdx < 0)
        {
            while (litIdx < 0 && block > 0)
            {
                block--;
                litIdx += d.SizeTable.U16(2L * block) + 1;
            }
        }
        else
        {
            while (litIdx > d.SizeTable.U16(2L * block) && block < (uint)maxBlock)
            {
                litIdx -= d.SizeTable.U16(2L * block) + 1;
                block++;
            }
        }

        long ptr = d.Data.Off + ((long)block << d.BlockSize);
        var acc = d.Data.A;

        int m = d.MinLen;
        ulong code = BinaryPrimitives.ReverseEndianness(acc.ReadUInt64(ptr));
        ptr += 8;
        int bitCnt = 0;
        uint sym = 0;

        for (;;)
        {
            int l = m;
            while (l - m < d.Base.Length && code < d.Base[l - m]) l++;
            if (l - m >= d.Base.Length)
                throw new InvalidOperationException($"decompress: l {l} out of range (h {d.Base.Length}, minLen {m})");
            sym = d.Offset[l - m];
            sym += (uint)((code - d.Base[l - m]) >> (64 - l));
            if (litIdx < d.SymLen[sym] + 1) break;
            litIdx -= d.SymLen[sym] + 1;
            code <<= l;
            bitCnt += l;
            if (bitCnt >= 32)
            {
                bitCnt -= 32;
                uint tmp = BinaryPrimitives.ReverseEndianness(acc.ReadUInt32(ptr));
                ptr += 4;
                code |= (ulong)tmp << bitCnt;
            }
        }

        byte[] symPat = d.SymPat;
        if (sym >= d.SymLen.Length)
            throw new InvalidOperationException($"decompress: sym {sym} >= numSyms {d.SymLen.Length} (idx {idx}, block {block}, minLen {m})");
        while (d.SymLen[sym] != 0)
        {
            int w3 = 3 * (int)sym;
            int s1 = ((symPat[w3 + 1] & 0xf) << 8) | symPat[w3];
            if (litIdx < d.SymLen[s1] + 1)
                sym = (uint)s1;
            else
            {
                litIdx -= d.SymLen[s1] + 1;
                sym = (uint)((symPat[w3 + 2] << 4) | (symPat[w3 + 1] >> 4));
            }
            if (sym >= d.SymLen.Length)
                throw new InvalidOperationException($"decompress: child sym {sym} >= numSyms {d.SymLen.Length}");
        }

        int off = 3 * (int)sym;
        return (symPat[off], symPat[off + 1]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int FillSquares(in Pos pos, byte[] pc, bool flip, int mirror, int[] p, int i)
    {
        int color = ColorOfPiece(pc[i]);
        if (flip) color = color == WHITE ? BLACK : WHITE;
        ulong bb = PiecesByType(pos, color, TypeOfPiece(pc[i]));
        while (bb != 0)
        {
            int sq = BitOperations.TrailingZeroCount(bb);
            p[i++] = sq ^ mirror;
            bb &= bb - 1;
        }
        return i;
    }

    private static int NumTables(TbEntry be, int type) => be.HasPawns ? (type == DTM ? 6 : 4) : 1;

    /// <summary>Base index into <see cref="TbEntry.Ei"/> for a table type (mirrors the reference's first_ei).</summary>
    private static int FirstEiIndex(TbEntry be, int type) => be.HasPawns
        ? type == WDL ? 0 : type == DTM ? 8 : 20
        : type == WDL ? 0 : type == DTM ? 2 : 4;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ProbeTable(in Pos pos, int s, out int success, int type)
    {
        success = 1;
        ulong key = CalcKey(pos, false);

        // KvK
        if (type == WDL && key == 0) return 0;

        int hashSize = 1 << TB_HASHBITS;
        int hashIdx = (int)(key >> (64 - TB_HASHBITS));
        // HARDENING: bounded linear probe. The 4096-slot table only ever holds
        // the loaded .rtbw/.rtbz tables (a few hundred), so it cannot fill in
        // practice; the bound guarantees we never spin even on corrupt state.
        int probed = 0;
        while (_tbHash[hashIdx].Ptr != null && _tbHash[hashIdx].Key != key && probed < hashSize)
        {
            hashIdx = (hashIdx + 1) & (hashSize - 1);
            probed++;
        }
        if (probed >= hashSize || _tbHash[hashIdx].Ptr == null || (_tbHash[hashIdx].ErrorMask & (1 << type)) != 0)
        {
            success = 0;
            return 0;
        }

        var be = _tbHash[hashIdx].Ptr!;
        if ((type == DTM && !be.HasDtm) || (type == DTZ && !be.HasDtz))
        {
            success = 0;
            return 0;
        }

        if (!be.Ready[type])
        {
            if ((_tbHash[hashIdx].ErrorMask & (1 << type)) != 0)
            {
                success = 0;
                return 0;
            }
            if (!be.Ready[type])
            {
                string str = PrtStr(pos, be.Key != key);
                if (!InitTable(be, str, type))
                {
                    _tbHash[hashIdx].ErrorMask |= (byte)(1 << type);
                    success = 0;
                    return 0;
                }
                be.Ready[type] = true;
            }
        }

        bool bside, flip;
        if (!be.Symmetric)
        {
            flip = key != be.Key;
            bside = (pos.Turn == true) == flip;
        }
        else
        {
            flip = pos.Turn != true;
            bside = false;
        }

        int eiBase = FirstEiIndex(be, type);
        int[] p = new int[TB_PIECES];
        ulong idx;
        int t = 0;
        byte flags = 0;

        if (!be.HasPawns)
        {
            if (type == DTZ)
            {
                flags = be.DtzFlags[0];
                if (((flags & 1) != 0) != bside && !be.Symmetric)
                {
                    success = -1;
                    return 0;
                }
            }
            var ei = type == DTZ ? be.Ei[eiBase] : be.Ei[eiBase + (bside ? 1 : 0)];
            int i = 0;
            while (i < be.Num)
                i = FillSquares(pos, ei.Pieces, flip, 0, p, i);
            idx = Encode(p, ei, be, PIECE_ENC);
            var (w0, w1) = DecompressPairs(ei.Precomp!, idx);

            if (type == WDL)
                return w0 - 2;

            int v = w0 + ((w1 & 0x0f) << 8);

            if (type == DTZ)
            {
                if (DebugProbe)
                    Console.Error.WriteLine($"[dtzdbg] tbl={be.Key:X16} s={s} bside={bside} flip={flip} flags={flags} " +
                                            $"idx={idx} w0={w0} w1={w1} v={v} kkenc={be.KkEnc} " +
                                            $"p=[{string.Join(",", p.Take(be.Num))}] " +
                                            $"pieces=[{string.Join(",", ei.Pieces.Take(be.Num).Select(x => $"0x{x:x}"))}] " +
                                            $"norm=[{string.Join(",", ei.Norm.Take(be.Num))}] factor=[{string.Join(",", ei.Factor.Take(6))}]");
                if ((flags & 2) != 0)
                {
                    int m = WdlToMap[s + 2];
                    // MapPtr.U8/U16 add DtzMap.Off internally, so only the
                    // map-relative index (DtzMapIdx) may be passed here.
                    // root cause: passing the ABSOLUTE file offset
                    // (baseOff + mapIdx) double-added the map base — every map
                    // read landed ~2*baseOff bytes into the file and decoded a
                    // garbage byte (observed dtz 315-430 where genuine max ~70).
                    int mapIdx = be.DtzMapIdx[m];
                    if ((flags & 16) == 0)
                        v = be.DtzMap.U8(mapIdx + v);
                    else
                        // u16 map: mapIdx is a BYTE offset (of the 2-byte length
                        // field); the values start 2 bytes later and each value index
                        // step is 2 bytes — the reference indexes ((uint16_t*)map)[mapIdx + v].
                        v = be.DtzMap.U16(mapIdx + 2 + 2 * v);
                    if (DebugProbe)
                        Console.Error.WriteLine($"[dtzdbg]   map: m={m} mapIdx={mapIdx} -> v_after_map={v}");
                }
                if (((flags & PAFlags[s + 2]) == 0) || (s & 1) != 0)
                    v *= 2;
            }
            return v;
        }

        // pawn tables
        int i2 = FillSquares(pos, be.Ei[eiBase].Pieces, flip, flip ? 0x38 : 0, p, 0);
        t = LeadingPawn(p, be, type != DTM ? FILE_ENC : RANK_ENC);
        if (type == DTZ)
        {
            flags = be.DtzFlags[t];
            if (((flags & 1) != 0) != bside && !be.Symmetric)
            {
                success = -1;
                return 0;
            }
        }
        var eiP = type == WDL ? be.Ei[eiBase + t + 4 * (bside ? 1 : 0)]
            : type == DTM ? be.Ei[eiBase + t + 6 * (bside ? 1 : 0)]
            : be.Ei[eiBase + t];
        while (i2 < be.Num)
            i2 = FillSquares(pos, eiP.Pieces, flip, flip ? 0x38 : 0, p, i2);
        idx = type != DTM ? Encode(p, eiP, be, FILE_ENC) : Encode(p, eiP, be, RANK_ENC);

        if (DebugProbe)
            Console.Error.WriteLine($"[dtzdbg-pawn] tbl={be.Key:X16} s={s} bside={bside} flip={flip} flags={flags} " +
                                    $"t={t} idx={idx} kkenc={be.KkEnc} " +
                                    $"p=[{string.Join(",", p.Take(be.Num))}] " +
                                    $"pieces=[{string.Join(",", eiP.Pieces.Take(be.Num).Select(x => $"0x{x:x}"))}] " +
                                    $"norm=[{string.Join(",", eiP.Norm.Take(be.Num))}] factor=[{string.Join(",", eiP.Factor.Take(6))}]");

        var (pw0, pw1) = DecompressPairs(eiP.Precomp!, idx);

        if (type == WDL)
            return pw0 - 2;

        int vP = pw0 + ((pw1 & 0x0f) << 8);

        if (type == DTZ)
        {
            if ((flags & 2) != 0)
            {
                int m = WdlToMap[s + 2];
                // Same Bug-20 double-offset fix as the pawnless branch: MapPtr
                // already adds DtzMap.Off — pass only the map-relative index.
                int mapIdx = be.DtzMapIdx[t * 4 + m];
                if ((flags & 16) == 0)
                    vP = be.DtzMap.U8(mapIdx + vP);
                else
                    // u16 map: byte-offset mapIdx + 2-byte length header + 2 bytes per value.
                    vP = be.DtzMap.U16(mapIdx + 2 + 2 * vP);
            }
            if (((flags & PAFlags[s + 2]) == 0) || (s & 1) != 0)
                vP *= 2;
        }
        return vP;
    }

    // probe_ab is not called for positions with en-passant captures
    private static int ProbeAb(in Pos pos, int alpha, int beta, out int success) => ProbeAb(pos, alpha, beta, out success, 0);

    private static int ProbeAb(in Pos pos, int alpha, int beta, out int success, int depth)
    {
        // HARDENING: recursion guard - see MAX_PROBE_DEPTH. Bail out as "not
        // covered" instead of recursing forever on corrupt state.
        if (depth > MAX_PROBE_DEPTH)
        {
            success = 0;
            return 0;
        }

        Span<ushort> moves = stackalloc ushort[TB_MAX_CAPTURES];
        int end = GenCaptures(pos, moves);
        for (int i = 0; i < end; i++)
        {
            ushort move = moves[i];
            if (!IsCapture(pos, move)) continue;
            Pos pos1 = default;
            if (!DoMove(ref pos1, pos, move)) continue; // illegal move
            int v = -ProbeAb(pos1, -beta, -alpha, out success, depth + 1);
            if (success == 0) return 0;
            if (v > alpha)
            {
                if (v >= beta) return v;
                alpha = v;
            }
        }

        int w = ProbeTable(pos, 0, out success, WDL);
        return alpha >= w ? alpha : w;
    }

    /// <summary>
    /// Probes the WDL table (with capture / en-passant resolution).
    /// success: 1 = ok, 2 = winning capture or ep-capture-only best move,
    /// 0 = failed. Return: -2..2 from the side to move's perspective.
    /// </summary>
    private static int ProbeWdl(ref Pos pos, out int success)
    {
        success = 1;

        Span<ushort> moves = stackalloc ushort[TB_MAX_CAPTURES];
        int end = GenCaptures(pos, moves);
        int bestCap = -3, bestEp = -3;

        for (int i = 0; i < end; i++)
        {
            ushort move = moves[i];
            if (!IsCapture(pos, move)) continue;
            Pos pos1 = default;
            if (!DoMove(ref pos1, pos, move)) continue; // illegal
            int v = -ProbeAb(pos1, -2, -bestCap, out success, 1);
            if (success == 0) return 0;
            if (v > bestCap)
            {
                if (v == 2)
                {
                    success = 2;
                    return 2;
                }
                if (!IsEnPassant(pos, move))
                    bestCap = v;
                else if (v > bestEp)
                    bestEp = v;
            }
        }

        int w = ProbeTable(pos, 0, out success, WDL);
        if (success == 0) return 0;

        if (bestEp > bestCap)
        {
            if (bestEp > w) // ep capture (possibly cursed losing) is best
            {
                success = 2;
                return bestEp;
            }
            bestCap = bestEp;
        }

        if (bestCap >= w)
        {
            success = 1 + (bestCap > 0 ? 1 : 0);
            return bestCap;
        }

        // Stalemate / en passant case
        if (bestEp > -3 && w == 0)
        {
            Span<ushort> all = stackalloc ushort[TB_MAX_MOVES];
            int end2 = GenMoves(pos, all);
            int i;
            for (i = 0; i < end2; i++)
            {
                if (!IsEnPassant(pos, all[i]) && LegalMove(pos, all[i])) break;
            }
            if (i == end2 && !IsCheck(pos))
            {
                success = 2;
                return bestEp;
            }
        }

        return w;
    }

    /// <summary>
    /// Probes the DTZ table. Returns from the side to move's perspective:
    /// n &lt; -100 cursed loss, -100..-1 loss in |n| ply (50-move counter 0),
    /// 0 draw, 1..100 win in n ply, &gt;100 cursed win. -1 instead of 0 at mate.
    /// </summary>
    private static int ProbeDtz(ref Pos pos, out int success)
    {
        int wdl = ProbeWdl(ref pos, out success);
        if (success == 0) return 0;
        if (wdl == 0) return 0;

        // winning capture or en-passant capture as only best move
        if (success == 2)
            return WdlToDtz[wdl + 2];

        Span<ushort> moves = stackalloc ushort[TB_MAX_MOVES];
        int end = 0;
        Pos pos1 = default;

        if (wdl > 0)
        {
            // winning: check for a winning non-capturing pawn move
            end = GenLegal(pos, moves);
            for (int i = 0; i < end; i++)
            {
                ushort move = moves[i];
                if (TypeOfPieceMoved(pos, move) != 1 || IsCapture(pos, move)) continue;
                if (!DoMove(ref pos1, pos, move)) continue;
                int v = -ProbeWdl(ref pos1, out success);
                if (success == 0) return 0;
                if (v == wdl)
                    return WdlToDtz[wdl + 2];
            }
        }

        // No ep capture is best; safe to probe DTZ with current wdl.
        int dtz = ProbeTable(pos, wdl, out success, DTZ);
        if (success >= 0)
            return WdlToDtz[wdl + 2] + (wdl > 0 ? dtz : -dtz);

        // success < 0: need to probe DTZ for the other side to move.
        // For wdl > 0, `end` still holds the legal moves generated above; for
        // wdl < 0 we generate all moves here (mirrors the C reference).
        int best;
        if (wdl > 0)
        {
            best = int.MaxValue;
        }
        else
        {
            best = WdlToDtz[wdl + 2];
            end = GenMoves(pos, moves);
        }

        for (int i = 0; i < end; i++)
        {
            ushort move = moves[i];
            if (IsCapture(pos, move) || TypeOfPieceMoved(pos, move) == 1) continue;
            if (!DoMove(ref pos1, pos, move)) continue;
            int v = -ProbeDtz(ref pos1, out success);
            if (v == 1 && IsMate(pos1))
                best = 1;
            else if (wdl > 0)
            {
                if (v > 0 && v + 1 < best) best = v + 1;
            }
            else
            {
                if (v - 1 < best) best = v - 1;
            }
            if (success == 0) return 0;
        }
        return best;
    }

    private static int DtzToWdl(int cnt50, int dtz)
    {
        int wdl;
        if (dtz > 0)
            wdl = dtz + cnt50 <= 100 ? 2 : 1;
        else if (dtz < 0)
            wdl = -dtz + cnt50 <= 100 ? -2 : -1;
        else
            wdl = 0;
        return wdl + 2;
    }

    /// <summary>Root-probe API: returns best move (0 = fail, 0xFFFE mate, 0xFFFF stalemate).</summary>
    private static ushort ProbeRootImpl(ref Pos pos, uint[] results, out int score)
    {
        int success;
        int dtz = ProbeDtz(ref pos, out success);
        if (success == 0)
        {
            score = 0;
            return 0;
        }

        Span<int> scores = stackalloc int[TB_MAX_MOVES];
        Span<ushort> moves = stackalloc ushort[TB_MAX_MOVES];
        int end = GenMoves(pos, moves);
        int len = end;
        int numDraw = 0;
        int j = 0;
        for (int i = 0; i < len; i++)
        {
            Pos pos1 = default;
            if (!DoMove(ref pos1, pos, moves[i]))
            {
                scores[i] = SCORE_ILLEGAL;
                continue;
            }
            int v;
            if (dtz > 0 && IsMate(pos1))
                v = 1;
            else
            {
                if (pos1.Rule50 != 0)
                {
                    v = -ProbeDtz(ref pos1, out success);
                    if (v > 0) v++;
                    else if (v < 0) v--;
                }
                else
                {
                    v = -ProbeWdl(ref pos1, out success);
                    v = WdlToDtz[v + 2];
                }
            }
            numDraw += v == 0 ? 1 : 0;
            if (success == 0)
            {
                score = 0;
                return 0;
            }
            scores[i] = v;
            if (results != null)
            {
                uint res = 0;
                res |= (uint)((DtzToWdl(pos.Rule50, v)) & 0x0f);
                res |= (uint)((MoveFrom(moves[i]) & 0x3f) << 10);
                res |= (uint)((MoveTo(moves[i]) & 0x3f) << 4);
                res |= (uint)((MovePromotes(moves[i]) & 0x7) << 16);
                if (IsEnPassant(pos, moves[i])) res |= 0x00080000;
                res |= (uint)((v < 0 ? -v : v) & 0xfff) << 20;
                results[j++] = res;
            }
        }
        if (results != null)
            results[j] = 0xFFFFFFFF;
        score = dtz;

        if (dtz > 0) // winning (or 50-move rule draw)
        {
            int best = BEST_NONE;
            ushort bestMove = 0;
            for (int i = 0; i < len; i++)
            {
                int v = scores[i];
                if (v == SCORE_ILLEGAL) continue;
                if (v > 0 && v < best)
                {
                    best = v;
                    bestMove = moves[i];
                }
            }
            return best == BEST_NONE ? (ushort)0 : bestMove;
        }
        if (dtz < 0) // losing (or 50-move rule draw)
        {
            int best = 0;
            ushort bestMove = 0;
            for (int i = 0; i < len; i++)
            {
                int v = scores[i];
                if (v == SCORE_ILLEGAL) continue;
                if (v < best)
                {
                    best = v;
                    bestMove = moves[i];
                }
            }
            return best == 0 ? MOVE_CHECKMATE : bestMove;
        }
        // drawing
        if (numDraw == 0)
            return MOVE_STALEMATE;

        // select a "random" draw-preserving move (calc_key as PRNG)
        ulong count = CalcKey(pos, !pos.Turn) % (ulong)numDraw;
        for (int i = 0; i < len; i++)
        {
            int v = scores[i];
            if (v == SCORE_ILLEGAL) continue;
            if (v == 0)
            {
                if (count == 0) return moves[i];
                count--;
            }
        }
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INIT (file scanning) helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string PrtStr(in Pos pos, bool flip)
    {
        int color = flip ? BLACK : WHITE;
        var sb = new System.Text.StringBuilder(16);
        for (int pt = 6; pt >= 1; pt--)
            for (int i = BitOperations.PopCount(PiecesByType(pos, color, pt)); i > 0; i--)
                sb.Append((char)PieceToChar[pt]);
        sb.Append('v');
        color ^= 1;
        for (int pt = 6; pt >= 1; pt--)
            for (int i = BitOperations.PopCount(PiecesByType(pos, color, pt)); i > 0; i--)
                sb.Append((char)PieceToChar[pt]);
        return sb.ToString();
    }

    private static bool TestTb(string str, string suffix)
    {
        foreach (string dir in _paths)
        {
            var full = Path.Combine(dir, str + suffix);
            if (!File.Exists(full)) continue;
            long size;
            try
            {
                size = new FileInfo(full).Length;
            }
            catch
            {
                continue;
            }
            if ((size & 63) == 16) return true;
        }
        return false;
    }

    private static void InitTb(string str)
    {
        if (!TestTb(str, TbSuffix[WDL])) return;

        int[] pcs = new int[16];
        int color = 0;
        foreach (char c in str)
        {
            if (c == 'v') color = 8;
            else
            {
                int pieceType = CharToPieceType(c);
                if (pieceType != 0)
                    pcs[pieceType | color]++;
            }
        }

        ulong key = CalcKeyFromPcs(pcs, false);
        ulong key2 = CalcKeyFromPcs(pcs, true);

        bool hasPawns = pcs[W_PAWN] != 0 || pcs[B_PAWN] != 0;

        var be = hasPawns ? _pawnEntry[_tbNumPawn++] = new TbEntry()
                          : _pieceEntry[_tbNumPiece++] = new TbEntry();
        be.HasPawns = hasPawns;
        be.Key = key;
        be.Symmetric = key == key2;
        be.Num = 0;
        for (int i = 0; i < 16; i++)
            be.Num += (byte)pcs[i];

        _numWdl++;
        be.HasDtm = TestTb(str, TbSuffix[DTM]);
        _numDtm += be.HasDtm ? 1 : 0;
        be.HasDtz = TestTb(str, TbSuffix[DTZ]);
        _numDtz += be.HasDtz ? 1 : 0;

        if (be.Num > MaxPieces) MaxPieces = be.Num;
        if (be.HasDtm && be.Num > MaxPieces) MaxPieces = be.Num;

        if (!be.HasPawns)
        {
            int j = 0;
            for (int i = 0; i < 16; i++)
                if (pcs[i] == 1) j++;
            be.KkEnc = j == 2;
        }
        else
        {
            be.Pawns0 = (byte)pcs[W_PAWN];
            be.Pawns1 = (byte)pcs[B_PAWN];
            if (pcs[B_PAWN] != 0 && (pcs[W_PAWN] == 0 || pcs[W_PAWN] > pcs[B_PAWN]))
                (be.Pawns0, be.Pawns1) = (be.Pawns1, be.Pawns0);
        }

        AddToHash(be, key);
        if (key != key2)
            AddToHash(be, key2);
    }

    private static int CharToPieceType(char c)
    {
        for (int i = 1; i <= 6; i++)
            if (c == (char)PieceToChar[i])
                return i;
        return 0;
    }

    private static void AddToHash(TbEntry ptr, ulong key)
    {
        int hashSize = 1 << TB_HASHBITS;
        int idx = (int)(key >> (64 - TB_HASHBITS));
        int probed = 0;
        // HARDENING: bounded probe (see ProbeTable). Never spin on a full
        // table - overwrite the home slot instead.
        while (_tbHash[idx].Ptr != null && probed < hashSize)
        {
            idx = (idx + 1) & (hashSize - 1);
            probed++;
        }
        if (probed >= hashSize)
            idx = (int)(key >> (64 - TB_HASHBITS));
        _tbHash[idx].Key = key;
        _tbHash[idx].Ptr = ptr;
        _tbHash[idx].ErrorMask = 0;
    }

    private static Pos ToPos(Position pos, int rule50)
    {
        return new Pos
        {
            White = pos.GetOccupancy(0),
            Black = pos.GetOccupancy(1),
            Kings = pos.GetPieceBitboard(0, 5) | pos.GetPieceBitboard(1, 5),
            Queens = pos.GetPieceBitboard(0, 4) | pos.GetPieceBitboard(1, 4),
            Rooks = pos.GetPieceBitboard(0, 3) | pos.GetPieceBitboard(1, 3),
            Bishops = pos.GetPieceBitboard(0, 2) | pos.GetPieceBitboard(1, 2),
            Knights = pos.GetPieceBitboard(0, 1) | pos.GetPieceBitboard(1, 1),
            Pawns = pos.GetPieceBitboard(0, 0) | pos.GetPieceBitboard(1, 0),
            Rule50 = (byte)Math.Clamp(rule50, 0, 255),
            Ep = (byte)(pos.EnPassantSquare >= 0 ? pos.EnPassantSquare : 0),
            Turn = pos.SideToMove == 0,
        };
    }
}
