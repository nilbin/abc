---
name: verify-loop
description: Use BEFORE claiming any Tam change is done — the full develop-and-verify loop: build gates, test suites, manifest/types/contract regeneration, and the fresh-database wire matrix on SQLite AND Postgres. Also the checklist of CI gates and the operational traps (stale build servers, non-idempotent suites, compound-command pkill).
---

# The develop-and-verify loop

"Done" in this repo means: builds clean, both test suites pass, the three generated
artifacts are re-exported when the wire surface changed, and the wire matrix ran green
against a FRESH database on BOTH engines. STATUS.md only ever records what actually ran.

## 1. Build

```sh
dotnet build Tam.slnx
```

- Build the SOLUTION. `dotnet build proj1 proj2` silently builds only the first — stale
  test binaries then "pass" against old code.
- After touching **Tam.Compiler** (source generator or analyzer): `dotnet build-server
  shutdown` first, or the old generator keeps running inside the persistent compiler.
- The analyzers are the spec: TAM001–009, L10N001, PLG/PKG/PAGE/SLOT/NAV/REACH/DOC rules.
  Never suppress to get green — extend the model or fix the code (CLAUDE.md hard rules).

## 2. Test suites

```sh
dotnet test Tam.slnx        # tests/Tam.Tests + samples/erp.Tests — all must pass
```

New behavior ships with tests (Tam.Testing's in-process pipeline harness for
operations/gates; model-verification tests for new build rules).

## 3. The generated artifacts (only when the wire surface or locale catalogs changed)

ONE command regenerates every committed artifact — manifest baseline, host contract, all
per-plugin contract slices (auto-discovered), and the TS client:

```sh
scripts/regen.sh        # then: git add -A, and check additivity below
python3 scripts/check_manifest.py <(git show HEAD:samples/erp/manifest.baseline.json) samples/erp/manifest.baseline.json
```

The individual exporters (if you need one in isolation):

```sh
dotnet run --project samples/erp -- manifest  $PWD/samples/erp/manifest.baseline.json
dotnet run --project samples/erp -- contract  $PWD/samples/erp/host-contract.json
dotnet run --project samples/erp -- contract  $PWD/samples/invoicing/invoicing.contract.json --plugin invoicing
node scripts/generate-types.mjs samples/erp/manifest.baseline.json samples/web/src/generated/tam.ts
```

- Paths to the exporters must be ABSOLUTE — relative paths resolve under the project dir
  (`scripts/regen.sh` handles this for you).
- The baseline check is additive-only (D4): removals/type-changes/permission-changes fail.
  Wire names are permanent; retire, don't drop.
- CI gates that must match the committed files byte-for-byte: manifest baseline (additive),
  `samples/web/src/generated/tam.ts`, `samples/erp/host-contract.json`, every
  `samples/*/*.contract.json` plugin slice (docs/37 D-V4 — CI re-exports and byte-compares each;
  `scripts/regen.sh` refreshes them all), plus
  `scripts/check_docs.py` and `scripts/check_structure.py` (the ~420-line file cap and
  wire-prefix conventions — run BOTH locally; a file that grew past the cap fails CI even
  when everything else is green).
- FE changes: `cd samples/web && npx tsc --noEmit && npx vite build` (build output is the
  served wwwroot — commit it).

## 4. The wire matrix — fresh DB on BOTH engines

The suites in `verify/` (run via `node verify/all.mjs`) are NOT idempotent — they create
orders, folders, shares, rules. A rerun against a used database fails with misleading
errors. Always start from a truly fresh database, and check port 5100 is free first
(`curl -s -o /dev/null -w "%{http_code}" http://localhost:5100/ --max-time 2` → `000`).

Fresh SQLite:

```sh
rm -f /tmp/erp-fresh.db*
ConnectionStrings__erp="Data Source=/tmp/erp-fresh.db" dotnet run --project samples/erp --no-build &
sleep 12 && node verify/all.mjs
```

Fresh Postgres (query translation differs — SQLite green is NOT enough when queries,
extensions/JSONB, or RLS are involved):

```sh
pg_ctlcluster 16 main restart    # "Removed stale pid file" is fine
su postgres -c 'psql -c "DROP DATABASE IF EXISTS erp;" && psql -c "CREATE DATABASE erp OWNER tam;"'
ConnectionStrings__erp="Host=localhost;Database=erp;Username=tam;Password=tam" dotnet run --project samples/erp --no-build &
sleep 16 && node verify/all.mjs
```

Stopping the server: run `pkill -f "samples/erp"` as its OWN command — never in a compound
command (`pkill ... && next`): its exit code 144 aborts everything chained after it.

When a change adds wire behavior, EXTEND the relevant suite in `verify/` with checks (keep
them order-independent within the suite) — the suite totals are part of the milestone
record in STATUS.md.

## 5. UI verification

For anything user-visible, drive the real app and look at it — see the **app-snap** skill
(`scripts/snap.mjs`). Screenshots accompany UI milestones in the reply to the user.

## 6. Per-milestone wrap (CLAUDE.md "Per-milestone expectations")

- Update STATUS.md (newest entry first) with WHAT ran: suite counts, matrix engines,
  regen/deep-equal facts. Never claim "verified on the wire" for anything that didn't run.
- Sync the affected docs/NN design doc — code and docs land together.
- Commit with a story-telling message; push; check CI retroactively (don't wait on it —
  fix forward if red). GitHub Actions listings are huge: save the JSON and parse with
  python instead of reading raw.
