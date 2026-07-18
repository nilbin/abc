using System.Text.Json;

namespace Tam;

/// <summary>
/// The host's EXTENSION SURFACE as one exported, versioned artifact (docs/31 slice 3) — the
/// plugin author's answer to "what can I extend?". Everything a plugin may compose with is
/// here: events (payload contracts, kinded), views (readable fields, kinded), slots (panel
/// targets with their context keys), extensible entities (packaged-field targets), and
/// gateable operations. Plugin projects reference the file as an AdditionalFile; the source
/// generator turns it into typed facades plus the HostContract index, so the artifact is both
/// the DOCUMENTATION an author browses and the CONTRACT the compiler enforces. Exported via
/// <c>dotnet run -- contract [path]</c>; CI keeps the committed copy honest.
/// </summary>
public static class HostContractExport
{
    /// <summary>Exports the consumable surface. With <paramref name="forOwner"/> null this is the
    /// HOST contract — host-owned and framework-package surface a plugin may consume (PLG010).
    /// With a plugin id it is that plugin's own SLICE (docs/37 D-V4, the second contract
    /// provider): only surface owned by that plugin, so a dependent referencing the slice gets
    /// exactly its parent's contract, and the slice stays stable against unrelated host changes.
    /// Slots and extensible entities are host concepts (PLG005) — absent from a plugin slice.</summary>
    public static string Write(TamModel model, string? forOwner = null)
    {
        bool Consumable(string? owner) => forOwner is null
            ? owner is null || model.Packages.ContainsKey(owner)
            : owner == forOwner;

        var slotSource = forOwner is null
            ? model.Slots
            : (IReadOnlyDictionary<string, SlotDefinition>)new Dictionary<string, SlotDefinition>();

        var contract = new
        {
            events = model.Events
                .Where(kv => Consumable(kv.Value.Plugin))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                    kv => kv.Key,
                    kv => new { fields = kv.Value.Fields, kinds = kv.Value.Kinds }),
            views = model.Views
                .Where(kv => Consumable(kv.Value.Plugin))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        permission = kv.Value.Permission,
                        fields = kv.Value.ResultFields.Select(f => f.WireName).ToList(),
                        kinds = kv.Value.ResultFields
                            .Select(f => (f.WireName, Kind: ContractKinds.FromClr(f.EffectiveType)))
                            .Where(x => x.Kind is not null)
                            .ToDictionary(x => x.WireName, x => x.Kind!),
                    }),
            slots = slotSource.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                kv => kv.Key,
                kv => new { keys = kv.Value.ContextKeys }),
            grids = model.Grids
                .Where(kv => Consumable(kv.Value.Plugin))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                    kv => kv.Key,
                    kv => new { view = kv.Value.ViewId }),
            extensibleEntities = (forOwner is null
                    ? model.ExtensibleEntityKeys : Enumerable.Empty<string>())
                .Order(StringComparer.Ordinal).ToList(),
            operations = model.Operations
                .Where(kv => Consumable(kv.Value.Plugin))
                .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
        };
        return JsonSerializer.Serialize(contract, new JsonSerializerOptions(TamJson.Options)
        {
            WriteIndented = true,
        });
    }
}
