#!/usr/bin/env python3
"""D4 manifest baseline check: additive-only evolution, enforced mechanically.

Compares the freshly exported manifest against the committed baseline. Additions are free;
anything a deployed client or integration could depend on must not silently change:

  - removed operation / view / form / grid          → BREAKING
  - removed field (input or view result)             → BREAKING
  - field type / wireKind change                     → BREAKING
  - optional field becoming required                 → BREAKING
  - new REQUIRED input field on an existing op       → BREAKING (needs baseline re-approval)
  - permission change on an existing op/view         → BREAKING

To approve an intentional break: re-export and commit the new baseline in the same PR
(`dotnet run --project samples/erp -- manifest samples/erp/manifest.baseline.json`).
"""
import json
import sys

def fields_by_name(fields):
    return {f["name"]: f for f in fields}

def diff_fields(path, baseline, current, breaks, new_required_is_break):
    base, cur = fields_by_name(baseline), fields_by_name(current)
    for name, bf in base.items():
        cf = cur.get(name)
        if cf is None:
            breaks.append(f"{path}: field '{name}' was removed")
            continue
        for prop in ("type", "wireKind"):
            if bf.get(prop) != cf.get(prop):
                breaks.append(f"{path}.{name}: {prop} changed "
                              f"'{bf.get(prop)}' → '{cf.get(prop)}'")
        if not bf.get("required") and cf.get("required"):
            breaks.append(f"{path}.{name}: optional field became required")
    if new_required_is_break:
        for name, cf in cur.items():
            if name not in base and cf.get("required"):
                breaks.append(f"{path}: new REQUIRED field '{name}' breaks existing callers")

def main(baseline_path, current_path):
    with open(baseline_path) as f: baseline = json.load(f)
    with open(current_path) as f: current = json.load(f)
    breaks = []

    for kind in ("operations", "views", "forms", "grids"):
        for key, bval in baseline.get(kind, {}).items():
            cval = current.get(kind, {}).get(key)
            if cval is None:
                breaks.append(f"{kind}.{key}: removed")
                continue
            if kind in ("operations", "views") and bval.get("permission") != cval.get("permission"):
                breaks.append(f"{kind}.{key}: permission changed "
                              f"'{bval.get('permission')}' → '{cval.get('permission')}'")
            if kind == "operations":
                diff_fields(f"{kind}.{key}", bval["fields"], cval["fields"],
                            breaks, new_required_is_break=True)
            if kind == "views":
                diff_fields(f"{kind}.{key}.result", bval["resultFields"], cval["resultFields"],
                            breaks, new_required_is_break=False)

    if breaks:
        print("MANIFEST BASELINE CHECK FAILED — non-additive changes:\n")
        for b in breaks:
            print(f"  ✗ {b}")
        print("\nIf intentional, re-export and commit the baseline (see script header).")
        return 1
    print("manifest baseline check passed (additive-only)")
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
