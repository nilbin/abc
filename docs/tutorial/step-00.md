# Step 0 — A new host from nothing *(BUILT — `samples/erp`)*

Three files make a Tam host. Everything else in this tutorial lands inside them.

**The project file** is the build contract: the three framework references, the compiler as an analyzer, and the locale catalogs as analyzer inputs — which is what makes missing keys *build errors* rather than runtime surprises:

```xml
<!-- samples/erp/Erp.csproj  (Sdk="Microsoft.NET.Sdk.Web") -->
<ItemGroup>
  <ProjectReference Include="..\..\src\Tam.Core\Tam.Core.csproj" />
  <ProjectReference Include="..\..\src\Tam.EntityFrameworkCore\Tam.EntityFrameworkCore.csproj" />
  <ProjectReference Include="..\..\src\Tam.AspNetCore\Tam.AspNetCore.csproj" />
  <ProjectReference Include="..\..\src\Tam.Compiler\Tam.Compiler.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<ItemGroup>
  <AdditionalFiles Include="locales/**/*.json" />
  <CompilerVisibleProperty Include="TamDefaultCulture" />
</ItemGroup>
<PropertyGroup>
  <TamDefaultCulture>sv</TamDefaultCulture>
</PropertyGroup>
```

A plugin project uses the same shape plus `<EmbeddedResource Include="locales/*.json" />` — its catalogs travel inside the package (Step 13).

**The DbContext** is ordinary EF Core wearing the framework's contracts:

```csharp
// samples/erp/Db.cs (trimmed)

public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options, TenantScope tenantScope)
    : DbContext(options),
      Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext,
      ITenantScopeContext
{
    public string? CurrentTenantId => tenantScope.Current;             // drives the global filter
    public IReadOnlyList<string> TenantReadSet => tenantScope.ReadSet; // subtree reads (Step 15)
    public bool CrossTenantScope => tenantScope.AllTenants;            // sanctioned escalation (docs/33)

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        });

        // Plugin storage opts in here — one line per installed plugin that ships entities (Step 13).
        Inspect.InspectionPlugin.AddInspect(modelBuilder);

        modelBuilder.UseTam(Database.ProviderName);   // framework tables + semantic value conversions
        modelBuilder.UseTamOpenIddict();              // token/client storage for the auth server (Step 14)
        modelBuilder.ApplyTenantFilter(this);         // ONE tenant boundary for the entire model
    }
}
```

`ITenantScopeContext` is the whole tenancy handshake: the framework reads the ambient scope off the context, and `ApplyTenantFilter` turns it into a global query filter over every `ITenantScoped` entity — framework and domain alike — so isolation is a property of the model, not a `Where` clause fifty call sites have to remember. `IDataProtectionKeyContext` (one `DbSet`) keeps the secrets vault's key ring in the shared database (Step 10).

**The composition root** builds the model, then the host — in an order that matters:

```csharp
// samples/erp/Program.cs (trimmed)

var model = new TamModelBuilder()
    .DefaultCulture("sv")
    .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
    .AddDiscovered()   // compile-time discovery from Tam.Compiler — no runtime assembly scan
    .AddTamSystem()    // framework packages: users, roles, audit, extensions, plugins, …
    // …forms, grids, nav, pages: Steps 4, 5 and 18…
    .Build();

// Manifest export mode (D4): `dotnet run -- manifest [path]` for the CI baseline check.
if (TamManifestExport.TryHandle(model, args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ErpDbContext>(options =>
{
    options.UseSqlite(connectionString);   // or UseNpgsql + options.AddTamRls() — Step 15
    options.UseTamConventions();           // tenant auto-stamp — one call, never forgotten
});
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddTamOpenIddict<ErpDbContext>(fallbackTenant: Seed.Tenant);   // Step 14

var app = builder.Build();
app.MapTamAuth();   // pins the request's active tenant right after authentication…
app.MapTam();       // …then everything else: /api/operations/*, /api/views/*,
                    // /api/forms/{id}/resolve, /api/manifest, /api/mcp, /api/events, /openapi.json
app.Run();
```

`MapTamAuth` before `MapTam`, always: the token's active-tenant claim must be honored before any endpoint touches data, so that every view, operation and lookup runs under the global tenant filter the DbContext declared above. And the manifest-export line is not ceremony — CI exports the manifest and diffs it against the committed baseline (`scripts/check_manifest.py`): additions are free, breaking changes (a removed field, a new required input) fail the build until a human commits the new baseline in the same PR (D4).

---
