using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

public static class RoleFindings
{
    public static readonly FindingFactory UnknownPermission = Finding.Error("roles.unknown-permission");
    public static readonly FindingFactory InvalidName = Finding.Error("roles.invalid-name");
}

/// <summary>
/// Decision D1's role layer as tenant data: roles are named grant sets managed through
/// operations, validated at definition time against the compiled permission catalogue —
/// the same registry-as-compiler pattern as tenant custom fields.
/// </summary>
[Operation("roles.define")]
[Authorize("roles.manage")]
public static class DefineRole
{
    public sealed record Input(string Name, List<string> Permissions);

    public sealed record Output(Guid RoleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, TamModel model, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Name, "^[a-z][a-z0-9-]*$"))
            return RoleFindings.InvalidName.At(nameof(Input.Name));

        // Registry-time check against the compiled catalogue: a grant must reference a
        // permission that actually exists in this build's manifest.
        var unknown = input.Permissions
            .Where(p => p != "*" && !model.Permissions.Contains(p))
            .ToList();
        if (unknown.Count > 0)
        {
            return new Result<Output>
            {
                Findings = unknown.Select(p =>
                    RoleFindings.UnknownPermission.With(("permission", p))
                        .At(nameof(Input.Permissions))).ToList(),
            };
        }

        var role = await db.Set<RoleEntity>().SingleOrDefaultAsync(
            x => x.TenantId == context.TenantId.Value && x.Name == input.Name, ct);
        if (role is null)
        {
            role = new RoleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                Name = input.Name,
            };
            db.Add(role);
        }
        role.PermissionsJson = System.Text.Json.JsonSerializer.Serialize(input.Permissions);

        return new Output(role.Id);
    }
}

[View("roles.list")]
[Authorize("roles.manage")]
public static class RoleList
{
    public sealed record Query();

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Permissions { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db) =>
        db.Set<RoleEntity>().Select(x => new Result
        {
            Id = x.Id, Name = x.Name, Permissions = x.PermissionsJson,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}
