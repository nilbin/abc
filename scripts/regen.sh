#!/usr/bin/env bash
# Thin shim over the framework tool. Regenerates every committed model artifact — manifest
# baseline, host contract, all per-plugin contract slices, and the TS client — from tam.json.
# Kept for muscle memory; the tool is the real thing (docs/38):
#
#   dotnet run --project src/Tam.Cli -- regen      # in this repo
#   dotnet tam regen                               # for a consumer who installed Tam.Cli
#
# then: git add -A  (and `python3 scripts/check_manifest.py …` for the D4 additive check).
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project src/Tam.Cli -- regen
