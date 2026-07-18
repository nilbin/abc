#!/usr/bin/env bash
# One command to regenerate EVERY committed build artifact from the compiled model, so a model
# change is never left with a stale baseline, contract, slice or client. CI only VERIFIES these
# are current (re-export + byte-compare) — it never writes them, because they are build INPUTS
# (plugin projects reference the slices as AdditionalFiles, like a lockfile). Run this before
# committing a model change, then `git add -A`.
#
#   scripts/regen.sh
#
# Plugin contract slices are auto-discovered from the committed samples/*/*.contract.json files,
# so adding a new dependency parent needs no edit here or in CI — drop its slice in and it is
# covered. (The host contract is samples/erp/host-contract.json — the "-contract.json" suffix
# keeps it out of the "*.contract.json" slice glob.)
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"          # `dotnet run --project` uses the project dir as cwd — outputs need ABSOLUTE paths.
ERP=samples/erp

dotnet build "$ERP/Erp.csproj" -v q --nologo

# The D4 manifest baseline — also the source the TS client generates from.
dotnet run --project "$ERP" --no-build -- manifest "$ROOT/$ERP/manifest.baseline.json"

# The host's extension-surface contract.
dotnet run --project "$ERP" --no-build -- contract "$ROOT/$ERP/host-contract.json"

# Per-plugin contract slices (docs/37 D-V4), one per committed *.contract.json.
for slice in samples/*/*.contract.json; do
  [ -e "$slice" ] || continue
  plugin="$(basename "$slice" .contract.json)"
  dotnet run --project "$ERP" --no-build -- contract "$ROOT/$slice" --plugin "$plugin"
  echo "  slice: $plugin"
done

# The generated TS client.
node scripts/generate-types.mjs "$ERP/manifest.baseline.json" samples/web/src/generated/tam.ts

echo "regenerated: manifest baseline, host contract, plugin slices, tam.ts — now: git add -A"
