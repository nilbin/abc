using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// The operation contract is enforced for EVERY caller at submit, not merely previewed at resolve
/// (docs/40) — FRAMEWORK behavior, driven here through the framework-owned Widget model plus a probe
/// derivation host added via the Builder() seam (nothing faked: the probes ride the same
/// RunDerivationsAsync path production uses). Two authority gaps this pins closed: a BLOCKING derivation
/// finding stops submit even when the handler would have succeeded, and the selected FORM is part of
/// idempotency identity.
/// </summary>
public sealed class ContractEnforcementTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private TestActor<WidgetDbContext> actor = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(
            WidgetModel.Builder().AddDerivationHost(typeof(WidgetProbes)).Build());
        actor = host.Actor("demo", "widgets.create", "widgets.edit", "bins.read");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private static object StandardWidget(string description) => new { name = "W", description };

    [Fact]
    public async Task A_blocking_derivation_finding_stops_submit_even_when_the_handler_would_succeed()
    {
        // The sentinel description makes the probe emit a blocking field error; the create handler has no
        // such check and would happily create the widget. The pipeline must reject it — proof that
        // derived.Findings are authoritative at submit, not just shown at resolve.
        (await actor.ExecuteAsync("widgets.create", StandardWidget(WidgetProbes.Sentinel)))
            .ShouldFailWith("test.probe-rejected", onField: "description");

        // Control: an ordinary description succeeds — the probe is inert and nothing else blocks.
        (await actor.ExecuteAsync("widgets.create", StandardWidget("an ordinary widget"))).ShouldSucceed();
    }

    [Fact]
    public async Task The_form_binding_is_part_of_idempotency_identity()
    {
        var body = StandardWidget("idempotent widget");

        var first = await actor.ExecuteAsync("widgets.create", body, idempotencyKey: "K");
        first.ShouldSucceed();
        Assert.DoesNotContain(first.Findings, f => f.Code == "pipeline.idempotent-replay");

        var replay = await actor.ExecuteAsync("widgets.create", body, idempotencyKey: "K");
        replay.ShouldSucceed();
        Assert.Contains(replay.Findings, f => f.Code == "pipeline.idempotent-replay");

        // Through the create form, SAME body + key: a DIFFERENT effective contract, so an INDEPENDENT
        // idempotency record — a fresh outcome, never the direct call's replayed result.
        var viaForm = await actor.ExecuteThroughFormAsync(
            "web.widgets.create", "widgets.create", body, idempotencyKey: "K");
        viaForm.ShouldSucceed();
        Assert.DoesNotContain(viaForm.Findings, f => f.Code == "pipeline.idempotent-replay");
    }

    [Fact]
    public async Task An_unknown_lookup_base_filter_fails_closed()
    {
        // The probe binds BinId to bins.lookup with a TYPO'd base filter ("grupId"). A silently-ignored
        // key would widen the candidate universe and admit an out-of-scope selection, so membership
        // refuses to run and the contract bug is surfaced loudly rather than accepted.
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create", new
        {
            name = "W", groupId = Guid.NewGuid(), binId = Guid.NewGuid(), description = WidgetProbes.BadFilterSentinel,
        }));
    }

    [Fact]
    public async Task A_closed_inline_option_set_is_enforced_at_submit()
    {
        // The probe declares Location's complete legal set. A value outside it is rejected...
        (await actor.ExecuteAsync("widgets.create", new
        {
            name = "W", location = "Otillåtnagatan 99", description = WidgetProbes.ClosedOptionsSentinel,
        })).ShouldFailWith("widgets.bin-not-available", onField: "location");

        // ...and a value inside it (matched through the option's semantic wrapper) goes through.
        (await actor.ExecuteAsync("widgets.create", new
        {
            name = "W", location = WidgetProbes.AllowedLocation, description = WidgetProbes.ClosedOptionsSentinel,
        })).ShouldSucceed();

        // A case-variant is NOT accepted — the comparison is ordinal, not case-folding.
        (await actor.ExecuteAsync("widgets.create", new
        {
            name = "W", location = WidgetProbes.AllowedLocation.ToLowerInvariant(),
            description = WidgetProbes.ClosedOptionsSentinel,
        })).ShouldFailWith("widgets.bin-not-available", onField: "location");
    }

    [Fact]
    public async Task An_unsupported_lookup_filter_operator_fails_closed()
    {
        // groupId is a Guid, so `.contains` generates no predicate — accepting it would widen the
        // universe just like an unknown key. Now rejected as a contract bug.
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create", new
        {
            name = "W", groupId = Guid.NewGuid(), binId = Guid.NewGuid(), description = WidgetProbes.BadOperatorSentinel,
        }));
    }

    [Fact]
    public async Task Two_lookup_bindings_for_one_field_fail_closed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create", new
        {
            name = "W", binId = Guid.NewGuid(), description = WidgetProbes.DoubleLookupSentinel,
        }));
    }

    [Fact]
    public async Task A_derivation_that_mutates_tracked_state_fails_closed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create",
            StandardWidget(WidgetProbes.MutateSentinel)));

        var smuggled = await host.QueryDbAsync("demo", db => db.Bins.CountAsync(b => b.Name == "Smuggel"));
        Assert.Equal(0, smuggled);
    }

    [Fact]
    public async Task A_derivation_that_saves_is_rejected_and_commits_nothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create",
            StandardWidget(WidgetProbes.MutateSaveSentinel)));

        var smuggled = await host.QueryDbAsync("demo", db => db.Bins.CountAsync(b => b.Name == "Sparsmuggel"));
        Assert.Equal(0, smuggled);
    }

    [Fact]
    public async Task A_derivation_cannot_bypass_the_write_guard_with_comments_or_ctes()
    {
        foreach (var sentinel in new[] { WidgetProbes.CommentWriteSentinel, WidgetProbes.CteWriteSentinel })
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create",
                StandardWidget(sentinel)));

        var hijacked = await host.QueryDbAsync("demo", db => db.Widgets.CountAsync(w => w.Name == "Kapad"));
        Assert.Equal(0, hijacked);
    }

    [Fact]
    public async Task Two_candidate_sources_on_one_field_fail_closed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create", new
        {
            name = "W", binId = Guid.NewGuid(), description = WidgetProbes.MixedCandidateSentinel,
        }));
    }

    [Fact]
    public async Task Advisory_options_cannot_overwrite_a_closed_sets_display()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("widgets.create", new
        {
            name = "W", location = WidgetProbes.AllowedLocation, description = WidgetProbes.OverwriteOptionsSentinel,
        }));
    }

    [Fact]
    public async Task Resolve_needs_the_change_wire_shape_for_edit_fields()
    {
        // A RAW scalar for a Change<T> field is invalid input.
        var raw = await actor.ResolveAsync("web.widgets.edit",
            new { widgetId = Guid.NewGuid(), description = WidgetProbes.EditSentinel });
        Assert.NotNull(raw.Error);

        // The correct {original, value} shape resolves, and the derivation reading the Change value fires.
        var shaped = await actor.ResolveAsync("web.widgets.edit",
            new { widgetId = Guid.NewGuid(), description = new { original = "old", value = WidgetProbes.EditSentinel } },
            changed: ["description"]);
        Assert.Null(shaped.Error);
        Assert.Contains(shaped.Response!.Fields["description"].Findings, f => f.Code == "test.probe-rejected");
    }

    [Fact]
    public async Task WasChanged_is_derived_from_Original_differing_from_Value()
    {
        // Value is the SENTINEL in both resolves; only Original differs, so the finding fires only when
        // the field was actually patched. This reads identically at submit.
        var patched = await actor.ResolveAsync("web.widgets.edit", new
        {
            widgetId = Guid.NewGuid(),
            description = new { original = "old", value = WidgetProbes.WasChangedSentinel },
        });
        Assert.Null(patched.Error);
        Assert.Contains(patched.Response!.Fields["description"].Findings, f => f.Code == "test.change-seen");

        var unchanged = await actor.ResolveAsync("web.widgets.edit", new
        {
            widgetId = Guid.NewGuid(),
            description = new { original = WidgetProbes.WasChangedSentinel, value = WidgetProbes.WasChangedSentinel },
        });
        Assert.Null(unchanged.Error);
        Assert.DoesNotContain(unchanged.Response!.Fields["description"].Findings, f => f.Code == "test.change-seen");
    }

    [Fact]
    public async Task WasChanged_is_never_true_for_a_non_change_set_field_at_submit()
    {
        // Location is present in the create body (non-change-set fields always are), but WasChanged must
        // still report it as NOT changed. If it leaked, the probe would block.
        (await actor.ExecuteAsync("widgets.create", new
        {
            name = "W", location = "somewhere", description = WidgetProbes.NonChangeChangedSentinel,
        })).ShouldSucceed();
    }

    [Fact]
    public async Task A_form_requiredwhen_over_a_change_field_works_at_submit()
    {
        // A form's RequiredWhen may reference a change-set field: the form submits complete Change<T>
        // state, so the predicate reads the field's actual Value at submit — no longer null from a sparse
        // body. This test-only form demands Name whenever Description has a value; submitting a described
        // widget with a CLEARED Name must fail requiredness — proof the predicate sees the value.
        await using var probeHost = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(
            WidgetModel.Builder()
                .Form<EditWidget.Input>("test.edit-required", "widgets.edit", form =>
                {
                    form.Field(x => x.WidgetId).Renderer("hidden");
                    form.Field(x => x.Description);
                    form.Field(x => x.Name).RequiredWhen(x => x.Description != null);
                })
                .Build());
        var probeActor = probeHost.Actor("demo", "widgets.edit");
        var result = await probeActor.ExecuteThroughFormAsync("test.edit-required", "widgets.edit", new
        {
            widgetId = Guid.NewGuid(),
            description = new { original = "old", value = "described" },
            name = new { original = "somewhere", value = (string?)null },
        });
        result.ShouldFailWith("validation.required", onField: "name");
    }
}
