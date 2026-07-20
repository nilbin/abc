using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// tam.rules effect-triggered rules are FRAMEWORK behavior: a rule triggered by a DOMAIN EVENT, evaluated
/// on the outbox dispatch path, whose set-field action writes an extension on the entity the payload
/// references. "When a Bin is created, flag Special bins for review." Exercised via the framework-owned
/// bin-created event.
/// </summary>
public sealed class EventRuleTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private TestActor<WidgetDbContext> admin = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        admin = host.Actor("demo", "rules.manage", "extensions.manage", "bins.manage");
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "bin",
            key = "needsReview",
            type = "boolean",
            labels = new Dictionary<string, string> { ["sv"] = "Granskas", ["en"] = "Review" },
        })).ShouldSucceed();
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Event_rule_sets_a_field_on_the_referenced_row_when_the_event_fires()
    {
        // Trigger: the bin-created event; condition on the payload's category; action: flag the bin (the
        // payload's binId row). Special bins get flagged, standard don't.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "flag-special-bins",
            onOperation = "",
            onEvent = "bin-created",
            condition = """{"t":"bin","op":"eq","l":{"t":"field","f":"category"},"r":{"t":"const","v":"special"}}""",
            messages = new Dictionary<string, string>(),
            action = """{"type":"set-field","field":"ext.needsReview","value":true}""",
        })).ShouldSucceed();

        (await admin.ExecuteAsync("bins.create", new
        {
            groupId = Guid.NewGuid(), name = "Special bin", category = "special",
        })).ShouldSucceed();
        (await admin.ExecuteAsync("bins.create", new
        {
            groupId = Guid.NewGuid(), name = "Standard bin", category = "standard",
        })).ShouldSucceed();

        // The event fires on dispatch — the rule's write happens when the outbox drains.
        await host.DispatchOutboxAsync();

        var flagged = await host.QueryDbAsync("demo", db =>
            db.Bins.Where(b => b.Name == "Special bin").Select(b => b.Extensions).SingleAsync());
        Assert.Equal(true, flagged.Raw("needsReview"));

        var quiet = await host.QueryDbAsync("demo", db =>
            db.Bins.Where(b => b.Name == "Standard bin").Select(b => b.Extensions).SingleAsync());
        Assert.Null(quiet.Raw("needsReview"));
    }

    [Fact]
    public async Task An_event_rule_must_carry_a_set_field_action()
    {
        // RUL007: a finding blocks nothing post-commit; publish-event could loop. Both refused.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "finding-on-event",
            onOperation = "",
            onEvent = "bin-created",
            condition = """{"t":"const","v":true}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        })).ShouldFailWith("rules.invalid-action", onField: "action");
    }

    [Fact]
    public async Task A_rule_may_not_trigger_on_a_rules_event()
    {
        // RUL006: the reserved prefix — no rule → event → rule cycles.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "loop-attempt",
            onOperation = "",
            onEvent = "rules.flag-special-bins",
            condition = """{"t":"const","v":true}""",
            messages = new Dictionary<string, string>(),
            action = """{"type":"set-field","field":"ext.needsReview","value":true}""",
        })).ShouldFailWith("rules.invalid-action", onField: "onEvent");
    }
}
