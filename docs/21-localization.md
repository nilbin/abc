# 21 — Localization

## The rule

**No display text in code. Ever.** Labels, finding messages, descriptions, enum display names, section titles, action names — anything a human reads is authored in per-culture resource files and referenced from code by key. An analyzer enforces it: a string literal in a display-text position is build error `L10N000`.

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
  "orders.order.description": "Arbetsbeskrivning",
  "orders.already-completed": "Ordern är redan slutförd.",
  "validation.too-long": "{label} får innehålla högst {max} tecken.",
  "enums.order-status.completed": "Slutförd"
}
```

The build fails if a key referenced in code is missing from the default culture (`L10N001`). Missing keys in other cultures are warnings with a completeness report (`L10N002`) — translation debt is visible in CI, not discovered by users.

## Keys by convention, authored once

The framework's inference principle applies to text: keys are derived, not declared.

| Text for | Convention key | Authored where |
| --- | --- | --- |
| Semantic value type | `types.order-description` | Once per type |
| Entity member | `orders.order.requested-date` | Once per member |
| Enum member | `enums.order-status.completed` | Once per value |
| Finding message | the finding **code** is the key | Once per code |
| Operation title/description | `operations.orders.create.title` | Once per operation |
| View column | inherits entity member / value type key | usually nothing |

Operation inputs and view results **inherit** label keys from the matching entity member or semantic value type — so `CreateOrder.Input.Description` and the grid column both display `orders.order.description`'s text with zero authoring. `[LabelKey("...")]` overrides the *key* (for sharing or divergence); there is no attribute that takes text.

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
L10N000: Hardcoded display text in a display position.            (error)
L10N001: Key "orders.already-completed" missing in default
         culture "sv".                                             (error)
L10N002: Culture "en" is missing 14 keys (report attached).       (warning)
L10N003: Key "orders.legacy-hint" is defined but never referenced. (info)
EXT006:  Custom field lacks a label for enabled culture "en".     (registry, error)
```

## Non-goals

No machine translation, no runtime translation-editing UI (tenant label overrides are the escape hatch), no grammatical-inflection engine beyond the ICU subset. Translation files are ordinary reviewable artifacts in the repository — they flow through pull requests like code, because they are product surface.
