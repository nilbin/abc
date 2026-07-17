using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    // Grid + record, no slots — the customers surface is not a contribution point until a
    // plugin needs it (docs/32).
    private static TamModelBuilder AddCustomers(this TamModelBuilder model) => model
        .Page("customers", page => page
            .Grid("web.customers.list")
            .Record(record => record
                .Detail("customers.detail", key: "customerId")
                .Title("name")
                .Form("web.customers.edit")))

        // No configure: the record IS the form — every input field, declaration order (docs/32).
        .Form<CreateCustomer.Input>("web.customers.create", "customers.create")

        // Enumerated only to hide the record key (the modal already IS the customer).
        .Form<EditCustomerContact.Input>("web.customers.edit", "customers.edit-contact", form =>
        {
            form.Field(x => x.CustomerId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.VisitAddress);
            form.Field(x => x.Email);
            form.Field(x => x.Phone);
        })

        .Grid<CustomerList.Result>("web.customers.list", "customers.list", grid =>
        {
            grid.Column(x => x.Name);
            grid.Column(x => x.Email);
            grid.Column(x => x.Phone);
            grid.Column(x => x.VisitAddress);
            grid.Column(x => x.IsActive);
            grid.RowAction("customers.deactivate");
            grid.ToolbarAction("customers.create");
        });
}
