using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;

var model = new TamModelBuilder()
    .DefaultCulture("sv")
    .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
    .AddAssembly(typeof(Program).Assembly)

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

    .Grid<ExtensionFieldList.Result>("web.extensions.fields", "extensions.fields", grid =>
    {
        grid.Column(x => x.Entity);
        grid.Column(x => x.Key);
        grid.Column(x => x.Type);
        grid.Column(x => x.Required);
        grid.Column(x => x.State);
        grid.ToolbarAction("extensions.define-field");
    })

    .Build();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ErpDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("erp") ?? "Data Source=erp.db"));
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTam();
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    Seed.Run(scope.ServiceProvider.GetRequiredService<ErpDbContext>());
}

app.Run();

public partial class Program;
