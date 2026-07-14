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
/// Grants may carry a record-scope qualifier (decision D1): "orders.read" grants everything,
/// "orders.read:own" grants only records owned by the actor. The entity declares what "own"
/// means; the grant declares who gets which scope.
/// </summary>
public sealed record Actor(string Id, string Name, IReadOnlySet<string> Permissions)
{
    public bool Can(string permission) =>
        Permissions.Contains("*")
        || Permissions.Contains(permission)
        || Permissions.Contains(permission + ":own");

    /// <summary>"all" or "own" for a granted permission.</summary>
    public string Scope(string permission) =>
        Permissions.Contains("*") || Permissions.Contains(permission) ? "all"
        : Permissions.Contains(permission + ":own") ? "own"
        : "none";

    public bool OwnsOnly(string permission) => Scope(permission) == "own";
}

public static class Scoping
{
    /// <summary>Declarative row scope for views: one line per view, enforced in the query.</summary>
    public static IQueryable<T> ScopedTo<T>(
        this IQueryable<T> source,
        OperationContext context,
        string permission,
        System.Linq.Expressions.Expression<Func<T, string?>> owner)
    {
        if (!context.Actor.OwnsOnly(permission)) return source;
        var actorId = context.Actor.Id;
        var parameter = owner.Parameters[0];
        var body = System.Linq.Expressions.Expression.Equal(
            owner.Body, System.Linq.Expressions.Expression.Constant(actorId, typeof(string)));
        return source.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    /// <summary>Operation-side scope precondition: same rule the view enforced, re-checked authoritatively.</summary>
    public static Result CheckOwnership(
        this OperationContext context, string permission, string? ownerActorId)
    {
        if (!context.Actor.OwnsOnly(permission)) return Result.Success();
        return ownerActorId == context.Actor.Id
            ? Result.Success()
            : PipelineFindings.NotAuthorized.With(("permission", permission + ":own"));
    }
}
