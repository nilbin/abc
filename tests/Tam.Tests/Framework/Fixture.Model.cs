using Tam;
using Tam.AspNetCore;

namespace Tam.Tests.Framework;

public static class WidgetModel
{
    /// <summary>The composed framework model BEFORE Build() — the seam a test uses to add a probe
    /// derivation host (ContractEnforcement) or an inline form to the real model, exactly as the ERP
    /// suite uses ErpModel.Builder(). The always-active system packages own extensions.define-field, the
    /// extension registry and the rules engine, and self-supply their locales from embedded resources.</summary>
    public static TamModelBuilder Builder() => new TamModelBuilder()
        .DefaultCulture("en")
        .AddTamSystem()
        .AddOperationType(typeof(CreateWidget))
        .AddOperationType(typeof(EditWidget))
        .AddOperationType(typeof(CreateWidgetPlain))
        .AddOperationType(typeof(SetWidgetPriority))
        .AddOperationType(typeof(CompleteWidget))
        .AddOperationType(typeof(CloseBin))
        .AddViewType(typeof(BinLookup))
        .AddDerivationHost(typeof(WidgetDerivations))
        .AddEventType(typeof(WidgetCreated))
        .Form<CreateWidget.Input>("web.widgets.create", "widgets.create", form =>
        {
            form.Field(x => x.Name);
            form.Field(x => x.Category);
            form.Field(x => x.GroupId);
            form.Field(x => x.BinId).VisibleWhen(x => x.Category == WidgetCategory.Special);
            form.Field(x => x.Description);
            form.Field(x => x.Location);
            form.Extensions();
        })
        .Form<EditWidget.Input>("web.widgets.edit", "widgets.edit", form =>
        {
            form.Field(x => x.WidgetId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.Description);
            form.Extensions();
        })
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["labels.widget-id"] = "Widget",
            ["labels.bin-id"] = "Bin",
            ["labels.description"] = "Description",
            ["labels.location"] = "Location",
            ["labels.category"] = "Category",
            ["labels.priority"] = "Priority",
            ["labels.group-id"] = "Group",
            ["labels.budget"] = "Budget",
            ["operations.widgets.create.title"] = "Create widget",
            ["operations.widgets.create-plain.title"] = "Create widget (plain)",
            ["operations.widgets.edit.title"] = "Edit widget",
            ["operations.widgets.set-priority.title"] = "Set widget priority",
            ["operations.widgets.complete.title"] = "Complete widget",
            ["operations.bins.close.title"] = "Close bin",
        });

    public static TamModel Build() => Builder().Build();
}
