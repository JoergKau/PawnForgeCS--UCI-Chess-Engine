using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using PawnForge.Core.Board;
using PawnForge.Core.Book;
using PawnForge.Core.Evaluation;
using PawnForge.Core.Search;
using PawnForge.Core.Tb;
using PawnForge.Core.TranspositionTable;
using SearchEngine = PawnForge.Core.Search.Search;

namespace PawnForge.UCI
{
    /// <summary>
    /// Orchestrates engine components: Board, Search, Evaluation, Thread Pool.
    /// Handles the execution of UCI commands that affect engine state.
    /// </summary>
    /// <summary>
    /// The engine conductor: owns the master position, the UCI options, the
    /// worker pool and the running search task. Translates UCI commands
    /// (go/stop/position/setoption) into Core search invocations, wires the
    /// per-thread Search instances (Lazy SMP: shared TT + signal, private
    /// evaluator and histories) and emits the info/bestmove stream.
    /// </summary>
    public class EngineOrchestrator
    {
        private readonly OptionsMap _options = new();

        // ── Hidden developer search options (2026-09-07) ────────────────────────────────────
        // Every public read/write bool or int property of the Search engine (all the Use* search
        // toggles plus the tuning ints: LMR weights, pruning margins, depth ceilings, …) is
        // exposed as an UNADVERTISED UCI option: `setoption name <PropertyName> value <v>` is
        // accepted and applied to every per-thread Search created for the next `go`, but the
        // options are NOT listed in the `uci` handshake (they are developer/SPRT knobs, not
        // user-facing settings). This lets a SINGLE binary run every arm of an A/B / SPRT matrix
        // instead of rebuilding per variant. Discovered by reflection so new toggles are covered
        // automatically; a small blocklist keeps operational (non-tuning) properties out.
        // Behavior-neutral until a value is actually set (empty override map = engine defaults).
        private static readonly System.Collections.Generic.Dictionary<string, System.Reflection.PropertyInfo>
            _searchOptionProps = BuildSearchOptionProps();
        private readonly System.Collections.Generic.Dictionary<string, string> _searchOverrides =
            new(StringComparer.OrdinalIgnoreCase);

