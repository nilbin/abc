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
        // Fail CLOSED (Sol re-review round 4, Finding 1): allow only commands PROVEN read-only, not
        // a write-verb denylist (which a leading comment, CTE, or extra statement trivially bypassed).
        // The RLS backstop's session SET is issued as a raw ADO command that bypasses the interceptor
        // pipeline entirely, so nothing framework-owned needs whitelisting here — every command that
        // reaches this guard during a derivation is either the derivation's own SELECT or a write.
        if (ReadOnly(eventData.Context) && !IsProvenReadOnly(command.CommandText))
            throw new InvalidOperationException(
                "DER007: a derivation issued a non-read command (ExecuteUpdate/ExecuteDelete/raw SQL). "
                + "Derivations must be read-only — move any write into the operation handler.");
    }

    /// <summary>True only when EVERY statement is a plain read (SELECT / VALUES / TABLE), after
    /// stripping comments and any leading WITH-CTE. Anything else — a write verb, a CTE that ends in
    /// UPDATE/DELETE, CALL/EXEC/PRAGMA/VACUUM/DDL, an empty leading token from a comment — is NOT
    /// proven read-only and is rejected. (SaveChanges' own writes never reach here — SavingChanges
    /// rejects them first.)</summary>
    private static bool IsProvenReadOnly(string sql)
    {
        foreach (var raw in StripComments(sql).Split(';'))
        {
            var statement = raw.Trim();
            if (statement.Length == 0) continue;
            var verb = LeadingVerbAfterCte(statement);
            if (!(verb.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                    || verb.Equals("VALUES", StringComparison.OrdinalIgnoreCase)
                    || verb.Equals("TABLE", StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    /// <summary>Removes <c>-- line</c> and <c>/* block */</c> comments so a comment prefix can't hide
    /// the real verb. (EF parameterizes values, so comment markers never appear inside a literal in
    /// generated SQL; raw SQL is the author's own.)</summary>
    private static string StripComments(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i++;
                sb.Append(' ');
            }
            else sb.Append(sql[i]);
        }
        return sb.ToString();
    }

    /// <summary>The statement's real verb: skips a leading <c>WITH … AS ( … )</c> CTE block (balanced
    /// parens) so <c>WITH t AS (SELECT …) UPDATE …</c> reports UPDATE, not a false SELECT.</summary>
    private static string LeadingVerbAfterCte(string statement)
    {
        var i = 0;
        var verb = ReadWord(statement, ref i);
        if (!verb.Equals("WITH", StringComparison.OrdinalIgnoreCase)) return verb;

        // Consume CTE definitions until the parenthesis depth returns to 0 and we reach the main verb.
        var depth = 0;
        var sawParens = false;
        for (; i < statement.Length; i++)
        {
            if (statement[i] == '(') { depth++; sawParens = true; }
            else if (statement[i] == ')') depth--;
            else if (depth == 0 && sawParens && char.IsLetter(statement[i])) break;
        }
        return ReadWord(statement, ref i);
    }

    private static string ReadWord(string s, ref int i)
    {
        while (i < s.Length && !char.IsLetter(s[i])) i++;
        var start = i;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        return s[start..i];
    }
}
