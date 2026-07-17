using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.Auth;
using Tam.EntityFrameworkCore;

namespace Erp;

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

        var pumpRefurb = Project.Create(Tenant, new("P-2026-001"), acme.Id, "Pumprenovering 2026", 250000m);
        var serviceDeal = Project.Create(Tenant, new("P-2026-002"), acme.Id, "Serviceavtal årligt");
        var sveaVent = Project.Create(Tenant, new("P-2026-003"), svea.Id, "Ventilationsbyte Storgatan", 90000m);
        var doneDeal = Project.Create(Tenant, new("P-2025-017"), kylteknik.Id, "Frysrumsinstallation", 145000m);
        doneDeal.Close();
        db.Projects.AddRange(pumpRefurb, serviceDeal, sveaVent, doneDeal);

        // The stock catalog (docs/34 M1): per-node, retire-don't-delete. Named locals feed the
        // M3 material lines below.
        var r22 = StockItem.Create(Tenant, new("KM-R22"), "Köldmedium R22 (utfasad)", StockUnit.Kilogram, 0m);
        r22.Deactivate();
        var packning = StockItem.Create(Tenant, new("PKG-DN50"), "Packning DN50", StockUnit.Piece, 145m);
        var koldmedium = StockItem.Create(Tenant, new("KM-R410A"), "Köldmedium R410A", StockUnit.Kilogram, 890m);
        var kopparror = StockItem.Create(Tenant, new("CU-15"), "Kopparrör 15 mm", StockUnit.Meter, 89m);
        db.Stock.AddRange(
            packning,
            koldmedium,
            StockItem.Create(Tenant, new("SRV-TIM"), "Servicetekniker, timme", StockUnit.Hour, 950m),
            kopparror,
            r22);

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

        // Work orders (docs/34 M2): one per state so the machine is visible in the demo.
        // Assignment stamps happen below once account ids exist.
        var woDraft = WorkOrder.Create(Tenant, new("WO-2026-0001"), pumpRefurb.Id,
            "Demontera pumphus", new("Demontera och rengör pumphus etapp 1"), acme.VisitAddress);
        var woScheduled = WorkOrder.Create(Tenant, new("WO-2026-0002"), pumpRefurb.Id,
            "Byt slitringar", new("Byt slitringar och lager på pump 2"), acme.VisitAddress);
        var woInProgress = WorkOrder.Create(Tenant, new("WO-2026-0003"), sveaVent.Id,
            "Riv gammalt aggregat", new("Demontering av befintligt ventilationsaggregat plan 3"),
            new("Storgatan 1, Stockholm"));
        var woDone = WorkOrder.Create(Tenant, new("WO-2026-0004"), sveaVent.Id,
            "Montera nya don", new("Montering av tilluftsdon plan 2"), new("Storgatan 1, Stockholm"));
        var woClosed = WorkOrder.Create(Tenant, new("WO-2026-0005"), pumpRefurb.Id,
            "Förbesiktning", new("Förbesiktning inför etapp 1"), acme.VisitAddress);
        // An URGENT draft (priority is set at creation, Normal elsewhere by default): a fresh
        // boot shows the priority column and the urgent-schedule-window rule has a live target.
        var woUrgent = WorkOrder.Create(Tenant, new("WO-2026-0006"), serviceDeal.Id,
            "Pumphaveri hos kund", new("Huvudpumpen har havererat — kunden står utan kyla"),
            acme.VisitAddress, WorkOrderPriority.Urgent);
        db.WorkOrders.AddRange(woDraft, woScheduled, woInProgress, woDone, woClosed, woUrgent);

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
            "orders.cancel", "orders.cancel-all",
            "customers.read", "customers.create", "customers.edit",
            "projects.read", "projects.create", "projects.edit", "projects.close",
            "stock.read", "stock.manage",
            "work-orders.read", "work-orders.read-all", "work-orders.create",
            "work-orders.edit", "work-orders.edit-all", "work-orders.schedule",
            "work-orders.assign", "work-orders.start", "work-orders.start-all",
            "work-orders.complete", "work-orders.complete-all", "work-orders.close",
            "users.lookup",
            // Time is own-scoped by default; the office reads the whole board and approves.
            "time.read", "time.read-all", "time.book", "time.approve",
            "materials.read", "materials.add",
            // Inspect v2 (docs/34 M6): the office curates templates and works checklists.
            "inspect.templates.read", "inspect.templates.manage",
            "inspect.checklists.read", "inspect.checklists.manage");
        // "viewer" is authored as ACCESS LEVELS (docs/27 D-A1): { orders: view, customers: view }
        // expands to the read atoms at load time — the level shape and the atom shape coexist.
        db.Add(new RoleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Name = "viewer",
            LevelsJson = """{"orders":"view","customers":"view","projects":"view","stock":"view","time":"view","materials":"view"}""",
        });
        // Technicians carry only the base atoms — own-scoped by construction, no suffixes.
        Role("technician",
            "orders.read", "orders.edit", "orders.complete", "customers.read",
            "projects.read", "stock.read",
            "work-orders.read", "work-orders.edit", "work-orders.start", "work-orders.complete",
            "users.lookup",
            // Base atoms only: a technician books and reads her OWN time; materials follow
            // the work order (no own scope — see materials.add).
            "time.read", "time.book",
            "materials.read", "materials.add",
            // Technicians check checklist lines off on site; templates stay the office's.
            "inspect.checklists.read", "inspect.checklists.manage");

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

        // Inspect v2 (docs/34 M6): checklist templates keyed on order type — one MANDATORY
        // service template (its checklists gate orders.complete) and one non-mandatory
        // (never blocks), so the demo shows both behaviors. Seeded exactly as the tenant
        // admin would author them through inspect.templates.define / add-item.
        var safety = Inspect.ChecklistTemplate.Create(
            Tenant, "Säkerhetskontroll", "service", mandatory: true);
        var handover = Inspect.ChecklistTemplate.Create(
            Tenant, "Överlämning till kund", "service", mandatory: false);
        db.AddRange(safety, handover,
            Inspect.ChecklistTemplateItem.Create(Tenant, safety.Id, 1, "Bryt och lås spänningen"),
            Inspect.ChecklistTemplateItem.Create(Tenant, safety.Id, 2, "Kontrollera tryckkärl och slangar"),
            Inspect.ChecklistTemplateItem.Create(Tenant, safety.Id, 3, "Fotografera arbetsplatsen före arbete"),
            Inspect.ChecklistTemplateItem.Create(Tenant, handover.Id, 1, "Gå igenom utfört arbete med kunden"),
            Inspect.ChecklistTemplateItem.Create(Tenant, handover.Id, 2, "Lämna serviceprotokoll"));

        // Checklists the templates WOULD have instantiated (seeded orders bypass the
        // pipeline, so the subscriber's work is mirrored here): the open service order
        // 2026-01415 carries both — its mandatory one blocks orders.complete until the
        // lines are checked off; 2026-01416's shows a partially completed non-mandatory one.
        void Instantiate(Inspect.ChecklistTemplate template, Order order, int doneLines)
        {
            var checklist = Inspect.Checklist.Create(
                Tenant, $"{template.Name} — {order.Number.Value}", order.Id.Value,
                template.Mandatory, template.Id);
            db.Add(checklist);
            var lines = db.ChangeTracker.Entries<Inspect.ChecklistTemplateItem>()
                .Select(e => e.Entity).Where(x => x.TemplateId == template.Id)
                .OrderBy(x => x.Position).ToList();
            foreach (var line in lines)
            {
                var item = Inspect.ChecklistItem.Create(
                    Tenant, checklist.Id, order.Id.Value, line.Position, line.Text);
                item.Done = line.Position <= doneLines;
                db.Add(item);
            }
        }
        Instantiate(safety, orders[3], doneLines: 0);
        Instantiate(handover, orders[3], doneLines: 0);
        Instantiate(handover, orders[4], doneLines: 1);

        // The demo tenant has already clicked Activate for inspect (plugins.activate writes
        // this row at runtime; seeding it keeps the checklist demo one boot away).
        db.Add(new PluginActivationEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            PluginId = "inspect",
        });

        // Invoicing too: with TWO plugins contributing panels to web.orders.detail, the order
        // record's PanelTabs marker expands into one tab per plugin (docs/32 arc 4) — the
        // multi-plugin story is visible on a fresh boot, not just in tests.
        db.Add(new PluginActivationEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            PluginId = "invoicing",
        });

        // The tenant's subscription (docs/24): a "standard" plan, 10 seats, entitled plugins.
        // A billing provider would drive this via subscriptions.set-plan. This row is the tree's
        // ANCHOR (docs/24 hierarchy): it covers "nord" too — the money cascades down the tree,
        // activation stays per node, and the seat pool spans both.
        db.Add(new SubscriptionEntity
        {
            TenantId = Tenant,
            Plan = "standard",
            Seats = 10,
            EntitlementsJson = """["inspect","fortnox","approvals","invoicing"]""",
            Status = "active",
        });

        // Ownership (:own scope) compares against the actor id, which is now the global account id
        // (docs/26), so assign by Tekla's account id — not the login handle.
        orders[3].AssignTo(accountIds["tekla"].ToString());
        orders[4].AssignTo(accountIds["tekla"].ToString());

        // Walk the seeded work orders through the machine — Tekla owns the active ones, so the
        // own-scope pairs and the technician's board are demonstrable from first login.
        var tekla = accountIds["tekla"].ToString();
        woScheduled.Schedule(new DateOnly(2026, 7, 21), tekla, "Tekla Nilsson");
        woInProgress.Schedule(new DateOnly(2026, 7, 14), tekla, "Tekla Nilsson");
        woInProgress.Start();
        woDone.Schedule(new DateOnly(2026, 7, 10), tekla, "Tekla Nilsson");
        woDone.Start();
        woDone.Complete();
        woClosed.Schedule(new DateOnly(2026, 6, 30), accountIds["didrik"].ToString(), "Didrik Berg");
        woClosed.Start();
        woClosed.Complete();
        woClosed.CloseOut();

        // Time entries (docs/34 M3): owned by the booking technician; Draft until the office
        // approves (time.approve — the M4 invoicing seam takes approved time only).
        var didrik = accountIds["didrik"].ToString();
        var teklaDay1 = TimeEntry.Book(Tenant, woInProgress.Id, tekla, "Tekla Nilsson",
            new DateOnly(2026, 7, 14), 6m, 950m, new("Demontering av aggregat, plan 3"));
        var teklaDay2 = TimeEntry.Book(Tenant, woInProgress.Id, tekla, "Tekla Nilsson",
            new DateOnly(2026, 7, 15), 4.5m, 950m, null);
        var teklaDone = TimeEntry.Book(Tenant, woDone.Id, tekla, "Tekla Nilsson",
            new DateOnly(2026, 7, 10), 8m, 950m, new("Montering av tilluftsdon"));
        teklaDone.Approve();
        var didrikClosed = TimeEntry.Book(Tenant, woClosed.Id, didrik, "Didrik Berg",
            new DateOnly(2026, 6, 30), 3m, 895m, new("Förbesiktning på plats"));
        didrikClosed.Approve();
        db.TimeEntries.AddRange(teklaDay1, teklaDay2, teklaDone, didrikClosed);

        // Material lines (docs/34 M3): the unit price is a SNAPSHOT taken at entry time.
        // The copper line deliberately carries 79 kr — the catalog price was raised to 89
        // afterwards; booked history must not follow it.
        db.MaterialLines.AddRange(
            MaterialLine.Add(Tenant, woInProgress.Id, packning.Id, 2m, packning.UnitPrice),
            MaterialLine.Add(Tenant, woInProgress.Id, koldmedium.Id, 3.2m, koldmedium.UnitPrice),
            MaterialLine.Add(Tenant, woDone.Id, kopparror.Id, 12m, 79m),
            MaterialLine.Add(Tenant, woClosed.Id, packning.Id, 1m, packning.UnitPrice));

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

        // A runtime custom field on the NEW entity (docs/34 M2): proves docs/15 generalizes
        // beyond the tutorial's order — boolean this time (a third wire kind in the demo).
        db.Add(new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Entity = "work-order",
            Key = "requiresLift",
            Type = "boolean",
            LabelsJson = """{"sv":"Kräver lift","en":"Requires lift"}""",
            State = ExtensionFieldState.Active,
        });
        woScheduled.Extensions = woScheduled.Extensions.WithValue("requiresLift", true);

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

        // The tenant's automation rule (docs/22), stored exactly as rules.define writes it:
        // "an URGENT work order cannot be scheduled more than 7 days out." The condition is
        // Px AST data over the schedule intent's input (scheduledDate) AND its target row
        // (row.priority — enums compare as wire strings); the cutoff is the RELATIVE-DATE
        // node {"t":"fn","op":"today","days":7} — evaluated fresh on every check, so the
        // policy never drifts (the define-time-constant wart RTFM #3 filed, closed).
        // Message text is per-culture rule DATA (the registry twin of the locale catalogs).
        db.Add(new AutomationRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Name = "urgent-schedule-window",
            OnOperation = "work-orders.schedule",
            ConditionJson =
                """{"t":"bin","op":"and","l":{"t":"bin","op":"eq","l":{"t":"field","f":"row.priority"},"r":{"t":"const","v":"urgent"}},"r":{"t":"bin","op":"gt","l":{"t":"field","f":"scheduledDate"},"r":{"t":"fn","op":"today","days":7}}}""",
            TargetField = "scheduledDate",
            MessagesJson = """{"sv":"Akuta arbetsordrar måste planeras inom 7 dagar.","en":"Urgent work orders must be scheduled within 7 days."}""",
            RowEntityKey = "work-order",
            RowIdField = "workOrderId",
        });

        db.SaveChanges();
    }
}
