using Erp;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// docs/22 effect-triggered rules: a rule triggered by a DOMAIN EVENT, evaluated on the outbox
/// dispatch path, whose set-field action writes an extension on the entity the payload
/// references. "When an order is created, flag project-type orders for review."
/// </summary>
public sealed class EventRuleTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private TestActor<ErpDbContext> admin = null!;
    private Guid customerId;
    private Guid projectId;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        admin = host.Actor("demo",
            "rules.manage", "extensions.manage", "orders.create", "orders.read", "orders.read-all");
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Acme"), new("Road 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            var project = Project.Create("demo", new("P-1"), customer.Id, "P");
            db.Projects.Add(project);
            projectId = project.Id.Value;
            return Task.CompletedTask;
        });
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "order",
            key = "needsReview",
            type = "boolean",
            labels = new Dictionary<string, string> { ["sv"] = "Granskas", ["en"] = "Review" },
        })).ShouldSucceed();
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Event_rule_sets_a_field_on_the_referenced_row_when_the_event_fires()
    {
        // Trigger: the order-created event; condition on the payload's orderType; action:
        // flag the order (the payload's orderId row). project orders get flagged, service don't.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "flag-project-orders",
            onOperation = "",
            onEvent = "order-created",
            condition = """{"t":"bin","op":"eq","l":{"t":"field","f":"orderType"},"r":{"t":"const","v":"project"}}""",
            messages = new Dictionary<string, string>(),
            action = """{"type":"set-field","field":"ext.needsReview","value":true}""",
        })).ShouldSucceed();

        (await admin.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "project",
            projectId,
            workAddress = "Verkstadsgatan 1",
            description = "Project order",
        })).ShouldSucceed();
        (await admin.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Verkstadsgatan 2",
            description = "Service order",
        })).ShouldSucceed();

        // The event fires on dispatch — the rule's write happens when the outbox drains.
        await host.DispatchOutboxAsync();

        var flagged = await host.QueryDbAsync("demo", db =>
            db.Orders.Where(o => o.Description == new OrderDescription("Project order"))
                .Select(o => o.Extensions).SingleAsync());
        Assert.Equal(true, flagged.Raw("needsReview"));

        var quiet = await host.QueryDbAsync("demo", db =>
            db.Orders.Where(o => o.Description == new OrderDescription("Service order"))
                .Select(o => o.Extensions).SingleAsync());
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
            onEvent = "order-created",
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
            onEvent = "rules.flag-project-orders",
            condition = """{"t":"const","v":true}""",
            messages = new Dictionary<string, string>(),
            action = """{"type":"set-field","field":"ext.needsReview","value":true}""",
        })).ShouldFailWith("rules.invalid-action", onField: "onEvent");
    }
}
