using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Inspect v2 (docs/34 M6) through the full pipeline: a tenant admin defines checklist
/// templates keyed on order type; creating an order instantiates the matching templates
/// via the order-created event; the plugin's gate blocks orders.complete while a MANDATORY
/// checklist has open lines; checking every line off unblocks it; non-mandatory checklists
/// never block. Activation is real: the tests click the same plugins.activate the tenant
/// admin would.
///
/// The harness does not run the outbox loop (step 11 scopes dispatch timing out), so this
/// class starts the production OutboxDispatcher itself and drains the outbox before
/// asserting on subscriber-produced state — the same at-least-once path the web host runs.
/// </summary>
public sealed class InspectV2Tests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Tam.AspNetCore.OutboxDispatcher dispatcher = null!;
    private Guid customerId;
    private TestActor<ErpDbContext> admin = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Testkund AB"), new("Testgatan 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            // The plan entitles the plugin (docs/24); activation below is the admin's click.
            db.Add(new Tam.EntityFrameworkCore.SubscriptionEntity
            {
                TenantId = "demo",
                Plan = "standard",
                Seats = 10,
                EntitlementsJson = """["inspect"]""",
                Status = "active",
            });
            return Task.CompletedTask;
        });

        // The production dispatcher over the harness's service provider, reached through
        // the seeded context's options (the harness exposes no DI seam of its own).
        var scopeFactory = await host.QueryDbAsync("demo", db =>
        {
            var provider = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(db)
                .FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()!
                .ApplicationServiceProvider!;
            return Task.FromResult(provider.GetRequiredService<IServiceScopeFactory>());
        });
        dispatcher = new Tam.AspNetCore.OutboxDispatcher(
            scopeFactory, sp => sp.GetRequiredService<ErpDbContext>(), host.Model);
        await dispatcher.StartAsync(CancellationToken.None);

        admin = host.Actor("demo",
            "plugins.manage",
            "orders.create", "orders.complete", "orders.complete-all",
            "inspect.templates.manage", "inspect.templates.read",
            "inspect.checklists.manage", "inspect.checklists.read");
        (await admin.ExecuteAsync("plugins.activate", new { pluginId = "inspect" }))
            .ShouldSucceed();
    }

    public async Task DisposeAsync()
    {
        await dispatcher.StopAsync(CancellationToken.None);
        dispatcher.Dispose();
        await host.DisposeAsync();
    }

    /// <summary>Deterministic wait: subscriber effects are at-least-once and async — assert
    /// only after every outbox row is dispatched (or dead) within the timeout.</summary>
    private async Task DrainOutboxAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var pending = await host.QueryDbAsync("demo", db =>
                db.Set<Tam.EntityFrameworkCore.OutboxRecord>().IgnoreQueryFilters()
                    .CountAsync(x => x.DispatchedAtIso == null && x.DeadAtIso == null));
            if (pending == 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The outbox did not drain within 15 s.");
    }

    private async Task<Guid> DefineTemplateAsync(
        string name, string orderType, bool mandatory, params string[] items)
    {
        var defined = await admin.ExecuteAsync("inspect.templates.define",
            new { name, orderType, mandatory });
        var templateId = defined.ShouldSucceed()
            .Output<Inspect.DefineTemplate.Output>().TemplateId;
        foreach (var text in items)
            (await admin.ExecuteAsync("inspect.templates.add-item",
                new { templateId, text })).ShouldSucceed();
        return templateId;
    }

    private async Task<Guid> CreateServiceOrderAsync()
    {
        var created = await admin.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Testgatan 1",
            description = "Byt packning",
        });
        created.ShouldSucceed().ShouldPublish("order-created");
        await DrainOutboxAsync();   // let the instantiation subscriber commit its work
        return created.Output<CreateOrder.Output>().OrderId.Value;
    }

    private Task<List<Inspect.ChecklistItem>> ItemsOfAsync(Guid orderId) =>
        host.QueryDbAsync("demo", db => db.Set<Inspect.ChecklistItem>()
            .Where(x => x.OrderId == orderId).OrderBy(x => x.Position).ToListAsync());

    [Fact]
    public async Task Creating_an_order_instantiates_matching_templates_with_items()
    {
        await DefineTemplateAsync("Säkerhetskontroll", "service", mandatory: true,
            "Bryt spänningen", "Kontrollera tryckkärl");
        await DefineTemplateAsync("Projektuppstart", "project", mandatory: true,
            "Boka startmöte");   // wrong order type — must NOT instantiate

        var orderId = await CreateServiceOrderAsync();

        var checklists = (await admin.QueryAsync("inspect.checklists.list",
            new Dictionary<string, string?> { ["orderId"] = orderId.ToString() }!))
            .ShouldSucceed();
        Assert.Equal(1, checklists.Total);

        var items = await ItemsOfAsync(orderId);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.False(item.Done));
    }

    [Fact]
    public async Task Mandatory_checklist_blocks_completion_until_every_line_is_checked()
    {
        await DefineTemplateAsync("Säkerhetskontroll", "service", mandatory: true,
            "Bryt spänningen", "Kontrollera tryckkärl");
        var orderId = await CreateServiceOrderAsync();

        (await admin.ExecuteAsync("orders.complete", new { orderId }))
            .ShouldFailWith("inspect.checklist-incomplete");

        var items = await ItemsOfAsync(orderId);
        foreach (var item in items)
            (await admin.ExecuteAsync("inspect.items.check", new { itemId = item.Id }))
                .ShouldSucceed();

        (await admin.ExecuteAsync("orders.complete", new { orderId })).ShouldSucceed();
    }

    [Fact]
    public async Task Checking_the_last_line_passes_the_checklist_and_unchecking_reopens_it()
    {
        await DefineTemplateAsync("Säkerhetskontroll", "service", mandatory: true,
            "Bryt spänningen", "Kontrollera tryckkärl");
        var orderId = await CreateServiceOrderAsync();
        var items = await ItemsOfAsync(orderId);

        (await admin.ExecuteAsync("inspect.items.check", new { itemId = items[0].Id }))
            .ShouldSucceed();
        var last = await admin.ExecuteAsync("inspect.items.check", new { itemId = items[1].Id });
        last.ShouldSucceed().ShouldPublish("inspect.checklist-passed");

        // The correction path: un-checking re-opens the checklist AND the gate.
        (await admin.ExecuteAsync("inspect.items.uncheck", new { itemId = items[1].Id }))
            .ShouldSucceed();
        (await admin.ExecuteAsync("orders.complete", new { orderId }))
            .ShouldFailWith("inspect.checklist-incomplete");
    }

    [Fact]
    public async Task Passing_a_checklist_with_open_lines_is_refused()
    {
        await DefineTemplateAsync("Säkerhetskontroll", "service", mandatory: true,
            "Bryt spänningen");
        var orderId = await CreateServiceOrderAsync();

        var checklistId = (await ItemsOfAsync(orderId)).Single().ChecklistId;
        (await admin.ExecuteAsync("inspect.checklists.pass", new { checklistId }))
            .ShouldFailWith("inspect.items-open");
    }

    [Fact]
    public async Task Non_mandatory_checklists_never_block_completion()
    {
        await DefineTemplateAsync("Överlämning", "service", mandatory: false,
            "Gå igenom arbetet", "Lämna protokoll");
        var orderId = await CreateServiceOrderAsync();

        // The checklist exists with every line open — and completion sails through.
        var checklists = (await admin.QueryAsync("inspect.checklists.list",
            new Dictionary<string, string?> { ["orderId"] = orderId.ToString() }!))
            .ShouldSucceed();
        Assert.Equal(1, checklists.Total);
        (await admin.ExecuteAsync("orders.complete", new { orderId })).ShouldSucceed();
    }

    [Fact]
    public async Task Retired_templates_stop_instantiating()
    {
        var templateId = await DefineTemplateAsync("Säkerhetskontroll", "service",
            mandatory: true, "Bryt spänningen");
        (await admin.ExecuteAsync("inspect.templates.retire", new { templateId }))
            .ShouldSucceed();

        var orderId = await CreateServiceOrderAsync();
        var checklists = (await admin.QueryAsync("inspect.checklists.list",
            new Dictionary<string, string?> { ["orderId"] = orderId.ToString() }!))
            .ShouldSucceed();
        Assert.Equal(0, checklists.Total);
        (await admin.ExecuteAsync("orders.complete", new { orderId })).ShouldSucceed();
    }
}
