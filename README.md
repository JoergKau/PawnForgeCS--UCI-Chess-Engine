# PawnForgeCS

PawnForgeCS is a UCI chess engine written in C# (.NET 11). It combines a modern
alpha-beta search with a fully embedded, self-designed and self-trained neural
network evaluation, Syzygy tablebase probing, and multi-threaded Lazy SMP search.

Windows 11 x64 is the primary platform and the intended CCRL build. macOS arm64
and Linux x64 are supported from the same source and use the same PGO release
path.

A detailed capability overview is in
[`PawnforgeCS-feature-list.md`](PawnforgeCS-feature-list.md).

## Neural network

The evaluation is a fully self-designed, self-trained NNUE in the v5 net format:
768 input slots (2 colours × 6 piece types × 64 squares) bucketed into 8 king
regions with a mirrored per-perspective mapping, L1 = 512, and a single output
head over the concatenated 2 × 512 SCReLU activations — 3,147,265 parameters in
a 6.3 MB embedded file (`jokausch.nnue`, description `v5_poolv9_512_auxwdl-150`).
Updates are fully incremental with a lazy own-king refresh; cold-start and
incremental evaluations are bit-identical. A strict structure report on the
shipped net passes: checksum OK, no genuinely dead input rows, and the two
output halves are distinct.

## Training

Positions are harvested from selected public Lichess game archives and
**re-scored with Stockfish** to produce teacher centipawn labels; the pool of
record contains 406,449,912 positions (~12.1 GiB), including curated endgame
shards. The complete training workflow is implemented on top of the **bullet**
Rust/CUDA trainer with a custom network example covering architecture, buckets,
quantization and export. A full run takes roughly 3–5 hours on a single laptop
GPU (150 epochs, batch 16,384, linear loss against pure Stockfish labels),
followed by material-ordering, region-audit, teacher-correlation, side-to-move
flip, bit-exactness and 600-game match gates before promotion.

### Training toolchain (Windows 11, CUDA)

