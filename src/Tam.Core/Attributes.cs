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

public sealed record Actor(string Id, string Name, IReadOnlySet<string> Permissions)
{
    public bool Can(string permission) => Permissions.Contains("*") || Permissions.Contains(permission);
}
