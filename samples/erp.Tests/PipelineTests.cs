using Erp;
using Erp.Features;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Tutorial Step 11, for real: the tests exercise the CONTRACT — operations and views through
/// the full pipeline (authorization, validation, gates, transaction, merge, audit) against a
/// real database — never the plumbing. The model under test is the same value Program.cs hosts.
/// </summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;
    private Guid projectId;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Testkund AB"), new("Testgatan 1"), null, null);
            var project = Project.Create("demo", new("P-TEST-001"), customer.Id, "Testprojekt");
            db.Customers.Add(customer);
            db.Projects.Add(project);
            customerId = customer.Id.Value;
            projectId = project.Id.Value;
            return Task.CompletedTask;
        });
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Create_order_succeeds_and_returns_output()
    {
        var actor = host.Actor("demo", "orders.create");
        var response = await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Testgatan 1",
            description = "Byt packning",
        });
        response.ShouldSucceed();
        Assert.NotNull(response.Output);
    }

    [Fact]
    public async Task Unknown_customer_fails_with_the_domain_finding()
    {
        var actor = host.Actor("demo", "orders.create");
        var response = await actor.ExecuteAsync("orders.create", new
        {
            customerId = Guid.NewGuid(),
            orderType = "service",
            workAddress = "x",
            description = "y",
        });
        response.ShouldFailWith("orders.invalid-customer");
    }

    [Fact]
    public async Task Missing_permission_is_denied_by_the_pipeline()
    {
        var actor = host.Actor("demo", "orders.read");   // read, not create
        var response = await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "x",
            description = "y",
        });
        response.ShouldBeDenied();
    }

    [Fact]
    public async Task Completing_a_work_order_publishes_the_event_contract()
    {
        var dispatcher = host.Actor("demo",
            "work-orders.create", "work-orders.schedule", "work-orders.start",
            "work-orders.start-all", "work-orders.complete", "work-orders.complete-all");

        var created = await dispatcher.ExecuteAsync("work-orders.create", new
        {
            projectId,
            title = "Testarbete",
            description = "Kontraktstest",
            location = "Testgatan 1",
        });
        var workOrderId = created.Output<CreateWorkOrder.Output>().WorkOrderId.Value;

        // Scheduling validates the assignee against real memberships — seed one.
        var accountId = Guid.NewGuid();
        await host.SeedAsync("demo", db =>
        {
            db.Add(new Tam.EntityFrameworkCore.AccountEntity
                { Id = accountId, Email = "tech@test", DisplayName = "Test Tech" });
            db.Add(new Tam.EntityFrameworkCore.TenantMembershipEntity
                { Id = Guid.NewGuid(), TenantId = "demo", AccountId = accountId });
            return Task.CompletedTask;
        });
        (await dispatcher.ExecuteAsync("work-orders.schedule", new
            { workOrderId, scheduledDate = "2026-08-01", assigneeActorId = accountId.ToString() }))
            .ShouldSucceed();
        (await dispatcher.ExecuteAsync("work-orders.start", new { workOrderId })).ShouldSucceed();

        var completed = await dispatcher.ExecuteAsync("work-orders.complete", new { workOrderId });
        completed.ShouldSucceed().ShouldPublish("work-order-completed");
    }

    [Fact]
    public async Task Own_scope_holds_without_the_widening_atom()
    {
        // Two technicians book time; each sees only their own entries through the view.
        var woActor = host.Actor("demo", "work-orders.create");
        var created = await woActor.ExecuteAsync("work-orders.create", new
            { projectId, title = "T", description = "D", location = "L" });
        var workOrderId = created.Output<CreateWorkOrder.Output>().WorkOrderId.Value;

        var tekla = host.ActorWithId("demo", Guid.NewGuid().ToString(), "time.book", "time.read");
        var didrik = host.ActorWithId("demo", Guid.NewGuid().ToString(), "time.book", "time.read", "time.read-all");
        (await tekla.ExecuteAsync("time.book", new
            { workOrderId, date = "2026-07-16", hours = 2, hourlyRate = 900 })).ShouldSucceed();
        (await didrik.ExecuteAsync("time.book", new
            { workOrderId, date = "2026-07-16", hours = 1, hourlyRate = 950 })).ShouldSucceed();

        var teklaSees = (await tekla.QueryAsync("time.list")).ShouldSucceed();
        Assert.Equal(1, teklaSees.Total);                       // own scope: hers only
        var didrikSees = (await didrik.QueryAsync("time.list")).ShouldSucceed();
        Assert.Equal(2, didrikSees.Total);                      // read-all widens
    }

    [Fact]
    public async Task Concurrent_same_field_edits_conflict_structurally()
    {
        var actor = host.Actor("demo", "projects.create", "projects.edit");
        var created = await actor.ExecuteAsync("projects.create", new
            { customerId, number = "P-TEST-002", name = "Original" });
        created.ShouldSucceed();
        var id = created.Output<CreateProject.Output>().ProjectId.Value;

        // User B moved Name to "Theirs"; user A edits from the stale base "Original".
        (await actor.ExecuteAsync("projects.edit-details", new
            { projectId = id, name = new { original = "Original", value = "Theirs" } })).ShouldSucceed();
        var stale = await actor.ExecuteAsync("projects.edit-details", new
            { projectId = id, name = new { original = "Original", value = "Mine" } });
        stale.ShouldConflictOn("name");
    }
}
