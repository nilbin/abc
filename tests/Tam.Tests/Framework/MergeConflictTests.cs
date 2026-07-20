using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// The three-way merge and field-level concurrency (docs/07, docs/40) are FRAMEWORK behavior — a
/// concurrent same-field edit conflicts, a "keep current" retry ({original: current, value: current}) is a
/// true no-op, and a null merge base is an ordinary stale conflict. Exercised through the framework-owned
/// Widget entity so the merge contract is verified independently of any sample app.
/// </summary>
public sealed class MergeConflictTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;

    public async Task InitializeAsync() =>
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<Guid> CreateWidgetNamed(TestActor<WidgetDbContext> actor, string name)
    {
        var created = await actor.ExecuteAsync("widgets.create", new { name });
        created.ShouldSucceed();
        return created.Output<CreateWidget.Output>().WidgetId;
    }

    [Fact]
    public async Task Concurrent_same_field_edits_conflict_structurally()
    {
        var actor = host.Actor("demo", "widgets.create", "widgets.edit");
        var id = await CreateWidgetNamed(actor, "Original");

        // User B moved Name to "Theirs"; user A edits from the stale base "Original".
        (await actor.ExecuteAsync("widgets.edit", new
            { widgetId = id, name = new { original = "Original", value = "Theirs" } })).ShouldSucceed();
        var stale = await actor.ExecuteAsync("widgets.edit", new
            { widgetId = id, name = new { original = "Original", value = "Mine" } });
        stale.ShouldConflictOn("name");
        Assert.Equal("stale", stale.Conflicts!.Single(c => c.Field == "name").Reason);
    }

    [Fact]
    public async Task Keep_current_rebases_the_base_to_the_server_value_so_the_retry_is_a_no_op()
    {
        // The client's "keep current" resolution (Sol re-review round 11, F1) rebases the field's base to
        // the server's CURRENT value AND sets the submitted value to it — {original: theirs, value:
        // theirs}. That is Original == Value, so the retry is a true no-op: no conflict (even though the
        // base moved out from under the form) and no write.
        var actor = host.Actor("demo", "widgets.create", "widgets.edit");
        var id = await CreateWidgetNamed(actor, "Original");

        // A concurrent writer moves Name to "Theirs".
        (await actor.ExecuteAsync("widgets.edit", new
            { widgetId = id, name = new { original = "Original", value = "Theirs" } })).ShouldSucceed();

        // "Keep current" — original rebased to the server value, value equal to it: a no-op, no conflict.
        (await actor.ExecuteAsync("widgets.edit", new
            { widgetId = id, name = new { original = "Theirs", value = "Theirs" } })).ShouldSucceed();

        // The value the concurrent writer set is what remains — keep-current wrote nothing over it.
        var name = await host.QueryDbAsync("demo", db =>
            db.Widgets.Where(w => w.Id == id).Select(w => w.Name).SingleAsync());
        Assert.Equal("Theirs", name);
    }

    [Fact]
    public async Task A_null_merge_base_is_reported_as_an_ordinary_stale_conflict()
    {
        var actor = host.Actor("demo", "widgets.create", "widgets.edit");
        var id = await CreateWidgetNamed(actor, "Original");

        // {value} with no original deserializes Original to null. Under the complete-state contract a null
        // Original is a VALID merge base (Sol re-review round 9, F4) — and JSON cannot distinguish an
        // explicit null from an omitted property — so a mismatch is an ordinary stale conflict, not a
        // special "original-missing" reason. That unsound inference was removed.
        var missing = await actor.ExecuteAsync("widgets.edit", new
            { widgetId = id, name = new { value = "Mine" } });
        missing.ShouldConflictOn("name");
        Assert.Equal("stale", missing.Conflicts!.Single(c => c.Field == "name").Reason);
    }
}
