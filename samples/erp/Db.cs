using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Erp;

public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Order> Orders => Set<Order>();

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

        modelBuilder.UseTam();
    }
}

public static class Seed
{
    public const string Tenant = "demo";

    public static void Run(ErpDbContext db)
    {
        db.Database.EnsureCreated();
        if (db.Customers.Any()) return;

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
        Role("dispatcher",
            "orders.read", "orders.create", "orders.edit", "orders.complete",
            "customers.read", "customers.create");
        Role("viewer", "orders.read", "customers.read");

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

        db.SaveChanges();
    }
}
