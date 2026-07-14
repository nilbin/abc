using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.SystemOps;

public static class SystemModule
{
    /// <summary>
    /// Registers the framework's own operations and views — tenant custom fields (docs/15),
    /// roles (D1), and the audit read model (D3) — plus their locale defaults from embedded
    /// resources. These are framework capabilities, not app code: the app only decides whether
    /// and where to expose them (grids/nav).
    /// </summary>
    public static TamModelBuilder AddTamSystem(this TamModelBuilder builder)
    {
        foreach (var culture in new[] { "sv", "en" })
        {
            using var stream = typeof(SystemModule).Assembly
                .GetManifestResourceStream($"Tam.AspNetCore.locales.{culture}.json");
            if (stream is null) continue;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            builder.LocaleDefaults(culture, entries);
        }

        return builder
            .AddOperationType(typeof(DefineExtensionField))
            .AddOperationType(typeof(RetireExtensionField))
            .AddOperationType(typeof(DefineRole))
            .AddOperationType(typeof(ActivatePlugin))
            .AddOperationType(typeof(DeactivatePlugin))
            .AddOperationType(typeof(InstallPackage))
            .AddOperationType(typeof(UninstallPackage))
            .AddOperationType(typeof(DefineAutomationRule))
            .AddOperationType(typeof(RetireRule))
            .AddViewType(typeof(ExtensionFieldList))
            .AddViewType(typeof(RoleList))
            .AddViewType(typeof(AuditLog))
            .AddViewType(typeof(PluginList))
            .AddViewType(typeof(PackageList))
            .AddViewType(typeof(RuleList));
    }
}

// ---------------------------------------------------------------- tenant custom fields

/// <summary>
/// The tenant field registry is managed through ordinary operations — authorized, audited,
/// registry-checked at definition time (docs/15). No deploys involved.
/// </summary>
[Operation("extensions.define-field")]
[Authorize("extensions.manage")]
public static class DefineExtensionField
{
    public sealed record Input(
        string Entity,
        string Key,
        string Type,
        Dictionary<string, string> Labels,
        bool Required = false,
        int? MaxLength = null,
        Dictionary<string, string>? Descriptions = null,
        List<string>? Options = null);

    public sealed record Output(Guid FieldId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        // Registry-time diagnostics: the runtime twin of the compiler's rule set.
        if (!SemanticTypes.ByKey.ContainsKey(input.Type))
            return ExtensionFindings.UnknownType.At(nameof(Input.Type));

        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Key, "^[a-z][a-zA-Z0-9]*$"))
            return ValidationFindings.InvalidValue.At(nameof(Input.Key));

        var collision = await tam.Db.Set<ExtensionFieldEntity>().AnyAsync(
            x => x.TenantId == context.TenantId.Value && x.Entity == input.Entity && x.Key == input.Key, ct);
        if (collision)
            return ExtensionFindings.KeyConflict.At(nameof(Input.Key));       // EXT005

        if (!input.Labels.ContainsKey(model.DefaultCulture))
            return ExtensionFindings.MissingLabel.At(nameof(Input.Labels));   // EXT006

        var entity = new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            Entity = input.Entity,
            Key = input.Key,
            Type = input.Type,
            Required = input.Required,
            MaxLength = input.MaxLength,
            LabelsJson = JsonSerializer.Serialize(input.Labels),
            DescriptionsJson = input.Descriptions is null
                ? null
                : JsonSerializer.Serialize(input.Descriptions),
            OptionsJson = input.Options is null ? null : JsonSerializer.Serialize(input.Options),
            State = ExtensionFieldState.Active,
        };
        tam.Db.Add(entity);
        return new Output(entity.Id);
    }
}

[Operation("extensions.retire-field")]
[Authorize("extensions.manage")]
public static class RetireExtensionField
{
    public sealed record Input(Guid FieldId);

    public sealed record Output(Guid FieldId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var field = await tam.Db.Set<ExtensionFieldEntity>().SingleOrDefaultAsync(
            x => x.Id == input.FieldId && x.TenantId == context.TenantId.Value, ct);
        if (field is null) return PipelineFindings.NotFound.Create();

        field.State = ExtensionFieldState.Retired;   // data preserved, key reserved forever
        return new Output(field.Id);
    }
}

