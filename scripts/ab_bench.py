#!/usr/bin/env python3
"""Balanced A/B speed comparison of two engine builds on one host.

A single back-to-back pair is biased: the process that runs first in a pair can
measure 2-4 % faster even when both binaries are byte-identical (boost state,
page cache, thermal state). This harness removes the bias by construction: each
round runs A B B A, so every engine is first once and last once, and the
reported change is the ratio of the mean NPS over all rounds. Run it with the
same binary twice as an A/A control to see the noise floor of the day before
believing a small gain.

Every run also checks the fixed-depth node pin, so a "faster" binary that
searched a different tree is rejected, not reported.

Usage:
    python3 scripts/ab_bench.py A B --depth 16 --rounds 4
    python3 scripts/ab_bench.py A A --depth 16 --rounds 4   # A/A control

Exit codes: 0 done, 1 a run failed, 2 a node count did not match the pin.
"""

import argparse
import os
import re
import statistics
import subprocess
import sys

# The four-position bench pins (single thread, compiled defaults).
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


def run_bench(engine: str, depth: int, timeout: int) -> tuple[int, int]:
    proc = subprocess.run(
        [engine],
        input=f"bench {depth}\nquit\n",
        capture_output=True,
        text=True,
        timeout=timeout,
        env=dict(os.environ),
    )
    nodes = TOTAL_NODES_RE.search(proc.stdout)
    nps = AVG_NPS_RE.search(proc.stdout)
    if nodes is None or nps is None:
        raise RuntimeError(
            f"bench produced no result (exit {proc.returncode}):\n"
            f"{proc.stdout[-1500:] or proc.stderr[-1500:]}"
        )
    return as_count(nodes.group(1)), as_count(nps.group(1))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("engine_a", help="baseline engine")
    parser.add_argument("engine_b", help="candidate engine")
    parser.add_argument("--depth", type=int, default=16, help="bench depth (default 16)")
    parser.add_argument("--rounds", type=int, default=4, help="ABBA rounds (default 4)")
    parser.add_argument("--timeout", type=int, default=900, help="seconds per run")
    args = parser.parse_args()

    pin = NODE_PINS.get(args.depth)
    samples = {"A": [], "B": []}
    engines = {"A": args.engine_a, "B": args.engine_b}

    # One discarded warm-up per engine (page cache, first-touch, tier-up).
    for name in ("A", "B"):
        run_bench(engines[name], args.depth, args.timeout)

    for round_index in range(args.rounds):
        for name in ("A", "B", "B", "A"):
            nodes, nps = run_bench(engines[name], args.depth, args.timeout)
            if pin is not None and nodes != pin:
                print(
                    f"node pin mismatch for {name}: {nodes} != {pin}",
                    file=sys.stderr,
                )
                return 2
            samples[name].append(nps)
        print(
            f"round {round_index + 1}: "
            f"A {statistics.mean(samples['A'][-2:]):,.0f}  "
            f"B {statistics.mean(samples['B'][-2:]):,.0f}"
        )

    mean_a = statistics.mean(samples["A"])
    mean_b = statistics.mean(samples["B"])
    spread_a = statistics.pstdev(samples["A"]) / mean_a * 100
    spread_b = statistics.pstdev(samples["B"]) / mean_b * 100
    print(f"A mean {mean_a:,.0f} NPS (sd {spread_a:.1f} %, {len(samples['A'])} runs)")
    print(f"B mean {mean_b:,.0f} NPS (sd {spread_b:.1f} %, {len(samples['B'])} runs)")
    print(f"B / A = {mean_b / mean_a:.4f}  ({(mean_b / mean_a - 1) * 100:+.2f} %)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
