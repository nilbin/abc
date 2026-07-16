# 02 — Domain State

Normal C# entities, value objects, domain rules, and EF Core persistence.

The framework must **not** require:

- Framework base classes
- Dynamic property bags
- Generic repositories
- Separate persistence and domain models unless genuinely necessary
- A parallel semantic entity registry

> Note: tenant-extensible entities opt in to a single framework-managed, schema-validated extension container — see [15-extensibility.md](15-extensibility.md). This is deliberately not a free-form property bag: domain code cannot write arbitrary keys into it, and it exists only at explicitly declared extension points.

## Example

```csharp
public sealed class Order
{
    public OrderId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public Address WorkAddress { get; private set; }
    public OrderDescription Description { get; private set; }
    public OrderStatus Status { get; private set; }

    public void ChangeDescription(OrderDescription description)
    {
        Description = description;
    }

    public Result Complete()
    {
        if (Status == OrderStatus.Completed)
            return Errors.AlreadyCompleted;

        Status = OrderStatus.Completed;
        return Result.Success();
    }
}
```

## Semantic value types

Value types should carry intrinsic meaning where appropriate:

```csharp
[Format("email")]
public readonly record struct EmailAddress(string Value);

[Multiline]
public readonly record struct OrderDescription(string Value);
```

Display text is never authored in code ([21-localization.md](21-localization.md)): labels resolve by convention key (`types.email-address`, `types.order-description`) from per-culture resource files, and the build fails if a key is missing in the default culture.

```jsonc
// locales/sv.json
{ "types.email-address": "E-post", "types.order-description": "Arbetsbeskrivning" }
```

Semantic value types are the single authority for:

- Intrinsic shape validation (email format, max intrinsic length, money precision)
- Normalization (email casing, phone number formats, decimal scale)
- Semantic equality (used by dirty tracking and three-way merge — see [07-partial-edits.md](07-partial-edits.md))
- Default label *keys* and formatting hints (label text lives in per-culture resources — [21-localization.md](21-localization.md))

The same semantic type vocabulary is reused by runtime-defined tenant fields ([15-extensibility.md](15-extensibility.md)), so a tenant-defined "email" field validates, normalizes, and compares exactly like a compiled `EmailAddress`.

## The type carries the defaults *(BUILT — docs/34 M5)*

A wrapper type is declared **once** and every usage — input field, result column, filter,
form control, agent schema — inherits its semantics. Three attributes cover the common cases:

```csharp
[Format("money")]                                   // or just use the ready-made Tam.Money
public readonly record struct UnitPrice(decimal Value);

[LabelKey("labels.project"), Lookup("projects.lookup")]
public readonly record struct ProjectId(Guid Value);
```

- **`[Format]` on the type** picks the semantic type. `Tam.Money` ships ready-made (implicit
  `decimal` conversions both ways); an undeclared bare `decimal` is a plain number — there is
  no name sniffing.
- **`[LabelKey]` on the type** names the concept once: every `ProjectId` field labels as
  "Project" without repeating the key at each member. A member-level `[LabelKey]` still wins
  when one usage genuinely differs.
- **`[Lookup("view.id")]` on the type** says the value REFERENCES another aggregate through
  that view: every form field of this type renders a searchable picker over the view's rows
  (`id` is the value, the first string result field the label) — no per-form renderer or
  options derivation. The view must exist at `Build()` (LOOKUP001).

Resolution order everywhere: **member attribute → type attribute → convention.** When two
different wrapper types fall through to the *same* convention key (neither declares
`[LabelKey]` and their property names collide), `Build()` emits an advisory L10N005 warning
(`TamModel.Warnings`, logged at startup) — declare the key on one of the types to disambiguate.
