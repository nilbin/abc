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
        TamDbFunctions.Register(modelBuilder, isNpgsql);
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
            // The dispatcher polls undispatched, un-dead rows oldest-first; the composite index
            // supports both the filter and the ordering as history accumulates.
            b.HasIndex(x => new { x.DispatchedAtIso, x.DeadAtIso, x.CreatedAtIso });
            b.Property(x => x.ClaimedUntilIso).IsConcurrencyToken();
        });
        modelBuilder.Entity<OutboundTaskEntity>(b =>
        {
            b.ToTable("outbound_tasks");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Status, x.NextAttemptIso });
            b.HasIndex(x => new { x.TenantId, x.IntegrationId });
            // NextAttemptIso is the retry driver's lease, exactly like the scheduler's NextRunIso.
            b.Property(x => x.NextAttemptIso).IsConcurrencyToken();
        });
        modelBuilder.Entity<PluginActivationEntity>(b =>
        {
            b.ToTable("plugin_activations");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.PluginId }).IsUnique();
        });
        modelBuilder.Entity<PackageInstallationEntity>(b =>
        {
            b.ToTable("package_installations");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Package }).IsUnique();
        });
        modelBuilder.Entity<TenantSettingEntity>(b =>
        {
            b.ToTable("tenant_settings");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        });
        modelBuilder.Entity<TenantSecretEntity>(b =>
        {
            b.ToTable("tenant_secrets");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        });
        modelBuilder.Entity<IntegrationScheduleEntity>(b =>
        {
            b.ToTable("integration_schedules");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.IntegrationId }).IsUnique();
            // The due-scan filters on Enabled and orders by NextRunIso every minute.
            b.HasIndex(x => new { x.Enabled, x.NextRunIso });
            // NextRunIso is the scheduler's lease: claiming a due schedule means moving it forward,
            // and this makes that move optimistically concurrent — two instances racing to fire the
            // same tick collide on the token and only one wins (docs/25). No lock table needed.
            b.Property(x => x.NextRunIso).IsConcurrencyToken();
        });
        modelBuilder.Entity<IntegrationRunEntity>(b =>
        {
            b.ToTable("integration_runs");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.IntegrationId });
        });
        modelBuilder.Entity<TamUserEntity>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.UserName }).IsUnique();
        });
        modelBuilder.Entity<SubscriptionEntity>(b =>
        {
            b.ToTable("subscriptions");
            b.HasKey(x => x.TenantId);
        });
        modelBuilder.Entity<AutomationRuleEntity>(b =>
        {
            b.ToTable("automation_rules");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.OnOperation });
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
public sealed class InboxRecord : ITenantScoped
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

    // Exponential-backoff gate (docs/10 + docs/25): a failed row is not re-driven before this
    // instant, so a rapid re-POST can't hammer a failing row — the same RetryPolicy the outbound
    // queue uses. Null/empty means "process immediately" (a never-attempted Pending row).
    public string? NextAttemptIso { get; set; }
}

/// <summary>
/// Outbound retry queue (docs/25): a failed event/schedule push is enqueued here and re-driven by
/// the IntegrationRetryDriver with the same attempts/backoff/dead-letter semantics as the inbound
/// inbox. The event payload is stored so a retry replays the exact push without the source event.
/// </summary>
public sealed class OutboundTaskEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Trigger { get; set; } = "";           // event | schedule
    public string? PayloadJson { get; set; }             // event payload to replay (null for schedule)
    public InboxStatus Status { get; set; } = InboxStatus.Failed;   // Failed (awaiting) | Processed | Dead
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string NextAttemptIso { get; set; } = "";     // due time AND optimistic-concurrency lease
    public string CreatedAtIso { get; set; } = "";
    public string? CompletedAtIso { get; set; }
}

