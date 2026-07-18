# 38 — Tooling: keeping the committed artifacts current

A Tam host commits a handful of **generated artifacts** — the manifest baseline, the host
contract, each plugin contract slice, and the generated TS client. They are committed rather than
built-on-the-fly because they are **build inputs and versioned dependencies**: a plugin references
a contract slice as an `AdditionalFiles` item (a lockfile the source generator reads), the TS app
imports the generated client, and the D4 baseline is the reference the additive check diffs
against. CI never writes them — it only verifies they are current — so regenerating is a step the
author runs, exactly like a lockfile update.

## Why they can't be a pure source generator

The manifest and contracts are derived from the **composed, built model** — the `TamModel` you get
after the fluent builder executes every `AddPlugin`/`Configure`/`PublishesEvent` call and runs
Build()-time verification. Producing them means *executing* the composition root, which is why the
host exposes `dotnet run -- manifest` / `contract`. A Roslyn source generator only sees syntax and
symbols; it cannot run the composition, so it cannot produce these. What a generator *does* do is
turn the committed JSON into typed symbols (`HostContract.*`, the event/view facades) — the last
mile, not the export. And the committed **file** (not the producing assembly) is deliberate: a
dependent plugin must not reference its parent's CLR types (the no-shared-assembly rule, docs/31),
and a cross-vendor consumer has only the published artifact anyway.

## `dotnet tam` — one tool, any host

The framework ships a CLI, `Tam.Cli`, packaged as a .NET tool. A consumer installs it and runs it
in their own repo:

```sh
dotnet tool install --global Tam.Cli      # or --local with a tool manifest
dotnet tam regen                          # rewrite every artifact, then: git add -A
dotnet tam verify                         # re-export to a temp dir and byte-compare (CI gate)
dotnet tam manifest [path]                # one-off manifest export
dotnet tam contract [path] [--plugin id]  # one-off host / plugin-slice export
```

Inside this repo the same tool is invoked through the project:

```sh
dotnet run --project src/Tam.Cli -- regen
dotnet run --project src/Tam.Cli -- verify
```

There are **no hardcoded paths** in the tool — layout comes from `tam.json`, found by walking up
from the working directory.

## `tam.json`

```json
{
  "host": "samples/erp/Erp.csproj",
  "manifest": "samples/erp/manifest.baseline.json",
  "hostContract": "samples/erp/host-contract.json",
  "webTypes": "samples/web/src/generated/tam.ts",
  "typesCommand": ["node", "scripts/generate-types.mjs", "{manifest}", "{webTypes}"]
}
```

- `host` — the composition-root project the exports run.
- `manifest` / `hostContract` / `webTypes` — where the committed artifacts live.
- `typesCommand` — how to regenerate the client; `{manifest}` and `{webTypes}` are substituted
  (the framework's codegen today is `scripts/generate-types.mjs`, but any command works).
- `slices` — OPTIONAL. Omitted, the tool auto-discovers every committed `*.contract.json` (the
  `-contract.json` host file is excluded by the dot) and derives each `pluginId` from the
  filename, so a new dependency parent (docs/37 D-V4) needs no edit here or in CI. Declare it
  explicitly only to pin a non-conventional path:

  ```json
  "slices": [{ "pluginId": "invoicing", "path": "samples/invoicing/invoicing.contract.json" }]
  ```

## CI

Two steps, both layout-agnostic:

```yaml
- name: Manifest baseline check (D4, additive-only)
  run: |
    dotnet run --project samples/erp --no-build -- manifest /tmp/manifest.current.json
    python3 scripts/check_manifest.py samples/erp/manifest.baseline.json /tmp/manifest.current.json
- name: Artifacts are current (dotnet tam verify)
  run: dotnet run --project src/Tam.Cli --no-build -- verify
```

`tam verify` covers **freshness** (the committed files equal a fresh export, byte for byte);
`check_manifest.py` covers **additivity** (D4 — wire names are permanent; retire, don't drop).
They are separate concerns: freshness catches "you forgot to regen"; additivity catches "this
change breaks the contract". A follow-up is per-slice additivity — today only the host manifest
gets the additive gate; a breaking change to a plugin's own contract is caught by its dependents
failing to compile, not yet by a dedicated slice diff.
