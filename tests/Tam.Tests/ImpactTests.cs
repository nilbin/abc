using Tam;

namespace Tam.Tests;

/// <summary>Step 12: the consolidated change-impact report — the compiled model diffed
/// against a baseline manifest. The baseline is the CURRENT export doctored backwards
/// (records with {}), so each test states exactly one "six months ago" difference.</summary>
public class ImpactTests
{
    [View("things.list")]
    [Authorize("things.read")]
    private static class ThingsList
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("labels.name")]
            public string Name { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    [Operation("things.rename")]
    [Authorize("things.manage")]
    private static class RenameThing
    {
        public sealed record Input(
            [property: LabelKey("labels.id")] Guid ThingId,
            [property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    private static TamModel Model() => new TamModelBuilder()
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["labels.id"] = "Id", ["labels.name"] = "Name",
            ["operations.things.rename.title"] = "Rename",
        })
        .AddViewType(typeof(ThingsList))
        .AddOperationType(typeof(RenameThing))
        .Form<RenameThing.Input>("web.things.edit", "things.rename", form => form.Field(x => x.Name))
        .Build();

    private static ManifestDto Export(TamModel model) => ManifestBuilder.Build(
        model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), revision: 0);

    [Fact]
    public void Unchanged_model_reports_no_changes()
    {
        var model = Model();
        var report = TamImpact.Against(model, Export(model));
        Assert.False(report.HasChanges);
        Assert.False(report.HasBreaks);
        Assert.Contains("no manifest changes vs baseline", report.Format());
    }

    [Fact]
    public void New_required_input_is_named_breaking_and_lists_the_auto_surfaces()
    {
        var model = Model();
        var baseline = Export(model);
        // Six months ago the input had no Name field — today's model adds it required.
        var op = baseline.Operations["things.rename"];
        baseline = baseline with
        {
            Operations = new Dictionary<string, ManifestOperation>(baseline.Operations)
            {
                ["things.rename"] = op with
                {
                    Fields = op.Fields.Where(f => f.Name != "name").ToList(),
                },
            },
        };

        var report = TamImpact.Against(model, baseline);
        var text = report.Format();
        Assert.True(report.HasBreaks);
        Assert.Contains("CHANGED operation things.rename", text);
        Assert.Contains("field name ADDED (required)", text);
        Assert.Contains("web.things.edit", text);                       // the form that re-renders
        Assert.Contains("MCP tool schema", text);                       // the silent-green, stated
        Assert.Contains("new REQUIRED field 'name' breaks existing callers", text);
    }

    [Fact]
    public void Removed_operation_and_view_field_are_breaking()
    {
        var model = Model();
        var baseline = Export(model);
        // Six months ago there was one MORE operation, and things.list served a field the
        // current model no longer has.
        var view = baseline.Views["things.list"];
        baseline = baseline with
        {
            Operations = new Dictionary<string, ManifestOperation>(baseline.Operations)
            {
                ["things.retire"] = baseline.Operations["things.rename"],
            },
            Views = new Dictionary<string, ManifestView>(baseline.Views)
            {
                ["things.list"] = view with
                {
                    ResultFields = [.. view.ResultFields,
                        new ManifestField("legacyCode", "labels.name", "text", "string",
                            null, false, null, null, false)],
                },
            },
        };

        var report = TamImpact.Against(model, baseline);
        var text = report.Format();
        Assert.True(report.HasBreaks);
        Assert.Contains("REMOVED operation things.retire", text);
        Assert.Contains("field legacyCode REMOVED", text);
        Assert.Contains("NON-ADDITIVE", text);
    }

    [Fact]
    public void Additive_change_says_the_baseline_stays_valid()
    {
        var model = Model();
        var baseline = Export(model);
        // Six months ago things.list had no name column — today's addition is additive.
        var view = baseline.Views["things.list"];
        baseline = baseline with
        {
            Views = new Dictionary<string, ManifestView>(baseline.Views)
            {
                ["things.list"] = view with
                {
                    ResultFields = view.ResultFields.Where(f => f.Name != "name").ToList(),
                },
            },
        };

        var report = TamImpact.Against(model, baseline);
        Assert.True(report.HasChanges);
        Assert.False(report.HasBreaks);
        Assert.Contains("additive-only — the committed baseline stays valid", report.Format());
    }
}
