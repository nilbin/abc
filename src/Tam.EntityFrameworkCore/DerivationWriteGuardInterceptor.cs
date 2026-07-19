using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tam.EntityFrameworkCore;

/// <summary>
/// Makes derivations structurally read-only (docs/40, Sol re-review Finding 3). While the DbContext
/// reports <see cref="ITenantScopeContext.DerivationReadOnly"/> — the window in which the operation
/// pipeline evaluates a derivation — this interceptor rejects every durable write:
///   • <c>SaveChanges</c> (an <c>Add</c>/<c>Update</c>/<c>Remove</c> then save), and
///   • any write COMMAND that bypasses the change tracker — <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>,
///     raw <c>ExecuteSql*</c>, DDL.
/// A derivation therefore cannot produce a side effect whether it returns, blocks, or throws. Reads
/// (SELECT, and the RLS backstop's SET) pass untouched, so the derivation can still read state.
/// </summary>
public sealed class DerivationWriteGuardInterceptor : DbCommandInterceptor, ISaveChangesInterceptor
{
    private static bool ReadOnly(DbContext? context) =>
        context is ITenantScopeContext { DerivationReadOnly: true };

    private static void RejectSave(DbContext? context)
    {
        if (ReadOnly(context))
            throw new InvalidOperationException(
                "DER007: a derivation attempted SaveChanges. Derivations compute input admissibility "
                + "and must be read-only — move any write into the operation handler.");
    }

    // ---- SaveChanges: the tracked-write path ----

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        RejectSave(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        RejectSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    // ---- Commands: the change-tracker-bypassing path (ExecuteUpdate/Delete, raw SQL, DDL) ----

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Guard(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken ct = default)
    {
        Guard(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Guard(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken ct = default)
    {
        Guard(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Guard(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken ct = default)
    {
        Guard(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static void Guard(DbCommand command, CommandEventData eventData)
    {
        if (ReadOnly(eventData.Context) && IsWrite(command.CommandText))
            throw new InvalidOperationException(
                "DER007: a derivation issued a write command (ExecuteUpdate/ExecuteDelete/raw SQL). "
                + "Derivations must be read-only — move any write into the operation handler.");
    }

    // First SQL keyword decides: a write verb is a durable effect; SELECT/WITH/SET are not. (SaveChanges'
    // own INSERT/UPDATE/DELETE never reach here — SavingChanges rejects it first.)
    private static bool IsWrite(string sql)
    {
        var i = 0;
        while (i < sql.Length && (char.IsWhiteSpace(sql[i]) || sql[i] == '(')) i++;
        var start = i;
        while (i < sql.Length && char.IsLetter(sql[i])) i++;
        var verb = sql[start..i];
        return verb.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("MERGE", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("DROP", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase);
    }
}
