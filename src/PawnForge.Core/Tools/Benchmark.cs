using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PawnForge.Core.Evaluation;
using TTable = PawnForge.Core.TranspositionTable.TTable;
using SearchEngine = PawnForge.Core.Search.Search;

namespace PawnForge.Core.Tools
{
    public static class Benchmark
    {
        private static readonly string[] BENCH_POSITIONS = new[]
        {
            // Startpos
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            // Kiwipete
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            // Open middlegame
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            // Tactical middlegame
            "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1"
        };

        public record BenchmarkResult(long TotalNodes, long TotalTimeMs, long AverageNps)
        {
            public override string ToString() => $"Nodes: {TotalNodes:N0} | Time: {TotalTimeMs} ms | NPS: {AverageNps:N0}";
        }

        /// <summary>
        /// Runs the fixed 4-position benchmark at <paramref name="depth"/>.
        /// </summary>
        /// <param name="depth">Per-position iterative-deepening depth.</param>
        /// <param name="numThreads">Lazy-SMP thread count (shared TT, split root).</param>
        /// <param name="output">Report sink.</param>
        /// <param name="applyOverrides">
        /// OPTIONAL per-thread configuration hook, invoked immediately after each
        /// <see cref="SearchEngine"/> is constructed and BEFORE its search starts —
        /// the same slot, and the same ordering, that the real UCI path uses
        /// (<c>EngineOrchestrator.RunSearch</c> calls <c>ApplySearchOverrides</c>
        /// there). The orchestrator passes that method here so a bench run measures
        /// the configuration the engine would ACTUALLY play.
        /// <para>
        ///: this call was missing, so <c>bench</c> built its engines with the
        /// compiled defaults and silently ignored every <c>setoption</c> knob — e.g.
        /// <c>QsDeltaMargin</c> 700 and 1500 both reported 1.314.831 nodes, making a
        /// knob A/B indistinguishable from "the knob does nothing".
        /// </para>
        /// <para>
        /// LEFT NULL, the benchmark reproduces the compiled defaults bit-for-bit, so
        /// every existing <c> number stays valid. That is why
        /// this is an optional hook and not a hard dependency: behaviour only changes
        /// for a caller that actually passes overrides.
        /// </para>
        /// </param>
        /// <param name="overrides">
        /// OPTIONAL snapshot of the knobs <paramref name="applyOverrides"/> will set,
        /// used ONLY to name them in the report. A benchmark that ran with
        /// non-default knobs must be identifiable from its own transcript, or it
        /// silently becomes the next arm's baseline.
        /// </param>
        public static BenchmarkResult Run(int depth, int numThreads, TextWriter output,
            Action<SearchEngine>? applyOverrides = null,
            IEnumerable<KeyValuePair<string, string>>? overrides = null,
            Func<Position, IPositionEvaluator>? evaluatorFactory = null)
        {
            // Materialize the snapshot once (the caller's map can change mid-run) and
            // echo it, so a knob-configured bench is self-labelling.
            List<KeyValuePair<string, string>>? appliedOverrides =
                overrides is null ? null : new List<KeyValuePair<string, string>>(overrides);
            if (appliedOverrides is { Count: > 0 })
            {
                foreach (var kv in appliedOverrides)
                    output.WriteLine($"info string bench override: {kv.Key} = {kv.Value}");
            }

            output.WriteLine("=================================");
            // The bench banner: PawnForgeCS identity (2.0 = the NNUE engine).
            output.WriteLine("PawnForgeCS 2.0 Benchmark");
            output.WriteLine("=================================");
            output.WriteLine("Depth: " + depth);
            output.WriteLine("Threads: " + numThreads);
            output.WriteLine("Positions: " + BENCH_POSITIONS.Length);
            output.WriteLine("=================================");
            output.WriteLine();

            long totalNodes = 0;
            long totalTime = 0;

            // CRITICAL FIX: Use Shared TT like UCI does (UciHandler.cs line 402)
            // Previously used local TT which could have different initialization
            var tt = TTable.Shared;

            for (int i = 0; i < BENCH_POSITIONS.Length; i++)
            {
                string fen = BENCH_POSITIONS[i];
                output.WriteLine($"Position {i + 1}/{BENCH_POSITIONS.Length}:");
                output.WriteLine("FEN: " + fen);

                // Clear TT and increment generation
                tt.Clear();  // CRITICAL: Clear TT between positions to avoid pollution
                tt.NewSearch();

                // Create position (evaluator created per-thread below)
                Position pos = Position.FromFEN(fen);

                // Shared stop signal for all threads
                var sharedSignal = new SearchSignal();

                var threadDatas = new ThreadData[numThreads];
                var searches = new SearchEngine[numThreads];
                var tasks = new Task[numThreads];

                for (int t = 0; t < numThreads; t++)
                {
                    threadDatas[t] = new ThreadData(t);
                }

                Func<long> nodeAggregator = () => {
                    long total = 0;
                    foreach (var td in threadDatas) total += td.Nodes;
                    return total;
                };

                var sw = Stopwatch.StartNew();

                for (int t = 0; t < numThreads; t++)
                {
                    int threadId = t;
                    var threadPos = new Position(pos); // copy
                    var td = threadDatas[t];
                    
                    // Create fresh evaluator for THIS thread's position (same rule as UCI).
                    tasks[t] = Task.Run(() => {
                        // The arm factory: the caller decides which evaluator
                        // the benchmark measures. There is no default arm any more —
                        // NNUE is the only evaluator, so falling back to a hardcoded
                        // classical instance would measure an engine that no longer exists.
                        IPositionEvaluator eval = evaluatorFactory is not null
                            ? evaluatorFactory(threadPos)
                            : throw new InvalidOperationException(
                                "Benchmark.Run requires an evaluatorFactory: NNUE is the only arm.");
                        
                        // All threads use the shared signal
                        var search = new SearchEngine(tt, td, sharedSignal, eval, nodeAggregator, s => output.WriteLine(s));
                        // Apply the caller's per-thread configuration (the UCI
                        // `setoption` overrides) in the SAME slot the UCI path uses,
                        // so `bench` and `go` search with identical settings.
                        applyOverrides?.Invoke(search);
                        search.TimeManager.SetInfinite(); // No time limit
                        // Run iterative deepening to specified depth
                        search.IterativeDeepening(threadPos, depth);
                    });
                }

                // Wait for the main thread (thread 0) to complete
                tasks[0].Wait();

                // Stop all helper threads
                sharedSignal.Stop();
                
                // Wait for helpers with timeout
                for (int t = 1; t < numThreads; t++)
                {
                    tasks[t].Wait(1000); // 1 second timeout
                }

                sw.Stop();

                long elapsed = sw.ElapsedMilliseconds;

                // Aggregate nodes
                long positionNodes = 0;
                for (int t = 0; t < numThreads; t++) positionNodes += threadDatas[t].Nodes;

                totalNodes += positionNodes;
                totalTime += elapsed;

                long nps = (elapsed > 0) ? (positionNodes * 1000L / elapsed) : 0;

                output.WriteLine($"Nodes: {positionNodes:N0}");
                output.WriteLine($"Time: {elapsed} ms");
                output.WriteLine($"NPS: {nps:N0}");
                output.WriteLine();
            }

            long averageNps = (totalTime > 0) ? (totalNodes * 1000L / totalTime) : 0;

            output.WriteLine("=================================");
            output.WriteLine("Benchmark Complete");
            output.WriteLine("=================================");
            output.WriteLine($"Total Nodes: {totalNodes:N0}");
            output.WriteLine($"Total Time: {totalTime} ms");
            output.WriteLine($"Average NPS: {averageNps:N0}");
            output.WriteLine("=================================");

            return new BenchmarkResult(totalNodes, totalTime, averageNps);
        }
    }
}
