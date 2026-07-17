# 21 — Localization

## The rule

**No display text in code. Ever.** Labels, finding messages, descriptions, enum display names, section titles, action names — anything a human reads is authored in per-culture resource files and referenced from code by key. The enforcement that exists today is `L10N001` (a key the model references but the default culture lacks is a build error, analyzer + Build()-time gate); a literal-in-display-position analyzer (`L10N000`) is designed, not built — the convention is held by the L10N001 gate plus review.

A corollary: **English is not special.** The application's default culture is configuration (`sv`, `en`, whatever the product needs). The compiler requires 100% key coverage in the default culture — whichever it is — and reports coverage gaps per additional culture. There is no implicit English fallback baked into code, because there is no text in code.

## Where text lives

Per-culture JSON resource files, per module, merged at build time into per-culture **catalogs** that ship as manifest artifacts:

```
Product.Application/locales/
  sv.json
  en.json
```

```jsonc
// locales/sv.json
{
  "labels.description": "Arbetsbeskrivning",
  "orders.already-completed": "Ordern är redan slutförd.",
  "validation.too-long": "{label} får innehålla högst {max} tecken.",
  "enums.completed": "Slutförd"
}
```

The build fails if a key referenced in code is missing from the default culture (`L10N001`) — the gate lists every missing key at once. A non-default-culture completeness report (`L10N002`) is designed, not built.

## Keys by convention, authored once

The framework's inference principle applies to text: keys are derived, not declared. Keys are **flat and global** — derived from the member or value name alone, never namespaced per entity or operation:

| Text for | Convention key | Authored where |
| --- | --- | --- |
| Field / column label | `labels.{kebab(member)}` — e.g. `labels.requested-date` | Once per member *name*, model-wide |
| Output field (operation results are surface too) | same `labels.{kebab(member)}` — no operation prefix | Once per member name, shared with inputs/columns |
| Enum member | `enums.{kebab(value)}` — e.g. `enums.completed` | Once per value name, model-wide |
| Finding message | the finding **code** is the key | Once per code |
| Operation title | `operations.{id}.title` — e.g. `operations.orders.create.title` | Once per operation |
| Nav node | `nav.{id}` | Once per node |
| Tenant/packaged field | `ext.{key}` / `ext.{pluginId}.{key}` (merged from the registry / plugin catalogs) | Registry / plugin |

Because the label key derives from the member name, `CreateOrder.Input.Description`, `Order.Description` and the grid column all display `labels.description`'s text with zero authoring — sharing across the model is the default, not inheritance from an entity. `[LabelKey("...")]` overrides the *key* where the derived one would be wrong (`CustomerId` → `labels.customer`); there is no attribute that takes text.

## Findings carry args, not prose

```csharp
public sealed record Finding(
    string Code,
    FindingSeverity Severity,
    IReadOnlyDictionary<string, object?> Args,   // structured parameters
    IReadOnlyList<FieldPath> Targets,
    bool BlocksSubmission,
    string? Message);   // resolved at the boundary in the request culture — never authored in code
```

Factories declare code and severity only; parameters travel structurally:

```csharp
public static class OrderErrors
{
    public static readonly FindingFactory AlreadyCompleted =
        Finding.Error("orders.already-completed");
    public static readonly FindingFactory TooLong =
        Finding.Error("validation.too-long");       // template uses {label}, {max}
}

return OrderErrors.TooLong.With(("max", 40));
```

Message templates use a constrained ICU MessageFormat subset: placeholders, plural, select. No arbitrary logic in templates.

## Who resolves text, and when

- **The server resolves `Message`** in the request culture for every response, using the compiled catalogs. Headless callers — agents, integrations, logs — always receive final human-readable text alongside the stable code.
- **Clients also receive catalogs** (per-culture manifest artifacts, cached by revision) and render locally from `code` + `Args`. This is not optional polish: portable derivations run client-side and must produce localized findings *without a server round-trip*, including offline on mobile. A locally rendered message wins over the server's when the client has the catalog; the server's `Message` is the guaranteed fallback.
- **Culture resolution order:** explicit request parameter → user preference → tenant default → application default. The envelope carries the resolved culture; audit records it.
- **Values are formatted by semantic type, per culture**, at the rendering edge (dates, money, numbers) — never pre-formatted into message strings on the server beyond the template's own arg formatting.

## The tenant layer

- The tenant overlay may **relabel any key per culture** (tenant terminology: "Order" → "Ärende"), validated like every overlay change.
- Tenant custom fields already carry `LocalizedText` for labels and descriptions ([15-extensibility.md](15-extensibility.md)); the registry requires entries for every culture the tenant has enabled — `EXT006` at definition time, the registry twin of `L10N001`.
- MCP schemas resolve descriptions in the connection's culture (default: tenant default), so agents read the same words the tenant's humans do.

## Diagnostics

```
L10N000: Hardcoded display text in a display position.            (error — designed, not built)
L10N001: Key "orders.already-completed" missing in default
         culture "sv".                                             (error)
L10N002: Culture "en" is missing 14 keys (report attached).       (warning — designed, not built)
L10N003: Key "orders.legacy-hint" is defined but never referenced. (info — designed, not built)
EXT006:  Custom field lacks a label for enabled culture "en".     (registry, error)
```

## Non-goals

No machine translation, no runtime translation-editing UI (tenant label overrides are the escape hatch), no grammatical-inflection engine beyond the ICU subset. Translation files are ordinary reviewable artifacts in the repository — they flow through pull requests like code, because they are product surface.

## Operational note: catalogs are copied at BUILD time

`locales/*.json` reach the output directory on build. A `dotnet run --no-build` after editing
a catalog runs against the OLD copies — the Build()-time gate then reports keys you can see in
the source file, which looks like the fix "didn't take" (an M3 RTFM finding). Rebuild, or drop
`--no-build`, after locale edits.
