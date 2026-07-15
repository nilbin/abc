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
            .AddOperationType(typeof(DefinePolicy))
            .AddOperationType(typeof(ActivatePlugin))
            .AddOperationType(typeof(DeactivatePlugin))
            .AddOperationType(typeof(InstallPackage))
            .AddOperationType(typeof(UninstallPackage))
            .AddOperationType(typeof(DefineAutomationRule))
            .AddOperationType(typeof(RetireRule))
            .AddOperationType(typeof(CreateTenant))
            .AddOperationType(typeof(MoveTenant))
            .AddOperationType(typeof(RenameTenant))
            .AddOperationType(typeof(DefineUser))
            .AddOperationType(typeof(DeactivateUser))
            .AddOperationType(typeof(SetPlan))
            .AddOperationType(typeof(SetSetting))
            .AddOperationType(typeof(SetSecret))
            .AddOperationType(typeof(ScheduleIntegration))
            .AddOperationType(typeof(RunIntegration))
            .AddOperationType(typeof(RequeueDeadLetter))
            .AddViewType(typeof(UserList))
            .AddViewType(typeof(CurrentSubscription))
            .AddViewType(typeof(SettingList))
            .AddViewType(typeof(SecretList))
            .AddViewType(typeof(IntegrationRunList))
            .AddViewType(typeof(DeadLetterList))
            .AddViewType(typeof(ExtensionFieldList))
            .AddViewType(typeof(RoleList))
            .AddViewType(typeof(PolicyList))
            .AddViewType(typeof(TenantList))
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
        if (!model.ExtensibleEntityKeys.Contains(input.Entity))
            return ExtensionFindings.UnknownEntity.With(("entity", input.Entity)).At(nameof(Input.Entity));   // EXT007
        if (!SemanticTypes.ByKey.ContainsKey(input.Type))
            return ExtensionFindings.UnknownType.At(nameof(Input.Type));

        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Key, "^[a-z][a-zA-Z0-9]*$"))
            return ValidationFindings.InvalidValue.At(nameof(Input.Key));

        var collision = await tam.Db.Set<ExtensionFieldEntity>().AnyAsync(
            x => x.Entity == input.Entity && x.Key == input.Key, ct);
        if (collision)
            return ExtensionFindings.KeyConflict.At(nameof(Input.Key));       // EXT005

        if (!input.Labels.ContainsKey(model.DefaultCulture))
            return ExtensionFindings.MissingLabel.At(nameof(Input.Labels));   // EXT006

        var entity = new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
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
            x => x.Id == input.FieldId, ct);
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

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
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
    /// <summary>A grant may carry a ":own" record-scope suffix; the catalogue holds the base name.</summary>
    public static string TrimScope(string permission) =>
        permission.EndsWith(":own", StringComparison.Ordinal) ? permission[..^4] : permission;

    public static IReadOnlyList<Finding> Validate(
        string name, IReadOnlyList<string> permissions, TamModel model, string field,
        IReadOnlyDictionary<string, string>? levels = null)
    {
        var findings = new List<Finding>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9-]*$"))
            findings.Add(RoleFindings.InvalidName.At(field));
        findings.AddRange(permissions
            .Where(p => p != "*" && !model.Permissions.Contains(TrimScope(p)))
            .Select(p => RoleFindings.UnknownPermission.With(("permission", p)).At(field)));
        // Reserved permissions (subscriptions.manage) are never grantable through a tenant role,
        // whether authored directly or shipped in a package — closes the wildcard-admin bypass.
        // (Access levels can't smuggle them either: AccessLevels.Expand never yields a reserved atom.)
        findings.AddRange(permissions
            .Where(p => Actor.Reserved.Contains(TrimScope(p)))
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

public static class PolicyFindings
{
    public static readonly FindingFactory UnknownResource = Finding.Error("policies.unknown-resource");
    public static readonly FindingFactory UnknownScope = Finding.Error("policies.unknown-scope");
    public static readonly FindingFactory InvalidName = Finding.Error("policies.invalid-name");
}

/// <summary>
/// Access policies (docs/27 Axis 2): a named resource → scope map ("orders" → "own"), the DATA-SCOPE
/// menu memberships pick from independently of roles. v1 scope kinds: all | own — `where`/`shared`
/// stay design-deferred (attribute predicates need the actor-attributes design; D-A2 note).
/// </summary>
[Operation("policies.define")]
[Authorize("roles.manage")]
public static class DefinePolicy
{
    public sealed record Input(
        string Name,
        [property: LabelKey("labels.scopes")] Dictionary<string, string> Scopes);

    public sealed record Output(Guid PolicyId);

    private static readonly HashSet<string> Known = ["all", "own"];

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        // Scope values are stored CANONICAL (lower-case): the actor-side narrowing compares ordinal,
        // so accepting "Own" verbatim would validate here and then silently fail OPEN at enforcement.
        var scopes = input.Scopes.ToDictionary(s => s.Key, s => s.Value.ToLowerInvariant());
        var findings = new List<Finding>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Name, "^[a-z][a-z0-9-]*$"))
            findings.Add(PolicyFindings.InvalidName.At(nameof(Input.Name)));
        foreach (var (resource, scope) in scopes)
        {
            if (!AccessLevels.Catalog(model).ContainsKey(resource))
                findings.Add(PolicyFindings.UnknownResource.With(("resource", resource)).At(nameof(Input.Scopes)));
            if (!Known.Contains(scope))
                findings.Add(PolicyFindings.UnknownScope.With(("scope", scope)).At(nameof(Input.Scopes)));
        }
        if (findings.Count > 0) return new Result<Output> { Findings = findings };

        var policy = await tam.Db.Set<AccessPolicyEntity>().SingleOrDefaultAsync(
            x => x.Name == input.Name, ct);
        if (policy is null)
        {
            policy = new AccessPolicyEntity { Id = Guid.NewGuid(), Name = input.Name };
            tam.Db.Add(policy);
        }
        policy.ScopesJson = JsonSerializer.Serialize(scopes);
        return new Output(policy.Id);
    }
}

