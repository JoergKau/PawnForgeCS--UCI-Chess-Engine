using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PawnForge.Core.Board;

namespace PawnForge.UCI
{
    /// <summary>
    /// Lightweight UCI protocol handler.
    /// Delegates engine logic to EngineOrchestrator and parsing to UciParser.
    /// </summary> /// <summary>
    /// The UCI protocol loop: reads stdin line by line, dispatches commands
    /// (uci/isready/ucinewgame/position/go/stop/ponderhit/quit/setoption/debug
    /// plus the non-standard d/eval/perft/bench) and writes responses to
    /// stdout. Thin layer: state lives in the EngineOrchestrator.
    /// </summary>
    public class UciHandler
    {
        /// <summary>The engine's identity, as reported on the UCI `id name` line
        /// and in the startup banner. "PawnForgeCS 2.0" = the NNUE engine
        /// (our own v5 NNUE primary, migration M7); "v1.1" was the
        /// pawnforge-v1.1-lineage name this repo grew out of. One source of
        /// truth — banner and handshake both read it.</summary>
        public const string EngineName = "PawnForgeCS";
        public const string EngineVersion = "2.0";

        /// <summary>
        /// Startup banner for terminal use, printed once before the UCI loop to
        /// STDERR and only when stdin is an interactive console (quirk Q6 fix:
        /// it is never written to stdout). The repo URL is the public home for
        /// the engine (author JoergKau).
        /// </summary>
        private const string StartupBanner =
            "*****************************************************************\n" +
            "*   PawnForgeCS 2.0 by JoergKau                                 *\n" +
            "*   https://github.com/JoergKau/Modern-C-Sharp-UCI-ChessEngine  *\n" +
            "*****************************************************************";

        private readonly EngineOrchestrator _orchestrator = new();

        /// <summary>[EXP-PARAM] Command-line parameter overrides
        /// (<c>param:Name=Value</c> argv, parsed by Program.cs). Applied to the
        /// orchestrator BEFORE the first UCI command, so every arm of a match
        /// can be the same published binary with a different argv.</summary>
        public string[] PresetParams { get; set; } = Array.Empty<string>();

        public UciHandler()
        {
        }

        public void Run(TextReader input, TextWriter output)
        {
            if (PresetParams.Length > 0)
                _orchestrator.PresetSearchOverrides(PresetParams);

            // Quirk Q6 fix: the banner is for people at a terminal. It used to go
            // to stdout unconditionally, which is stray output ahead of the UCI
            // handshake (CCRL asks for none). It now goes to STDERR, and only
            // when stdin is an interactive console, so a GUI or a match runner
            // never sees it on either stream.
            if (!Console.IsInputRedirected)
                Console.Error.WriteLine(StartupBanner);
            
            bool quit = false;
            string? line;
            while (!quit && (line = input.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;
                
                try
                {
                    quit = ProcessCommand(trimmed, output);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"info string Error: {ex.Message}");
                }
            }
            
            _orchestrator.Stop();
        }
        
        private bool ProcessCommand(string command, TextWriter output)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            
            var cmd = parts[0].ToLowerInvariant();
            
            switch (cmd)
            {
                case "uci":
                    HandleUci(output);
                    break;
                    
                case "isready":
                    // First `isready` triggers the one-time JIT warm-up so the
                    // first search does not run on unoptimized tier-0 code
                    // (see EngineOrchestrator.WarmUp). The eval-source line is
                    // re-stated before readyok (a GUI reads neither stderr nor
                    // the first info string). Crucially, `readyok` is emitted
                    // IMMEDIATELY even while a search runs: blocking the command
                    // loop here makes a later `stop`/`quit` unreachable
                    // (a hang).
                    _orchestrator.WarmUp(output);
                    _orchestrator.AnnounceEvalSource(output);
                    _orchestrator.ConfirmReady(output);
                    break;
                    
                case "ucinewgame":
                    _orchestrator.UciNewGame();
                    break;
                    
                case "position":
                    HandlePosition(parts, output);
                    break;
                    
                case "go":
                    HandleGo(command, output);
                    break;
                    
                case "stop":
                    _orchestrator.Stop();
                    break;

                case "ponderhit":
                    // The opponent has played the move the engine was
                    // pondering (`go ponder`): continue as a normal search.
                    _orchestrator.PonderHit(output);
                    break;
                    
                case "quit":
                    return true;
                    
                case "debug":
                bool debugOn = parts.Length > 1 && parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                _orchestrator.SetDebugMode(debugOn);
                break;

            case "setoption":
                    HandleSetOption(command, output);
                    break;
                    
                case "d":
                    _orchestrator.Display(output);
                    break;
                    
                case "eval":
                    _orchestrator.Eval(output);
                    break;

                case "nnueeval":
                    // Hidden parity-gate hook (and
                    //): the NNUE eval of the current position in
                    // centipawns, side-to-move relative — or "nnueeval none" when
                    // no net is loaded. Ported from the the previous generation engine; the
                    // training toolchain depends on the exact output form.
                    _orchestrator.NnueEval(output);
                    break;

                case "nnueregions":
                    // Hidden diagnostic hook (region-analysis pass):
                    // the real-game king-region histogram behind the
                    // accumulator-refresh counter. `nnueregions reset` clears
                    // the counters and STARTS counting; bare `nnueregions`
                    // prints the histogram; `nnueregions off` stops counting.
                    // Measurement flow: reset -> bench -> read.
                    _orchestrator.NnueRegions(parts.Length > 1 ? parts[1] : null, output);
                    break;

                case "searchstats":
                    // P0 search instrumentation :
                    // reset -> search/bench -> read. Prints qsearch share, TT
                    // hit rate and the cutoff-move-index histogram.
                    _orchestrator.SearchStats(parts.Length > 1 ? parts[1] : null, output);
                    break;
                    
                case "perft":
                    HandlePerft(parts, output);
                    break;
                    
                case "bench":
                    HandleBench(parts, output);
                    break;

                case "checkmoves":
                    // Debug: print each legal move + GivesCheck verdict (validated externally
                    // against python-chess by).
                    _orchestrator.CheckMoves(output);
                    break;
                    
                default:
                    output.WriteLine($"info string Unknown command: {cmd}");
                    break;
            }
            
