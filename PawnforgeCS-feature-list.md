# PawnForgeCS — Feature List

PawnForgeCS 2.0 is a C# (.NET 11) UCI chess engine with a fully self-contained
neural evaluation, a modern alpha-beta search, and Syzygy tablebase support.

## Engine

- **Language / runtime**: C# 15, .NET 11, single self-contained binary per platform.
- **Search**: iterative deepening with aspiration windows, principal variation
  search, transposition table, and Lazy SMP multi-threading.
- **Evaluation**: fully self-designed embedded neural network
  (`jokausch.nnue`) — see the network and training sections below.
  `EvalFile` can override the embedded net.
- **Threads**: shared lockless transposition table; per-thread histories,
  killers and evaluator state.

## Neural network (fully self-designed)

- **Architecture** — the v5 net format, feature scheme 2: 768 input slots
  (2 colours × 6 piece types × 64 squares), bucketed into 8 king regions with a
  mirrored per-perspective mapping (`half_ka_v2`), L1 = 512 (6,144 inputs =
  8 regions × 768 slots), and one global output head over the concatenated
  2 × 512 SCReLU activations. **3,147,265 parameters** in a 6,294,672-byte
  file.
- **Self-designed and self-trained** — the v5 architecture, feature mapping,
  quantized file format and training pipeline are defined in this project; the
  shipped weights are trained entirely from this project's own data (no
  external net and no fine-tune of a foreign network).
- **Shipped net** — `jokausch.nnue`, description header
  `v5_poolv9_512_auxwdl-150`; checksum verified, quantization constants pinned
  per build (QA = 255, SCReLU shift 8, output shift 0, eval scale 1600 / 2²⁰).
- **Runtime** — fully incremental accumulator with lazy own-king refresh and
  **one accumulator pair per ply** (a make writes the child pair directly, an
  unmake steps back, and the copy plus the dirty-row updates are fused into one
  load/store pass); cold-start and incremental evaluations are bit-identical;
  hardware-intrinsic forward pass (x64 AVX2, ARM64 AdvSimd) with a portable
  vector fallback and a scalar reference path, all bit-identical; the
  accumulator kernels also have a `Vector512<T>` arm for AVX-512 hosts, and
  the tail lanes are computed so the result does not depend on the host's
  vector width.
- **Structure report** — verified in strict mode with the net-report tool:
  checksum OK, 88.02 % weight fill with no genuinely dead input rows (every
  dead row is dead by construction), distinct output halves (r = −0.653), and
  head weights exercising the full quantized range.

## Training workflow

- **Data** — positions are harvested from selected public Lichess game
  archives, validated, and then **re-scored with Stockfish** to obtain teacher
  centipawn labels before being packed into a compact binary record format.
  The shipped net's pool of record holds **406,449,912 positions (~12.1 GiB)**,
  including curated endgame shards. The data pipeline and all labels are
  generated in-house end to end.
- **Trainer** — the complete **bullet** training workflow (Rust, CUDA) is
  implemented end to end, with a fully implemented custom network example
  defining the v5 architecture, feature scheme, king-region buckets,
  quantization and export. A full run trains on a single laptop GPU (RTX 5060)
  in roughly 3–5 hours at 4.4–6.5 M positions/s, using batch 16,384, a
  150-epoch cosine schedule, and a linear score loss against pure Stockfish
  labels (no game-result blending).
- **Promotion gates** — in-training material-ordering gate, region audit with
  negative controls, teacher-correlation and side-to-move-flip checks,
  bit-exactness tests, and a 600-game fastchess match as the only strength
  arbiter. The current champion is `v5_poolv9_512_auxwdl-150` (its auxiliary
  WDL head is trainer-only and is not exported).

## Search techniques

- Transposition table: lockless XOR-verified 16-byte entries, bucket-aligned to
  one cache line, static-eval caching, PV-flag propagation, cache prefetching.
- Move ordering: TT move, staged move picker, captures with SEE, killers,
  countermoves, butterfly/continuation history, gravity-bounded history updates,
  quiet-ordering dynamic pre-bonus.
