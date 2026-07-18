using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.Auth;
using Tam.EntityFrameworkCore;

namespace Erp;

public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options, TenantScope tenantScope)
    : DbContext(options),
      Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext,
      ITenantScopeContext
{
    // The ambient request tenant that drives the global query filter (ITenantScoped). Null in a
    // background or startup-seed scope — those opt out explicitly with IgnoreQueryFilters.
    public string? CurrentTenantId => tenantScope.Current;

    // Subtree-read widening (docs/26 D-H1): non-empty only while a SubtreeRead view executes.
    public IReadOnlyList<string> TenantReadSet => tenantScope.ReadSet;

    // The subtree's PATH twin — the constant-size setting the RLS backstop syncs (docs/33).
    public string? TenantReadPath => tenantScope.ReadPath;

    // Sanctioned request-wide cross-tenant escalation (docs/33 D-R8) — the RLS backstop's '*'.
    public bool CrossTenantScope => tenantScope.AllTenants;

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<StockItem> Stock => Set<StockItem>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<MaterialLine> MaterialLines => Set<MaterialLine>();

    // Data Protection key ring in the shared DB (docs/25): survives restarts, shared across
    // instances — so encrypted secrets stay decryptable. One DbSet is the whole opt-in.
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>
        DataProtectionKeys => Set<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<Project>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        });
        modelBuilder.Entity<StockItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique();
        });
        modelBuilder.Entity<TimeEntry>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.TechnicianName).HasMaxLength(200);
            b.Property(x => x.Note).HasMaxLength(500);
            // No natural unique key — a technician may book several entries per day on one
            // order; the index serves the per-order lists.
            b.HasIndex(x => new { x.TenantId, x.OrderId });
        });
        modelBuilder.Entity<MaterialLine>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.OrderId });
        });
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        });

        // Plugin storage opts in here: the plugin's tables live in the host database and
        // migrate with it (docs/22). One line per installed plugin.
        Inspect.InspectionPlugin.AddInspect(modelBuilder);
        Approvals.ApprovalsPlugin.AddApprovals(modelBuilder);
        Invoicing.InvoicingPlugin.AddInvoicing(modelBuilder);

        modelBuilder.UseTam(Database.ProviderName);
        modelBuilder.UseTamOpenIddict();   // token/client storage for the embedded auth server

        // One tenant boundary for the whole model: every ITenantScoped entity — framework and
        // domain alike — is filtered to CurrentTenantId, so isolation is a property of the model,
        // not a Where-clause each of 50 call sites has to remember. Closes the sample's unfiltered
        // Customer/Order/Project reads by construction. Background scopes use IgnoreQueryFilters.
        modelBuilder.ApplyTenantFilter(this);
    }
}
