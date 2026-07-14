using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tam.EntityFrameworkCore;

public static class TamModelConventions
{
    /// <summary>
    /// Applies Tam conventions to an EF model: semantic value-wrapper conversions,
    /// <see cref="ExtensionData"/> JSON columns for <see cref="IExtensible"/> entities,
    /// audit and idempotency tables, and concurrency tokens for <see cref="IVersioned"/>.
    /// </summary>
    public static ModelBuilder UseTam(this ModelBuilder modelBuilder, string? providerName = null)
    {
        var isNpgsql = providerName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.ToTable("audit_entries");
            b.HasKey(x => x.Id);
            b.HasMany(x => x.Changes).WithOne().HasForeignKey(x => x.EntryId);
            b.HasIndex(x => new { x.TenantId, x.Timestamp });
        });
        modelBuilder.Entity<AuditChange>(b =>
        {
            b.ToTable("audit_changes");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Entity, x.EntityId });
        });
        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency");
            b.HasKey(x => new { x.TenantId, x.OperationId, x.Key });
        });
        modelBuilder.Entity<ExtensionFieldEntity>(b =>
        {
            b.ToTable("extension_fields");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Entity, x.Key }).IsUnique();
        });
        modelBuilder.Entity<RoleEntity>(b =>
        {
            b.ToTable("roles");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });
        modelBuilder.Entity<InboxRecord>(b =>
        {
            b.ToTable("integration_inbox");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.IntegrationId, x.Key }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status });
        });
        modelBuilder.Entity<OutboxRecord>(b =>
        {
            b.ToTable("outbox");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.DispatchedAtIso);
        });
        modelBuilder.Entity<PluginActivationEntity>(b =>
        {
            b.ToTable("plugin_activations");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.PluginId }).IsUnique();
        });

        foreach (var entity in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(IExtensible).IsAssignableFrom(entity.ClrType))
            {
                var property = modelBuilder.Entity(entity.ClrType)
                    .Property<ExtensionData>(nameof(IExtensible.Extensions))
                    .HasConversion(
                        new ValueConverter<ExtensionData, string>(
                            d => d.ToJson(),
                            json => ExtensionData.FromJson(json)))
                    .HasColumnName("extensions");
                // The design's target storage (docs/15): one JSONB column per extensible aggregate.
                if (isNpgsql) property.HasColumnType("jsonb");
            }

            if (typeof(IVersioned).IsAssignableFrom(entity.ClrType))
            {
                modelBuilder.Entity(entity.ClrType)
                    .Property<long>(nameof(IVersioned.Version))
                    .IsConcurrencyToken();
            }

            foreach (var property in entity.ClrType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (!ValueWrapper.IsWrapper(type) || type == typeof(ExtensionData)) continue;
                var underlying = ValueWrapper.UnderlyingType(type)!;
                var converter = (ValueConverter)Activator.CreateInstance(
                    typeof(WrapperConverter<,>).MakeGenericType(type, underlying))!;
                modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion(converter);
            }
        }

        return modelBuilder;
    }

    private sealed class WrapperConverter<TWrapper, TValue>() : ValueConverter<TWrapper, TValue>(
        w => (TValue)ValueWrapper.Unwrap(w)!,
        v => (TWrapper)ValueWrapper.Wrap(typeof(TWrapper), v));
}

public enum InboxStatus
{
    Pending,
    Processed,
    Failed,
    Dead,
}

/// <summary>
/// Integration inbox (docs/10): every received external row is persisted before processing,
/// retried from its stored payload on later runs, and dead-lettered after repeated failure —
/// so a fixed root cause (e.g. the missing customer) recovers without re-sending anything.
/// </summary>
public sealed class InboxRecord
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Key { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public InboxStatus Status { get; set; } = InboxStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>Outbox row: an explicit event effect, persisted in the operation's transaction (docs/09).</summary>
public sealed class OutboxRecord
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string CreatedAtIso { get; set; } = "";
    public string? DispatchedAtIso { get; set; }
}

/// <summary>Tenant-managed role: a named grant set (decision D1). Managed only through operations.</summary>
public sealed class RoleEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PermissionsJson { get; set; } = "[]";

    public IReadOnlySet<string> Permissions() =>
        System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(PermissionsJson) ?? [];
}

/// <summary>Per-tenant plugin activation (docs/22): the row's existence IS the activation.
/// The plugin's code is compiled into the deployment either way; for tenants without the row,
/// its contributions are omitted from the manifest and its endpoints answer 404.</summary>
public sealed class PluginActivationEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string PluginId { get; set; } = "";
}

/// <summary>Registry storage for tenant-defined fields (docs/15). Managed only through operations.</summary>
public sealed class ExtensionFieldEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Key { get; set; } = "";
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public int? MaxLength { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public string? DescriptionsJson { get; set; }
    public string? OptionsJson { get; set; }
    public ExtensionFieldState State { get; set; } = ExtensionFieldState.Active;

    public ExtensionFieldSpec ToSpec() => new(
        Key, Entity, Type, Required, MaxLength,
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(LabelsJson) ?? [],
        DescriptionsJson is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(DescriptionsJson),
        OptionsJson is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(OptionsJson),
        State);
}