- Pruning and reductions:
  - Reverse futility pruning (per-depth margins, optional linear formula).
  - Razoring (per-depth margins, not-improving easing).
  - Null-move pruning with verified-null mate clamp and optional TT seal.
  - Late move pruning (per-depth limits, improving-aware variants).
  - History pruning and SEE-based move-loop pruning.
  - Futility pruning in the move loop (per-depth clamped step cells).
  - Late move reductions: v1 table schedule and an optional fixed-point v2
    architecture with ttPv / cut-node / not-improving / PV-distance /
    history-divisor terms.
  - ProbCut (main and secondary layers), singular extensions, check extensions,
    per-path extension cap, TT-score extension, negative extensions.
  - Quiescence search: check-aware generation, TT probe, delta pruning,
    recapture-only narrowing, promotion cutoff, capture SEE ordering.
- Selectivity controls: twofold repetition detection, contempt score,
  selective-singular verification, eval cache.

## Endgame tablebases

- Syzygy WDL + DTZ probing (3-4-5 piece, up to 7-man capable), material-gated
  and depth-unconditional in search and quiescence.
- Optional root probe (`SyzygyRootProbe`) for direct DTZ-optimal root play.
- 50-move-rule–aware WDL scoring and DTZ root ranking.

## Opening book

- Own-book support with `.ctg` (ChessBase) reading, configurable file and depth.
  `Own Book` defaults OFF (tournament rule friendly).

## UCI interface

- UCI handshake plus `isready`, `ucinewgame`, `position`, `go`, `stop`, `ponderhit`,
  `setoption`, `quit`.
- The engine always holds a position: it starts from the start position, and
  `ucinewgame` resets it to the start position.
- A `position` command that is rejected (invalid FEN or an illegal move) is
  reported with an `info string` error line and leaves the previous position in
  place. The engine does not exit and does not search a position the GUI did not
  set.
- `go` supports depth, movetime, wtime/btime/winc/binc, movestogo, nodes,
  mate, infinite, ponder.
- `bench` command for deterministic node-count benchmarking.
- **Public options (12)**:
  `Hash`, `Threads`, `Move Overhead`, `Clear Hash`, `Own Book`, `Book File`,
  `Book Depth`, `SyzygyPath`, `SyzygyRootProbe`, `Syzygy50MoveRule`,
  `EvalFile`, `Contempt`.
- Research/tuning levers are registered as hidden options: fully settable
  through `setoption name <Name> value <Value>` or the `param:Name=Value`
  argv override, but not listed in the UCI handshake.

## Portability and quality

- Windows 11 (x64) is the primary platform and the intended CCRL build; macOS
  (Apple Silicon) and Linux (x64) are built and tested from the same source as
  single-file builds. There are no per-ISA or per-host builds: the .NET JIT
  selects the instruction set at run time and dynamic PGO profiles the host.
- **Release build = dynamic PGO on every platform**: `TieredPGO` + Server GC +
  immediate call counting. Built and measured on Windows 11 x64, macOS arm64
  and Linux x64; the PGO build was faster than the same binary with
  `DOTNET_TieredPGO=0` on each, with the node pins unchanged. NPS and the PGO
  effect are host properties; run `scripts/pgo_verify.py` on the host that will
  run the engine. NativeAOT and composite ReadyToRun measured slower at steady
  state, so both remain optional comparison builds, not the release default.
- Forced-reply counting without building the move list: the search counts
  knight, slider and king targets directly and bails at the second move; the
  result is identical to the full generator at unchanged node counts.
- Fused per-ply NNUE accumulator: no copy on push/pop, one load/store pass per
  make, fused full refresh; bit-identical to the cold-start accumulator.
- Allocation-free capture ordering (stack SEE scores, one static piece-value
  table, one-time syzygy trace flag).
- Hot NNUE loops use `Vector.LoadUnsafe`/`StoreUnsafe` over `ref` arithmetic
  (accumulator row add/subtract and both intrinsic heads), replacing per-chunk
  `Span` construction and bounds checks: **bit-identical** (same node counts).
- Deterministic fixed-depth node counts across all of the above:
  `bench 6` **15,528**, `bench 12` **456,308**, `bench 16` **2,815,799**,
  `bench 18` **6,892,874**.
- xUnit test suite (780 tests): perft, move generation, search behaviour,
  NNUE loader/accumulator/evaluator, vector-width parity, tablebases, UCI
  plumbing, book parsing.
- Zero build errors and zero warnings in Release.

## License

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
