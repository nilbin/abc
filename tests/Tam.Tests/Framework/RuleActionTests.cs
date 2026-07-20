using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// The tam.rules action catalog is FRAMEWORK behavior: a firing rule can DO something beyond blocking —
/// set a registered extension field on the operation's target row, or publish an event — executed in the
/// operation's own transaction. Exercised through bins.set-status (target row = bin).
/// </summary>
public sealed class RuleActionTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private TestActor<WidgetDbContext> admin = null!;
    private BinId bin;
    private BinId otherBin;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        admin = host.Actor("demo", "rules.manage", "extensions.manage", "bins.manage");
        bin = new BinId(Guid.NewGuid());
        otherBin = new BinId(Guid.NewGuid());
        await host.SeedAsync("demo", db =>
        {
            db.Bins.Add(new Bin { Id = bin, TenantId = "demo", Name = "First", Status = BinStatus.Open });
            db.Bins.Add(new Bin { Id = otherBin, TenantId = "demo", Name = "Second", Status = BinStatus.Open });
            return Task.CompletedTask;
        });
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "bin",
            key = "reviewFlag",
            type = "boolean",
            labels = new Dictionary<string, string> { ["sv"] = "Granskas", ["en"] = "Review" },
        })).ShouldSucceed();
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<OperationResponse> DefineAsync(string name, string action) =>
        admin.ExecuteAsync("rules.define", new
        {
            name,
            onOperation = "bins.set-status",
            condition = """{"t":"bin","op":"eq","l":{"t":"field","f":"status"},"r":{"t":"const","v":"closed"}}""",
            messages = new Dictionary<string, string>(),
            action,
        });

    private Task<ExtensionData> ExtensionsOf(BinId id) =>
        host.QueryDbAsync("demo", db => db.Bins.Where(b => b.Id == id).Select(b => b.Extensions).SingleAsync());

    [Fact]
    public async Task Set_field_action_writes_the_target_rows_extension_in_the_same_commit()
    {
        (await DefineAsync("flag-closed", """{"type":"set-field","field":"ext.reviewFlag","value":true}"""))
            .ShouldSucceed();

        (await admin.ExecuteAsync("bins.set-status", new { binId = bin.Value, status = "closed" })).ShouldSucceed();
        Assert.Equal(true, (await ExtensionsOf(bin)).Raw("reviewFlag"));

        (await admin.ExecuteAsync("bins.set-status", new { binId = otherBin.Value, status = "open" })).ShouldSucceed();
        Assert.Null((await ExtensionsOf(otherBin)).Raw("reviewFlag"));
    }

    [Fact]
    public async Task Publish_event_action_lands_an_outbox_row_with_the_derived_type()
    {
        (await DefineAsync("closed-alert", """{"type":"publish-event"}""")).ShouldSucceed();

        (await admin.ExecuteAsync("bins.set-status", new { binId = bin.Value, status = "closed" })).ShouldSucceed();
        var events = await host.QueryDbAsync("demo", db =>
            db.Set<Tam.EntityFrameworkCore.OutboxRecord>().IgnoreQueryFilters()
                .Where(x => x.EventType == "rules.closed-alert").CountAsync());
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
    public async Task Set_field_with_an_out_of_options_value_is_rejected_at_define()
    {
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "bin",
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
