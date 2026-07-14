using Microsoft.AspNetCore.Http;
using Tam.AspNetCore;

namespace Erp;

/// <summary>
/// Demo identity: the X-Demo-Role header stands in for authentication. Grant resolution is the
/// framework's registry-backed <see cref="RoleActorProvider"/>; only the identity source and
/// the demo persona names live here.
/// </summary>
public sealed class DemoActorProvider() : RoleActorProvider(
    http => http.Request.Headers["X-Demo-Role"].FirstOrDefault() ?? "admin",
    role => Names.GetValueOrDefault(role, role))
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = "Alva Andersson",
        ["dispatcher"] = "Didrik Berg",
        ["viewer"] = "Vera Lund",
        ["technician"] = "Tekla Nilsson",
    };
}
