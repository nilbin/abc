using Tam.AspNetCore.Postgres;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// The RLS backstop's provider-independent halves (docs/33): the policy statements and the
/// scope fingerprint. The database-side behavior (policies enforcing, interceptor sync,
/// fail-closed unset settings) is proven on a real PostgreSQL in the wire run — STATUS records
/// what it proved.
/// </summary>
public class RlsTests
{
    private sealed class Scope : ITenantScopeContext
    {
        public string? CurrentTenantId { get; init; }
        public IReadOnlyList<string> TenantReadSet { get; init; } = [];
        public bool CrossTenantScope { get; init; }
    }

    [Fact]
    public void The_policy_mirrors_the_EF_filter()
    {
        var sql = TamRls.PolicySql("public", "orders", "TenantId");

        // Enable + FORCE (the app role owns its tables) + drop/create for idempotency.
        Assert.Contains("ALTER TABLE \"public\".\"orders\" ENABLE ROW LEVEL SECURITY", sql);
        Assert.Contains("ALTER TABLE \"public\".\"orders\" FORCE ROW LEVEL SECURITY", sql);
        Assert.Contains($"DROP POLICY IF EXISTS {TamRls.PolicyName}", sql);

        // The three arms: cross-tenant sentinel, current tenant, subtree read set.
        Assert.Contains($"current_setting('{TamRls.TenantSetting}', true) = '*'", sql);
        Assert.Contains($"\"TenantId\" = current_setting('{TamRls.TenantSetting}', true)", sql);
        Assert.Contains($"nullif(current_setting('{TamRls.ReadSetSetting}', true), '')", sql);
    }

    [Fact]
    public void The_fingerprint_tracks_tenant_and_read_set()
    {
        // Null scope = the explicit cross-tenant contract → the sentinel (docs/33 D-R2).
        Assert.Equal("*|", TamRls.Fingerprint(new Scope()));
        Assert.Equal("demo|", TamRls.Fingerprint(new Scope { CurrentTenantId = "demo" }));
        Assert.Equal("demo|nord,syd", TamRls.Fingerprint(
            new Scope { CurrentTenantId = "demo", TenantReadSet = ["nord", "syd"] }));

        // Widening the read set MUST move the fingerprint, or the settings would go stale
        // mid-request when the view executor widens for a SubtreeRead view.
        Assert.NotEqual(
            TamRls.Fingerprint(new Scope { CurrentTenantId = "demo" }),
            TamRls.Fingerprint(new Scope { CurrentTenantId = "demo", TenantReadSet = ["nord"] }));

        // The sticky escalation (docs/33 D-R8) wins over the pinned tenant.
        Assert.Equal("*|", TamRls.Fingerprint(
            new Scope { CurrentTenantId = "demo", CrossTenantScope = true }));
    }

    [Fact]
    public void The_cross_tenant_tag_counts_only_in_the_leading_comment_block()
    {
        // The tag EF emits for .AcrossTenants() (docs/33 D-R7).
        Assert.True(TamRls.HasCrossTenantTag(
            "-- tam:cross-tenant\n\nSELECT m.\"TenantId\" FROM memberships AS m"));
        // EF renders each tag as a comment FOLLOWED BY A BLANK LINE — a second tag must still
        // be found past the separator (review-round-4 F4: the naive scan stopped at the blank).
        Assert.True(TamRls.HasCrossTenantTag(
            "-- some other tag\n\n-- tam:cross-tenant\n\nSELECT 1"));

        // A tag-shaped string past the comment block — a literal, a value, a column — never
        // escalates the command.
        Assert.False(TamRls.HasCrossTenantTag(
            "SELECT * FROM t WHERE name = 'tam:cross-tenant'"));
        Assert.False(TamRls.HasCrossTenantTag(
            "-- harmless tag\nSELECT * FROM t WHERE name = '-- tam:cross-tenant'"));
        Assert.False(TamRls.HasCrossTenantTag("SELECT 1"));
    }
}