            return false;
        }
        
        private void HandleUci(TextWriter output)
        {
            // Engine identity (UCI): the `id name` string is what GUIs and match
            // harnesses display and index. Version 2.0 = the NNUE engine
            // (our own v5 NNUE, migration M7); "v1.1" was the
            // pawnforge-v1.1-lineage name. Kept as
            // named constants so the banner and the handshake cannot drift.
            output.WriteLine($"id name {EngineName} {EngineVersion}");
            output.WriteLine("id author JoergKau");

            // Print all options
            output.Write(_orchestrator.Options.ToString());

            // Report the opening book, tablebase and eval state (helpful for
            // GUI debugging). The eval-source line (M7) is parseable by the
            // match harness: it states which net is in force — a net that failed
            // to load is announced here with its reason, and the evaluation
            // is never anything but what this line says.
            output.WriteLine($"info string {_orchestrator.BookStatus}");
            output.WriteLine($"info string {_orchestrator.SyzygyStatus}");
            _orchestrator.AnnounceEvalSource(output);

            output.WriteLine("uciok");
        }
        
        private void HandlePosition(string[] parts, TextWriter output)
        {
            // Syntax: position [fen <fenstring> | startpos] [moves <move1> ... <movei>]
            
            bool isStartPos = parts.Length > 1 && parts[1].ToLowerInvariant() == "startpos";
            bool isFen = parts.Length > 1 && parts[1].ToLowerInvariant() == "fen";
            
            int movesIndex = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].ToLowerInvariant() == "moves")
                {
                    movesIndex = i;
                    break;
                }
            }
            
            string fen = "startpos";
            if (isFen)
            {
                // Join from index 2 up to movesIndex (or end)
                int fenEnd = (movesIndex > 0) ? movesIndex : parts.Length;
                if (fenEnd > 2)
                {
                    fen = string.Join(" ", parts.Skip(2).Take(fenEnd - 2));
                }
            }
            
            string[] moves = (movesIndex >= 0 && movesIndex < parts.Length - 1)
                ? parts.Skip(movesIndex + 1).ToArray()
                : Array.Empty<string>();
                
            _orchestrator.SetPosition(fen, moves, output);
        }
        
        private void HandleGo(string command, TextWriter output)
        {
            // command is the full line "go depth 10 ..."
            var limits = UciParser.ParseGoCommand(command);
            
            if (limits.IsPerft)
            {
                _orchestrator.Perft(limits.PerftDepth, output);
            }
            else
            {
                _orchestrator.Go(limits, output);
            }
        }
        
        private void HandleSetOption(string command, TextWriter output)
        {
            // Parse: setoption name <name> [value <value>]
            var match = Regex.Match(
                command,
                @"setoption\s+name\s+(?<name>[^\s]+(?:\s+[^\s]+)*?)(?:\s+value\s+(?<value>.+))?$",
                RegexOptions.IgnoreCase);
            
            if (!match.Success)
            {
                output.WriteLine("info string Invalid setoption command");
                return;
            }
            
            string name = match.Groups["name"].Value.Trim();
            string value = match.Groups["value"].Value.Trim();
            
            _orchestrator.SetOption(name, value, output);
        }
        
        private void HandlePerft(string[] parts, TextWriter output)
        {
            // perft <depth>
            if (parts.Length > 1 && int.TryParse(parts[1], out int depth))
            {
                _orchestrator.Perft(depth, output);
            }
            else
            {
                _orchestrator.Perft(1, output);
            }
        }
        
        private void HandleBench(string[] parts, TextWriter output)
        {
            // bench [depth] [threads]
            int depth = 13; // Default bench depth
            int threads = 1;
            
            // Try to parse values if present
            if (parts.Length > 1 && int.TryParse(parts[1], out int d)) depth = d;
            if (parts.Length > 2 && int.TryParse(parts[2], out int t)) threads = t;
            
            // If threads not specified in command, use engine option?
            // Usually bench command overrides.
            // PawnForge Benchmark.Run expects explicit parameters.
            
            output.WriteLine("info string Running benchmark...");
            _orchestrator.Bench(depth, threads, output);
        }
    }
}