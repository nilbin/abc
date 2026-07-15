using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.AspNetCore.SystemOps;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Tenant packages (docs/22 P3): declarative field/role bundles, install/uninstall.</summary>
[TamPackage("tam.tenantpackages", "packages", "web.packages")]
public sealed class TamTenantPackagesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("packages", grid: "web.packages", suggest: "administration", order: 50));
        plugin.Model
            .AddOperationType(typeof(InstallPackage))
            .AddOperationType(typeof(UninstallPackage))
            .AddViewType(typeof(PackageList))
            .Form<InstallPackage.Input>("web.packages.install", "packages.install", form =>
            {
                form.Field(x => x.Document).Renderer("multiline");
                form.Field(x => x.DryRun);
            })
            .Grid<PackageList.Result>("web.packages", "packages.list", grid =>
            {
                grid.Column(x => x.Package);
                grid.Column(x => x.Version);
                grid.Column(x => x.InstalledAt);
                grid.ToolbarAction("packages.install");
                grid.RowAction("packages.uninstall");
            });
    }
}

public static class PackageFindings
{
    public static readonly FindingFactory InvalidDocument = Finding.Error("packages.invalid-document");
    public static readonly FindingFactory FieldConflict = Finding.Error("packages.field-conflict");
    public static readonly FindingFactory OlderVersion = Finding.Error("packages.older-version");
    public static readonly FindingFactory NotInstalled = Finding.Error("packages.not-installed");
    public static readonly FindingFactory RoleConflict = Finding.Error("packages.role-conflict");
}

/// <summary>
/// The tenant-package bundle (docs/22 P3): everything the registry accepts one item at a time,
/// as one reviewable JSON document. A package is a file — it lives in a repo, installs into a
/// test tenant first, and a consultant carries the same file to ten customers.
/// </summary>
public sealed record PackageDocument(
    string Package,
    int Version,
    List<PackageFieldSpec>? Fields = null,
    List<PackageRoleSpec>? Roles = null);

public sealed record PackageFieldSpec(
    string Entity,
    string Key,
    string Type,
    Dictionary<string, string> Labels,
    bool Required = false,
    int? MaxLength = null,
    Dictionary<string, string>? Descriptions = null,
    List<string>? Options = null);

public sealed record PackageRoleSpec(string Name, List<string> Permissions);

/// <summary>
/// Installs a tenant package: every item is validated with the registry's own rules first;
/// findings abort the whole install (the pipeline transaction makes it atomic). DryRun runs
/// the identical validation and reports what WOULD happen without applying — the answer to
/// "what will this do to my org" before it does it.
/// </summary>
[Operation("packages.install")]
[Authorize("packages.manage")]
public static class InstallPackage
{
    public sealed record Input(
        [property: LabelKey("labels.package-document")] string Document,
        bool DryRun = false);

