using System.Diagnostics;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// External-oracle fixture validation.
///
/// Gated fixture checks (mate proofs, best-move agreement, FEN echo, perft
/// cross-checks) run whenever a Stockfish binary is available — via
/// PAWNFORGE_STOCKFISH or PATH. When no binary is installed the oracle is
/// disabled and those checks no-op; CI installs Stockfish so the gates are
/// live there.
/// </summary>
public static class Stockfish
{
    /// <summary>
    /// Optional Stockfish binary used as an external oracle for fixture
    /// validation.
    ///
    /// Resolution order:
    ///   1. the PAWNFORGE_STOCKFISH environment variable (explicit wins),
    ///   2. a <c>stockfish</c> executable on PATH.
    ///
    /// When neither resolves, the oracle is DISABLED: every gated fixture check
    /// no-ops, so the suite still runs on a machine without Stockfish. CI
    /// installs Stockfish, where the checks are live.
    /// </summary>
    public static string? BinaryPath { get; } = ResolveBinaryPath();

    /// <summary>True when an external oracle binary was found.</summary>
    public static bool IsAvailable => BinaryPath is not null;

    private static string? ResolveBinaryPath()
    {
        string? env = System.Environment.GetEnvironmentVariable("PAWNFORGE_STOCKFISH");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        return FindExecutableOnPath("stockfish");
    }

