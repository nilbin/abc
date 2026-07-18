namespace Tam;

// The REACH seam (docs/35): the vocabulary for naming a people-set on a ROW (a folder ACL, a
// share list) — the object-side question the capability axis deliberately does not answer.
// Domains store ReachRef strings in their own ACL tables and ask the resolver at check time;
// providers answer containment over facts they own. Reach never touches actor resolution or
// the flat grant set (docs/28 D-AG3).

/// <summary>
/// A canonical, storable reference to a people-set: <c>kind</c> or <c>kind:id</c> —
/// <c>tenant</c>, <c>user:0d3f…</c>, <c>role:dispatcher</c>, <c>approvals.group:7a41…</c>.
/// The kind names a registered <see cref="IReachProvider"/>; the id's meaning is the
/// provider's. Parsing splits on the FIRST colon, so ids may contain colons.
/// </summary>
public sealed record ReachRef(string Kind, string? Id)
{
    public override string ToString() => Id is null ? Kind : $"{Kind}:{Id}";

    public static bool TryParse(string? value, out ReachRef reach)
    {
        reach = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var colon = value.IndexOf(':');
        var kind = colon < 0 ? value : value[..colon];
        var id = colon < 0 ? null : value[(colon + 1)..];
        if (!IsKind(kind) || id == "") return false;
        reach = new ReachRef(kind, id);
        return true;
    }

    /// <summary>Kind grammar: one or more dot-separated slugs (<c>user</c>, <c>approvals.group</c>).</summary>
    public static bool IsKind(string kind) =>
        kind.Length > 0 && kind.Split('.').All(Naming.IsSlug);
}

/// <summary>One pickable entry of a reach kind: the full canonical ref plus a display label
/// (a name from data, never a locale key — the label IS the data).</summary>
public sealed record ReachOption(ReachRef Ref, string Label);

/// <summary>
/// Answers for one reach KIND (docs/35): containment — does the ACTING actor fall within the
/// given reach? — and search — the pickable refs an ACL editor offers. Constructed per call
/// with constructor injection (<c>ITamDb</c>, … as ctor parameters), the gate idiom (docs/22
/// P2). Both answers are evaluated against the acting context's tenant; fail closed on
/// anything unresolvable.
/// </summary>
public interface IReachProvider
{
    Task<bool> ContainsAsync(ReachRef reach, OperationContext context, CancellationToken ct);

    Task<IReadOnlyList<ReachOption>> SearchAsync(
        string? search, OperationContext context, CancellationToken ct);

    /// <summary>The display label for ONE stored ref (docs/35 D-R6) — how an ACL surface shows
    /// "user:0d3f…" as a person's name. Null means "no label" and the caller falls back to the
    /// canonical string; the default keeps every existing provider compiling — describing is
    /// opt-in polish, containment stays the contract.</summary>
    Task<string?> DescribeAsync(ReachRef reach, OperationContext context, CancellationToken ct)
        => Task.FromResult<string?>(null);
}

/// <summary>A registered reach kind: the provider TYPE (ITamActivator-built per call) and the
/// declaring plugin — null for host/framework kinds. Plugin kinds are activation-gated at
/// resolution (docs/35 D-R3).</summary>
public sealed record ReachDefinition(string Kind, string? PluginId, Type ProviderType);

/// <summary>
/// A typed cross-entity reference (docs/35): entity KIND plus row id, with a canonical string
/// form (<c>order:8f3c…</c>) — one storable, filterable column instead of a per-domain
/// (kind, id) pair convention. EntityKey is the wire entity vocabulary AcceptsExtensions
/// already binds; consumers validate the key against the entities they accept.
/// </summary>
public readonly record struct EntityRef(string EntityKey, Guid Id)
{
    public override string ToString() => $"{EntityKey}:{Id:D}";

    public static bool TryParse(string? value, out EntityRef reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        var key = value[..colon];
        if (!key.Split('.').All(Naming.IsSlug)) return false;
        if (!Guid.TryParse(value[(colon + 1)..], out var id)) return false;
        reference = new EntityRef(key, id);
        return true;
    }

    public static EntityRef Parse(string value) =>
        TryParse(value, out var reference)
            ? reference
            : throw new FormatException($"'{value}' is not an EntityRef ('entityKey:guid').");
}

/// <summary>A host-declared MAGIC FOLDER binding (docs/35): when the named event commits, the
/// documents package materializes the folder rendered from the template — placeholders name
/// payload fields of the event's declared contract ("/order/{number}"). Every order gets its
/// folder without any handler knowing about documents. Verified at Build (DOC001).</summary>
public sealed record DocumentFolderBinding(string EventType, string PathTemplate);
