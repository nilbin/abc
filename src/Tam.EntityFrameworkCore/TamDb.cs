using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

/// <summary>
/// Framework database seam: system operations and plugins use this, never the application's
/// concrete DbContext type — a module composes around the host, it does not reach into it
/// (docs/22). The host's single context is registered behind it at startup.
/// </summary>
public interface ITamDb
{
    DbContext Db { get; }
}

public sealed class TamDb(DbContext db) : ITamDb
{
    public DbContext Db { get; } = db;
}
