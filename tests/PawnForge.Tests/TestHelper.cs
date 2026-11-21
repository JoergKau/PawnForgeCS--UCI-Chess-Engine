using System.Diagnostics;
using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;

namespace PawnForge.Tests;

/// <summary>
/// Shared helpers for the test suites: move lookup by coordinate, game
/// playback, and a perft implementation built on the PUBLIC make/unmake API
/// (so perft also validates make/unmake state restoration).
/// </summary>
internal static class TestHelper
{
    /// <summary>Converts a square name ("e2") to its index (A1=0.. H8=63).</summary>
    public static int Sq(string square)
    {
        int file = square[0] - 'a';
        int rank = square[1] - '1';
        return rank * 8 + file;
    }

    /// <summary>Finds the (pseudo-legal or legal) move matching from/to, optionally with promotion piece ('q','r','b','n').</summary>
    public static Move FindMove(Position pos, string from, string to, char? promotion = null, bool pseudoLegal = false)
    {
        Span<Move> buffer = stackalloc Move[256];
        int count = pseudoLegal
            ? pos.GeneratePseudoLegalMoves(buffer)
            : pos.GenerateMoves(buffer);

        int f = Sq(from), t = Sq(to);
        for (int i = 0; i < count; i++)
        {
            Move m = buffer[i];
            if (m.From != f || m.To != t) continue;
            if (promotion.HasValue)
            {
                // Promoted stores the colored Piece enum; compare by piece TYPE.
                // ToIndex is 0-based: Pawn=0, Knight=1, Bishop=2, Rook=3, Queen=4, King=5.
                int wantType = promotion.Value switch { 'q' => 4, 'r' => 3, 'b' => 2, 'n' => 1, _ => 0 };
                if (m.Promoted == 0) continue;
                int promoType = ((Piece)m.Promoted).ToIndex();
                if (promoType != wantType) continue;
            }
            return m;
        }
        throw new InvalidOperationException($"Move {from}{to}{(promotion.HasValue ? promotion.Value.ToString() : "")} not found (legal={(pseudoLegal ? "pseudo" : "legal")})");
    }

    /// <summary>
    /// Plays a sequence of UCI moves ("e2e4", "e7e8q",...) on the position,
    /// keeping the evaluator in sync exactly like the
    /// engine does (board move first, then evaluator.MakeMove).
    /// Returns the list of (move, movingPiece, captured) for unmaking.
    ///
    /// Takes the <see cref="IPositionEvaluator"/> seam rather than a concrete arm:
    /// NNUE is the only evaluator now, but the helpers stay arm-agnostic so a test
    /// can hand them whatever evaluator it is exercising.
    /// </summary>
    public static List<(Move Move, Piece Moving, Piece Captured)> Play(
        Position pos, IPositionEvaluator evaluator, params string[] uciMoves)
    {
        var played = new List<(Move, Piece, Piece)>();
        foreach (string uci in uciMoves)
        {
            Move m = FindMove(pos, uci[..2], uci[2..4], uci.Length > 4 ? uci[4] : null);
            Piece moving = pos.GetPieceAt(m.From);
            Piece captured = pos.MakePublicMove(m);
            evaluator.MakeMove(pos, m, moving, captured);
            played.Add((m, moving, captured));
        }
        return played;
    }

    /// <summary>Unplays a sequence in reverse order (evaluator first, then board).</summary>
    public static void Unplay(Position pos, IPositionEvaluator evaluator, List<(Move Move, Piece Moving, Piece Captured)> played)
    {
        for (int i = played.Count - 1; i >= 0; i--)
        {
            evaluator.UnmakeMove();
            pos.UnmakePublicMove(played[i].Move, played[i].Captured);
        }
    }

    /// <summary>
    /// Perft via the public GenerateMoves + Make/Unmake API. Validates that
    /// make/unmake restores the position exactly (a corrupted unmake breaks
    /// perft counts immediately).
    /// </summary>
    public static long Perft(Position pos, int depth)
    {
        if (depth == 0) return 1;

        Span<Move> buffer = stackalloc Move[256];
        int count = pos.GenerateMoves(buffer);
        if (depth == 1) return count;

        long nodes = 0;
        for (int i = 0; i < count; i++)
        {
            Move m = buffer[i];
            Piece captured = pos.MakePublicMove(m);
            nodes += Perft(pos, depth - 1);
            pos.UnmakePublicMove(m, captured);
        }
        return nodes;
    }

    /// <summary>Plays a deterministic pseudo-random game from the given position.</summary>
    public static void PlayRandomGame(Position pos, IPositionEvaluator evaluator, int plies, int seed)
    {
        var rng = new Random(seed);
        Span<Move> buffer = stackalloc Move[256]; // once per call, NOT per ply (CA2014)
        for (int i = 0; i < plies; i++)
        {
            int count = pos.GenerateMoves(buffer);
            if (count == 0) break;

            Move m = buffer[rng.Next(count)];
            Piece moving = pos.GetPieceAt(m.From);
            Piece captured = pos.MakePublicMove(m);
            evaluator.MakeMove(pos, m, moving, captured);
        }
    }

    /// <summary>Collects magic-bitboard failure details for the test output.</summary>
    public static readonly List<string> MagicFailures = new();

    public static void RecordMagicFailure(string message)
    {
        lock (MagicFailures) MagicFailures.Add(message);
    }

    // ── the evaluator every test now uses ───────────────────────────────────
    // The HCE modules were deleted when NNUE became the engine's only arm, so
    // `new HceEvaluator(pos)` no longer exists. Search/quiescence tests still
    // need SOME evaluator to hand to SearchEngine, so they call
    // NewEvaluator(pos) instead and stay arm-agnostic via the seam interface.
    //
    // The net is read once and shared: NnueNetV5 is immutable after load, and
    // only the per-instance accumulator is mutable, so one parse serves the
    // whole run instead of re-reading 12.7 MB per test.

    private static readonly Lazy<NnueNetV5> SharedNetInstance = new(LoadEmbeddedNet);

    /// <summary>The process-wide embedded net. Parsed once, on first use.</summary>
    public static NnueNetV5 SharedNet() => SharedNetInstance.Value;

    private static NnueNetV5 LoadEmbeddedNet()
    {
        var assembly = typeof(NnueNetV5).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(NnueNetV5.EmbeddedNetResourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "the embedded net resource V5_POOLV4-150.nnue is missing from PawnForge.Core");
        using var stream = assembly.GetManifestResourceStream(name)!;
        return NnueNetV5.Read(stream);
    }

    /// <summary>A fresh NNUE evaluator positioned on <paramref name="pos"/>.
    /// One per caller: the accumulator is per-instance state.</summary>
    public static IPositionEvaluator NewEvaluator(Position pos)
    {
        var evaluator = new NnueEvaluator(SharedNet());
        evaluator.SetPosition(pos);
        return evaluator;
    }

    /// <summary>
    /// Locates the checked-in test opening book (books/Perfect2023.ctg) by
    /// walking up from the test output directory to the repo root.
    /// </summary>
    public static string? FindBookPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string[] names =
            {
                Path.Combine(dir.FullName, "books", "Perfect2023.ctg"),
                Path.Combine(dir.FullName, "Perfect2023.ctg"),
            };
            foreach (string candidate in names)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Locates the checked-in 3-man Syzygy test tables (tbdata/) by walking up
    /// from the test output directory to the repo root.
    /// </summary>
    public static string? FindTbDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "tbdata");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.rtbw").Any())
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
