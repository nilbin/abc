namespace Tam;

[AttributeUsage(AttributeTargets.Class)]
public sealed class OperationAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ViewAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class AuthorizeAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>Marks an operation as carrying tenant extension changes for the given extensible entity.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AcceptsExtensionsAttribute(Type entity) : Attribute
{
    public Type Entity { get; } = entity;
}

/// <summary>
/// Field-level masking (docs/27 D-A3): the field is visible/writable only to actors holding the
/// named permission atom (e.g. "customers.sensitive"). Read masking drops it from views and the
/// manifest; write masking rejects any input that carries it. The atom joins the compiled permission
/// catalogue so roles can grant it — Manage level includes it, View/Edit do not.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class SensitiveAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerDerivationAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>Input members (by name) whose changes invalidate this derivation.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DependsOnAttribute(params string[] members) : Attribute
{
    public string[] Members { get; } = members;
}

public enum InvocationSource
{
    Web,
    Admin,
    Mobile,
    Mcp,
    Integration,
    Workflow,
    ScheduledJob,
    Internal,
}

public readonly record struct TenantId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// The actor is a flat grant set — capability atoms only (docs/28 D-AG1/D-AG2): row reach is
/// either the tenancy dimension (tree scopes, framework-owned end to end) or a DOMAIN pattern
/// (paired atoms — "orders.read" own-scoped by default, "orders.read-all" widening — enforced
/// via <see cref="Scoping.ScopedUnless{T}"/> / <see cref="Scoping.CheckOwnershipUnless"/>).
/// </summary>
public sealed record Actor(string Id, string Name, IReadOnlySet<string> Permissions)
{
    /// <summary>
    /// Permissions a wildcard grant ("*") deliberately does NOT confer. These gate actions that
    /// change the tenant's own commercial standing — provisioning seats, entitling plugins — which
    /// the billing provider drives, never a tenant admin or a plugin running as the system actor.
    /// Granting one requires naming it explicitly; "*" is "everything the app does", not "everything
    /// the platform can do to itself". Closes the self-service entitlement bypass (docs/24).
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string> { "subscriptions.manage" };

    /// <summary>True for a reserved atom OR its widening twin (docs/28): "subscriptions.manage" is
    /// reserved, and so is "subscriptions.manage-all" — otherwise a widening atom on a reserved
    /// base would slip past the wildcard carve-out and the role-definition guard. Use this wherever
    /// the plain <see cref="Reserved"/> set was, so the twin can never be granted or expanded.</summary>
    public static bool IsReserved(string permission) =>
        Reserved.Contains(permission)
        || (permission.EndsWith("-all", StringComparison.Ordinal)
            && Reserved.Contains(permission[..^4]));

    public bool Can(string permission) =>
        Permissions.Contains(permission)
        || (Permissions.Contains("*") && !IsReserved(permission));
}

/// <summary>
/// Declares the WIDENING atom a scoped view/operation consults (docs/28 D-AG2): the resource is
/// own-scoped by default and this capability lifts the restriction ("orders.read-all"). The
/// declaration puts the atom into the compiled catalogue (so roles can grant it and levels can
/// expand into it) and lets the analyzer verify the class actually applies the scope (TAM006) —
/// the fail-closed property policy-authored scopes could never have.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class WidensAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

public static class Scoping
{
    /// <summary>Declarative row scope for views (the paired-atom pattern, docs/28): rows are
    /// restricted to the actor's own UNLESS the actor holds the widening capability. Declare the
    /// atom with <see cref="WidensAttribute"/> on the view class — TAM006 enforces the pairing.</summary>
    public static IQueryable<T> ScopedUnless<T>(
        this IQueryable<T> source,
        OperationContext context,
        string wideningPermission,
        System.Linq.Expressions.Expression<Func<T, string?>> owner)
    {
        if (context.Actor.Can(wideningPermission)) return source;
        var actorId = context.Actor.Id;
        var parameter = owner.Parameters[0];
        var body = System.Linq.Expressions.Expression.Equal(
            owner.Body, System.Linq.Expressions.Expression.Constant(actorId, typeof(string)));
        return source.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    /// <summary>Operation-side twin: the same rule the view enforced, re-checked authoritatively
    /// on the target row before mutating — a stale/forged id cannot escape the scope.</summary>
    public static Result CheckOwnershipUnless(
        this OperationContext context, string wideningPermission, string? ownerActorId)
    {
        if (context.Actor.Can(wideningPermission)) return Result.Success();
        return ownerActorId == context.Actor.Id
            ? Result.Success()
            : PipelineFindings.NotAuthorized.With(("permission", wideningPermission));
    }
}
