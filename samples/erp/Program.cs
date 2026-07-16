using Fortnox;
using Tam.Auth;
using Erp;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.AspNetCore.Postgres;
using Tam.EntityFrameworkCore;

var model = ErpModel.Build();

// Manifest export mode (D4): `dotnet run -- manifest [path]` for the CI baseline check.
if (TamManifestExport.TryHandle(model, args)) return;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("erp") ?? "Data Source=erp.db";
builder.Services.AddDbContext<ErpDbContext>(options =>
{
    // Provider by connection-string shape: "Host=..." → PostgreSQL (jsonb), else SQLite dev file.
    if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connectionString);
        // The RLS backstop (docs/33): keeps app.tenant_id/app.tenant_read_set true to the
        // ambient scope so the database-side policies mirror the EF filter.
        options.AddTamRls();
    }
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
    // Row-level security policies over every tenant-scoped table (docs/33) — Postgres only;
    // throws if the connecting role would silently bypass RLS (superuser/BYPASSRLS).
    if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        await TamRls.ProvisionAsync(db);
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
