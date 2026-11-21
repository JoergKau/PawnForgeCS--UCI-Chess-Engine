using PawnForge.Core.Board;

namespace PawnForge.Core.Book;

/// <summary>
/// Reader for ChessBase .ctg opening books. Only the binary .ctg file is
/// required - the .cto/.ctb companion files are NOT used. Instead the whole
/// file is scanned once at load time and every position signature is indexed
/// in memory (hash → entry), which also makes probing independent of the
/// sometimes-inconsistent .cto lookup tables found in the wild.
///
/// CTG layout (4 KiB pages, page 0 = header, pages 1..N = position data):
/// page: [numPositions:2][bytesUsed:2] then numPositions entries of
/// [signature: sigLen = firstByte & 31, the first byte also carries the
/// castle/ep flag bits][moveListSize:1][2×numMoves bytes: move, annot]
/// [statistics: 33 bytes = total(3) losses(3) wins(3) draws(3) unk(4)
/// avgRatingGames(3) avgRatingScore(4) perfRatingGames(3)
/// perfRatingScore(4) recommendation(1) unk(2) comment(1)]
///
/// Probing follows the canonical position form (see <see cref="CtgSignature"/>).
/// A move byte is decoded with the format's opaque piece/delta tables into a
/// (from,to) square pair, which is then matched against the position's legal
/// moves. The weight of each book move is derived from the statistics of the
/// position it leads to (2·wins + draws) scaled by its annotation byte, and
/// one move is selected by weighted random choice - the standard way CTG
/// books are played by engines.
///
/// Format reference (web pages used to derive this reader):
/// • https://chessprogramming.org/CTG (format overview)
/// • http://rybkaforum.net/cgi-bin/rybkaforum/topic_show.pl?tid=2319
/// (Sesse's leaked CTG specification, 2007)
/// • https://github.com/AaronBecker/daydreamer (GPL, studied
/// for the page/entry layout and move-byte tables only - no code copied)
/// The move decode tables below are format constants shared by every CTG
/// reader; the surrounding code is PawnForge's own work.
/// </summary>
public sealed class CtgBook
{
    private const int PageSize = 4096;

    private byte[] _file = Array.Empty<byte>();
    private Dictionary<ulong, List<CtgEntry>>? _index;

    /// <summary>Number of position entries loaded (0 when no book is loaded).</summary>
    public int EntryCount { get; private set; }

    /// <summary>The path of the loaded book (empty when none is loaded).</summary>
    public string BookPath { get; private set; } = string.Empty;

    /// <summary>True once a book has been loaded successfully.</summary>
    public bool IsLoaded => _index is { Count: > 0 };

    // ── CTG move-byte decoding tables (reverse-engineered format constants) ──
    private const string PieceCode =
        "PNxQPQPxQBKxPBRNxxBKPBxxPxQBxBxxxRBQPxBPQQNxxPBQNQBxNxNQQQBQBxxx" +
        "xQQxKQxxxxPQNQxxRxRxBPxxxxxxPxxPxQPQxxBKxRBxxxRQxxBxQxxxxBRRPRQR" +
        "QRPxxNRRxxNPKxQQxxQxQxPKRRQPxQxBQxQPxRxxxRxQxRQxQPBxxRxQxBxPQQKx" +
        "xBBBRRQPPQBPBRxPxPNNxxxQRQNPxxPKNRxRxQPQRNxPPQQRQQxNRBxNQQQQxQQx";

    private static readonly byte[] PieceIndex =
    {
        5,2,9,2,2,1,4,9,2,2,1,9,1,1,2,1,9,9,1,1,8,1,9,9,7,9,2,1,9,2,9,9,
        9,2,2,2,8,9,1,3,1,1,2,9,9,6,1,1,2,1,2,9,1,9,1,1,2,1,1,2,1,9,9,9,
        9,2,1,9,1,1,9,9,9,9,8,1,2,2,9,9,1,9,1,9,2,3,9,9,9,9,9,9,7,9,9,5,
        9,1,2,2,9,9,1,1,9,2,1,0,9,9,1,2,9,9,2,9,1,9,9,9,9,2,1,2,3,2,1,1,
        1,1,6,9,9,1,1,1,9,9,1,1,1,9,2,1,9,9,2,9,1,9,2,1,1,1,1,3,9,1,9,2,
        2,9,1,8,9,2,9,9,9,2,9,2,9,2,2,9,2,6,1,9,9,2,9,1,9,2,9,5,2,2,1,9,
        9,1,2,1,2,2,2,7,7,2,2,6,2,1,9,4,9,2,2,2,9,9,9,1,2,1,1,1,9,9,5,1,
        2,1,9,2,9,1,4,1,1,1,9,4,1,1,2,1,2,1,9,2,2,2,0,1,2,2,2,2,9,1,2,9
    };