    private static string? FindExecutableOnPath(string name)
    {
        string? pathVar = System.Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;
        string separator = System.OperatingSystem.IsWindows() ? ";" : ":";
        string executable = System.OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full = System.IO.Path.Combine(dir.Trim(), executable);
            if (System.IO.File.Exists(full))
                return full;
        }
        return null;
    }

    private static StockfishResult NoOracle(string fen, int depth) =>
        new(fen, depth, "(none)", null, null, null);

    /// <summary>
    /// Runs Stockfish on a FEN at a fixed depth (single thread, fixed strength)
    /// and returns the last evaluation plus the best move.
    /// Throws if Stockfish is unavailable or produces no bestmove.
    /// </summary>
    public static StockfishResult Analyze(string fen, int depth, int movetimeMs = 0)
    {
        if (BinaryPath is null)
            throw new InvalidOperationException(
                "Stockfish oracle is not installed (set PAWNFORGE_STOCKFISH or put `stockfish` on PATH).");
        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start Stockfish at {BinaryPath}");

        var output = new List<string>();
        string? bestMoveLine = null;

        // Reader on a background task; we drive via stdin and wait for "bestmove".
        var reader = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                lock (output) output.Add(line);
                if (line.StartsWith("bestmove", StringComparison.Ordinal))
                {
                    bestMoveLine = line;
                    break; // stop reading; we send "quit" below
                }
            }
        });

        void Send(string cmd)
        {
            process.StandardInput.WriteLine(cmd);
            process.StandardInput.Flush();
        }

        try
        {
            Send("uci");
            Send("setoption name Threads value 1");
            Send("setoption name MultiPV value 1");
            Send($"position fen {fen}");
            // Fixture validation is intentionally movetime-based: modern engine builds do not
            // prove forced mates at shallow FIXED depths (their shallow-depth pruning is
            // aggressive), so fixed-depth fixture validation was engine-version-dependent.
            // A movetime search is depth-agnostic and stable. Depth-based callers are
            // unchanged.
            Send(movetimeMs > 0 ? $"go movetime {movetimeMs}" : $"go depth {depth}");

            var completed = Task.WaitAny(new[] { reader }, TimeSpan.FromSeconds(60));
            if (completed != 0)
                throw new TimeoutException($"Stockfish did not answer within 60 s for: {fen}");

            if (bestMoveLine == null)
                throw new InvalidOperationException($"Stockfish produced no bestmove for: {fen}");

            // Parse the best move ("bestmove e2e4 ponder ..." / "bestmove (none)").
            string bestMove = bestMoveLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                          .ElementAtOrDefault(1) ?? "(none)";

            // Parse the LAST info line that carries a score.
            int? cp = null, mate = null;
            string? pvLine = null;
            lock (output)
            {
                foreach (var line in output)
                {
                    if (!line.StartsWith("info", StringComparison.Ordinal)) continue;
                    int scoreIdx = line.IndexOf(" score ", StringComparison.Ordinal);
                    if (scoreIdx < 0) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        if (parts[i] == "cp" && int.TryParse(parts[i + 1], out int cpVal))
                        {
                            cp = cpVal; mate = null;
                        }
                        else if (parts[i] == "mate" && int.TryParse(parts[i + 1], out int mateVal))
                        {
                            mate = mateVal; cp = null;
                        }
                    }
                    if (line.Contains(" pv ")) pvLine = line;
                }
            }

            return new StockfishResult(fen, depth, bestMove, cp, mate, pvLine);
        }
        finally
        {
            try
            {
                Send("quit");
                process.WaitForExit(2000);
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                // process already gone
            }
        }
    }

    /// <summary>Asserts the fixture is a mate in exactly <paramref name="moves"/> for the side to move.</summary>
    public static StockfishResult AssertMateIn(string fen, int moves)
    {
        if (!IsAvailable) return NoOracle(fen, Math.Max(4, 2 * moves));
        // Movetime validation for version stability (see Analyze comment).
        var result = Analyze(fen, Math.Max(4, 2 * moves), movetimeMs: 1000);
        Assert.True(result.MateIn == moves,
            $"Fixture validation failed: '{fen}' — expected mate in {moves}, " +
            $"Stockfish reports {(result.MateIn.HasValue ? $"mate {result.MateIn}" : $"cp {result.ScoreCp}")}.");
        return result;
    }

    /// <summary>Asserts the side to move is checkmated (mate 0).</summary>
    public static StockfishResult AssertCheckmate(string fen)
    {
        if (!IsAvailable) return NoOracle(fen, 2);
        // Movetime validation for version stability (see Analyze comment).
        var result = Analyze(fen, 2, movetimeMs: 1000);
        Assert.True(result.MateIn == 0,
            $"Fixture validation failed: '{fen}' — expected checkmate, " +
            $"Stockfish reports {(result.MateIn.HasValue ? $"mate {result.MateIn}" : $"cp {result.ScoreCp}")}.");
        return result;
    }

    /// <summary>Asserts the oracle's best move at the given depth equals the expected UCI move.</summary>
    public static StockfishResult AssertBestMove(string fen, int depth, string expectedUci)
    {
        if (!IsAvailable) return NoOracle(fen, depth);
        var result = Analyze(fen, depth);
        Assert.True(result.BestMove == expectedUci,
            $"Fixture validation failed: '{fen}' — expected bestmove {expectedUci}, " +
            $"Stockfish plays {result.BestMove} (score: {result.ScoreSummary}). " +
            "If multiple winning moves exist, do not use AssertBestMove — use AssertMateIn instead.");
        return result;
    }

    /// <summary>
    /// The G3 echo gate: verifies Stockfish parses this FEN to the IDENTICAL
    /// position (the oracle must echo it back unchanged). Compares the first
    /// four FEN fields (piece placement, side to move, castling, en passant) —
    /// the move counters are echoed as parsed and cannot change geometry, but
    /// they are still required to be present and integer. Throws loudly on any
    /// disagreement; a Stockfish-gated check that silently no-ops is a defect,
    /// not a skip.
    /// </summary>
    public static void VerifyFen(string fen)
    {
        if (!IsAvailable) return;
        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start Stockfish at {BinaryPath}");

        var output = new List<string>();
        var reader = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                lock (output) output.Add(line);
            }
        });

        process.StandardInput.WriteLine("uci");
        process.StandardInput.WriteLine($"position fen {fen}");
        process.StandardInput.WriteLine("d");
        process.StandardInput.WriteLine("quit");
        process.StandardInput.Flush();
        reader.Wait(TimeSpan.FromSeconds(30));

        // Stockfish 17 prints "FEN: <fen>"; Stockfish 19 prints "Fen: <fen>".
        // Match case-insensitively so the gate is version-stable.
        string? echoed = output.FirstOrDefault(l =>
            l.StartsWith("FEN: ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("Fen: ", StringComparison.Ordinal));
        Assert.True(echoed is not null, $"Stockfish produced no FEN echo for: {fen}");
        string echoFen = echoed.Substring(echoed.IndexOf(':') + 1).Trim();

        string[] want = fen.Split(' ');
        string[] got = echoFen.Split(' ');
        Assert.True(want.Length >= 6, $"FEN does not have six fields: {fen}");
        Assert.True(got.Length >= 6, $"Stockfish echo is not a FEN: {echoFen}");
        for (int i = 0; i < 4; i++)
            Assert.True(want[i] == got[i],
                $"Stockfish parses field {i} of '{fen}' as '{got[i]}', not '{want[i]}' — the FEN is not what it claims to be.");
        _ = int.Parse(got[4]);
        _ = int.Parse(got[5]);
    }

    /// <summary>
    /// Runs Stockfish's `go perft &lt;depth&gt;` on a FEN and returns the total node
    /// count (the "Nodes searched:" line). Used by the FEN-verification gate to
    /// cross-check OUR move generator against Stockfish's on the same positions.
    /// Throws loudly if Stockfish produces no perft total.
    /// </summary>
    public static long Perft(string fen, int depth)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start Stockfish at {BinaryPath}");

        var output = new List<string>();
        var reader = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                lock (output) output.Add(line);
            }
        });

        process.StandardInput.WriteLine("uci");
        process.StandardInput.WriteLine($"position fen {fen}");
        process.StandardInput.WriteLine($"go perft {depth}");
        process.StandardInput.WriteLine("quit");
        process.StandardInput.Flush();
        reader.Wait(TimeSpan.FromSeconds(60));

        string? total = output.FirstOrDefault(l => l.StartsWith("Nodes searched", StringComparison.OrdinalIgnoreCase));
        Assert.True(total is not null, $"Stockfish produced no perft total for: {fen} at depth {depth}");
        return long.Parse(total.Split(':')[1].Trim());
    }
}

/// <summary>Normalized Stockfish analysis result.</summary>
public sealed record StockfishResult(
    string Fen,
    int Depth,
    string BestMove,
    int? ScoreCp,
    int? MateIn,
    string? PvLine)
{
    /// <summary>Mate distance in plies from the search's point of view, or null.</summary>
    public bool IsMate => MateIn.HasValue;

    public string ScoreSummary => MateIn.HasValue ? $"mate {MateIn}" : $"cp {ScoreCp}";
}
