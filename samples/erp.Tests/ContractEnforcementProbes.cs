using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;

namespace Erp.Tests;

/// <summary>Test-only derivation on orders.create: emits a blocking finding on a sentinel input the
/// create handler never inspects, so a green submit through it can only mean the pipeline enforced
/// the derivation's finding. Registered via ErpModel.Builder().AddDerivationHost in the test.</summary>
public static class BlockingProbeDerivations
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
    public const string AllowedAddress = "Tillåtnagatan 1";

    public static readonly FindingFactory Rejected = Finding.Error("test.probe-rejected");
    public static readonly FindingFactory ChangeSeen = Finding.Error("test.change-seen");
    public static readonly FindingFactory NonChangeSeen = Finding.Error("test.nonchange-seen");

    [ServerDerivation("test.orders.create.block-probe")]
    public static DerivationResult BlockOnSentinel(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == Sentinel
            ? DerivationResult.FieldError(nameof(CreateOrder.Input.Description), Rejected)
            : DerivationResult.Empty;

    // A derivation on the EDIT operation that reads a Change<T> field (Sol re-review, Finding 3):
    // resolve can only run this if the client sends Description in its {original, value} wire shape.
    [ServerDerivation("test.orders.edit.change-probe")]
    public static DerivationResult EditChangeProbe(EditOrderDetails.Input input, DerivationContext context) =>
        input.Description?.Value?.Value == EditSentinel
            ? DerivationResult.FieldError(nameof(EditOrderDetails.Input.Description), Rejected)
            : DerivationResult.Empty;

    // The change-MEMBERSHIP signal (Sol re-review round 6, F3): the description value is identical in
    // both resolves below, but the finding fires only when the caller's change set lists it. WasChanged
    // — not wrapper presence — is the reliable "did the user touch this" signal, since resolve sends
    // every initialized Change<T> whether touched or not.
    [ServerDerivation("test.orders.edit.was-changed-probe")]
    public static DerivationResult WasChangedProbe(EditOrderDetails.Input input, DerivationContext context) =>
        input.Description?.Value?.Value == WasChangedSentinel
            && context.WasChanged(nameof(EditOrderDetails.Input.Description))
            ? DerivationResult.FieldError(nameof(EditOrderDetails.Input.Description), ChangeSeen)
            : DerivationResult.Empty;

    // WasChanged is a CHANGE-SET concept (Sol re-review round 7, F3): a non-change-set field is always
    // present in a create body but is never "changed". This probe fires if WasChanged wrongly reports a
    // non-change field as changed — proving submit's body-presence set is narrowed to change-set fields,
    // so it can't disagree with resolve's touched set.
    [ServerDerivation("test.orders.create.nonchange-changed-probe")]
    public static DerivationResult NonChangeChangedProbe(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == NonChangeChangedSentinel
            && context.WasChanged(nameof(CreateOrder.Input.WorkAddress))
            ? DerivationResult.FieldError(nameof(CreateOrder.Input.WorkAddress), NonChangeSeen)
            : DerivationResult.Empty;

    // Binds ProjectId to a lookup whose base filter key is misspelled — only under the sentinel, so
    // ordinary orders in this host are untouched. Proves membership fails closed on a bogus filter.
    [ServerDerivation("test.orders.create.bad-filter-probe")]
    public static DerivationResult BadFilter(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == BadFilterSentinel
            ? DerivationResult.Empty.Lookup(
                nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                new Dictionary<string, string?> { ["custmerId"] = input.CustomerId.Value.ToString() },
                OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // Binds ProjectId with an UNSUPPORTED operator for the field type: customerId on projects.lookup
    // is a Guid, so `.contains` is not a legal predicate. A silently-dropped operator would widen the
    // universe exactly like an unknown key (Sol re-review, Finding 2) — so it must fail closed too.
    [ServerDerivation("test.orders.create.bad-operator-probe")]
    public static DerivationResult BadOperator(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == BadOperatorSentinel
            ? DerivationResult.Empty.Lookup(
                nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                new Dictionary<string, string?> { ["customerId.contains"] = input.CustomerId.Value.ToString() },
                OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // Declares an authoritative CLOSED option set on WorkAddress — the complete legal set — under the
    // sentinel. The option value is a SEMANTIC WRAPPER (Address), which membership must unwrap to its
    // scalar to compare against the submitted key (Sol re-review, Finding 5).
    [ServerDerivation("test.orders.create.closed-options-probe")]
    public static DerivationResult ClosedOptions(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == ClosedOptionsSentinel
            ? DerivationResult.Empty.RequireOneOf(
                nameof(CreateOrder.Input.WorkAddress),
                [new Option(new Address(AllowedAddress), AllowedAddress)],
                OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // Emits TWO lookup bindings for the SAME field in one evaluation — resolve would show one and
    // submit would enforce both (Sol re-review, Finding 6). Must fail closed (DER008).
    [ServerDerivation("test.orders.create.double-lookup-probe")]
    public static DerivationResult DoubleLookup(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == DoubleLookupSentinel
            ? DerivationResult.Empty
                .Lookup(nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                    new Dictionary<string, string?>(), OrderFindings.ProjectNotAvailable)
                .Lookup(nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                    new Dictionary<string, string?>(), OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // MUTATES the operation's writable context — the exact thing derivations must not do (Sol
    // re-review, Finding 3). An unsaved Add is detected as tracked write-state and fails closed (DER007).
    [ServerDerivation("test.orders.create.mutate-probe")]
    public static DerivationResult Mutate(CreateOrder.Input input, DerivationContext context, ErpDbContext db)
    {
        if (input.Description.Value == MutateSentinel)
            db.Customers.Add(Customer.Create("demo", new("Smuggelkund"), new("Smuggelgatan 1"), null, null));
        return DerivationResult.Empty;
    }

    // The DURABLE variant: Add THEN SaveChanges — the escape a tracked-state check alone would miss.
    // The write-guard interceptor rejects SaveChanges structurally, so nothing is ever committed.
    [ServerDerivation("test.orders.create.mutate-save-probe")]
    public static async Task<DerivationResult> MutateAndSave(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.Description.Value == MutateSaveSentinel)
        {
            db.Customers.Add(Customer.Create("demo", new("Sparsmuggel"), new("Smuggelgatan 2"), null, null));
            await db.SaveChangesAsync(ct);
        }
        return DerivationResult.Empty;
    }

    // The change-tracker-BYPASSING writes the old first-token denylist let through (Sol re-review
    // round 4, Finding 1): a comment-prefixed raw UPDATE (verb hidden behind `--`) and a CTE UPDATE
    // (leading token WITH). The fail-closed classifier strips comments + the CTE and rejects both.
    [ServerDerivation("test.orders.create.raw-write-probe")]
    public static async Task<DerivationResult> RawWrite(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        var sql = input.Description.Value switch
        {
            CommentWriteSentinel => "-- derivation helper\nUPDATE Customers SET Name = 'Kapad' WHERE 1 = 0",
            CteWriteSentinel => "WITH t AS (SELECT 1 AS x) UPDATE Customers SET Name = 'Kapad' WHERE 1 = 0",
            _ => null,
        };
        if (sql is not null) await db.Database.ExecuteSqlRawAsync(sql, ct);
        return DerivationResult.Empty;
    }

    // Overwriting the DISPLAYED options after declaring the closed set (Sol re-review round 5, Finding
    // 2): RequireOneOf's legal set, then a different AddOptions list. Resolve would show one thing and
    // submit enforce another — caught as two candidate sources REGARDLESS of the fluent call order.
    [ServerDerivation("test.orders.create.overwrite-options-probe")]
    public static DerivationResult OverwriteOptions(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == OverwriteOptionsSentinel
            ? DerivationResult.Empty
                .RequireOneOf(nameof(CreateOrder.Input.WorkAddress),
                    [new Option(new Address(AllowedAddress), AllowedAddress)], OrderFindings.ProjectNotAvailable)
                .AddOptions(nameof(CreateOrder.Input.WorkAddress),
                    [new Option(new Address("Vilseledande 9"), "Vilseledande 9")])
            : DerivationResult.Empty;

    // Two candidate SOURCES on one field (Sol re-review round 4, Finding 3): a Lookup AND a closed
    // option set on ProjectId. Resolve can render only one; submit would enforce both. Fail closed.
    [ServerDerivation("test.orders.create.mixed-candidate-probe")]
    public static DerivationResult MixedCandidate(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == MixedCandidateSentinel
            ? DerivationResult.Empty
                .Lookup(nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                    new Dictionary<string, string?>(), OrderFindings.ProjectNotAvailable)
                .RequireOneOf(nameof(CreateOrder.Input.ProjectId),
                    [new Option("x", "x")], OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;
}
