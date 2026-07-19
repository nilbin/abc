using Tam;
using Tam.AspNetCore;
using Tam.Generated;

namespace Erp;

/// <summary>
/// The compiled model is a VALUE (docs/01): built once here, then hosted by Program.cs and
/// tested by Erp.Tests through the SAME instance shape — what the test host verifies is what
/// the web host serves (tutorial Step 11).
///
/// Each domain declares its own model fragment (ErpModel.Orders.cs, ErpModel.Customers.cs, …
/// mirroring Features/): a page, its forms, its grid. This file keeps what spans domains —
/// plugins, event contracts, and the nav tree the HOST owns.
/// </summary>
public static partial class ErpModel
{
    public static TamModel Build() => Builder().Build();

    /// <summary>The composed model builder BEFORE <c>Build()</c> — the seam a test uses to add a
    /// probe derivation/operation to the real model without re-declaring the whole chain (the
    /// production <see cref="Build"/> just builds this as-is).</summary>
    public static TamModelBuilder Builder() => new TamModelBuilder()
        .DefaultCulture("sv")
        .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
        .AddDiscovered()   // compile-time discovery from Tam.Compiler — no runtime assembly scan
        .AddTamSystem()    // framework operations/views: custom fields, roles, audit, plugins
        .AddPlugin<Inspect.InspectionPlugin>()   // compiled in; each tenant activates at runtime (docs/22)
        .AddPlugin<Fortnox.FortnoxPlugin>()      // a plugin that ships an inbound integration (docs/10 + docs/22)
        .AddPlugin<Approvals.ApprovalsPlugin>()  // Step 16: approval flows over the three seams (docs/28 D-AG4)
        .AddPlugin<Invoicing.InvoicingPlugin>()  // Step 17: extends the Orders domain (docs/31)

        // Event contracts (docs/31 "events are records"): the [DomainEvent] payload records in
        // Domain/ ARE the declarations — AddDiscovered registered them; nothing to repeat here.

        // Magic folder (docs/35): every created order gets its document folder — the tree
        // layout is the HOST's declaration; no handler learns about documents.
        .DocumentFolder("order-created", "/order/{number}")

        // The order detail is a CONTRIBUTION POINT (docs/31 D-X4): placing it on the record
        // DECLARES it (docs/34 M5 — placement is declaration; the record's key is its
        // context). model.Slot() would only be needed for external slots or a custom key.

        .AddOrders()
        .AddCustomers()
        .AddProjects()
        .AddStock()
        .AddTime()
        .AddMaterials()

        // The web nav tree (docs/30): the HOST owns layout — modes at the top, the administration
        // section collects every package/plugin page that SUGGESTS it; anything uncollected lands
        // under "more" in the last mode automatically (nothing can be authored into invisibility).
        .Nav("web", nav => nav
            .Mode("work", m => m
                .Page("orders", page: "orders", order: 10)   // declared page: permission derives
                .Page("projects", page: "projects", order: 20)
                .Page("customers", page: "customers", order: 30)
                .Page("stock", page: "stock", order: 40)
                // The documents tree browser — the app's ONE registered React page (docs/32
                // D-P2: the escape hatch is for genuinely custom UX; registered pages carry
                // their explicit permission).
                .Page("documents", page: "documents-browser", permission: "documents.read", order: 50))
            // The technician's mode (docs/34 M4): the same declared pages, curated for the
            // field — and the thing a tenant can HIDE via the nav override registry (docs/30 v2).
            .Mode("field", m => m
                .Page("my-work", page: "orders", order: 10)
                .Page("my-time", page: "time", order: 20))
            .Mode("admin", m => m
                .Section("administration"))
            // The DEVELOPER mode (docs/31 slice 3): the extension surface rendered in the
            // running app — the portal page over developer.contract, for developer.read.
            .Mode("developer", m => m
                .Page("developer-portal", page: "developer-portal", permission: "developer.read", order: 10)));
}
