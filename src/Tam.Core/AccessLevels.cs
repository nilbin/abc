namespace Tam;

/// <summary>
/// Access levels (docs/27 D-A1): None &lt; View &lt; Edit &lt; Manage — ordered presets over the
/// permission atoms, so a role is authored as { resource: level } instead of enumerating strings.
/// Levels are authoring sugar over a stable substrate: they expand to atoms AT LOAD TIME (a new
/// action added to a resource flows into existing Manage roles automatically), so enforcement, the
/// manifest and the analyzer keep working on the flat atom set exactly as before.
/// </summary>
public static class AccessLevels
{
    public const string None = "none";
    public const string View = "view";
    public const string Edit = "edit";
    public const string Manage = "manage";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { None, View, Edit, Manage };

    private static readonly HashSet<string> ViewActions = ["read", "view", "list", "lookup"];
    private static readonly HashSet<string> EditActions = ["create", "edit", "update"];

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        TamModel, Dictionary<string, HashSet<string>>> Catalogs = new();

    /// <summary>
    /// resource → its permission atoms' actions, derived mechanically from every [Authorize]
    /// permission in the model ("orders.read" → orders: {read}). The resource IS the atom prefix —
    /// nothing is declared twice, and a new operation's permission extends its resource by existing.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<string>> Catalog(TamModel model) =>
        Catalogs.GetValue(model, static m =>
        {
            var catalog = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var permission in m.Permissions)
            {
                var dot = permission.IndexOf('.');
                if (dot <= 0 || dot == permission.Length - 1) continue;
                var resource = permission[..dot];
                if (!catalog.TryGetValue(resource, out var actions))
                    catalog[resource] = actions = [];
                actions.Add(permission[(dot + 1)..]);
            }
            return catalog;
        });

    /// <summary>
    /// Expands one level grant to its atoms: view → read-ish actions, edit → view + create/edit/
    /// update, manage → every atom the resource has. Reserved permissions (docs/24) are NEVER
    /// granted by a level — like "*", a level means "what the app does", not "what the platform
    /// does to itself".
    /// </summary>
    public static IEnumerable<string> Expand(TamModel model, string resource, string level)
    {
        if (!Catalog(model).TryGetValue(resource, out var actions)) yield break;
        foreach (var action in actions)
        {
            var atom = $"{resource}.{action}";
            if (Actor.Reserved.Contains(atom)) continue;
            var included = level.ToLowerInvariant() switch
            {
                View => ViewActions.Contains(action),
                Edit => ViewActions.Contains(action) || EditActions.Contains(action),
                Manage => true,
                _ => false,
            };
            if (included) yield return atom;
        }
    }
}
