using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// The operation contract is enforced for EVERY caller at submit, not merely previewed at resolve
/// (docs/40, Sol re-review). Two authority gaps this pins closed:
///   • a BLOCKING derivation finding stops submit even when the handler would have succeeded — the
///     derivation is the sole admissibility check (Finding 1), so the pipeline, not a duplicated
///     handler check, must enforce it;
///   • the selected FORM is part of idempotency identity (Finding 2): the same body + key submitted
///     directly and through a form are different effective requests and must not replay each other.
/// </summary>
public sealed class ContractEnforcementTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;
    private TestActor<ErpDbContext> actor = null!;

    public async Task InitializeAsync()
    {
        // The REAL model plus a probe derivation on orders.create — added through the Builder() seam
        // so nothing is faked: the probe rides the same RunDerivationsAsync path production uses.
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(
            ErpModel.Builder().AddDerivationHost(typeof(BlockingProbeDerivations)).Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Kontraktkund"), new("Kontraktsgatan 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            return Task.CompletedTask;
        });
        actor = host.Actor("demo", "orders.create", "orders.edit");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object ServiceOrder(string description) => new
    {
        customerId,
        orderType = "service",
        workAddress = "Verkstadsgatan 1",
        description,
    };

    [Fact]
    public async Task A_blocking_derivation_finding_stops_submit_even_when_the_handler_would_succeed()
    {
        // The sentinel description makes the probe emit a blocking field error; the create handler
        // has no such check and would happily create the order. The pipeline must reject it — proof
        // that derived.Findings are authoritative at submit, not just shown at resolve.
        (await actor.ExecuteAsync("orders.create", ServiceOrder(BlockingProbeDerivations.Sentinel)))
            .ShouldFailWith("test.probe-rejected", onField: "description");

        // Control: the same operation with an ordinary description succeeds — the probe is inert and
        // nothing else blocks, so this isolates the derivation finding as the sole cause above.
        (await actor.ExecuteAsync("orders.create", ServiceOrder("an ordinary order")))
            .ShouldSucceed();
    }

    [Fact]
    public async Task The_form_binding_is_part_of_idempotency_identity()
    {
        var body = ServiceOrder("idempotent order");

        // Direct, key K: a fresh outcome, not a replay.
        var first = await actor.ExecuteAsync("orders.create", body, idempotencyKey: "K");
        first.ShouldSucceed();
        Assert.DoesNotContain(first.Findings, f => f.Code == "pipeline.idempotent-replay");

        // Direct again, SAME body + key: THIS is the same effective request → replayed.
        var replay = await actor.ExecuteAsync("orders.create", body, idempotencyKey: "K");
        replay.ShouldSucceed();
        Assert.Contains(replay.Findings, f => f.Code == "pipeline.idempotent-replay");

        // Through the create form, SAME body + key: a DIFFERENT effective contract, so it is an
        // INDEPENDENT idempotency record — a fresh outcome, never the direct call's replayed result.
        var viaForm = await actor.ExecuteThroughFormAsync(
            "web.orders.create", "orders.create", body, idempotencyKey: "K");
        viaForm.ShouldSucceed();
        Assert.DoesNotContain(viaForm.Findings, f => f.Code == "pipeline.idempotent-replay");
    }

    [Fact]
    public async Task An_unknown_lookup_base_filter_fails_closed()
    {
        // The probe binds ProjectId to projects.lookup with a TYPO'd base filter ("custmerId"). A
        // silently-ignored key would widen the candidate universe to "any project with this id" and
        // admit an out-of-scope selection — so membership refuses to run and the contract bug is
        // surfaced loudly (Sol re-review, Finding 5), rather than accepting the value.
        // A SERVICE order, so the real AvailableProjects lookup (which would short-circuit on the
        // unknown project first) does not fire — the probe's bad-filter lookup is the one exercised.
        var input = new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 9",
            description = BlockingProbeDerivations.BadFilterSentinel,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => actor.ExecuteAsync("orders.create", input));
    }

    [Fact]
    public async Task A_closed_inline_option_set_is_enforced_at_submit()
    {
        // The probe declares WorkAddress's complete legal set. A value outside it is rejected...
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Otillåtnagatan 99",
            description = BlockingProbeDerivations.ClosedOptionsSentinel,
        })).ShouldFailWith("orders.project-not-available", onField: "workAddress");

        // ...and a value inside it (matched through the option's semantic wrapper) goes through.
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = BlockingProbeDerivations.AllowedAddress,
            description = BlockingProbeDerivations.ClosedOptionsSentinel,
        })).ShouldSucceed();

        // A case-variant is NOT accepted — the comparison is ordinal, not case-folding, so a
        // case-sensitive code/reference can't slip through in the wrong casing (Sol re-review F5).
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = BlockingProbeDerivations.AllowedAddress.ToLowerInvariant(),
            description = BlockingProbeDerivations.ClosedOptionsSentinel,
        })).ShouldFailWith("orders.project-not-available", onField: "workAddress");
    }

    [Fact]
    public async Task An_unsupported_lookup_filter_operator_fails_closed()
    {
        // customerId is a Guid, so `.contains` generates no predicate — accepting it would widen the
        // universe just like an unknown key. Now rejected as a contract bug (Sol re-review, Finding 2).
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 10",
            description = BlockingProbeDerivations.BadOperatorSentinel,
        }));
    }

    [Fact]
    public async Task Two_lookup_bindings_for_one_field_fail_closed()
    {
        // Resolve would show one candidate universe while submit enforces both — the UI can't
        // represent that, so the contract is refused (Sol re-review, Finding 6).
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 11",
            description = BlockingProbeDerivations.DoubleLookupSentinel,
        }));
    }

    [Fact]
    public async Task A_derivation_that_mutates_tracked_state_fails_closed()
    {
        // The probe adds an entity to the operation's writable context. Derivations must be read-only,
        // so the pipeline detects the tracked write and refuses (Sol re-review, Finding 3) — the
        // smuggled Customer is never committed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Verkstadsgatan 12",
            description = BlockingProbeDerivations.MutateSentinel,
        }));

        var smuggled = await host.QueryDbAsync("demo", db =>
            db.Customers.CountAsync(c => c.Name == new CustomerName("Smuggelkund")));
        Assert.Equal(0, smuggled);
    }

    [Fact]
    public async Task A_derivation_that_saves_is_rejected_and_commits_nothing()
    {
        // The durable escape: Add + SaveChanges. The write-guard interceptor rejects the SaveChanges
        // itself (Sol re-review, Finding 3), so the smuggled row is never committed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Verkstadsgatan 13",
            description = BlockingProbeDerivations.MutateSaveSentinel,
        }));

        var smuggled = await host.QueryDbAsync("demo", db =>
            db.Customers.CountAsync(c => c.Name == new CustomerName("Sparsmuggel")));
        Assert.Equal(0, smuggled);
    }

    [Fact]
    public async Task A_derivation_cannot_bypass_the_write_guard_with_comments_or_ctes()
    {
        // The two shapes the old first-token denylist let through: a write hidden behind a leading
        // comment, and a write behind a leading WITH-CTE (Sol re-review round 4, Finding 1).
        foreach (var sentinel in new[]
                 { BlockingProbeDerivations.CommentWriteSentinel, BlockingProbeDerivations.CteWriteSentinel })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
            {
                customerId,
                orderType = "service",
                workAddress = "Verkstadsgatan 14",
                description = sentinel,
            }));
        }

        var hijacked = await host.QueryDbAsync("demo", db =>
            db.Customers.CountAsync(c => c.Name == new CustomerName("Kapad")));
        Assert.Equal(0, hijacked);
    }

    [Fact]
    public async Task Two_candidate_sources_on_one_field_fail_closed()
    {
        // A Lookup AND a closed option set on ProjectId — resolve shows one, submit enforces both, so
        // the model refuses it (Sol re-review round 4, Finding 3, broadening DER008).
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 15",
            description = BlockingProbeDerivations.MixedCandidateSentinel,
        }));
    }

    [Fact]
    public async Task Advisory_options_cannot_overwrite_a_closed_sets_display()
    {
        // RequireOneOf(legal).AddOptions(different) — the display-vs-enforced discrepancy is caught as
        // two candidate sources on WorkAddress, independent of call order (Sol re-review round 5, F2).
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = BlockingProbeDerivations.AllowedAddress,
            description = BlockingProbeDerivations.OverwriteOptionsSentinel,
        }));
    }

    [Fact]
    public async Task Resolve_needs_the_change_wire_shape_for_edit_fields()
    {
        // A RAW scalar for a Change<T> field is invalid input — the exact malformed payload the old
        // reactive resolve sent, which failed silently and left stale derived state (Sol re-review F3).
        var raw = await actor.ResolveAsync("web.orders.edit",
            new { orderId = Guid.NewGuid(), description = BlockingProbeDerivations.EditSentinel });
        Assert.NotNull(raw.Error);

        // The correct {original, value} shape resolves, and the derivation reading the Change value
        // fires — resolve now sees exactly what submit will.
        var shaped = await actor.ResolveAsync("web.orders.edit",
            new
            {
                orderId = Guid.NewGuid(),
                description = new { original = "old", value = BlockingProbeDerivations.EditSentinel },
            },
            changed: ["description"]);
        Assert.Null(shaped.Error);
        Assert.Contains(shaped.Response!.Fields["description"].Findings, f => f.Code == "test.probe-rejected");
    }

    [Fact]
    public async Task WasChanged_is_derived_from_Original_differing_from_Value()
    {
        // WasChanged is now Original != Value, derived from the input itself (Sol re-review round 8) —
        // no wire-sent `changed` list. Value is the SENTINEL in both resolves; only Original differs, so
        // the finding fires only when the field was actually patched. This reads identically at submit.
        var patched = await actor.ResolveAsync("web.orders.edit", new
        {
            orderId = Guid.NewGuid(),
            description = new { original = "old", value = BlockingProbeDerivations.WasChangedSentinel },
        });
        Assert.Null(patched.Error);
        Assert.Contains(patched.Response!.Fields["description"].Findings, f => f.Code == "test.change-seen");

        var unchanged = await actor.ResolveAsync("web.orders.edit", new
        {
            orderId = Guid.NewGuid(),
            description = new
            {
                original = BlockingProbeDerivations.WasChangedSentinel,
                value = BlockingProbeDerivations.WasChangedSentinel,
            },
        });
        Assert.Null(unchanged.Error);
        Assert.DoesNotContain(unchanged.Response!.Fields["description"].Findings, f => f.Code == "test.change-seen");
    }

    [Fact]
    public async Task WasChanged_is_never_true_for_a_non_change_set_field_at_submit()
    {
        // WorkAddress is present in the create body (non-change-set fields always are), but WasChanged
        // must still report it as NOT changed — the submit set is narrowed to change-set fields so it
        // matches resolve's touched set (Sol re-review round 7, F3). If it leaked, the probe would block.
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Verkstadsgatan 20",
            description = BlockingProbeDerivations.NonChangeChangedSentinel,
        })).ShouldSucceed();
    }

    [Fact]
    public async Task A_form_requiredwhen_over_a_change_field_now_works_at_submit()
    {
        // The round-6 build gate forbidding a RequiredWhen over a change field is REMOVED (Sol re-review
        // round 8): the form submits complete Change<T> state, so the predicate reads the field's actual
        // Value at submit — no longer null from a sparse body. This test-only form
        // demands WorkAddress whenever Description has a value; submitting through it with a described
        // order but a cleared WorkAddress must fail requiredness — proof the predicate sees the value.
        var probeHost = await TamTestHost<ErpDbContext>.CreateSqliteAsync(
            ErpModel.Builder()
                .Form<EditOrderDetails.Input>("test.edit-required", "orders.edit-details", form =>
                {
                    form.Field(x => x.OrderId).Renderer("hidden");
                    form.Field(x => x.Description);
                    form.Field(x => x.WorkAddress).RequiredWhen(x => x.Description != null);
                })
                .Build());
        try
        {
            await probeHost.SeedAsync("demo", db =>
            {
                db.Customers.Add(Customer.Create("demo", new("K"), new("G 1"), null, null));
                return Task.CompletedTask;
            });
            var probeActor = probeHost.Actor("demo", "orders.edit");
            var result = await probeActor.ExecuteThroughFormAsync("test.edit-required", "orders.edit-details", new
            {
                orderId = Guid.NewGuid(),
                description = new { original = "old", value = "described" },
                workAddress = new { original = "somewhere", value = (string?)null },
            });
            result.ShouldFailWith("validation.required", onField: "workAddress");
        }
        finally
        {
            await probeHost.DisposeAsync();
        }
    }
}
