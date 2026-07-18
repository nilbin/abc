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

/// <summary>One record-level section: the edit form, a GRID of related records (its query
/// params bound to the open record's detail fields), or a slot bound to the record context.
/// Grid binds mirror slot binds — a query param filled from a named record field — so a child
/// listing (this work order's time entries) filters mechanically off the parent, no new view.</summary>
public sealed record RecordSection(string Kind, string Id, IReadOnlyList<RecordBind>? Bind = null)
{
    public const string FormKind = "form";
    public const string GridKind = "grid";
    public const string SlotKind = "slot";
}

/// <summary>A grid section's query param filled from a record (detail-view) field — or,
/// when <see cref="EntityKey"/> is set, from the record's own IDENTITY as a canonical
/// EntityRef string ("order:{recordKey}", docs/35): the documents-tab shape, where the child
/// listing filters on WHICH RECORD, not on one of its fields.</summary>
public sealed record RecordBind(string Param, string? Field, string? EntityKey = null);

/// <summary>
/// A group of record sections shown under one tab (docs/32 record tabs). Declaration order is
/// tab order; each tab's sections keep declaration-order layout, exactly like a page. A record
/// is ALWAYS tabs on the wire — flat authoring normalizes to one implicit tab with a null
/// <see cref="HeadingKey"/> (the client shows tab chrome only when there is something to
/// choose). A tab carrying <see cref="SlotId"/> is a PANEL-TABS marker: the client expands it
/// into one tab per contributing PLUGIN (docs/31 D-X4) — the host opts the slot in once and
/// never names a plugin.
/// </summary>
public sealed record RecordTab(
    string Id, string? HeadingKey, IReadOnlyList<RecordSection> Sections, string? SlotId = null);

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

/// <summary>How substantial a record surface is (docs/32): a MODAL record is a quick edit over
/// the grid; a PAGE record is a workspace — a routed full surface replacing the grid. The model
/// states the semantic; the client maps it to presentation. Derived from structure when not
/// declared: tabs or child grids make a workspace.</summary>
public enum RecordDisplay
{
    Modal,
    Page,
}

/// <summary>The record surface: the detail VIEW fetched by <see cref="ContextKey"/> (filled
/// from the clicked row's id), an optional detail field for the title, the display semantic,
/// and ORDERED tabs of form/grid/slot sections. ONE representation: flat authoring already
/// normalized into a single implicit tab at Build(), so every consumer walks tabs and nothing
/// else.</summary>
public sealed record RecordDefinition(
    string DetailViewId,
    string ContextKey,
    string? TitleField,
    RecordDisplay Display,
    IReadOnlyList<RecordTab> Tabs)
{
    /// <summary>Every section across every tab — the walk verification and derivation use.</summary>
    public IEnumerable<RecordSection> AllSections => Tabs.SelectMany(t => t.Sections);
}

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

/// <summary>Collects record sections (form / grid / slot) in declaration order — the shared
/// surface a flat record and a record TAB both author against.</summary>
public sealed class RecordSectionBuilder
{
    internal List<RecordSection> Sections { get; } = [];

    /// <summary>A form section, prefilled from same-named detail fields.</summary>
    public RecordSectionBuilder Form(string formId)
    {
        Sections.Add(new RecordSection(RecordSection.FormKind, formId));
        return this;
    }

    /// <summary>A grid of related records: its query params are filled from the open record's
    /// detail fields (<c>bind.Query("workOrderNumber", fromRecord: "number")</c>), so the child
    /// listing filters mechanically. With no bind the grid shows unfiltered.</summary>
    public RecordSectionBuilder Grid(string gridId, Action<RecordBindBuilder>? bind = null)
    {
        var builder = new RecordBindBuilder();
        bind?.Invoke(builder);
        Sections.Add(new RecordSection(RecordSection.GridKind, gridId,
            builder.Binds.Count > 0 ? builder.Binds : null));
        return this;
    }

    /// <summary>A slot bound to the record context — plugin detail panels land here.</summary>
    public RecordSectionBuilder Slot(string slotId)
    {
        Sections.Add(new RecordSection(RecordSection.SlotKind, slotId));
        return this;
    }
}

/// <summary>Declared-bind author for a record grid section: query param ← record field.</summary>
public sealed class RecordBindBuilder
{
    internal List<RecordBind> Binds { get; } = [];

