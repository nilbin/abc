// Framework ENTITIES (docs/29): the persisted shapes behind the pipeline and the
// framework packages. Conventions that map them live in ModelConventions.cs.

namespace Tam.EntityFrameworkCore;

public enum InboxStatus
{
    Pending,
    Processed,
    Failed,
    Dead,
}

/// <summary>
/// Integration inbox (docs/10): every received external row is persisted before processing,
/// retried from its stored payload on later runs, and dead-lettered after repeated failure —
/// so a fixed root cause (e.g. the missing customer) recovers without re-sending anything.
/// </summary>
public sealed class InboxRecord : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Key { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public InboxStatus Status { get; set; } = InboxStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>ISO-8601 twin of <see cref="ReceivedAt"/>: lexicographic == chronological, so
    /// the backlog drain can ORDER BY + TAKE server-side on every provider (SQLite cannot
    /// order DateTimeOffset) instead of materializing the whole backlog (review-round-4 #8).</summary>
    public string ReceivedAtIso { get; set; } = "";

    public DateTimeOffset? ProcessedAt { get; set; }

    // Exponential-backoff gate (docs/10 + docs/25): a failed row is not re-driven before this
    // instant, so a rapid re-POST can't hammer a failing row — the same RetryPolicy the outbound
    // queue uses. Null/empty means "process immediately" (a never-attempted Pending row).
    public string? NextAttemptIso { get; set; }
}

/// <summary>
/// Outbound retry queue (docs/25): a failed event/schedule push is enqueued here and re-driven by
/// the IntegrationRetryDriver with the same attempts/backoff/dead-letter semantics as the inbound
/// inbox. The event payload is stored so a retry replays the exact push without the source event.
/// </summary>
public sealed class OutboundTaskEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Trigger { get; set; } = "";           // event | schedule
    public string? PayloadJson { get; set; }             // event payload to replay (null for schedule)
    public InboxStatus Status { get; set; } = InboxStatus.Failed;   // Failed (awaiting) | Processed | Dead
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string NextAttemptIso { get; set; } = "";     // due time AND optimistic-concurrency lease
    public string CreatedAtIso { get; set; } = "";
    public string? CompletedAtIso { get; set; }
}

/// <summary>Outbox row: an explicit event effect, persisted in the operation's transaction (docs/09).</summary>
public sealed class OutboxRecord : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string CreatedAtIso { get; set; } = "";
    public string? DispatchedAtIso { get; set; }

    // Multi-instance safety (review-round-3): the dispatcher takes a time-boxed lease before
    // dispatching so only one instance delivers a given row; ClaimedUntilIso doubles as the
    // optimistic-concurrency token that makes the claim atomic. A crash mid-dispatch lets the
    // lease lapse and another instance re-delivers (at-least-once preserved).
    public string? ClaimedUntilIso { get; set; }
    // Poison-message dead-letter, mirroring the inbox: a row that keeps throwing is parked after
    // a cap instead of blocking every newer event behind it forever.
    public int Attempts { get; set; }
    public string? DeadAtIso { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// A tenant's subscription (docs/24): the plan, seat ceiling and plugin entitlements a billing
/// provider drives through subscriptions.set-plan. One row per tenant; its absence means the
/// free default, so the framework is fully usable without any billing system wired up.
/// </summary>
public sealed class SubscriptionEntity : ITenantScoped, IVersioned
{
    public string TenantId { get; set; } = "";
    public string Plan { get; set; } = "unconfigured";
    public int Seats { get; set; } = 2;
    public string EntitlementsJson { get; set; } = "[]";
    public string Status { get; set; } = "active";
    public string? RenewsAtIso { get; set; }

    /// <summary>Concurrency token: consuming a seat bumps this (the "seat lease"), so two
    /// users.define racing past the count check conflict at SaveChanges instead of both
    /// slipping under the ceiling.</summary>
    public long Version { get; set; }

    public IReadOnlyList<string> Entitlements() =>
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(EntitlementsJson) ?? [];

    public bool Entitles(string pluginId)
    {
        var entitlements = Entitlements();
        return entitlements.Contains("*") || entitlements.Contains(pluginId);
    }
}

/// <summary>
/// Framework user record: identity is data like roles are (D1). Which authentication mechanism
/// proves the identity (the built-in OpenIddict server, an external IdP, a header in dev) is an
/// application decision behind IActorProvider — the user store is mechanism-agnostic.
/// </summary>
/// <summary>
/// A platform-global identity (docs/26 Option 1): one human, one account, reachable across any
/// number of tenants — related or not. Deliberately NOT <see cref="ITenantScoped"/>: the account is
/// owned by the platform, not a tenant. Access to a tenant is a <see cref="TenantMembershipEntity"/>.
/// </summary>
public sealed class AccountEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";        // the global, unique login handle
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Active { get; set; } = true;
}

