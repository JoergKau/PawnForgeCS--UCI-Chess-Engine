#!/usr/bin/env python3
"""Fixed-depth node-identity gate between two engine builds.

Any change that touches search, move ordering, evaluation or the accumulator
must keep the deterministic bench node totals EXACTLY equal when it is meant to
be neutral. This script runs `bench <depth>` on both binaries, parses the
four-position totals and compares them depth by depth.

Usage:
    python3 scripts/bench_identity_check.py --a old/PawnForge.UCI --b new/PawnForge.UCI
    python3 scripts/bench_identity_check.py --a A --b B --depths 6,12,16,18

Exit codes: 0 identical, 1 a run failed, 2 a node total differs.
"""

import argparse
import re
import subprocess
import sys

TOTAL_NODES_RE = re.compile(r"^Total Nodes:\s*([\d.,]+)\s*$", re.MULTILINE)


def as_count(text: str) -> int:
    return int(text.replace(".", "").replace(",", ""))


def bench(engine: str, depth: int, timeout: int) -> int:
    proc = subprocess.run(
        [engine],
        input=f"bench {depth}\nquit\n",
        capture_output=True,
        text=True,
        timeout=timeout,
    )
    match = TOTAL_NODES_RE.search(proc.stdout)
    if match is None:
        raise RuntimeError(
            f"bench {depth} on {engine} produced no total (exit {proc.returncode}):\n"
            f"{proc.stdout[-1200:] or proc.stderr[-1200:]}"
        )
    return as_count(match.group(1))


def parse_depths(text: str) -> list[int]:
    depths: list[int] = []
    for part in text.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            first, last = part.split("-", 1)
            depths.extend(range(int(first), int(last) + 1))
        else:
            depths.append(int(part))
    return sorted(set(depths))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--a", required=True, help="pre-change engine")
    parser.add_argument("--b", required=True, help="post-change engine")
    parser.add_argument(
        "--depths",
        default="6,12,16,18",
        help="comma list or range, e.g. 6,12,16,18 (default)",
    )
    parser.add_argument("--timeout", type=int, default=900, help="seconds per run")
    args = parser.parse_args()

    ok = True
    for depth in parse_depths(args.depths):
        try:
            nodes_a = bench(args.a, depth, args.timeout)
            nodes_b = bench(args.b, depth, args.timeout)
        except Exception as exc:  # noqa: BLE001 - report the run error verbatim
            print(f"bench {depth}: FAILED ({exc})", file=sys.stderr)
            return 1
        delta = nodes_b - nodes_a
        status = "OK" if delta == 0 else "CHANGED"
        print(f"bench {depth}: A {nodes_a:,}  B {nodes_b:,}  delta {delta:+,}  {status}")
        ok = ok and delta == 0

    if not ok:
        print("node identity FAILED: a search-neutral change moved the tree")
        return 2
    print("node identity OK: every depth matches exactly")
    return 0


if __name__ == "__main__":
    sys.exit(main())