    public RecordBindBuilder Query(string param, string fromRecord)
    {
        Binds.Add(new RecordBind(Naming.Camel(param), Naming.Camel(fromRecord)));
        return this;
    }

    /// <summary>Fills a grid query param with the open record's EntityRef ("order:{id}",
    /// docs/35) — the documents-tab bind: every document attached to THIS record.</summary>
    public RecordBindBuilder QueryEntityRef(string param, string entityKey)
    {
        Binds.Add(new RecordBind(Naming.Camel(param), null, entityKey));
        return this;
    }
}

public sealed class RecordBuilder
{
    /// <summary>The implicit tab id flat authoring normalizes into.</summary>
    public const string ImplicitTabId = "record";

    private string? detailViewId;
    private string? contextKey;
    private string? titleField;
    private readonly RecordSectionBuilder flat = new();
    private readonly List<RecordTab> tabs = [];

    /// <summary>The detail view and the query field the clicked row's id fills.</summary>
    public RecordBuilder Detail(string viewId, string key)
    {
        detailViewId = viewId;
        contextKey = Naming.Camel(key);
        return this;
    }

    /// <summary>A flat form section (untabbed record). Declaration order is layout order.</summary>
    public RecordBuilder Form(string formId) { flat.Form(formId); return this; }

    /// <summary>A flat grid section (untabbed record), bound to the record.</summary>
    public RecordBuilder Grid(string gridId, Action<RecordBindBuilder>? bind = null)
    {
        flat.Grid(gridId, bind);
        return this;
    }

    /// <summary>A flat slot section (untabbed record).</summary>
    public RecordBuilder Slot(string slotId) { flat.Slot(slotId); return this; }

    /// <summary>A TAB grouping record sections (docs/32 record tabs). Declaration order is tab
    /// order. <paramref name="heading"/> is a locale KEY (L10N001-gated). A record uses tabs OR
    /// flat sections, not both.</summary>
    public RecordBuilder Tab(string id, string heading, Action<RecordSectionBuilder> configure)
    {
        var builder = new RecordSectionBuilder();
        configure(builder);
        tabs.Add(new RecordTab(Naming.Camel(id), heading, builder.Sections));
        return this;
    }

    /// <summary>
    /// Expands a record-context slot into ONE TAB PER CONTRIBUTING PLUGIN (docs/31 D-X4): each
    /// active plugin's panels become its own tab, headed by the panel's headingKey or the
    /// plugin's title — the host opts the slot in once and never names, counts, or labels the
    /// plugins. The tab-per-plugin expansion happens client-side from the manifest's panel
    /// list, so activation filtering applies per tenant with no server work.
    /// </summary>
    public RecordBuilder PanelTabs(string slotId)
    {
        tabs.Add(new RecordTab(slotId, null, [], SlotId: slotId));
        return this;
    }

    /// <summary>A detail result field shown in the record title (e.g. the order number).</summary>
    public RecordBuilder Title(string detailField)
    {
        titleField = Naming.Camel(detailField);
        return this;
    }

    /// <summary>Overrides the display semantic. Undeclared, it DERIVES from structure: a
    /// record with several tabs or any child grid is a workspace (page); a plain form/slot
    /// record is a quick edit (modal).</summary>
    public RecordBuilder Display(RecordDisplay display)
    {
        this.display = display;
        return this;
    }

    private RecordDisplay? display;

    internal RecordDefinition Build()
    {
        if (flat.Sections.Count > 0 && tabs.Count > 0)
            throw new InvalidOperationException(
                "PAGE001: a record declares flat sections OR tabs, not both.");
        // ONE representation downstream: flat sections become the single implicit tab (no
        // heading — the client shows tab chrome only when a choice exists).
        IReadOnlyList<RecordTab> normalized = flat.Sections.Count > 0
            ? [new RecordTab(ImplicitTabId, null, flat.Sections)]
            : tabs;
        var duplicate = normalized.GroupBy(t => t.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"PAGE001: duplicate record tab id '{duplicate.Key}' (ids are camelCased — 'Details' and 'details' collide).");
        var derived = normalized.Count > 1
            || normalized.SelectMany(t => t.Sections).Any(s => s.Kind == RecordSection.GridKind)
            ? RecordDisplay.Page
            : RecordDisplay.Modal;
        return new RecordDefinition(
            detailViewId ?? throw new InvalidOperationException("PAGE001: record declares no detail view."),
            contextKey ?? "id",
            titleField, display ?? derived, normalized);
    }
}
