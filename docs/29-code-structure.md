# 29 — Code structure: the repo map and where things go

Status: **normative** for source layout. CLAUDE.md carries the working agreement; this doc is
the reference it points at. docs/16 is the design-era NuGet packaging sketch, not the source
layout — the two answer different questions.

## Repo map

```
src/Tam.Core               contracts, model builder + verification, manifest, portable AST,
                           locale catalogs. ZERO package references — BCL only. Types here are
                           wire-adjacent; adding a dependency here is a design event.
src/Tam.Compiler           Roslyn analyzer + source generator (TAM/L10N/EDIT/PLG rules,
                           AddDiscovered). netstandard2.0 — no modern C# runtime helpers.
src/Tam.EntityFrameworkCore EF integration: conventions, tenant filter/stamp, audit capture,
                           merge, framework ENTITIES, outbox/idempotency records, IsoTime.
src/Tam.AspNetCore         the pipeline (executors, endpoints, DI), background loops,
                           framework packages (Packages/), pipeline infrastructure (root).
src/Tam.Auth.OpenIddict    the embedded auth server behind IActorProvider.
packages/tam-core          TS manifest types, Px evaluator, localization, HTTP client.
packages/tam-react         React runtime: context, renderers, OperationForm, ViewGrid.
apps/web                   the sample SPA (Norrservice ERP) — host code, not framework.
samples/erp                the reference host: domain, bindings, seed, Program wiring.
samples/inspect|fortnox|approvals   vendor-plugin exemplars (the plugin shapes to copy).
tests/Tam.Tests            the unit/pipeline suite. Wire verification scripts live outside
                           the repo (session tooling) — STATUS records what they proved.
docs/                      numbered design docs; 19-decisions.md is the ledger.
```

Dependency lines that must not blur: Core references nothing; EFCore references Core;
AspNetCore references both; nothing references a sample; samples reference the framework and
each other never.

## The package tier on disk

One package = one file: `src/Tam.AspNetCore/Packages/<Area>.cs`, containing in order

1. the `[TamPackage]` class (its `Configure` holds the form/grid bindings),
2. its `<Area>Findings`,
3. its gates / policy helpers,
4. its operations,
5. its views.

Registration and implementation are never in different files — that was the smell that
motivated this layout. A package that outgrows ~400 lines graduates to a folder under
`Packages/` and splits within it (the approvals sample shows the split shape: Domain /
Features / Plugin). The shared locale catalogs stay one embedded pair
(`src/Tam.AspNetCore/locales/{sv,en}.json`) — per-package locale files would fragment the
L10N001 gate for no benefit.

## Package content vs pipeline infrastructure

Litmus test: **"would this type still exist if the package's admin surface didn't?"** If yes,
it is infrastructure and lives at the project root, never inside `Packages/`.

The canonical infrastructure list and why (consumers in parentheses):

- `PluginActivation.cs` — `PluginActivations`, `ActivationCache`,
  `PluginAwareExtensionRegistry` (OperationExecutor, ViewExecutor, ResolveExecutor,
  manifest + integration endpoints).
- `Entitlements.cs` — `Subscriptions` + `SubscriptionFindings`: the enforcement half of
  billing (plugins.activate entitlement gate, users seat lease). A billing check can't live
  behind the activation it gates.
- `SecretVault.cs` — the encryption service (outbound integrations, DI, host seeds).
- Executors, outbox dispatcher, retry drivers, scheduler, janitor, SSE broadcaster,
  `TamActivator`, `PinnedScope`, `EnvelopeReplay`, `TamDirectory` — the pipeline itself.

The one sanctioned cross-package call: a package's public **policy helper** (e.g. `RoleRules`
— the single validation path for `roles.define` AND tenant-package install, so a bundle can
never become a privilege-escalation vector). Packages never call each other's operations.

## Where a new feature's pieces go

| Piece | Location |
| --- | --- |
| Entity | `src/Tam.EntityFrameworkCore` (`*Entity`, `ITenantScoped`, `*Json`, `*Iso`) |
| Operation / view | the owning package file (framework) or plugin Features.cs (vendor) |
| Form / grid binding | the same package/plugin `Configure` |
| Nav declaration | package/plugin: `plugin.Nav(...)` in `Configure` (content + suggestion); host layout: `model.Nav("web", …)` in Program.cs — never the other way (docs/30) |
| Finding codes | `<Area>Findings` in the same file; locale entries in BOTH catalogs |
| Gate / handler classes | same file as the package/plugin class; ctor-injected |
| Test | `tests/Tam.Tests` — plus an analyzer rule when the invariant is structural |
| Wire change | re-export `manifest.baseline.json` + regenerate `apps/web/src/generated/tam.ts` |

## Namespace policy

Namespaces are API; folders are not. File moves never change namespaces as a side effect.
Current split: package classes + extensions/roles/audit content sit in
`Tam.AspNetCore.SystemOps`; the other packages' content sits in `Tam.AspNetCore` (the
`SystemOps` aggregate `AddTamSystem` is the public entry hosts call). Unifying these is
possible pre-1.0 but must be its own deliberate commit, never ride a refactor.

## Enforcement map

| Convention | Enforced by |
| --- | --- |
| Operation/view shape, authorize, tenant filters, query composition, paired atoms | analyzer TAM001–006 (build error) |
| No display text in code; label keys exist in default culture | analyzer L10N001 + Build()-time gate |
| Change<T> discipline | analyzer EDIT001 |
| Plugin/package namespacing, gate targets, packaged fields, plugin-only seams | Build()-time PLG000–005 / PKG000 |
| Wire-name permanence | CI baseline check (scripts/check_manifest.py) |
| File layout, naming, cross-package policy-helper rule | review + this doc — promote to analyzer when it hurts |

## Refactor ledger

Done:
- [x] Packages/ co-location (11 files), infra extraction (PluginActivation, Entitlements,
      SecretVault), InboundIntegrations rename — `dbdb9d8`.

- [x] Framework renderers into tam-react; badge-color registry; conflict-dialog locale keys;
      Tam.AspNetCore.Postgres extraction; MockFortnox to samples/fortnox; UseTamConventions +
      TamManifestExport host helpers; erp locale dedupe.

- [x] App.tsx rewritten onto nav slots (docs/30 v1): the hand-wired NavLink block and one-line
      page components are gone; what remains is the shell, `CustomerPicker`, and the one custom
      page (`OrdersPage`, registered via `registerPage`). Nav model + merge live in
      `Tam.Core/Nav.cs` + `TamModel.MergeNav/VerifyNav`; slot components in
      `packages/tam-react/src/nav.tsx`.

- [x] The mechanical-split batch, proven by a byte-identical manifest AND an unchanged web
      bundle hash: `ModelConventions.cs` → conventions + `FrameworkEntities.cs`;
      `TamAspNetCore.cs` → `Providers.cs` + `TamServices.cs` (AddTam) + `TamEndpoints.cs`
      (MapTam, as one partial class — the public name is API); `TamModel.cs` → model +
      `TamModelBuilder.cs` + `ModelVerification.cs` (partial builder); `TamPasswords.cs` and
      `PipelineFindings` (→ `Findings.cs`) extracted; `tam-core/src/index.ts` → `manifest.ts` /
      `px.ts` / `i18n.ts` / `client.ts` + re-exporting index; `erp/Db.cs` → Db + `Seed.cs`;
      embedded-locale loads memoized per assembly (the 11 packages now deserialize the shared
      catalogs once). The invite-accept item was already satisfied — the handlers render via
      `AuthPages` and no inline markup remained.

Open: nothing — new debts get a checkbox here when they appear.
