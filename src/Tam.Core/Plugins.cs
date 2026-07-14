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
/// The plugin's registration surface. <see cref="Model"/> is the host's model builder in
/// plugin scope: everything registered through it is tagged with the plugin id, so the
/// manifest can omit it per tenant and PLG001 can enforce the namespace.
/// </summary>
public sealed class PluginBuilder
{
    internal PluginBuilder(string id, TamModelBuilder model)
    {
        Id = id;
        Model = model;
    }

    public string Id { get; }

    /// <summary>Plugin-scoped model builder — use the assembly's generated AddDiscovered() here.</summary>
    public TamModelBuilder Model { get; }

    /// <summary>Plugin locale defaults (embedded resources); application locale files override them.</summary>
    public PluginBuilder LocaleDefaults(string culture, IReadOnlyDictionary<string, string> entries)
    {
        Model.LocaleDefaults(culture, entries);
        return this;
    }
}
