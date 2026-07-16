using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.Postgres;

/// <summary>
/// The RLS backstop (docs/33, the docs/19 D2 commitment): PostgreSQL row-level security
/// MIRRORING the EF tenant filter, so a forgotten filter or raw-SQL escape hatch cannot become
/// a cross-tenant leak on its own. The EF filter stays the mechanism; this is defense-in-depth.
/// </summary>
public static class TamRls
{
    public const string TenantSetting = "app.tenant_id";
    public const string ReadSetSetting = "app.tenant_read_set";

    /// <summary>The database mirror of a NULL <see cref="ITenantScopeContext.CurrentTenantId"/>
    /// — the framework's explicit cross-tenant scope (background loops, docs/33 D-R2).</summary>
    public const string CrossTenantSentinel = "*";

    public const string PolicyName = "tam_tenant_isolation";

    /// <summary>Registers the setting-sync interceptor. Call beside UseNpgsql; the host's
    /// DbContext must implement <see cref="ITenantScopeContext"/> (it already does for the EF
    /// filter — the interceptor reads the same contract, no extra DI).</summary>
    public static DbContextOptionsBuilder AddTamRls(this DbContextOptionsBuilder options) =>
        options.AddInterceptors(new TamRlsInterceptor());

    /// <summary>
    /// Enables + FORCES row-level security and (re)creates the tenant policy on every
    /// <see cref="ITenantScoped"/> table in the model — the same convention that applies the EF
    /// filter, so the two layers cannot drift. Idempotent; call at startup after schema
    /// creation. THROWS if the connecting role bypasses RLS (docs/33 D-R3): a backstop that
    /// silently does not apply is worse than none.
    /// </summary>
    public static async Task ProvisionAsync(DbContext db, CancellationToken ct = default)
    {
        var bypasses = await db.Database
            .SqlQueryRaw<bool>("""
                SELECT rolsuper OR rolbypassrls AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """)
            .SingleAsync(ct);
        if (bypasses)
            throw new InvalidOperationException(
                "RLS0: the connecting role is SUPERUSER or BYPASSRLS, so row-level security " +
                "would silently not apply. Run the application as an ordinary owning role " +
                "(NOSUPERUSER NOBYPASSRLS) — docs/33 D-R3.");

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)) continue;
            // The tenants REGISTRY is exempt (docs/33 D-R6): topology metadata every request
            // needs to resolve scopes at all (act-as validation, standable sets, paths). It
            // holds names and structure, never business rows.
            if (entityType.ClrType == typeof(TenantEntity)) continue;
            var table = entityType.GetTableName();
            if (table is null) continue;
            var schema = entityType.GetSchema() ?? "public";
            var column = entityType.FindProperty(nameof(ITenantScoped.TenantId))!
                .GetColumnName(Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier
                    .Table(table, entityType.GetSchema())) ?? nameof(ITenantScoped.TenantId);

            await db.Database.ExecuteSqlRawAsync(PolicySql(schema, table, column), ct);
        }
    }

    /// <summary>The per-table statements: enable + force (the app role OWNS its tables, docs/33
    /// D-R3) + one FOR ALL policy mirroring the EF filter — current tenant, subtree read set,
    /// or the explicit cross-tenant sentinel. An unset setting is NULL: fail closed. Public so
    /// hosts that provision through migration scripts can emit the same statements.</summary>
    public static string PolicySql(string schema, string table, string column) => $"""
        ALTER TABLE "{schema}"."{table}" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "{schema}"."{table}" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS {PolicyName} ON "{schema}"."{table}";
        CREATE POLICY {PolicyName} ON "{schema}"."{table}" FOR ALL USING (
            current_setting('{TenantSetting}', true) = '{CrossTenantSentinel}'
            OR "{column}" = current_setting('{TenantSetting}', true)
            OR "{column}" = ANY (string_to_array(
                   nullif(current_setting('{ReadSetSetting}', true), ''), ','))
        );
        """;

    public static string Fingerprint(ITenantScopeContext scope) =>
        scope.CrossTenantScope
            ? $"{CrossTenantSentinel}|"
            : $"{scope.CurrentTenantId ?? CrossTenantSentinel}|{string.Join(",", scope.TenantReadSet)}";

    /// <summary>True when the command carries the <see cref="TamTenantFilter.CrossTenantQueryTag"/>
    /// (docs/33 D-R7) — checked ONLY in the leading comment block EF emits for query tags, so a
    /// tag-shaped string in a value or literal can never escalate a command. EF renders EACH tag
    /// as a comment followed by a BLANK line (review-round-4 F4), so blank lines inside the
    /// header must not end the scan — only the first substantive SQL line does.</summary>
    public static bool HasCrossTenantTag(string commandText)
    {
        foreach (var line in commandText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;                 // blank separators between tags
            if (!trimmed.StartsWith("--")) return false;       // first real SQL ends the header
            if (trimmed.Contains(TamTenantFilter.CrossTenantQueryTag)) return true;
        }
        return false;
    }
}

