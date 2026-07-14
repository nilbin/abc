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
    DependentValuePolicy SuggestionPolicy);

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
}

public sealed class GridBuilder<TResult>
{
    private readonly List<string> columns = [];
    private readonly List<string> rowActions = [];
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
        new(id, viewId, columns, rowActions, toolbarActions, includeExtensions);
}