/// <summary>A role assigned on a membership. <see cref="Cascade"/> (docs/26 D-H5) extends the role
/// to every descendant of the membership's node — per assignment, so one membership can cascade
/// "orders-manager" while "users-admin" stays node-local. Names bind to the role definitions of the
/// MEMBERSHIP's node, never the active node's (docs/27 — cross-level role resolution).</summary>
public sealed record RoleAssignment(string Name, bool Cascade);

/// <summary>
/// An account's access to one tenant (docs/26 + docs/27): the join that carries authorization. Its
/// <see cref="RolesJson"/> is the capability axis (docs/27); the data-scope axis (access policies)
/// lands with the scope work. Tenant-scoped, so the global filter already limits a tenant's
/// membership list to that tenant. The same account has independent memberships in other tenants.
/// </summary>
public sealed class TenantMembershipEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid AccountId { get; set; }
    public string RolesJson { get; set; } = "[]";
    public bool Active { get; set; } = true;

    /// <summary>Role assignments; accepts both shapes — `["admin"]` (legacy flat, cascade: false)
    /// and `[{"name":"admin","cascade":true}]` (D-H5) — so existing rows keep working.</summary>
    public IReadOnlyList<RoleAssignment> Roles()
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(
            RolesJson) ?? [];
        var assignments = new List<RoleAssignment>(parsed.Count);
        foreach (var element in parsed)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.String)
                assignments.Add(new RoleAssignment(element.GetString() ?? "", false));
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                && element.TryGetProperty("name", out var name))
                assignments.Add(new RoleAssignment(
                    name.GetString() ?? "",
                    element.TryGetProperty("cascade", out var cascade) && cascade.GetBoolean()));
        }
        return assignments;
    }
}

/// <summary>
/// A tenant node and its place in the hierarchy (docs/26): <see cref="Path"/> is the ancestor chain
/// of ids including self ("acme.eu.sales"), so "in my subtree" is a path-prefix test — one indexed
/// range scan, not a recursive query. Ids must not contain '.', the path separator. The registry of
/// tenants itself; not tenant-scoped.
/// </summary>
public sealed class TenantEntity : IVersioned
{
    public string Id { get; set; } = "";          // the tenant id used as TenantId across the model
    public string? ParentId { get; set; }
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Concurrency token: structural operations (move, create-under) are read-modify-write
    /// over the registry with a cross-row invariant (path consistency), so a stale write must FAIL
    /// at SaveChanges rather than silently persist an orphaned path.</summary>
    public long Version { get; set; }

    /// <summary>The ancestor chain including self, from the path segments (segment = tenant id).</summary>
    public IReadOnlyList<string> AncestorIds() => Path.Split('.');

    /// <summary>True when <paramref name="other"/> is this node or a descendant of it. Segment-safe:
    /// "demo" is not an ancestor of "demo2" — the prefix must end at a separator.</summary>
    public static bool IsSelfOrDescendant(string ancestorPath, string otherPath) =>
        otherPath == ancestorPath || otherPath.StartsWith(ancestorPath + ".", StringComparison.Ordinal);
}

/// <summary>A pending invitation (docs/26): the account + membership exist (the seat is consumed at
/// invite time, so the admin's count is predictable), but the account has no password until the
/// invitee accepts. Only the token's SHA-256 lands in the database — the mailed link is the secret.</summary>
public sealed class InviteEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid AccountId { get; set; }
    public string TokenHash { get; set; } = "";
    public string ExpiresAtIso { get; set; } = "";
    public string? AcceptedAtIso { get; set; }
}

/// <summary>Tenant-managed role: a named grant set (decision D1). Managed only through operations.
/// Carries BOTH authoring shapes (docs/27 D-A1): explicit permission atoms and per-resource access
/// levels ({"orders":"manage"}); levels expand to atoms at load time (see Tam.AccessLevels).</summary>
public sealed class RoleEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PermissionsJson { get; set; } = "[]";
    public string LevelsJson { get; set; } = "{}";

    /// <summary>Retire-don't-drop (docs/29 conventions): grants stop applying, the audit
    /// trail keeps its referent, and roles.define under the same name un-retires.</summary>
    public bool Retired { get; set; }

    public IReadOnlySet<string> Permissions() =>
        System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(PermissionsJson) ?? [];

    public IReadOnlyDictionary<string, string> Levels() =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(LevelsJson) ?? [];
}

