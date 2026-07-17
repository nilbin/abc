using System.Linq.Expressions;

namespace Tam;

public enum DependentValuePolicy
{
    Preserve,
    Clear,
    ClearIfInvalid,
    RecomputeIfUntouched,
    RequireConfirmation,
}

public sealed record FormFieldConfig(
    string WireName,
    string? Renderer,
    Px? VisibleWhen,
    Px? RequiredWhen,
    DependentValuePolicy SuggestionPolicy)
{
    /// <summary>docs/34 M5: a computed-display seat — rendered disabled, fed by suggestions;
    /// the server's value stays authoritative. Ends the editable-input-the-server-ignores wart.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>docs/34 M6: a plugin's string field offers the HOST's enum values as options —
    /// referenced by the enum's kebab wire name, verified at Build (ENUM001). Keeps the field an
    /// opaque wire string (no CLR coupling) while ending the typo-defines-a-dead-template wart.</summary>
    public string? OptionsFromEnum { get; init; }

    /// <summary>The DependsOn twin for VALUES (docs/05): when any of these sibling fields is
    /// edited, this field's value is discarded — it referenced the old sibling's world (a project
    /// of the previous customer, a condition over the previous trigger's fields). One hop only:
    /// a mechanical reset never triggers further resets, so mutual pairs are cycle-safe — which
    /// is also how "exactly one of A/B" is declared: each resets on the other.</summary>
    public IReadOnlyList<string>? ResetOn { get; init; }
}

public sealed record FormDefinition(
    string Id,
    string OperationId,
    IReadOnlyList<FormFieldConfig> Fields,
    bool IncludeExtensions,
    string? BasedOn)
{
    /// <summary>Owning plugin id, or null for host-defined forms (docs/22).</summary>
    public string? Plugin { get; init; }
}

public sealed class FormBuilder<TInput>
{
    private readonly List<FormFieldConfig> fields = [];
    private bool includeExtensions;

    public FormFieldBuilder<TInput> Field<TValue>(Expression<Func<TInput, TValue>> member)
    {
        var name = Naming.Camel(MemberName(member));
        var config = new FormFieldConfig(name, null, null, null, DependentValuePolicy.RecomputeIfUntouched);
        fields.Add(config);
        return new FormFieldBuilder<TInput>(this, fields.Count - 1);
    }

    public FormBuilder<TInput> Extensions()
    {
        includeExtensions = true;
        return this;
    }

    internal void Update(int index, Func<FormFieldConfig, FormFieldConfig> mutate) =>
        fields[index] = mutate(fields[index]);

    internal FormDefinition Build(string id, string operationId, string? basedOn = null) =>
        new(id, operationId, fields, includeExtensions, basedOn);

    internal static string MemberName<TValue>(Expression<Func<TInput, TValue>> member) =>
        member.Body switch
        {
            MemberExpression m => m.Member.Name,
            UnaryExpression { Operand: MemberExpression m } => m.Member.Name,
            _ => throw new ArgumentException("Expected a simple member access.", nameof(member)),
        };
}

public sealed class FormFieldBuilder<TInput>(FormBuilder<TInput> form, int index)
{
    public FormFieldBuilder<TInput> Renderer(string key)
    {
        form.Update(index, f => f with { Renderer = key });
        return this;
    }

    public FormFieldBuilder<TInput> VisibleWhen(Expression<Func<TInput, bool>> rule)
    {
        form.Update(index, f => f with { VisibleWhen = PortableExpression.Lower(rule) });
        return this;
    }

    public FormFieldBuilder<TInput> RequiredWhen(Expression<Func<TInput, bool>> rule)
    {
        form.Update(index, f => f with { RequiredWhen = PortableExpression.Lower(rule) });
        return this;
    }

    public FormFieldBuilder<TInput> OnSourceChange(DependentValuePolicy policy)
    {
        form.Update(index, f => f with { SuggestionPolicy = policy });
        return this;
    }

    /// <summary>Renders disabled — a display seat for derivation-computed values.</summary>
    public FormFieldBuilder<TInput> ReadOnly()
    {
        form.Update(index, f => f with { ReadOnly = true });
        return this;
    }

    /// <summary>Offers another module's enum values as this string field's options — by the
    /// enum's kebab wire name (e.g. "order-type"), so a plugin form can present the host's
    /// vocabulary without referencing its CLR types. Verified at Build (ENUM001).</summary>
    public FormFieldBuilder<TInput> EnumOptions(string enumWireName)
    {
        form.Update(index, f => f with { OptionsFromEnum = enumWireName });
        return this;
    }

    /// <summary>Discards this field's value whenever one of <paramref name="members"/> is edited
    /// (docs/05) — the DependsOn twin for values: a value authored against a sibling's old state
    /// must not survive that sibling changing. One hop, cycle-safe; a mutual pair declares
    /// "exactly one of the two".</summary>
    public FormFieldBuilder<TInput> ResetOn(params Expression<Func<TInput, object?>>[] members)
    {
        var names = members.Select(m => Naming.Camel(FormBuilder<TInput>.MemberName(m))).ToList();
        form.Update(index, f => f with
        {
            ResetOn = f.ResetOn is { } existing ? [.. existing, .. names] : names,
        });
        return this;
    }
}

public sealed record GridDefinition(
    string Id,
    string ViewId,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> RowActions,
    IReadOnlyList<string> ToolbarActions,
    bool IncludeExtensions)
{
    /// <summary>Owning plugin id, or null for host-defined grids (docs/22).</summary>
    public string? Plugin { get; init; }

    /// <summary>Row actions that OPEN the operation's form prefilled from the row (docs/32) —
    /// the edit affordance — where RowActions execute immediately (complete, retire). The row's
    /// result fields prefill same-named form fields, so an upsert operation edits in place.</summary>
    public IReadOnlyList<string> RowForms { get; init; } = [];
}

public sealed class GridBuilder<TResult>
{
    private readonly List<string> columns = [];
    private readonly List<string> rowActions = [];
    private readonly List<string> rowForms = [];
    private readonly List<string> toolbarActions = [];
    private bool includeExtensions;

    public GridBuilder<TResult> Column<TValue>(Expression<Func<TResult, TValue>> member)
    {
        columns.Add(Naming.Camel(FormBuilder<TResult>.MemberName(member)));
        return this;
    }

    public GridBuilder<TResult> RowAction(string operationId)
    {
        rowActions.Add(operationId);
        return this;
    }

    /// <summary>An EDIT row action: opens <paramref name="operationId"/>'s form prefilled from
    /// the row (same-named fields), instead of executing immediately like RowAction.</summary>
    public GridBuilder<TResult> RowForm(string operationId)
    {
        rowForms.Add(operationId);
        return this;
    }

    public GridBuilder<TResult> ToolbarAction(string operationId)
    {
        toolbarActions.Add(operationId);
        return this;
    }

    public GridBuilder<TResult> Extensions()
    {
        includeExtensions = true;
        return this;
    }

    internal GridDefinition Build(string id, string viewId) =>
        new(id, viewId, columns, rowActions, toolbarActions, includeExtensions)
        {
            RowForms = rowForms,
        };
}
