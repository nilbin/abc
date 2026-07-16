using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The secrets vault (docs/25). Encryption is ASP.NET Core Data Protection — authenticated AES
/// with automatic key rotation, already in the framework (no dependency, no cloud coupling). In
/// production the Data-Protection key ring is persisted and, if desired, wrapped by Azure Key
/// Vault / AWS KMS via <c>ProtectKeysWith*</c> — the same calling code, harder key custody.
/// The plaintext exists only inside <see cref="Secret"/>; it is never stored, logged, or
/// returned by any view or operation output.
/// </summary>
public sealed class SecretVault(IDataProtectionProvider provider, ITamDb tam)
{
    // Purpose isolation, chained per tenant: "Tam.Secrets.v1" separates vault secrets from any
    // other protector, and the tenant id binds each ciphertext to its tenant — a blob copied into
    // another tenant's row cannot be unprotected there. Cheap to derive, so per call, not cached.
    private IDataProtector Protector(string tenantId) =>
        provider.CreateProtector("Tam.Secrets.v1", tenantId);

    // Every read here takes an explicit tenantId and is reached from background integration runs
    // (scheduler/retry/outbox) where no ambient request tenant is set — so they opt out of the
    // global tenant filter and rely on the explicit predicate, correct in request and job scopes.
    public async Task SetAsync(string tenantId, string key, string plaintext, CancellationToken ct)
    {
        var entity = await tam.Db.Set<TenantSecretEntity>().AcrossTenants().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Key == key, ct);
        if (entity is null)
        {
            entity = new TenantSecretEntity { Id = Guid.NewGuid(), TenantId = tenantId, Key = key };
            tam.Db.Add(entity);
        }
        entity.ProtectedValue = Protector(tenantId).Protect(plaintext);
    }

    /// <summary>Decrypts one secret transiently. Returns null if unset or the key ring can no
    /// longer unprotect it (rotated-away key); callers treat that as "not configured".</summary>
    public async Task<string?> GetAsync(string tenantId, string key, CancellationToken ct)
    {
        var entity = await tam.Db.Set<TenantSecretEntity>().AcrossTenants().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, ct);
        if (entity is null) return null;
        try { return Protector(tenantId).Unprotect(entity.ProtectedValue); }
        catch { return null; }
    }

    public async Task<string?> SettingAsync(string tenantId, string key, CancellationToken ct)
    {
        var entity = await tam.Db.Set<TenantSettingEntity>().AcrossTenants().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, ct);
        return entity?.Value;
    }
}

// ---------------------------------------------------------------- settings (non-secret)
