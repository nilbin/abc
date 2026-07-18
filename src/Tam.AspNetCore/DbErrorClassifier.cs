using Microsoft.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Tells a genuine UNIQUE-constraint violation — a check-then-insert race that should surface as a
/// retryable conflict — apart from every OTHER <see cref="DbUpdateException"/> (foreign key, check
/// constraint, not-null, data conversion, provider/connection fault), which are real failures that
/// must NOT be dressed up as version conflicts (Sol review, Finding 4). A provider package
/// registers a precise implementation after AddTam (last registration wins); the default is a
/// portable message heuristic.
/// </summary>
public interface ITamDbErrorClassifier
{
    bool IsUniqueViolation(DbUpdateException exception);
}

/// <summary>Provider-agnostic fallback: scans the exception chain for the text every major
/// provider uses for a unique violation (SQLite "UNIQUE constraint failed", SQLSTATE 23505,
/// "duplicate key"). Precise, code-based classification is a provider package's job — this only
/// keeps the framework honest when no such package is registered.</summary>
internal sealed class HeuristicDbErrorClassifier : ITamDbErrorClassifier
{
    public bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("23505", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
