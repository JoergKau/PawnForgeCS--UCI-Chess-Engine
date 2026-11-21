#!/usr/bin/env python3
"""Measure the dynamic-PGO effect of the shipped build on this host.

The release build enables the runtime's dynamic profile-guided optimization
(TieredPGO). This script runs the SAME binary twice -- once with TieredPGO on,
once with it off -- at a fixed bench depth, checks the node identity pin on
every run, and reports the median NPS ratio. It is the C# analogue of the
profile-use build: the profile is collected and consumed by the runtime on the
machine, so it must be measured there, never copied between hosts.

Usage:
    python3 scripts/pgo_verify.py --engine publish/osx-arm64/PawnForge.UCI
    python3 scripts/pgo_verify.py --engine ./engine --depth 18 --runs 5
    python3 scripts/pgo_verify.py --engine ./engine --require-gain

Exit codes:
    0  measurement completed and every node pin matched
    1  an engine run failed or printed no result
    2  a node pin moved (the search changed; this is not a PGO result)
    3  --require-gain was set and the PGO run was not faster
"""

import argparse
import os
import re
import statistics
import subprocess
import sys

# The four-position bench pins (single thread, compiled defaults). Same values
# as tools/bench_identity_check.py and the README.
NODE_PINS = {
    6: 15_528,
    12: 456_308,
    16: 2_815_799,
    18: 6_892_874,
}

TOTAL_NODES_RE = re.compile(r"^Total Nodes:\s*([\d.,]+)\s*$", re.MULTILINE)
AVG_NPS_RE = re.compile(r"^Average NPS:\s*([\d.,]+)\s*$", re.MULTILINE)


def as_count(text: str) -> int:
    return int(text.replace(".", "").replace(",", ""))


def run_bench(engine: str, depth: int, tiered_pgo: bool, timeout: int) -> tuple[int, int]:
    env = dict(os.environ)
    env["DOTNET_TieredPGO"] = "1" if tiered_pgo else "0"
    proc = subprocess.run(
        [engine],
        input=f"bench {depth}\nquit\n",
        capture_output=True,
        text=True,
        timeout=timeout,
        env=env,
    )
    nodes = TOTAL_NODES_RE.search(proc.stdout)
    nps = AVG_NPS_RE.search(proc.stdout)
    if nodes is None or nps is None:
        raise RuntimeError(
            f"bench produced no result (exit {proc.returncode}):\n"
            f"{proc.stdout[-1500:] or proc.stderr[-1500:]}"
        )
    return as_count(nodes.group(1)), as_count(nps.group(1))


def measure(engine: str, depth: int, runs: int, tiered_pgo: bool, timeout: int) -> list[int]:
    expected = NODE_PINS.get(depth)
    values: list[int] = []
    for index in range(runs):
        nodes, nps = run_bench(engine, depth, tiered_pgo, timeout)
        if expected is not None and nodes != expected:
            raise SystemExit(
                f"node pin mismatch with TieredPGO={int(tiered_pgo)}: {nodes} != {expected}"
            )
        values.append(nps)
        print(
            f"  TieredPGO={int(tiered_pgo)} run {index + 1}/{runs}: "
            f"nodes {nodes:,}  nps {nps:,}"
        )
    return values


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--engine", required=True, help="path to the built engine")
    parser.add_argument("--depth", type=int, default=18, help="bench depth (default 18)")
    parser.add_argument("--runs", type=int, default=5, help="runs per mode (default 5)")
    parser.add_argument("--timeout", type=int, default=900, help="seconds per run")
    parser.add_argument(
        "--require-gain",
        action="store_true",
        help="exit 3 unless the PGO run is faster than the non-PGO run",
    )
    args = parser.parse_args()

    if not os.path.isfile(args.engine):
        print(f"engine not found: {args.engine}", file=sys.stderr)
        return 1

    # One discarded warm-up per mode (page cache, first-touch).
    run_bench(args.engine, args.depth, True, args.timeout)
    run_bench(args.engine, args.depth, False, args.timeout)

    print("== TieredPGO on (shipped build)")
    on = measure(args.engine, args.depth, args.runs, True, args.timeout)
    print("== TieredPGO off (control)")
    off = measure(args.engine, args.depth, args.runs, False, args.timeout)

    median_on = statistics.median(on)
    median_off = statistics.median(off)
    ratio = median_on / median_off
    print()
    print(f"PGO on:  {median_on:,.0f} NPS (median of {len(on)})")
    print(f"PGO off: {median_off:,.0f} NPS (median of {len(off)})")
    print(f"PGO effect: {(ratio - 1) * 100:+.2f} % at bench {args.depth}")
    print("node pins: OK")

    if args.require_gain and ratio <= 1.0:
        print("error: the PGO build is not faster on this host", file=sys.stderr)
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
