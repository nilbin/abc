namespace Tam;

/// <summary>
/// Names a plugin assembly's permanent namespace prefix (docs/22, decision D8). Every id the
/// plugin contributes — operations, views, forms, grids, permissions — must start with
/// "{id}." (PLG001). Wire-name permanence (D4) applies to the prefix itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TamPluginAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>
/// A compiled, curated extensibility module: reviewed code bound at host build time, activated
/// per tenant as runtime data. Tenants never upload code — that line is D8's core commitment.
/// </summary>
public interface ITamPlugin
{
    void Configure(PluginBuilder plugin);
}

/// <summary>A registered plugin in the compiled model. Activation state is tenant data, not model.</summary>
public sealed record PluginDefinition(string Id)
{
    public string TitleKey => $"plugins.{Id}.title";
}

/// <summary>
/// Names a FRAMEWORK PACKAGE: the framework-trust tier of the plugin system (docs/22). A package
/// registers through the same <see cref="PluginBuilder"/> surface a vendor plugin uses — the
/// framework's own admin capabilities dogfood the seams they sell — but differs on the tier axes:
/// always active for every tenant (never in the activation table, never entitlement-gated), and
/// it CLAIMS existing wire prefixes ("users", "audit") instead of being namespaced under its id —
/// those wire names are live and permanent (D4). Prefix claims are validated like PLG001.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TamPackageAttribute(string id, params string[] prefixes) : Attribute
{
    public string Id { get; } = id;

    /// <summary>The wire prefixes this package owns; every id/permission it registers must sit
    /// under one of them ("users" covers "users.invite" and permission "users.manage").</summary>
    public string[] Prefixes { get; } = prefixes;
}

/// <summary>A framework package in the compiled model. Always active — there is no row.</summary>
public sealed record PackageDefinition(string Id, IReadOnlyList<string> Prefixes);

/// <summary>
/// A plugin-packaged extension field on a HOST entity (docs/22 P2): same spec, same
/// ExtensionData storage, same wire channel as tenant custom fields — compiled origin,
/// key-prefixed with the plugin id, present only for tenants with the plugin active.
/// </summary>
public sealed record PackagedFieldDefinition(string PluginId, string EntityKey, ExtensionFieldSpec Spec);

/// <summary>
/// A cohesive UNIT of a plugin's registration (review round 4): a big plugin splits its
/// Configure into parts — one per concern (the host-facing contract, the UI surface, …) —
/// and the plugin class composes them with <see cref="PluginBuilder.AddPart{TPart}"/>, staying
/// a readable table of contents. Parts share the plugin's builder, so every PLG gate applies
/// unchanged. Composition is EXPLICIT (never auto-discovered): Configure is the index.
/// </summary>
public interface IPluginPart
{
    void Configure(PluginBuilder plugin);
}
