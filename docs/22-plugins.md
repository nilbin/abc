# 22 — Plugins: Packaged Extensibility

**Status: P1–P3 and P5-v1 implemented and verified** (packaging, PLG001–PLG005, per-tenant activation, manifest/MCP/OpenAPI omission, 404 gating, packaged fields on host entities, gates on host operations, effect subscribers, **plugin-shipped inbound integrations** (samples/fortnox), tenant packages with dry-run/atomic install/version guard/retire-on-uninstall, and automation rules as Px-conditioned blocking findings with RUL001–003 — see STATUS.md). Plugins compose with **subscription entitlements** (docs/24): activation is gated by the tenant's plan, which is how a marketplace prices them. **P4 (custom objects) and the rest of P5 (action catalog, effect triggers, rule-builder UI) are design**. One correction from implementation: plugins address host entities and operations by *wire key* (`"order"`, `"orders.complete"`), not CLR types — a plugin references the host's contract, never its assembly. Decision summary: D8 in [19-decisions.md](19-decisions.md).

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

Two clarifications the boundary implies rather than forbids:

- **Tenants may run all the code they want — out of process, on their compute, with their credentials.** An external app holds a scoped actor (permissions from the D1 catalogue, nothing more), calls operations and views through the public API, and subscribes to committed effects via webhooks off the outbox. The full pipeline — authorization, validation, audit, idempotency — applies to their code exactly as to a human. This is the Shopify model, and it is the primary answer to "I need custom logic": the marketplace's third tier below.
- **The researched escalation path, if hot-path in-process tenant logic is ever truly required, is a WebAssembly sandbox** (Wasmtime-class: fuel-metered, memory-capped, capability-based — the guest sees only host functions we hand it: read these fields, return findings). Data in, data out; no EF, no reflection, no network. That is the modern Apex, adopted for good reasons by Shopify Functions for their own hot paths. It is named here so the future debate starts from a real design, not from "let's just load their DLL" — which modern .NET cannot isolate at all.

## Plugins (the compiled channel)

A plugin is an ordinary .NET assembly containing the same five concepts as the host — domain state, operations, views, derivations, bindings — plus a manifest class that names it:

```csharp
[TamPlugin("inspect")]                    // the namespace prefix, permanent (wire name rules apply)
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.RequiresView("orders.detail", "id", "status"); // declared dependency, checked at build (docs/31 D-X3;
                                                          // the earlier RequiresHostEntity<Order> sketch was CLR-shaped and never built)
        plugin.LocaleDefaults();   // embedded locales/{culture}.json by convention, app-overridable
    }
}
```

The host adds one line — `model.AddPlugin<InspectionPlugin>()` (the source generator emits the discovery, exactly like `AddDiscovered()`) — plus, when the plugin ships its own tables, the storage opt-in in the host DbContext: `InspectionPlugin.AddInspect(modelBuilder)`. Everything else follows from what the plugin's assembly declares.

### What a plugin may contribute

- **Its own entities, operations, views, forms, grids** — indistinguishable from host ones except for the mandatory id prefix: `inspect.checklists.complete`, permission `inspect.checklists.manage`. A plugin id that doesn't prefix all of its ids is a build error (`PLG001`).
- **Packaged fields on host entities.** The plugin declares extension fields on `Order` through the *same* spec the tenant registry uses, but compiled and namespaced: `inspect.requiresInspection`. They ride the existing `ExtensionData` column and `ext.{key}` wire channel; the overlay now has three origins (compiled / plugin / tenant), and key prefixes make collisions impossible. Nothing downstream changes — grids, forms, audit, MCP, filtering already treat extension fields uniformly.
- **Gates on host operations.** The Salesforce-validation-rule power, typed: a plugin may register a precondition on a host operation — `plugin.Gate<ChecklistGate>("orders.complete")`, a class constructed per invocation with ctor injection, returning findings. The pipeline runs gates after authorization, before the handler. Every gate is listed in the manifest and the impact report (`orders.complete` shows "gated by inspect"), so the coupling is visible, not magic (`PLG002`: a gate on a nonexistent or non-deterministic target is a build error).
- **Effect subscribers.** Plugins react to committed host effects (the outbox) — never by patching host handlers. "When an order completes, open an inspection" is a subscriber creating its own aggregate via its own operation, with its own audit trail.
- **Inbound integrations.** A plugin ships an external-system integration targeting a host operation by wire id (`plugin.Integration("orders.import", "orders.create", key, map)`), mapped to `POST /api/integrations/{plugin}.{id}`, activation-gated, inbox-idempotent with retry and late-fix recovery. The mapper resolves external identities through host *views* as the actor — never host tables. Implemented: samples/fortnox.
- **Locales and permissions**, merged like the system module's are today: plugin defaults embedded, app files override, L10N001 gates coverage.