The primary trainer is [bullet](https://github.com/jw1912/bullet), built from
source at the pinned commit `c004ebf04025edf91c6ccac47e72d561062a46b5`
(bullet is MIT licensed and is not linked into the engine). The Windows/CUDA
build needs, in this order:

1. **Visual Studio 2022 Build Tools** with the *Desktop development with C++*
   workload (`cl.exe`). Build from the **x64 Native Tools Command Prompt for
   VS 2022**; `nvcc` needs MSVC on the path.
2. **Rust stable** (the training host used 1.100.0; bullet requires 1.87 or
   newer).
3. **NVIDIA driver + CUDA Toolkit 12.8 or newer** — the RTX 5060 is Blackwell
   (`sm_120`) — with `CUDA_PATH` pointing at the toolkit directory (CUDA 13.4
   on the training host).

Clone and pin the trainer, then build the utilities and the custom example:

```bat
git clone https://github.com/jw1912/bullet.git
cd bullet
git rev-parse HEAD   :: c004ebf04025edf91c6ccac47e72d561062a46b5

cargo build --release --package bullet-utils
cargo build --release --example mcnn_v5 --features cuda
```

The trainer is `target\release\examples\mcnn_v5.exe`. The `mcnn_v5` example is
PawnForgeCS's network overlay on top of the pinned checkout, so the upstream
tree does not contain it; the overlay lives with the training workspace.
`--features cuda` is a correctness requirement, not a speed switch: without it
bullet builds a mock runtime that compiles and passes `--help` but fails at the
first gradient step.

## Requirements

- Windows 11 x64 is the primary build and test platform (the CCRL candidate).
- [.NET 11 SDK](https://dotnet.microsoft.com/download) (the exact version is
  pinned in `global.json`).
- On x64, the .NET 11 runtime raises the CPU floor to **x86-64-v2** (SSE4.2 +
  POPCNT), i.e. any x86-64 CPU from roughly 2009 onward. ARM builds need ARMv8
  (Apple Silicon, Raspberry Pi 4 class).
- Optional: a Syzygy tablebase directory for endgame probing.
- Optional: a Stockfish binary for the external-oracle test gates (the suite
  runs without it; those checks are skipped).

## Build

Windows 11 x64:

```bat
dotnet build PawnForgeCS.slnx -c Release
```

The engine binary is
`src\PawnForge.UCI\bin\Release\net11.0\PawnForge.UCI.exe` on Windows. On
macOS and Linux the same path uses forward slashes and has no `.exe`.

Self-contained single-file builds for the three supported platforms:

```bat
dotnet publish src\PawnForge.UCI\PawnForge.UCI.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:ServerGarbageCollection=true -p:TieredPGO=true -o publish\win-x64
```

```bash
dotnet publish src/PawnForge.UCI/PawnForge.UCI.csproj -c Release \
  -r osx-arm64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:ServerGarbageCollection=true -p:TieredPGO=true -o publish/osx-arm64

dotnet publish src/PawnForge.UCI/PawnForge.UCI.csproj -c Release \
  -r linux-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:ServerGarbageCollection=true -p:TieredPGO=true -o publish/linux-x64
```

Server GC and dynamic PGO are also set in `PawnForge.UCI.csproj`, so the
`-p:ServerGarbageCollection` and `-p:TieredPGO` arguments above repeat the
project settings and are a no-op; they are shown explicitly so the build
command is self-describing.

The release wrapper builds the same artifacts and zips them. It runs in a bash
shell (Git Bash or WSL on Windows); `MODE=pgo` is the default:

```bash
MODE=pgo scripts/package-release.sh win-x64
MODE=pgo scripts/package-release.sh osx-arm64
MODE=pgo scripts/package-release.sh linux-x64
```

### Release build: dynamic PGO on all three platforms

The release artifact is the runtime's **dynamic PGO** build on every platform:
`TieredPGO=true`, Server GC, and the call-counting delay set to zero in the
runtimeconfig. The profile is collected and consumed on the machine that runs
the engine, so it is measured there and never copied between hosts.

The same mechanism was built and measured on all three supported platforms —
Windows 11 x64, macOS arm64 and Linux x64 — and the PGO build was faster than
the same binary with `DOTNET_TieredPGO=0` on each of them, with the node pins
unchanged. No platform is a stand-in for another: NPS and the PGO effect are
host properties. `scripts/pgo_verify.py` prints the number for the host it runs
on.

The comparison builds (NativeAOT and composite ReadyToRun) measured slower than
PGO at steady state, so neither is the release default. `MODE=r2r` and
`MODE=aot` in the release script build them only for comparison. The `dotnet
publish` command above produces the Windows PGO build.

Run the PGO check on each platform against the binary built there:

**Windows 11 x64**

```bat
python scripts\pgo_verify.py --engine publish\win-x64\PawnForge.UCI.exe --depth 18
```

**macOS arm64**

```bash
python3 scripts/pgo_verify.py --engine publish/osx-arm64/PawnForge.UCI --depth 18
```

**Linux x64**

```bash
python3 scripts/pgo_verify.py --engine publish/linux-x64/PawnForge.UCI --depth 18
```

`scripts/pgo_verify.py` runs the same binary once with `DOTNET_TieredPGO=1` and
once with `=0`, checks the fixed-depth node pin on every run, and reports the
median NPS ratio. Add `--runs 5` for a longer measurement and `--require-gain`
to fail when PGO is not faster on the host.

Example benchmark results for one Windows 11 / Zen 4 host are in
[`performance.md`](performance.md).

## Run

On Windows 11 x64:

```bat
src\PawnForge.UCI\bin\Release\net11.0\PawnForge.UCI.exe
```

or the self-contained release binary at `publish\win-x64\PawnForge.UCI.exe`.

On macOS or Linux, run
`./src/PawnForge.UCI/bin/Release/net11.0/PawnForge.UCI`.

The engine speaks UCI on stdin/stdout and can be used with any UCI-compatible
GUI or match runner (Cute Chess, Arena, BanksiaGUI, fastchess, …).

A quick handshake check on Windows:

```bat
(echo uci & echo isready & echo quit) | src\PawnForge.UCI\bin\Release\net11.0\PawnForge.UCI.exe
```

On macOS or Linux use
`printf 'uci\nisready\nquit\n' | ./src/PawnForge.UCI/bin/Release/net11.0/PawnForge.UCI`.

## UCI options

The engine advertises twelve user-facing options:

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `Hash` | spin | 256 | Transposition table size in MB |
| `Threads` | spin | 1 | Search threads |
| `Move Overhead` | spin | 50 | Safety margin in ms for GUI/match lag |
| `Clear Hash` | button | — | Clears the transposition table |
| `Own Book` | check | false | Enables the opening book |
| `Book File` | string | — | Path to a `.ctg` book |
| `Book Depth` | spin | 100 | Maximum book ply |
| `SyzygyPath` | string | — | Tablebase directory (or `;`/`:`-separated list) |
| `SyzygyRootProbe` | check | false | Direct DTZ-optimal root probing |
| `Syzygy50MoveRule` | check | true | Honour the 50-move rule in tablebase scores |
| `EvalFile` | string | — | Load an alternative `.nnue` network |
| `Contempt` | spin | 0 | Draw-score bias in centipawns |

### Hidden research levers

The search exposes many additional tuning parameters as *hidden* options. They
are fully functional via `setoption name <Name> value <Value>` and via the
`param:Name=Value` command-line override, but they are deliberately absent from
the `uci` handshake so GUI option dialogs stay focused on the twelve options
above. Match runners such as fastchess only forward `option.*` arguments for
names they see in the handshake — use `param:` (passed verbatim to the engine)
or scripted stdin to deliver a hidden lever in automated matches.

## Tournament use

The shipped defaults are tournament-friendly:

- The **Windows x64** release zip is the build intended for CCRL and Windows
  match runners.
- `Own Book` defaults to **OFF**; no position learning is performed. A tester's
  own opening book and ponder settings can be used unchanged.
- Suggested settings: `Hash` 128–256 MB, `Threads` 1. Optional `SyzygyPath`
  enables 3-4-5 piece tablebases; `SyzygyRootProbe` stays off by default.
- `scripts/package-release.sh` builds the release zips
  (`publish/PawnForgeCS-<version>-<rid>.zip`), each containing the engine
  executable, README, feature list, license and third-party notices. The
  default `MODE=pgo` is the measured-fastest build on every host tested; see
  the release-build section above.

## Testing

```bat
dotnet test PawnForgeCS.slnx -c Release
```

The suite contains 779 tests covering perft/move generation, search behaviour,
the NNUE loader/accumulator/evaluator, tablebases, the opening-book reader, and
the UCI plumbing. Fixtures that are validated against an external Stockfish
oracle run when a `stockfish` binary is on `PATH` or `PAWNFORGE_STOCKFISH`
points at one; otherwise those checks no-op.

The deterministic benchmark identity check on Windows:

```bat
(echo uci & echo bench 16 1 1 & echo quit) | src\PawnForge.UCI\bin\Release\net11.0\PawnForge.UCI.exe
```

On macOS or Linux use
`printf 'uci\nbench 16 1 1\nquit\n' | ./src/PawnForge.UCI/bin/Release/net11.0/PawnForge.UCI`.

A two-binary gate and a balanced speed comparison are included (Windows paths
shown; use `python3` and `/` on macOS/Linux):

```bat
:: Any search-neutral change: both binaries must print identical totals.
python scripts\bench_identity_check.py --a old\PawnForge.UCI.exe --b new\PawnForge.UCI.exe

:: Speed: A B B A rounds, node pin checked on every run (use A A as a control).
python scripts\ab_bench.py old\PawnForge.UCI.exe new\PawnForge.UCI.exe --depth 16 --rounds 4

:: The dynamic-PGO effect on this host.
python scripts\pgo_verify.py --engine new\PawnForge.UCI.exe --depth 18
```

The depth-16 bench node count for the shipped binary is **2,815,799**
(`bench 6` **15,528**, `bench 12` **456,308**, `bench 18` **6,892,874** — the
fixed-depth identity gate used by `scripts/pgo_verify.py` and the release
scripts). These are the pins of the bug-fixed reference build; a search change
that moves any of them is a different engine and must be reviewed as such.

## License

MIT — see [`LICENSE`](LICENSE). Third-party attributions and license texts are
in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
