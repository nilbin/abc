using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Integration settings + secrets (docs/25). The vault SERVICE (encryption) is core;
/// this is the masked admin surface.</summary>
[TamPackage("tam.vault", "settings", "secrets")]
public sealed class TamVaultPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin
            .AddOperationType(typeof(SetSetting))
            .AddOperationType(typeof(SetSecret))
            .AddViewType(typeof(SettingList))
            .AddViewType(typeof(SecretList));
    }
}

public static class VaultFindings
{
    public static readonly FindingFactory InvalidKey = Finding.Error("vault.invalid-key");
}

[Operation("settings.set")]
[Authorize("settings.manage")]
public static class SetSetting
{
    public sealed record Input(
        string Key,
        string Value);

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
        public string Key { get; init; } = "";
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
        string Key,
        string Value);

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
