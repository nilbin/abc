using Microsoft.AspNetCore.Http;
using Tam;
using Tam.AspNetCore;

namespace Erp;

/// <summary>
/// Demo stand-in for decision D1's role layer: three roles with grant sets, selected per
/// request via the X-Demo-Role header. Production replaces this with tenant-managed roles;
/// the enforcement path (Actor.Can in the pipeline) is already the real one.
/// </summary>
public sealed class DemoRoleActorProvider : IActorProvider
{
    private static readonly Dictionary<string, Actor> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = new("admin", "Alva Andersson", new HashSet<string> { "*" }),
        ["dispatcher"] = new("dispatcher", "Didrik Berg", new HashSet<string>
        {
            "orders.read", "orders.create", "orders.edit", "orders.complete",
            "customers.read", "customers.create",
        }),
        ["viewer"] = new("viewer", "Vera Lund", new HashSet<string>
        {
            "orders.read", "customers.read",
        }),
    };

    public Actor GetActor(HttpContext http)
    {
        var role = http.Request.Headers["X-Demo-Role"].FirstOrDefault() ?? "admin";
        return Roles.GetValueOrDefault(role, Roles["admin"]);
    }
}
