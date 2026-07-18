using Erp;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// docs/22 action catalog: a firing rule can DO something beyond blocking — set a registered
/// extension field on the operation's target row, or publish an event — executed in the
/// operation's own transaction, never blocking the operation itself.
/// </summary>
public sealed class RuleActionTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private TestActor<ErpDbContext> admin = null!;
    private Guid order;
    private Guid otherOrder;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        admin = host.Actor("demo",
            "rules.manage", "extensions.manage",
            "orders.edit", "orders.edit-all", "orders.read", "orders.read-all");
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Acme"), new("Road 1"), null, null);
            db.Customers.Add(customer);
            var project = Project.Create("demo", new("P-1"), customer.Id, "P");
            db.Projects.Add(project);
            var o1 = Order.Create("demo", new("O-1"), customer.Id, OrderType.Project,
                project.Id, customer.VisitAddress, new("First"), null, null);
            var o2 = Order.Create("demo", new("O-2"), customer.Id, OrderType.Project,
                project.Id, customer.VisitAddress, new("Second"), null, null);
            db.Orders.AddRange(o1, o2);
            order = o1.Id.Value;
            otherOrder = o2.Id.Value;
            return Task.CompletedTask;
        });

        // The tenant defines the extension field the action will write — the admin's real path.
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "order",
            key = "reviewFlag",
            type = "boolean",
            labels = new Dictionary<string, string> { ["sv"] = "Granskas", ["en"] = "Review" },
        })).ShouldSucceed();
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<Tam.OperationResponse> DefineAsync(string name, string action) =>
        admin.ExecuteAsync("rules.define", new
        {
            name,
            onOperation = "orders.set-priority",
            condition = """{"t":"bin","op":"eq","l":{"t":"field","f":"priority"},"r":{"t":"const","v":"urgent"}}""",
            messages = new Dictionary<string, string>(),
            action,
        });

    private Task<ExtensionData> ExtensionsOf(Guid id) =>
        host.QueryDbAsync("demo", db => db.Orders
            .Where(o => o.Id == new OrderId(id)).Select(o => o.Extensions).SingleAsync());

    [Fact]
    public async Task Set_field_action_writes_the_target_rows_extension_in_the_same_commit()
    {
        (await DefineAsync("flag-urgent", """{"type":"set-field","field":"ext.reviewFlag","value":true}"""))
            .ShouldSucceed();

        (await admin.ExecuteAsync("orders.set-priority",
            new { orderId = order, priority = "urgent" })).ShouldSucceed();
        Assert.Equal(true, (await ExtensionsOf(order)).Raw("reviewFlag"));

        (await admin.ExecuteAsync("orders.set-priority",
            new { orderId = otherOrder, priority = "low" })).ShouldSucceed();
        Assert.Null((await ExtensionsOf(otherOrder)).Raw("reviewFlag"));
    }

    [Fact]
    public async Task Publish_event_action_lands_an_outbox_row_with_the_derived_type()
    {
        (await DefineAsync("urgent-alert", """{"type":"publish-event"}""")).ShouldSucceed();

        (await admin.ExecuteAsync("orders.set-priority",
            new { orderId = order, priority = "urgent" })).ShouldSucceed();
        var events = await host.QueryDbAsync("demo", db =>
            db.Set<Tam.EntityFrameworkCore.OutboxRecord>().IgnoreQueryFilters()
                .Where(x => x.EventType == "rules.urgent-alert").CountAsync());
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Set_field_on_an_unregistered_field_is_rejected_at_define()
    {
        (await DefineAsync("bad-field", """{"type":"set-field","field":"ext.nope","value":true}"""))
            .ShouldFailWith("rules.invalid-action", onField: "action");
    }

    [Fact]
    public async Task Unknown_action_type_is_rejected_at_define()
    {
        (await DefineAsync("bad-type", """{"type":"send-email"}"""))
            .ShouldFailWith("rules.invalid-action", onField: "action");
    }

    [Fact]
    public async Task Set_field_targeting_a_plugin_owned_read_only_field_is_rejected_at_define()
    {
        // Review round 5, F1: the wire channel refuses ReadOnly extension fields (plugin-owned
        // state, docs/31 D-X2); the rule action path must too, or a rules.manage admin could
        // overwrite plugin state through someone else's operation. Here we prove the
        // value-validation arm: an out-of-options value is refused.
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "order",
            key = "riskBand",
            type = "selection",
            options = new[] { "low", "high" },
            labels = new Dictionary<string, string> { ["sv"] = "Risk", ["en"] = "Risk" },
        })).ShouldSucceed();

        (await DefineAsync("bad-option", """{"type":"set-field","field":"ext.riskBand","value":"extreme"}"""))
            .ShouldFailWith("rules.invalid-action", onField: "action");
        (await DefineAsync("ok-option", """{"type":"set-field","field":"ext.riskBand","value":"high"}"""))
            .ShouldSucceed();
    }
}