[View("policies.list")]
[Authorize("roles.manage")]
public static class PolicyList
{
    public sealed record Query();

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        [LabelKey("labels.scopes")]
        public string Scopes { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context) =>
        tam.Db.Set<AccessPolicyEntity>()
            .Select(x => new Result { Id = x.Id, Name = x.Name, Scopes = x.ScopesJson });

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
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
        List<string> Permissions,
        [property: LabelKey("labels.levels")] Dictionary<string, string>? Levels = null);

    public sealed record Output(Guid RoleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        var invalid = RoleRules.Validate(
            input.Name, input.Permissions, model, nameof(Input.Permissions), input.Levels);
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
        role.PermissionsJson = JsonSerializer.Serialize(input.Permissions);
        role.LevelsJson = JsonSerializer.Serialize(input.Levels ?? []);

        return new Output(role.Id);
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
        [LabelKey("labels.levels")]
        public string Levels { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context) =>
        tam.Db.Set<RoleEntity>()
            .Select(x => new Result
            {
                Id = x.Id, Name = x.Name, Permissions = x.PermissionsJson, Levels = x.LevelsJson,
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

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var changes = tam.Db.Set<AuditChange>().AsQueryable();
        if (query.Entity is { Length: > 0 }) changes = changes.Where(x => x.Entity == query.Entity);
        if (query.EntityId is { Length: > 0 }) changes = changes.Where(x => x.EntityId == query.EntityId);

        // Entries carry the tenant; joining through them scopes the change rows.
        return changes
            .Join(tam.Db.Set<AuditEntry>(),
                c => c.EntryId, e => e.Id, (c, e) => new Result
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
