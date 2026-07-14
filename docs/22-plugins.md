# 22 — Plugins: Packaged Extensibility

**Status: design.** Nothing in this document is implemented yet; it is the agreed target for the next milestone, written before code the same way [20-tutorial.md](20-tutorial.md) was. Decision summary: D8 in [19-decisions.md](19-decisions.md).

## The problem this solves

[15-extensibility.md](15-extensibility.md) gives one tenant one custom field. That is the atom of extensibility, not the product of it. A consumer building on Tam — the "Salesforce-like" ambition — needs three larger structures:

1. **A packaged unit of *code* extensibility**: a vertical capability (inspection checklists, time reporting, e-signing) built by the product team or a partner, shipped as a module, switched on per tenant — without the host application knowing it exists.
2. **A packaged unit of *configuration* extensibility**: a bundle of custom fields, roles, and (later) rules that installs into a tenant as one reviewed, versioned act instead of twenty admin clicks.
3. **Tenant-defined *entities*** — custom objects — so a tenant can track something the product never modeled, with forms, grids, permissions, audit, and agents included.

These are three different trust levels, and the design's central move is to refuse to blur them.

## The trust boundary, stated once

| Channel | Who authors | What it is | When it binds | Trust |
| --- | --- | --- | --- | --- |
| **Compiled model** | product team | C# operations/views/entities | build | full |
| **Plugin** | product team / vetted partner | C# module, namespaced | build (of the host), **activated per tenant at runtime** | full — it is reviewed code in the host process |
| **Tenant package** | admin / consultant / marketplace | declarative bundle (fields, roles, rules) | runtime, validated by the registry compiler | data, never code |
| **Custom object** | tenant admin | declarative entity definition | runtime | data, never code |

**Tenants never upload executable code.** That single sentence is what keeps Tam out of the Apex tar pit: no sandbox, no metering VM, no per-tenant compiler service, no security review of customer code. Everything a tenant authors is data validated by a registry (EXT/RUL diagnostics), evaluated by engines we own (the Px AST, the action catalog), and therefore analyzable, auditable, and portable. When a tenant outgrows the declarative ceiling, the answer is the graduation path (docs/15) or a plugin built by someone with a compiler — not a scripting hole.

Salesforce's equivalents, for orientation: plugin ≈ managed package, tenant package ≈ unmanaged package/change set, custom object ≈ custom object, automation rule ≈ validation rule/flow. What we deliberately do not clone is Apex.

## Plugins (the compiled channel)

A plugin is an ordinary .NET assembly containing the same five concepts as the host — domain state, operations, views, derivations, bindings — plus a manifest class that names it:

```csharp
[TamPlugin("inspect")]                    // the namespace prefix, permanent (wire name rules apply)
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.RequiresHostEntity<Order>();               // declared dependency, checked at build
        plugin.LocaleDefaults("Inspect.locales");         // embedded sv/en, overridable by the app
    }
}
```

The host adds one line — `model.AddPlugin<InspectionPlugin>()` (the source generator emits the discovery, exactly like `AddDiscovered()`). Everything else follows from what the plugin's assembly declares.

### What a plugin may contribute

- **Its own entities, operations, views, forms, grids** — indistinguishable from host ones except for the mandatory id prefix: `inspect.checklists.complete`, permission `inspect.checklists.manage`. A plugin id that doesn't prefix all of its ids is a build error (`PLG001`).
- **Packaged fields on host entities.** The plugin declares extension fields on `Order` through the *same* spec the tenant registry uses, but compiled and namespaced: `inspect.requiresInspection`. They ride the existing `ExtensionData` column and `ext.{key}` wire channel; the overlay now has three origins (compiled / plugin / tenant), and key prefixes make collisions impossible. Nothing downstream changes — grids, forms, audit, MCP, filtering already treat extension fields uniformly.
- **Gates on host operations.** The Salesforce-validation-rule power, typed: a plugin may register a precondition on a host operation — `plugin.Gate<CompleteOrder>(async (input, ctx, db) => …)` returning findings. The pipeline runs gates after authorization, before the handler. Every gate is listed in the manifest and the impact report (`orders.complete` shows "gated by inspect"), so the coupling is visible, not magic (`PLG002`: a gate on a nonexistent or non-deterministic target is a build error).
- **Effect subscribers.** Plugins react to committed host effects (the outbox) — never by patching host handlers. "When an order completes, open an inspection" is a subscriber creating its own aggregate via its own operation, with its own audit trail.
- **Locales and permissions**, merged like the system module's are today: plugin defaults embedded, app files override, L10N001 gates coverage.

### What a plugin may NOT do

Reach into another module's `DbContext`, replace host operations, contribute middleware, or register un-namespaced anything. Plugins compose *around* the host's model through the same public seams every other consumer uses (operations, views, effects, extensions). If a plugin needs host data, it queries host views with the actor's permissions — a plugin is not a superuser.

### Activation is tenant data

The plugin is compiled into the deployment, but it is *dormant* until a tenant activates it (`plugins.activate`, a framework operation like `extensions.define-field` — audited, revisioned, permission-gated). The effective manifest simply omits contributions of inactive plugins: no nav, no operations, no MCP tools, no packaged fields. HTTP calls to an inactive plugin's operations 404 per tenant. This gives the "install an app in your org" experience with build-time safety — installing new *code* is a deploy of the host; *enabling* it is a click.

Per-plugin entitlement (billing tiers) is the same mechanism with a different guard on the activate operation.

