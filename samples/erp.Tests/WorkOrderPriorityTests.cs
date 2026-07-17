using Erp;
using Erp.Features;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Work-order priority (docs/02: the enum is the semantic; docs/21: members localize as
/// enums.{kebab(value)}) plus the tenant policy from docs/22 automation rules: "an URGENT
/// work order cannot be scheduled more than 7 days out". The rule is authored exactly as a
/// tenant admin would — through rules.define, as Px condition DATA over the schedule
/// operation's input (scheduledDate) and its target row (row.priority) — never compiled code.
/// </summary>
public sealed class WorkOrderPriorityTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid projectId;
    private Guid accountId;
    private TestActor<ErpDbContext> dispatcher = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        accountId = Guid.NewGuid();
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Testkund AB"), new("Testgatan 1"), null, null);
            var project = Project.Create("demo", new("P-TEST-001"), customer.Id, "Testprojekt");
            db.Customers.Add(customer);
            db.Projects.Add(project);
            projectId = project.Id.Value;
            // Scheduling validates the assignee against real memberships — seed one.
            db.Add(new Tam.EntityFrameworkCore.AccountEntity
                { Id = accountId, Email = "tech@test", DisplayName = "Test Tech" });
            db.Add(new Tam.EntityFrameworkCore.TenantMembershipEntity
                { Id = Guid.NewGuid(), TenantId = "demo", AccountId = accountId });
            return Task.CompletedTask;
        });
        dispatcher = host.Actor("demo",
            "work-orders.create", "work-orders.read", "work-orders.read-all",
            "work-orders.edit", "work-orders.edit-all",
            "work-orders.schedule", "work-orders.start", "work-orders.start-all");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<Guid> CreateAsync(string? priority = null)
    {
        var response = priority is null
            ? await dispatcher.ExecuteAsync("work-orders.create", new
                { projectId, title = "T", description = "D", location = "L" })
            : await dispatcher.ExecuteAsync("work-orders.create", new
                { projectId, title = "T", description = "D", location = "L", priority });
        return response.ShouldSucceed().Output<CreateWorkOrder.Output>().WorkOrderId.Value;
    }

    private async Task<WorkOrderDetail.Result> DetailAsync(Guid workOrderId)
    {
        var detail = (await dispatcher.QueryAsync("work-orders.detail",
            new Dictionary<string, string?> { ["workOrderId"] = workOrderId.ToString() }!))
            .ShouldSucceed();
        return Assert.IsType<WorkOrderDetail.Result>(Assert.Single(detail.Rows));
    }

    // ---- Round-trip: create → detail → grid ----

    [Fact]
    public async Task Priority_defaults_to_normal_when_omitted()
    {
        var id = await CreateAsync();
        Assert.Equal(WorkOrderPriority.Normal, (await DetailAsync(id)).Priority);
    }

    [Fact]
    public async Task Priority_round_trips_through_create_detail_and_grid()
    {
        var id = await CreateAsync("urgent");
        Assert.Equal(WorkOrderPriority.Urgent, (await DetailAsync(id)).Priority);

        var grid = (await dispatcher.QueryAsync("work-orders.list")).ShouldSucceed();
        var row = Assert.IsType<WorkOrderList.Result>(Assert.Single(grid.Rows));
        Assert.Equal(WorkOrderPriority.Urgent, row.Priority);

        // The declared filter capability, on the wire: enum equality by wire string.
        var urgentOnly = (await dispatcher.QueryAsync("work-orders.list",
            new Dictionary<string, string?> { ["priority"] = "urgent" }!)).ShouldSucceed();
        Assert.Equal(1, urgentOnly.Total);
        var none = (await dispatcher.QueryAsync("work-orders.list",
            new Dictionary<string, string?> { ["priority"] = "low" }!)).ShouldSucceed();
        Assert.Equal(0, none.Total);
    }

    // ---- The set-priority intent (EDIT001 keeps enums off the generic change-set) ----

    [Fact]
    public async Task Priority_changes_through_the_intent_while_editable()
    {
        var id = await CreateAsync();
        (await dispatcher.ExecuteAsync("work-orders.set-priority", new
            { workOrderId = id, priority = "urgent" })).ShouldSucceed();
        Assert.Equal(WorkOrderPriority.Urgent, (await DetailAsync(id)).Priority);
    }

    [Fact]
    public async Task Priority_freezes_once_work_starts()
    {
        var id = await CreateAsync();
        (await dispatcher.ExecuteAsync("work-orders.schedule", new
        {
            workOrderId = id,
            scheduledDate = Iso(2),
            assigneeActorId = accountId.ToString(),
        })).ShouldSucceed();
        (await dispatcher.ExecuteAsync("work-orders.start", new { workOrderId = id }))
            .ShouldSucceed();

        (await dispatcher.ExecuteAsync("work-orders.set-priority", new
            { workOrderId = id, priority = "low" }))
            .ShouldFailWith("work-orders.not-editable");
    }

    // ---- The tenant policy: urgent work orders schedule within 7 days ----

    private static string Iso(int daysFromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysFromToday).ToString("yyyy-MM-dd");

    /// <summary>The condition a tenant admin authors in the rule builder: Px AST data.
    /// The date input rides the operation (`scheduledDate`); priority is not on the thin
    /// schedule intent at all — it resolves from the TARGET row (`row.priority`, docs/22
    /// row.* — enums compare as wire strings). Px carries no relative-date node, so the
    /// cutoff is a constant the author computes at definition time.</summary>
    private static string UrgentWindowCondition(string cutoffIso) =>
        "{\"t\":\"bin\",\"op\":\"and\"," +
        "\"l\":{\"t\":\"bin\",\"op\":\"eq\",\"l\":{\"t\":\"field\",\"f\":\"row.priority\"},\"r\":{\"t\":\"const\",\"v\":\"urgent\"}}," +
        "\"r\":{\"t\":\"bin\",\"op\":\"gt\",\"l\":{\"t\":\"field\",\"f\":\"scheduledDate\"},\"r\":{\"t\":\"const\",\"v\":\"" + cutoffIso + "\"}}}";

    private async Task DefineUrgentWindowRuleAsync()
    {
        var admin = host.Actor("demo", "rules.manage");
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "urgent-schedule-window",
            onOperation = "work-orders.schedule",
            condition = UrgentWindowCondition(Iso(7)),
            // Rule messages are tenant DATA per culture (RUL003 gates the default culture),
            // the registry twin of the locale catalogs — no display text in code.
            messages = new Dictionary<string, string>
            {
                ["sv"] = "Akuta arbetsordrar måste planeras inom 7 dagar.",
                ["en"] = "Urgent work orders must be scheduled within 7 days.",
            },
            targetField = "scheduledDate",
        })).ShouldSucceed();
    }

    private Task<Tam.OperationResponse> ScheduleAsync(Guid workOrderId, int daysOut) =>
        dispatcher.ExecuteAsync("work-orders.schedule", new
        {
            workOrderId,
            scheduledDate = Iso(daysOut),
            assigneeActorId = accountId.ToString(),
        });

    [Fact]
    public async Task Rule_blocks_an_urgent_order_scheduled_more_than_seven_days_out()
    {
        await DefineUrgentWindowRuleAsync();
        var id = await CreateAsync("urgent");

        var blocked = await ScheduleAsync(id, daysOut: 10);
        blocked.ShouldFailWith("rules.urgent-schedule-window");
        Assert.Equal(WorkOrderPriority.Urgent, (await DetailAsync(id)).Priority);
        Assert.Equal(WorkOrderStatus.Draft, (await DetailAsync(id)).Status);
    }

    [Fact]
    public async Task Rule_passes_an_urgent_order_scheduled_within_the_window()
    {
        await DefineUrgentWindowRuleAsync();
        var id = await CreateAsync("urgent");

        (await ScheduleAsync(id, daysOut: 3)).ShouldSucceed();
        Assert.Equal(WorkOrderStatus.Scheduled, (await DetailAsync(id)).Status);
    }

    [Fact]
    public async Task Rule_leaves_normal_orders_free_to_schedule_far_out()
    {
        await DefineUrgentWindowRuleAsync();
        var id = await CreateAsync();   // Normal by default

        (await ScheduleAsync(id, daysOut: 30)).ShouldSucceed();
        Assert.Equal(WorkOrderStatus.Scheduled, (await DetailAsync(id)).Status);
    }

    [Fact]
    public async Task Retiring_the_rule_ungates_far_urgent_scheduling()
    {
        await DefineUrgentWindowRuleAsync();
        var id = await CreateAsync("urgent");
        (await ScheduleAsync(id, daysOut: 10)).ShouldFailWith("rules.urgent-schedule-window");

        (await host.Actor("demo", "rules.manage").ExecuteAsync("rules.retire", new
            { name = "urgent-schedule-window" })).ShouldSucceed();
        (await ScheduleAsync(id, daysOut: 10)).ShouldSucceed();
    }
}
