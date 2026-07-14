using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Tam.EntityFrameworkCore;

/// <summary>Append-only audit written in the operation's own transaction (decision D3).</summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Culture { get; set; } = "";
    public string? CorrelationId { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    /// <summary>ISO-8601 copy of <see cref="Timestamp"/>: sortable as text on every provider.</summary>
    public string TimestampIso { get; set; } = "";
    public List<AuditChange> Changes { get; set; } = [];
}

public sealed class AuditChange
{
    public Guid Id { get; set; }
    public Guid EntryId { get; set; }
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Kind { get; set; } = "";       // added | modified | deleted
    public string Field { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public sealed class IdempotencyRecord
{
    public string Key { get; set; } = "";        // (tenant, operation, key) composite
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string PayloadHash { get; set; } = "";  // same key + different payload = client bug, rejected
    public string ResponseJson { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}

public interface IVersioned
{
    long Version { get; set; }
}

public static class TamAudit
{
    /// <summary>
    /// Captures field-level old/new from EF change tracking into audit rows and bumps
    /// <see cref="IVersioned"/> versions. Call once, immediately before SaveChanges,
    /// inside the operation transaction — audit and change are atomic or neither exists.
    /// </summary>
    public static AuditEntry Capture(DbContext db, OperationContext context, string operationId)
    {
        var entry = new AuditEntry
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            OperationId = operationId,
            ActorId = context.Actor.Id,
            ActorName = context.Actor.Name,
            Source = context.Source.ToString(),
            Culture = context.Culture,
            CorrelationId = context.CorrelationId,
            IdempotencyKey = context.IdempotencyKey,
            Timestamp = DateTimeOffset.UtcNow,
            TimestampIso = DateTimeOffset.UtcNow.ToString("O"),
        };

        foreach (var tracked in db.ChangeTracker.Entries().ToList())
        {
            if (tracked.Entity is AuditEntry or AuditChange or IdempotencyRecord) continue;
            if (tracked.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            if (tracked.Entity is IVersioned versioned && tracked.State == EntityState.Modified)
                versioned.Version++;

            var entityName = TamModel.EntityKey(tracked.Entity.GetType());
            var entityId = PrimaryKey(tracked);

            switch (tracked.State)
            {
                case EntityState.Added:
                    entry.Changes.Add(Change(entry, entityName, entityId, "added", "", null, null));
                    break;
                case EntityState.Deleted:
                    entry.Changes.Add(Change(entry, entityName, entityId, "deleted", "", null, null));
                    break;
                case EntityState.Modified:
                    foreach (var property in tracked.Properties.Where(p => p.IsModified))
                    {
                        entry.Changes.Add(Change(
                            entry, entityName, entityId, "modified",
                            Naming.Camel(property.Metadata.Name),
                            Render(property.OriginalValue),
                            Render(property.CurrentValue)));
                    }
                    break;
            }
        }

        db.Add(entry);
        return entry;
    }

    /// <summary>Persistence effects inferred from change tracking (docs/09).</summary>
    public static IReadOnlyList<OperationEffect> InferEffects(AuditEntry entry) =>
        entry.Changes
            .GroupBy(c => (c.Entity, c.EntityId, c.Kind))
            .Select(g => (OperationEffect)(g.Key.Kind switch
            {
                "added" => new EntityCreated(g.Key.Entity, g.Key.EntityId),
                "deleted" => new EntityModified(g.Key.Entity, g.Key.EntityId, ["<deleted>"]),
                _ => new EntityModified(g.Key.Entity, g.Key.EntityId,
                    g.Select(c => c.Field).Where(f => f.Length > 0).ToList()),
            }))
            .ToList();

    private static AuditChange Change(
        AuditEntry entry, string entity, string entityId, string kind,
        string field, string? oldValue, string? newValue) => new()
    {
        Id = Guid.NewGuid(),
        EntryId = entry.Id,
        Entity = entity,
        EntityId = entityId,
        Kind = kind,
        Field = field,
        OldValue = oldValue,
        NewValue = newValue,
    };

    private static string PrimaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null) return "";
        return string.Join("/", key.Properties.Select(p =>
            ValueWrapper.Unwrap(entry.Property(p.Name).CurrentValue)?.ToString() ?? ""));
    }

    private static string? Render(object? value) =>
        ValueWrapper.Unwrap(value)?.ToString();
}