/// <summary>
/// Keeps <c>app.tenant_id</c>/<c>app.tenant_read_set</c> true to the context's
/// <see cref="ITenantScopeContext"/> on the physical connection. One class, three EF roles
/// (docs/33): COMMAND — fingerprint-compare and <c>set_config</c> before each command;
/// CONNECTION — a (re)opened connection starts unknown (Npgsql's pool reset wipes session
/// settings); TRANSACTION — <c>set_config</c> is transactional, so rollbacks (and savepoint
/// rollbacks) revert it and the fingerprint must clear. An unapplied connection fails CLOSED:
/// <c>current_setting(..., true)</c> is NULL and the policy matches nothing.
/// </summary>
public sealed class TamRlsInterceptor
    : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private sealed class Applied { public string? Fingerprint; }

    private static readonly ConditionalWeakTable<DbConnection, Applied> State = new();

    // ---- command role: sync before execution --------------------------------------------

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Sync(command, eventData); return result; }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Sync(command, eventData); return result; }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Sync(command, eventData); return result; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    { await SyncAsync(command, eventData, cancellationToken); return result; }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    { await SyncAsync(command, eventData, cancellationToken); return result; }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    { await SyncAsync(command, eventData, cancellationToken); return result; }

    // ---- connection role: opened connections start unknown ------------------------------

    void IDbConnectionInterceptor.ConnectionOpened(
        DbConnection connection, ConnectionEndEventData eventData) =>
        Invalidate(connection);

    Task IDbConnectionInterceptor.ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken)
    { Invalidate(connection); return Task.CompletedTask; }

    // ---- transaction role: rollbacks revert set_config ----------------------------------

    void IDbTransactionInterceptor.TransactionRolledBack(
        DbTransaction transaction, TransactionEndEventData eventData) =>
        Invalidate(transaction.Connection);

    Task IDbTransactionInterceptor.TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken)
    { Invalidate(transaction.Connection); return Task.CompletedTask; }

    void IDbTransactionInterceptor.RolledBackToSavepoint(
        DbTransaction transaction, TransactionEventData eventData) =>
        Invalidate(transaction.Connection);

    Task IDbTransactionInterceptor.RolledBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData,
        CancellationToken cancellationToken)
    { Invalidate(transaction.Connection); return Task.CompletedTask; }

    // ---- the sync itself -----------------------------------------------------------------

    private static void Invalidate(DbConnection? connection)
    {
        if (connection is not null && State.TryGetValue(connection, out var applied))
            applied.Fingerprint = null;
    }

    private static (DbCommand Set, Applied Applied, string Desired)? Prepare(
        DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context is not ITenantScopeContext scope || command.Connection is null)
            return null;
        // A tagged command (docs/33 D-R7) runs under the sentinel; everything else under the
        // scope. Either way the applied fingerprint records the truth, so the NEXT command
        // flips back if it differs — per-command precision without per-command round-trips.
        var escalated = TamRls.HasCrossTenantTag(command.CommandText);
        var desired = escalated ? $"{TamRls.CrossTenantSentinel}|" : TamRls.Fingerprint(scope);
        var applied = State.GetOrCreateValue(command.Connection);
        if (applied.Fingerprint == desired) return null;

        // A raw ADO command on the same connection/transaction — it does NOT pass back through
        // the EF interceptor pipeline, so there is no recursion.
        var set = command.Connection.CreateCommand();
        set.Transaction = command.Transaction;
        set.CommandText =
            $"SELECT set_config('{TamRls.TenantSetting}', @tenant, false), " +
            $"set_config('{TamRls.ReadSetSetting}', @readSet, false)";
        var tenant = set.CreateParameter();
        tenant.ParameterName = "tenant";
        tenant.Value = escalated || scope.CrossTenantScope
            ? TamRls.CrossTenantSentinel
            : scope.CurrentTenantId ?? TamRls.CrossTenantSentinel;
        set.Parameters.Add(tenant);
        var readSet = set.CreateParameter();
        readSet.ParameterName = "readSet";
        readSet.Value = escalated || scope.CrossTenantScope
            ? "" : string.Join(",", scope.TenantReadSet);
        set.Parameters.Add(readSet);

        return (set, applied, desired);
    }

    // The fingerprint advances ONLY after set_config succeeds (review-round-4 F3): recording
    // it before execution would, on a transient failure, mark the connection as applied while
    // the session still holds the previous (possibly other-tenant) setting — and the next
    // command with the same desired state would skip the re-apply entirely.
    private static void Sync(DbCommand command, CommandEventData eventData)
    {
        if (Prepare(command, eventData) is not { } prepared) return;
        using var set = prepared.Set;
        prepared.Applied.Fingerprint = null;
        set.ExecuteNonQuery();
        prepared.Applied.Fingerprint = prepared.Desired;
    }

    private static async Task SyncAsync(
        DbCommand command, CommandEventData eventData, CancellationToken ct)
    {
        if (Prepare(command, eventData) is not { } prepared) return;
        await using var set = prepared.Set;
        prepared.Applied.Fingerprint = null;
        await set.ExecuteNonQueryAsync(ct);
        prepared.Applied.Fingerprint = prepared.Desired;
    }
}
