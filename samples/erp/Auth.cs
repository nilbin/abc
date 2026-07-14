using Microsoft.AspNetCore.Http;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp;

/// <summary>
/// Decision D1's role layer, resolved from tenant data: the X-Demo-Role header stands in for
/// real authentication and names a role stored in the roles registry (see Features/Roles.cs).
/// Enforcement is the real path — Actor.Can in the pipeline; only the identity source is demo.
/// </summary>
public sealed class DbRoleActorProvider : IActorProvider
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = "Alva Andersson",
        ["dispatcher"] = "Didrik Berg",
        ["viewer"] = "Vera Lund",
    };

    public Actor GetActor(HttpContext http)
    {
        var roleName = http.Request.Headers["X-Demo-Role"].FirstOrDefault() ?? "admin";
        var db = http.RequestServices.GetRequiredService<ErpDbContext>();

        var role = db.Set<RoleEntity>().FirstOrDefault(
            x => x.TenantId == Seed.Tenant && x.Name == roleName);

        var permissions = role?.Permissions()
            ?? (roleName == "admin" ? new HashSet<string> { "*" } : []);

        return new Actor(
            roleName,
            DisplayNames.GetValueOrDefault(roleName, roleName),
            permissions);
    }
}
