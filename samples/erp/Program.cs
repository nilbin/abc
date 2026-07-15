using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
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

    .Form<DefineExtensionField.Input>("web.extensions.define", "extensions.define-field", form =>
    {
        form.Field(x => x.Entity);
        form.Field(x => x.Key);
        form.Field(x => x.Type);
        form.Field(x => x.Labels).Renderer("culture-text");
        form.Field(x => x.Required);
        form.Field(x => x.MaxLength);
    })

    .Grid<OrderList.Result>("web.orders.list", "orders.list", grid =>
    {
        grid.Column(x => x.Number);
        grid.Column(x => x.CustomerName);
        grid.Column(x => x.Type);
        grid.Column(x => x.Status);
        grid.Column(x => x.RequestedDate);
        grid.Column(x => x.EstimatedTotal);
        grid.Extensions();
        grid.RowAction("orders.complete");
        grid.ToolbarAction("orders.create");
    })

    // The group roll-up (docs/26 D-H1): read-only by design — writes fan in to one node (D-H4).
    .Grid<OrderOverview.Result>("web.orders.overview", "orders.overview", grid =>
    {
        grid.Column(x => x.Number);
        grid.Column(x => x.Company);
        grid.Column(x => x.Description);
        grid.Column(x => x.Type);
        grid.Column(x => x.Status);
        grid.Column(x => x.RequestedDate);
        grid.Column(x => x.EstimatedTotal);
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

    .Grid<AuditLog.Result>("web.audit.list", "audit.entries", grid =>
    {
        grid.Column(x => x.Timestamp);
        grid.Column(x => x.OperationId);
        grid.Column(x => x.ActorName);
        grid.Column(x => x.Entity);
        grid.Column(x => x.Field);
        grid.Column(x => x.OldValue);
        grid.Column(x => x.NewValue);
    })

    .Grid<ExtensionFieldList.Result>("web.extensions.fields", "extensions.fields", grid =>
    {
        grid.Column(x => x.Entity);
        grid.Column(x => x.Key);
        grid.Column(x => x.Type);
        grid.Column(x => x.Required);
        grid.Column(x => x.State);
        grid.ToolbarAction("extensions.define-field");
    })

    .Grid<PluginList.Result>("web.plugins", "plugins.list", grid =>
    {
        grid.Column(x => x.PluginId);
        grid.Column(x => x.Active);
        grid.RowAction("plugins.activate");
        grid.RowAction("plugins.deactivate");
    })

    .Form<InstallPackage.Input>("web.packages.install", "packages.install", form =>
    {
        form.Field(x => x.Document).Renderer("multiline");
        form.Field(x => x.DryRun);
    })

    .Grid<PackageList.Result>("web.packages", "packages.list", grid =>
    {
        grid.Column(x => x.Package);
        grid.Column(x => x.Version);
        grid.Column(x => x.InstalledAt);
        grid.ToolbarAction("packages.install");
        grid.RowAction("packages.uninstall");
    })

    .Form<DefineAutomationRule.Input>("web.rules.define", "rules.define", form =>
    {
        form.Field(x => x.Name);
        form.Field(x => x.OnOperation);
        form.Field(x => x.Condition).Renderer("multiline");
        form.Field(x => x.Messages).Renderer("culture-text");
        form.Field(x => x.TargetField);
    })

    .Grid<RuleList.Result>("web.rules", "rules.list", grid =>
    {
        grid.Column(x => x.Name);
        grid.Column(x => x.OnOperation);
        grid.Column(x => x.Retired);
        grid.ToolbarAction("rules.define");
        grid.RowAction("rules.retire");
    })

    .Form<CreateTenant.Input>("web.tenants.create", "tenants.create", form =>
    {
        form.Field(x => x.Id);
        form.Field(x => x.DisplayName);
    })

    .Form<MoveTenant.Input>("web.tenants.move", "tenants.move", form =>
    {
        form.Field(x => x.TenantId);
        form.Field(x => x.NewParentId);
    })

    .Form<RenameTenant.Input>("web.tenants.rename", "tenants.rename", form =>
    {
        form.Field(x => x.TenantId);
        form.Field(x => x.DisplayName);
    })

    .Grid<TenantList.Result>("web.tenants", "tenants.list", grid =>
    {
        grid.Column(x => x.Id);
        grid.Column(x => x.DisplayName);
        grid.Column(x => x.Path);
        grid.ToolbarAction("tenants.create");
        grid.ToolbarAction("tenants.move");
        grid.ToolbarAction("tenants.rename");
    })

    .Form<DefinePolicy.Input>("web.policies.define", "policies.define", form =>
    {
        form.Field(x => x.Name);
        form.Field(x => x.Scopes).Renderer("scope-map");
    })

    .Grid<PolicyList.Result>("web.policies", "policies.list", grid =>
    {
        grid.Column(x => x.Name);
        grid.Column(x => x.Scopes);
        grid.ToolbarAction("policies.define");
    })

    .Build();

// Manifest export mode (D4): `dotnet run -- manifest [path]` writes the compiled model's manifest
// for the CI baseline check, then exits. No server, no database.
if (args is ["manifest", ..])
{
    var path = args.Length > 1 ? args[1] : "manifest.baseline.json";
    var exported = Tam.ManifestBuilder.Build(
        model, new Dictionary<string, IReadOnlyList<Tam.ExtensionFieldSpec>>(), revision: 0);
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
        exported, new System.Text.Json.JsonSerializerOptions(Tam.TamJson.Options) { WriteIndented = true }));
    Console.WriteLine($"manifest written to {path}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("erp") ?? "Data Source=erp.db";
builder.Services.AddDbContext<ErpDbContext>(options =>
{
    // Provider by connection-string shape: "Host=..." → PostgreSQL (jsonb), else SQLite dev file.
    if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString);
    // Auto-stamp TenantId on inserted ITenantScoped rows from the ambient tenant (write-side mirror
    // of the global read filter) — so operation code never assigns TenantId by hand.
    options.AddInterceptors(new Tam.EntityFrameworkCore.TenantStampInterceptor());
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

// A stand-in for Fortnox's accounting API, so the outbound-integration loop (docs/25) is
// verifiable end to end without a real external system. Records every push it receives.
var mockVouchers = new System.Collections.Concurrent.ConcurrentBag<string>();
app.MapPost("/mock/fortnox/vouchers", async (HttpContext http) =>
{
    if (http.Request.Headers["Access-Token"] != "seeded-secret-key") return Results.Unauthorized();
    using var reader = new StreamReader(http.Request.Body);
    mockVouchers.Add(await reader.ReadToEndAsync());
    return Results.Ok(new { created = true });
});
app.MapGet("/mock/fortnox/vouchers", () => Results.Json(mockVouchers.ToArray()));
app.MapGet("/mock/fortnox/orders", () => Results.Json(Array.Empty<object>()));   // poll target

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
