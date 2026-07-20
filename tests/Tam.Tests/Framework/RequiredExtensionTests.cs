using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// A required tenant extension field must be satisfied when a row is CREATED — even when the client sends
/// no effective extension patch (Sol re-review round 11, F2) — and a PREFILLED create value must be
/// applied even though it arrives as {original: X, value: X} (round 12, F2). These drive the whole
/// pipeline (the extension channel included) against a FRAMEWORK-owned extensible entity, so the behavior
/// is verified independently of any sample app.
/// </summary>
public sealed class RequiredExtensionTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        var admin = host.Actor("demo", "extensions.manage");
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "widget",
            key = "warrantyRef",
            type = "text",
            labels = new Dictionary<string, string> { ["sv"] = "Garantiref", ["en"] = "Warranty ref" },
            required = true,
        })).ShouldSucceed();
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "widget",
            key = "note",
            type = "text",
            labels = new Dictionary<string, string> { ["sv"] = "Notering", ["en"] = "Note" },
        })).ShouldSucceed();
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<ExtensionData> ExtensionsOf(Guid id) =>
        host.QueryDbAsync("demo", db => db.Widgets.Where(w => w.Id == id).Select(w => w.Extensions).SingleAsync());

    private object CreateBody(object? extensions = null) =>
        extensions is null ? new { name = "W" } : new { name = "W", extensions };

    [Fact]
    public async Task Create_omitting_the_extensions_channel_still_enforces_a_required_field()
    {
        var actor = host.Actor("demo", "widgets.create");
        (await actor.ExecuteAsync("widgets.create", CreateBody()))
            .ShouldFailWith("validation.required", "extensions.warrantyRef");
    }

    [Fact]
    public async Task Create_with_an_empty_change_for_the_required_field_is_rejected()
    {
        var actor = host.Actor("demo", "widgets.create");
        var body = CreateBody(new { warrantyRef = new { original = (string?)null, value = (string?)null } });
        (await actor.ExecuteAsync("widgets.create", body))
            .ShouldFailWith("validation.required", "extensions.warrantyRef");
    }

    [Fact]
    public async Task Create_supplying_the_required_field_succeeds()
    {
        var actor = host.Actor("demo", "widgets.create");
        var body = CreateBody(new { warrantyRef = new { original = (string?)null, value = "W-123" } });
        (await actor.ExecuteAsync("widgets.create", body)).ShouldSucceed();
    }

    [Fact]
    public async Task Create_persists_a_prefilled_required_field_sent_as_original_equals_value()
    {
        // A prefilled create form freezes the same value as baseline AND current, so a required field can
        // arrive as {original: W-9, value: W-9} (round 12, F2). The edit-style effective patch would drop
        // it as a no-op; a CREATE must apply it.
        var actor = host.Actor("demo", "widgets.create");
        var created = await actor.ExecuteAsync("widgets.create",
            CreateBody(new { warrantyRef = new { original = "W-9", value = "W-9" } }));
        created.ShouldSucceed();
        Assert.Equal("W-9", (await ExtensionsOf(created.Output<CreateWidget.Output>().WidgetId)).Get<string>("warrantyRef"));
    }

    [Fact]
    public async Task Create_persists_a_prefilled_optional_field_sent_as_original_equals_value()
    {
        // The optional twin: {original: N-1, value: N-1} on an OPTIONAL field has no required-active spec
        // to force processing, so before the round-13 fix the prefilled value was silently discarded.
        var actor = host.Actor("demo", "widgets.create");
        var created = await actor.ExecuteAsync("widgets.create", CreateBody(new
        {
            warrantyRef = new { original = (string?)null, value = "W-1" },
            note = new { original = "N-1", value = "N-1" },
        }));
        created.ShouldSucceed();
        Assert.Equal("N-1", (await ExtensionsOf(created.Output<CreateWidget.Output>().WidgetId)).Get<string>("note"));
    }

    [Fact]
    public async Task Editing_an_existing_row_with_the_field_unchanged_neither_re_requires_nor_conflicts()
    {
        // Required enforcement is a CREATE concern: an edit that carries the extension unchanged
        // (Original == Value) reduces to an empty effective patch, so the pipeline does no target selection
        // and no required re-check — the edit of the main field just succeeds.
        var actor = host.Actor("demo", "widgets.create", "widgets.edit");
        var created = await actor.ExecuteAsync("widgets.create",
            CreateBody(new { warrantyRef = new { original = (string?)null, value = "W-1" } }));
        created.ShouldSucceed();
        var widgetId = created.Output<CreateWidget.Output>().WidgetId;

        var edit = await actor.ExecuteAsync("widgets.edit", new
        {
            widgetId,
            name = new { original = "W", value = "W2" },
            extensions = new { warrantyRef = new { original = "W-1", value = "W-1" } },
        });
        edit.ShouldSucceed();
    }
}
