using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

public static class TamEvents
{
    /// <summary>
    /// Queues an event on the outbox through the caller's DbContext — persisted by the caller's
    /// own SaveChanges/transaction, so the event exists if and only if the surrounding write
    /// committed (docs/09). Owns the row conventions (id, ISO timestamp, payload serialization);
    /// callers never hand-build <see cref="OutboxRecord"/>s. With no explicit
    /// <paramref name="tenantId"/> the ambient stamp fills it, like any tenant-scoped row.
    /// </summary>
    public static void Publish(
        this DbContext db, string eventType, object payload,
        string operationId = "", string? tenantId = null)
        => db.Add(new OutboxRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId ?? "",
            OperationId = operationId,
            EventType = eventType,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, TamJson.Options),
            CreatedAtIso = IsoTime.Now(),
        });
}