### What a plugin may NOT do

Reach into another module's `DbContext`, replace host operations, contribute middleware, or register un-namespaced anything. Plugins compose *around* the host's model through the same public seams every other consumer uses (operations, views, effects, extensions). If a plugin needs host data, it queries host views with the actor's permissions — a plugin is not a superuser.

### Activation is tenant data

The plugin is compiled into the deployment, but it is *dormant* until a tenant activates it (`plugins.activate`, a framework operation like `extensions.define-field` — audited, revisioned, permission-gated). The effective manifest simply omits contributions of inactive plugins: no nav, no operations, no MCP tools, no packaged fields. HTTP calls to an inactive plugin's operations 404 per tenant. This gives the "install an app in your org" experience with build-time safety — installing new *code* is a deploy of the host; *enabling* it is a click.

Per-plugin entitlement (billing tiers) is the same mechanism with a different guard on the activate operation.

### Versioning and compatibility

D4 applies per plugin: each plugin's manifest contribution is baseline-checked in the host's CI, and its wire names/permissions are permanent. A plugin upgrade that removes an operation fails the host build until the baseline is consciously re-approved — the host vendor, not the tenant, absorbs compatibility risk, which is where the compiler is.

### Framework packages (the framework-trust tier) — BUILT

The framework's own admin capabilities register through the SAME `PluginBuilder` surface a
vendor plugin uses — `AddTamSystem()` is eleven `[TamPackage]` modules (`tam.users`,
`tam.audit`, `tam.roles`, `tam.tenancy`, `tam.rules`, …), each shipping its operations, views,
forms and grids. Every framework capability therefore exercises the plugin seams daily — the
strongest regression guard the seams can have. A package differs from a vendor plugin on the
tier axes only:

- **Always active.** Never in the activation table, never entitlement-gated; every activation
  consumer (pipeline existence check, gate filter, manifest, outbox dispatch) unions package
  ids in. There is nothing to toggle — `plugins.activate` (input: `{ "pluginId": ... }`) doesn't know packages exist, so the
  always-on tier can't be switched off (who activates the activator).
- **Claimed prefixes instead of an id namespace.** Framework wire names (`users.invite`,
  `audit.entries`) are live and permanent (D4), so a package CLAIMS the prefixes it has always
  owned — `[TamPackage("tam.users", "users", "web.users")]` — and PLG001's package variant
  validates every contribution against the claims.
- **Framework trust.** Packages ship in the Tam repo and may touch framework tables; the hook
  set itself (gates, subscribers, park, replay) stays closed even for packages — the executor
  remains auditable. Middleware/endpoint access, when it arrives, is package-tier only: a raw
  endpoint bypasses the pipeline, which is exactly the hole the wire-only rule closes for
  vendor plugins.

What stays CORE, never a package: authorization and actor resolution (a gate fails OPEN by
absence — nothing mandatory can be a gate), tenant isolation, the same-transaction pipeline
atoms (idempotency, audit capture, outbox write, extension channel), entitlement/seat
enforcement, and the activation machinery itself.

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

> Superseded in detail by [23-custom-objects.md](23-custom-objects.md), which resolves the permission model and storage reuse; the sketch below records the original direction.

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

