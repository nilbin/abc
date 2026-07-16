namespace Tam;

// Framework-composed pages (docs/32): a PAGE becomes a declared composition in the model — a
// grid plus an optional RECORD surface (detail view + edit form + slots) — rendered entirely
// by the client library. registerPage() remains the escape hatch for genuinely custom UX; a
// declared page is how the standard list-and-detail shape stops being hand-written React.
// Pages are the HOST's (like nav layout and slots); plugins reach them through slots and
// grid-action contributions, never by declaring pages.

/// <summary>A declared page: one grid, optionally a record surface its rows open into.</summary>
public sealed record PageDefinition(string Id, string GridId, RecordDefinition? Record);

/// <summary>The record surface of a page: the detail VIEW fetched by <see cref="ContextKey"/>
/// (filled from the clicked row's id), an optional edit FORM (fields prefilled from same-named
/// detail fields — the row-action convention, declared here), an optional detail field used in
/// the title, and the SLOTS whose panels render below (docs/31 D-X4).</summary>
public sealed record RecordDefinition(
    string DetailViewId,
    string ContextKey,
    string? FormId,
    string? TitleField,
    IReadOnlyList<string> SlotIds);

public sealed class PageBuilder
{
    private string? gridId;
    private RecordDefinition? record;

    public PageBuilder Grid(string id)
    {
        gridId = id;
        return this;
    }

    public PageBuilder Record(Action<RecordBuilder> configure)
    {
        var builder = new RecordBuilder();
        configure(builder);
        record = builder.Build();
        return this;
    }

    internal PageDefinition Build(string id) =>
        new(id, gridId ?? throw new InvalidOperationException(
            $"PAGE001: page '{id}' declares no grid."), record);
}

public sealed class RecordBuilder
{
    private string? detailViewId;
    private string? contextKey;
    private string? formId;
    private string? titleField;
    private readonly List<string> slotIds = [];

    /// <summary>The detail view and the query field the clicked row's id fills.</summary>
    public RecordBuilder Detail(string viewId, string key)
    {
        detailViewId = viewId;
        contextKey = Naming.Camel(key);
        return this;
    }

    public RecordBuilder Form(string formId)
    {
        this.formId = formId;
        return this;
    }

    /// <summary>A detail result field shown in the record title (e.g. the order number).</summary>
    public RecordBuilder Title(string detailField)
    {
        titleField = Naming.Camel(detailField);
        return this;
    }

    public RecordBuilder Slot(string slotId)
    {
        slotIds.Add(slotId);
        return this;
    }

    internal RecordDefinition Build() => new(
        detailViewId ?? throw new InvalidOperationException("PAGE001: record declares no detail view."),
        contextKey ?? "id",
        formId, titleField, slotIds);
}
