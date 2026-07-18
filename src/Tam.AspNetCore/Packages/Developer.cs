using Tam;

namespace Tam.AspNetCore;

/// <summary>
/// The FOURTEENTH framework package (docs/31 slice 3): the host's extension surface served to
/// the running app — the same contract `dotnet run -- contract` exports, readable by anyone
/// holding <c>developer.read</c> through one ordinary view. The developer PORTAL page renders
/// it (samples/web `developer-portal`, placed by the host in its own nav mode): what a plugin
/// author browses before writing a line — events, views, slots, extensible entities, gateable
/// operations. Discoverability has three synchronized forms now: the artifact file (build
/// input), the generated HostContract symbols (IntelliSense), and this page (the running app).
/// </summary>
[TamPackage("tam.developer", "developer", "web.developer")]
public sealed class TamDeveloperPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.AddViewType(typeof(ContractView));

        plugin.LocaleDefaults("en", new Dictionary<string, string>
        {
            ["nav.developer"] = "Developer",
            ["labels.contract"] = "Contract",
            ["dev.intro"] = "The host's extension surface — what a plugin may compose with. The same contract ships as host-contract.json and as the generated HostContract symbols.",
            ["dev.headings.events"] = "Events",
            ["dev.headings.views"] = "Views",
            ["dev.headings.slots"] = "Slots",
            ["dev.headings.entities"] = "Extensible entities",
            ["dev.headings.operations"] = "Operations",
            ["dev.hint.events"] = "Payload contracts a plugin subscribes to (RequiresEvent<OrderCompletedEvent>()).",
            ["dev.hint.views"] = "Readable fields per view (RequiresView<OrdersDetailRow>(r => r.Id, …)); permission in the badge.",
            ["dev.hint.slots"] = "Host surfaces a plugin's panels can land in, with their context keys.",
            ["dev.hint.entities"] = "Entities accepting packaged extension fields.",
            ["dev.hint.operations"] = "Operation ids a plugin may gate or target with grid actions.",
        });
        plugin.LocaleDefaults("sv", new Dictionary<string, string>
        {
            ["nav.developer"] = "Utvecklare",
            ["labels.contract"] = "Kontrakt",
            ["dev.intro"] = "Värdens utökningsyta — det en plugin kan komponera mot. Samma kontrakt levereras som host-contract.json och som de genererade HostContract-symbolerna.",
            ["dev.headings.events"] = "Händelser",
            ["dev.headings.views"] = "Vyer",
            ["dev.headings.slots"] = "Platser",
            ["dev.headings.entities"] = "Utökningsbara entiteter",
            ["dev.headings.operations"] = "Operationer",
            ["dev.hint.events"] = "Nyttolastkontrakt en plugin prenumererar på (RequiresEvent<OrderCompletedEvent>()).",
            ["dev.hint.views"] = "Läsbara fält per vy (RequiresView<OrdersDetailRow>(r => r.Id, …)); behörighet i etiketten.",
            ["dev.hint.slots"] = "Värdytor där en plugins paneler kan landa, med sina kontextnycklar.",
            ["dev.hint.entities"] = "Entiteter som tar emot paketerade utökningsfält.",
            ["dev.hint.operations"] = "Operations-id:n som en plugin kan spärra (gate) eller rikta liståtgärder mot.",
        });
    }
}

/// <summary>One row: the whole contract as json — the portal parses and renders it. Serving
/// the surface THROUGH a view keeps it permission-checked, manifest-listed and reachable by
/// the standard client like everything else.</summary>
[View("developer.contract")]
[Authorize("developer.read")]
public static class ContractView
{
    public sealed record Query;

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.contract")]
        public string Contract { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, TamModel model) =>
        new[] { new Result { Id = Guid.Empty, Contract = HostContractExport.Write(model) } }
            .AsQueryable();
}