/// <summary>Outbox row: an explicit event effect, persisted in the operation's transaction (docs/09).</summary>
public sealed class OutboxRecord : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string CreatedAtIso { get; set; } = "";
    public string? DispatchedAtIso { get; set; }

    // Multi-instance safety (review-round-3): the dispatcher takes a time-boxed lease before
    // dispatching so only one instance delivers a given row; ClaimedUntilIso doubles as the
    // optimistic-concurrency token that makes the claim atomic. A crash mid-dispatch lets the
    // lease lapse and another instance re-delivers (at-least-once preserved).
    public string? ClaimedUntilIso { get; set; }
    // Poison-message dead-letter, mirroring the inbox: a row that keeps throwing is parked after
    // a cap instead of blocking every newer event behind it forever.
    public int Attempts { get; set; }
    public string? DeadAtIso { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// A tenant's subscription (docs/24): the plan, seat ceiling and plugin entitlements a billing
/// provider drives through subscriptions.set-plan. One row per tenant; its absence means the
/// free default, so the framework is fully usable without any billing system wired up.
/// </summary>
public sealed class SubscriptionEntity : ITenantScoped
{
    public string TenantId { get; set; } = "";
    public string Plan { get; set; } = "free";
    public int Seats { get; set; } = 2;
    public string EntitlementsJson { get; set; } = "[]";
    public string Status { get; set; } = "active";
    public string? RenewsAtIso { get; set; }

    public IReadOnlyList<string> Entitlements() =>
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(EntitlementsJson) ?? [];

    public bool Entitles(string pluginId)
    {
        var entitlements = Entitlements();
        return entitlements.Contains("*") || entitlements.Contains(pluginId);
    }
}

/// <summary>
/// Framework user record: identity is data like roles are (D1). Which authentication mechanism
/// proves the identity (the built-in OpenIddict server, an external IdP, a header in dev) is an
/// application decision behind IActorProvider — the user store is mechanism-agnostic.
/// </summary>
public sealed class TamUserEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string RolesJson { get; set; } = "[]";
    public bool Active { get; set; } = true;

    public IReadOnlyList<string> Roles() =>
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(RolesJson) ?? [];
}

/// <summary>Tenant-managed role: a named grant set (decision D1). Managed only through operations.</summary>
public sealed class RoleEntity : ITenantScoped
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
public sealed class PluginActivationEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string PluginId { get; set; } = "";
}

/// <summary>
/// A tenant automation rule (docs/22 P5): trigger operation + Px condition (stored as the
/// structured AST, never a parsed string) + a blocking finding. Managed only through
/// operations; retire-don't-delete like every registry artifact.
/// </summary>
public sealed class AutomationRuleEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OnOperation { get; set; } = "";
    public string ConditionJson { get; set; } = "";
    public string? TargetField { get; set; }
    public string MessagesJson { get; set; } = "{}";
    public bool Retired { get; set; }
}

/// <summary>Non-secret per-tenant integration config (docs/25): base URLs, account ids, flags.
/// Readable in the clear; managed through settings.set / settings.list.</summary>
public sealed class TenantSettingEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A per-tenant secret (docs/25): API keys, tokens, passwords. Stored ENCRYPTED — the column
/// only ever holds the Data-Protection ciphertext; the plaintext is decrypted transiently when
/// an integration runs and is never returned by any view or operation output.
/// </summary>
public sealed class TenantSecretEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Key { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
}

/// <summary>A schedule for an outbound integration (docs/25): spec + next-run bookkeeping.</summary>
public sealed class IntegrationScheduleEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Spec { get; set; } = "";          // "every:15m" | "daily:02:00"
    public bool Enabled { get; set; } = true;
    public string NextRunIso { get; set; } = "";
    public string? LastRunIso { get; set; }
    public string? LastStatus { get; set; }
}

/// <summary>One execution of an integration (docs/25): the audit trail for external calls.</summary>
public sealed class IntegrationRunEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Trigger { get; set; } = "";       // event | schedule | manual
    public string Status { get; set; } = "";        // ok | failed
    public string? Detail { get; set; }
    public string RanAtIso { get; set; } = "";
}

/// <summary>An installed tenant package (docs/22 P3): the bundle document is retained so
/// upgrades can diff against what was actually applied.</summary>
public sealed class PackageInstallationEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Package { get; set; } = "";
    public int Version { get; set; }
    public string DocumentJson { get; set; } = "";
    public string InstalledAtIso { get; set; } = "";
}

/// <summary>Registry storage for tenant-defined fields (docs/15). Managed only through operations.</summary>
public sealed class ExtensionFieldEntity : ITenantScoped
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

    /// <summary>Set when the field arrived via a tenant package (docs/22 P3) — uninstall
    /// retires exactly these, never fields the tenant defined by hand.</summary>
    public string? Package { get; set; }

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
