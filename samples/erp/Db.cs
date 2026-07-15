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

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Order> Orders => Set<Order>();

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
        modelBuilder.Entity<Project>(b => b.HasKey(x => x.Id));
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

        modelBuilder.UseTam(Database.ProviderName);
        modelBuilder.UseTamOpenIddict();   // token/client storage for the embedded auth server

        // One tenant boundary for the whole model: every ITenantScoped entity — framework and
        // domain alike — is filtered to CurrentTenantId, so isolation is a property of the model,
        // not a Where-clause each of 50 call sites has to remember. Closes the sample's unfiltered
        // Customer/Order/Project reads by construction. Background scopes use IgnoreQueryFilters.
        modelBuilder.ApplyTenantFilter(this);
    }
}

public static class Seed
{
    public const string Tenant = "demo";

    public static void Run(ErpDbContext db)
    {
        db.Database.EnsureCreated();
        // Seeding runs outside a request (no ambient tenant), so this cross-tenant guard opts out
        // of the global filter — otherwise it would see no rows and re-seed on every startup.
        if (db.Customers.IgnoreQueryFilters().Any()) return;

        var acme = Customer.Create(Tenant, new("Acme Industri AB"), new("Industrigatan 4, Västerås"),
            new("info@acme-industri.se"), new("+46 21 12 34 56"));
        var nordpump = Customer.Create(Tenant, new("Nordpump AB"), new("Hamnvägen 12, Göteborg"),
            new("kontakt@nordpump.se"), new("+46 31 98 76 54"), creditBlocked: true);
        var svea = Customer.Create(Tenant, new("Svea Fastigheter"), new("Storgatan 1, Stockholm"),
            new("drift@sveafastigheter.se"), null);
        var kylteknik = Customer.Create(Tenant, new("Kylteknik i Umeå AB"), new("Verkstadsgatan 8, Umeå"),
            null, new("+46 90 11 22 33"));
        var inactive = Customer.Create(Tenant, new("Nedlagda Bolaget AB"), new("Okänd adress"), null, null);
        inactive.Deactivate();
        db.Customers.AddRange(acme, nordpump, svea, kylteknik, inactive);

        var pumpRefurb = Project.Create(Tenant, acme.Id, "Pumprenovering 2026");
        var serviceDeal = Project.Create(Tenant, acme.Id, "Serviceavtal årligt");
        var sveaVent = Project.Create(Tenant, svea.Id, "Ventilationsbyte Storgatan");
        db.Projects.AddRange(pumpRefurb, serviceDeal, sveaVent);

        var orders = new[]
        {
            Order.Create(Tenant, new("2026-01412"), acme.Id, OrderType.Service, null,
                acme.VisitAddress, new("Byt packning på huvudpump"), new DateOnly(2026, 7, 20), 4500m),
            Order.Create(Tenant, new("2026-01413"), acme.Id, OrderType.Project, pumpRefurb.Id,
                acme.VisitAddress, new("Renovera pumpstation etapp 1"), new DateOnly(2026, 8, 1), 120000m),
            Order.Create(Tenant, new("2026-01414"), svea.Id, OrderType.Project, sveaVent.Id,
                new("Storgatan 1, Stockholm"), new("Demontera gammalt ventilationsaggregat"), null, 65000m),
            Order.Create(Tenant, new("2026-01415"), nordpump.Id, OrderType.Service, null,
                nordpump.VisitAddress, new("Felsök tryckfall i matarledning"), new DateOnly(2026, 7, 16), null),
            Order.Create(Tenant, new("2026-01416"), kylteknik.Id, OrderType.Service, null,
                kylteknik.VisitAddress, new("Årlig service av kylaggregat"), new DateOnly(2026, 9, 5), 8200m),
        };
        orders[0].Complete();
        db.Orders.AddRange(orders);

        // Tenant-managed roles (decision D1): named grant sets, stored as data.
        void Role(string name, params string[] permissions) => db.Add(new RoleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Name = name,
            PermissionsJson = System.Text.Json.JsonSerializer.Serialize(permissions),
        });
        Role("admin", "*");
        // The paired-atom pattern (docs/28 D-AG2): orders are OWN-scoped by default; the
        // "-all" widening atoms lift the restriction. Dispatchers work the whole board.
        Role("dispatcher",
            "orders.read", "orders.read-all", "orders.create",
            "orders.edit", "orders.edit-all", "orders.complete", "orders.complete-all",
            "customers.read", "customers.create");
        // "viewer" is authored as ACCESS LEVELS (docs/27 D-A1): { orders: view, customers: view }
        // expands to the read atoms at load time — the level shape and the atom shape coexist.
        db.Add(new RoleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Name = "viewer",
            LevelsJson = """{"orders":"view","customers":"view"}""",
        });
        // Technicians carry only the base atoms — own-scoped by construction, no suffixes.
        Role("technician",
            "orders.read", "orders.edit", "orders.complete", "customers.read");

        // The tenant node (docs/26): the demo tenant is the root of its own hierarchy, so its
        // materialized Path is just its own id. Nesting adds children with Path = "demo.<child>".
        db.Add(new TenantEntity { Id = Tenant, ParentId = null, Path = Tenant, DisplayName = "Demo AB" });

        // Identity is platform-global (docs/26): an Account is owned by the platform and reachable
        // across tenants; a TenantMembership grants it access to THIS tenant with a set of roles.
        // The login handle is the account Email (short names here so the demo's one-click buttons
        // keep working). Everyone's demo password: "demo123". "mcp-agent" is the machine client's
        // account — agents authenticate with client credentials and act as it, fully audited.
        var accountIds = new Dictionary<string, Guid>();
        TenantMembershipEntity User(string handle, string display, params string[] roles)
        {
            var accountId = Guid.NewGuid();
            accountIds[handle] = accountId;
            db.Add(new AccountEntity
            {
                Id = accountId,
                Email = handle,
                DisplayName = display,
                PasswordHash = TamPasswords.Hash("demo123"),
            });
            var membership = new TenantMembershipEntity
            {
                Id = Guid.NewGuid(),
                TenantId = Tenant,
                AccountId = accountId,
                RolesJson = System.Text.Json.JsonSerializer.Serialize(roles),
            };
            db.Add(membership);
            return membership;
        }
        // Alva's admin CASCADES (D-H5 shape): one membership at "demo" reaches every descendant node.
        // The others keep the legacy flat shape (reads as cascade: false) — exercising back-compat.
        User("alva", "Alva Andersson", "admin").RolesJson = """[{"name":"admin","cascade":true}]""";
        User("didrik", "Didrik Berg", "dispatcher");
        User("tekla", "Tekla Nilsson", "technician");
        User("vera", "Vera Lund", "viewer");
        User("mcp-agent", "MCP Agent", "dispatcher");

        // A CHILD tenant under "demo" (docs/26): its Path extends the parent's, so subtree membership
        // is a prefix test. Deliberately NO membership rows here — Alva stands at "nord" purely through
        // her cascading admin on "demo" (grants fan out), while its DATA stays strictly its own (the
        // global filter is per-node): the demo of "capability cascades, data does not".
        const string TenantNord = "nord";
        db.Add(new TenantEntity
        {
            Id = TenantNord, ParentId = Tenant, Path = Tenant + "." + TenantNord,
            DisplayName = "Norrservice Nord AB",
        });
        var fjallvarme = Customer.Create(TenantNord, new("Fjällvärme AB"), new("Bruksvägen 2, Kiruna"),
            new("info@fjallvarme.se"), new("+46 980 12 345"));
        db.Customers.Add(fjallvarme);
        db.Orders.Add(Order.Create(TenantNord, new("2026-09001"), fjallvarme.Id, OrderType.Service, null,
            fjallvarme.VisitAddress, new("Service av värmepump"), new DateOnly(2026, 8, 15), 12500m));

        // A SECOND, unrelated tenant (docs/26): proves platform-global identity — Alva is one account
        // with memberships in two tenants that are NOT in the same hierarchy. At login she gets a
        // tenant picker; the chosen tenant scopes her data (5 customers in "demo", 2 here). Roles are
        // tenant-scoped, so "demo2" declares its own.
        const string Tenant2 = "demo2";
        db.Add(new TenantEntity { Id = Tenant2, ParentId = null, Path = Tenant2, DisplayName = "Andra Bolaget AB" });
        db.Add(new RoleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant2,
            Name = "viewer",
            PermissionsJson = System.Text.Json.JsonSerializer.Serialize(new[] { "orders.read", "customers.read" }),
        });
        db.Add(new TenantMembershipEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant2,
            AccountId = accountIds["alva"],
            RolesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "viewer" }),
        });
        db.Customers.AddRange(
            Customer.Create(Tenant2, new("Berg & Söner Bygg"), new("Verkstadsvägen 3, Borås"),
                new("info@bergsoner.se"), new("+46 33 10 20 30")),
            Customer.Create(Tenant2, new("Lidköping Kyl AB"), new("Fabriksgatan 9, Lidköping"),
                new("kontakt@lidkyl.se"), null));

        // The tenant's subscription (docs/24): a "standard" plan, 10 seats, entitled plugins.
        // A billing provider would drive this via subscriptions.set-plan. This row is the tree's
        // ANCHOR (docs/24 hierarchy): it covers "nord" too — the money cascades down the tree,
        // activation stays per node, and the seat pool spans both.
        db.Add(new SubscriptionEntity
        {
            TenantId = Tenant,
            Plan = "standard",
            Seats = 10,
            EntitlementsJson = """["inspect","fortnox","approvals"]""",
            Status = "active",
        });

        // Ownership (:own scope) compares against the actor id, which is now the global account id
        // (docs/26), so assign by Tekla's account id — not the login handle.
        orders[3].AssignTo(accountIds["tekla"].ToString());
        orders[4].AssignTo(accountIds["tekla"].ToString());

        // Integration config (docs/25): a non-secret base URL and — via the vault at startup —
        // an encrypted API key. The base URL points at this app's own mock Fortnox endpoint so
        // the outbound loop is verifiable. (The secret is seeded in Program.cs through the vault,
        // since encryption needs the Data-Protection provider.)
        db.Add(new TenantSettingEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Key = "fortnox.baseUrl",
            Value = "http://localhost:5100/mock/fortnox",
        });

        // A tenant-defined custom field, exactly as an admin would create it at runtime (docs/15).
        db.Add(new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Entity = "order",
            Key = "machineSerialNumber",
            Type = "text",
            MaxLength = 40,
            LabelsJson = """{"sv":"Maskinserienummer","en":"Machine serial number"}""",
            DescriptionsJson = """{"sv":"Serienummer för den servade maskinen, från typskylten.","en":"Serial number of the serviced machine, from the type plate."}""",
            State = ExtensionFieldState.Active,
        });
        orders[4].Extensions = orders[4].Extensions.WithValue("machineSerialNumber", "KA-2201-X");

        // A numeric tenant field: exercises typed JSON predicates (equality + ranges) end to end.
        db.Add(new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Entity = "order",
            Key = "weightKg",
            Type = "number",
            LabelsJson = """{"sv":"Vikt (kg)","en":"Weight (kg)"}""",
            State = ExtensionFieldState.Active,
        });
        orders[1].Extensions = orders[1].Extensions.WithValue("weightKg", 1250);
        orders[2].Extensions = orders[2].Extensions.WithValue("weightKg", 380);
        orders[4].Extensions = orders[4].Extensions.WithValue("weightKg", 95.5);

        db.SaveChanges();
    }
}