    public sealed record Output(string Package, int Version, bool Applied, int FieldsAdded, int RolesDefined);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        PackageDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PackageDocument>(input.Document, TamJson.Options);
        }
        catch (JsonException)
        {
            return PackageFindings.InvalidDocument.At(nameof(Input.Document));
        }
        if (document is null || string.IsNullOrWhiteSpace(document.Package))
            return PackageFindings.InvalidDocument.At(nameof(Input.Document));

        var tenant = context.TenantId.Value;
        var installed = await tam.Db.Set<PackageInstallationEntity>().SingleOrDefaultAsync(
            x => x.Package == document.Package, ct);
        if (installed is not null && document.Version < installed.Version)
            return PackageFindings.OlderVersion
                .With(("installed", installed.Version), ("offered", document.Version))
                .At(nameof(Input.Document));

        // ---- validate everything first: all findings or all applied, never halfway ----
        var findings = new List<Finding>();
        var existingFields = await tam.Db.Set<ExtensionFieldEntity>()
            .ToListAsync(ct);
        var toAdd = new List<ExtensionFieldEntity>();

        foreach (var field in document.Fields ?? [])
        {
            var path = FieldPath.Extension(field.Key);
            if (!model.ExtensibleEntityKeys.Contains(field.Entity))
            {
                findings.Add(ExtensionFindings.UnknownEntity.With(("entity", field.Entity)).At(path));
                continue;
            }
            if (!SemanticTypes.ByKey.ContainsKey(field.Type))
            {
                findings.Add(ExtensionFindings.UnknownType.At(path));
                continue;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(field.Key, "^[a-z][a-zA-Z0-9]*$"))
            {
                findings.Add(ValidationFindings.InvalidValue.At(path));
                continue;
            }
            if (!field.Labels.ContainsKey(model.DefaultCulture))
            {
                findings.Add(ExtensionFindings.MissingLabel.At(path));       // EXT006
                continue;
            }
            var existing = existingFields.FirstOrDefault(
                x => x.Entity == field.Entity && x.Key == field.Key);
            if (existing is not null)
            {
                // Re-install/upgrade against a live or previously-retired field of the SAME
                // package: identical spec → no-op; identical after uninstall → resurrect
                // (uninstall retires, reinstall un-retires — reversible by design). Anything
                // else is a conflict — silent redefinition would corrupt data.
                var identical = existing.Type == field.Type
                    && existing.Required == field.Required
                    && existing.MaxLength == field.MaxLength
                    && existing.LabelsJson == JsonSerializer.Serialize(field.Labels)
                    && existing.OptionsJson == (field.Options is null ? null : JsonSerializer.Serialize(field.Options));
                if (identical && existing.State == ExtensionFieldState.Active)
                    continue;
                if (identical && existing.State == ExtensionFieldState.Retired
                    && existing.Package == document.Package)
                {
                    if (!input.DryRun) existing.State = ExtensionFieldState.Active;
                    continue;
                }
                findings.Add(PackageFindings.FieldConflict.With(("key", field.Key)).At(path));
                continue;
            }
            toAdd.Add(new ExtensionFieldEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenant,
                Entity = field.Entity,
                Key = field.Key,
                Type = field.Type,
                Required = field.Required,
                MaxLength = field.MaxLength,
                LabelsJson = JsonSerializer.Serialize(field.Labels),
                DescriptionsJson = field.Descriptions is null ? null : JsonSerializer.Serialize(field.Descriptions),
                OptionsJson = field.Options is null ? null : JsonSerializer.Serialize(field.Options),
                State = ExtensionFieldState.Active,
                Package = document.Package,
            });
        }

        var roleSpecs = document.Roles ?? [];
        var existingRoles = await tam.Db.Set<RoleEntity>()
            .ToListAsync(ct);
        foreach (var role in roleSpecs)
        {
            // Same role validation as roles.define — one rule set, so a package can't smuggle a
            // reserved permission a future rule would block on the direct path.
            findings.AddRange(RoleRules.Validate(role.Name, role.Permissions, model, nameof(Input.Document)));
            // A role is a PERMISSION grant — never silently overwrite one the tenant already
            // authored differently. Identical → no-op; different → explicit conflict.
            var existingRole = existingRoles.FirstOrDefault(x => x.Name == role.Name);
            if (existingRole is not null
                && existingRole.PermissionsJson != JsonSerializer.Serialize(role.Permissions))
                findings.Add(PackageFindings.RoleConflict.With(("role", role.Name)).At(nameof(Input.Document)));
        }

        if (findings.Count > 0)
            return new Result<Output> { Findings = findings };

        if (input.DryRun)
            return new Output(document.Package, document.Version, Applied: false, toAdd.Count, roleSpecs.Count);

        // ---- apply: one transaction (the pipeline's), one audit entry, one act ----
        tam.Db.AddRange(toAdd);
        foreach (var role in roleSpecs)
        {
            var entity = await tam.Db.Set<RoleEntity>().SingleOrDefaultAsync(
                x => x.Name == role.Name, ct);
            if (entity is null)
            {
                entity = new RoleEntity { Id = Guid.NewGuid(), TenantId = tenant, Name = role.Name };
                tam.Db.Add(entity);
            }
            entity.PermissionsJson = JsonSerializer.Serialize(role.Permissions);
        }

        if (installed is null)
        {
            installed = new PackageInstallationEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenant,
                Package = document.Package,
            };
            tam.Db.Add(installed);
        }
        installed.Version = document.Version;
        installed.DocumentJson = input.Document;
        installed.InstalledAtIso = IsoTime.Now();

        return new Output(document.Package, document.Version, Applied: true, toAdd.Count, roleSpecs.Count);
    }
}

/// <summary>
/// Uninstall retires, never deletes (docs/15's retire-don't-drop, applied to bundles): the
/// package's fields stop appearing anywhere new, their data and keys stay forever. Roles the
/// package defined remain — they may have been granted to real users.
/// </summary>
[Operation("packages.uninstall")]
[Authorize("packages.manage")]
public static class UninstallPackage
{
    public sealed record Input([property: LabelKey("labels.package")] string Package);

    public sealed record Output(string Package, int FieldsRetired);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var tenant = context.TenantId.Value;
        var installed = await tam.Db.Set<PackageInstallationEntity>().SingleOrDefaultAsync(
            x => x.Package == input.Package, ct);
        if (installed is null)
            return PackageFindings.NotInstalled.With(("package", input.Package)).At(nameof(Input.Package));

        var fields = await tam.Db.Set<ExtensionFieldEntity>()
            .Where(x => x.Package == input.Package
                && x.State != ExtensionFieldState.Retired)
            .ToListAsync(ct);
        foreach (var field in fields) field.State = ExtensionFieldState.Retired;

        tam.Db.Remove(installed);
        return new Output(input.Package, fields.Count);
    }
}

[View("packages.list")]
[Authorize("packages.manage")]
public static class PackageList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.package")]
        public string Package { get; init; } = "";
        [LabelKey("labels.version")]
        public int Version { get; init; }
        [LabelKey("labels.installed-at")]
        public string InstalledAt { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var installed = tam.Db.Set<PackageInstallationEntity>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            installed = installed.Where(x => x.Package.Contains(query.Search!));
        return installed.Select(x => new Result
        {
            Id = x.Id,
            Package = x.Package,
            Version = x.Version,
            InstalledAt = x.InstalledAtIso.Substring(0, 16).Replace("T", " "),
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Package)).DefaultSort(nameof(Result.Package));
}
