using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
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
    public async Task The_merged_machine_runs_schedule_start_complete_and_publishes()
    {
        var dispatcher = host.Actor("demo",
            "orders.create", "orders.schedule", "orders.start",
            "orders.start-all", "orders.complete", "orders.complete-all");

        var created = await dispatcher.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Testgatan 1",
            description = "Kontraktstest",
        });
        var orderId = created.Output<CreateOrder.Output>().OrderId.Value;

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
        (await dispatcher.ExecuteAsync("orders.schedule", new
            { orderId, scheduledDate = "2026-08-01", assigneeActorId = accountId.ToString() }))
            .ShouldSucceed();
        (await dispatcher.ExecuteAsync("orders.start", new { orderId })).ShouldSucceed();

        var completed = await dispatcher.ExecuteAsync("orders.complete", new { orderId });
        completed.ShouldSucceed().ShouldPublish("order-completed");
    }

    [Fact]
    public async Task Own_scope_holds_without_the_widening_atom()
    {
        // Two technicians book time; each sees only their own entries through the view.
        var orderActor = host.Actor("demo", "orders.create");
        var created = await orderActor.ExecuteAsync("orders.create", new
            { customerId, orderType = "service", workAddress = "L", description = "D" });
        var orderId = created.Output<CreateOrder.Output>().OrderId.Value;

        var tekla = host.ActorWithId("demo", Guid.NewGuid().ToString(), "time.book", "time.read");
        var didrik = host.ActorWithId("demo", Guid.NewGuid().ToString(), "time.book", "time.read", "time.read-all");
        (await tekla.ExecuteAsync("time.book", new
            { orderId, date = "2026-07-16", hours = 2, hourlyRate = 900 })).ShouldSucceed();
        (await didrik.ExecuteAsync("time.book", new
            { orderId, date = "2026-07-16", hours = 1, hourlyRate = 950 })).ShouldSucceed();

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
        Assert.Equal("stale", stale.Conflicts!.Single(c => c.Field == "name").Reason);
    }

    [Fact]
    public async Task Keep_current_rebases_the_base_to_the_server_value_so_the_retry_is_a_no_op()
    {
        // The client's "keep current" resolution (Sol re-review round 11, F1) rebases the field's base to
        // the server's CURRENT value AND sets the submitted value to it — {original: theirs, value:
        // theirs}. That is Original == Value, so the retry is a true no-op: no conflict (even though the
        // base moved out from under the form) and no write. The stale shape below is the contrast — same
        // field, but a real edit off the old base, which conflicts.
        var actor = host.Actor("demo", "projects.create", "projects.edit");
        var created = await actor.ExecuteAsync("projects.create", new
            { customerId, number = "P-TEST-004", name = "Original" });
        var id = created.Output<CreateProject.Output>().ProjectId.Value;

        // A concurrent writer moves Name to "Theirs".
        (await actor.ExecuteAsync("projects.edit-details", new
            { projectId = id, name = new { original = "Original", value = "Theirs" } })).ShouldSucceed();

        // "Keep current" — original rebased to the server value, value equal to it: a no-op, no conflict.
        (await actor.ExecuteAsync("projects.edit-details", new
            { projectId = id, name = new { original = "Theirs", value = "Theirs" } })).ShouldSucceed();

        // The value the concurrent writer set is what remains — keep-current wrote nothing over it.
        var name = await host.QueryDbAsync("demo", db =>
            db.Projects.Where(p => p.Id == new ProjectId(id)).Select(p => p.Name).SingleAsync());
        Assert.Equal("Theirs", name);
    }

    [Fact]
    public async Task A_null_merge_base_is_reported_as_an_ordinary_stale_conflict()
    {
        var actor = host.Actor("demo", "projects.create", "projects.edit");
        var created = await actor.ExecuteAsync("projects.create", new
            { customerId, number = "P-TEST-003", name = "Original" });
        var id = created.Output<CreateProject.Output>().ProjectId.Value;

        // {value} with no original deserializes Original to null. Under the complete-state contract a
        // null Original is a VALID merge base (Sol re-review round 9, F4) — and JSON cannot distinguish
        // an explicit null from an omitted property — so a mismatch is an ordinary stale conflict, not
        // a special "original-missing" reason. That unsound inference was removed.
        var missing = await actor.ExecuteAsync("projects.edit-details", new
            { projectId = id, name = new { value = "Mine" } });
        missing.ShouldConflictOn("name");
        Assert.Equal("stale", missing.Conflicts!.Single(c => c.Field == "name").Reason);
    }
}
