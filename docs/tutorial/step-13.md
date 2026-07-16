# Step 13 — A partner ships a plugin *(implemented — [22-plugins.md](../22-plugins.md), decision D8; running in samples/inspect)*

Norrservice's certification partner sells an inspection-checklist capability. It arrives as a NuGet package, and the host application adds two lines — one to the model and, because this plugin ships its own entities, one storage opt-in in the DbContext (Step 0's pattern):

```csharp
model.AddPlugin<InspectionPlugin>();               // Program.cs — the model
InspectionPlugin.AddInspect(modelBuilder);         // Db.cs — the plugin's tables, in the host database
```

Inside the package, the same five concepts as everywhere else — a `ChecklistTemplate` entity, `inspect.checklists.*` operations and views, forms and grids, embedded sv/en locales. Three things make it a *plugin* rather than a copy of the host's patterns:

```csharp
[TamPlugin("inspect")]                                  // permanent namespace; PLG001 enforces it
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Model.AddDiscovered();                   // the plugin's own compile-time discovery

        // A packaged field on the HOST's entity — same channel as tenant custom fields,
        // compiled origin, collision-proof key "inspect.requiresInspection", label from the
        // plugin's own locale files. Addressed by WIRE key: a plugin references the host's
        // contract, never its assembly.
        plugin.ExtensionField("order", "requiresInspection", "boolean");

        // Behaviors are NOT registered here — the [Gate]/[OnEffect] attributes below are
        // picked up by AddDiscovered exactly like [Operation]/[View]: declaration lives ON
        // the behavior, and Configure stays a table of contents.
    }
}

// Handlers are internal top-level classes constructed per invocation with CONSTRUCTOR
// injection — the ctor signature is the dependency declaration, exactly like an operation
// handler's parameters. The attribute is the registration: a declared precondition on the
// HOST's operation, visible in the manifest as orders.complete.gatedBy: ["inspect"]. The
// gate reads the wire input and the plugin's OWN data, never host CLR types; the ambient
// tenant filter scopes the query, so no hand-written TenantId predicate.
[Gate("orders.complete")]
internal sealed class ChecklistGate(ITamDb tam) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        if (!gate.Input.TryGetProperty("orderId", out var idElement)
            || !idElement.TryGetGuid(out var orderId))
            return Result.Success();   // malformed input is validation's problem, not the gate's
        var blocked = await tam.Db.Set<Checklist>().AnyAsync(
            x => x.OrderId == orderId && !x.Passed, ct);
        return blocked ? InspectFindings.ChecklistIncomplete : Result.Success();
    }
}

// A reaction to a committed HOST effect — post-commit, off the outbox, in a scope pinned to
// the record's tenant. Multiple [OnEffect] attributes subscribe one class to multiple events.
[OnEffect("order-completed")]
internal sealed class OpenFollowUpChecklist(ITamDb tam) : IEffectHandler
{
    public Task HandleAsync(EffectEvent effect, CancellationToken ct)
        => Task.CompletedTask; /* create the follow-up checklist, idempotently */
}
```

Because the packaged field rides the extension channel, it is already in every grid, form, audit trail, MCP schema, and D7 filter — none of that is plugin code. Because the gate is declared, `orders.complete` in the manifest now reads "gated by inspect" — the fact Step 12's (designed) impact report will surface when anyone touches `CompleteOrder`.

The tenant admin flips it on — `plugins.activate("inspect")` — an audited framework operation like any other. For tenants that haven't, the manifest simply omits everything: no nav entry, no MCP tools, no packaged field, HTTP 404 on `inspect.*`. Installing code was the vendor's deploy; enabling it was the tenant's click. And the trust line holds: the partner wrote C# through a compiler and a review; the *tenant* still authors only data — fields, roles, packages, and (later) Px-bounded automation rules (built — see Step 9) and, later, custom objects, per D8.

Two more tiers ride the same machinery, in opposite trust directions. **The framework itself is
packages**: `AddTamSystem()` is a set of `[TamPackage]` modules — `tam.users`, `tam.roles`,
`tam.audit`, `tam.tenancy`, `tam.rules`, … — registering through the SAME `PluginBuilder`
surface the inspection vendor used, differing only on the tier axes: always active (never in
the activation table — who activates the activator), claiming their historical wire prefixes
(`users.invite`, `audit.entries`) instead of a namespace, and framework-trusted. Every admin
grid Norrservice clicks exercises the plugin seams daily — the strongest regression guard the
seams can have. And below plugins sit **tenant packages**: everything the registry accepts one
item at a time — fields, roles, rules — expressed as one JSON document and installed as one
act, with dry-run findings, atomic apply, version-guarded upgrade, and retire-on-uninstall
(data outlives configuration). A consultant carries the same `cold-chain` package to ten
customers; it lives in a repo and gets code review, because it is a file.

---
