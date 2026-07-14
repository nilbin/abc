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
        var entity = await tam.Db.Set<TenantSecretEntity>().IgnoreQueryFilters().SingleOrDefaultAsync(
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
        var entity = await tam.Db.Set<TenantSecretEntity>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, ct);
        if (entity is null) return null;
        try { return Protector(tenantId).Unprotect(entity.ProtectedValue); }
        catch { return null; }
    }

    public async Task<string?> SettingAsync(string tenantId, string key, CancellationToken ct)
    {
        var entity = await tam.Db.Set<TenantSettingEntity>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, ct);
        return entity?.Value;
    }
}

// ---------------------------------------------------------------- settings (non-secret)

public static class VaultFindings
{
    public static readonly FindingFactory InvalidKey = Finding.Error("vault.invalid-key");
}

[Operation("settings.set")]
[Authorize("settings.manage")]
public static class SetSetting
{
    public sealed record Input(
        [property: LabelKey("labels.key")] string Key,
        [property: LabelKey("labels.value")] string Value);

    public sealed record Output(string Key);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        if (!IsKey(input.Key)) return VaultFindings.InvalidKey.At(nameof(Input.Key));
        var entity = await tam.Db.Set<TenantSettingEntity>().SingleOrDefaultAsync(
            x => x.Key == input.Key, ct);
        if (entity is null)
        {
            entity = new TenantSettingEntity
            {
                Id = Guid.NewGuid(), Key = input.Key,
            };
            tam.Db.Add(entity);
        }
        entity.Value = input.Value;
        return new Output(input.Key);
    }

    internal static bool IsKey(string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-zA-Z0-9.-]*$");
}

[View("settings.list")]
[Authorize("settings.manage")]
public static class SettingList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        [LabelKey("labels.key")]
        public string Key { get; init; } = "";
        [LabelKey("labels.value")]
        public string Value { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context) =>
        tam.Db.Set<TenantSettingEntity>()
            .Select(x => new Result { Key = x.Key, Value = x.Value });

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Key)).DefaultSort(nameof(Result.Key));
}

// ---------------------------------------------------------------- secrets (encrypted, write-only)

/// <summary>
/// Secrets are WRITE-ONLY through the API: setting a value encrypts it; nothing ever reads it
/// back out. The list view shows keys and a set/unset flag — never the value, not even masked
/// ciphertext. Only an integration run decrypts a secret, in-process.
/// </summary>
[Operation("secrets.set")]
[Authorize("secrets.manage")]
public static class SetSecret
{
    public sealed record Input(
        [property: LabelKey("labels.key")] string Key,
        [property: LabelKey("labels.value")] string Value);

    public sealed record Output(string Key);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, SecretVault vault, CancellationToken ct)
    {
        if (!SetSetting.IsKey(input.Key)) return VaultFindings.InvalidKey.At(nameof(Input.Key));
        await vault.SetAsync(context.TenantId.Value, input.Key, input.Value, ct);
        return new Output(input.Key);   // never echoes the value
    }
}

[View("secrets.list")]
[Authorize("secrets.manage")]
public static class SecretList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        [LabelKey("labels.key")]
        public string Key { get; init; } = "";
        [LabelKey("labels.secret-set")]
        public bool IsSet { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context) =>
        tam.Db.Set<TenantSecretEntity>()
            .Select(x => new Result { Key = x.Key, IsSet = true });   // value never leaves the vault

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Key)).DefaultSort(nameof(Result.Key));
}
