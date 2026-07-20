using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;

namespace Tam.Tests.Framework;

/// <summary>The REAL create derivation: a Special widget requires a Bin (operation-owned requiredness),
/// bound to the bins.lookup universe scoped to its Group (authoritative membership at submit).</summary>
public static class WidgetDerivations
{
    [ServerDerivation("widgets.create.available-bins")]
    [DependsOn(nameof(CreateWidget.Input.Category), nameof(CreateWidget.Input.GroupId))]
    public static DerivationResult AvailableBins(CreateWidget.Input input, DerivationContext context)
    {
        var isSpecial = input.Category == WidgetCategory.Special;
        var result = DerivationResult.Empty
            .Require(nameof(CreateWidget.Input.BinId), isSpecial, WidgetFindings.BinRequired);
        if (!isSpecial || input.GroupId == Guid.Empty) return result;
        return result.Lookup(
            nameof(CreateWidget.Input.BinId), "bins.lookup",
            new Dictionary<string, string?> { ["groupId"] = input.GroupId.ToString() },
            WidgetFindings.BinNotAvailable);
    }
}

/// <summary>Test-only probe derivations on widgets.create / widgets.edit (the framework twin of the ERP
/// BlockingProbeDerivations): each fires only under a sentinel Description the handler never inspects, so a
/// green/blocked/thrown submit through it can only be the pipeline enforcing the probe's contract.</summary>
public static class WidgetProbes
{
    public const string Sentinel = "__probe_block__";
    public const string BadFilterSentinel = "__bad_filter__";
    public const string BadOperatorSentinel = "__bad_operator__";
    public const string ClosedOptionsSentinel = "__closed_options__";
    public const string DoubleLookupSentinel = "__double_lookup__";
    public const string MutateSentinel = "__mutate__";
    public const string MutateSaveSentinel = "__mutate_save__";
    public const string CommentWriteSentinel = "__comment_write__";
    public const string CteWriteSentinel = "__cte_write__";
    public const string MixedCandidateSentinel = "__mixed_candidate__";
    public const string OverwriteOptionsSentinel = "__overwrite_options__";
    public const string EditSentinel = "__edit_probe__";
    public const string WasChangedSentinel = "__was_changed_probe__";
    public const string NonChangeChangedSentinel = "__nonchange_changed_probe__";
    public const string AllowedLocation = "Tillåtnagatan 1";

    public static readonly FindingFactory Rejected = Finding.Error("test.probe-rejected");
    public static readonly FindingFactory ChangeSeen = Finding.Error("test.change-seen");
    public static readonly FindingFactory NonChangeSeen = Finding.Error("test.nonchange-seen");