    private static readonly sbyte[] Forward =
    {
        1,-1,9,0,1,1,1,9,0,6,-1,9,1,3,0,-1,9,9,7,1,1,5,9,9,1,9,6,1,9,7,9,9,
        9,0,2,6,1,9,7,1,5,0,-2,9,9,1,1,0,-2,0,5,9,2,9,1,4,4,0,6,5,5,9,9,9,
        9,5,7,9,-1,3,9,9,9,9,2,5,2,1,9,9,6,9,0,9,1,1,9,9,9,9,9,9,1,9,9,2,
        9,6,2,7,9,9,3,1,9,7,4,0,9,9,0,7,9,9,7,9,0,9,9,9,9,6,3,6,1,1,3,0,
        6,1,1,9,9,2,0,5,9,9,-2,1,-1,9,2,0,9,9,1,9,3,9,1,0,0,4,6,2,9,2,9,4,
        3,9,2,1,9,5,9,9,9,0,9,6,9,0,3,9,4,2,6,9,9,0,9,5,9,3,9,1,0,2,0,9,
        9,2,2,2,0,4,5,1,2,7,3,1,5,0,9,1,9,1,1,1,9,9,9,1,0,2,-2,2,9,9,1,1,
        -1,7,9,3,9,0,2,4,2,-1,9,1,1,7,1,0,0,1,9,2,2,1,0,1,0,6,0,2,9,7,3,9
    };

    private static readonly sbyte[] Left =
    {
        -1,2,9,-2,0,0,1,9,-4,-6,0,9,1,-3,-3,2,9,9,-7,0,-1,-5,9,9,0,9,0,1,9,-7,9,9,
        9,-7,2,-6,1,9,7,1,-5,-6,-1,9,9,-1,-1,-1,1,-3,-5,9,-1,9,-2,0,4,-5,-6,5,5,9,9,9,
        9,-5,7,9,-1,-3,9,9,9,9,0,5,-1,0,9,9,0,9,-6,9,1,0,9,9,9,9,9,9,-1,9,9,0,
        9,-6,0,7,9,9,3,-1,9,0,-4,0,9,9,-5,-7,9,9,7,9,-2,9,9,9,9,6,0,0,-1,0,3,-1,
        6,0,1,9,9,1,-7,0,9,9,-1,-1,1,9,2,-7,9,9,-1,9,0,9,-1,1,-3,0,0,0,9,0,9,4,
        0,9,-2,0,9,0,9,9,9,-2,9,6,9,-4,-3,9,0,0,6,9,9,-5,9,0,9,-3,9,0,-5,0,-1,9,
        9,-2,-2,2,-1,0,0,1,0,0,3,0,5,-2,9,0,9,1,-2,2,9,9,9,1,-6,2,1,0,9,9,1,1,
        -2,0,9,0,9,-4,0,-4,0,-2,9,-1,0,-7,1,-4,-7,-1,9,1,0,-1,0,2,-1,0,-3,-2,9,0,3,9
    };

    private sealed class CtgEntry
    {
        public byte[] Signature = Array.Empty<byte>();
        public byte[] Moves = Array.Empty<byte>(); // (move, annotation) pairs
        public int NumMoves;
        public int Total, Wins, Draws, Losses;
        public byte Recommendation;
    }

