namespace Tam;

// Framework-composed pages (docs/32): a PAGE is an ordered list of sections — grids and slots —
// plus an optional RECORD surface (itself an ordered list of form/slot sections) that rows of
// the page's FIRST grid open into. Declaration order IS layout order: "position hints" are the
// structure itself, not string annotations. registerPage() remains the escape hatch for
// genuinely custom UX. Pages are the HOST's (like nav layout and slots); plugins reach them
// through slots and grid-action contributions, never by declaring pages.

/// <summary>One page-level section: a grid, or a slot (page-level slots carry no record
/// context — their panels render unbound, e.g. dashboard-style plugin widgets).</summary>
public sealed record PageSection(string Kind, string Id, string? HeadingKey = null)
{
    public const string GridKind = "grid";
    public const string SlotKind = "slot";
}

/// <summary>One record-level section: the edit form, or a slot bound to the record context.</summary>
public sealed record RecordSection(string Kind, string Id)
{
    public const string FormKind = "form";
    public const string SlotKind = "slot";
}

public sealed record PageDefinition(
    string Id, IReadOnlyList<PageSection> Sections, RecordDefinition? Record)
{
    /// <summary>Owning plugin id, or null for host pages (review round 4: a plugin's own
    /// aggregate deserves a record surface — EXISTENCE is the plugin's, placement stays the
    /// host's/tenant's through the nav suggestion machinery).</summary>
    public string? Plugin { get; init; }

    /// <summary>The grid whose rows open the record surface — the FIRST grid section.</summary>
    public string? PrimaryGridId =>
        Sections.FirstOrDefault(s => s.Kind == PageSection.GridKind)?.Id;
}

/// <summary>The record surface: the detail VIEW fetched by <see cref="ContextKey"/> (filled
/// from the clicked row's id), an optional detail field for the title, and ORDERED sections —
/// form(s) prefilled from same-named detail fields, slots receiving the record context.</summary>
public sealed record RecordDefinition(
    string DetailViewId,
    string ContextKey,
    string? TitleField,
    IReadOnlyList<RecordSection> Sections);

public sealed class PageBuilder
{
    private readonly List<PageSection> sections = [];
    private RecordDefinition? record;

    /// <summary>A grid section. The FIRST grid's rows open the record surface (if declared);
    /// later grids are additional listings. Declaration order is layout order. A page with
    /// several sections labels them via <paramref name="heading"/> — a locale KEY (docs/34 M6;
    /// L10N001-gated), never text; a single-section page usually needs none.</summary>
    public PageBuilder Grid(string id, string? heading = null)
    {
        sections.Add(new PageSection(PageSection.GridKind, id, heading));
        return this;
    }

    /// <summary>A page-level slot section: plugin panels without record context.</summary>
    public PageBuilder Slot(string slotId, string? heading = null)
    {
        sections.Add(new PageSection(PageSection.SlotKind, slotId, heading));
        return this;
    }

    public PageBuilder Record(Action<RecordBuilder> configure)
    {
        var builder = new RecordBuilder();
        configure(builder);
        record = builder.Build();
        return this;
    }

    internal PageDefinition Build(string id)
    {
        var page = new PageDefinition(id, sections, record);
        if (page.PrimaryGridId is null)
            throw new InvalidOperationException($"PAGE001: page '{id}' declares no grid.");
        return page;
    }
}

public sealed class RecordBuilder
{
    private string? detailViewId;
    private string? contextKey;
    private string? titleField;
    private readonly List<RecordSection> sections = [];

    /// <summary>The detail view and the query field the clicked row's id fills.</summary>
    public RecordBuilder Detail(string viewId, string key)
    {
        detailViewId = viewId;
        contextKey = Naming.Camel(key);
        return this;
    }

    /// <summary>A form section, prefilled from same-named detail fields. Declaration order is
    /// layout order — a slot declared before the form renders above it.</summary>
    public RecordBuilder Form(string formId)
    {
        sections.Add(new RecordSection(RecordSection.FormKind, formId));
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
        sections.Add(new RecordSection(RecordSection.SlotKind, slotId));
        return this;
    }

    internal RecordDefinition Build() => new(
        detailViewId ?? throw new InvalidOperationException("PAGE001: record declares no detail view."),
        contextKey ?? "id",
        titleField, sections);
}