    [ServerDerivation("test.widgets.create.block-probe")]
    public static DerivationResult BlockOnSentinel(CreateWidget.Input input, DerivationContext context) =>
        input.Description == Sentinel
            ? DerivationResult.FieldError(nameof(CreateWidget.Input.Description), Rejected)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.edit.change-probe")]
    public static DerivationResult EditChangeProbe(EditWidget.Input input, DerivationContext context) =>
        input.Description?.Value == EditSentinel
            ? DerivationResult.FieldError(nameof(EditWidget.Input.Description), Rejected)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.edit.was-changed-probe")]
    public static DerivationResult WasChangedProbe(EditWidget.Input input, DerivationContext context) =>
        input.Description?.Value == WasChangedSentinel
            && context.WasChanged(nameof(EditWidget.Input.Description))
            ? DerivationResult.FieldError(nameof(EditWidget.Input.Description), ChangeSeen)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.nonchange-changed-probe")]
    public static DerivationResult NonChangeChangedProbe(CreateWidget.Input input, DerivationContext context) =>
        input.Description == NonChangeChangedSentinel
            && context.WasChanged(nameof(CreateWidget.Input.Location))
            ? DerivationResult.FieldError(nameof(CreateWidget.Input.Location), NonChangeSeen)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.bad-filter-probe")]
    public static DerivationResult BadFilter(CreateWidget.Input input, DerivationContext context) =>
        input.Description == BadFilterSentinel
            ? DerivationResult.Empty.Lookup(
                nameof(CreateWidget.Input.BinId), "bins.lookup",
                new Dictionary<string, string?> { ["grupId"] = input.GroupId.ToString() },
                WidgetFindings.BinNotAvailable)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.bad-operator-probe")]
    public static DerivationResult BadOperator(CreateWidget.Input input, DerivationContext context) =>
        input.Description == BadOperatorSentinel
            ? DerivationResult.Empty.Lookup(
                nameof(CreateWidget.Input.BinId), "bins.lookup",
                new Dictionary<string, string?> { ["groupId.contains"] = input.GroupId.ToString() },
                WidgetFindings.BinNotAvailable)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.closed-options-probe")]
    public static DerivationResult ClosedOptions(CreateWidget.Input input, DerivationContext context) =>
        input.Description == ClosedOptionsSentinel
            ? DerivationResult.Empty.RequireOneOf(
                nameof(CreateWidget.Input.Location),
                [new Option(new WidgetLocation(AllowedLocation), AllowedLocation)],
                WidgetFindings.BinNotAvailable)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.double-lookup-probe")]
    public static DerivationResult DoubleLookup(CreateWidget.Input input, DerivationContext context) =>
        input.Description == DoubleLookupSentinel
            ? DerivationResult.Empty
                .Lookup(nameof(CreateWidget.Input.BinId), "bins.lookup",
                    new Dictionary<string, string?>(), WidgetFindings.BinNotAvailable)
                .Lookup(nameof(CreateWidget.Input.BinId), "bins.lookup",
                    new Dictionary<string, string?>(), WidgetFindings.BinNotAvailable)
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.mutate-probe")]
    public static DerivationResult Mutate(CreateWidget.Input input, DerivationContext context, WidgetDbContext db)
    {
        if (input.Description == MutateSentinel)
            db.Bins.Add(new Bin { Id = new BinId(Guid.NewGuid()), TenantId = "demo", Name = "Smuggel" });
        return DerivationResult.Empty;
    }

    [ServerDerivation("test.widgets.create.mutate-save-probe")]
    public static async Task<DerivationResult> MutateAndSave(
        CreateWidget.Input input, DerivationContext context, WidgetDbContext db, CancellationToken ct)
    {
        if (input.Description == MutateSaveSentinel)
        {
            db.Bins.Add(new Bin { Id = new BinId(Guid.NewGuid()), TenantId = "demo", Name = "Sparsmuggel" });
            await db.SaveChangesAsync(ct);
        }
        return DerivationResult.Empty;
    }

    [ServerDerivation("test.widgets.create.raw-write-probe")]
    public static async Task<DerivationResult> RawWrite(
        CreateWidget.Input input, DerivationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var sql = input.Description switch
        {
            CommentWriteSentinel => "-- derivation helper\nUPDATE Widgets SET Name = 'Kapad' WHERE 1 = 0",
            CteWriteSentinel => "WITH t AS (SELECT 1 AS x) UPDATE Widgets SET Name = 'Kapad' WHERE 1 = 0",
            _ => null,
        };
        if (sql is not null) await db.Database.ExecuteSqlRawAsync(sql, ct);
        return DerivationResult.Empty;
    }

    [ServerDerivation("test.widgets.create.overwrite-options-probe")]
    public static DerivationResult OverwriteOptions(CreateWidget.Input input, DerivationContext context) =>
        input.Description == OverwriteOptionsSentinel
            ? DerivationResult.Empty
                .RequireOneOf(nameof(CreateWidget.Input.Location),
                    [new Option(new WidgetLocation(AllowedLocation), AllowedLocation)], WidgetFindings.BinNotAvailable)
                .AddOptions(nameof(CreateWidget.Input.Location),
                    [new Option(new WidgetLocation("Vilseledande 9"), "Vilseledande 9")])
            : DerivationResult.Empty;

    [ServerDerivation("test.widgets.create.mixed-candidate-probe")]
    public static DerivationResult MixedCandidate(CreateWidget.Input input, DerivationContext context) =>
        input.Description == MixedCandidateSentinel
            ? DerivationResult.Empty
                .Lookup(nameof(CreateWidget.Input.BinId), "bins.lookup",
                    new Dictionary<string, string?>(), WidgetFindings.BinNotAvailable)
                .RequireOneOf(nameof(CreateWidget.Input.BinId),
                    [new Option(Guid.NewGuid(), "x")], WidgetFindings.BinNotAvailable)
            : DerivationResult.Empty;
}
