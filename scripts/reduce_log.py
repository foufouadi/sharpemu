#!/usr/bin/env python3
"""Collapse a SharpEmu log down to what is actually distinct.

An emulator log is dominated by lines that repeat with only an address or a
counter changing. This normalises each line to a signature -- timestamps,
hex values and numbers replaced by placeholders -- then keeps only the first
and last few occurrences of each signature, in place, with a marker naming
how many were dropped. Chronology is preserved, so the reduced file can be
read as a log rather than as a report.

Two passes over the file; memory holds one entry per distinct signature,
not per line.

    python scripts/reduce_log.py run.log -o out/ --head 3 --tail 3
"""

from __future__ import annotations

import argparse
import os
import re
import sys

TIMESTAMP = re.compile(r"^\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s*")
GUEST_CLOCK = re.compile(r"\bt=\d+\.\d+\s*")
HEX = re.compile(r"0x[0-9A-Fa-f]+")
DECIMAL = re.compile(r"(?<![A-Za-z0-9_])\d+(?:\.\d+)?")


def signature(line: str) -> str:
    line = TIMESTAMP.sub("", line.rstrip("\n"))
    line = GUEST_CLOCK.sub("", line)
    line = HEX.sub("0xX", line)
    return DECIMAL.sub("N", line)


def count_signatures(path: str) -> dict[str, int]:
    counts: dict[str, int] = {}
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            key = signature(line)
            counts[key] = counts.get(key, 0) + 1
    return counts


def write_reduced(path: str, out_path: str, counts: dict[str, int],
                  head: int, tail: int) -> tuple[int, int]:
    seen: dict[str, int] = {}
    written = 0
    total = 0
    with open(path, "r", encoding="utf-8", errors="replace") as handle, \
            open(out_path, "w", encoding="utf-8", newline="\n") as out:
        for line in handle:
            total += 1
            key = signature(line)
            count = counts[key]
            index = seen.get(key, 0)
            seen[key] = index + 1

            if count <= head + tail or index < head:
                out.write(line)
                written += 1
                continue

            if index == head:
                dropped = count - head - tail
                out.write(f"    ... {dropped} more like the line above "
                          f"({count} total) ...\n")
                written += 1

            if index >= count - tail:
                out.write(line)
                written += 1
    return total, written


def write_census(out_path: str, counts: dict[str, int]) -> None:
    with open(out_path, "w", encoding="utf-8", newline="\n") as out:
        out.write("count\tsignature\n")
        for key, count in sorted(counts.items(), key=lambda kv: -kv[1]):
            out.write(f"{count}\t{key}\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("log", help="log file to reduce")
    parser.add_argument("-o", "--out-dir", default=".",
                        help="directory for reduced.log and census.tsv")
    parser.add_argument("--head", type=int, default=3,
                        help="occurrences kept at the start of each group")
    parser.add_argument("--tail", type=int, default=3,
                        help="occurrences kept at the end of each group")
    args = parser.parse_args()

    if not os.path.isfile(args.log):
        print(f"no such file: {args.log}", file=sys.stderr)
        return 1

    os.makedirs(args.out_dir, exist_ok=True)
    reduced_path = os.path.join(args.out_dir, "reduced.log")
    census_path = os.path.join(args.out_dir, "census.tsv")

    counts = count_signatures(args.log)
    total, written = write_reduced(args.log, reduced_path, counts,
                                   args.head, args.tail)
    write_census(census_path, counts)

    source_mb = os.path.getsize(args.log) / (1024 * 1024)
    reduced_mb = os.path.getsize(reduced_path) / (1024 * 1024)
    print(f"lines      {total:>12,} -> {written:>12,}"
          f"  ({written / max(total, 1):.2%})")
    print(f"megabytes  {source_mb:>12,.1f} -> {reduced_mb:>12,.1f}")
    print(f"distinct   {len(counts):>12,} signatures")
    print(f"wrote      {reduced_path}")
    print(f"wrote      {census_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