### Versioning and compatibility

D4 applies per plugin: each plugin's manifest contribution is baseline-checked in the host's CI, and its wire names/permissions are permanent. A plugin upgrade that removes an operation fails the host build until the baseline is consciously re-approved — the host vendor, not the tenant, absorbs compatibility risk, which is where the compiler is.

## Tenant packages (the declarative channel, bundled)

Everything the tenant registry accepts one item at a time — extension fields today; roles; later custom objects and rules — can be expressed as one JSON document and installed as one act:

```jsonc
{ "package": "cold-chain", "version": 3,
  "fields":  [{ "entity": "order", "key": "coldChainClass", "type": "choice", … }],
  "roles":   [{ "name": "cold-chain-auditor", "grants": ["orders.read", "audit.read"] }] }
```

Install runs the exact registry validation each item would get individually (EXT005/EXT006, roles' catalogue check), atomically — all findings or all applied, with a dry-run mode that returns the findings without applying. Uninstall retires (never deletes — data outlives configuration, as with field retirement). Upgrade is a diff against the installed version with the same additive bias as D4: narrowing a choice list or retiring a field that has data produces warnings that require explicit confirmation.

A tenant package is a *file*: it lives in a repo, gets code review, installs into the test tenant first, and a consultant can carry the same package to ten customers. This is deliberately the boring 80% of "AppExchange" — distribution and billing are product features, not framework ones; the framework's job is that installation is validated, atomic, audited, and reversible.

## Custom objects (the tenant-defined entity)

The largest step, and the reason the extension registry was built as a compiler from day one. A custom object definition is registry data:

```jsonc
{ "object": "coolingUnit",
  "labels": { "sv": "Kylaggregat", "en": "Cooling unit" },
  "fields": [ { "key": "serialNumber", "type": "text", "required": true },
              { "key": "installedAt", "type": "date" },
              { "key": "order", "type": "reference", "target": "order" } ],
  "titleField": "serialNumber" }
```

From it the framework derives what it derives for compiled entities: standard operations (`custom.coolingUnit.create/edit/list` — generated, but flowing through the *same* pipeline: authorization, validation, transaction, audit, idempotency, effects), permissions (`custom.coolingUnit.read/create/edit` entering the catalogue for roles to grant), manifest surface (forms, grids, MCP tools — agents see a custom object exactly as they see orders), and filtering/sorting via the mechanical D7 path.

Storage is one shared table (`custom_records`: TenantId, ObjectKey, Id, Version, Document jsonb, plus the audit tables it already shares) — the JSONB decision (docs/15) generalized from a column to a document. References are validated at write time; reverse lookups ("orders referencing this cooling unit") are a derived view.

**Prerequisites before this ships**, in dependency order: numeric/typed JSON predicates (today's extension filtering is string containment), promoted expression indexes (docs/15's performance path), and D2's RLS backstop. Custom objects inherit the graduation path: when `coolingUnit` becomes load-bearing, it graduates to a compiled entity and the registry entry becomes a redirect.

**Ceilings, stated honestly:** no tenant-defined invariants beyond field constraints and automation rules (below) — a custom object with real business rules is the signal to graduate; no cross-object transactions authored by tenants; list performance is bounded by JSONB until an index is promoted.

## Automation rules (declarative logic, Px-bounded)

The last Salesforce pillar, and the one where the "no tenant code" line matters most. A rule is: **trigger** (operation or effect) + **condition** (a Px AST tree — the same portable expression language forms already evaluate, authored through a builder UI, stored as structure, *never parsed from a string*) + **action from a closed catalog**: add finding (validation), set extension field, publish event, enqueue integration message.

```jsonc
{ "rule": "coldChainNeedsDate", "on": "orders.create",
  "when": { "bin": "and",
    "l": { "bin": "eq", "l": { "field": "ext.coldChainClass" }, "r": { "const": "class-2" } },
    "r": { "bin": "eq", "l": { "field": "requestedDate" }, "r": { "const": null } } },
  "then": { "finding": "rules.cold-chain-needs-date", "target": "requestedDate" } }
```

Rules are registry data (RUL### diagnostics at definition: unknown field, type mismatch, unreachable condition), evaluated in the pipeline with a budget (no loops, no recursion, bounded tree depth), fully audited, and — because Px is portable — the *same* condition can drive client-side form behavior without a round trip. Message keys, as always: the finding's text lives in the tenant's label catalog, per culture.

What rules never get: arbitrary code, HTTP calls (that's the integration channel, via the enqueue action), or writes to compiled fields (operations own compiled state transitions — EDIT001's philosophy extended to tenants).

## Phasing

1. **P1 — plugin packaging**: `ITamPlugin`, id namespacing + PLG001, discovery via the source generator, per-tenant activation, manifest omission for inactive plugins. Proves the seam with a real sample plugin.
2. **P2 — plugin depth**: packaged fields on host entities (three-origin overlay), operation gates + manifest/impact visibility, effect subscribers.
3. **P3 — tenant packages**: bundle format, dry-run install, atomic apply, retire-on-uninstall, upgrade diffs.
4. **P4 — custom objects**: after typed JSON predicates + index promotion + RLS. Generated standard operations through the real pipeline.
5. **P5 — automation rules**: Px rule storage, RUL diagnostics, action catalog, pipeline evaluation with budgets.

Each phase is independently shippable and independently valuable; nothing in P1–P3 waits on the JSONB query work that gates P4.