        private static System.Collections.Generic.Dictionary<string, System.Reflection.PropertyInfo>
            BuildSearchOptionProps()
        {
            // Operational (not tuning) properties that must never be driven as search options.
            var blocklist = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                nameof(SearchEngine.PonderMode),
                nameof(SearchEngine.MateTarget),
                nameof(SearchEngine.CurrMoveReportIntervalMs),
            };
            var dict = new System.Collections.Generic.Dictionary<string, System.Reflection.PropertyInfo>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var p in typeof(SearchEngine).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.PropertyType != typeof(bool) && p.PropertyType != typeof(int)) continue;
                if (blocklist.Contains(p.Name)) continue;
                dict[p.Name] = p;
            }
            return dict;
        }

        // ── Eval-arm infrastructure (migration M7) ────────────────────────────────────
        // The engine has ONE evaluator arm: the v5 NNUE. The net is
        // embedded (accepted cost). A failed load is a REFUSAL, not a
        // fallback — there is nothing left to fall back to, so the reason is
        // announced parseably and re-stated (an arm that engages silently
        // measures the wrong thing).

        /// <summary>The loaded v5 net, or null when the load failed (then <see cref="NnueLoadError"/> says why).
        /// Lazily triggers the load — reading the property never observes an un-attempted state.</summary>
        public NnueNetV5? NnueNet { get { EnsureNnueLoaded(); return _nnueNet; } }

        /// <summary>Why the net failed to load, or null when it loaded.
        /// Non-null ⇒ the engine cannot search. Lazily triggers the load.</summary>
        public string? NnueLoadError { get { EnsureNnueLoaded(); return _nnueLoadError; } }

        /// <summary>The arm in force: "nnue", or "none" when the net did not
        /// load and the engine therefore refuses to search.</summary>
        public string EvalSource => NnueNet is not null ? "nnue" : "none";

        private readonly object _nnueLock = new();
        private NnueNetV5? _nnueNet;
        private string? _nnueLoadError;
        private bool _nnueLoadAttempted;

        /// <summary>EvalFile override (the `EvalFile` UCI option). Empty/null =
        /// the embedded champion net. Set to a candidate .nnue file, that file
        /// REPLACES the embedded net — this is how the training pipeline's parity
        /// gate and promote workflow test a candidate net without a rebuild
        /// (migrated from the previous generation engine, where this option was the only
        /// net source). When the file cannot be opened or validated the load
        /// FAILS — no silent fall-back to the embedded net, because the gate
        /// scripts rely on the reported eval coming from exactly the net they
        /// passed (the fill-the-gap failure class). Clearing the option restores
        /// the embedded champion.</summary>
        private string? _evalFileOverride;

        // P0 search instrumentation snapshot (aggregated after each search).
        private readonly object _searchStatsLock = new();
        private SearchStats _lastSearchStats = new();

        /// <summary>The net name to report in eval-source diagnostics: the
        /// EvalFile override's file name when an override is set, else the
        /// embedded champion's resource name.</summary>
        private string NnueSourceName =>
            string.IsNullOrEmpty(_evalFileOverride)
                ? NnueNetV5.EmbeddedNetResourceName
                : Path.GetFileName(_evalFileOverride!);

        /// <summary>Loads the net exactly once (the EvalFile override when set,
        /// else the embedded v5 champion). ANY failure is captured into
        /// <see cref="NnueLoadError"/>. No filler, no partial load, no silent
        /// path fallback: a net that cannot be validated is a net the engine
        /// refuses to use.</summary>
        private void EnsureNnueLoaded()
        {
            lock (_nnueLock)
            {
                if (_nnueLoadAttempted) return;
                _nnueLoadAttempted = true;
                try
                {
                    if (!string.IsNullOrEmpty(_evalFileOverride))
                    {
                        // Candidate-net override: the file replaces the embedded
                        // net. A failure here must NOT fall back to the embedded
                        // net — the bit-exactness check relies on the reported eval
                        // coming from exactly the net it passed.
                        using var file = File.OpenRead(_evalFileOverride!);
                        _nnueNet = NnueNetV5.Read(file);
                    }
                    else
                    {
                        // The net is embedded in PawnForge.Core (the csproj's
                        // EmbeddedResource lives there), NOT in the UCI assembly.
                        var assembly = typeof(NnueNetV5).Assembly;
                        string? name = assembly.GetManifestResourceNames()
                            .FirstOrDefault(n => n.EndsWith(NnueNetV5.EmbeddedNetResourceName, StringComparison.Ordinal));
                        if (name is null)
                            throw new InvalidDataException("the embedded net resource jokausch.nnue is missing from this build (csproj resource entry and NnueNetV5.EmbeddedNetResourceName must move together)");
                        using var stream = assembly.GetManifestResourceStream(name)
                            ?? throw new InvalidDataException($"embedded net resource '{name}' could not be opened");
                        _nnueNet = NnueNetV5.Read(stream);
                    }
                }
                catch (Exception ex)
                {
                    _nnueNet = null;
                    _nnueLoadError = ex.Message;
                }
            }
        }

        /// <summary>`EvalFile` option handler: (re)points the net source and
        /// forces the next use to (re)load. The load itself is lazy and locked
        /// (see <see cref="EnsureNnueLoaded"/>). Clearing the value restores the
        /// embedded champion net.</summary>
        private void ResetNnue(string? path)
        {
            lock (_nnueLock)
            {
                _evalFileOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
                _nnueLoadAttempted = false;
                _nnueNet = null;
                _nnueLoadError = null;
            }
        }

        /// <summary>Hidden parity-gate hook (<c>
        /// ported from the the previous generation engine): prints the NNUE eval of the
        /// current position in centipawns, side-to-move relative, as
        /// <c>nnueeval &lt;cp&gt;</c> — or <c>nnueeval none</c> when no net is
        /// loaded. The gate scripts treat BOTH "none" and a flat eval as
        /// failures (a prior failure class), so nothing here may silently degrade.</summary>
        public void NnueEval(TextWriter output)
        {
            if (_currentPosition == null)
            {
                output.WriteLine("nnueeval none");
                return;
            }
            EnsureNnueLoaded();
            if (_nnueNet is null)
            {
                output.WriteLine("nnueeval none");
                return;
            }
            var eval = new NnueEvaluator(_nnueNet);
            eval.SetPosition(_currentPosition);
            output.WriteLine($"nnueeval {eval.Evaluate()}");
        }

        /// <summary>Hidden diagnostic hook (<c>nnueregions</c>, Network-Analysis
        /// Phase 1.1): prints the real-game king-region histogram collected by
        /// <see cref="NnueRegionCounters"/> — one u64 per region, incremented on
        /// every accumulator refresh. ONE parseable line:
        ///
        /// nnueregions r&lt;i&gt;=&lt;count&gt; ... total &lt;n&gt; [of &lt;pov-events&gt;]
        ///
        /// so a script can `nnueregions reset`, run `bench`, then read the shares
        /// back. Sub-command <paramref name="arg"/>:
        /// reset — clear every counter AND start counting (measurement start)
        /// off — stop counting (leaves the counts readable)
        /// null — print the current histogram (counting state unchanged)
        /// The region count K is taken from the LOADED net (any net the engine
        /// refuses is a refusal before this line matters); with no net loaded
        /// the histogram is still printable from a prior counted run, so the
        /// print does not depend on the net at all.</summary>
        public void NnueRegions(string? arg, TextWriter output)
        {
            switch (arg)
            {
                case "reset":
                    NnueRegionCounters.Reset();
                    NnueRegionCounters.Enable();
                    output.WriteLine("info string nnueregions: counters cleared, counting STARTED");
                    break;
                case "off":
                    NnueRegionCounters.Disable();
                    output.WriteLine("info string nnueregions: counting STOPPED");
                    break;
            }

            // K: the loaded net's region count when one is loadable, else the
            // default table's 8 — enough to print a meaningful histogram even
            // after a net refusal.
            int regions = _nnueNet?.RegionCount ?? 8;
            var (counts, total) = NnueRegionCounters.Snapshot(regions);

            var parts = new List<string>(regions + 2);
            for (int r = 0; r < regions; r++)
                parts.Add($"r{r}={counts[r]}");
            parts.Add($"total {total}");
            if (NnueRegionCounters.Enabled)
                parts.Add("(counting)");
            output.WriteLine("nnueregions " + string.Join(" ", parts));

            // The per-region SHARES, in percent — the number the audit compares
            // against the pool's 0.79 % / 1.30 % for r4/r5. Zero total (nothing
            // measured yet) prints no shares rather than NaNs.
            if (total > 0)
            {
                var shares = new List<string>(regions);
                for (int r = 0; r < regions; r++)
                    shares.Add($"r{r} {100.0 * counts[r] / total:F3}%");
                output.WriteLine("info string nnueregions shares: " + string.Join(" ", shares));
            }
        }

        /// <summary>Search instrumentation hook.
        /// `searchstats reset` clears the snapshot and enables counting for the
        /// next search; bare `searchstats` prints the last search's counters;
        /// `searchstats off` stops counting.</summary>
        public void SearchStats(string? arg, TextWriter output)
        {
            switch (arg)
            {
                case "reset":
                    SearchDiagnostics.Enabled = true;
                    lock (_searchStatsLock) _lastSearchStats = new SearchStats();
                    output.WriteLine("info string searchstats: counters cleared, counting STARTED");
                    break;
                case "off":
                    SearchDiagnostics.Enabled = false;
                    output.WriteLine("info string searchstats: counting STOPPED");
                    break;
            }

            SearchStats s;
            lock (_searchStatsLock) s = _lastSearchStats;

            double qshare = s.Nodes > 0 ? 100.0 * s.QNodes / s.Nodes : 0;
            double tthit = s.TtProbes > 0 ? 100.0 * s.TtHits / s.TtProbes : 0;
            double qtthit = s.QTtProbes > 0 ? 100.0 * s.QTtHits / s.QTtProbes : 0;
            double first = s.Cutoffs > 0 ? 100.0 * s.CutoffIndex[0] / s.Cutoffs : 0;
            double second = s.Cutoffs > 0 ? 100.0 * s.CutoffIndex[1] / s.Cutoffs : 0;
            double third = s.Cutoffs > 0 ? 100.0 * s.CutoffIndex[2] / s.Cutoffs : 0;
            double qfirst = s.QCutoffs > 0 ? 100.0 * s.QCutoffIndex[0] / s.QCutoffs : 0;
            double qsecond = s.QCutoffs > 0 ? 100.0 * s.QCutoffIndex[1] / s.QCutoffs : 0;

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"searchstats nodes {s.Nodes} qnodes {s.QNodes} qshare {qshare:F1}% " +
                $"tt {s.TtProbes} hits {s.TtHits} hitrate {tthit:F1}% " +
                $"qtt {s.QTtProbes} hits {s.QTtHits} hitrate {qtthit:F1}% " +
                $"cutoffs {s.Cutoffs} first {first:F1}% second {second:F1}% third {third:F1}% " +
                $"qcutoffs {s.QCutoffs} first {qfirst:F1}% second {qsecond:F1}%"));
        }

        /// <summary>The evaluator factory: every Search instance gets the NNUE arm.
        ///
        /// NNUE is the ONLY evaluator in this engine (the HCE modules were removed
        /// once the net became the sole arm), so there is no second opinion to fall
        /// back to. A net that fails to load is a REFUSAL, not a degraded mode:
        /// silently evaluating with something else would mean every gate result and
        /// every match came from an arm nobody asked for. The reason is announced
        /// loudly by <see cref="AnnounceEvalSource"/> before this throws.
        /// </summary>
        public IPositionEvaluator CreateEvaluator(Position pos)
        {
            EnsureNnueLoaded();
            if (_nnueNet is null)
                throw new InvalidOperationException(
                    $"the NNUE net could not be loaded ({NnueLoadError}); this engine has no " +
                    "fallback evaluator and will not search. Rebuild, or restore the embedded " +
                    "the embedded champion net resource.");

            var nnue = new NnueEvaluator(_nnueNet);
            nnue.SetPosition(pos);
            return nnue;
        }

        /// <summary>The parseable eval-source announcement. On the `uci` handshake
        /// and re-stated on EVERY `isready` (P6: a GUI reads neither stderr nor
        /// the first info string, so the isready re-state is the part that
        /// actually reaches a human). A net that failed to load names the reason
        /// on BOTH stderr and the info stream, because the engine cannot search.</summary>
        public void AnnounceEvalSource(TextWriter output)
        {
            EnsureNnueLoaded();
            if (_nnueNet is not null)
            {
                output.WriteLine($"info string eval-source: nnue net {NnueSourceName}");
            }
            else
            {
                const string Refusal = "NNUE NOT LOADED: no fallback evaluator exists — the engine will not search";
                Console.Error.WriteLine($"PawnForge: {Refusal}: {_nnueLoadError}");
                output.WriteLine($"info string {Refusal}: {_nnueLoadError}");
                output.WriteLine("info string eval-source: none");
            }
        }

        /// <summary>
        /// Applies any hidden search-option overrides set via `setoption` to a freshly created
        /// per-thread Search instance. No-op when nothing has been overridden, so the engine's
        /// compiled defaults are reproduced bit-for-bit unless a knob was explicitly changed.
        /// </summary>
        private void ApplySearchOverrides(SearchEngine search)
        {
            foreach (var kv in _searchOverrides)
            {
                if (!_searchOptionProps.TryGetValue(kv.Key, out var prop)) continue;
                if (prop.PropertyType == typeof(bool) && bool.TryParse(kv.Value, out var b))
                    prop.SetValue(search, b);
                else if (prop.PropertyType == typeof(int) && int.TryParse(kv.Value, out var i))
                    prop.SetValue(search, i);
            }
        }

        /// <summary>[EXP-PARAM] Preloads search-parameter overrides from the
        /// process command line (Program.cs passes every <c>param:Name=Value</c>
        /// argv here). Uses the SAME validated store as the hidden setoption
        /// path (<see cref="_searchOverrides"/>), applied to every Search on
        /// the next `go` — so an experimental arm is the SAME published binary
        /// with a different argv: no rebuild, no test re-pin, no UCI handshake
        /// (argv cannot be silently skipped the way fastchess skips
        /// unadvertised options — the SPSA v1 void). FAIL-FAST: an unknown name
        /// or unparseable value aborts startup BEFORE any search, so a match
        /// run with a typo'd spec is void by construction instead of silently
        /// defaults-vs-defaults.</summary>
        public void PresetSearchOverrides(params string[] specs)
        {
            foreach (var spec in specs)
            {
                int eq = spec.IndexOf('=');
                if (eq <= 0)
                    throw new ArgumentException($"param override '{spec}' is not Name=Value");
                string name = spec[..eq].Trim();
                string raw = spec[(eq + 1)..].Trim();
                if (!_searchOptionProps.TryGetValue(name, out var prop))
                    throw new ArgumentException(
                        $"param override: unknown search option '{name}' " +
                        "(known: public bool/int properties of Search)");
                if (prop.PropertyType == typeof(bool))
                {
                    if (!bool.TryParse(raw, out _))
                        throw new ArgumentException($"param override: '{raw}' is not true/false for {name}");
                }
                else if (prop.PropertyType == typeof(int))
                {
                    if (!int.TryParse(raw, out _))
                        throw new ArgumentException($"param override: '{raw}' is not an integer for {name}");
                }
                else
                {
                    throw new ArgumentException($"param override: unsupported type {prop.PropertyType} for {name}");
                }
                _searchOverrides[prop.Name] = raw;
                Console.WriteLine($"info string param-override: {name}={raw} (applies to every go)");
            }
        }

        private SearchSignal _searchSignal = new SearchSignal();
        private Task? _searchTask;
        private Position? _currentPosition = Position.StartingPosition();
        private readonly Stopwatch _searchTimer = new();
        private readonly object _searchLock = new object();
        private WorkerThreadPool? _workerThreadPool = null;
        private int _currentPoolThreadCount = 0;

        // ── JIT warm-up state (see WarmUp) ──────────────────────────────────
        // _warmedUp latches the one-time tiered-JIT compilation pass so the
        // FIRST real search never runs on slow tier-0 code. Guarded so a
        // warm-up can only ever run once per process lifetime.
        private bool _warmedUp;

        // ── Opening book state ────────────────────────────────────────────
        // Loaded on startup (auto-detect) and on `setoption name Book File`.
        // DISABLED by default (`Own Book` option, default off): CCRL requires
        // the engine's own book to be off (it supplies its own). When enabled
        // and the current position is inside the book, `go` returns the book
        // move directly instead of searching.
        private readonly CtgBook _book = new();
        private bool _bookEnabled = false;
        private int _bookDepth = 16;      // plies from the initial position
        private int _bookPlies = 0;       // plies applied in the last position cmd

        // ── Ponder state (see PonderHit / RunSearch) ────────────────────────
        // _pondering : a `go ponder` search is running (searching the
        // opponent's move; no bestmove is volunteered).
        // _ponderHit : `ponderhit` arrived - the running ponder search must
        // suppress its bestmove (a fresh normal search emits it).
        // _ponderPos : the exact position the ponder search was given (the
        // opponent is to move); advanced by the pondered move.
        // _ponderLims : the `go ponder` limits (carry both clocks) reused for
        // the post-ponderhit normal search.
        // _ponderSrch : thread-0 Search instance of the ponder search, source
        // of CurrentBestMove (the expected opponent move).
        private volatile bool _pondering;
        private volatile bool _ponderHit;
        private Position? _ponderPosition;
        private SearchLimits? _ponderLimits;
        private SearchEngine? _ponderSearch;

        public OptionsMap Options => _options;
        public Position? CurrentPosition => _currentPosition;

        /// <summary>Human-readable status of the opening book (for `uci` output).</summary>
        public string BookStatus => _book.IsLoaded
            ? $"opening book loaded: {_book.BookPath} ({_book.EntryCount} positions)"
            : "no opening book loaded";

        /// <summary>Human-readable status of the Syzygy tablebases (for `uci` output).</summary>
        public string SyzygyStatus => Syzygy.IsInitialized && Syzygy.MaxPieces > 0
            ? $"syzygy tablebases: {Syzygy.WdlTableCount} WDL tables, up to {Syzygy.MaxPieces} pieces"
            : "syzygy tablebases: none (set SyzygyPath)";

        public EngineOrchestrator()
        {
            InitializeOptions();
            InitializeWorkerPool(1);
            LoadBook(""); // auto-detect a .ctg book at startup
        }

        /// <summary>`debug on/off` UCI command state - enables verbose diagnostics.</summary>
        public bool DebugMode { get; private set; }

        /// <summary>
        /// DTZ root probe: when ON, a root position inside the tables is played directly
        /// from the DTZ probe (bypassing the search — no repetition awareness, DTZ-optimal
        /// ≠ fastest mate; see the `go` handler note). Default OFF.
        /// </summary>
        public bool SyzygyRootProbe { get; private set; }

        public void SetDebugMode(bool on) => DebugMode = on;

        private void InitializeOptions()
        {
            // CCRL-ready defaults : Hash 256 MB (the CCRL condition
            // is 128/256 MB), Threads 1. The Hash default matches the shared TT's
            // initial size (TranspositionTable.Shared = new TTable(256)).
            //
            // Bug 150: the callback used to hand the PARSED value straight to
            // TTable.Resize. A value above the declared maximum then overflowed
            // the allocation-size cast inside Resize — heap corruption that
            // killed the process, sometimes only seconds later at an unrelated
            // allocation. The value is clamped into the DECLARED range first
            // (the same numbers the option publishes to the GUI), through the
            // testable ClampHashSize helper; Resize validates its argument as
            // well (defence in depth).
            const int hashMinMb = 1;
            const int hashMaxMb = 1024;
            _options.Add(new UciOption("Hash", 256, hashMinMb, hashMaxMb, val => 
            {
                if (int.TryParse(val, out int size))
                {
                    TTable.Shared.Resize(ClampHashSize(size, hashMinMb, hashMaxMb));
                }
            }));
            
            _options.Add(new UciOption("Threads", 1, 1, 256, val => 
            {
                if (int.TryParse(val, out int threads))
                {
                    InitializeWorkerPool(threads);
                }
            }));
            
            // NOTE: MultiPV is NOT announced - it is not implemented (single PV only).
            _options.Add(new UciOption("Move Overhead", 50, 0, 5000, _ => { })); // default 10 → 50 ms (the reference's value; 10 ms was under-provisioned for GUI/matcher lag)


            _options.Add(UciOption.Button("Clear Hash", () => TTable.Shared.Clear()));

            // Opening book options. `Book File` with an empty value auto-detects
            // the first *.ctg next to the executable / in a books/ subdirectory.
            // Own Book defaults OFF (CCRL requirement — a tournament book must be
            // disabled; CCRL supplies its own 12-move book).
            _options.Add(new UciOption("Own Book", false, val => _bookEnabled = val == "true"));
            _options.Add(new UciOption("Book File", "", val => LoadBook(val)));
            _options.Add(new UciOption("Book Depth", _bookDepth, 0, 100, val =>
            {
                if (int.TryParse(val, out int depth))
                    _bookDepth = Math.Max(0, depth);
            }));

            // Syzygy tablebase options (see Core/Tb/Syzygy.cs).
            _options.Add(new UciOption("SyzygyPath", "", val =>
            {
                Syzygy.Initialize(val);                // One-time load confirmation in the match log:
                // proves the tables were actually found/loaded — a silent load failure
                // would otherwise be invisible (the status prints only in the `uci`
                // handshake, which precedes setoption). Emitted directly on stdout:
                // setoption callbacks have no writer, and `info string` is legal at any
                // time under UCI.
                Console.Out.WriteLine($"info string {SyzygyStatus}");
            }));
            _options.Add(new UciOption("SyzygyRootProbe", false, val => SyzygyRootProbe = val == "true"));
            _options.Add(new UciOption("Syzygy50MoveRule", true, val => Syzygy.UseRule50 = val == "true"));

            // Candidate-net override (migrated from the the previous generation engine, where
            // it was the only net source): when set to a .nnue file, that file
            // replaces the embedded champion for THIS process — the training
            // pipeline's bit-exactness check and promote workflow test candidate nets
            // this way without a rebuild. Empty restores the embedded champion.
            // A file that fails to load is a REFUSAL (see EnsureNnueLoaded), never
            // a silent fall-back to the embedded net.
            _options.Add(new UciOption("EvalFile", "", val => ResetNnue(val)));

            // The eval-arm switch is GONE: NNUE is the only evaluator, so there is
            // nothing to flip. The former Hce_* module toggles (Hce_Pawns,
            // Hce_Mobility, Hce_Rooks, Hce_Threats(+Scale), Hce_See,
            // Hce_Space(+Threshold/Scale), Hce_KingSafety, Hce_Checks,
            // Hce_WaveB, Hce_Endgame, Hce_TbDamp, Hce_Incremental and
            // Hce_WeightsFile) were removed with the HCE modules themselves —
            // an option that toggles a deleted module is worse than no option,
            // because a GUI would still send it and it would be silently inert.
            // NOTE: the old "SyzygyProbeDepth" option was REMOVED: depth-gated probing
            // let the heuristic pruning stack mask exact TB values (see Syzygy.ProbingEnabled).
            // Probing is now material-gated only and depth-unconditional, like in the
            // strongest mainstream engines.
            // RESEARCH LEVERS — HIDDEN (2026-11-07). Every Search tuning knob is
            // registered through OptionsMap.AddLever, i.e. it is functional over
            // `setoption` / the `param:` argv override but is NOT announced in
            // the `uci` handshake, so no GUI lists it. Rationale: ~80 levers
            // bury the twelve user-facing options in every GUI's option dialog,
            // and no public user should be nudged into A/B toggles that have
            // only in-house leverage. DELIVERY CAVEAT for testers: fastchess
            // silently skips any `option.X` argument for a name it does not see
            // in the handshake (proven with a logging fake engine: zero
            // `setoption` lines sent), so a match arm that wants a lever uses
            // the `param:Name=value` argv override (passed verbatim to the
            // engine) or scripted stdin, never `option.*`. Every setter records
            // into _searchOverrides; ApplySearchOverrides applies them per
            // thread on the next `go`. Defaults MUST equal the Search compiled
            // defaults (locked by AdvertisedSearchOptionTests, which now walks
            // the FULL catalog rather than only the advertised lines).
            // Behavior-neutral until set (empty map).
            _options.AddLever(new UciOption(nameof(SearchEngine.FutilityMaxDepth), 7, 4, 10, val => _searchOverrides[nameof(SearchEngine.FutilityMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.LmpMaxDepth), 4, 2, 8, val => _searchOverrides[nameof(SearchEngine.LmpMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.SeePruningMaxDepth), 8, 2, 10, val => _searchOverrides[nameof(SearchEngine.SeePruningMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpMaxDepth), 4, 4, 12, val => _searchOverrides[nameof(SearchEngine.RfpMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RazorMaxDepth), 3, 2, 8, val => _searchOverrides[nameof(SearchEngine.RazorMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.NmpBaseReduction), 3, 1, 6, val => _searchOverrides[nameof(SearchEngine.NmpBaseReduction)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.QsDeltaMargin), 1100, 700, 1500, val => _searchOverrides[nameof(SearchEngine.QsDeltaMargin)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.HistPruneMargin), 2000, 800, 4000, val => _searchOverrides[nameof(SearchEngine.HistPruneMargin)] = val));
            // Improving-awareness deltas (same hidden-lever policy). Defaults 0
            // = flat margins (bit-identical); the reference-port shape is
            // RfpImprovingDelta=-60, RfpNotImprovingDelta=2,
            // RfpOpponentWorseningDelta=-13, RazorNotImprovingDelta=20.
            // Range ±200 covers the reference shape with tuning headroom.
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpImprovingDelta), 0, -200, 200, val => _searchOverrides[nameof(SearchEngine.RfpImprovingDelta)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpNotImprovingDelta), 0, -200, 200, val => _searchOverrides[nameof(SearchEngine.RfpNotImprovingDelta)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpOpponentWorseningDelta), 0, -200, 200, val => _searchOverrides[nameof(SearchEngine.RfpOpponentWorseningDelta)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RazorNotImprovingDelta), 0, -200, 200, val => _searchOverrides[nameof(SearchEngine.RazorNotImprovingDelta)] = val));

            // ── WAVE-A feature-family levers (hidden, see the policy above) ──────
            // These levers expose code paths that already exist behind a Search
            // property — registering them changes no behaviour, because every
            // default below equals the compiled default and _searchOverrides
            // stays empty until a value is set.
            //
            // Every entry uses the UciOption(string, bool, Action<string>)
            // overload (type "check", default true|false) and writes into the
            // SAME _searchOverrides store the hidden SetOption path uses,
            // applied by ApplySearchOverrides on the next `go`. bool.TryParse in
            // ApplySearchOverrides accepts the "true"/"false" the callback
            // receives, so no value conversion is needed here.
            //
            // Gate: AdvertisedSearchOptionTests pins each catalog default
            // against the compiled default over the FULL catalog (hidden
            // levers included), so an accidental default drift cannot ship
            // silently, and its node-count probes prove each lever really
            // moves the search.
            //
            // NOTE (polarity): the levers that ship ON are as important to
            // register as the OFF ones — their A/B arm is `=false`.
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrV2), false, val => _searchOverrides[nameof(SearchEngine.UseLmrV2)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTable), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTable)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrNoisyMoves), false, val => _searchOverrides[nameof(SearchEngine.UseLmrNoisyMoves)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTerm1NotImproving), false, val => _searchOverrides[nameof(SearchEngine.UseTerm1NotImproving)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermTtPv), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermTtPv)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermCutNode), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermCutNode)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermNotImproving), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermNotImproving)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermPvDistance), false, val => _searchOverrides[nameof(SearchEngine.UseLmrTermPvDistance)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermHistory), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermHistory)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermFutile), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermFutile)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermFailHighCount), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermFailHighCount)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrTermComplexity), true, val => _searchOverrides[nameof(SearchEngine.UseLmrTermComplexity)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseCutNode), true, val => _searchOverrides[nameof(SearchEngine.UseCutNode)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseRefLmp), false, val => _searchOverrides[nameof(SearchEngine.UseRefLmp)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseRefHistoryPruning), false, val => _searchOverrides[nameof(SearchEngine.UseRefHistoryPruning)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseRefFutilityFormula), false, val => _searchOverrides[nameof(SearchEngine.UseRefFutilityFormula)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseRefSeeThresholds), true, val => _searchOverrides[nameof(SearchEngine.UseRefSeeThresholds)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseBadNoisyPruning), false, val => _searchOverrides[nameof(SearchEngine.UseBadNoisyPruning)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseAlphaRaiseReduction), false, val => _searchOverrides[nameof(SearchEngine.UseAlphaRaiseReduction)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseAdaptiveReSearch), false, val => _searchOverrides[nameof(SearchEngine.UseAdaptiveReSearch)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseProbCut), false, val => _searchOverrides[nameof(SearchEngine.UseProbCut)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseSecondaryProbCut), false, val => _searchOverrides[nameof(SearchEngine.UseSecondaryProbCut)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseNegativeExtensions), true, val => _searchOverrides[nameof(SearchEngine.UseNegativeExtensions)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseImprovingFlag), true, val => _searchOverrides[nameof(SearchEngine.UseImprovingFlag)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseRefCorrectionHistory), true, val => _searchOverrides[nameof(SearchEngine.UseRefCorrectionHistory)] = val)); // owner bake (measured +19.13); was false
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTwofoldRepetitionDraw), true, val => _searchOverrides[nameof(SearchEngine.UseTwofoldRepetitionDraw)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseQuiescenceChecks), false, val => _searchOverrides[nameof(SearchEngine.UseQuiescenceChecks)] = val));

            // AUDIT WAVE A2 — the spins the twelve need. Same rule as above:
            // existing code, advertisedDefault == compiled default.
            // NmpDepthDivisor the reference's exact value is 2 (we ship 4), so the
            // lever's experiment is a NUMBER, not a toggle.
            // MaxPathExtensions 32 shipped; the reference's check extension is
            // uncapped, so this prices our safety valve.
            _options.AddLever(new UciOption(nameof(SearchEngine.NmpDepthDivisor), 4, 1, 8, val => _searchOverrides[nameof(SearchEngine.NmpDepthDivisor)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.MaxPathExtensions), 32, 8, 64, val => _searchOverrides[nameof(SearchEngine.MaxPathExtensions)] = val));

            // AUDIT WAVE A3 — the aspiration window. Wholly NEW code: these were
            // `private const`s inside IterativeDeepening, so the window could not be
            // tested without a rebuild per arm. Every default reproduces the old const
            // exactly (50 / 5 / 8), so this is behaviour-neutral until an arm sets one.
            // Recommended first run: AspirationBase=33 (the design reference's ladder start; the reference
            // is 17). See Search.AspirationBase for the recorded 25/35/40 ladder and why
            // the margin re-basing since then makes a re-test honest.
            _options.AddLever(new UciOption(nameof(SearchEngine.AspirationBase), 50, 10, 100, val => _searchOverrides[nameof(SearchEngine.AspirationBase)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.AspirationStartDepth), 5, 1, 10, val => _searchOverrides[nameof(SearchEngine.AspirationStartDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.AspirationMaxCycles), 8, 1, 16, val => _searchOverrides[nameof(SearchEngine.AspirationMaxCycles)] = val));

            // ── FULL-AUDIT PHASE 1–3 ──
            // The first three items of the recommended execution order, ADVERTISED so the
            // SPRT/match arms can deliver them (an unadvertised option is silently skipped
            // by fastchess — the SPSA v1 void,
            // FullAuditPhase123Tests). Every default equals the compiled default, so
            // behaviour is bit-identical until an arm sets a value:
            // UseFormulaRfp=false keeps the clamped texel array + gate 4; the formula
            // path (margin = depth·(improving?65:95), gate 8) is the experiment.
            // LmrV2NotImprovingWeight ships at the reference's REAL 70 (was mis-transcribed
            // as 275); only read on the UseLmrV2 path, which is itself OFF by default.
            // D1 UseQsTtProbe=false keeps QS TT-free; ON probes/stores at depth 0.
            _options.AddLever(new UciOption(nameof(SearchEngine.UseFormulaRfp), true, val => _searchOverrides[nameof(SearchEngine.UseFormulaRfp)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpImprovingScale), 65, 10, 200, val => _searchOverrides[nameof(SearchEngine.RfpImprovingScale)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpNotImprovingScale), 95, 10, 300, val => _searchOverrides[nameof(SearchEngine.RfpNotImprovingScale)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.RfpFormulaMaxDepth), 8, 4, 12, val => _searchOverrides[nameof(SearchEngine.RfpFormulaMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseQsTtProbe), true, val => _searchOverrides[nameof(SearchEngine.UseQsTtProbe)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.LmrV2NotImprovingWeight), 70, 0, 600, val => _searchOverrides[nameof(SearchEngine.LmrV2NotImprovingWeight)] = val));

            // ── FULL-AUDIT ITEM 10 (G1): ──
            // Clock-imbalance TM. Two-sided (spend more ahead, less behind), superseding the
            // one-sided TM3 (UseTimeAdvantage) when ON; clipped by the existing hard limit so
            // the zero-flag guarantee is untouched. Default OFF = indistinguishable from the
            // shipped TM. NOTE the gate for this item is a **TIMED match** — a
            // fixed-depth bench pre-flight is meaningless here by construction (it never
            // consults the clock), which is why a fixed-depth bench cannot validate it.
            _options.AddLever(new UciOption(nameof(SearchEngine.UseClockImbalance), false, val => _searchOverrides[nameof(SearchEngine.UseClockImbalance)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.ClockImbalanceAheadScale), 50, 0, 100, val => _searchOverrides[nameof(SearchEngine.ClockImbalanceAheadScale)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.ClockImbalanceBehindScale), 50, 0, 80, val => _searchOverrides[nameof(SearchEngine.ClockImbalanceBehindScale)] = val));

            // ── STRUCTURAL CLOSURES BAND 1 ──
            // The structural closures (prune-more with cheap verification + eval accuracy)
            // plus the decoupled bad-capture LMR leg. Same hidden-lever delivery rule as
            // every band above: each default equals the compiled default, so behaviour is
            // bit-identical until an arm sets a value. One arm per toggle. (Match runners:
            // deliver these via the `param:` argv override, never `option.*` — see the
            // hidden-lever policy note above.)
            // UseTtScoreEval — TT-score-as-eval (main + QS; QS half rides UseQsTtProbe)
            // UseSkipFans — skip-rest on the legacy history-pruning fan
            // UseQsNarrowing (+2) — recapture-only past qs ply 8, no promotions past 2
            // UseSeeReach (+4) — the reference's SEE reach: ceiling 10, −44·d / −17·d², −hist/133
            // UseLmrNoisyMoves — (already advertised in Wave A) now honoured by v1 LMR too
            // UseTtExtension (+1) — the reference TtExtDepth: out-of-bounds TT entry at depth ≤ 6 → depth++
            // UseVerifiedNmp (+1) — mate clamp + deep verification/seal; UseRefNmpEvalScale = rider (d)
            // UseRefNmpEvalScale is now baked ON (owner override; v9 +12.17, inconclusive).
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTtScoreEval), true, val => _searchOverrides[nameof(SearchEngine.UseTtScoreEval)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseSkipFans), false, val => _searchOverrides[nameof(SearchEngine.UseSkipFans)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseQsNarrowing), false, val => _searchOverrides[nameof(SearchEngine.UseQsNarrowing)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.QsRecaptureOnlyPly), 8, 0, 16, val => _searchOverrides[nameof(SearchEngine.QsRecaptureOnlyPly)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.QsPromotionMaxPly), 2, 0, 8, val => _searchOverrides[nameof(SearchEngine.QsPromotionMaxPly)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseSeeReach), false, val => _searchOverrides[nameof(SearchEngine.UseSeeReach)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.SeeReachMaxDepth), 10, 2, 12, val => _searchOverrides[nameof(SearchEngine.SeeReachMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.SeeReachQuietMargin), -44, -200, 0, val => _searchOverrides[nameof(SearchEngine.SeeReachQuietMargin)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.SeeReachNoisyMargin), -17, -200, 0, val => _searchOverrides[nameof(SearchEngine.SeeReachNoisyMargin)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.SeeReachHistoryDivisor), 133, 32, 512, val => _searchOverrides[nameof(SearchEngine.SeeReachHistoryDivisor)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTtExtension), false, val => _searchOverrides[nameof(SearchEngine.UseTtExtension)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.TtExtensionMaxDepth), 6, 0, 12, val => _searchOverrides[nameof(SearchEngine.TtExtensionMaxDepth)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseVerifiedNmp), false, val => _searchOverrides[nameof(SearchEngine.UseVerifiedNmp)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseRefNmpEvalScale), true, val => _searchOverrides[nameof(SearchEngine.UseRefNmpEvalScale)] = val)); // owner bake (measured +12.17); was false

            // ── STRUCTURAL CLOSURES BAND 2 ──
            // Reduction & eval plumbing: the RFP fail-soft half + linear margin are one
            // behaviour change folded into the already-advertised `UseFormulaRfp`, and the
            // already-advertised `UseLmrV2` re-arms with the verified reference term set,
            // so neither needs a new option. The three NEW toggles:
            // UseLinearFutilityMargin — 108 + 82·d + hist/98 − 4·moves on the live path
            // UseFormulaLmp — (3+38d)/10 / (1+83d)/10, gate 8 (alias path of UseRefLmp)
            // UseEvalCache — per-thread static-eval cache (NPS→depth)
            // Same delivery rule: advertised default == compiled default (false), so a fresh
            // binary is bit-identical until an arm sets a value.
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLinearFutilityMargin), false, val => _searchOverrides[nameof(SearchEngine.UseLinearFutilityMargin)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseFormulaLmp), false, val => _searchOverrides[nameof(SearchEngine.UseFormulaLmp)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseEvalCache), false, val => _searchOverrides[nameof(SearchEngine.UseEvalCache)] = val));

            // ── STRUCTURAL CLOSURES BAND 3 ──
            // Extensions & feedback. Nine new toggles, all default-OFF except the
            // trio's three sub-toggles (default ON INSIDE the default-OFF master, so the bundle
            // can be bisected without a rebuild). The gate/tuning constants
            // (SelectiveSingularMinDepth, EvalSwingBonus*) stay hidden but settable through the
            // reflection path — they are not arms on their own.
            // UseExtensionTrio (+ UseTrioHindsight / UseTrioAlphaRaise / UseTrioTtShrink)
            // UseSelectiveSingular
            // UseEvalSwingBonus
            // UseLmrHistoryPrecision
            // UseLmrReductionFloor
            // UseQsCorrectionHistory
            _options.AddLever(new UciOption(nameof(SearchEngine.UseExtensionTrio), false, val => _searchOverrides[nameof(SearchEngine.UseExtensionTrio)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTrioHindsight), true, val => _searchOverrides[nameof(SearchEngine.UseTrioHindsight)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTrioAlphaRaise), true, val => _searchOverrides[nameof(SearchEngine.UseTrioAlphaRaise)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseTrioTtShrink), true, val => _searchOverrides[nameof(SearchEngine.UseTrioTtShrink)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseSelectiveSingular), false, val => _searchOverrides[nameof(SearchEngine.UseSelectiveSingular)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseEvalSwingBonus), false, val => _searchOverrides[nameof(SearchEngine.UseEvalSwingBonus)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrHistoryPrecision), true, val => _searchOverrides[nameof(SearchEngine.UseLmrHistoryPrecision)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLmrReductionFloor), false, val => _searchOverrides[nameof(SearchEngine.UseLmrReductionFloor)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseQsCorrectionHistory), false, val => _searchOverrides[nameof(SearchEngine.UseQsCorrectionHistory)] = val));

            // ── STRUCTURAL CLOSURES BAND 4 ──
            // Control-plane & NPS polish. (PEXT/magic) is an AUTOMATIC
            // runtime dispatch — no option: the engine benchmarks both at startup and keeps the
            // faster, with PAWNFORGE_DISABLE_PEXT / PAWNFORGE_FORCE_PEXT as diagnostics.
            // UseCapturesOnlyMovegen — captures-only generation (NPS; node-identical)
            // UseLazySee — lazy SEE in the MovePicker (NPS/ordering)
            // UseStableTimeManagement— the reference's three-signal soft-limit scaler (timed)
            // Contempt — draw score spin (0 = off)
            // UseMicroRiders — QS fail-high averaging + IIR quality gate + 1024-node poll
            _options.AddLever(new UciOption(nameof(SearchEngine.UseCapturesOnlyMovegen), false, val => _searchOverrides[nameof(SearchEngine.UseCapturesOnlyMovegen)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseLazySee), false, val => _searchOverrides[nameof(SearchEngine.UseLazySee)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseStableTimeManagement), true, val => _searchOverrides[nameof(SearchEngine.UseStableTimeManagement)] = val));
            _options.Add(new UciOption(nameof(SearchEngine.Contempt), 0, -50, 50, val => _searchOverrides[nameof(SearchEngine.Contempt)] = val));
            _options.AddLever(new UciOption(nameof(SearchEngine.UseMicroRiders), true, val => _searchOverrides[nameof(SearchEngine.UseMicroRiders)] = val));
        }

        /// <summary>
        /// Clamps a `setoption Hash` value into the option's declared MB range
        /// (predecessor bug 150). The Hash callback used to apply the raw value,
        /// and an out-of-range maximum overflowed TTable.Resize's allocation-size
        /// cast — heap corruption that killed the process, sometimes only seconds
        /// after the command. Extracted into a helper so the clamp itself is
        /// directly unit-testable, like Search.IsQuietStoreMove for bug 149.
        /// </summary>
        public static int ClampHashSize(int sizeMb, int minMb, int maxMb)
            => Math.Clamp(sizeMb, minMb, maxMb);

        /// <summary>
        /// Loads the opening book. An empty or null path auto-detects the
        /// first .ctg book (executable directory + books/ subdirectory). A
        /// failed load leaves the book disabled without crashing.
        /// </summary>
        public void LoadBook(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = CtgBook.FindDefaultBookPath();
                    if (path == null)
                    {
                        _book.Load(path ?? "");
                        return;
                    }
                }
                if (!File.Exists(path))
                {
                    _book.Load("");
                    return;
                }
                _book.Load(path);
            }
            catch
            {
                _book.Load("");
            }
        }

        private void InitializeWorkerPool(int threadCount)
        {
            if (threadCount != _currentPoolThreadCount || _workerThreadPool == null)
            {
                _workerThreadPool?.Shutdown();
                _workerThreadPool = new WorkerThreadPool(threadCount);
                _currentPoolThreadCount = threadCount;
            }
        }

        /// <summary>
        /// One-time JIT warm-up, invoked from the first `isready` (UCI sync
        /// point). Without it the FIRST search pays the tiered-JIT compilation
        /// cost: RyuJIT starts methods on unoptimized tier-0 code and only
        /// promotes the hot ones to tier-1 (PGO-optimized) after enough
        /// invocations, so the first ~100-300 ms of a search run on slow code.
        ///
        /// Two passes force compilation of the engine's hot paths:
        /// 1. NNUE evaluation on startpos (8× Evaluate) - compiles the
        /// eval/scan hot path.
        /// 2. A ~10 ms fixed-time shallow search on startpos - compiles the
        /// search/move-generation/move-ordering/SEE hot loops.
        ///
        /// SAFETY:
        /// * Runs at most once per process (latched by _warmedUp).
        /// * Skipped if a real search is already in flight (a mid-search
        /// `isready` must not add latency or contend on the shared TT).
        /// * Uses a PRIVATE SearchSignal/evaluator and is wrapped in
        /// try/catch: a warm-up failure can never break the UCI
        /// handshake.
        /// * Writes into a PRIVATE transposition table (quirk Q7 fix), so the
        /// shared table and every later search are exactly as if the
        /// warm-up had never run.
        /// </summary>
        public void WarmUp(TextWriter output)
        {
            if (_warmedUp) return;
            if (_searchTask is { IsCompleted: false }) return;
            _warmedUp = true;

            try
            {
                Position pos = Position.StartingPosition();

                // 1) NNUE warm-up: compiles the net's accumulator and SIMD
                // forward paths. Best-effort — a failed load has already been
                // captured and announced; CreateEvaluator refuses later.
                EnsureNnueLoaded();
                if (_nnueNet is not null)
                {
                    var nnue = new NnueEvaluator(_nnueNet);
                    nnue.SetPosition(pos);
                    for (int i = 0; i < 8; i++)
                        _ = nnue.Evaluate();
                }

                // 3) Shallow search warm-up (search/movegen/SEE hot paths) with
                // the ACTIVE arm, so the tier-0 → tier-1 promotion happens on
                // the code the first real search runs.
                var signal = new SearchSignal();
                var threadData = new ThreadData(0);
                var evaluator = CreateEvaluator(pos);
                Func<long> nodeAggregator = () => threadData.Nodes;
                // Quirk Q7 fix: the warm-up searches into a PRIVATE 1 MB table.
                // It used to write into TTable.Shared, so the first real search
                // started from time-dependent leftovers of a throwaway search
                // (only `ucinewgame` cleaned them up). The JIT benefit is
                // unchanged - the same code runs - but no shared state is touched.
                var warmUpTable = new TTable(1);
                var search = new SearchEngine(warmUpTable, threadData, signal, evaluator, nodeAggregator, _ => { });
                search.TimeManager.SetFixedTime(10, PawnForge.Core.Search.TimeManager.NowMs());
                search.IterativeDeepening(new Position(pos), 6);

                if (DebugMode)
                    output.WriteLine("info string warm-up complete");
            }
            catch
            {
                // Best-effort: the engine must still answer readyok.
            }
        }

        /// <summary>
        /// Answers the UCI <c>isready</c> probe. This must return IMMEDIATELY,
        /// even while a search is running: <c>isready</c> is a synchronization
        /// ping, not a "wait for the search" command (UCI spec). Waiting on
        /// <see cref="_searchTask"/> here blocks the single command-reading
        /// thread, so the later <c>stop</c>/<c>quit</c> in stdin is never read
        /// and GUI watchdogs kill the engine (a hang).
        /// </summary>
        public void ConfirmReady(TextWriter output)
        {
            output.WriteLine("readyok");
        }

        public void UciNewGame()
        {
            lock (_searchLock)
            {
                Stop();
                TTable.Shared.Clear();
                _currentPosition = Position.StartingPosition();
            }
        }

        public void SetPosition(string fen, string[] moves, TextWriter output)
        {
            lock (_searchLock)
            {
                _bookPlies = 0; // depth is measured from the position command's start

                // A rejected position command is reported and changes nothing: the
                // previous position stays, so the engine never silently searches a
                // position that the GUI did not ask for. The moves are applied to a
                // copy and stored only when all of them are legal.
                Position position;
                try
                {
                    position = Position.FromFEN(fen == "startpos"
                        ? "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
                        : fen);
                }
                catch (Exception error)
                {
                    output.WriteLine($"info string Error: invalid position, previous position kept: {error.Message}");
                    return;
                }

                int bookPlies = 0; // depth is measured from the position command's start
                foreach (var moveStr in moves)
                {
                    var move = UciParser.ParseMove(position, moveStr);
                    if (move.IsNone)
                    {
                        output.WriteLine($"info string Error: illegal move {moveStr}, position command rejected, previous position kept");
                        return;
                    }
                    position.MakePublicMove(move);
                    bookPlies++;
                }
                _currentPosition = position;
                _bookPlies = bookPlies;
            }
        }

        public void Go(SearchLimits limits, TextWriter output)
        {
            lock (_searchLock)
            {
                if (_currentPosition == null) return;

                // Opening book: when enabled, inside the book and within the
                // book depth, play the book move immediately instead of
                // searching. Skipped for perft and for `go searchmoves`
                // (the restriction must be honored), and for pondering
                // (the ponder search predicts the opponent's move).
                if (!limits.IsPerft && limits.SearchMoves.Count == 0 && !limits.Ponder
                    && _bookEnabled && _book.IsLoaded && (_bookDepth <= 0 || _bookPlies < _bookDepth))
                {
                    Move bookMove = _book.PickBookMove(_currentPosition, Random.Shared);
                    if (!bookMove.IsNone)
                    {
                        output.WriteLine($"info string book move: {UciParser.FormatMove(bookMove)}");
                        output.WriteLine($"bestmove {UciParser.FormatMove(bookMove)}");
                        return;
                    }
                }

                // Syzygy tablebase root probe :
                // DEFAULT OFF. The old behavior played the DTZ-optimal move DIRECTLY,
                // bypassing the search entirely — that has three defects:
                // 1. no repetition awareness (DTZ-optimal play can shuffle into a
                // threefold draw — observed in endgame testing),
                // 2. DTZ-optimal ≠ fastest mate (slow conversions: DTZ counts plies
                // to the next zeroing move, not to mate),
                // 3. it bypasses every search-side strength (repetition avoidance,
                // time management, eval-guided defense).
                // The sound path — which the search provides on its own — is the WDL
                // LEAF probe (exact values at depth <= SyzygyProbeDepth): winning leaves
                // score +TbWinScore, drawn leaves 0, so the search itself navigates to
                // real wins while the repetition rule keeps it honest.
                // The DTZ root probe stays available behind the toggle for analysis;
                // mate/stalemate at the root inside the tables is handled by the normal
                // search (no-legal-moves -> bestmove 0000 fallback below).
                if (SyzygyRootProbe
                    && !limits.IsPerft && limits.SearchMoves.Count == 0 && !limits.Ponder
                    && Syzygy.IsInitialized && Syzygy.MaxPieces > 0
                    && BitOperations.PopCount(_currentPosition.GetAllOccupancy()) <= Syzygy.MaxPieces)
                {
                    var tb = Syzygy.ProbeRoot(_currentPosition);
                    if (tb.IsCheckmate || tb.IsStalemate)
                    {
                        output.WriteLine("bestmove 0000");
                        return;
                    }
                    if (!tb.Failed && !tb.Move.IsNone)
                    {
                        output.WriteLine($"info string tablebase move: {UciParser.FormatMove(tb.Move)} (wdl {tb.Wdl}, dtz {tb.Dtz})");
                        output.WriteLine($"bestmove {UciParser.FormatMove(tb.Move)}");
                        return;
                    }
                }

                // If a search is running, stop it first
                if (DebugMode) output.WriteLine("info string debug: stopping previous search");
                Stop();

                // Clone position for search
                var searchPos = new Position(_currentPosition);

                // Enter/leave ponder mode according to `go ponder` / `go`.
                _pondering = limits.Ponder;
                _ponderHit = false;
                if (limits.Ponder)
                {
                    _ponderPosition = searchPos;
                    _ponderLimits = limits;
                }

                StartSearch(searchPos, limits, output, limits.Ponder);
            }
        }

        /// <summary>
        /// `stop` (UCI): abort the running search. The stopped search emits its
        /// bestmove - for a ponder search that is stopped (the opponent did NOT
        /// play the expected move) it is the best move of the last completed
        /// iteration (quirk Q5 fix; it used to be `bestmove 0000`).
        /// </summary>
        public void Stop()
        {
            _searchSignal.Stop();
            _searchTask?.Wait();
            _pondering = false;
        }

        /// <summary>
        /// `ponderhit` (UCI): the opponent has played exactly the move the
        /// engine was pondering. The pondered root move is made on the search
        /// position and a fresh NORMAL search is started from there for the
        /// engine's own reply (the shared TT carries the ponder work over).
        /// The ponder search itself is stopped without emitting a bestmove.
        ///
        /// Reference: UCI protocol specification (ponder section),
        /// http://wbec-ridderkerk.nl/html/UCIProtocol.html
        /// </summary>
        public void PonderHit(TextWriter output)
        {
            lock (_searchLock)
            {
                // Only meaningful while a ponder search is actually running.
                if (!_pondering) return;

                // Suppress the ponder search's bestmove: it is replaced by the
                // fresh normal search below, which emits the real move.
                _ponderHit = true;

                // The expected opponent move = thread-0's current root best move.
                Move ponderMove = _ponderSearch?.CurrentBestMove ?? Move.None;

                // Stop the ponder search (its RunSearch sees _ponderHit and
                // prints nothing). The Wait also orders the flag visibility:
                // the suppress decision happens-before we clear _ponderHit.
                Stop();

                if (ponderMove.IsNone)
                {
                    // No pondered move was established (e.g. the opponent was
                    // mated/stalemated). Fall back to a normal search on the
                    // current position - it will report bestmove 0000.
                    _ponderHit = false;
                    if (_currentPosition != null && _ponderLimits != null)
                        StartSearch(new Position(_currentPosition), _ponderLimits, output, ponderMode: false);
                    return;
                }

                // Advance the ponder position by the expected opponent move and
                // continue with a normal, time-managed search for our reply.
                Position advanced;
                try
                {
                    advanced = new Position(_ponderPosition ?? _currentPosition!);
                    advanced.MakePublicMove(ponderMove);
                }
                catch
                {
                    // Should not happen (ponderMove is legal in that position),
                    // but never let a bad state hang the search.
                    _ponderHit = false;
                    if (_currentPosition != null && _ponderLimits != null)
                        StartSearch(new Position(_currentPosition), _ponderLimits, output, ponderMode: false);
                    return;
                }

                _ponderHit = false;
                StartSearch(advanced, _ponderLimits!, output, ponderMode: false);
            }
        }

        /// <summary>
        /// Launches a new search task on a fresh SearchSignal. Callers must
        /// hold <see cref="_searchLock"/>. The signal is replaced per search so
        /// an external Stop can never race a fresh search's start.
        /// </summary>
        private void StartSearch(Position position, SearchLimits limits, TextWriter output, bool ponderMode)
        {
            // CRITICAL FIX: Create a NEW SearchSignal for each search
            // Don't reuse _searchSignal because external Stop calls (e.g., from quit command)
            // can set the stop flag between Reset and when the search task starts
            _searchSignal = new SearchSignal();
            _searchTimer.Restart();
            _searchTask = Task.Run(() => RunSearch(position, limits, output, ponderMode));
        }

        public void Perft(int depth, TextWriter output)
        {
            if (_currentPosition == null)
            {
                output.WriteLine("info string No position set");
                return;
            }
            
            var perftPos = new Position(_currentPosition);
            var stopwatch = Stopwatch.StartNew();
            long nodes = perftPos.Perft(depth);
            stopwatch.Stop();
            
            double ms = stopwatch.Elapsed.TotalMilliseconds;
            double nps = ms > 0 ? nodes * 1000 / ms : 0;
            
            output.WriteLine($"Nodes: {nodes}");
            output.WriteLine($"Time: {ms:F0}ms");
            output.WriteLine($"NPS: {nps:F0}");
        }

        public void Bench(int depth, int threads, TextWriter output)
        {
            //: hand the `setoption` overrides to the benchmark so it measures
            // the configuration the engine would actually play. Without this, every
            // knob benchmarked as its compiled default and two different arms
            // returned identical node counts — a knob A/B then looks exactly like a
            // no-op. The map is snapshotted because later setoptions mutate it.
            // With no overrides set the list is empty and the run is bit-identical
            // to before, so numbers stay valid.
            var snapshot = new System.Collections.Generic.List<
                System.Collections.Generic.KeyValuePair<string, string>>(_searchOverrides);
            // The bench measures the ACTIVE evaluator (the same factory the
            // search path uses). With no overrides the run is
            // behaviour-identical to before the hook existed.
            PawnForge.Core.Tools.Benchmark.Run(depth, threads, output, ApplySearchOverrides, snapshot, CreateEvaluator);
        }

        public void SetOption(string name, string value, TextWriter output)
        {
            if (_options.TryGetOption(name, out var option))
            {
                if (option.Type == "button")
                {
                    _options.SetOption(name, "");
                }
                else if (!string.IsNullOrEmpty(value))
                {
                    _options.SetOption(name, value);
                }
                else
                {
                    output.WriteLine($"info string Option {name} requires a value");
                }
            }
            else if (_searchOptionProps.TryGetValue(name, out var prop))
            {
                // Hidden developer search option (not advertised in `uci`). Validate the value
                // against the property type, store the override, and it will be applied to every
                // per-thread Search created for subsequent `go` commands (see ApplySearchOverrides).
                if (prop.PropertyType == typeof(bool))
                {
                    if (!bool.TryParse(value, out _))
                    {
                        output.WriteLine($"info string search option {prop.Name} expects true/false");
                        return;
                    }
                }
                else if (!int.TryParse(value, out _))
                {
                    output.WriteLine($"info string search option {prop.Name} expects an integer");
                    return;
                }
                _searchOverrides[prop.Name] = value;
                output.WriteLine($"info string search option {prop.Name} = {value} (applies to next go)");
            }
            else
            {
                output.WriteLine($"info string Unknown option: {name}");
            }
        }

        private void RunSearch(Position position, SearchLimits limits, TextWriter output, bool ponderMode)
        {
            try
            {
                int threads = int.Parse(_options.GetValue("Threads"));
                if (threads < 1) threads = 1;
                
                // Ensure thread pool is initialized
                InitializeWorkerPool(threads);

                if (_workerThreadPool == null)
                    throw new InvalidOperationException("WorkerThreadPool failed to initialize");

                int moveOverhead = int.Parse(_options.GetValue("Move Overhead"));
                
                // Setup Search structures
                var tasks = new Task<Move>[threads];
                var searches = new SearchEngine[threads];  // Store search references for explicit Stop()
                var threadDatas = new ThreadData[threads];
                
                for (int i = 0; i < threads; i++)
                {
                    threadDatas[i] = new ThreadData(i);
                }
                
                // Aggregator for total nodes (across all threads)
                Func<long> nodeAggregator = () =>
                {
                    long total = 0;
                    foreach (var td in threadDatas)
                        total += td.Nodes;
                    return total;
                };
                
                for (int i = 0; i < threads; i++)
                {
                    int threadId = i;
                    // Clone position for this thread
                    var threadPos = new Position(position);
                    var threadData = threadDatas[i];
                    
                    // Queue work on worker pool
                    tasks[i] = _workerThreadPool.QueueWork(() => 
                    {
                        try
                        {
                            // Each thread gets its own evaluator via the
                            // evaluator factory (M7). The Search is
                            // evaluator-blind; any construction failure is a
                            // loud refusal, never a silent fallback.
                            var evaluator = CreateEvaluator(threadPos);

                            var search = new SearchEngine(TTable.Shared, threadData, _searchSignal, evaluator, nodeAggregator, s => output.WriteLine(s));
                            // Apply any hidden search-option overrides (setoption) to this
                            // per-thread instance. No-op unless a knob was explicitly set.
                            ApplySearchOverrides(search);
                            searches[threadId] = search;
                            if (threadId == 0) _ponderSearch = search;
                            search.PonderMode = ponderMode;
                            
                            // Configure Time Management (monotonic clock - see TimeManager.NowMs)
                            long currentTime = PawnForge.Core.Search.TimeManager.NowMs();
                            
                            if (ponderMode)
                            {
                                // Pondering is thinking on the OPPONENT's clock:
                                // no time budget applies - the search keeps
                                // going until `ponderhit` or `stop`.
                                search.TimeManager.SetInfinite();
                            }
                            else if (limits.Infinite)
                            {
                                search.TimeManager.SetInfinite();
                            }
                            else if (limits.MoveTime > 0)
                            {
                                search.TimeManager.SetFixedTime(limits.MoveTime, currentTime);
                            }
                            else if (limits.HasTimeControl)
                            {
                                search.TimeManager.Init(
                                    limits.WhiteTime,
                                    limits.BlackTime,
                                    limits.WhiteIncrement,
                                    limits.BlackIncrement,
                                    limits.MovesToGo,
                                    threadPos.SideToMove,
                                    currentTime,
                                    moveOverhead
                                );
                            }
                            else if (limits.IsPerft)
                            {
                                // Perft uses search engine but infinite time until depth complete
                                search.TimeManager.SetInfinite();
                            }
                            else if (limits.Depth == 0 && limits.Nodes == 0)
                            {
                                // Default go command -> use time management based on remaining moves/time
                                // But if no time info provided, default to infinite analysis or depth 6?
                                // UciHandler.ParseGoCommand defaults to depth 6 if nothing provided.
                                search.TimeManager.SetInfinite();
                            }
                            else
                            {
                                // Depth or Nodes limit -> infinite time, check limits in search loop
                                search.TimeManager.SetInfinite();
                            }

                            // `go nodes N` - enforced in the search's periodic stop polling
                            // (aggregated across all threads)
                            search.NodeLimit = limits.Nodes;

                            // `go mate N` - stop as soon as mate within N is proven
                            search.MateTarget = limits.Mate;

                            // `go searchmoves e2e4 ...` - restrict the root move loop
                            if (limits.SearchMoves.Count > 0)
                            {
                                var restriction = new HashSet<long>();
                                foreach (string uciMove in limits.SearchMoves)
                                {
                                    int f = UciParser.SquareFromString(uciMove[..2]);
                                    int t = UciParser.SquareFromString(uciMove.Substring(2, 2));
                                    if (f >= 0 && t >= 0)
                                        restriction.Add(f * 64L + t); // promotion suffix folded
                                }
                                search.RootMoveRestriction = restriction;
                            }
                            else
                            {
                                search.RootMoveRestriction = null;
                            }


                            // Ponder searches never run out of depth on their
                            // own (they terminate via stop/ponderhit), so give
                            // them a depth cap far beyond any real search.
                            int searchDepth = ponderMode ? 256 : (limits.Depth > 0 ? limits.Depth : 64);
                            return search.IterativeDeepening(threadPos, searchDepth);
                        }
                        catch (Exception innerEx)
                        {
                            if (threadId == 0) output.WriteLine($"info string Error in search thread {threadId}: {innerEx.Message}");
                            return Move.None;
                        }
                    });
                }
                
                // Wait for main thread (Task 0)
                try 
                {
                    tasks[0].Wait();
                }
                catch (Exception ex)
                {
                    output.WriteLine($"info string Error in main search thread: {ex.Message}");
                }

                // Stop helper threads
                if (threads > 1)
                {
                    _searchSignal.Stop();
                    for (int i = 1; i < threads; i++) searches[i]?.Stop();
                    
                    var shutdownTasks = new List<Task>();
                    for (int i = 1; i < threads; i++)
                    {
                        int idx = i;
                        // Avoid waiting for tasks[0] again, wait for helpers
                        shutdownTasks.Add(Task.Run(() => 
                        {
                            try { tasks[idx].Wait(200); } catch { } 
                        }));
                    }
                    Task.WaitAll(shutdownTasks.ToArray(), 250);
                }
                
                // P0 instrumentation snapshot: aggregate the per-thread counters
                // after the search has stopped. Cheap and only meaningful when
                // `searchstats reset` enabled counting for this search.
                lock (_searchStatsLock)
                {
                    var snapshot = new SearchStats();
                    foreach (var td in threadDatas)
                    {
                        snapshot.Add(td.Stats);
                        snapshot.Nodes += td.Nodes;
                    }
                    _lastSearchStats = snapshot;
                }

                var bestMove = tasks[0].Result;
                if (ponderMode)
                {
                    // Ponder mode volunteers no move until it is stopped: either a
                    // `ponderhit` already transitioned us to a fresh normal search
                    // that emits the real bestmove (nothing is printed here), or
                    // the GUI sent `stop` because the opponent deviated.
                    // Quirk Q5 fix: that `stop` is answered with the best move of
                    // the last completed iteration like any other stopped search
                    // (UCI: the engine must still send a bestmove after `stop`).
                    // It used to answer `bestmove 0000`, which a few GUIs read as
                    // an illegal move.
                    if (!_ponderHit)
                    {
                        output.WriteLine(bestMove.IsNone
                            ? "bestmove 0000"
                            : $"bestmove {UciParser.FormatMove(bestMove)}");
                    }
                }
                else if (bestMove.IsNone)
                    output.WriteLine("bestmove 0000");
                else
                    output.WriteLine($"bestmove {UciParser.FormatMove(bestMove)}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"info string Fatal error in search: {ex.Message}");
                output.WriteLine($"info string Stack trace: {ex.StackTrace}");
                output.WriteLine("bestmove 0000");
            }
        }
        public void Eval(TextWriter output)
        {
            if (_currentPosition == null)
            {
                output.WriteLine("info string No position set");
                return;
            }
            
            EnsureNnueLoaded();
            if (_nnueNet is null)
            {
                output.WriteLine($"info string NNUE NOT LOADED: {NnueLoadError}");
                return;
            }

            var eval = new NnueEvaluator(_nnueNet);
            eval.SetPosition(_currentPosition);
            output.WriteLine($"info string Evaluation: {eval.Evaluate()} cp (source: nnue net {NnueSourceName})");
        }

        public void Display(TextWriter output)
        {
            if (_currentPosition == null)
            {
                output.WriteLine("info string No position set");
                return;
            }
            
            output.WriteLine("info string");
            output.WriteLine($"info string FEN: {_currentPosition.ToFEN()}");
            output.WriteLine($"info string Zobrist: 0x{_currentPosition.ZobristKey:X16}");
        }

        /// <summary>
        /// Debug command (`checkmoves`): for the current position, prints every LEGAL move and
        /// the value of <see cref="Position.GivesCheck"/> for it, one per line:
        /// checkmove &lt;uci&gt; givescheck=&lt;true|false&gt;
        /// then a terminating `checkmoves done` line.
        ///
        /// Purpose: lets an external, authoritative oracle (python-chess `board.gives_check`)
        /// validate PawnForge's make-free GivesCheck predicate over thousands of REAL positions,
        /// with NO hand-authored FENs anywhere. See 
        /// </summary>
        public void CheckMoves(TextWriter output)
        {
            if (_currentPosition == null)
            {
                output.WriteLine("info string No position set");
                output.WriteLine("checkmoves done");
                return;
            }

            var pos = _currentPosition;
            int us = pos.SideToMove;
            Span<PawnForge.Core.Board.Move> buf = stackalloc PawnForge.Core.Board.Move[256];
            int n = pos.GeneratePseudoLegalMoves(buf);

            for (int i = 0; i < n; i++)
            {
                var m = buf[i];
                // Compute the predicate BEFORE making the move.
                bool gives = pos.GivesCheck(m);
                // Emit only LEGAL moves (own king not left in check), matching the set the
                // search/quiescence would ever consider.
                var captured = pos.MakePublicMove(m);
                bool legal = !pos.IsKingAttacked(us);
                pos.UnmakePublicMove(m, captured);
                if (legal)
                    output.WriteLine($"checkmove {m} givescheck={(gives ? "true" : "false")}");
            }
            output.WriteLine("checkmoves done");
        }
    }
}
