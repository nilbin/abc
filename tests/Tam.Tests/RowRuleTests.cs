using System.Text.Json;
using Erp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// docs/22 row.* increment: rule conditions over the operation's TARGET row. These tests
/// drive RuleEvaluator directly with stored rules whose RowEntityKey/RowIdField are what
/// rules.define resolves — the definition-time arms (RUL004, RUL002-over-row) are wire-
/// verified by the rulesgate suite through the real operation.
/// </summary>
public class RowRuleTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<ErpDbContext> _options;
    private readonly Guid _bigProject;
    private readonly Guid _smallProject;

    public RowRuleTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite(_conn)
            .Options;

        using var db = new ErpDbContext(_options, new TenantScope());
        db.Database.EnsureCreated();
        var customer = Customer.Create("a", new("Acme"), new("Road 1"), null, null);
        db.Customers.Add(customer);
        var big = Project.Create("a", new("P-1"), customer.Id, "Big", budget: 200_000m);
        var small = Project.Create("a", new("P-2"), customer.Id, "Small", budget: 5_000m);
        db.Projects.AddRange(big, small);
        db.SaveChanges();
        _bigProject = big.Id.Value;
        _smallProject = small.Id.Value;
    }

    private ErpDbContext Db(string tenant) => new(_options, new TenantScope { Current = tenant });

    private static AutomationRuleEntity Rule(string condition) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "a",
        Name = "no-big-close",
        OnOperation = "projects.close",
        ConditionJson = condition,
        MessagesJson = """{"en":"Big projects need a controller sign-off"}""",
        RowEntityKey = "project",
        RowIdField = "projectId",
    };

    // Money serializes as a plain number and enums as their wire strings (TamJson) — the
    // row namespace is wire-identical, so this condition reads exactly like an input one.
    private const string BigOpenProject = """
        { "t": "bin", "op": "and",
          "l": { "t": "bin", "op": "gt",
                 "l": { "t": "field", "f": "row.budget" }, "r": { "t": "const", "v": 100000 } },
          "r": { "t": "bin", "op": "eq",
                 "l": { "t": "field", "f": "row.status" }, "r": { "t": "const", "v": "open" } } }
        """;

    private static OperationContext Context(string tenant) => new()
    {
        Actor = new Actor("t", "test", new HashSet<string>()),
        TenantId = new TenantId(tenant),
        Source = InvocationSource.Internal,
        Culture = "en",
        CorrelationId = "test",
        Services = null!,
    };

    private async Task<List<Finding>> Evaluate(string tenant, Guid projectId, string condition)
    {
        await using var db = Db(tenant);
        db.Add(Rule(condition));
        await db.SaveChangesAsync();
        var body = JsonSerializer.SerializeToElement(
            new { projectId = projectId.ToString() }, TamJson.Options);
        return await RuleEvaluator.EvaluateAsync(
            db, "projects.close", body, Context(tenant), "en", CancellationToken.None);
    }

    [Fact]
    public async Task Row_condition_fires_on_the_matching_row()
    {
        var findings = await Evaluate("a", _bigProject, BigOpenProject);
        var finding = Assert.Single(findings);
        Assert.Equal("rules.no-big-close", finding.Code);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task Row_condition_stays_quiet_below_the_threshold()
    {
        Assert.Empty(await Evaluate("a", _smallProject, BigOpenProject));
    }

    [Fact]
    public async Task Missing_row_means_the_rule_does_not_fire()
    {
        // The pipeline's own not-found follows — a rule must neither block nor warn here.
        Assert.Empty(await Evaluate("a", Guid.NewGuid(), BigOpenProject));
    }

    [Fact]
    public async Task Another_tenants_row_is_invisible_to_the_rule()
    {
        // FindAsync bypasses the global filter; the evaluator re-checks the boundary
        // explicitly (same lesson as the packaged writer) — tenant b sees no row, no fire.
        Assert.Empty(await Evaluate("b", _bigProject, BigOpenProject));
    }

    public void Dispose() => _conn.Dispose();
}
