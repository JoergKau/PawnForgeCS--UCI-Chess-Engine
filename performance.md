# Performance

Example benchmark results for PawnForgeCS 2.0. They describe the single host
below and are not a cross-platform baseline: NPS depends on the CPU, clocks,
memory and background load, and must be measured on the machine that will run
the engine.

## Test machine

| Component | Value |
| :--- | :--- |
| CPU | AMD Ryzen 7 260 (Zen 4), 8 cores / 16 threads |
| Memory | 16 GB (15.3 GB usable) |
| Operating system | Windows 11, build 10.0.26300 |
| Runtime | .NET 11.0.100 |
| Build | Release, self-contained single-file, Server GC, dynamic PGO (`win-x64`) |

## Fixed-depth benchmark (1 thread)

The engine's `bench` command searches four positions at a fixed depth and prints
the node total and NPS. The node counts are the engine's identity pins and are
identical on every host. Depths 6 and 12 are dominated by process start-up and
JIT, so use 16 and 18 for throughput.

| Command | Nodes | NPS | Wall time |
| :--- | ---: | ---: | ---: |
| `bench 6 1` | 15,528 | 94,682 | 0.71 s |
| `bench 12 1` | 456,308 | 898,244 | 1.04 s |
| `bench 16 1` | 2,815,799 | 1,461,234 | 2.48 s |
| `bench 18 1` | 6,892,874 | 1,462,523 | 5.26 s |

## Thread scaling

Median of three runs per configuration; NPS is the engine's aggregate across all
search threads. At more than one thread the node totals differ between runs
(Lazy SMP), so only the 1-thread counts are fixed.

| Depth | Threads | NPS | Speed-up |
| ---: | ---: | ---: | ---: |
| 16 | 1 | 1,471,928 | 1.00x |
| 16 | 4 | 5,151,238 | 3.50x |
| 16 | 8 | 7,407,512 | 5.03x |
| 18 | 1 | 1,584,933 | 1.00x |
| 18 | 4 | 5,529,717 | 3.49x |
| 18 | 8 | 8,602,114 | 5.43x |

## Reproduce

Build the release binary and run the benchmark directly:

```bat
dotnet publish src\PawnForge.UCI\PawnForge.UCI.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:ServerGarbageCollection=true -p:TieredPGO=true -o publish\win-x64

(echo uci & echo bench 18 1 & echo quit) | publish\win-x64\PawnForge.UCI.exe
(echo uci & echo bench 18 4 & echo quit) | publish\win-x64\PawnForge.UCI.exe
(echo uci & echo bench 18 8 & echo quit) | publish\win-x64\PawnForge.UCI.exe
```

For a balanced A/B comparison between two builds on the same host, use
`scripts/ab_bench.py`; it runs the engines in `A B B A` order and checks the
node pin on every run.