/// <summary>Per-tenant plugin activation (docs/22): the row's existence IS the activation.
/// The plugin's code is compiled into the deployment either way; for tenants without the row,
/// its contributions are omitted from the manifest and its endpoints answer 404.</summary>
public sealed class PluginActivationEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string PluginId { get; set; } = "";
}

/// <summary>
/// A tenant automation rule (docs/22 P5): trigger operation + Px condition (stored as the
/// structured AST, never a parsed string) + a blocking finding. Managed only through
/// operations; retire-don't-delete like every registry artifact.
/// </summary>
public sealed class AutomationRuleEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OnOperation { get; set; } = "";
    // docs/22 effect-triggered rules: a rule triggers on EITHER an operation OR a domain event.
    // Exactly one is set; OnEvent rules evaluate on the outbox dispatch path (set-field only).
    public string? OnEvent { get; set; }
    public string ConditionJson { get; set; } = "";
    public string? TargetField { get; set; }
    public string MessagesJson { get; set; } = "{}";
    public bool Retired { get; set; }

    // docs/22 row.* increment: resolved ONCE at rules.define when the condition references
    // row.* — the target entity's wire key and the operation input field carrying its id.
    public string? RowEntityKey { get; set; }
    public string? RowIdField { get; set; }

    // docs/22 action catalog: null = the blocking finding (v1's one action). Otherwise the
    // validated action spec ({"type":"set-field",...} | {"type":"publish-event"}) — action
    // rules run in the TRANSACTIONAL gate phase (they write; findings stay pure).
    public string? ActionJson { get; set; }
}

/// <summary>Non-secret per-tenant integration config (docs/25): base URLs, account ids, flags.
/// Readable in the clear; managed through settings.set / settings.list.</summary>
public sealed class TenantSettingEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A per-tenant secret (docs/25): API keys, tokens, passwords. Stored ENCRYPTED — the column
/// only ever holds the Data-Protection ciphertext; the plaintext is decrypted transiently when
/// an integration runs and is never returned by any view or operation output.
/// </summary>
public sealed class TenantSecretEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Key { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
}

/// <summary>A schedule for an outbound integration (docs/25): spec + next-run bookkeeping.</summary>
public sealed class IntegrationScheduleEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Spec { get; set; } = "";          // "every:15m" | "daily:02:00"
    public bool Enabled { get; set; } = true;
    public string NextRunIso { get; set; } = "";
    public string? LastRunIso { get; set; }
    public string? LastStatus { get; set; }
}

/// <summary>One execution of an integration (docs/25): the audit trail for external calls.</summary>
public sealed class IntegrationRunEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IntegrationId { get; set; } = "";
    public string Trigger { get; set; } = "";       // event | schedule | manual
    public string Status { get; set; } = "";        // ok | failed
    public string? Detail { get; set; }
    public string RanAtIso { get; set; } = "";
}

/// <summary>An installed tenant package (docs/22 P3): the bundle document is retained so
/// upgrades can diff against what was actually applied.</summary>
public sealed class PackageInstallationEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Package { get; set; } = "";
    public int Version { get; set; }
    public string DocumentJson { get; set; } = "";
    public string InstalledAtIso { get; set; } = "";
}

/// <summary>A tenant's nav override (docs/30 v2): presentation-only mutations of one declared
/// nav node — hide, per-culture relabel, reorder, regroup under another section. Retire
/// restores the declared default. An override whose node vanished (plugin deactivated) is
/// dormant, not broken. Nav is discoverability, never authorization (D-N6).</summary>
public sealed class NavOverrideEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public bool Hidden { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public int? Order { get; set; }
    public string? Parent { get; set; }

    public IReadOnlyDictionary<string, string> Labels() =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(LabelsJson) ?? [];
}

/// <summary>Registry storage for tenant-defined fields (docs/15). Managed only through operations.</summary>
public sealed class ExtensionFieldEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Key { get; set; } = "";
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public int? MaxLength { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public string? DescriptionsJson { get; set; }
    public string? OptionsJson { get; set; }
    public ExtensionFieldState State { get; set; } = ExtensionFieldState.Active;

    /// <summary>Set when the field arrived via a tenant package (docs/22 P3) — uninstall
    /// retires exactly these, never fields the tenant defined by hand.</summary>
    public string? Package { get; set; }

    public ExtensionFieldSpec ToSpec() => new(
        Key, Entity, Type, Required, MaxLength,
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(LabelsJson) ?? [],
        DescriptionsJson is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(DescriptionsJson),
        OptionsJson is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(OptionsJson),
        State);
}
