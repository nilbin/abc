# CLAUDE.md — working agreement for the Tam repo

Tam (Typed Application Model): a .NET 10 framework where operations/views/forms/grids are
compiled model, the manifest is the public API, and tenants extend by data — never code.
Read docs/01-overview.md for the idea, STATUS.md for what is actually true today.

## Build / test / verify

- `dotnet test` — all of tests/Tam.Tests must pass. New behavior ships with tests.
- `cd samples/erp && dotnet run` — the reference host (http://localhost:5100).
- Manifest baseline (D4): wire names are permanent. If you intentionally change the wire
  surface, re-export and commit the baseline in the same change:
  `cd samples/erp && dotnet run -- manifest manifest.baseline.json`, then regenerate the
  TS types: `node scripts/generate-types.mjs samples/erp/manifest.baseline.json
  samples/web/src/generated/tam.ts` (scripts/check_manifest.py fails CI otherwise).
- "Verified on the wire" means exercised over HTTP against the running sample — on SQLite
  AND PostgreSQL when query translation is involved. Don't claim it in STATUS.md otherwise.
  The suites live in `verify/` (`node verify/all.mjs` against a running host on a FRESH
  database — they are not idempotent). The full loop — build gates, regen trio, fresh-DB
  matrix on both engines, operational traps — is the **verify-loop** skill
  (.claude/skills/verify-loop). Driving/screenshotting the app with Playwright (login flow,
  tenant picker, locator gotchas) is `scripts/snap.mjs` + the **app-snap** skill.

## Hard rules (build-enforced — don't fight the analyzers, extend them)

- TAM001–003: every operation has [Operation], a permission, Input, Execute.
- TAM004/TAM005: NEVER write a manual `TenantId ==` filter or compose widened queries over
  implicitly-filtered sources. Tenant scoping is the platform's job.
- TAM006: paired-atom scoping — `x.read` is own-scoped, `x.read-all` widens; both directions
  enforced. Never invent scope suffixes.
- L10N001: zero display text in code. Every label/message is a locale key present in every
  culture catalog (sv + en). Use [LabelKey] and FindingFactory codes.
- The TYPE carries the defaults (docs/02, docs/34 M5): semantic wrapper types own
  [Format]/[LabelKey]/[Lookup] so every usage inherits them; member attributes override,
  convention is the last resort. Use Tam.Money for money; never re-add name sniffing.
  Reference fields get [Lookup("x.lookup")] on the wrapper, not per-form renderers or
  options derivations.
- EDIT001: consequential state never rides a Change<T> — state transitions are intent
  operations.
- PLG001–005: everything a plugin/package contributes is id-prefixed (packages: claimed
  prefixes); gates/fields/effects/integrations only via PluginBuilder.
- Prefer making a new rule build-enforced (analyzer or Build()-time verification) over
  documenting it here.

## Where code goes

- One package = one file: src/Tam.AspNetCore/Packages/<Area>.cs holds the [TamPackage] class
  (with its Form/Grid bindings) AND its findings, gates, operations, views. Registration and
  implementation are never in different files. If a package outgrows ~400 lines, give it a
  folder under Packages/ and split within it.
- Pipeline infrastructure stays at the project root, never inside Packages/: anything the
  executors, endpoints, or other packages depend on (PluginActivation.cs, Entitlements.cs,
  SecretVault.cs, executors, outbox, broadcaster). Litmus test: "would this still exist if
  the package's admin surface didn't?" → root.
- Entities live in src/Tam.EntityFrameworkCore (never next to operations); suffix *Entity,
  ITenantScoped, JSON columns as *Json, timestamps as *Iso strings via IsoTime.
- Plugins/samples split BY AGGREGATE once they have more than one: Domain/<Aggregate>.cs
  (entity + its findings + value types) and Features/<Aggregate>.cs (its operations/views),
  mirroring the ERP host's Features/ + ErpModel.<Domain>.cs fragments; <Name>Plugin.cs keeps
  only the plugin class, its Configure table of contents, gates/handlers, and EF mapping. A
  single-aggregate plugin may stay Domain.cs + Features.cs. No file mixes two aggregates'
  classes, and ~400 lines is the split tripwire everywhere (same as packages). ENFORCED in
  CI by scripts/check_structure.py (line cap + one wire prefix per file; exceptions are
  allowlisted there with a reason).
- Sample DOMAINS model tactical DDD where invariants exist — factory methods, private
  setters, Result-returning guarded transitions, semantic value types (the Order/WorkOrder
  shape) — and stay plain data ONLY for genuinely dumb rows (join/lookup tables). An entity
  whose state machine lives in its operations is the smell to fix, not the pattern to copy.
  OWNED CHILDREN live ON their root: a collection navigation plus root intents (AddItem,
  Check — the ChecklistTemplate/Checklist shape), never free-floating rows keyed by parent
  id with the invariant re-implemented as queries in each operation. Join rows to EXTERNAL
  identities (group members, role grants) are references, not owned children — those stay
  flat. EF note: when the root is already tracked, a newly created child must be Db.Add()ed
  explicitly (its client-set key otherwise reads as an existing row → phantom UPDATE).
  ENFORCED by analyzer TAM008: an ITenantScoped class under Domain/ with a mutable public
  setter is a build error; a genuinely plain row opts out with #pragma warning
  disable/restore TAM008 plus a reason comment (grep the pragmas to audit the exceptions).
  Before ADDING a new domain/aggregate (or making an operation write a child entity
  directly), run the domain-design skill (.claude/skills/domain-design) and write the
  aggregate classification down FIRST — domains are designed, never fast-forwarded.
- Cross-package calls: only to a package's public policy helper (e.g. RoleRules — the single
  validation path for roles.define AND tenant-package install), never to its operations.
