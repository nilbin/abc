using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Tenant-managed roles (D1): definition + list, validated against the catalogue.</summary>
[TamPackage("tam.roles", "roles", "web.roles")]
public sealed class TamRolesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("roles", grid: "web.roles", suggest: "administration", order: 20));
        plugin
            .AddOperationType(typeof(DefineRole))
            .AddOperationType(typeof(RetireRole))
            .AddViewType(typeof(RoleList))
            .Form<DefineRole.Input>("web.roles.define", "roles.define", form =>
            {
                form.Field(x => x.Name);
                form.Field(x => x.Levels).Renderer("level-map");
                form.Field(x => x.Permissions).Renderer("string-list");
            })
            .Grid<RoleList.Result>("web.roles", "roles.list", grid =>
            {
                grid.Column(x => x.Name);
                grid.Column(x => x.Levels);
                grid.Column(x => x.Permissions);
                grid.Column(x => x.Retired);
                grid.ToolbarAction("roles.define");
                grid.RowAction("roles.retire");
            });
    }
}

public static class RoleFindings
{
    public static readonly FindingFactory UnknownPermission = Finding.Error("roles.unknown-permission");
    public static readonly FindingFactory InvalidName = Finding.Error("roles.invalid-name");
    public static readonly FindingFactory ReservedPermission = Finding.Error("roles.reserved-permission");
    public static readonly FindingFactory UnknownResource = Finding.Error("roles.unknown-resource");
    public static readonly FindingFactory UnknownLevel = Finding.Error("roles.unknown-level");
}

/// <summary>
/// The single source of truth for what a tenant role may be (docs/1 + docs/24). Both the
/// <c>roles.define</c> operation and package install validate through here, so a new rule — a new
/// reserved permission especially — is enforced on every path at once, not added to one and
/// forgotten in the other (which would make a package a privilege-escalation vector).
/// </summary>
public static class RoleRules
{
    public static IReadOnlyList<Finding> Validate(
        string name, IReadOnlyList<string> permissions, TamModel model, string field,
        IReadOnlyDictionary<string, string>? levels = null)
    {
        var findings = new List<Finding>();
        if (!Naming.IsSlug(name))
            findings.Add(RoleFindings.InvalidName.At(field));
        // Atoms only (docs/28 D-AG1/D-AG2): there are no scope suffixes — own-scoping is the
        // paired-atom pattern ("orders.read" own-scoped, "orders.read-all" widening), so every
        // grant must literally exist in the compiled catalogue.
        findings.AddRange(permissions
            .Where(p => p != "*" && !model.Permissions.Contains(p))
            .Select(p => RoleFindings.UnknownPermission.With(("permission", p)).At(field)));
        // Reserved permissions (subscriptions.manage) are never grantable through a tenant role,
        // whether authored directly or shipped in a package — closes the wildcard-admin bypass.
        // (Access levels can't smuggle them either: AccessLevels.Expand never yields a reserved atom.)
        findings.AddRange(permissions
            .Where(p => Actor.IsReserved(p))
            .Select(p => RoleFindings.ReservedPermission.With(("permission", p)).At(field)));
        // Access levels (docs/27 D-A1): the resource must exist in the compiled catalogue and the
        // level must be one of the ordered presets — same registry-as-compiler rule as the atoms.
        foreach (var (resource, level) in levels ?? new Dictionary<string, string>())
        {
            if (!AccessLevels.Catalog(model).ContainsKey(resource))
                findings.Add(RoleFindings.UnknownResource.With(("resource", resource)).At(field));
            if (!AccessLevels.All.Contains(level))
                findings.Add(RoleFindings.UnknownLevel.With(("level", level)).At(field));
        }
        return findings;
    }
}

/// <summary>
/// Roles are tenant data managed through operations, validated at definition time against the
/// compiled permission catalogue — the same registry-as-compiler pattern as custom fields.
/// </summary>
[Operation("roles.define")]
[Authorize("roles.manage")]
public static class DefineRole
{
    /// <summary>A role is authored as access levels per resource ({"orders":"manage"}) and/or
    /// explicit permission atoms — the escape hatch when a level is too coarse (docs/27 D-A1).</summary>
    public sealed record Input(
        string Name,
        List<string>? Permissions = null,
        Dictionary<string, string>? Levels = null);

    public sealed record Output(Guid RoleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        var invalid = RoleRules.Validate(
            input.Name, input.Permissions ?? [], model, nameof(Input.Permissions), input.Levels);
        if (invalid.Count > 0) return new Result<Output> { Findings = [.. invalid] };

        var role = await tam.Db.Set<RoleEntity>().SingleOrDefaultAsync(
            x => x.Name == input.Name, ct);
        if (role is null)
        {
            role = new RoleEntity
            {
                Id = Guid.NewGuid(),
                Name = input.Name,
            };
            tam.Db.Add(role);
        }
        role.PermissionsJson = JsonSerializer.Serialize(input.Permissions ?? []);
        role.LevelsJson = JsonSerializer.Serialize(input.Levels ?? []);
        role.Retired = false;   // redefining a retired name revives it, like rules

        return new Output(role.Id);
    }
}

/// <summary>Retire by natural key, like every define-by-name surface (docs/29 conventions):
/// the role stops granting anything; the name and audit referents stay.</summary>
[Operation("roles.retire")]
[Authorize("roles.manage")]
public static class RetireRole
{
    public sealed record Input(string Name);

    public sealed record Output(string Name);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var role = await tam.Db.Set<RoleEntity>().SingleOrDefaultAsync(
            x => x.Name == input.Name, ct);
        if (role is null) return PipelineFindings.NotFound.Create();

        role.Retired = true;
        return new Output(role.Name);
    }
}

[View("roles.list")]
[Authorize("roles.manage")]
public static class RoleList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Permissions { get; init; } = "";
        public string Levels { get; init; } = "";
        public bool Retired { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context) =>
        tam.Db.Set<RoleEntity>()
            .Select(x => new Result
            {
                Id = x.Id, Name = x.Name, Permissions = x.PermissionsJson, Levels = x.LevelsJson,
                Retired = x.Retired,
            });

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}

