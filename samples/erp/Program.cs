using Erp;
using Fortnox;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;
using Tam.AspNetCore.Postgres;
using Tam.AspNetCore.SystemOps;
using Tam.Auth;
using Tam.Generated;

var model = new TamModelBuilder()
    .DefaultCulture("sv")
    .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
    .AddDiscovered()   // compile-time discovery from Tam.Compiler — no runtime assembly scan
    .AddTamSystem()    // framework operations/views: custom fields, roles, audit, plugins
    .AddPlugin<Inspect.InspectionPlugin>()   // compiled in; each tenant activates at runtime (docs/22)
    .AddPlugin<Fortnox.FortnoxPlugin>()      // a plugin that ships an inbound integration (docs/10 + docs/22)
    .AddPlugin<Approvals.ApprovalsPlugin>()  // Step 16: approval flows over the three seams (docs/28 D-AG4)
    .AddPlugin<Invoicing.InvoicingPlugin>()  // Step 17: extends the Orders domain (docs/31)

    // The web nav tree (docs/30): the HOST owns layout — modes at the top, the administration
    // section collects every package/plugin page that SUGGESTS it; anything uncollected lands
    // under "more" in the last mode automatically (nothing can be authored into invisibility).
    // Event contracts (docs/31 D-X5): what subscribers/triggers may bind to, with payload shape.
    .PublishesEvent("order-completed", "orderId", "number")

    // The order detail is a CONTRIBUTION POINT (docs/31 D-X4): declared once, with the record
    // context it provides — every current and future plugin lands panels here unnamed.
    .Slot("web.orders.detail", slot => slot.Key("orderId"))

    .Nav("web", nav => nav
        .Mode("work", m => m
            .Page("orders", page: "orders", permission: "orders.read", order: 10)
            .Page("customers", grid: "web.customers.list", order: 30))
        .Mode("admin", m => m
            .Section("administration")))

    .Form<CreateOrder.Input>("web.orders.create", "orders.create", form =>
    {
        form.Field(x => x.CustomerId).Renderer("customer-picker");
        form.Field(x => x.OrderType);
        form.Field(x => x.ProjectId)
            .VisibleWhen(x => x.OrderType == OrderType.Project)
            .RequiredWhen(x => x.OrderType == OrderType.Project);
        form.Field(x => x.WorkAddress)
            .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);
        form.Field(x => x.Description);
        form.Field(x => x.RequestedDate);
        form.Field(x => x.EstimatedTotal).Renderer("money");
        form.Extensions();
    })

    .Form<EditOrderDetails.Input>("web.orders.edit", "orders.edit-details", form =>
    {
        form.Field(x => x.OrderId).Renderer("hidden");
        form.Field(x => x.Description);
        form.Field(x => x.RequestedDate);
        form.Field(x => x.WorkAddress);
        form.Field(x => x.EstimatedTotal).Renderer("money");
        form.Extensions();
    })

    .Form<CreateCustomer.Input>("web.customers.create", "customers.create", form =>
    {
        form.Field(x => x.Name);
        form.Field(x => x.VisitAddress);
        form.Field(x => x.Email);
        form.Field(x => x.Phone);
    })

    .Grid<OrderList.Result>("web.orders.list", "orders.list", grid =>
    {
        grid.Column(x => x.Number);
        grid.Column(x => x.TenantId);   // the company column — rendered only when acting above a leaf
        grid.Column(x => x.CustomerName);
        grid.Column(x => x.Type);
        grid.Column(x => x.Status);
        grid.Column(x => x.RequestedDate);
        grid.Column(x => x.EstimatedTotal);
        grid.Extensions();
        grid.RowAction("orders.complete");
        grid.ToolbarAction("orders.create");
    })


    .Grid<CustomerList.Result>("web.customers.list", "customers.list", grid =>
    {
        grid.Column(x => x.Name);
        grid.Column(x => x.Email);
        grid.Column(x => x.Phone);
        grid.Column(x => x.VisitAddress);
        grid.Column(x => x.IsActive);
        grid.ToolbarAction("customers.create");
    })

    .Build();

// Manifest export mode (D4): `dotnet run -- manifest [path]` for the CI baseline check.
if (TamManifestExport.TryHandle(model, args)) return;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("erp") ?? "Data Source=erp.db";
builder.Services.AddDbContext<ErpDbContext>(options =>
{
    // Provider by connection-string shape: "Host=..." → PostgreSQL (jsonb), else SQLite dev file.
    if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString);
    // Framework DbContext conventions (tenant auto-stamp) — one call, never forgotten.
    options.UseTamConventions();
});
builder.Services.AddTam<ErpDbContext>(model, integrations =>
{
    // The demo's Fortnox base URL is this app's own localhost mock, so it opts into private-network
    // egress. A real deployment leaves this off and the SSRF guard blocks internal destinations.
    integrations.AllowPrivateNetwork = true;
    // Short retry timings so the outbound retry → backoff → dead-letter loop is observable in the
    // demo. Production keeps the 30s/1h defaults.
    integrations.RetryBaseDelay = TimeSpan.FromSeconds(2);
    integrations.RetryDriverInterval = TimeSpan.FromSeconds(2);
});
// Real authentication: the framework's embedded OpenIddict server — Authorization Code + PKCE for
// humans (framework-rendered login + tenant picker), client credentials for agents — plus
// claims-based actor and active-tenant resolution. Any external IdP plugs in through
// ClaimsActorProvider instead; a custom IActorProvider replaces the whole seam. The fallback tenant
// scopes requests that carry no active-tenant claim yet.
builder.Services.AddTamOpenIddict<ErpDbContext>(fallbackTenant: Seed.Tenant);
// On Postgres, cross-instance SSE via LISTEN/NOTIFY (docs/12); SQLite dev keeps the in-process default.
if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddTamPostgresBackplane(connectionString);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
// MapTamAuth pins the request's active tenant right after authentication (so the token's
// active-tenant claim is honored) — the token exchange, actor/role resolution and every view all
// run through the resulting global tenant filter.
app.MapTamAuth();
app.MapTam();

// The Fortnox mock lives with the fortnox sample plugin — the host only opts in for the demo.
app.MapMockFortnox();

app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
    Seed.Run(db);
    // Machine client for agents/integrations: authenticates with client credentials and acts
    // as the same-named framework user (seeded with the dispatcher role) — audited like anyone.
    await Tam.Auth.TamOpenIddict.EnsureClientAsync(scope.ServiceProvider, "mcp-agent", "agent-secret");
    // The SPA is a public client using Authorization Code + PKCE; the code is returned to /callback.
    await Tam.Auth.TamOpenIddict.EnsureSpaClientAsync(scope.ServiceProvider, "tam-spa",
        "http://localhost:5100/callback", "http://localhost:5173/callback");
    // Seed the encrypted Fortnox API key through the vault (needs the Data-Protection provider).
    if (!db.Set<Tam.EntityFrameworkCore.TenantSecretEntity>().Any())
    {
        await scope.ServiceProvider.GetRequiredService<Tam.AspNetCore.SecretVault>()
            .SetAsync(Erp.Seed.Tenant, "fortnox.apiKey", "seeded-secret-key", default);
        await db.SaveChangesAsync();
    }
}

app.Run();

public partial class Program;
