#!/usr/bin/env python3
"""File-structure guardrails (CLAUDE.md "Where code goes"), enforced mechanically.

Two rules, both aimed at the drift that produced the 395-line multi-aggregate Features.cs:

  1. LINE CAP: a C# file under samples/*/ or src/Tam.*/Packages/ stays under the split
     tripwire (~400 lines). Outgrowing it means: split by aggregate (samples) or give the
     package a folder (Packages/) — see CLAUDE.md.
  2. ONE WIRE PREFIX PER FILE: a file's [Operation]/[View] ids must share one first segment
     ("documents.folders.define" and "documents.list" agree on "documents") — a file serving
     two prefixes is mixing two aggregates' features.

Known, justified exceptions are ALLOWLISTED here explicitly — additions need a reason.
"""
import re
import sys
from pathlib import Path

LINE_CAP = 420  # the ~400 tripwire with headroom for doc comments

# path (repo-relative, posix) -> reason
ALLOWED_OVER_CAP = {
    # One package = one file until it outgrows a folder (CLAUDE.md); these are the current
    # largest packages, each still a single coherent area.
    "src/Tam.AspNetCore/Packages/Rules.cs": "single area; folder split pending real growth",
    "src/Tam.AspNetCore/Packages/Documents.cs": "single area; folder split pending real growth",
    "src/Tam.AspNetCore/Packages/Integrations.cs": "single area; folder split pending real growth",
    "src/Tam.AspNetCore/Packages/Tenancy.cs": "single area; folder split pending real growth",
    # One deliberate demo narrative — splitting the seed hides the story it tells.
    "samples/erp/Seed.cs": "the demo narrative reads top to bottom",
}

# path -> reason: files intentionally serving MORE than one wire prefix.
ALLOWED_MULTI_PREFIX = {
    # The vault is ONE package claiming two wire prefixes (settings + secrets) — a single
    # area, not two aggregates (docs/25).
    "src/Tam.AspNetCore/Packages/Vault.cs": "one vault area, two claimed prefixes",
}

WIRE_ID = re.compile(r'\[(?:Operation|View)\("([a-z0-9.-]+)"\)\]')

def prefix(wire_id: str) -> str:
    return wire_id.split(".")[0]

def main() -> int:
    root = Path(__file__).resolve().parent.parent
    failures = []
    files = [
        p for pattern in ("samples/*/**/*.cs", "src/Tam.*/Packages/**/*.cs")
        for p in root.glob(pattern)
        if not any(part in ("bin", "obj", "generated") for part in p.parts)
    ]
    for path in sorted(set(files)):
        rel = path.relative_to(root).as_posix()
        text = path.read_text(encoding="utf-8")
        lines = text.count("\n") + 1
        if lines > LINE_CAP and rel not in ALLOWED_OVER_CAP:
            failures.append(
                f"{rel}: {lines} lines (> {LINE_CAP}) — split by aggregate / give the "
                "package a folder (CLAUDE.md), or allowlist with a reason")
        prefixes = {prefix(m) for m in WIRE_ID.findall(text)}
        if len(prefixes) > 1 and rel not in ALLOWED_MULTI_PREFIX:
            failures.append(
                f"{rel}: declares operations/views under multiple wire prefixes "
                f"({', '.join(sorted(prefixes))}) — one aggregate per file")
    for rel in list(ALLOWED_OVER_CAP) + list(ALLOWED_MULTI_PREFIX):
        if not (root / rel).exists():
            failures.append(f"allowlist entry '{rel}' no longer exists — remove it")
    if failures:
        print("structure check FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print(f"structure check OK ({len(files)} files)")
    return 0

if __name__ == "__main__":
    sys.exit(main())