Rules are registry data (RUL### diagnostics at definition: unknown field, type mismatch, unreachable condition), evaluated with a budget (no loops, no recursion, bounded tree depth), fully audited, and — the sharpest dogfood in the codebase — **executed as the `tam.rules` package's own PURE wildcard gate**: the executor has no rules special case; the framework's P5 feature runs through the very gate seam it sells, in the pre-transaction phase (a declared pure-over-input gate is the cheap fail; transactional gates keep running inside the transaction). And — because Px is portable — the *same* condition can drive client-side form behavior without a round trip. Message keys, as always: the finding's text lives in the tenant's label catalog, per culture.

What rules never get: arbitrary code, HTTP calls (that's the integration channel, via the enqueue action), or writes to compiled fields (operations own compiled state transitions — EDIT001's philosophy extended to tenants).

## The marketplace: three tiers, one trust model

"Pick and choose from a marketplace" needs no tenant code upload — it decomposes onto the channels above:

| Tier | Listing | Install | Contained by |
| --- | --- | --- | --- |
| **Configurations** | tenant packages (fields, roles, rules) | self-service: dry-run → findings → atomic apply | the registry compiler |
| **Vetted plugins** | compiled partner modules | vendor reviews + builds in; tenant clicks *Activate* | build-time diagnostics + review |
| **External apps** | third-party services | tenant grants the app a scoped actor; app uses API + webhooks | D1 permissions |

Catalog UI, ratings, and billing are product features on top of `plugins.activate` and package install; the framework owes the marketplace only what D8 already defines — namespacing, per-tenant activation, entitlement guards, atomic validated install, scoped app actors (arrives with real authn).

What the mechanics make possible that incumbent marketplaces get only from manual review: **a provable capability manifest per listing**. A tenant package's exact footprint — which entities it touches, which fields it adds, which operations its rules gate, which events it consumes — is *derived from its content by the registry compiler*, not claimed by its author. A plugin's footprint (contributed operations, packaged fields, gates, subscribers) is likewise a build artifact, and a plugin *upgrade* diffs as a manifest diff. The install screen can therefore say "this package reads orders, writes `ext.coldChainClass`, gates `orders.complete`, and never sees customer emails" with compiler authority. Shopify approximates this with coarse OAuth scopes; Salesforce with human security review. Here it falls out of contributions-as-data.

A tenant's entire customization — installed packages, fields, roles, rules, custom objects — is likewise one serializable, diffable document: **the org is a file**. Test-to-production promotion is a diff review; migrating a tenant is an export; the "what changed in our org this quarter" question is version control, not archaeology.

## Beyond parity: what the model enables that the incumbents can't retrofit

Everything above reaches parity with Salesforce/Shopify on safer foundations. These four are the moves that go *past* them — each is possible only because of commitments already made (manifest as the single contract, operations as the only write path, Px as structured expressions, same-transaction audit), which is precisely why they are hard to copy. Design-stage, ranked by leverage:

1. **Agent-authored customization.** Both incumbents built human-first admin UIs and are now bolting agents onto metadata swamps. Tam's effective manifest *is* an agent's world model — typed, per-tenant, complete — and the registry's diagnostics are the agent's guardrails: it structurally cannot install an invalid package, and dry-run gives it a feedback loop. The endgame: a tenant admin says "we need cold-chain tracking on orders with an auditor role and a rule that class-2 requires a date", and the agent authors the tenant package, dry-runs it, presents the findings and the capability manifest, and installs on approval — through `extensions.define-field`-class operations that already exist and are already MCP tools. Extensibility-by-conversation with compiler-checked output is a different product category than "AI helps you click".
2. **Time-machine dry-run (replay impact preview).** Because operations are the only mutation path and the audit trail stores every invocation in canonical wire form, a candidate rule/package can be evaluated against *recorded reality*, not a guess: replay the tenant's last N weeks of audited operations through the pipeline with the candidate configuration overlaid, inside transactions that never commit and with effects suppressed, and report "this rule would have blocked 37 of 412 real orders — these three users hit it most". Advisory (time-dependent logic and sequences make replay approximate), but it converts the scariest moment of tenant self-service — "what will this break?" — into evidence. Salesforce cannot do this generally (Apex side effects, no uniform operation log); it falls out of Tam's audit + purity decisions.
3. **Deterministic composition with provenance.** The moment two packages and a plugin all touch `orders.complete`, incumbents descend into undefined trigger order and flow-vs-trigger folklore. Tam can compile each tenant's full contribution set (gates, rules, subscribers — all data or declared code) into one ordered, cycle-checked execution plan at install time: two writers to one extension field is an *install-time finding*, not a production mystery; and every runtime finding carries the rule/package/plugin that produced it, so "why was this blocked?" has a provenance answer in the UI. The effective manifest already merges origins; this extends the merge from fields to behavior.
4. **The binding-time dial (graduation as tooling, both directions).** For the incumbents, clicks and code are different universes with no path between them — orgs rot into hundreds of flows nobody dares touch. Tam's thesis is that a custom object and a compiled entity are *the same model at different binding times*, so graduation becomes a command: scaffold the C# entity/operations from the registry definition, generate the data migration, preserve wire names so no client notices. And the dial turns both ways — a compiled feature that only one tenant uses can be demoted to a package. Nobody offers configuration→code→configuration mobility; the wire-name permanence rule (D4) is what makes it invisible to callers.

Ordering note: #2 needs only machinery that exists today (audit + pipeline + overlay), #1 needs P3's dry-run install plus the MCP tools that already exist, #3 becomes real at P5, #4 at P4. None create new trust surface — they are leverage on the boundary, not holes in it.

## Phasing

1. **P1 — plugin packaging**: `ITamPlugin`, id namespacing + PLG001, discovery via the source generator, per-tenant activation, manifest omission for inactive plugins. Proves the seam with a real sample plugin.
2. **P2 — plugin depth**: packaged fields on host entities (three-origin overlay), operation gates + manifest/impact visibility, effect subscribers.
3. **P3 — tenant packages**: bundle format, dry-run install, atomic apply, retire-on-uninstall, upgrade diffs.
4. **P4 — custom objects**: after typed JSON predicates + index promotion + RLS. Generated standard operations through the real pipeline.
5. **P5 — automation rules**: Px rule storage, RUL diagnostics, action catalog, pipeline evaluation with budgets.

Each phase is independently shippable and independently valuable; nothing in P1–P3 waits on the JSONB query work that gates P4.


## The plugin authoring reference (what the RTFM run proved must be written down)

Two documentation-only implementers built plugins against this chapter; everything below is
what they had to discover through compiler errors. Normative from now on:

- **Data access**: plugin operations/views/gates take **`ITamDb`** (namespace `Tam.AspNetCore`;
  surface `tam.Db.Set<T>()`) as an ordinary handler parameter — never a host DbContext type.
  Host reads go through **`IHostViewReader`**: actor mode
  `RowsAsync(viewId, IReadOnlyDictionary<string, string?> query, OperationContext, ct)`
  (values are wire strings — `guid.ToString()` yourself), service mode drops the context and is
  whitelisted by `RequiresView`. Both return `ViewResponse` (`Rows`, `Total`).
- **Entities**: tenant scoping is structural — implement
  `Tam.EntityFrameworkCore.ITenantScoped` (a settable `string TenantId`); the ambient filter
  and the insert stamp then apply. Store timestamps you intend to SORT on as ISO-8601 strings
  (the framework's `*Iso` convention — SQLite cannot order `DateTimeOffset`); `IsoTime.Now()`
  produces them. Optimistic concurrency (`Version`) is opt-in via `IVersioned`.
- **Bindings and events**: a plugin declares its own forms/grids with
  `plugin.Model.Form<T>(...)` / `.Grid<T>(...)` (convention defaults apply, docs/32 D-P6) and
  its events with `plugin.Model.PublishesEvent("id.event", ...fields)`; an operation publishes
  with `result.Effect(new EventPublished("id.event", payload))`. Binding/nav/view/operation ids
  all sit under the plugin prefix — for web bindings that means **plugin first, surface second**
  (`invoicing.web.invoices`), the mirror of the host's `web.orders.list` (PLG001 enforces it).
- **Locales**: ship `locales/{sv,en}.json` as `EmbeddedResource` and call
  `plugin.LocaleDefaults()`. Required keys beyond your fields: `plugins.{id}.title`, and
  `ext.{id}.{key}` for every packaged extension field. Every label key — inputs, OUTPUTS,
  results, nav — is L10N-gated in the default culture at Build().
- **csproj**: copy a sample plugin's project file — the contract is references to
  Tam.Core/Tam.EntityFrameworkCore/Tam.AspNetCore, `Tam.Compiler` as
  `OutputItemType="Analyzer"`, `AdditionalFiles` + `EmbeddedResource` for `locales/*.json`.
  Build()-time PLG/L10N checks run in the HOST build — a plugin compiling green alone has not
  been verified until a host registers it (the manifest export is the cheapest host check).