    /// <summary>Loads the given .ctg file and indexes all positions. Returns false on any error.</summary>
    public bool Load(string path)
    {
        try
        {
            byte[] file = File.ReadAllBytes(path);
            var index = new Dictionary<ulong, List<CtgEntry>>();
            int pages = file.Length / PageSize;
            int count = 0;

            for (int page = 1; page < pages; page++)
            {
                int pageStart = page * PageSize;
                int pageEnd = Math.Min(pageStart + PageSize, file.Length);
                int numPositions = (file[pageStart] << 8) | file[pageStart + 1];
                // A position page cannot hold more than ~117 entries (smallest
                // entry is 35 bytes); larger counts mean this is not a position
                // page (string/name pages, free pages, ...).
                if (numPositions <= 0 || numPositions > 256)
                    continue;

                int pos = pageStart + 4;
                for (int i = 0; i < numPositions; i++)
                {
                    if (pos + 1 >= pageEnd) break;
                    int entrySize = file[pos] & 31;
                    if (entrySize < 1 || pos + entrySize + 34 + 34 > pageEnd) break;
                    var sig = new byte[entrySize];
                    Array.Copy(file, pos, sig, 0, entrySize);
                    pos += entrySize;

                    if (pos >= pageEnd) break;
                    int moveListSize = file[pos];
                    if (moveListSize < 1 || pos + moveListSize + 33 > pageEnd) break;

                    var entry = new CtgEntry { Signature = sig };
                    entry.NumMoves = (moveListSize - 1) / 2;
                    if (entry.NumMoves > 0)
                    {
                        entry.Moves = new byte[moveListSize - 1];
                        Array.Copy(file, pos + 1, entry.Moves, 0, moveListSize - 1);
                    }
                    pos += moveListSize;

                    entry.Total = Read24(file, pos); pos += 3;
                    entry.Losses = Read24(file, pos); pos += 3;
                    entry.Wins = Read24(file, pos); pos += 3;
                    entry.Draws = Read24(file, pos); pos += 3;
                    pos += 4;                       // unknown1
                    pos += 3;                       // avg rating games
                    pos += 4;                       // avg rating score
                    pos += 3;                       // perf rating games
                    pos += 4;                       // perf rating score
                    entry.Recommendation = file[pos]; pos += 1;
                    pos += 2;                       // unknown2 + comment

                    ulong h = Fnv1a64(sig);
                    if (!index.TryGetValue(h, out var list))
                    {
                        list = new List<CtgEntry>(1);
                        index[h] = list;
                    }
                    list.Add(entry);
                    count++;
                }
            }

            _file = file;
            _index = index;
            EntryCount = count;
            BookPath = path;
            return count > 0;
        }
        catch
        {
            _file = Array.Empty<byte>();
            _index = null;
            EntryCount = 0;
            BookPath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Looks for the first usable .ctg book in the usual locations: the
    /// directory of the running executable and a "books" subdirectory beneath
    /// it (also tried relative to the current working directory).
    /// </summary>
    public static string? FindDefaultBookPath()
    {
        string[] bases = { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (string root in bases)
        {
            string[] dirs = { root, Path.Combine(root, "books") };
            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir))
                        continue;
                    foreach (string file in Directory.EnumerateFiles(dir, "*.ctg"))
                        return file;
                }
                catch
                {
                    // skip unreadable directories
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Picks a book move for <paramref name="pos"/> using weighted random
    /// selection over the legal moves of the position, or <see cref="Move.None"/>
    /// when the position is not in the book (or none of its moves are legal).
    /// </summary>
    public Move PickBookMove(Position pos, Random rng)
    {
        if (_index == null)
            return Move.None;

        Span<byte> sigBuf = stackalloc byte[32];
        int sigLen = CtgSignature.Compute(pos, sigBuf);
        var entry = FindEntry(sigBuf[..sigLen]);
        if (entry is null || entry.NumMoves == 0)
            return Move.None;

        bool flip = pos.SideToMove == 1;
        bool mirror = CtgSignature.IsMirrored(pos);

        Span<Move> legal = stackalloc Move[256];
        int legalCount = pos.GenerateMoves(legal);

        var candidates = new List<(Move Move, long Weight, bool Recommended)>(entry.NumMoves);
        bool haveRecommended = false;

        for (int i = 0; i < entry.NumMoves; i++)
        {
            byte moveByte = entry.Moves[2 * i];
            byte annotation = entry.Moves[2 * i + 1];

            if (!TryDecodeMove(moveByte, pos, flip, mirror, out int from, out int to))
                continue;

            Move matched = Move.None;
            for (int j = 0; j < legalCount; j++)
            {
                if (legal[j].From == from && legal[j].To == to)
                {
                    matched = legal[j];
                    break;
                }
            }
            if (matched.IsNone)
                continue;

            bool recommended = ChildWeight(pos, matched, out long weight);
            weight = ApplyAnnotation(weight, annotation, ref recommended);
            if (recommended)
                haveRecommended = true;

            if (weight > 0)
                candidates.Add((matched, weight, recommended));
        }

        // If any candidate is marked "recommended", restrict the choice to them.
        if (haveRecommended)
            candidates.RemoveAll(c => !c.Recommended);

        if (candidates.Count == 0)
            return Move.None;

        long total = 0;
        foreach (var c in candidates)
            total = ClampAdd(total, c.Weight);
        if (total <= 0)
            return Move.None;

        long choice = (long)(rng.NextDouble() * total);
        foreach (var c in candidates)
        {
            choice -= c.Weight;
            if (choice < 0)
                return c.Move;
        }
        return candidates[^1].Move;
    }

    private static long ClampAdd(long a, long b)
    {
        long sum = a + b;
        return sum < 0 ? long.MaxValue : sum;
    }

    /// <summary>Applies a CTG move annotation byte (NAG-like) to a base weight.</summary>
    private static long ApplyAnnotation(long weight, byte annotation, ref bool recommended)
    {
        switch (annotation)
        {
            case 0x01: return weight * 8;             //  !
            case 0x02: recommended = false; return 0; //  ?
            case 0x03: return weight * 32;            // !!
            case 0x04: recommended = false; return 0; // ??
            case 0x05: recommended = false; return weight / 2; // !?
            case 0x06: recommended = false; return weight / 8; // ?!
            case 0x08: return long.MaxValue;          // only move
            default: return weight;
        }
    }

    /// <summary>
    /// Weight of <paramref name="move"/>: makes it, looks up the resulting
    /// position's statistics and returns (2·wins + draws) scaled to 0..10000.
    /// Also reports whether the resulting position is marked "recommended".
    /// </summary>
    private bool ChildWeight(Position pos, Move move, out long weight)
    {
        weight = 0;
        var captured = pos.MakePublicMove(move);
        try
        {
            Span<byte> sigBuf = stackalloc byte[32];
            int sigLen = CtgSignature.Compute(pos, sigBuf);
            var child = FindEntry(sigBuf[..sigLen]);
            if (child is null)
                return false;

            long games = (long)child.Wins + child.Draws + child.Losses;
            if (games < 1)
                return false;

            weight = (2L * child.Wins + child.Draws) * 10000 / games;
            if (child.Recommendation == 64)
            {
                weight = 0;
                return false;
            }
            if (child.Recommendation == 128)
                return true;
            return false;
        }
        finally
        {
            pos.UnmakePublicMove(move, captured);
        }
    }

    /// <summary>
    /// Decodes one CTG move byte into a (from, to) square pair of the REAL
    /// board (the byte references the piece type, its index counted from a1
    /// in canonical form, and dx/dy deltas). Castling bytes are special-cased.
    /// </summary>
    private static bool TryDecodeMove(byte b, Position pos, bool flip, bool mirror, out int from, out int to)
    {
        from = -1;
        to = -1;

        if (b == 107) // O-O
        {
            int rank = flip ? 7 : 0;
            from = rank * 8 + 4;
            to = rank * 8 + 6;
            return true;
        }
        if (b == 246) // O-O-O
        {
            int rank = flip ? 7 : 0;
            from = rank * 8 + 4;
            to = rank * 8 + 2;
            return true;
        }

        char glyph = b < PieceCode.Length ? PieceCode[b] : 'x';
        if (glyph is not ('P' or 'N' or 'B' or 'R' or 'Q' or 'K'))
            return false;

        int nth = PieceIndex[b];
        var board = pos.Mailbox;
        int fromFile = -1, fromRank = -1;
        int count = 0;
        for (int f = 0; f < 8 && fromFile < 0; f++)
        {
            for (int r = 0; r < 8; r++)
            {
                int cf = mirror ? 7 - f : f;
                int cr = flip ? 7 - r : r;
                Piece piece = board[cr * 8 + cf];
                if (flip && piece != Piece.Empty)
                    piece = (Piece)((int)piece < 7 ? (int)piece + 6 : (int)piece - 6);
                if (piece.ToChar() == glyph)
                {
                    count++;
                    if (count == nth)
                    {
                        fromFile = f;
                        fromRank = r;
                        break;
                    }
                }
            }
        }
        if (fromFile < 0)
            return false;

        int toFile = (fromFile - Left[b] + 8) % 8;
        int toRank = (fromRank + Forward[b] + 8) % 8;

        // The decoded squares are in the canonical (flip/mirrored) frame -
        // convert back to the real board.
        from = (flip ? 7 - fromRank : fromRank) * 8 + (mirror ? 7 - fromFile : fromFile);
        to = (flip ? 7 - toRank : toRank) * 8 + (mirror ? 7 - toFile : toFile);
        return true;
    }

    private CtgEntry? FindEntry(ReadOnlySpan<byte> signature)
    {
        if (_index == null)
            return null;
        if (!_index.TryGetValue(Fnv1a64(signature), out var list))
            return null;
        foreach (var entry in list)
        {
            if (entry.Signature.AsSpan().SequenceEqual(signature))
                return entry;
        }
        return null;
    }

    private static int Read24(byte[] file, int pos)
        => (file[pos] << 16) | (file[pos + 1] << 8) | file[pos + 2];

    private static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash;
    }
}
