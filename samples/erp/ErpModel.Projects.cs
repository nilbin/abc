using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    // The field-service slice (docs/34 M1): a declared page, zero React.
    private static TamModelBuilder AddProjects(this TamModelBuilder model) => model
        .Page("projects", page => page
            .Grid("web.projects.list")
            .Record(record => record
                .Detail("projects.detail", key: "projectId")
                .Title("number")
                .Form("web.projects.edit")))

        .Form<CreateProject.Input>("web.projects.create", "projects.create", form =>
        {
            form.Field(x => x.CustomerId);   // [Lookup] on CustomerId renders the picker
            form.Field(x => x.Number);
            form.Field(x => x.Name);
            form.Field(x => x.Budget);
        })

        .Form<EditProjectDetails.Input>("web.projects.edit", "projects.edit-details", form =>
        {
            form.Field(x => x.ProjectId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.Budget);
        })

        .Grid<ProjectList.Result>("web.projects.list", "projects.list", grid =>
        {
            grid.Column(x => x.Number);
            grid.Column(x => x.TenantId);   // the company column — rendered only above a leaf
            grid.Column(x => x.Name);
            grid.Column(x => x.CustomerName);
            grid.Column(x => x.Status);
            grid.Column(x => x.Budget);
            grid.RowAction("projects.close");
            grid.RowAction("projects.reopen");
            grid.ToolbarAction("projects.create");
        });
}