[View("extensions.fields")]
[Authorize("extensions.manage")]
public static class ExtensionFieldList
{
    public sealed record Query(string? Entity = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string Entity { get; init; } = "";
        public string Key { get; init; } = "";
        public string Type { get; init; } = "";
        public bool Required { get; init; }
        public ExtensionFieldState State { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var fields = tam.Db.Set<ExtensionFieldEntity>().AsQueryable();
        if (query.Entity is { Length: > 0 }) fields = fields.Where(x => x.Entity == query.Entity);
        return fields.Select(x => new Result
        {
            Id = x.Id, Entity = x.Entity, Key = x.Key, Type = x.Type,
            Required = x.Required, State = x.State,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Key)).DefaultSort(nameof(Result.Key));
}

// ---------------------------------------------------------------- roles (decision D1)

public static class RoleFindings
{
    public static readonly FindingFactory UnknownPermission = Finding.Error("roles.unknown-permission");
    public static readonly FindingFactory InvalidName = Finding.Error("roles.invalid-name");
}

/// <summary>
/// Roles are tenant data managed through operations, validated at definition time against the
/// compiled permission catalogue — the same registry-as-compiler pattern as custom fields.
/// </summary>
[Operation("roles.define")]
[Authorize("roles.manage")]
public static class DefineRole
{
    public sealed record Input(string Name, List<string> Permissions);

    public sealed record Output(Guid RoleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Name, "^[a-z][a-z0-9-]*$"))
            return RoleFindings.InvalidName.At(nameof(Input.Name));

        var unknown = input.Permissions
            .Where(p => p != "*" && !model.Permissions.Contains(TrimScope(p)))
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

        var role = await tam.Db.Set<RoleEntity>().SingleOrDefaultAsync(
            x => x.TenantId == context.TenantId.Value && x.Name == input.Name, ct);
        if (role is null)
        {
            role = new RoleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                Name = input.Name,
            };
            tam.Db.Add(role);
        }
        role.PermissionsJson = JsonSerializer.Serialize(input.Permissions);

        return new Output(role.Id);
    }

    private static string TrimScope(string permission) =>
        permission.EndsWith(":own", StringComparison.Ordinal) ? permission[..^4] : permission;
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
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam) =>
        tam.Db.Set<RoleEntity>().Select(x => new Result
        {
            Id = x.Id, Name = x.Name, Permissions = x.PermissionsJson,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}

// ---------------------------------------------------------------- audit read model (decision D3)

/// <summary>Entity history is an ordinary indexed query over the same-transaction audit tables.</summary>
[View("audit.entries")]
[Authorize("audit.read")]
public static class AuditLog
{
    public sealed record Query(string? Entity = null, string? EntityId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.timestamp")]
        public string Timestamp { get; init; } = "";
        [LabelKey("labels.operation")]
        public string OperationId { get; init; } = "";
        [LabelKey("labels.actor")]
        public string ActorName { get; init; } = "";
        public string Entity { get; init; } = "";
        [LabelKey("labels.entity-id")]
        public string EntityId { get; init; } = "";
        public string Field { get; init; } = "";
        [LabelKey("labels.old-value")]
        public string? OldValue { get; init; }
        [LabelKey("labels.new-value")]
        public string? NewValue { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var changes = tam.Db.Set<AuditChange>().AsQueryable();
        if (query.Entity is { Length: > 0 }) changes = changes.Where(x => x.Entity == query.Entity);
        if (query.EntityId is { Length: > 0 }) changes = changes.Where(x => x.EntityId == query.EntityId);

        return changes
            .Join(tam.Db.Set<AuditEntry>(), c => c.EntryId, e => e.Id, (c, e) => new Result
            {
                Id = c.Id,
                // ISO string column: sortable/formattable on every provider (SQLite incl.)
                Timestamp = e.TimestampIso.Substring(0, 16).Replace("T", " "),
                OperationId = e.OperationId,
                ActorName = e.ActorName,
                Entity = c.Entity,
                EntityId = c.EntityId,
                Field = c.Field.Length > 0 ? c.Field : c.Kind,
                OldValue = c.OldValue,
                NewValue = c.NewValue,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Timestamp), nameof(Result.OperationId))
        .DefaultSort(nameof(Result.Timestamp), descending: true);
}
