using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.AspNetCore.SystemOps;
using Tam.Generated;

var model = new TamModelBuilder()
    .DefaultCulture("sv")
    .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
    .AddDiscovered()   // compile-time discovery from Tam.Compiler — no runtime assembly scan
    .AddTamSystem()    // framework operations/views: custom fields, roles, audit, plugins
    .AddPlugin<Inspect.InspectionPlugin>()   // compiled in; each tenant activates at runtime (docs/22)

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
});
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddSingleton<Tam.AspNetCore.IActorProvider, DemoActorProvider>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTam();
Erp.Integrations.ImportFortnoxOrders.Map(app, model);
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    Seed.Run(scope.ServiceProvider.GetRequiredService<ErpDbContext>());
}

app.Run();

public partial class Program;