- Findings: `static class <Area>Findings { public static readonly FindingFactory X =
  Finding.Error("area.kebab-code"); }` — colocated with the feature that raises them.
- Handler classes (gates, effect handlers, parked work) take dependencies as CONSTRUCTOR
  parameters — never a service locator; `IServiceProvider` in a signature is a smell.

## Naming

- Wire ids: kebab-case, `area.verb-noun` (`extensions.define-field`); permissions
  `area.manage` / paired atoms. Wire ids and locale keys are PERMANENT once shipped.
- [TamPackage] classes: Tam<Area>Package (framework tier, always active).
  [TamPlugin] classes: <Name>Plugin (vendor tier, activation-gated). The *Module suffix is
  retired — don't add new ones.
- Operations: static class, nested `Input`/`Output` sealed records, static `Execute` with
  parameter injection. Views: nested `Query`/`Result`, `Execute` → IQueryable<Result>,
  `Capabilities` declares sortable/filterable/default-sort — undeclared = unsupported.
- Retire, don't delete: deactivation/retirement flags, never row deletion, for anything
  audited or referenced.

## The docs site (published surface)

- https://nilbin.github.io/abc/ is rebuilt from docs/ by .github/workflows/docs.yml on every
  push to main (MkDocs Material, `--strict`); STATUS.md publishes as /status. There is no
  separate publish step to remember — but there IS a completeness gate:
- A new doc page lands in THREE places in the same commit: the file under docs/, the
  mkdocs.yml nav (in its themed section), and docs/llms.txt (one-line description entry).
  scripts/check_docs.py fails the docs build when any of the three is missing.
- Tutorial content lives one page per step in docs/tutorial/ (edit step-NN.md + index.md
  together); docs/20-tutorial.md is a frozen pointer kept only for inbound links.
- docs/llms.txt is API for agents the same way the manifest is for clients: keep its
  descriptions truthful when a doc's scope changes, not just when files appear.

## Per-milestone expectations

1. Tests green (`dotnet test`), analyzer-clean build, manifest baseline reconciled.
2. STATUS.md updated in the same change — it is the honest ledger; never let it overstate.
3. New invariants get a test in tests/Tam.Tests AND, where possible, an analyzer rule.
4. Docs: if a decision changed, update the numbered doc + docs/19-decisions.md, not just code.

## Commits

- Single-purpose commits; one refactor motion per commit (file moves NEVER mixed with
  behavior changes — moves must be provably mechanical: tests green + manifest byte-identical).
- Message = one line stating the invariant or capability, not the mechanics.
- Baseline re-approvals, STATUS/doc updates ride the same commit as the change that caused them.

## Structure reference

docs/29-code-structure.md — full repo map, the package/infrastructure boundary, and the
refactor ledger. docs/16-packages-and-layout.md is the design-era NuGet layout, not the
source layout.
