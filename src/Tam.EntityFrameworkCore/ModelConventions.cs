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
        modelBuilder.Entity<InviteEntity>(b =>
        {
            b.ToTable("invites");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.AccountId });
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
        modelBuilder.Entity<AccountEntity>(b =>
        {
            b.ToTable("accounts");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Email).IsUnique();
        });
        modelBuilder.Entity<TenantMembershipEntity>(b =>
        {
            b.ToTable("tenant_memberships");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.AccountId }).IsUnique();
            b.HasIndex(x => x.AccountId);
        });
        modelBuilder.Entity<TenantEntity>(b =>
        {
            b.ToTable("tenants");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Path);
        });
        modelBuilder.Entity<SubscriptionEntity>(b =>
        {
            b.ToTable("subscriptions");
            b.HasKey(x => x.TenantId);
        });
        modelBuilder.Entity<NavOverrideEntity>(b =>
        {
            b.ToTable("nav_overrides");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.NodeId }).IsUnique();
        });
        modelBuilder.Entity<AutomationRuleEntity>(b =>
        {
            b.ToTable("automation_rules");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.OnOperation });
        });

        modelBuilder.Entity<FolderEntity>(b =>
        {
            b.ToTable("document_folders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Path).HasMaxLength(1000);
            b.Property(x => x.Name).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Path }).IsUnique();
        });
        modelBuilder.Entity<DocumentEntity>(b =>
        {
            b.ToTable("documents");
            b.HasKey(x => x.Id);
            b.Property(x => x.FileName).HasMaxLength(500);
            b.Property(x => x.ContentType).HasMaxLength(200);
            b.Property(x => x.ContentHash).HasMaxLength(64);
            b.Property(x => x.AttachedTo).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.FolderId });
            // The record-tab read (docs/35 EntityRef): all documents attached to one record.
            b.HasIndex(x => new { x.TenantId, x.AttachedTo });
        });
        modelBuilder.Entity<DocumentAclEntity>(b =>
        {
            b.ToTable("document_acls");
            b.HasKey(x => x.Id);
            b.Property(x => x.Reach).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.FolderId, x.Reach }).IsUnique();
        });
        modelBuilder.Entity<DocumentBlobEntity>(b =>
        {
            b.ToTable("document_blobs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Hash).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.Hash }).IsUnique();
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
