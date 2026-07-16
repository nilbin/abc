#!/usr/bin/env python3
"""Docs-site coverage gate: every doc page must be reachable and indexed.

Fails the docs build when a page under docs/ is missing from the mkdocs nav,
or when a top-level design doc is missing from docs/llms.txt (the machine-
readable index promises completeness — a silently unindexed page breaks it).
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"

# Pages that are deliberately outside the site nav.
NAV_EXEMPT = {
    "20-tutorial.md",  # compatibility pointer for pre-split inbound links
    "llms.txt",
    "status.md",       # generated from STATUS.md by the workflow, already in nav
}
# llms.txt lists every top-level design doc by file name; tutorial steps are
# covered by the tutorial index entry, and these pages by their nav role.
LLMS_EXEMPT = {"index.md", "20-tutorial.md", "status.md"}

nav_text = (ROOT / "mkdocs.yml").read_text(encoding="utf-8")
llms_text = (DOCS / "llms.txt").read_text(encoding="utf-8")

problems: list[str] = []

for path in sorted(DOCS.rglob("*.md")):
    rel = path.relative_to(DOCS).as_posix()
    if rel in NAV_EXEMPT:
        continue
    if not re.search(rf"(?m)^\s*(?:- |.*: ){re.escape(rel)}\s*$", nav_text):
        problems.append(f"{rel}: not in mkdocs.yml nav (add it, or list it in NAV_EXEMPT with a reason)")
    if "/" not in rel and rel not in LLMS_EXEMPT and path.name not in llms_text:
        problems.append(f"{rel}: not mentioned in docs/llms.txt (add an entry with a one-line description)")

if problems:
    print("docs coverage check FAILED:")
    for p in problems:
        print(f"  - {p}")
    sys.exit(1)
print("docs coverage check OK")
